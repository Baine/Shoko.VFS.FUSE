using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;
using Shoko.VFS.FUSE.Runtime;
using Shoko.VFS.FUSE.Host.Validation;
using Microsoft.Extensions.Logging;

namespace Shoko.VFS.FUSE.Host.Daemon;

/// <summary>
/// Per-mount state tracked by the daemon. Mirrors the plugin's <c>RelayMountStatusState</c>
/// and adds a host-specific code for path-mapping mismatches.
/// </summary>
public enum DaemonMountState
{
    /// <summary>Mount is active and healthy.</summary>
    Mounted,

    /// <summary>Mount is blocked by policy (e.g. duplicate root, source-only folder).</summary>
    Blocked,

    /// <summary>Mount is present but the FUSE daemon has stopped unexpectedly.</summary>
    Failed,

    /// <summary>Mount failed initial path validation — source paths do not exist on disk.</summary>
    PathMappingMismatch,

    /// <summary>Mount is stopped (unmounted).</summary>
    Stopped,
}

/// <summary>State of one managed-folder mount within the daemon.</summary>
public sealed record DaemonMountStatus(
    int ManagedFolderId,
    string ManagedFolderName,
    string TargetPath,
    string RootKind,
    DaemonMountState State,
    string? Error = null,
    DateTime? LastStartedAt = null,
    DateTime? LastStoppedAt = null)
{
    public bool IsMounted => State == DaemonMountState.Mounted;
    public bool IsFailed => State == DaemonMountState.Failed || State == DaemonMountState.PathMappingMismatch;
}

/// <summary>Host-side lifecycle lease for one FUSE mount, modeled on the plugin's RelayMountLease.</summary>
///
/// Internally manages a <c>ShokoRelayDataSource</c>, a <c>ShokoPathResolver</c>, and a
/// <c>FuseMountService</c>. Provides <c>Invalidate()</c>, <c>DisposeAsync()</c> and
/// lifecycle events wired to the service's <c>UnexpectedStopped</c> / <c>DaemonStopped</c> /
/// <c>CleanupCompleted</c> (all <c>internal</c> on the service — IVT to this host).
public sealed class DaemonLease : IAsyncDisposable
{
    private readonly FuseMountService _service;
    private readonly ShokoPathResolver _resolver;
    private readonly ShokoRelayDataSource _dataSource;
    private readonly ILogger<DaemonLease> _logger;
    private readonly Action? _onUnexpectedStopped;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private int _disposed;

    /// <summary>The mount target this lease was created from.</summary>
    public RelayMountTarget Target { get; }

    /// <summary>The resolved mount point path.</summary>
    public string MountPoint => _service.MountPoint;

    /// <summary>Human-readable mount name.</summary>
    public string Name => _service.Name;

    /// <summary>Whether the FUSE mount is currently running.</summary>
    public bool IsRunning => _service.IsRunning;

    /// <summary>Current mount state (updated by lifecycle callbacks).</summary>
    public DaemonMountState State { get; private set; } = DaemonMountState.Stopped;

    /// <summary>Last error message, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Last time the mount was started.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Last time the mount was stopped.</summary>
    public DateTime? StoppedAt { get; private set; }

    internal DaemonLease(
        RelayMountTarget target,
        FuseMountService service,
        ShokoPathResolver resolver,
        ShokoRelayDataSource dataSource,
        ILogger<DaemonLease> logger,
        Action? onUnexpectedStopped = null)
    {
        Target = target;
        _service = service;
        _resolver = resolver;
        _dataSource = dataSource;
        _logger = logger;
        _onUnexpectedStopped = onUnexpectedStopped;

        // Wire internal service events to the lease lifecycle.
        _service.UnexpectedStopped += NotifyUnexpectedStopped;
        _service.DaemonStopped += NotifyDaemonStopped;
        _service.CleanupCompleted += NotifyCleanupCompleted;
    }

    private void NotifyUnexpectedStopped()
    {
        lock (this)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            State = DaemonMountState.Failed;
            Error = "FUSE daemon stopped unexpectedly";
            _logger.LogWarning("Mount {Name} daemon stopped unexpectedly; state -> {State}", Name, State);
        }
        // Signal orchestrator to reconcile and re-mount.
        _onUnexpectedStopped?.Invoke();
    }

    private void NotifyDaemonStopped()
    {
        lock (this)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            State = DaemonMountState.Stopped;
            StoppedAt = DateTime.UtcNow;
            _logger.LogInformation("Mount {Name} daemon stopped", Name);
        }
    }

    private void NotifyCleanupCompleted()
    {
        lock (this)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            State = DaemonMountState.Stopped;
            _logger.LogInformation("Mount {Name} cleanup completed", Name);
        }
    }

    /// <summary>
    /// Forces the resolver (and its aggregation cache) to refresh. This is called by
    /// the event coalescer when content or topology changes are detected.
    /// </summary>
    public void Invalidate()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _dataSource.Invalidate();
        _resolver.Invalidate("", includeChildren: true);
    }

    /// <summary>
    /// Pins the underlying data source so the FUSE mount keeps serving the last known
    /// snapshot while the Shoko server is unreachable. No rebuild attempts happen while
    /// frozen; reads continue to return the cached series list.
    /// </summary>
    public void Freeze()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _dataSource.Freeze();
    }

    /// <summary>
    /// Releases the freeze and marks the resolver dirty so the next read rebuilds the
    /// snapshot from the (now reachable) Shoko server.
    /// </summary>
    public void Unfreeze()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _dataSource.Unfreeze();
        _resolver.Invalidate("", includeChildren: true);
    }

    /// <summary>True if the underlying data source is currently frozen.</summary>
    public bool IsFrozen => _dataSource.IsFrozen;

    /// <summary>Loads the persisted snapshot from the configured store.</summary>
    public bool TryLoadFromStore() => _dataSource.TryLoadFromStore();

    /// <summary>True when a snapshot store is configured and the last shutdown was clean.</summary>
    public bool WasLastShutdownClean() => _dataSource.WasLastShutdownClean();

    /// <summary>Discards any persisted snapshot + clean-shutdown marker for this mount.</summary>
    public void InvalidateStored() => _dataSource.InvalidateStored();

    /// <summary>Persists the current snapshot via the configured <see cref="FileSnapshotStore"/>.</summary>
    public void SaveToStore() => _dataSource.SaveToStore();

    /// <summary>
    /// Aggregates the mount's series data and validates that sample source paths exist
    /// on disk. Returns <c>false</c> on a server/host path-mapping mismatch.
    /// </summary>
    public async Task<bool> ValidateSourcePathsAsync(int sampleCount, int maxPathLength, Action<string>? log = null)
    {
        var series = await _dataSource.GetAllSeriesAsync().ConfigureAwait(false);
        return PathValidation.ValidatePaths(series, sampleCount, maxPathLength, log);
    }

    /// <summary>Marks the lease as a path-mapping mismatch (used by the orchestrator).</summary>
    public void MarkPathMappingMismatch(string? error = null)
    {
        lock (this)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            State = DaemonMountState.PathMappingMismatch;
            Error = error ?? "Path validation failed: source paths missing on disk";
        }
    }

    /// <summary>
    /// Starts the FUSE mount. Returns immediately; throws <c>FuseStartException</c> on classified failures.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (this)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(Name);
            State = DaemonMountState.Mounted;
            StartedAt = DateTime.UtcNow;
            Error = null;
        }

        await _service.StartAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Mount {Name} started at {MountPoint}", Name, MountPoint);
    }

    /// <summary>
    /// Stops and unmounts the lease. Idempotent — calling twice is safe.
    /// </summary>
    public async Task StopAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                // Non-blocking stop (the service never blocks on the daemon thread).
                _ = _service.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mount {Name}: StopAsync failed", Name);
            }
            _service.Dispose();

            _resolver.Stop(); // internal — IVT to Host
            _dataSource.Dispose();

            // Unsubscribe from events (they reference the lease).
            _service.UnexpectedStopped -= NotifyUnexpectedStopped;
            _service.DaemonStopped -= NotifyDaemonStopped;
            _service.CleanupCompleted -= NotifyCleanupCompleted;

            Volatile.Write(ref _disposed, 1);
            State = DaemonMountState.Stopped;
            StoppedAt = DateTime.UtcNow;
            _logger.LogInformation("Mount {Name} stopped", Name);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync().ConfigureAwait(false);
}