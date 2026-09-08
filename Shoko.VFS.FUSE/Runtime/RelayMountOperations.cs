using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Runtime;

/// <summary>Internal lifetime lease for one runtime-owned mount.</summary>
internal sealed class RelayMountLease : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask> _stop;
    private readonly Action _invalidate;
    private readonly Action<int>? _invalidateSeries;
    private readonly Action _stopped;
    private readonly Action? _daemonStopped;
    private readonly Action? _cleanupCompleted;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private int _disposed;
    private int _daemonHasStopped;
    private int _cleanupHasCompleted;

    public RelayMountTarget Target { get; }

    internal RelayMountLease(
        RelayMountTarget target,
        Action invalidate,
        Func<CancellationToken, ValueTask> stop,
        Action stopped,
        Action? daemonStopped = null,
        Action? cleanupCompleted = null,
        Action<int>? invalidateSeries = null)
    {
        Target = target;
        _invalidate = invalidate;
        _stop = stop;
        _stopped = stopped;
        _daemonStopped = daemonStopped;
        _cleanupCompleted = cleanupCompleted;
        _invalidateSeries = invalidateSeries;
    }

    public void Invalidate()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _invalidate();
    }

    /// <summary>
    /// Drops cached data for one series. Leases without targeted wiring (or lazy
    /// data sources) fall back to the full invalidation.
    /// </summary>
    public void InvalidateSeries(int seriesId)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (_invalidateSeries is { } targeted)
            targeted(seriesId);
        else
            _invalidate();
    }

    internal void NotifyUnexpectedStopped()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _stopped();
    }

    internal bool DaemonHasStopped => Volatile.Read(ref _daemonHasStopped) != 0;

    internal bool CleanupHasCompleted => Volatile.Read(ref _cleanupHasCompleted) != 0;

    internal void NotifyDaemonStopped()
    {
        Volatile.Write(ref _daemonHasStopped, 1);
        if (Volatile.Read(ref _disposed) == 0)
            InvokeSafely(_daemonStopped);
    }

    internal void NotifyCleanupCompleted()
    {
        Volatile.Write(ref _cleanupHasCompleted, 1);
        if (Volatile.Read(ref _disposed) == 0)
            InvokeSafely(_cleanupCompleted);
    }

    private static void InvokeSafely(Action? callback)
    {
        try
        {
            callback?.Invoke();
        }
        catch
        {
            // A lifecycle notification must never break FUSE's stop thread.
        }
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

internal sealed class RelayMountStartException(Exception failure, RelayMountLease pendingLease)
    : Exception(failure.Message, failure)
{
    internal RelayMountLease PendingLease { get; } = pendingLease;
}

/// <summary>Internal delegate seam used by the runtime and lifecycle tests.</summary>
internal sealed class RelayMountOperations
{
    private readonly Func<RelayMountTarget, FusePluginConfiguration, IReadOnlyList<IReadOnlyList<int>>, CancellationToken, Action, Action, Action, Task<RelayMountLease>> _start;

    internal RelayMountOperations(Func<RelayMountTarget, FusePluginConfiguration, IReadOnlyList<IReadOnlyList<int>>, CancellationToken, Action, Task<RelayMountLease>> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = (target, configuration, manualOverrides, cancellationToken, stopped, daemonStopped, cleanupCompleted) =>
            start(target, configuration, manualOverrides, cancellationToken, stopped);
    }

    internal RelayMountOperations(Func<RelayMountTarget, FusePluginConfiguration, IReadOnlyList<IReadOnlyList<int>>, CancellationToken, Action, Action, Action, Task<RelayMountLease>> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    internal Task<RelayMountLease> StartAsync(
        RelayMountTarget target,
        FusePluginConfiguration configuration,
        IReadOnlyList<IReadOnlyList<int>> manualOverrides,
        CancellationToken cancellationToken,
        Action stopped) => StartAsync(target, configuration, manualOverrides, cancellationToken, stopped, null, null);

    internal Task<RelayMountLease> StartAsync(
        RelayMountTarget target,
        FusePluginConfiguration configuration,
        IReadOnlyList<IReadOnlyList<int>> manualOverrides,
        CancellationToken cancellationToken,
        Action stopped,
        Action? daemonStopped,
        Action? cleanupCompleted) => _start(target, configuration, manualOverrides, cancellationToken, stopped, daemonStopped ?? (() => { }), cleanupCompleted ?? (() => { }));
}

/// <summary>Creates runtime mounts backed by the real Relay resolver and FUSE service.</summary>
internal static class RelayMountOperationsFactory
{
    internal static RelayMountOperations Create(
        IMetadataService metadataService,
        ILoggerFactory loggerFactory,
        Shoko.Abstractions.Video.Services.IVideoService? videoService = null) =>
        new((target, configuration, manualOverrides, cancellationToken, stopped, daemonStopped, cleanupCompleted) =>
            StartAsync(metadataService, videoService, loggerFactory, target, configuration, manualOverrides, cancellationToken, stopped, daemonStopped, cleanupCompleted));

    private static async Task<RelayMountLease> StartAsync(
        IMetadataService metadataService,
        Shoko.Abstractions.Video.Services.IVideoService? videoService,
        ILoggerFactory loggerFactory,
        RelayMountTarget target,
        FusePluginConfiguration configuration,
        IReadOnlyList<IReadOnlyList<int>> manualOverrides,
        CancellationToken cancellationToken,
        Action stopped,
        Action daemonStopped,
        Action cleanupCompleted)
    {
        var dataSource = new RelayShokoPathDataSource(
            metadataService,
            new RelayPathDataSourceOptions(target.ManagedFolderId, target.ManagedFolderPath)
            {
                ManagedFolderName = target.ManagedFolderName,
                ManagedFolderType = DropFolderType.Excluded,
                TmdbEpNumbering = configuration.TmdbEpNumbering,
                MergeTmdbSeries = configuration.MergeTmdbSeries,
                PlexLocalExtras = configuration.PlexLocalExtras,
                FolderExclusions = configuration.FolderExclusions,
                ManagedFolderExclusions = configuration.ManagedFolderExclusions,
                RelayTvFolderName = configuration.RelayTvFolderName,
                RelayMovieFolderName = configuration.RelayMovieFolderName,
                SeriesTitleLanguage = configuration.SeriesTitleLanguage,
                EpisodeTitleLanguage = configuration.EpisodeTitleLanguage,
                MoveCommonSeriesTitlePrefixes = configuration.MoveCommonSeriesTitlePrefixes,
                TmdbEpGroupNames = configuration.TmdbEpGroupNames,
                ManualOverrideGroups = manualOverrides,
                SeriesCacheTtl = target.ResolverOptions.SeriesCacheTtl,
            },
            videoService
        );
        var resolver = new ShokoPathResolver(
            dataSource,
            new RelayNamingStrategy(),
            target.ResolverOptions,
            loggerFactory.CreateLogger<ShokoPathResolver>()
        );
        var service = new FuseMountService(
            new MountOptions
            {
                Name = $"Relay {target.RootName} ({target.ManagedFolderName})",
                MountPoint = target.TargetPath,
                AllowOther = configuration.FuseAllowOther,
                Uid = configuration.FuseMountUid,
                Gid = configuration.FuseMountGid,
                AttrTimeout = configuration.AttrTimeout,
                EntryTimeout = configuration.EntryTimeout,
                NegativeTimeout = configuration.NegativeTimeout,
            },
            resolver,
            loggerFactory.CreateLogger<FuseMountService>()
        );
        RelayMountLease? lease = null;
        void OnUnexpectedStopped() => lease?.NotifyUnexpectedStopped();
        void OnDaemonStoppedFromService() => lease?.NotifyDaemonStopped();
        void OnCleanupCompletedFromService() => lease?.NotifyCleanupCompleted();
        lease = new RelayMountLease(
            target,
            () => resolver.Invalidate("", includeChildren: true),
            async _ =>
            {
                await service.StopAsync().ConfigureAwait(false);
                service.Dispose();
                resolver.Stop();
                service.UnexpectedStopped -= OnUnexpectedStopped;
                service.DaemonStopped -= OnDaemonStoppedFromService;
                service.CleanupCompleted -= OnCleanupCompletedFromService;
            },
            stopped,
            daemonStopped,
            cleanupCompleted,
            seriesId => resolver.InvalidateSeries(dataSource.MapToPrimarySeriesId(seriesId)));
        service.UnexpectedStopped += OnUnexpectedStopped;
        service.DaemonStopped += OnDaemonStoppedFromService;
        service.CleanupCompleted += OnCleanupCompletedFromService;

        try
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch (Exception ex)
        {
            if (service.CleanupPending)
                throw new RelayMountStartException(ex, lease);

            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                throw new RelayMountStartException(ex, lease);
            }
            throw;
        }
    }
}
