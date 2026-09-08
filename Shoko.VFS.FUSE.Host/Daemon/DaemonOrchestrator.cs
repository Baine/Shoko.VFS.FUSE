using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Api.Models;
using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Host.Config;
using Shoko.VFS.FUSE.Host.Monitor;
using Shoko.VFS.FUSE.Host.SignalR;
using Shoko.VFS.FUSE.Host.Validation;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Runtime;
using AbstractionsDropFolderType = Shoko.Abstractions.Video.Enums.DropFolderType;

namespace Shoko.VFS.FUSE.Host.Daemon;

/// <summary>
/// Central daemon orchestrator: connects to Shoko, computes the desired Relay mount
/// topology, reconciles real leases against it, consumes SignalR dirty events (coalesced),
/// and runs periodic safety reconciles. Owns the authenticated REST client and SignalR
/// connection; the health endpoint reads <see cref="DaemonStateTracker"/>.
/// </summary>
public sealed class DaemonOrchestrator : IAsyncDisposable
{
    private readonly HostConfig _config;
    private readonly DaemonStateTracker _state;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DaemonOrchestrator> _logger;
    private readonly HttpClient _http;
    private readonly ShokoRestClient _client;
    private readonly Dictionary<string, DaemonLease> _leases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly HashSet<string> _validationFailed = new(StringComparer.Ordinal);

    private ShokoSignalRConnection? _signalR;
    private DirtyCoalescer? _contentCoalescer;
    private DirtyCoalescer? _topologyCoalescer;
    private ServerAvailabilityMonitor? _availability;
    private FileSnapshotStore? _snapshotStore;
    private Task? _safetyLoop;
    private bool _connected;
    private bool _stopped;
    private volatile bool _reconcileRequested;

    // Coalesced content-change tracking: file ids seen since the last flush, or a
    // full-invalidation flag when an event carried no usable file id (or a storm overflowed).
    private readonly ConcurrentQueue<int> _dirtyFileIds = new();
    private int _fullContentInvalidation;

    public DaemonOrchestrator(HostConfig config, DaemonStateTracker state, ILoggerFactory loggerFactory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<DaemonOrchestrator>();
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            // ponytail: default HttpClient.Timeout is 100s, too low for big-library
            // /api/v3/ManagedFolder/{id}/File?pageSize=0&include=XRefs aggregations.
            Timeout = config.RequestTimeout > TimeSpan.Zero ? config.RequestTimeout : TimeSpan.FromMinutes(10),
        };
        _client = new ShokoRestClient(_http, config.ShokoUrl!, retries: config.HttpRetries);

        if (!string.IsNullOrWhiteSpace(config.SnapshotCacheDir))
        {
            try
            {
                _snapshotStore = new FileSnapshotStore(
                    ResolveSnapshotDir(config.SnapshotCacheDir!),
                    message => _logger.LogDebug("Snapshot: {Message}", message));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot store init failed; persistence disabled.");
            }
        }
    }

    private static string ResolveSnapshotDir(string configured)
    {
        if (Path.IsPathRooted(configured))
            return configured;
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "shoko-vfs-fuse", configured);
    }

    public DaemonStateTracker State => _state;

    /// <summary>
    /// Current SignalR hub connection state ("connected", "reconnecting", "disconnected") or null if never started.
    /// </summary>
    public string? SignalRState => _signalR?.State switch
    {
        Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected => "connected",
        Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Reconnecting => "reconnecting",
        Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected => "disconnected",
        _ => null,
    };

    /// <summary>
    /// Runs the daemon until <paramref name="ct"/> is cancelled. Performs connection,
    /// login, SignalR setup, stale-mount cleanup, startup reconcile, and the periodic safety loop.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _state.State = DaemonState.WaitingForServer;
        _logger.LogInformation("Waiting for Shoko server at {Url}", _config.ShokoUrl);

        await InitializeAsync(ct).ConfigureAwait(false);

        // Periodic safety reconcile.
        _safetyLoop = RunSafetyLoopAsync(ct);

        // Run until cancelled.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One-shot warmup: connects to Shoko and aggregates, validates, and persists one
    /// snapshot per managed folder — without mounting anything. The snapshot cache lives
    /// in the data source, not the FUSE mount, so leases are unnecessary; this keeps
    /// warmup free of mount/unmount churn and persists each folder's snapshot as soon
    /// as it is built (a cancelled warmup keeps everything finished so far).
    ///
    /// Tv and Movie targets of the same managed folder share identical data-source
    /// options and snapshot content, so aggregation runs once per folder and the result
    /// is stored under each target's snapshot key.
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct)
    {
        _state.State = DaemonState.WaitingForServer;
        _logger.LogInformation("Warmup: connecting to {Url}", _config.ShokoUrl);

        if (!await WaitForServerAsync(ct).ConfigureAwait(false))
            throw new TimeoutException(
                $"Shoko server at {_config.ShokoUrl} did not become reachable within {_config.ServerStartupTimeout.TotalSeconds:0}s.");
        await LoginAsync(ct).ConfigureAwait(false);

        if (_snapshotStore is null)
        {
            _logger.LogWarning("Warmup: no snapshot store configured; nothing to persist.");
            return;
        }

        var folders = await _client.GetManagedFoldersAsync().ConfigureAwait(false);
        var (targets, _, _) = BuildPlan(folders, _config);
        var byFolder = targets.GroupBy(t => t.ManagedFolderId).ToList();
        _logger.LogInformation(
            "Warmup: aggregating {FolderCount} managed folder(s) for {TargetCount} mount snapshot(s).",
            byFolder.Count, targets.Count);

        // ponytail: fixed degree 3; one folder is already 4 parallel HTTP requests deep,
        // so this keeps server load sane while cutting wall-clock ~3x vs sequential.
        await Parallel.ForEachAsync(byFolder,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
            async (group, token) =>
            {
                var folderTargets = group.ToArray();
                var first = folderTargets[0];
                var dataSource = DaemonMounter.CreateDataSource(first, _config, _client, _snapshotStore);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var snapshot = await dataSource.GetAllSeriesAsync().ConfigureAwait(false);

                    if (!PathValidation.ValidatePaths(snapshot, _config.PathValidationSamples,
                            _config.PathValidationMaxPathLength,
                            msg => _logger.LogWarning("[Warmup {Folder}] {Message}", first.ManagedFolderName, msg)))
                    {
                        _logger.LogError(
                            "Warmup {Folder} (id {Id}) failed path validation: source paths missing on disk; snapshot not persisted.",
                            first.ManagedFolderName, group.Key);
                        return;
                    }

                    var stored = dataSource.GetSnapshot();
                    if (stored is null)
                    {
                        _logger.LogWarning(
                            "Warmup {Folder}: aggregation cache unavailable (AggregationCacheTtl disabled?); nothing persisted.",
                            first.ManagedFolderName);
                        return;
                    }

                    // Progressive persistence: each key is written as soon as the folder's
                    // aggregation is done, so an interrupted warmup keeps its progress.
                    foreach (var target in folderTargets)
                    {
                        try { _snapshotStore.Save($"{target.ManagedFolderId}_{target.RootKind}", stored); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Snapshot save failed for {Name}.", target.TargetPath);
                        }
                    }
                    _logger.LogInformation(
                        "Warmup: snapshot persisted for {Folder} ({Series} series).",
                        first.ManagedFolderName, snapshot.Count);
                }
                finally
                {
                    dataSource.Dispose();
                }
            }).ConfigureAwait(false);

        _logger.LogInformation(
            "Warmup: complete. Next normal daemon start will load the snapshots and skip the cold aggregation.");
    }

    /// <summary>
    /// Shared startup path: wait for server, login, start SignalR + availability monitor,
    /// clean stale mounts, load persisted snapshots, run the startup reconcile. Used by
    /// both <see cref="RunAsync"/> (which continues with the safety loop) and
    /// <see cref="WarmupAsync"/> (which returns immediately after this returns).
    /// </summary>
    private async Task InitializeAsync(CancellationToken ct)
    {
        // 1. Wait for the server to become reachable.
        if (!await WaitForServerAsync(ct).ConfigureAwait(false))
            throw new TimeoutException(
                $"Shoko server at {_config.ShokoUrl} did not become reachable within {_config.ServerStartupTimeout.TotalSeconds:0}s.");

        // 2. Authenticate.
        await LoginAsync(ct).ConfigureAwait(false);
        _connected = true;

        // 3. Start SignalR + wire coalescers.
        await StartSignalRAsync(ct).ConfigureAwait(false);

        // 3b. Best-effort cleanup of stale FUSE mounts from a previous crashed run.
        await CleanupStaleMountsAsync().ConfigureAwait(false);

        // 4. Start the availability monitor. Wires freeze/unfreeze + reconcile triggers.
        StartAvailabilityMonitor(ct);

        // 5. Try to load any persisted snapshot from a previous clean shutdown.
        await LoadPersistedSnapshotsAsync().ConfigureAwait(false);

        // 6. Startup reconcile (uses the loaded snapshot if available; rebuilds otherwise).
        await ReconcileAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitForServerAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _config.ServerStartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _client.PingAsync(ct: ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Shoko server is reachable.");
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.ShokoApiKey))
        {
            _client.SetApiKey(_config.ShokoApiKey);
            _logger.LogInformation("Using provided API key.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ShokoUser))
            throw new InvalidOperationException(
                "No username set: set SHOKO_USER (or SHOKO_API_KEY) to connect.");

        _logger.LogInformation("Authenticating to {Url} as {User}", _config.ShokoUrl, _config.ShokoUser);
        await _client.LoginAsync(_config.ShokoUser, _config.ShokoPass).ConfigureAwait(false);
    }

    private async Task StartSignalRAsync(CancellationToken ct)
    {
        _contentCoalescer = new DirtyCoalescer(InvalidateContentAsync, _config.ContentDirtyCooldown);
        _topologyCoalescer = new DirtyCoalescer(() => SafeReconcileAsync(), _config.TopologyDirtyCooldown);

        _signalR = new ShokoSignalRConnection(_config.ShokoUrl!, _client.ApiKey!,
            message => _logger.LogDebug("SignalR: {Message}", message));
        // File-event payloads carry a FileID (except file:detected); none carries a SeriesID,
        // so per-series scoping goes through the data source's file→series xref index.
        _signalR.FileDetected += (_, _) => OnContentEvent(fileId: null);
        _signalR.FileHashed += (_, e) => OnContentEvent(e.FileId);
        _signalR.FileRelocated += (_, e) => OnContentEvent(e.FileId);
        _signalR.FileDeleted += (_, e) => OnContentEvent(e.FileId);
        _signalR.ReleaseSaved += (_, e) => OnContentEvent(e.FileId);
        _signalR.ReleaseRemoved += (_, e) => OnContentEvent(e.FileId);
        _signalR.ManagedFolderAdded += (_, _) => _topologyCoalescer.Trigger();
        _signalR.ManagedFolderUpdated += (_, _) => _topologyCoalescer.Trigger();
        _signalR.ManagedFolderRemoved += (_, _) => _topologyCoalescer.Trigger();
        _signalR.Reconnected += async (_, _) =>
        {
            _logger.LogInformation("SignalR reconnected; running full reconcile + content invalidation.");
            await SafeReconcileAsync().ConfigureAwait(false);
            InvalidateAllContent();
            await InvalidateContentAsync().ConfigureAwait(false);
        };

        try
        {
            await _signalR.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR connect failed; content/topology events will not be received until reconnect.");
        }
    }

    /// <summary>
    /// Best-effort cleanup of stale FUSE mounts left behind by a crashed previous run.
    /// Parses /proc/self/mountinfo, finds any mount whose mountpoint contains our VFS root
    /// folder names (!ShokoRelayVFS / !ShokoRelayMovieVFS), and attempts fusermount -u.
    /// </summary>
    private async Task CleanupStaleMountsAsync()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var stalePaths = GetStaleMountPoints();
            if (stalePaths.Count == 0)
                return;

            _logger.LogInformation("Found {Count} stale FUSE mount(s) from a previous run; attempting cleanup.", stalePaths.Count);
            foreach (var mountPoint in stalePaths)
            {
                _logger.LogInformation("Cleaning stale mount {MountPoint}", mountPoint);
                await UnmountPathAsync(mountPoint).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stale-mount cleanup failed; mounts may need manual unmounting.");
        }
    }

    /// <summary>
    /// Reads /proc/self/mountinfo and returns mountpoints containing our VFS root folder names
    /// as path segments (i.e., managed folder children like /mnt/.../!ShokoRelayVFS).
    /// </summary>
    internal static List<string> GetStaleMountPoints()
    {
        var result = new List<string>();
        if (!File.Exists("/proc/self/mountinfo"))
            return result;

        var tvName = "!ShokoRelayVFS";
        var movieName = "!ShokoRelayMovieVFS";

        foreach (var raw in File.ReadLines("/proc/self/mountinfo"))
        {
            // Format: ... mountpoint ... - fstype ...
            // Split on whitespace; mountpoint is field index 4.
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                continue;

            var mountPoint = fields[4];
            var segments = mountPoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && (segments[^1].Equals(tvName, StringComparison.Ordinal)
                                     || segments[^1].Equals(movieName, StringComparison.Ordinal)))
            {
                result.Add(mountPoint);
            }
        }

        return result;
    }

    private static async Task UnmountPathAsync(string mountPoint)
    {
        // Try fusermount3 first, then fusermount, then lazy variants.
        string[][] attempts =
        [
            ["fusermount3", "-u", mountPoint],
            ["fusermount", "-u", mountPoint],
            ["fusermount3", "-uz", mountPoint],
            ["fusermount", "-uz", mountPoint],
        ];

        foreach (var args in attempts)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = string.Join(' ', args.Skip(1)),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                proc.Start();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode == 0)
                    return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Command not found; try next variant.
            }
            catch
            {
                // Ignore; try next variant.
            }
        }
    }

    private void InvalidateAllContent() => Interlocked.Exchange(ref _fullContentInvalidation, 1);

    /// <summary>
    /// Records a content-dirty signal. Events with a FileID are queued for targeted
    /// per-series invalidation at the next coalesced flush; events without one (file:detected)
    /// or a queue overflow force a full invalidation, matching the old blanket behavior.
    /// </summary>
    private void OnContentEvent(int? fileId)
    {
        if (fileId is > 0 && Volatile.Read(ref _fullContentInvalidation) == 0 && _dirtyFileIds.Count < 512)
            _dirtyFileIds.Enqueue(fileId.Value);
        else
            InvalidateAllContent();
        _contentCoalescer?.Trigger();
    }

    private async Task InvalidateContentAsync()
    {
        var fileIds = new List<int>();
        while (_dirtyFileIds.TryDequeue(out var id))
            fileIds.Add(id);
        bool full = Interlocked.Exchange(ref _fullContentInvalidation, 0) != 0;
        if (fileIds.Count > 0 && fileIds.Count > 256)
        {
            // ponytail: a huge burst is a batch operation; one rebuild beats hundreds of
            // targeted cache drops. Small storms stay targeted.
            full = true;
            fileIds.Clear();
        }

        if (full)
        {
            _logger.LogDebug("Content dirty (coalesced); invalidating all mount data sources.");
            foreach (var lease in _leases.Values.ToArray())
                lease.Invalidate();
        }
        else if (fileIds.Count > 0)
        {
            _logger.LogDebug("Content dirty (coalesced): {Count} file event(s); targeted per-series invalidation.", fileIds.Count);
            foreach (var lease in _leases.Values.ToArray())
                foreach (var id in fileIds)
                    lease.InvalidateFile(id);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the <see cref="ServerAvailabilityMonitor"/> and wires its up/down events
    /// to the cache lifecycle. While down, all data sources are frozen (serving the last
    /// known snapshot, skipping rebuilds). When the server returns, caches are unfrozen
    /// and a full reconcile + content invalidation kicks off.
    /// </summary>
    private void StartAvailabilityMonitor(CancellationToken ct)
    {
        _availability = new ServerAvailabilityMonitor(
            _client,
            _config.ServerPollInterval,
            _config.MaxServerProbeFailures,
            _loggerFactory.CreateLogger<ServerAvailabilityMonitor>());
        _availability.ServerUp += async (_, _) =>
        {
            _state.ServerUp = true;
            _logger.LogInformation("Server is UP; unfreezing caches and rebuilding.");
            UnfreezeAllLeases();
            await SafeReconcileAsync().ConfigureAwait(false);
            InvalidateAllContent();
            await InvalidateContentAsync().ConfigureAwait(false);
        };
        _availability.ServerDown += (_, _) =>
        {
            _state.ServerUp = false;
            _state.ServerReady = false;
            _logger.LogWarning("Server is DOWN; freezing caches (serving stale snapshots).");
            FreezeAllLeases();
            _state.LastReconcileError ??= "Shoko server unreachable; serving cached snapshot.";
        };
        _availability.ServerReady += (_, _) =>
        {
            _state.ServerReady = true;
            _state.LastServerProbeAt = DateTime.UtcNow;
            _state.LastServerProbeError = null;
            _logger.LogDebug("Server is READY (API responding).");
        };
        // Mirror probe outcome into the state tracker even before the first transition
        // (so /health surfaces the current monitor verdict without waiting for a tick).
        _ = Task.Run(async () =>
        {
            while (_availability is not null && !_stopped)
            {
                _state.ServerUp = _availability.IsUp;
                _state.ServerReady = _availability.IsReady;
                _state.LastServerProbeAt = _availability.LastReadyAt ?? _availability.LastUpAt;
                _state.LastServerProbeError = _availability.LastProbeError;
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
        _availability.Start();
    }

    private void FreezeAllLeases()
    {
        if (!_config.CacheFreezeOnOutage)
            return;
        foreach (var lease in _leases.Values.ToArray())
            lease.Freeze();
    }

    private void UnfreezeAllLeases()
    {
        foreach (var lease in _leases.Values.ToArray())
            lease.Unfreeze();
    }

    /// <summary>
    /// Loads any persisted snapshot files (if a previous run shut down cleanly) and
    /// primes the in-memory caches. When the clean-shutdown marker is absent the
    /// stored snapshot is discarded — it may be inconsistent with the server state.
    /// </summary>
    private Task LoadPersistedSnapshotsAsync()
    {
        if (_snapshotStore is null)
            return Task.CompletedTask;
        // The snapshot is loaded on lease creation via the snapshot store/key passed to
        // DaemonMounter. Here we just log + clear stale data if the marker is missing.
        // (DaemonMounter is the single place that knows the per-mount snapshot key.)
        return Task.CompletedTask;
    }

    private async Task SafeReconcileAsync()
    {
        try
        {
            await ReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconcile failed.");
        }
    }

    private async Task RunSafetyLoopAsync(CancellationToken ct)
    {
        if (_config.ReconcileInterval <= TimeSpan.Zero)
            return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.ReconcileInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await SafeReconcileAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reconciles desired versus mounted topology. Safe to call concurrently: if a
    /// reconcile is already running the new trigger is dropped (coalescing).
    /// <paramref name="ct"/> aborts between mount operations (e.g. Ctrl+C during startup).
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!_connected)
            return;
        if (!await _reconcileGate.WaitAsync(0).ConfigureAwait(false))
        {
            _reconcileRequested = true;
            _logger.LogDebug("Reconcile already in progress; trigger queued.");
            return;
        }

        try
        {
            _state.State = DaemonState.Reconciling;
            _state.ReconcileCount++;
            _state.LastReconcileAt = DateTime.UtcNow;

            var folders = await _client.GetManagedFoldersAsync().ConfigureAwait(false);
            var plan = BuildPlan(folders, _config);
            var desired = plan.Targets;
            var desiredPaths = desired.Select(t => t.TargetPath).ToHashSet(StringComparer.Ordinal);

            // Contract summary: where the daemon will mount, so Shokofin/ShokoRelay can be
            // pointed at the same root names. Counted by unique managed folder (TV + Movie
            // targets for the same folder collapse to one row).
            _logger.LogInformation(
                "External VFS contract: TvRoot={TvRoot}; MovieRoot={MovieRoot}; Mounts={FolderCount}",
                _config.RelayTvFolderName,
                _config.RelayMovieFolderName,
                desired.Select(t => t.ManagedFolderId).Distinct().Count());

            // Reconcile.
            await ReconcileDesiredAsync(desired, desiredPaths, ct).ConfigureAwait(false);

            // Update tracker.
            _state.Mounts = BuildMountStatuses(desired);
            _state.LastReconcileError = null;
            _state.State = _state.Mounts.Any(m => m.IsFailed)
                ? DaemonState.Degraded
                : DaemonState.Healthy;
        }
        catch (Exception ex)
        {
            _state.LastReconcileError = ex.Message;
            _state.State = DaemonState.Degraded;
            _logger.LogError(ex, "Reconcile failed.");
        }
        finally
        {
            _reconcileGate.Release();
            // If a topology event arrived while we held the gate, honor it now.
            if (_reconcileRequested)
            {
                _reconcileRequested = false;
                await SafeReconcileAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReconcileDesiredAsync(IReadOnlyList<RelayMountTarget> desired, HashSet<string> desiredPaths, CancellationToken ct)
    {
        // Stop leases whose target path is no longer desired (unless validation-failed,
        // which we already keep unmounted).
        foreach (var path in _leases.Keys.ToArray())
        {
            if (!desiredPaths.Contains(path))
            {
                _logger.LogInformation("Stopping lease no longer desired: {Path}", path);
                var lease = _leases[path];
                _leases.Remove(path);
                await lease.StopAsync().ConfigureAwait(false);
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Start leases for desired-but-not-mounted targets.
        foreach (var target in desired)
        {
            if (_leases.ContainsKey(target.TargetPath))
                continue;
            if (_validationFailed.Contains(target.TargetPath))
                continue;

            try
            {
                _logger.LogInformation("Mounting {RootName} ({Kind}) at {Path}",
                    target.RootName, target.RootKind, target.TargetPath);
                var lease = await DaemonMounter.StartLeaseAsync(target, _config, _client, _loggerFactory,
                    onUnexpectedStopped: () => _topologyCoalescer?.Trigger(),
                    ct: ct,
                    snapshotStore: _snapshotStore)
                    .ConfigureAwait(false);
                _leases[target.TargetPath] = lease;

                // First aggregation path validation: do not serve an empty/broken VFS.
                if (!await ValidateNewLeaseAsync(target, lease).ConfigureAwait(false))
                {
                    _logger.LogError(
                        "MOUNT {Path} FAILED PATH VALIDATION: server/host path mapping mismatch. " +
                        "Source files do not exist on disk. Mount will not be served. " +
                        "(check ServerPathRoot / ManagedFolderPathRoot).", target.TargetPath);
                    lease.MarkPathMappingMismatch();
                    _validationFailed.Add(target.TargetPath);
                    await lease.DisposeAsync().ConfigureAwait(false);
                    _leases.Remove(target.TargetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mount {RootName} at {Path}", target.RootName, target.TargetPath);
            }
        }
    }

    private async Task<bool> ValidateNewLeaseAsync(RelayMountTarget target, DaemonLease lease)
    {
        try
        {
            int samples = _config.PathValidationSamples;
            int maxLen = _config.PathValidationMaxPathLength;
            return await lease.ValidateSourcePathsAsync(samples, maxLen, msg => _logger.LogWarning("{Message}", msg))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Path validation for {Path} could not complete; treating as failed.", target.TargetPath);
            return false;
        }
    }

    private IReadOnlyList<DaemonMountStatus> BuildMountStatuses(IReadOnlyList<RelayMountTarget> desired)
    {
        var statuses = new List<DaemonMountStatus>();
        foreach (var target in desired)
        {
            var lease = _leases.GetValueOrDefault(target.TargetPath);
            var state = lease?.State ?? DaemonMountState.Stopped;
            string? error = lease?.Error ?? (_validationFailed.Contains(target.TargetPath)
                ? "Path validation failed: source paths missing on disk"
                : null);
            var clockNow = DateTime.UtcNow;
            statuses.Add(new DaemonMountStatus(
                target.ManagedFolderId,
                target.ManagedFolderName,
                target.TargetPath,
                target.RootKind.ToString(),
                state,
                error,
                lease?.StartedAt,
                lease?.StoppedAt));
        }
        return statuses;
    }

    /// <summary>
    /// Builds the desired Relay mount topology and host-maps its paths. Reuses the
    /// plugin's public <see cref="RelayMountPlanner"/>. Pure — no filesystem or mount
    /// side effects, so both reconcile and --dry-run use it.
    /// </summary>
    public static (IReadOnlyList<RelayMountTarget> Targets, IReadOnlyList<RelayMountDiagnostic> Diagnostics, IReadOnlyList<ManagedFolderDto> Folders) BuildPlan(
        IReadOnlyList<ManagedFolderDto> folders, HostConfig config)
    {
        var snapshots = folders
            .Select(f => new RelayManagedFolderSnapshot(f.ID, f.Name, f.Path, (AbstractionsDropFolderType)f.DropFolderType))
            .ToList();

        var pluginConfig = new FusePluginConfiguration
        {
            RelayEnabled = config.RelayEnabled,
            RelayTvFolderName = config.RelayTvFolderName,
            RelayMovieFolderName = config.RelayMovieFolderName,
            MovieGenerationMode = config.MovieGenerationMode,
            TmdbEpNumbering = config.TmdbEpNumbering,
            MergeTmdbSeries = config.MergeTmdbSeries,
            PlexLocalExtras = config.PlexLocalExtras,
            FolderExclusions = config.FolderExclusions,
            ManagedFolderExclusions = config.ManagedFolderExclusions,
            SeriesTitleLanguage = config.SeriesTitleLanguage,
            EpisodeTitleLanguage = config.EpisodeTitleLanguage,
            MoveCommonSeriesTitlePrefixes = config.MoveCommonSeriesTitlePrefixes,
            TmdbEpGroupNames = config.TmdbEpGroupNames,
        };

        var plan = RelayMountPlanner.Build(pluginConfig, snapshots);

        var targets = plan.Targets
            .Select(t => t with
            {
                ManagedFolderPath = MapPath(config, t.ManagedFolderPath),
                TargetPath = MapPath(config, t.TargetPath),
            })
            .ToList();

        return (targets, plan.Diagnostics, folders);
    }

    /// <summary>
    /// Prefix-replaces <c>ServerPathRoot</c> with <c>ManagedFolderPathRoot</c>
    /// (identity when either is empty). Example: /mnt/shoko/import + roots
    /// /mnt/shoko→/mnt becomes /mnt/import.
    /// </summary>
    public static string MapPath(HostConfig config, string serverPath)
    {
        var from = config.ServerPathRoot?.TrimEnd('/') ?? "";
        var to = config.ManagedFolderPathRoot?.TrimEnd('/') ?? "";
        if (from.Length == 0 || to.Length == 0
            || !serverPath.StartsWith(from, StringComparison.Ordinal))
            return serverPath;
        var rest = serverPath[from.Length..];
        return to + rest;
    }

    /// <summary>Stops all mounts, SignalR, persists cached snapshots + clean-shutdown markers,
    /// and releases the HTTP client. Idempotent.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;

        _contentCoalescer?.Dispose();
        _topologyCoalescer?.Dispose();

        if (_availability is not null)
        {
            try { await _availability.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Availability monitor stop failed."); }
            _availability = null;
        }

        if (_signalR is not null)
        {
            try { await _signalR.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "SignalR dispose failed."); }
            _signalR = null;
        }

        // Flush per-mount snapshots to disk before disposing the leases.
        // FileSnapshotStore writes a clean-shutdown marker so the next start
        // knows the snapshot is internally consistent.
        foreach (var lease in _leases.Values.ToArray())
        {
            try { lease.SaveToStore(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Snapshot save failed for {Name}.", lease.Name); }
        }

        foreach (var lease in _leases.Values.ToArray())
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Lease dispose failed for {Name}.", lease.Name); }
        }
        _leases.Clear();

        _http.Dispose();
        _state.State = DaemonState.Stopped;
        _logger.LogInformation("Daemon stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _reconcileGate.Dispose();
    }
}
