// Legacy Phase-1 aggregate check, preserved under the `--agg` subcommand.
// Lists the resolved series in aggregate and optionally listens on SignalR
// until Ctrl+C.

using Microsoft.Extensions.Logging;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Config;
using Shoko.VFS.FUSE.Host.SignalR;

namespace Shoko.VFS.FUSE.Host.Daemon;

internal static class AggregateCheck
{
    public static async Task<int> RunAsync(string[] args, HostConfig? config = null)
    {
        int? folderId = null;
        string? rootPath = null;
        bool useSignalR = args.Contains("--signalr");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--folder" && i + 1 < args.Length && int.TryParse(args[++i], out var id))
                folderId = id;
            if (args[i] == "--root" && i + 1 < args.Length)
                rootPath = args[++i];
        }

        config ??= LoadConfig(args);
        if (string.IsNullOrWhiteSpace(config.ShokoUrl))
        {
            Console.Error.WriteLine("--agg requires SHOKO_URL (and credentials).");
            return 1;
        }

        using var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        var client = new ShokoRestClient(http, config.ShokoUrl);

        try
        {
            if (!string.IsNullOrWhiteSpace(config.ShokoApiKey))
            {
                client.SetApiKey(config.ShokoApiKey);
            }
            else if (string.IsNullOrWhiteSpace(config.ShokoUser) || string.IsNullOrWhiteSpace(config.ShokoPass))
            {
                Console.Error.WriteLine("--agg requires SHOKO_USER / SHOKO_PASS (or SHOKO_API_KEY).");
                return 1;
            }
            else
            {
                await client.LoginAsync(config.ShokoUser, config.ShokoPass).ConfigureAwait(false);
            }

            var series = await client.GetAllSeriesAsync().ConfigureAwait(false);
            int mappedFiles = 0;
            foreach (var s in series)
                mappedFiles += (await client.GetSeriesEpisodesAsync(s.IDs.ID).ConfigureAwait(false)).Count;
            // ponytail: sequential episode fetches; parallelize if many series slow this check.

            Console.WriteLine($"Aggregate: {series.Count} series, {mappedFiles} episodes.");
            if (rootPath is not null)
                Console.WriteLine($"  (root {rootPath})");

            if (useSignalR && !string.IsNullOrEmpty(client.ApiKey))
            {
                var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                Console.WriteLine("Listening on SignalR until Ctrl+C...");
                var conn = new ShokoSignalRConnection(config.ShokoUrl, client.ApiKey,
                    msg => Console.WriteLine("[signalr] " + msg));
                await conn.StartAsync(cts.Token).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("--agg failed: " + ex.Message);
            return 1;
        }
    }

    private static HostConfig LoadConfig(string[] args)
    {
        string? path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
                path = args[++i];
        }
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "shoko-vfs-fuse", "config.json");
        return HostConfig.Load(path);
    }
}
