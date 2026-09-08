// Entry point for the Shoko.VFS.FUSE host daemon.
//
// Usage:
//   dotnet run --project Shoko.VFS.FUSE.Host -- --selftest            (self-check, no server)
//   dotnet run --project Shoko.VFS.FUSE.Host -- --dry-run             (print mount plan, do not mount)
//   dotnet run --project Shoko.VFS.FUSE.Host -- --warmup              (prime cache + persist snapshots, exit)
//   SHOKO_URL=... SHOKO_USER=... SHOKO_PASS=... \
//       dotnet run --project Shoko.VFS.FUSE.Host                      (run the daemon)
//   ... --config <path>                                               (JSON config file)
//   ... --agg [--folder <id> --root <path>] [--signalr]               (legacy Phase-1 aggregate check)
//
// Environment overrides: SHOKO_URL, SHOKO_USER, SHOKO_PASS, SHOKO_API_KEY.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Host.Config;
using Shoko.VFS.FUSE.Host.Daemon;
using Shoko.VFS.FUSE.Host.Health;
using Shoko.VFS.FUSE.Host.Validation;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;
using Shoko.VFS.FUSE.Runtime;

if (args.Contains("--selftest"))
    return RunSelfTest();

if (args.Contains("--dry-run"))
    return await RunDryRunAsync(args).ConfigureAwait(false);

if (args.Contains("--warmup"))
    return await RunWarmupAsync(args).ConfigureAwait(false);

if (args.Contains("--agg"))
    return await AggregateCheck.RunAsync(args).ConfigureAwait(false);

return await RunDaemonAsync(args).ConfigureAwait(false);

// ---------------------------------------------------------------------------
// Dry run: print the mount plan without mounting anything.
// ---------------------------------------------------------------------------
static async Task<int> RunDryRunAsync(string[] args)
{
    var config = TryLoadConfig(args);
    if (string.IsNullOrWhiteSpace(config.ShokoUrl))
    {
        Console.Error.WriteLine("--dry-run requires SHOKO_URL (and SHOKO_USER / SHOKO_PASS or SHOKO_API_KEY).");
        return 1;
    }

    using var http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
    var client = new ShokoRestClient(http, config.ShokoUrl);
    try
    {
        if (!await client.PingAsync().ConfigureAwait(false))
        {
            Console.Error.WriteLine($"Shoko server at {config.ShokoUrl} is unreachable. Cannot dry-run.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(config.ShokoApiKey))
        {
            client.SetApiKey(config.ShokoApiKey);
        }
        else if (string.IsNullOrWhiteSpace(config.ShokoUser))
        {
            Console.Error.WriteLine("--dry-run requires SHOKO_USER (or SHOKO_API_KEY); password may be empty for a passwordless Shoko user.");
            return 1;
        }
        else
        {
            Console.WriteLine($"Authenticating to {config.ShokoUrl} as {config.ShokoUser} ...");
            await client.LoginAsync(config.ShokoUser, config.ShokoPass).ConfigureAwait(false);
        }

        var folders = await client.GetManagedFoldersAsync().ConfigureAwait(false);
        Console.WriteLine($"Managed folders: {folders.Count}");
        var (targets, diagnostics, _) = DaemonOrchestrator.BuildPlan(folders, config);

        Console.WriteLine($"\nDesired mount targets: {targets.Count}");
        Console.WriteLine("(folder | root kind | target path | decision)");
        foreach (var target in targets)
        {
            Console.WriteLine($"  [{target.ManagedFolderId}] {target.ManagedFolderName} | {target.RootKind} | {target.TargetPath} | would-mount=true");
        }

        if (diagnostics.Count > 0)
        {
            Console.WriteLine("\nPlanner diagnostics:");
            foreach (var d in diagnostics)
                Console.WriteLine($"  [{d.Code}] {d.Message}{(d.Blocking ? " (BLOCKING)" : "")}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("--dry-run failed: " + ex.Message);
        return 1;
    }
}

// ---------------------------------------------------------------------------
// Warmup: connect, login, then aggregate + validate + persist one snapshot per
// managed folder (no FUSE mounts; Tv/Movie targets of a folder share one
// aggregation). Snapshots are persisted progressively, so Ctrl+C keeps all
// finished folders. Exit; the next daemon start loads the snapshots and skips
// the cold aggregation.
// ---------------------------------------------------------------------------
static async Task<int> RunWarmupAsync(string[] args)
{
    var config = TryLoadConfig(args);
    if (string.IsNullOrWhiteSpace(config.ShokoUrl))
    {
        Console.Error.WriteLine("--warmup requires SHOKO_URL (and SHOKO_USER / SHOKO_PASS or SHOKO_API_KEY).");
        return 1;
    }

    ProbeFuseConf(config);

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.SetMinimumLevel(LogLevel.Information);
    });
    var tracker = new DaemonStateTracker();
    var daemon = new DaemonOrchestrator(config, tracker, loggerFactory);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        await daemon.WarmupAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine("Warmup complete; per-folder snapshots + clean-shutdown markers persisted.");
        Console.WriteLine("Next normal daemon start will load the snapshot and skip the cold aggregation.");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Warmup cancelled.");
        return 1;
    }
    catch (TimeoutException ex)
    {
        Console.Error.WriteLine("Warmup failed: " + ex.Message);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Warmup failed: " + ex.Message);
        return 1;
    }
    finally
    {
        // Snapshots were already persisted progressively during warmup.
        await daemon.DisposeAsync().ConfigureAwait(false);
    }
}

// ---------------------------------------------------------------------------
// Default: run the daemon.
// ---------------------------------------------------------------------------
static async Task<int> RunDaemonAsync(string[] args)
{
    var config = TryLoadConfig(args);
    if (string.IsNullOrWhiteSpace(config.ShokoUrl))
    {
        Console.Error.WriteLine("SHOKO_URL is not set.");
        return 1;
    }

    ProbeFuseConf(config);

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.SetMinimumLevel(LogLevel.Information);
    });
    var tracker = new DaemonStateTracker();
    var daemon = new DaemonOrchestrator(config, tracker, loggerFactory);
    var health = await HealthEndpoint.StartAsync(config.HealthPort,
        () => (tracker.State, tracker.Mounts, tracker.LastReconcileAt, tracker.LastReconcileError, tracker.ReconcileCount, daemon.SignalRState,
              tracker.ServerUp, tracker.ServerReady, tracker.LastServerProbeAt, tracker.LastServerProbeError),
        loggerFactory).ConfigureAwait(false);

    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        cts.Cancel();
    });

    Console.WriteLine("Shoko VFS FUSE host daemon starting...");
    try
    {
        await daemon.RunAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown requested.
    }
    catch (TimeoutException ex)
    {
        Console.Error.WriteLine("Startup failed: " + ex.Message);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Daemon failed: " + ex.Message);
        return 1;
    }
    finally
    {
        health?.Dispose();
        await daemon.DisposeAsync().ConfigureAwait(false);
    }

    Console.WriteLine("Shoko VFS FUSE host daemon stopped cleanly.");
    return 0;
}

static void ProbeFuseConf(HostConfig config)
{
    if (!config.FuseAllowOther || !OperatingSystem.IsLinux())
        return;

    const string fuseConfPath = "/etc/fuse.conf";
    const string directive = "user_allow_other";

    if (!File.Exists(fuseConfPath))
    {
        Console.Error.WriteLine($"[WARN] allow_other requested but {fuseConfPath} does not exist. " +
            $"Add '{directive}' to {fuseConfPath} and restart. Other users/containers/NFS cannot access mounts until then.");
        return;
    }

    foreach (var raw in File.ReadLines(fuseConfPath))
    {
        var line = raw.Trim();
        if (line.StartsWith('#') || line.Length == 0)
            continue;
        if (line.Contains(directive, StringComparison.Ordinal))
            return; // Directive present.
    }

    Console.Error.WriteLine($"[WARN] allow_other requested but {fuseConfPath} lacks '{directive}'. " +
        $"Add '{directive}' to {fuseConfPath} and restart. Other users/containers/NFS cannot access mounts until then.");
}

static HostConfig TryLoadConfig(string[] args)
{
    string? configPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
            configPath = args[++i];
    }
    configPath ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "shoko-vfs-fuse", "config.json");
    return HostConfig.Load(configPath);
}

// ---------------------------------------------------------------------------
// Self-test: no server required.
// ---------------------------------------------------------------------------
static int RunSelfTest()
{
    int checks = 0;
    void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException("Self-test failed: " + label);
        checks++;
    }

    // --- relay mapping projection (6) ---
    SeriesData tv = BuildProjectionTestSeries();
    Check(!tv.IsMovie, "TV series not flagged as a movie");
    Check(tv.Mappings.Count == 3, "hidden episode dropped (3 visible mappings)");
    Check(tv.Mappings.Single(m => m.EpisodeId == 101).Season == 1, "regular episode -> season 1");
    Check(tv.Mappings.Single(m => m.EpisodeId == 103).Season == 0, "special -> season 0");
    Check(tv.Mappings.All(m => m.PartIndex is null && m.PartCount == 1), "single-video episodes have no part split");
    Check(tv.Mappings.Single(m => m.FileId == 2).SourcePath == "/vault/series/ep2.mkv", "source path preserved");

    // --- relay grouping (4) ---
    Check(OverrideCsvTest(), "override CSV parser (one group per line, skips comments)");
    Check(GrouperMergeTest(), "manual override merges 23+24 into one group");
    Check(GroupCountTest() == 2, "ungrouped series kept separate");
    Check(GroupPrimaryTest(), "group primary = first listed id");

    // --- DataSourceCache single-flight (1) ---
    Check(CacheSingleFlightTest(), "DataSourceCache single-flight");

    // --- dirty coalescer debounce (1) ---
    Check(CoalescerTest(), "dirty coalescer debounce");

    // --- PathValidation (3) ---
    Check(PathValidationMissingTest(), "PathValidation missing path -> false");
    Check(PathValidationPresentTest(), "PathValidation existing path -> true");
    Check(PathValidationEmptyTest(), "PathValidation no paths -> true (not a failure)");

    Console.WriteLine($"selftest: OK ({checks} assertions)");
    return 0;
}

static SeriesData BuildProjectionTestSeries()
{
    // TV series: regular episodes -> season 1, special -> season 0, hidden dropped.
    RelayRawSeries raw = new(
        7,
        Shoko.Abstractions.Metadata.Enums.AnimeType.TV,
        "Test Series",
        [
            new RelayRawEpisode(101, EpisodeType.Episode, 1, 1, false, "Episode 1",
                [new RelayRawVideo(1, false, "/vault/series/ep1.mkv", "/vault/series/ep1.mkv", 100, ".mkv", [])]),
            new RelayRawEpisode(102, EpisodeType.Episode, 2, 1, false, "Episode 2",
                [new RelayRawVideo(2, false, "/vault/series/ep2.mkv", "/vault/series/ep2.mkv", 200, ".mkv", [])]),
            new RelayRawEpisode(103, EpisodeType.Special, 1, 0, false, "Special",
                [new RelayRawVideo(3, false, "/vault/series/special.mkv", "/vault/series/special.mkv", 300, ".mkv", [])]),
            new RelayRawEpisode(104, EpisodeType.Episode, 3, 1, true, "Hidden",
                [new RelayRawVideo(4, false, "/vault/series/hidden.mkv", "/vault/series/hidden.mkv", 400, ".mkv", [])]),
        ]);
    return RelayMappingProjector.Project(raw);
}

static bool OverrideCsvTest()
{
    var groups = RelaySeriesGrouper.ParseManualOverrides("# comment\n22,23\n\n31,32,33\n");
    return groups.Count == 2
        && groups.All(g => g.Count >= 2)
        && groups.Any(g => g.SequenceEqual([22, 23]))
        && groups.Any(g => g.SequenceEqual([31, 32, 33]));
}

static bool GrouperMergeTest()
{
    var groups = BuildOverrideGroups();
    return groups.Any(g => g.SeriesIds.Contains(23) && g.SeriesIds.Contains(24));
}

static int GroupCountTest()
{
    return BuildOverrideGroups().Count;
}

static bool GroupPrimaryTest()
{
    var groups = RelaySeriesGrouper.Group(
        [
            new RelaySeriesGroupingInput(new SeriesData(23, "A", false, []), AnidbAnimeId: 23),
            new RelaySeriesGroupingInput(new SeriesData(24, "B", false, []), AnidbAnimeId: 24),
        ],
        manualOverrides: [[23, 24]]);
    return groups.Count == 1 && groups[0].PrimarySeriesId == 23;
}

static IReadOnlyList<RelaySeriesGroup> BuildOverrideGroups()
{
    var c = new SeriesData(23, "A", false, [new EpisodeData(12, 303, 1, 1, null, null, null, false, true, null, "/c.mkv", 1, ".mkv")]);
    var d = new SeriesData(24, "B", false, [new EpisodeData(13, 304, 1, 1, null, null, null, false, true, null, "/d.mkv", 1, ".mkv")]);
    return RelaySeriesGrouper.Group(
        [
            new RelaySeriesGroupingInput(c, AnidbAnimeId: 23),
            new RelaySeriesGroupingInput(d, AnidbAnimeId: 24),
            new RelaySeriesGroupingInput(new SeriesData(25, "C", false, [])),
        ],
        manualOverrides: [[23, 24]]);
}

static bool CacheSingleFlightTest()
{
    int upstreamFetches = 0;
    var cache = new DataSourceCache(
        _ => Task.FromResult<IReadOnlyList<SeriesData>>(new[] { new SeriesData(1, "A", false, []) })
            .ContinueWith(t => { upstreamFetches++; return t.Result; }),
        TimeSpan.FromSeconds(60));

    Task.Run(() => cache.GetAsync()).Wait();
    Task.Run(() => cache.GetAsync()).Wait();
    return upstreamFetches == 1;
}

static bool CoalescerTest()
{
    int runs = 0;
    using var coalescer = new DirtyCoalescer(() => Task.FromResult(runs++), TimeSpan.FromMilliseconds(30));
    coalescer.Trigger();
    coalescer.Trigger();
    Thread.Sleep(200);
    return runs == 1;
}

static bool PathValidationMissingTest()
{
    var missing = new SeriesData(1, "A", false,
        [new EpisodeData(1, 1, 1, 1, null, null, null, false, true, null, "/nonexistent/definitely-missing.mkv", 1, ".mkv")]);
    return PathValidation.ValidatePaths([missing], sampleCount: 5, maxPathLength: 500, log: null) == false;
}

static bool PathValidationPresentTest()
{
    var tmp = Path.Combine(Path.GetTempPath(), $"shoko-vfs-selftest-{Guid.NewGuid():N}.tmp");
    File.WriteAllText(tmp, "probe");
    try
    {
        var present = new SeriesData(1, "A", false,
            [new EpisodeData(1, 1, 1, 1, null, null, null, false, true, null, tmp, 4, ".tmp")]);
        return PathValidation.ValidatePaths([present], sampleCount: 5, maxPathLength: 500, log: null) == true;
    }
    finally
    {
        File.Delete(tmp);
    }
}

static bool PathValidationEmptyTest()
{
    // No source paths to validate -> not a failure.
    return PathValidation.ValidatePaths([], sampleCount: 5, maxPathLength: 500, log: null) == true;
}
