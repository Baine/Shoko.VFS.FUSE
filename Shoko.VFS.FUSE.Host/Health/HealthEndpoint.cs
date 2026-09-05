using System.Net;
using Shoko.VFS.FUSE.Host.Daemon;
using Microsoft.Extensions.Logging;

namespace Shoko.VFS.FUSE.Host.Health;

/// <summary>
/// A lightweight HTTP health endpoint using <c>System.Net.HttpListener</c> (stdlib, no ASP.NET).
/// Binds on 127.0.0.1:<port>. GET / returns a JSON object with daemon state and mount info.
/// </summary>
public sealed class HealthEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _runTask;
    private readonly ILogger<HealthEndpoint> _logger;

    /// <summary>
    /// Creates and starts the health endpoint. <c>port=0</c> disables it.
    /// </summary>
    /// <param name="port">Port to bind on (127.0.0.1). 0 = disabled.</param>
    /// <param>
    /// Callback that returns current health data.
    /// </param>
    public static Task<HealthEndpoint?> StartAsync(int port, Func<(DaemonState State, IReadOnlyList<DaemonMountStatus> Mounts, DateTime? LastReconcileAt, string? LastReconcileError, int ReconcileCount, string? SignalRState, bool? ServerUp, bool? ServerReady, DateTime? LastServerProbeAt, string? LastServerProbeError)> getHealth, ILoggerFactory loggerFactory)
    {
        if (port <= 0) return Task.FromResult<HealthEndpoint?>(null);

        var endpoint = new HealthEndpoint(port, getHealth, loggerFactory);
        endpoint.Start();
        return Task.FromResult<HealthEndpoint?>(endpoint);
    }

    private HealthEndpoint(int port, Func<(DaemonState State, IReadOnlyList<DaemonMountStatus> Mounts, DateTime? LastReconcileAt, string? LastReconcileError, int ReconcileCount, string? SignalRState, bool? ServerUp, bool? ServerReady, DateTime? LastServerProbeAt, string? LastServerProbeError)> getHealth, ILoggerFactory loggerFactory)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _getHealth = getHealth;
        _logger = loggerFactory.CreateLogger<HealthEndpoint>();
        _runTask = RunAsync();
    }

    private readonly Func<(DaemonState State, IReadOnlyList<DaemonMountStatus> Mounts, DateTime? LastReconcileAt, string? LastReconcileError, int ReconcileCount, string? SignalRState, bool? ServerUp, bool? ServerReady, DateTime? LastServerProbeAt, string? LastServerProbeError)> _getHealth;

    private void Start()
    {
        try
        {
            _listener.Start();
            _logger.LogInformation("Health endpoint listening on http://127.0.0.1/{Port}/", _listener.Prefixes.FirstOrDefault()?.Split('/')[2] ?? "?");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health endpoint failed to start on port {_Port}; health checks disabled", _listener.Prefixes.FirstOrDefault()?.Split('/')[2] ?? "?");
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(context), CancellationToken.None);
                }
                catch (HttpListenerException) when (!_listener.IsListening)
                {
                    // Listener stopped — exit.
                    break;
                }
                catch
                {
                    // Ignore transient listener errors.
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected on dispose.
        }
        catch
        {
            // Log but don't crash the daemon over a health-check failure.
            _logger.LogError("Health endpoint run loop failed.");
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var req = context.Request;
            if (req.HttpMethod == "GET" && string.Equals(req.Url?.PathAndQuery, "/", StringComparison.Ordinal))
            {
                var health = _getHealth();
                var json = SerializeHealth(health);
                var buffer = System.Text.Encoding.UTF8.GetBytes(json);
                var resp = context.Response;
                resp.StatusCode = 200;
                resp.ContentType = "application/json";
                resp.ContentLength64 = buffer.Length;
                resp.OutputStream.Write(buffer, 0, buffer.Length);
                resp.OutputStream.Flush();
                resp.Close();
                return;
            }

            // 404 for anything else.
            var notFound = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
            var resp404 = context.Response;
            resp404.StatusCode = 404;
            resp404.ContentLength64 = notFound.Length;
            resp404.OutputStream.Write(notFound, 0, notFound.Length);
            resp404.OutputStream.Flush();
            resp404.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health endpoint request failed.");
            try { context.Response.Close(); } catch { }
        }
    }

    private string SerializeHealth((DaemonState State, IReadOnlyList<DaemonMountStatus> Mounts, DateTime? LastReconcileAt, string? LastReconcileError, int ReconcileCount, string? SignalRState, bool? ServerUp, bool? ServerReady, DateTime? LastServerProbeAt, string? LastServerProbeError) h)
    {
        var mountsJson = string.Join(",", h.Mounts.Select(m =>
            $"{{\"managedFolderId\":{m.ManagedFolderId},\"managedFolderName\":\"{Escape(m.ManagedFolderName)}\",\"targetPath\":\"{Escape(m.TargetPath)}\",\"rootKind\":\"{m.RootKind}\",\"state\":\"{m.State}\",\"error\":\"{Escape(m.Error ?? "")}\",\"lastStartedAt\":\"{m.LastStartedAt?.ToUniversalTime().ToString("O")}\",\"lastStoppedAt\":\"{m.LastStoppedAt?.ToUniversalTime().ToString("O")}\"}}"));

        var signalrState = Escape(h.SignalRState ?? "disconnected");
        var serverUp = h.ServerUp.HasValue ? (h.ServerUp.Value ? "true" : "false") : "null";
        var serverReady = h.ServerReady.HasValue ? (h.ServerReady.Value ? "true" : "false") : "null";
        return $"{{\"state\":\"{h.State}\",\"signalrState\":\"{signalrState}\",\"serverUp\":{serverUp},\"serverReady\":{serverReady},\"lastServerProbeAt\":\"{h.LastServerProbeAt?.ToUniversalTime().ToString("O")}\",\"lastServerProbeError\":\"{Escape(h.LastServerProbeError ?? "")}\",\"mounts\":[{mountsJson}],\"lastReconcileAt\":\"{h.LastReconcileAt?.ToUniversalTime().ToString("O")}\",\"lastReconcileError\":\"{Escape(h.LastReconcileError ?? "")}\",\"version\":\"1.1.0\"}}";
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}