using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;
using Shoko.VFS.FUSE.Runtime;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Host.Config;
using Shoko.VFS.FUSE.Host.Api;
using Microsoft.Extensions.Logging;

namespace Shoko.VFS.FUSE.Host.Daemon;

/// <summary>
/// Creates FUSE leases for a single relay mount target.
/// Mirrors the plugin's <c>RelayMountOperationsFactory</c> but uses the host's
/// <c>ShokoRelayDataSource</c> instead of the plugin's <c>RelayShokoPathDataSource</c>.
/// </summary>
public static class DaemonMounter
{
    /// <summary>
    /// Builds the Relay path data source (REST aggregation client) for a mount target.
    /// Shared by <see cref="StartLeaseAsync"/> and the orchestrator's warmup, which
    /// aggregates without mounting.
    /// </summary>
    public static ShokoRelayDataSource CreateDataSource(
        RelayMountTarget target,
        HostConfig config,
        ShokoRestClient client,
        FileSnapshotStore? snapshotStore = null)
    {
        var relayOptions = new RelayPathDataSourceOptions(target.ManagedFolderId, target.ManagedFolderPath)
        {
            ManagedFolderName = target.ManagedFolderName,
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Excluded,
            TmdbEpNumbering = config.TmdbEpNumbering,
            MergeTmdbSeries = config.MergeTmdbSeries,
            PlexLocalExtras = config.PlexLocalExtras,
            FolderExclusions = config.FolderExclusions,
            ManagedFolderExclusions = config.ManagedFolderExclusions,
            RelayTvFolderName = config.RelayTvFolderName,
            RelayMovieFolderName = config.RelayMovieFolderName,
            SeriesTitleLanguage = config.SeriesTitleLanguage,
            EpisodeTitleLanguage = config.EpisodeTitleLanguage,
            MoveCommonSeriesTitlePrefixes = config.MoveCommonSeriesTitlePrefixes,
            TmdbEpGroupNames = config.TmdbEpGroupNames,
            ManualOverrideGroups = [],
        };

        var snapshotKey = snapshotStore is null
            ? null
            : $"{target.ManagedFolderId}_{target.RootKind}";

        return new ShokoRelayDataSource(client, relayOptions,
            cacheTtl: config.AggregationCacheTtl,
            maxDegree: config.AggregationFetchDegree,
            snapshotStore: snapshotStore,
            snapshotKey: snapshotKey);
    }

    /// <summary>
    /// Creates and starts a lease for the given <c>RelayMountTarget</c>.
    /// Returns a <c>DaemonLease</c> on success, throws <c>FuseStartException</c> on failure.
    /// </summary>
    /// <param name="target">Mount target (paths already host-mapped by the orchestrator).</param>
    /// <param name="config">Host configuration.</param>
    /// <param name="client">Authenticated Shoko REST client (shared by all mounts).</param>
    /// <param name="snapshotStore">Optional persistent snapshot store (one file per mount).</param>
    /// <param name="ct">Cancels between mount steps; also flows into source-path validation.</param>
    public static async Task<DaemonLease> StartLeaseAsync(
        RelayMountTarget target,
        HostConfig config,
        ShokoRestClient client,
        ILoggerFactory loggerFactory,
        Action? onUnexpectedStopped = null,
        CancellationToken ct = default,
        FileSnapshotStore? snapshotStore = null)
    {
        var logger = loggerFactory.CreateLogger("Shoko.VFS.FUSE.Host.Daemon.DaemonMounter");

        // 1. Build the Relay path data source (reads data from the server via REST).
        var dataSource = CreateDataSource(target, config, client, snapshotStore);

        // If the previous run shut down cleanly, prime the cache with the persisted
        // snapshot so the FUSE mount responds immediately while the orchestrator does
        // its first live reconcile.
        if (snapshotStore is not null)
        {
            if (dataSource.WasLastShutdownClean() && dataSource.TryLoadFromStore())
                logger.LogInformation("Loaded cached snapshot for {Root} ({Folder}).",
                    target.RootName, target.ManagedFolderName);
            else
            {
                logger.LogInformation(
                    "No clean-shutdown snapshot for {Root} ({Folder}); cache will rebuild from server.",
                    target.RootName, target.ManagedFolderName);
                dataSource.InvalidateStored();
            }
        }

        // 2. Build the path resolver (produces the virtual filesystem tree).
        var resolverOptions = target.ResolverOptions;
        var resolver = new ShokoPathResolver(
            dataSource,
            new RelayNamingStrategy(),
            resolverOptions,
            loggerFactory.CreateLogger<ShokoPathResolver>());

        // 3. Build the FUSE mount service.
        var mountOptions = new MountOptions
        {
            Name = $"Relay {target.RootName} ({target.ManagedFolderName})",
            MountPoint = target.TargetPath,
            AllowOther = config.FuseAllowOther,
            Uid = config.FuseMountUid,
            Gid = config.FuseMountGid,
            AttrTimeout = config.AttrTimeout,
            EntryTimeout = config.EntryTimeout,
            NegativeTimeout = config.NegativeTimeout,
        };

        var service = new FuseMountService(
            mountOptions,
            resolver,
            loggerFactory.CreateLogger<FuseMountService>());

        // 4. Create the lease and wire service events.
        var lease = new DaemonLease(target, service, resolver, dataSource,
            loggerFactory.CreateLogger<DaemonLease>(), onUnexpectedStopped);

        try
        {
            await lease.StartAsync(ct).ConfigureAwait(false);
            return lease;
        }
        catch (FuseStartException)
        {
            // On mount failure, clean up resources.
            try { await lease.StopAsync().ConfigureAwait(false); } catch { }
            throw;
        }
    }
}