using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using FuseDotNet;
using FuseNative = FuseDotNet.Fuse;
using Microsoft.Extensions.Logging;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Fuse;

/// <summary>Classified reasons a FUSE mount cannot start.</summary>
internal enum FuseStartReason
{
    UnsupportedPlatform,
    FuseDeviceUnavailable,
    FuseMountHelperUnavailable,
    FuseLibraryUnavailable,
    MountAccessDenied,
    MountTargetBlocked,
    MountAlreadyMountedUnknown,
    MountTimeout,
    MountNotResponsive,
    MountIdentityUnavailable,
    MountOwnershipLost,
    MountFailed,
}

/// <summary>An exception carrying a classified <see cref="FuseStartReason"/> for structured failure handling.</summary>
internal sealed class FuseStartException(FuseStartReason reason, string message)
    : InvalidOperationException(message)
{
    public FuseStartReason Reason { get; } = reason;
}

/// <summary>Pure decision logic for mount-point ownership, unit-testable without the kernel.</summary>
internal readonly record struct OwnershipDecision(bool Create, bool AcceptEmpty, bool Block, FuseStartReason? BlockReason)
{
    /// <summary>
    /// Classifies target state into an ownership action.
    /// </summary>
    public static OwnershipDecision Evaluate(
        bool parentExists,
        bool targetExists,
        bool targetIsEmptyDir,
        bool targetIsFile,
        bool targetIsMounted)
    {
        if (!parentExists)
            return new(false, false, true, FuseStartReason.MountAccessDenied);

        if (targetIsMounted)
            return new(false, false, true, FuseStartReason.MountAlreadyMountedUnknown);

        if (targetIsFile)
            return new(false, false, true, FuseStartReason.MountTargetBlocked);

        if (!targetExists)
            return new(true, false, false, null);

        if (targetIsEmptyDir)
            return new(false, true, false, null);

        // exists but non-empty directory
        return new(false, false, true, FuseStartReason.MountTargetBlocked);
    }
}

/// <summary>Identity observed for one process-owned mount.</summary>
internal readonly record struct MountIdentity(
    string CanonicalPath,
    int MountId,
    string FileSystemType,
    string SourceToken);

internal readonly record struct MountInfoEntry(
    string CanonicalPath,
    int MountId,
    string FileSystemType,
    string SourceToken)
{
    public MountIdentity Identity => new(CanonicalPath, MountId, FileSystemType, SourceToken);
}

internal enum FilesystemProbeState
{
    True,
    False,
    Failed,
}

/// <summary>
/// Owns the lifecycle of a single FUSE mount: validates the mount point,
/// starts the FUSE daemon, and unmounts cleanly on shutdown.
/// Only unmounts when this instance started the daemon.
/// </summary>
public sealed class FuseMountService : IDisposable
{
    private readonly MountOptions _options;
    private readonly IVirtualPathResolver _resolver;
    private readonly ILogger _logger;
    private readonly Func<string, bool>? _probeDirectoryExists;
    private readonly Func<string, bool>? _probeFileExists;
    private readonly Func<string, bool>? _probeDirectoryEmpty;
    private readonly Func<string, CancellationToken, Task<bool>>? _probeConnection;
    private readonly Func<bool>? _probeFuseRunning;
    private readonly Func<bool>? _probeFuseDevice;
    private readonly Func<bool>? _probeFusermount;
    private readonly Action _ensureNativeResolver;
    private FuseService? _fuse;
    private ShokoFuseFileSystem? _fs;
    private MountIdentity? _mountIdentity;
    private string? _sourceToken;
    private bool _disposed;
    private bool _stopping;
    private bool _ownsDaemon; // true only after successful _fuse.Start()
    private int _cleanupPending;
    private readonly object _cleanupLock = new();

    /// <summary>Raised when the owned daemon stops without a requested shutdown.</summary>
    internal event Action? UnexpectedStopped;

    /// <summary>Raised when the daemon exits, including while cleanup is retrying.</summary>
    internal event Action? DaemonStopped;

    /// <summary>Raised after this service has confirmed cleanup and released its daemon.</summary>
    internal event Action? CleanupCompleted;

    internal bool CleanupPending => Volatile.Read(ref _cleanupPending) != 0;

    public string Name => _options.Name;
    public string MountPoint => _options.MountPoint;
    public bool IsRunning => _fuse?.Running == true;

    public FuseMountService(MountOptions options, IVirtualPathResolver resolver, ILogger logger)
    {
        _options = options;
        _resolver = resolver;
        _logger = logger;
        _ensureNativeResolver = FuseNativeLibraryResolver.EnsureRegistered;
    }

    internal FuseMountService(MountOptions options, IVirtualPathResolver resolver, ILogger logger,
        Action ensureNativeResolver)
        : this(options, resolver, logger)
    {
        _ensureNativeResolver = ensureNativeResolver;
    }

    // Internal constructor for test seam injection.
    internal FuseMountService(MountOptions options, IVirtualPathResolver resolver, ILogger logger,
        Func<bool>? probeFuseDevice, Func<bool>? probeFusermount,
        Func<string, bool>? probeDirectoryExists = null,
        Func<string, bool>? probeFileExists = null,
        Func<string, bool>? probeDirectoryEmpty = null,
        Action? ensureNativeResolver = null,
        Func<string, CancellationToken, Task<bool>>? probeConnection = null,
        Func<bool>? probeFuseRunning = null,
        FuseService? fuseForTesting = null)
        : this(options, resolver, logger)
    {
        _probeFuseDevice = probeFuseDevice;
        _probeFusermount = probeFusermount;
        _probeDirectoryExists = probeDirectoryExists;
        _probeFileExists = probeFileExists;
        _probeDirectoryEmpty = probeDirectoryEmpty;
        _probeConnection = probeConnection;
        _probeFuseRunning = probeFuseRunning;
        _ensureNativeResolver = ensureNativeResolver ?? FuseNativeLibraryResolver.EnsureRegistered;
        _fuse = fuseForTesting;
        _ownsDaemon = fuseForTesting is not null;
    }

    /// <summary>Mounts the filesystem. Throws <see cref="FuseStartException"/> for classified failures.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        // 1. Platform check
        if (!OperatingSystem.IsLinux())
            throw new FuseStartException(FuseStartReason.UnsupportedPlatform,
                $"FUSE mount {Name} requires Linux.");

        try
        {
            _ensureNativeResolver();
        }
        catch (FuseLibraryUnavailableException)
        {
            throw new FuseStartException(FuseStartReason.FuseLibraryUnavailable,
                "FUSE native library support is unavailable.");
        }

        // 2. Parent check — mount point must be a direct child of an existing directory
        bool parentExists;
        bool targetExists;
        bool targetIsFile;
        bool targetIsEmptyDir;
        bool targetIsMounted;
        try
        {
            var parent = Path.GetDirectoryName(_options.MountPoint);
            // Check mountinfo before any directory-content probe. A visible
            // mount is never inspected as if it were a physical directory.
            var mountInfo = await ReadMountInfoAsync(ct).ConfigureAwait(false);
            targetIsMounted = FindMountInfo(mountInfo, _options.MountPoint, out _);
            ct.ThrowIfCancellationRequested();

            var parentState = string.IsNullOrEmpty(parent)
                ? FilesystemProbeState.False
                : await RunFilesystemProbeAsync(() => DirectoryExists(parent), ct).ConfigureAwait(false);
            var targetState = await RunFilesystemProbeAsync(() => DirectoryExists(_options.MountPoint), ct).ConfigureAwait(false);
            var fileState = await RunFilesystemProbeAsync(() => FileExists(_options.MountPoint), ct).ConfigureAwait(false);
            var emptyState = targetState == FilesystemProbeState.True && !targetIsMounted
                ? await RunFilesystemProbeAsync(() => IsDirectoryEmpty(_options.MountPoint), ct).ConfigureAwait(false)
                : FilesystemProbeState.False;

            if (parentState == FilesystemProbeState.Failed
                || targetState == FilesystemProbeState.Failed
                || fileState == FilesystemProbeState.Failed
                || emptyState == FilesystemProbeState.Failed)
                throw new FuseStartException(FuseStartReason.MountAccessDenied,
                    $"FUSE mount {Name} target inspection failed.");

            parentExists = parentState == FilesystemProbeState.True;
            targetExists = targetState == FilesystemProbeState.True;
            targetIsFile = fileState == FilesystemProbeState.True;
            targetIsEmptyDir = emptyState == FilesystemProbeState.True;
        }
        catch (UnauthorizedAccessException)
        {
            throw new FuseStartException(FuseStartReason.MountAccessDenied, "FUSE mount access was denied.");
        }
        catch (TimeoutException)
        {
            throw new FuseStartException(FuseStartReason.MountIdentityUnavailable, "FUSE mount identity could not be read.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            throw new FuseStartException(FuseStartReason.MountFailed, "FUSE mount target could not be inspected.");
        }

        var decision = OwnershipDecision.Evaluate(parentExists, targetExists, targetIsEmptyDir, targetIsFile, targetIsMounted);

        if (decision.Block)
            throw new FuseStartException(decision.BlockReason!.Value,
                $"FUSE mount {Name} refused: {decision.BlockReason.Value}");

        if (decision.Create)
        {
            // Create exactly this one directory — this service owns it.
            try
            {
                Directory.CreateDirectory(_options.MountPoint);
            }
            catch (UnauthorizedAccessException)
            {
                throw new FuseStartException(FuseStartReason.MountAccessDenied, "FUSE mount target could not be created.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
            {
                throw new FuseStartException(FuseStartReason.MountFailed, "FUSE mount target could not be created.");
            }
        }
        else if (decision.AcceptEmpty)
        {
            // Existing empty directory is acceptable; continue.
        }

        // 4. Host prerequisite probes (non-destructive)
        ct.ThrowIfCancellationRequested();
        if (!await RunBoundedBooleanProbeAsync(CheckFuseDevice, ct).ConfigureAwait(false))
            throw new FuseStartException(FuseStartReason.FuseDeviceUnavailable,
                $"FUSE mount {Name} refused: /dev/fuse unavailable.");

        ct.ThrowIfCancellationRequested();
        if (!await RunBoundedBooleanProbeAsync(CheckFusermount, ct).ConfigureAwait(false))
            throw new FuseStartException(FuseStartReason.FuseMountHelperUnavailable,
                $"FUSE mount {Name} refused: fusermount3 not found.");

        // 5. Start FUSE daemon (NO stale unmount — auto_unmount handles crash recovery)
        _fs = new ShokoFuseFileSystem(_resolver, _logger, _options.MaxReadSize);
        _sourceToken = $"Shoko.VFS.FUSE:{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        var args = BuildArgs(_sourceToken);
        _fuse = new FuseService(_fs, args);
        _fuse.Error += ErrorHandler;
        _fuse.Dismounting += DismountingHandler;
        _fuse.Stopped += StoppedHandler;

        try
        {
            _fuse.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // No daemon can be owned when the native library could not load.
            _ownsDaemon = false;
            BeginPendingCleanup();
            throw new FuseStartException(FuseStartReason.FuseLibraryUnavailable,
                "FUSE native library support is unavailable.");
        }
        catch (Exception)
        {
            // Start may have created a daemon before surfacing an error. Mark
            // it as a cleanup candidate; ShutdownSafe still requires this
            // start's exact mountinfo identity before unmounting anything.
            _ownsDaemon = true;
            BeginPendingCleanup();
            throw new FuseStartException(FuseStartReason.MountFailed,
                $"FUSE mount {Name} failed to start.");
        }

        // This instance started the daemon from here on; partial-start failures
        // (timeout/not-responsive) must still unmount it, not leak a dead mount.
        _ownsDaemon = true;

        // FuseService.Start() spawns a background thread; Running is true
        // immediately even before the kernel mount is live. Wait for the
        // mount point to actually appear in the kernel mount table...
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            MountIdentity identity;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_fuse.IsDisposed || DateTime.UtcNow > deadline)
                    throw new FuseStartException(FuseStartReason.MountTimeout,
                        $"FUSE mount {Name} failed to appear within timeout.");

                var mountInfo = await ReadMountInfoAsync(ct).ConfigureAwait(false);
                if (FindMountInfo(mountInfo, _options.MountPoint, out var observed))
                {
                    if (!IsExpectedMount(observed, _options.MountPoint, _sourceToken!))
                        throw new FuseStartException(FuseStartReason.MountOwnershipLost,
                            $"FUSE mount {Name} was replaced by a foreign mount.");

                    identity = observed.Identity;
                    _mountIdentity = identity;
                    break;
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            // ...then wait until a root stat/GETATTR round-trips through the daemon.
            await WaitForConnectionAsync(
                _options.MountPoint,
                identity,
                _sourceToken!,
                ReadMountInfoAsync,
                ProbeConnectionAsync,
                () => _fuse.IsDisposed,
                ct,
                ConnectionTimeout).ConfigureAwait(false);

            _logger.LogInformation("FUSE mount {Name} started", Name);
        }
        catch (FuseStartException)
        {
            BeginPendingCleanup();
            throw;
        }
        catch (OperationCanceledException)
        {
            BeginPendingCleanup();
            throw;
        }
        catch (Exception)
        {
            // Wrap unexpected exceptions in a classified failure
            BeginPendingCleanup();
            throw new FuseStartException(FuseStartReason.MountFailed,
                $"FUSE mount {Name} failed to start.");
        }
    }

    /// <summary>Unmounts the filesystem. Only acts if this instance owns the daemon. Idempotent.</summary>
    public Task StopAsync()
    {
        if (!ShutdownSafe())
            throw new InvalidOperationException($"FUSE mount {Name} cleanup is incomplete.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the FUSE daemon and unmounts the filesystem without ever
    /// blocking indefinitely. Only unmounts when this instance started the daemon.
    /// </summary>
    private bool ShutdownSafe()
    {
        lock (_cleanupLock)
            return ShutdownSafeCore();
    }

    private bool ShutdownSafeCore()
    {
        var fuse = _fuse;
        if (fuse is null)
            return true;

        // Only unmount if this instance started the daemon.
        if (!_ownsDaemon)
        {
            if (!fuse.Running && _mountIdentity is null)
                return CompleteCleanup(fuse);
            return false;
        }

        // A stopped/disposed daemon is no longer capable of owning a mount.
        // Release this service without probing or unmounting a path that may
        // have been replaced by another process.
        if (fuse.IsDisposed || !IsFuseRunning(fuse))
            return CompleteCleanup(fuse);

        if (!TryReadMountInfo(out var mountInfo))
        {
            _logger.LogWarning("Cannot verify FUSE mount identity for {Name}; skipping unmount", Name);
            return false;
        }

        if (!FindMountInfo(mountInfo, _options.MountPoint, out var observed))
        {
            // Mount is absent from mountinfo: no path-based unmount is
            // possible or needed. Dispose the daemon to release the process.
            return CompleteCleanup(fuse);
        }

        if (_mountIdentity is null)
        {
            if (!TryAdoptMountIdentity(mountInfo, _options.MountPoint, _sourceToken, out var adopted))
            {
                _logger.LogWarning("FUSE mount identity for {Name} cannot be adopted; skipping unmount", Name);
                return false;
            }
            _mountIdentity = adopted;
        }

        if (!MountIdentityMatches(_mountIdentity.Value, observed.Identity)
            || !IsExpectedMount(observed, _options.MountPoint, _sourceToken))
        {
            _logger.LogWarning("FUSE mount identity for {Name} no longer matches; skipping unmount", Name);
            return false;
        }

        _stopping = true;
        try
        {
            // Recheck immediately before the path-based unmount. FuseDotNet
            // exposes no mount FD/PID/ID, so this is the strongest safe
            // identity check available; a replacement can still race here.
            if (!TryReadMountInfo(out mountInfo)
                || !FindMountInfo(mountInfo, _options.MountPoint, out observed)
                || !_mountIdentity.Value.Equals(observed.Identity)
                || !IsExpectedMount(observed, _options.MountPoint, _sourceToken))
            {
                _logger.LogWarning("FUSE mount identity for {Name} changed before unmount; skipping unmount", Name);
                _stopping = false;
                return false;
            }

            _logger.LogInformation("Unmounting FUSE mount {Name}", Name);
            Unmount(_options.MountPoint);
            if (IsFuseRunning(fuse))
                fuse.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error unmounting {Name}; retaining mount identity", Name);
        }

        if (TryReadMountInfo(out mountInfo)
            && !FindMountInfo(mountInfo, _options.MountPoint, out _))
        {
            // Mount is gone after unmount attempt: clean up regardless
            // of daemon state. The mount is already released.
            return CompleteCleanup(fuse);
        }

        _logger.LogWarning(
            "FUSE mount for {Name} still present after unmount attempt; retaining state",
            Name);
        _stopping = false;
        GC.KeepAlive(fuse);
        return false;
    }

    private bool CompleteCleanup(FuseService fuse)
    {
        fuse.Error -= ErrorHandler;
        fuse.Dismounting -= DismountingHandler;
        fuse.Stopped -= StoppedHandler;

        try
        {
            if (!fuse.IsDisposed)
                fuse.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FUSE daemon for {Name} could not be disposed; retaining mount identity", Name);
            return false;
        }

        _ownsDaemon = false;
        _stopping = true;
        _mountIdentity = null;
        _sourceToken = null;
        _fuse = null;
        Volatile.Write(ref _cleanupPending, 0);
        NotifyCleanupCompleted();
        return true;
    }

    private void BeginPendingCleanup()
    {
        lock (_cleanupLock)
        {
            if (_fuse is null || Volatile.Read(ref _cleanupPending) != 0)
                return;

            Volatile.Write(ref _cleanupPending, 1);
            _ = Task.Run(MonitorPendingCleanupAsync);
        }
    }

    private async Task MonitorPendingCleanupAsync()
    {
        while (CleanupPending)
        {
            try
            {
                if (ShutdownSafe())
                    return;

                var fuse = _fuse;
                if (fuse is null)
                    return;

                if (fuse.IsDisposed || !IsFuseRunning(fuse))
                {
                    lock (_cleanupLock)
                    {
                        if (_fuse == fuse && (!IsFuseRunning(fuse) || fuse.IsDisposed))
                            CompleteCleanup(fuse);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pending FUSE cleanup for {Name} could not be completed", Name);
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private void ErrorHandler(object? sender, ThreadExceptionEventArgs e) =>
        _logger.LogError(e.Exception, "FUSE mount {Name} reported an error", Name);

    private bool IsFuseRunning(FuseService fuse) => _probeFuseRunning?.Invoke() ?? fuse.Running;

    private void DismountingHandler(object? sender, EventArgs e) =>
        _logger.LogInformation("FUSE mount {Name} is dismounting", Name);

    private void StoppedHandler(object? sender, EventArgs e)
    {
        _logger.LogInformation("FUSE mount {Name} stopped", Name);
        if (_ownsDaemon && !_stopping)
            NotifyUnexpectedStopped();
        if (_ownsDaemon)
        {
            NotifyDaemonStopped();
            _ = Task.Run(BeginPendingCleanup);
        }
    }

    internal void NotifyUnexpectedStopped()
    {
        foreach (var subscriber in UnexpectedStopped?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected FUSE stop notification for {Name} failed", Name);
            }
        }
    }

    private void NotifyDaemonStopped()
    {
        foreach (var subscriber in DaemonStopped?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FUSE daemon stop completion for {Name} failed", Name);
            }
        }
    }

    private void NotifyCleanupCompleted()
    {
        foreach (var subscriber in CleanupCompleted?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FUSE cleanup completion for {Name} failed", Name);
            }
        }
    }

    /// <summary>
    /// Unmounts a FUSE mount point. Uses fusermount3 (works for non-root
    /// users, unlike the raw umount2 syscall), falling back to a lazy
    /// unmount (succeeds while the filesystem is busy, e.g. a consumer is
    /// streaming), then to the managed unmount API.
    /// </summary>
    internal static void Unmount(string mountPoint)
    {
        FuseNativeLibraryResolver.EnsureRegistered();
        if (!TryFusermountUnmount(mountPoint) && !TryFusermountUnmount(mountPoint, lazy: true))
            FuseNative.Unmount(mountPoint);
    }

    private static bool TryFusermountUnmount(string mountPoint, bool lazy = false)
    {
        var args = lazy ? $"-uz {mountPoint}" : $"-u {mountPoint}";
        try
        {
            var psi = new ProcessStartInfo("fusermount3", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // fusermount3 not available
        }
    }

    /// <summary>Non-destructive probe: does /dev/fuse exist and can this process open it?</summary>
    private bool CheckFuseDevice()
    {
        if (_probeFuseDevice is not null)
            return _probeFuseDevice();
        return File.Exists("/dev/fuse");
    }

    /// <summary>Non-destructive probe: is fusermount3 resolvable on PATH?</summary>
    private bool CheckFusermount()
    {
        if (_probeFusermount is not null)
            return _probeFusermount();
        try
        {
            var psi = new ProcessStartInfo("fusermount3", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs a synchronous OS probe without allowing it to defeat cancellation.</summary>
    internal static async Task<T> RunBoundedProbeAsync<T>(Func<T> probe, CancellationToken ct, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return await Task.Run(probe).WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private static async Task<bool> RunBoundedBooleanProbeAsync(Func<bool> probe, CancellationToken ct) 
    {
        try
        {
            return await RunBoundedProbeAsync(probe, ct, ProbeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<FilesystemProbeState> RunFilesystemProbeAsync(Func<bool> probe, CancellationToken ct)
    {
        try
        {
            return await RunBoundedProbeAsync(probe, ct, ProbeTimeout).ConfigureAwait(false)
                ? FilesystemProbeState.True
                : FilesystemProbeState.False;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            return FilesystemProbeState.Failed;
        }
    }

    private bool DirectoryExists(string path) => _probeDirectoryExists?.Invoke(path) ?? Directory.Exists(path);

    private bool FileExists(string path) => _probeFileExists?.Invoke(path) ?? File.Exists(path);

    private bool IsDirectoryEmpty(string path) => _probeDirectoryEmpty?.Invoke(path)
        ?? !Directory.EnumerateFileSystemEntries(path).Any();

    private Task<bool> ProbeConnectionAsync(string mountPoint, CancellationToken ct)
    {
        if (_probeConnection is not null)
            return _probeConnection(mountPoint, ct);

        // A root stat reaches the daemon (GETATTR round-trip) without forcing
        // virtual root enumeration. The caller supplies the remaining readiness
        // deadline; do not use a short retry timeout that leaves abandoned
        // concurrent probes behind.
        return Task.Run(() => IsConnected(mountPoint), ct);
    }

    internal static async Task WaitForConnectionAsync(
        string mountPoint,
        MountIdentity expectedIdentity,
        string sourceToken,
        Func<CancellationToken, Task<IReadOnlyList<MountInfoEntry>>> readMountInfo,
        Func<string, CancellationToken, Task<bool>> connectionProbe,
        Func<bool> isDisposed,
        CancellationToken ct,
        TimeSpan connectionTimeout)
    {
        ArgumentNullException.ThrowIfNull(readMountInfo);
        ArgumentNullException.ThrowIfNull(connectionProbe);
        ArgumentNullException.ThrowIfNull(isDisposed);

        var connectedDeadline = DateTime.UtcNow.Add(connectionTimeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (isDisposed() || DateTime.UtcNow > connectedDeadline)
                throw new FuseStartException(FuseStartReason.MountNotResponsive,
                    $"FUSE mount at {mountPoint} did not become responsive within timeout.");

            var mountInfo = await readMountInfo(ct).ConfigureAwait(false);
            if (!FindMountInfo(mountInfo, mountPoint, out var observed)
                || !IsExpectedMount(observed, mountPoint, sourceToken)
                || !expectedIdentity.Equals(observed.Identity))
                throw new FuseStartException(FuseStartReason.MountOwnershipLost,
                    "FUSE mount lost its process-owned identity.");

            TimeSpan remaining = connectedDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new FuseStartException(FuseStartReason.MountNotResponsive,
                    "FUSE mount did not become responsive within timeout.");

            bool connected;
            try
            {
                connected = await connectionProbe(mountPoint, ct).WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The synchronous stat task may finish later, but startup stops
                // here and never launches a concurrent replacement probe.
                throw new FuseStartException(FuseStartReason.MountNotResponsive,
                    "FUSE mount did not become responsive within timeout.");
            }

            if (connected)
            {
                var finalMountInfo = await readMountInfo(ct).ConfigureAwait(false);
                if (!FindMountInfo(finalMountInfo, mountPoint, out var finalObserved)
                    || !IsExpectedMount(finalObserved, mountPoint, sourceToken)
                    || !expectedIdentity.Equals(finalObserved.Identity))
                    throw new FuseStartException(FuseStartReason.MountOwnershipLost,
                        "FUSE mount lost its process-owned identity.");
                return;
            }

            TimeSpan retryDelay = connectedDeadline - DateTime.UtcNow;
            if (retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay < TimeSpan.FromMilliseconds(50)
                    ? retryDelay
                    : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
    }

    private static bool IsConnected(string mountPoint)
    {
        try
        {
            // Directory.Exists performs a synchronous stat of the mount root,
            // which requires a daemon GETATTR response but does not enumerate it.
            return Directory.Exists(mountPoint);
        }
        catch
        {
            return false; // failed stat / daemon response
        }
    }

    private static async Task<IReadOnlyList<MountInfoEntry>> ReadMountInfoAsync(CancellationToken ct) =>
        await RunBoundedProbeAsync(ReadMountInfo, ct, ProbeTimeout).ConfigureAwait(false);

    private static IReadOnlyList<MountInfoEntry> ReadMountInfo()
    {
        var entries = new List<MountInfoEntry>();
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            if (TryParseMountInfo(line, out var entry))
                entries.Add(entry);
        }
        return entries;
    }

    private static bool TryReadMountInfo(out IReadOnlyList<MountInfoEntry> entries)
    {
        try
        {
            entries = ReadMountInfo();
            return true;
        }
        catch
        {
            entries = [];
            return false;
        }
    }

    internal static bool TryParseMountInfo(string line, out MountInfoEntry entry)
    {
        entry = default;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int separator = Array.IndexOf(fields, "-");
        if (fields.Length < 7 || separator < 6 || separator + 2 >= fields.Length
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int mountId))
            return false;

        try
        {
            entry = new MountInfoEntry(
                CanonicalizeMountPath(UnescapeMountInfo(fields[4])),
                mountId,
                fields[separator + 1],
                UnescapeMountInfo(fields[separator + 2]));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool FindMountInfo(IEnumerable<MountInfoEntry> entries, string mountPoint, out MountInfoEntry entry)
    {
        entry = default;
        string canonicalPath;
        try
        {
            canonicalPath = CanonicalizeMountPath(mountPoint);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            entry = default;
            return false;
        }

        bool found = false;
        foreach (var candidate in entries)
        {
            if (string.Equals(candidate.CanonicalPath, canonicalPath, StringComparison.Ordinal))
            {
                entry = candidate;
                found = true;
            }
        }

        if (!found)
            entry = default;
        return found;
    }

    internal static bool IsExpectedMount(MountInfoEntry entry, string mountPoint, string? sourceToken)
    {
        if (string.IsNullOrEmpty(sourceToken))
            return false;
        try
        {
            return string.Equals(entry.CanonicalPath, CanonicalizeMountPath(mountPoint), StringComparison.Ordinal)
                && entry.FileSystemType == "fuse.shoko-vfs"
                && entry.SourceToken == sourceToken;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool TryAdoptMountIdentity(
        IEnumerable<MountInfoEntry> entries,
        string mountPoint,
        string? sourceToken,
        out MountIdentity identity)
    {
        if (FindMountInfo(entries, mountPoint, out var observed)
            && IsExpectedMount(observed, mountPoint, sourceToken))
        {
            identity = observed.Identity;
            return true;
        }

        identity = default;
        return false;
    }

    internal static bool MountIdentityMatches(MountIdentity expected, MountIdentity observed) => expected.Equals(observed);

    private static string CanonicalizeMountPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string UnescapeMountInfo(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
            {
                int character = (value[i + 1] - '0') * 64 + (value[i + 2] - '0') * 8 + value[i + 3] - '0';
                builder.Append((char)character);
                i += 3;
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString();
    }

    private static bool IsOctal(char value) => value is >= '0' and <= '7';

    private string[] BuildArgs(string sourceToken)
        => BuildArgs(_options, sourceToken);

    /// <summary>
    /// Pure builder exposed for tests. Pulls out only the fields that affect the FUSE
    /// mount command line; everything else (resolver, logger, runtime state) is irrelevant.
    /// </summary>
    internal static string[] BuildArgs(MountOptions options, string sourceToken)
    {
        // argv[0] must be the program name; libfuse treats the first element
        // as the executable name and ignores it (issues a warning otherwise).
        var args = new List<string>
        {
            "shoko-vfs-fuse",
            "-f", // foreground
            "-o", $"attr_timeout={options.AttrTimeout.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-o", $"entry_timeout={options.EntryTimeout.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-o", $"negative_timeout={options.NegativeTimeout.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-o", $"fsname={sourceToken}",
            "-o", "subtype=shoko-vfs",
            "-o", "auto_unmount", // kernel unmounts if this process dies (crash safety)
        };

        if (options.AllowOther)
            args.AddRange(new[] { "-o", "allow_other" });

        if (options.Uid.HasValue)
            args.AddRange(new[] { "-o", $"uid={options.Uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" });

        if (options.Gid.HasValue)
            args.AddRange(new[] { "-o", $"gid={options.Gid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" });

        args.Add(options.MountPoint);
        return args.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        // Same safe path as StopAsync: never blocks on the daemon thread.
        if (!ShutdownSafe())
        {
            _logger.LogWarning("FUSE mount {Name} cleanup remains incomplete after disposal", Name);
            return;
        }

        _disposed = true;
    }
}
