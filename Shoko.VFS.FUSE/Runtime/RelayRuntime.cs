using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using Shoko.VFS.FUSE.Api;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Runtime;

/// <summary>Hosted Relay mount coordinator.</summary>
public sealed class RelayRuntime : BackgroundService
{
    private readonly ISystemService _systemService;
    private readonly IVideoService _videoService;
    private readonly IVideoHashingService _hashingService;
    private readonly IVideoRelocationService _relocationService;
    private readonly IVideoReleaseService _releaseService;
    private readonly ConfigurationProvider<FusePluginConfiguration> _configurationProvider;
    private readonly IApplicationPaths _applicationPaths;
    private readonly RelayMountOperations _mountOperations;
    private readonly RelayIgnoreRule _ignoreRule;
    private readonly ILogger<RelayRuntime> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly RelayMountPlanner _planner = new();
    private readonly object _statusLock = new();
    private readonly object _leaseStateLock = new();
    private readonly ConcurrentDictionary<string, MountedLease> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MountedLease> _pendingLeases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _mountAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unexpectedStops = new(StringComparer.Ordinal);
    private readonly HashSet<string> _capabilityCodes = new(StringComparer.Ordinal);
    private RelayHealthStatus _status;
    private int _topologyDirty = 1;
    private int _contentDirty;
    private bool _subscribed;
    private IReadOnlyList<IReadOnlyList<int>> _lastValidOverrideGroups = [];

    public RelayRuntime(
        ISystemService systemService,
        IVideoService videoService,
        IVideoHashingService hashingService,
        IVideoRelocationService relocationService,
        IVideoReleaseService releaseService,
        IMetadataService metadataService,
        ConfigurationProvider<FusePluginConfiguration> configurationProvider,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        ILogger<RelayRuntime> logger)
        : this(
            systemService,
            videoService,
            hashingService,
            relocationService,
            releaseService,
            metadataService,
            configurationProvider,
            applicationPaths,
            RelayMountOperationsFactory.Create(metadataService, loggerFactory),
            logger)
    {
    }

    internal RelayRuntime(
        ISystemService systemService,
        IVideoService videoService,
        IVideoHashingService hashingService,
        IVideoRelocationService relocationService,
        IVideoReleaseService releaseService,
        IMetadataService metadataService,
        ConfigurationProvider<FusePluginConfiguration> configurationProvider,
        IApplicationPaths applicationPaths,
        RelayMountOperations mountOperations,
        ILogger<RelayRuntime> logger)
    {
        _systemService = systemService ?? throw new ArgumentNullException(nameof(systemService));
        _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
        _hashingService = hashingService ?? throw new ArgumentNullException(nameof(hashingService));
        _relocationService = relocationService ?? throw new ArgumentNullException(nameof(relocationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        ArgumentNullException.ThrowIfNull(metadataService);
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        _mountOperations = mountOperations ?? throw new ArgumentNullException(nameof(mountOperations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ignoreRule = new RelayIgnoreRule();
        _status = new RelayHealthStatus(RelayRuntimeState.WaitingForServer, [], [], 0, 0, 0);
        RegisterIgnoreRule();
    }

    public RelayHealthStatus Status => Volatile.Read(ref _status);

    internal IManagedFolderIgnoreRule IgnoreRule => _ignoreRule;
    internal Action? BeforeLeasePublication { get; set; }
    internal Action? BeforePendingLeasePublication { get; set; }
    internal int PendingLeaseCount => _pendingLeases.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetState(RelayRuntimeState.WaitingForServer);
        try
        {
            Subscribe();
            await _systemService.WaitForStartupAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            Signal();

            while (true)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _topologyDirty, 0) != 0)
                {
                    Interlocked.Exchange(ref _contentDirty, 0);
                    await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (Interlocked.Exchange(ref _contentDirty, 0) != 0)
                {
                    InvalidateMountedResolvers();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay runtime failed before startup completed.");
            AddCapability("StartupFailed");
            SetState(RelayRuntimeState.Degraded);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            Unsubscribe();
            await StopOwnedLeasesAsync().ConfigureAwait(false);
            SetStatusAfterStops(RelayRuntimeState.Stopped);
            _signal.Dispose();
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        SetState(RelayRuntimeState.Reconciling);

        FusePluginConfiguration configuration;
        try
        {
            configuration = _configurationProvider.Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay configuration could not be loaded.");
            PreserveCurrentState("ConfigurationLoadFailed");
            return;
        }

        if (!configuration.RelayEnabled)
        {
            await StopOwnedLeasesAsync().ConfigureAwait(false);
            SetStatusAfterStops(_leases.IsEmpty ? RelayRuntimeState.Disabled : RelayRuntimeState.Degraded);
            return;
        }

        IReadOnlyList<RelayManagedFolderSnapshot> managedFolders;
        try
        {
            managedFolders = (_videoService.GetAllManagedFolders() ?? [])
                .Select(folder => new RelayManagedFolderSnapshot(folder.ID, folder.Name, folder.Path, folder.DropFolderType))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay managed folders could not be loaded.");
            PreserveCurrentState("ManagedFolderReadFailed");
            return;
        }

        if (!TryReadOverrideGroups(out var overrideGroups))
        {
            PreserveCurrentState("OverrideReadFailed");
            return;
        }

        var plan = _planner.Plan(configuration, managedFolders);

        var statuses = new List<RelayMountStatus>(plan.Targets.Count);
        if (plan.IsBlocked)
        {
            var reasons = plan.Diagnostics.Where(diagnostic => diagnostic.Blocking).Select(diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray();
            AddCapabilities(reasons);
            PreserveCurrentState();
            return;
        }

        var operation = _mountOperations;
        var desired = plan.Targets
            .Select(target => (Target: target, Fingerprint: ComputeFingerprint(configuration, target, overrideGroups)))
            .ToArray();
        var desiredByPath = desired.ToDictionary(item => item.Target.TargetPath, StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        var stopFailed = new HashSet<string>(StringComparer.Ordinal);
        var pendingStopFailed = await StopPendingLeasesAsync().ConfigureAwait(false);

        foreach (var (path, mounted) in _leases.ToArray())
        {
            if (desiredByPath.TryGetValue(path, out var target) && target.Fingerprint == mounted.Fingerprint)
            {
                retained.Add(path);
                continue;
            }

            if (_leases.TryRemove(path, out _))
            {
                if (await StopLeaseAsync(mounted.Lease).ConfigureAwait(false))
                    RefreshIgnoreRoots();
                else
                {
                    _leases[path] = mounted;
                    stopFailed.Add(path);
                }
            }
        }

        foreach (var (target, fingerprint) in desired)
        {
            if (retained.Contains(target.TargetPath))
            {
                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Mounted, []));
                continue;
            }

            if (pendingStopFailed.Contains(target.TargetPath) || _pendingLeases.ContainsKey(target.TargetPath))
            {
                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Failed, ["MountCleanupPending"]));
                continue;
            }

            string attemptId = Guid.NewGuid().ToString("N");
            lock (_leaseStateLock)
                _mountAttempts[target.TargetPath] = attemptId;
            try
            {
                var lease = await operation.StartAsync(
                    target,
                    configuration,
                    overrideGroups,
                    cancellationToken,
                    () => OnUnexpectedStopped(target, attemptId),
                    () => OnDaemonStopped(target, attemptId),
                    () => OnCleanupCompleted(target, attemptId)).ConfigureAwait(false);
                BeforeLeasePublication?.Invoke();
                bool stoppedDuringPublication;
                lock (_leaseStateLock)
                {
                    stoppedDuringPublication = _unexpectedStops.TryRemove(attemptId, out _);
                    if (!stoppedDuringPublication)
                    {
                        _leases[target.TargetPath] = new MountedLease(lease, fingerprint, attemptId);
                        RefreshIgnoreRoots();
                    }
                }

                if (stoppedDuringPublication)
                {
                    await StopLeaseAsync(lease).ConfigureAwait(false);
                    statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Failed, ["MountStoppedUnexpectedly"]));
                    continue;
                }

                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Mounted, []));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Fuse.FuseStartException ex)
            {
                _logger.LogWarning("Relay mount failed for managed folder {ManagedFolderId}, root {RootKind}: {Reason}", target.ManagedFolderId, target.RootKind, ex.Reason);
                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Failed, [ex.Reason.ToString()]));
            }
            catch (RelayMountStartException ex)
            {
                var pendingLease = ex.PendingLease;
                bool published;
                lock (_leaseStateLock)
                {
                    BeforePendingLeasePublication?.Invoke();
                    published = !pendingLease.CleanupHasCompleted;
                    if (published)
                        _pendingLeases[target.TargetPath] = new MountedLease(pendingLease, fingerprint, attemptId);
                }

                if (!published || pendingLease.DaemonHasStopped)
                    MarkDirty(fullReconcile: true);

                var reason = ex.InnerException is Fuse.FuseStartException fuseException
                    ? fuseException.Reason.ToString()
                    : "MountFailed";
                _logger.LogWarning("Relay mount cleanup is pending for managed folder {ManagedFolderId}, root {RootKind}: {Reason}", target.ManagedFolderId, target.RootKind, reason);
                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Failed, [reason]));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay mount failed for managed folder {ManagedFolderId}, root {RootKind}.", target.ManagedFolderId, target.RootKind);
                statuses.Add(new RelayMountStatus(target.ManagedFolderId, target.RootKind, RelayMountStatusState.Failed, ["MountFailed"]));
            }
            finally
            {
                lock (_leaseStateLock)
                {
                    _mountAttempts.TryRemove(target.TargetPath, out _);
                    _unexpectedStops.TryRemove(attemptId, out _);
                }
            }
        }

        var capabilities = plan.Diagnostics
            .Where(diagnostic => diagnostic.Code == "LocalExtrasUnsupported")
            .Select(diagnostic => diagnostic.Code)
            .ToArray();
        AddCapabilities(capabilities);
        int failed = statuses.Count(status => status.State == RelayMountStatusState.Failed);
        var state = failed > 0 || plan.IsDegraded || HasCapabilities()
            ? RelayRuntimeState.Degraded
            : RelayRuntimeState.Healthy;
        lock (_leaseStateLock)
        {
            var targetsByKind = plan.Targets.ToDictionary(
                target => (target.ManagedFolderId, target.RootKind),
                target => target.TargetPath);
            for (int i = 0; i < statuses.Count; i++)
            {
                var status = statuses[i];
                if (status.State == RelayMountStatusState.Mounted
                    && (!targetsByKind.TryGetValue((status.ManagedFolderId, status.RootKind), out var path)
                        || !_leases.ContainsKey(path)))
                {
                    statuses[i] = status with
                    {
                        State = RelayMountStatusState.Failed,
                        ReasonCodes = ["MountStoppedUnexpectedly"],
                    };
                }
            }

            failed = statuses.Count(status => status.State == RelayMountStatusState.Failed);
            state = failed > 0 || plan.IsDegraded || HasCapabilities()
                ? RelayRuntimeState.Degraded
                : RelayRuntimeState.Healthy;
            SetStatus(state, statuses, plan.Targets.Count, _leases.Count, failed);
        }
    }

    private bool TryReadOverrideGroups(out IReadOnlyList<IReadOnlyList<int>> overrideGroups)
    {
        string path = RelaySeriesGrouper.GetOverridePath(_applicationPaths.DataPath);
        try
        {
            if (!File.Exists(path))
            {
                _lastValidOverrideGroups = [];
                overrideGroups = _lastValidOverrideGroups;
                return true;
            }

            _lastValidOverrideGroups = RelaySeriesGrouper.ParseManualOverrides(File.ReadLines(path));
            overrideGroups = _lastValidOverrideGroups;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay override groups could not be loaded.");
            overrideGroups = _lastValidOverrideGroups;
            return false;
        }
    }

    private void InvalidateMountedResolvers()
    {
        foreach (var mounted in _leases.Values.ToArray())
        {
            try
            {
                mounted.Lease.Invalidate();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Relay resolver invalidation failed.");
                AddCapability("CacheInvalidationFailed");
            }
        }
    }

    private async Task StopOwnedLeasesAsync()
    {
        foreach (var (path, mounted) in _leases.ToArray())
        {
            if (!_leases.TryGetValue(path, out var current) || !ReferenceEquals(current, mounted))
                continue;
            if (await StopLeaseAsync(mounted.Lease).ConfigureAwait(false))
            {
                _leases.TryRemove(path, out _);
                RefreshIgnoreRoots();
            }
        }

        await StopPendingLeasesAsync().ConfigureAwait(false);
    }

    private async Task<HashSet<string>> StopPendingLeasesAsync()
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, pending) in _pendingLeases.ToArray())
        {
            if (!_pendingLeases.TryGetValue(path, out var current) || !ReferenceEquals(current, pending))
                continue;
            if (await StopLeaseAsync(pending.Lease).ConfigureAwait(false))
                _pendingLeases.TryRemove(path, out _);
            else
                failed.Add(path);
        }

        return failed;
    }

    private async Task<bool> StopLeaseAsync(RelayMountLease lease)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay mount stop failed.");
            AddCapability("MountStopFailed");
            return false;
        }
    }

    private void RefreshIgnoreRoots() =>
        _ignoreRule.SetRoots(_leases.Values.Select(mounted => mounted.Lease.Target));

    private void SetStatusAfterStops(RelayRuntimeState state)
    {
        var current = Status;
        var active = _leases.Values
            .Select(mounted => (mounted.Lease.Target.ManagedFolderId, mounted.Lease.Target.RootKind))
            .ToHashSet();
        var mounts = current.Mounts
            .Where(mount => active.Contains((mount.ManagedFolderId, mount.RootKind)))
            .ToArray();
        SetStatus(state, mounts, state == RelayRuntimeState.Disabled ? 0 : current.DesiredMountCount, _leases.Count, current.FailedMountCount);
    }

    private void PreserveCurrentState(string? capabilityCode = null)
    {
        if (capabilityCode is not null)
            AddCapability(capabilityCode);
        var current = Status;
        SetStatus(RelayRuntimeState.Degraded, current.Mounts, current.DesiredMountCount, _leases.Count, current.FailedMountCount);
    }

    private void OnUnexpectedStopped(RelayMountTarget target, string attemptId)
    {
        MountedLease? mounted = null;
        bool removed = false;
        bool queued = false;
        lock (_leaseStateLock)
        {
            if (_leases.TryGetValue(target.TargetPath, out mounted))
            {
                if (string.Equals(mounted.AttemptId, attemptId, StringComparison.Ordinal))
                    removed = _leases.TryRemove(target.TargetPath, out _);
            }
            else if (_mountAttempts.TryGetValue(target.TargetPath, out var activeAttempt)
                && string.Equals(activeAttempt, attemptId, StringComparison.Ordinal))
            {
                _unexpectedStops[attemptId] = 0;
                queued = true;
            }

            if (removed)
                RefreshIgnoreRoots();
        }

        if (removed)
        {
            AddCapability("MountStoppedUnexpectedly");
            var current = Status;
            var mounts = current.Mounts
                .Where(mount => mount.ManagedFolderId != target.ManagedFolderId || mount.RootKind != target.RootKind)
                .ToArray();
            SetStatus(RelayRuntimeState.Degraded, mounts, current.DesiredMountCount, _leases.Count, current.FailedMountCount);
            MarkDirty(fullReconcile: true);
            return;
        }

        if (queued)
        {
            AddCapability("MountStoppedUnexpectedly");
            MarkDirty(fullReconcile: true);
        }
    }

    private void OnDaemonStopped(RelayMountTarget target, string attemptId) =>
        OnLeaseCompleted(target, attemptId);

    private void OnCleanupCompleted(RelayMountTarget target, string attemptId) =>
        OnLeaseCompleted(target, attemptId);

    private void OnLeaseCompleted(RelayMountTarget target, string attemptId)
    {
        bool removed = false;
        lock (_leaseStateLock)
        {
            if (_leases.TryGetValue(target.TargetPath, out var mounted)
                && string.Equals(mounted.AttemptId, attemptId, StringComparison.Ordinal))
                removed = _leases.TryRemove(target.TargetPath, out _);

            if (_pendingLeases.TryGetValue(target.TargetPath, out var pending)
                && string.Equals(pending.AttemptId, attemptId, StringComparison.Ordinal))
                removed |= _pendingLeases.TryRemove(target.TargetPath, out _);

            if (removed)
                RefreshIgnoreRoots();
        }

        if (removed)
            MarkDirty(fullReconcile: true);
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;
        _subscribed = true;
        _videoService.VideoFileDetected += OnContent;
        _videoService.VideoFileHashed += OnContent;
        _videoService.VideoFileDeleted += OnContent;
        _relocationService.FileRelocated += OnContent;
        _releaseService.ReleaseSaved += OnContent;
        _releaseService.ReleaseDeleted += OnContent;
        _videoService.ManagedFolderAdded += OnTopology;
        _videoService.ManagedFolderUpdated += OnTopology;
        _videoService.ManagedFolderRemoved += OnTopology;
        _configurationProvider.Saved += OnConfigurationSaved;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        _videoService.VideoFileDetected -= OnContent;
        _videoService.VideoFileHashed -= OnContent;
        _videoService.VideoFileDeleted -= OnContent;
        _relocationService.FileRelocated -= OnContent;
        _releaseService.ReleaseSaved -= OnContent;
        _releaseService.ReleaseDeleted -= OnContent;
        _videoService.ManagedFolderAdded -= OnTopology;
        _videoService.ManagedFolderUpdated -= OnTopology;
        _videoService.ManagedFolderRemoved -= OnTopology;
        _configurationProvider.Saved -= OnConfigurationSaved;
        _subscribed = false;
    }

    private void OnContent(object? sender, EventArgs _) => MarkDirty(fullReconcile: false);

    private void OnTopology(object? sender, EventArgs _) => MarkDirty(fullReconcile: true);

    private void OnConfigurationSaved(object? sender, ConfigurationSavedEventArgs<FusePluginConfiguration> _) => MarkDirty(fullReconcile: true);

    private void MarkDirty(bool fullReconcile)
    {
        if (fullReconcile)
            Interlocked.Exchange(ref _topologyDirty, 1);
        else
            Interlocked.Exchange(ref _contentDirty, 1);
        Signal();
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RegisterIgnoreRule()
    {
        try
        {
            if (_videoService.IgnoreRules.Count > 0)
            {
                AddCapability("IgnoreRuleRegistrationUnavailable");
                return;
            }

            _videoService.AddParts([_ignoreRule]);
            if (!_videoService.IgnoreRules.Contains(_ignoreRule))
                AddCapability("IgnoreRuleRegistrationUnavailable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay ignore rule could not be registered.");
            AddCapability("IgnoreRuleRegistrationUnavailable");
        }
    }

    private void AddCapability(string code)
    {
        lock (_statusLock)
            _capabilityCodes.Add(code);
        RefreshCapabilityStatus();
    }

    private void AddCapabilities(IEnumerable<string> codes)
    {
        lock (_statusLock)
            foreach (var code in codes)
                _capabilityCodes.Add(code);
    }

    private bool HasCapabilities()
    {
        lock (_statusLock)
            return _capabilityCodes.Count > 0;
    }

    private void SetState(RelayRuntimeState state)
    {
        var current = Status;
        SetStatus(state, current.Mounts, current.DesiredMountCount, current.ActiveMountCount, current.FailedMountCount);
    }

    private void RefreshCapabilityStatus()
    {
        var current = Status;
        if (current.State is RelayRuntimeState.Healthy or RelayRuntimeState.Degraded)
            SetStatus(RelayRuntimeState.Degraded, current.Mounts, current.DesiredMountCount, current.ActiveMountCount, current.FailedMountCount);
    }

    private void SetStatus(RelayRuntimeState state, IEnumerable<RelayMountStatus> mounts, int desired, int active, int failed)
    {
        string[] capabilities;
        lock (_statusLock)
            capabilities = _capabilityCodes.OrderBy(code => code, StringComparer.Ordinal).ToArray();
        var snapshot = new RelayHealthStatus(state, mounts.OrderBy(mount => mount.ManagedFolderId).ThenBy(mount => mount.RootKind).ToArray(), capabilities, desired, active, failed);
        Volatile.Write(ref _status, snapshot);
    }

    private sealed class RelayIgnoreRule : IManagedFolderIgnoreRule
    {
        private Root[] _roots = [];

        public string Name => "Shoko.VFS.FUSE Relay roots";

        public void SetRoots(IEnumerable<RelayMountTarget> targets) =>
            Volatile.Write(ref _roots, targets.Select(target => new Root(target.ManagedFolderId, Path.GetFullPath(target.TargetPath))).ToArray());

        public bool ShouldIgnore(IManagedFolder folder, FileSystemInfo fileSystemInfo)
        {
            string path;
            try
            {
                path = Path.GetFullPath(fileSystemInfo.FullName);
            }
            catch
            {
                return false;
            }

            foreach (var root in Volatile.Read(ref _roots))
            {
                if (root.ManagedFolderId != folder.ID)
                    continue;
                if (string.Equals(path, root.Path, PathComparison))
                    return true;
                if (path.StartsWith(root.Path + Path.DirectorySeparatorChar, PathComparison)
                    || path.StartsWith(root.Path + Path.AltDirectorySeparatorChar, PathComparison))
                    return true;
            }
            return false;
        }

        private sealed record Root(int ManagedFolderId, string Path);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string ComputeFingerprint(
        FusePluginConfiguration configuration,
        RelayMountTarget target,
        IReadOnlyList<IReadOnlyList<int>> overrideGroups)
    {
        var value = new
        {
            configuration.RelayEnabled,
            configuration.MovieGenerationMode,
            configuration.TmdbEpNumbering,
            configuration.MergeTmdbSeries,
            configuration.PlexLocalExtras,
            configuration.FolderExclusions,
            configuration.ManagedFolderExclusions,
            configuration.SeriesTitleLanguage,
            configuration.EpisodeTitleLanguage,
            configuration.MoveCommonSeriesTitlePrefixes,
            configuration.TmdbEpGroupNames,
            configuration.RelayTvFolderName,
            configuration.RelayMovieFolderName,
            configuration.FuseAllowOther,
            configuration.AttrTimeout,
            configuration.EntryTimeout,
            configuration.NegativeTimeout,
            OverrideGroups = overrideGroups.Select(group => group.ToArray()).ToArray(),
            Target = new
            {
                target.ManagedFolderId,
                target.ManagedFolderName,
                target.ManagedFolderPath,
                target.RootName,
                target.TargetPath,
                target.RootKind,
                ResolverOptions = new
                {
                    target.ResolverOptions.Shows,
                    target.ResolverOptions.MoviesAsTv,
                    target.ResolverOptions.StandaloneMovies,
                    target.ResolverOptions.CacheTtl,
                    target.ResolverOptions.IncludeMovieExtras,
                },
            },
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    }

    private sealed record MountedLease(RelayMountLease Lease, string Fingerprint, string AttemptId);
}
