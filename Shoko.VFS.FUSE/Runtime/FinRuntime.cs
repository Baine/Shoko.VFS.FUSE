using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Fin;

namespace Shoko.VFS.FUSE.Runtime;

/// <summary>Hosted Fin mount coordinator. Exactly one mount, far simpler than Relay's multi-folder machinery.</summary>
public sealed class FinRuntime : BackgroundService
{
    private readonly ISystemService _systemService;
    private readonly IMetadataService _metadataService;
    private readonly ConfigurationProvider<FusePluginConfiguration> _configurationProvider;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FinRuntime> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _leaseLock = new();
    private IFinMountOperations? _mountOperations;
    private FinMountLease? _lease;
    private bool _subscribed;
    private int _state; // (int)FinRuntimeState

    public FinRuntime(
        ISystemService systemService,
        IMetadataService metadataService,
        ConfigurationProvider<FusePluginConfiguration> configurationProvider,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        ILogger<FinRuntime> logger)
    {
        _systemService = systemService ?? throw new ArgumentNullException(nameof(systemService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Internal constructor for test seam injection.</summary>
    internal FinRuntime(
        ISystemService systemService,
        IMetadataService metadataService,
        ConfigurationProvider<FusePluginConfiguration> configurationProvider,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        IFinMountOperations mountOperations,
        ILogger<FinRuntime> logger)
        : this(systemService, metadataService, configurationProvider, applicationPaths, loggerFactory, logger)
    {
        _mountOperations = mountOperations ?? throw new ArgumentNullException(nameof(mountOperations));
    }

    public FinRuntimeState State => (FinRuntimeState)Volatile.Read(ref _state);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetState(FinRuntimeState.WaitingForServer);
        try
        {
            Subscribe();
            await _systemService.WaitForStartupAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            Signal();

            while (true)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fin runtime failed.");
            SetState(FinRuntimeState.Degraded);
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
            await StopLeaseAsync().ConfigureAwait(false);
            SetState(FinRuntimeState.Stopped);
            _signal.Dispose();
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        FusePluginConfiguration configuration;
        try
        {
            configuration = _configurationProvider.Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fin configuration could not be loaded.");
            SetState(FinRuntimeState.Degraded);
            return;
        }

        if (!configuration.FinEnabled)
        {
            await StopLeaseAsync().ConfigureAwait(false);
            SetState(_lease is null ? FinRuntimeState.Disabled : FinRuntimeState.Degraded);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.FinMountPoint) || !Path.IsPathRooted(configuration.FinMountPoint))
        {
            _logger.LogWarning("FinMountPoint is not a valid rooted path; disabling Fin mount.");
            await StopLeaseAsync().ConfigureAwait(false);
            SetState(FinRuntimeState.Disabled);
            return;
        }

        // ponytail: single mount — no planner, no fingerprint comparison.
        // Config save triggers a full rebuild for simplicity.
        await StopLeaseAsync().ConfigureAwait(false);

        try
        {
            var operations = _mountOperations ?? new FinMountOperations();
            var lease = await operations.StartAsync(
                configuration,
                _applicationPaths,
                _metadataService,
                _loggerFactory,
                cancellationToken,
                OnUnexpectedStopped,
                OnDaemonStopped,
                OnCleanupCompleted).ConfigureAwait(false);

            lock (_leaseLock)
            {
                _lease = lease;
            }

            SetState(FinRuntimeState.Healthy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fin mount failed to start.");
            SetState(FinRuntimeState.Degraded);
        }
    }

    private void OnUnexpectedStopped()
    {
        lock (_leaseLock)
        {
            _lease = null;
        }

        _logger.LogWarning("Fin mount stopped unexpectedly; will attempt restart.");
        SetState(FinRuntimeState.Degraded);
        MarkDirty();
    }

    private void OnDaemonStopped()
    {
        lock (_leaseLock)
        {
            _lease = null;
        }
    }

    private void OnCleanupCompleted()
    {
        lock (_leaseLock)
        {
            _lease = null;
        }
        MarkDirty();
    }

    private async Task StopLeaseAsync()
    {
        FinMountLease? lease;
        lock (_leaseLock)
        {
            lease = _lease;
            _lease = null;
        }

        if (lease is null)
            return;

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fin mount stop failed.");
        }
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;
        _subscribed = true;
        _configurationProvider.Saved += OnConfigurationSaved;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        _configurationProvider.Saved -= OnConfigurationSaved;
        _subscribed = false;
    }

    private void OnConfigurationSaved(object? sender, ConfigurationSavedEventArgs<FusePluginConfiguration> _) => MarkDirty();

    private void MarkDirty() => Signal();

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

    private void SetState(FinRuntimeState state) => Volatile.Write(ref _state, (int)state);
}

/// <summary>Runtime states for the Fin mount coordinator.</summary>
public enum FinRuntimeState
{
    WaitingForServer,
    Healthy,
    Degraded,
    Disabled,
    Stopped,
}

/// <summary>Internal lifetime lease for the single Fin mount.</summary>
internal sealed class FinMountLease : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask> _stop;
    private readonly Action _invalidate;
    private readonly Action _stopped;
    private readonly Action? _daemonStopped;
    private readonly Action? _cleanupCompleted;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private int _disposed;

    internal FinMountLease(
        Action invalidate,
        Func<CancellationToken, ValueTask> stop,
        Action stopped,
        Action? daemonStopped = null,
        Action? cleanupCompleted = null)
    {
        _invalidate = invalidate;
        _stop = stop;
        _stopped = stopped;
        _daemonStopped = daemonStopped;
        _cleanupCompleted = cleanupCompleted;
    }

    internal void NotifyUnexpectedStopped()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _stopped();
    }

    internal void NotifyDaemonStopped()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _daemonStopped?.Invoke();
    }

    internal void NotifyCleanupCompleted()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _cleanupCompleted?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await _stop(CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _disposed, 1);
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }
}

/// <summary>Delegate seam for mount operations, testable without the kernel.</summary>
internal interface IFinMountOperations
{
    Task<FinMountLease> StartAsync(
        FusePluginConfiguration configuration,
        IApplicationPaths applicationPaths,
        IMetadataService metadataService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        Action stopped,
        Action daemonStopped,
        Action cleanupCompleted);
}

/// <summary>Creates the real Fin mount backed by FinShokoPathDataSource and FuseMountService.</summary>
internal sealed class FinMountOperations : IFinMountOperations
{
    public Task<FinMountLease> StartAsync(
        FusePluginConfiguration configuration,
        IApplicationPaths applicationPaths,
        IMetadataService metadataService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        Action stopped,
        Action daemonStopped,
        Action cleanupCompleted) =>
        StartAsyncCore(configuration, applicationPaths, metadataService, loggerFactory, cancellationToken, stopped, daemonStopped, cleanupCompleted);

    private static async Task<FinMountLease> StartAsyncCore(
        FusePluginConfiguration configuration,
        IApplicationPaths applicationPaths,
        IMetadataService metadataService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        Action stopped,
        Action daemonStopped,
        Action cleanupCompleted)
    {
        var dataSource = new FinShokoPathDataSource(
            metadataService,
            new FinLibraryProfile(Guid.NewGuid()),
            []);

        var resolver = new ShokoPathResolver(
            dataSource,
            new PathResolverOptions { CacheTtl = TimeSpan.FromSeconds(30) },
            loggerFactory.CreateLogger<ShokoPathResolver>());

        var service = new FuseMountService(
            new MountOptions
            {
                Name = "Fin (Jellyfin)",
                MountPoint = configuration.FinMountPoint,
                AllowOther = configuration.FuseAllowOther,
                Uid = configuration.FuseMountUid,
                Gid = configuration.FuseMountGid,
                AttrTimeout = configuration.AttrTimeout,
                EntryTimeout = configuration.EntryTimeout,
                NegativeTimeout = configuration.NegativeTimeout,
            },
            resolver,
            loggerFactory.CreateLogger<FuseMountService>());

        FinMountLease? lease = null;
        void OnUnexpectedStoppedFromService() => lease?.NotifyUnexpectedStopped();
        void OnDaemonStoppedFromService() => lease?.NotifyDaemonStopped();
        void OnCleanupCompletedFromService() => lease?.NotifyCleanupCompleted();

        lease = new FinMountLease(
            () => resolver.Invalidate("", includeChildren: true),
            async _ =>
            {
                await service.StopAsync().ConfigureAwait(false);
                service.Dispose();
                resolver.Stop();
                service.UnexpectedStopped -= OnUnexpectedStoppedFromService;
                service.DaemonStopped -= OnDaemonStoppedFromService;
                service.CleanupCompleted -= OnCleanupCompletedFromService;
            },
            stopped,
            daemonStopped,
            cleanupCompleted);

        service.UnexpectedStopped += OnUnexpectedStoppedFromService;
        service.DaemonStopped += OnDaemonStoppedFromService;
        service.CleanupCompleted += OnCleanupCompletedFromService;

        try
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch (Exception)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup; the mount may have partially started.
            }
            throw;
        }
    }
}
