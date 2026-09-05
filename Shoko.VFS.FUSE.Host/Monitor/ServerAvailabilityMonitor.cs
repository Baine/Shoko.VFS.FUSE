using Microsoft.Extensions.Logging;
using Shoko.VFS.FUSE.Host.Api;

namespace Shoko.VFS.FUSE.Host.Monitor;

/// <summary>
/// Background monitor that pings the Shoko Server and tracks its availability.
/// Distinguishes between "process up" (TCP/HTTP responds) and "API ready" (an
/// authenticated request succeeds) so callers can decide whether to freeze caches,
/// trigger rebuilds, or wait.
/// </summary>
public sealed class ServerAvailabilityMonitor : IAsyncDisposable
{
    private readonly ShokoRestClient _client;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxConsecutiveFailures;
    private readonly ILogger<ServerAvailabilityMonitor> _logger;
    private readonly Func<CancellationToken, Task<bool>> _readinessProbe;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public ServerAvailabilityMonitor(
        ShokoRestClient client,
        TimeSpan pollInterval,
        int maxConsecutiveFailures,
        ILogger<ServerAvailabilityMonitor> logger,
        Func<CancellationToken, Task<bool>>? readinessProbe = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(5);
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        // Default readiness probe: list managed folders. Succeeds only when the server
        // is past initialization (DB loaded, auth context valid).
        _readinessProbe = readinessProbe ?? (async ct => (await _client.GetManagedFoldersAsync().ConfigureAwait(false)) is not null);
    }

    /// <summary>Fired once when the server transitions from down to up.</summary>
    public event EventHandler? ServerUp;

    /// <summary>Fired once when the server transitions from up to down.</summary>
    public event EventHandler? ServerDown;

    /// <summary>Fired on every successful readiness probe (idempotent, fires often).</summary>
    public event EventHandler? ServerReady;

    /// <summary>Whether the server is currently considered up.</summary>
    public bool IsUp { get; private set; }

    /// <summary>Whether the server is currently considered fully ready (API responds).</summary>
    public bool IsReady { get; private set; }

    /// <summary>Last time the monitor detected the server as up (UTC).</summary>
    public DateTime? LastUpAt { get; private set; }

    /// <summary>Last time the monitor detected the server as down (UTC).</summary>
    public DateTime? LastDownAt { get; private set; }

    /// <summary>Last readiness-probe success (UTC).</summary>
    public DateTime? LastReadyAt { get; private set; }

    /// <summary>Number of consecutive failed probes (resets on success).</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Last readiness-probe error message (for health endpoint diagnostics).</summary>
    public string? LastProbeError { get; private set; }

    public void Start()
    {
        if (_runTask is not null)
            return;
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Server availability probe iteration failed.");
            }
        }
    }

    /// <summary>
    /// Performs a single probe. Public so callers (e.g. startup) can synchronise on the
    /// initial state without waiting for the first tick of the background loop.
    /// </summary>
    public async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        // 1. Cheap reachability probe.
        bool reachable;
        try
        {
            reachable = await _client.PingAsync(ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reachable = false;
            LastProbeError = ex.Message;
        }

        if (!reachable)
        {
            ConsecutiveFailures++;
            LastProbeError ??= "Ping returned false";
            if (IsUp && ConsecutiveFailures >= _maxConsecutiveFailures)
                RaiseDown();
            return;
        }

        // 2. Readiness probe (authenticated API call). Failures here still count as
        // "server up but not ready" — we don't toggle ServerUp/Down on them, only
        // IsReady, so SignalR-based freshness logic isn't disturbed.
        bool ready = false;
        try
        {
            ready = await _readinessProbe(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastProbeError = ex.Message;
        }

        ConsecutiveFailures = 0;
        LastProbeError = null;

        if (!IsUp)
            RaiseUp();

        if (ready)
        {
            LastReadyAt = DateTime.UtcNow;
            if (!IsReady)
            {
                IsReady = true;
                try { ServerReady?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "ServerReady handler threw."); }
            }
        }
        else
        {
            IsReady = false;
        }
    }

    private void RaiseUp()
    {
        IsUp = true;
        LastUpAt = DateTime.UtcNow;
        _logger.LogInformation("Shoko server detected UP.");
        try { ServerUp?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "ServerUp handler threw."); }
    }

    private void RaiseDown()
    {
        IsUp = false;
        IsReady = false;
        LastDownAt = DateTime.UtcNow;
        _logger.LogWarning("Shoko server detected DOWN ({Failures} consecutive failures).", ConsecutiveFailures);
        try { ServerDown?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "ServerDown handler threw."); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
