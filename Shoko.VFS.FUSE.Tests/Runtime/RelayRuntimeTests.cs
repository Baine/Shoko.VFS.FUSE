using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using Shoko.VFS.FUSE.Api;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Tests.Runtime;

public sealed class RelayRuntimeTests
{
    [Fact]
    public async Task WaitsForStartupAndContentEventsOnlyInvalidate()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);

        Assert.Equal(RelayRuntimeState.WaitingForServer, fixture.Runtime.Status.State);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1, () => $"task={fixture.Runtime.ExecuteTask?.Status}, error={fixture.Runtime.ExecuteTask?.Exception?.GetBaseException().Message}, waitCalled={fixture.System.WaitCalled}, starts={fixture.Starts}, state={fixture.Runtime.Status.State}, desired={fixture.Runtime.Status.DesiredMountCount}, failed={fixture.Runtime.Status.FailedMountCount}, capabilities={string.Join(',', fixture.Runtime.Status.CapabilityCodes)}");

        fixture.Video.RaiseDetected();
        await Eventually(() => fixture.Invalidations == 1);
        Assert.Equal(1, fixture.Starts);
        Assert.Equal(RelayRuntimeState.Healthy, fixture.Runtime.Status.State);

        await fixture.Runtime.StopAsync(CancellationToken.None);
        Assert.Equal(RelayRuntimeState.Stopped, fixture.Runtime.Status.State);
    }

    [Fact]
    public async Task ContentEventsWithLinkedSeriesInvalidateOnlyThoseSeries()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Video.RaiseHashed(42, 43);
        await Eventually(() => fixture.SeriesInvalidations.Count == 2);
        Assert.Equal(new[] { 42, 43 }, fixture.SeriesInvalidations.OrderBy(id => id).ToArray());
        Assert.Equal(0, fixture.Invalidations);

        // Events without series context fall back to the full invalidation.
        fixture.Video.RaiseDetected();
        await Eventually(() => fixture.Invalidations == 1);

        // Hashed files without any linked series also take the full path.
        fixture.Video.RaiseHashed();
        await Eventually(() => fixture.Invalidations == 2);
        Assert.Equal(2, fixture.SeriesInvalidations.Count);
        Assert.Equal(1, fixture.Starts);

        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReconcileWithUnchangedConfigurationKeepsExistingLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        int initialLoads = fixture.Configuration.Loads;
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Configuration.Loads > initialLoads);
        await Task.Delay(100);

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        Assert.Equal(1, fixture.Runtime.Status.ActiveMountCount);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FingerprintChangingConfigurationRemountsAffectedTarget()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration
        {
            RelayEnabled = true,
            PlexLocalExtras = false,
            SeriesTitleLanguage = "SHOKO",
        });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.Current.SeriesTitleLanguage = "en";
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Starts == 2 && fixture.Stops == 1);

        Assert.Equal(1, fixture.Runtime.Status.ActiveMountCount);
        Assert.Equal(RelayMountStatusState.Mounted, Assert.Single(fixture.Runtime.Status.Mounts).State);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RemovedManagedFolderStopsOnlyItsLease()
    {
        using var fixture = new RuntimeFixture(
            new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false },
            [Folder(1, "One", "/tmp/library-one"), Folder(2, "Two", "/tmp/library-two")]
        );
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 2);

        var removed = fixture.Video.GetFolder(2);
        fixture.Video.SetFolders([Folder(1, "One", "/tmp/library-one")]);
        fixture.Video.RaiseManagedFolderRemoved(removed);
        await Eventually(() => fixture.Stops == 1 && fixture.Runtime.Status.ActiveMountCount == 1);

        Assert.Equal(2, fixture.Starts);
        Assert.Contains(fixture.Runtime.Status.Mounts, mount => mount.ManagedFolderId == 1 && mount.State == RelayMountStatusState.Mounted);
        Assert.DoesNotContain(fixture.Runtime.Status.Mounts, mount => mount.ManagedFolderId == 2);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NewTargetStartFailureKeepsPreviouslyMountedSiblingActive()
    {
        using var fixture = new RuntimeFixture(
            new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false },
            [Folder(1, "One", "/tmp/library-one")]
        )
        {
            FailFolderId = 2,
        };
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Video.SetFolders([Folder(1, "One", "/tmp/library-one"), Folder(2, "Two", "/tmp/library-two")]);
        fixture.Video.RaiseManagedFolderAdded(fixture.Video.GetFolder(2));
        await Eventually(() => fixture.Runtime.Status.FailedMountCount == 1);

        Assert.Equal(2, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        Assert.Equal(1, fixture.Runtime.Status.ActiveMountCount);
        Assert.Contains(fixture.Runtime.Status.Mounts, mount => mount.ManagedFolderId == 1 && mount.State == RelayMountStatusState.Mounted);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentEventBurstsDoNotThrowFromHandlerPath()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(fixture.Video.RaiseDetected)));

        Assert.Equal(RelayRuntimeState.Healthy, fixture.Runtime.Status.State);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationChangeReconcilesAndStopsOnlyOwnedLeases()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.Current = new FusePluginConfiguration { RelayEnabled = false };
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Stops == 1 && fixture.Runtime.Status.State == RelayRuntimeState.Disabled, () => $"waitCalled={fixture.System.WaitCalled}, starts={fixture.Starts}, stops={fixture.Stops}, state={fixture.Runtime.Status.State}, desired={fixture.Runtime.Status.DesiredMountCount}, failed={fixture.Runtime.Status.FailedMountCount}, capabilities={string.Join(',', fixture.Runtime.Status.CapabilityCodes)}");

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(1, fixture.Stops);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedStopRetainsLeaseForRetry()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.FailStops = true;
        fixture.Configuration.Current = new FusePluginConfiguration { RelayEnabled = false };
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Runtime.Status.State == RelayRuntimeState.Degraded && fixture.Runtime.Status.ActiveMountCount == 1);
        Assert.Equal(1, fixture.StopAttempts);

        fixture.FailStops = false;
        fixture.Video.RaiseTopology();
        await Eventually(() => fixture.Runtime.Status.State == RelayRuntimeState.Disabled && fixture.Runtime.Status.ActiveMountCount == 0);
        Assert.Equal(2, fixture.StopAttempts);
        Assert.Equal(1, fixture.Stops);
    }

    [Fact]
    public async Task FailedStopFollowedByDaemonExitRemovesLeaseAndIgnoreRoot()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.FailStops = true;
        fixture.Configuration.Current = new FusePluginConfiguration { RelayEnabled = false };
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Runtime.Status.ActiveMountCount == 1);

        fixture.RaiseCurrentDaemonStopped();
        await Eventually(() => fixture.Runtime.PendingLeaseCount == 0
            && fixture.Runtime.Status.ActiveMountCount == 0
            && !fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, new FileInfo("/tmp/!ShokoRelayVFS/episode.mkv")));

        Assert.Equal(0, fixture.Stops);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CanceledStartRetainsPendingLeaseUntilDaemonExit()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        fixture.PendingStart = true;
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Runtime.PendingLeaseCount == 1 && fixture.Runtime.Status.ActiveMountCount == 0);

        fixture.HoldNextStart();
        fixture.RaiseCurrentDaemonStopped();
        await Eventually(() => fixture.Runtime.PendingLeaseCount == 0
            && fixture.Runtime.Status.ActiveMountCount == 0
            && !fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, new FileInfo("/tmp/!ShokoRelayVFS/episode.mkv")));

        fixture.ReleaseStart();
        await Eventually(() => fixture.Starts == 2 && fixture.Runtime.Status.ActiveMountCount == 1,
            () => $"starts={fixture.Starts}, active={fixture.Runtime.Status.ActiveMountCount}, pending={fixture.Runtime.PendingLeaseCount}, state={fixture.Runtime.Status.State}, failed={fixture.Runtime.Status.FailedMountCount}, mounts={string.Join(';', fixture.Runtime.Status.Mounts.Select(m => m.RootKind + ":" + m.State + ":" + string.Join(',', m.ReasonCodes)))}");
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CleanupCompletionRaceDoesNotRetainUnpublishedLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        fixture.PendingStart = true;
        fixture.PreparePendingCleanupNotification();
        fixture.Runtime.BeforePendingLeasePublication = () =>
        {
            fixture.HoldNextStart();
            _ = Task.Run(() => fixture.PendingLease!.NotifyCleanupCompleted());
            fixture.PendingCleanupNotification!.Task.GetAwaiter().GetResult();
            fixture.Runtime.BeforePendingLeasePublication = null;
        };

        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await fixture.PendingCleanupNotification!.Task;
        await Eventually(() => fixture.Runtime.PendingLeaseCount == 0
            && fixture.Runtime.Status.ActiveMountCount == 0
            && !fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, new FileInfo("/tmp/!ShokoRelayVFS/episode.mkv")));

        fixture.ReleaseStart();
        await Eventually(() => fixture.Starts == 2 && fixture.Runtime.Status.ActiveMountCount == 1,
            () => $"starts={fixture.Starts}, active={fixture.Runtime.Status.ActiveMountCount}, pending={fixture.Runtime.PendingLeaseCount}, state={fixture.Runtime.Status.State}, failed={fixture.Runtime.Status.FailedMountCount}");
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IgnoreRuleTracksOnlyActiveRuntimeRoots()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        var file = new FileInfo("/tmp/!ShokoRelayVFS/episode.mkv");

        Assert.False(fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, file));
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);
        Assert.True(fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, file));

        await fixture.Runtime.StopAsync(CancellationToken.None);
        Assert.False(fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EachFuseMountOptionChangeRemountsTarget(int option)
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration
        {
            RelayEnabled = true,
            PlexLocalExtras = false,
            AttrTimeout = 2,
            EntryTimeout = 2,
            NegativeTimeout = 0.5,
        });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.Current = new FusePluginConfiguration
        {
            RelayEnabled = true,
            PlexLocalExtras = false,
            FuseAllowOther = option == 0,
            AttrTimeout = option == 1 ? 3 : 2,
            EntryTimeout = option == 2 ? 3 : 2,
            NegativeTimeout = option == 3 ? 1 : 0.5,
        };
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Starts == 2 && fixture.Stops == 1);

        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationLoadFailureRetainsHealthyLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.ThrowOnLoad = true;
        fixture.Video.RaiseTopology();
        await Eventually(() => fixture.Runtime.Status.State == RelayRuntimeState.Degraded && fixture.Runtime.Status.ActiveMountCount == 1);

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManagedFolderReadFailureRetainsHealthyLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Video.ThrowOnRead = true;
        fixture.Video.RaiseTopology();
        await Eventually(() => fixture.Runtime.Status.State == RelayRuntimeState.Degraded && fixture.Runtime.Status.ActiveMountCount == 1);

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BlockedPlanRetainsHealthyLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.Current = new FusePluginConfiguration
        {
            RelayEnabled = true,
            PlexLocalExtras = false,
            RelayTvFolderName = "",
        };
        fixture.Video.RaiseTopology();
        await Eventually(() => fixture.Runtime.Status.State == RelayRuntimeState.Degraded && fixture.Runtime.Status.ActiveMountCount == 1);

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationBeforeStartupStopsWithoutMounting()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        await Eventually(() => fixture.System.WaitCalled);
        await fixture.Runtime.StopAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Starts);
        Assert.Equal(0, fixture.Stops);
        Assert.Equal(RelayRuntimeState.Stopped, fixture.Runtime.Status.State);
    }

    [Fact]
    public async Task UnexpectedStopRemovesRootAndTriggersReconcile()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.HoldNextStart();
        fixture.RaiseUnexpectedStop();
        await Eventually(() => fixture.Runtime.Status.ActiveMountCount == 0
            && !fixture.Runtime.IgnoreRule.ShouldIgnore(fixture.Video.Folder, new FileInfo("/tmp/!ShokoRelayVFS/episode.mkv")));

        fixture.ReleaseStart();
        await Eventually(() => fixture.Starts == 2);
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await fixture.Runtime.StopAsync(stopTimeout.Token);
    }

    [Fact]
    public async Task UnexpectedStopDuringLeasePublicationCannotPublishStoppedLease()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Runtime.BeforeLeasePublication = () =>
        {
            fixture.Runtime.BeforeLeasePublication = null;
            fixture.RaiseCurrentUnexpectedStop();
        };
        fixture.Configuration.Current.SeriesTitleLanguage = "en";
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Starts == 3 && fixture.Stops == 2 && fixture.Runtime.Status.ActiveMountCount == 1);

        Assert.DoesNotContain(fixture.Runtime.Status.Mounts, mount => mount.State == RelayMountStatusState.Failed);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TargetFailureDoesNotStopHealthySibling()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration
        {
            RelayEnabled = true,
            MovieGenerationMode = MovieGenerationMode.EnabledMaintain,
        });
        fixture.FailMovies = true;
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Runtime.Status.FailedMountCount == 1, () => $"waitCalled={fixture.System.WaitCalled}, starts={fixture.Starts}, stops={fixture.Stops}, state={fixture.Runtime.Status.State}, desired={fixture.Runtime.Status.DesiredMountCount}, active={fixture.Runtime.Status.ActiveMountCount}, failed={fixture.Runtime.Status.FailedMountCount}, capabilities={string.Join(',', fixture.Runtime.Status.CapabilityCodes)}");

        Assert.Equal(1, fixture.Runtime.Status.ActiveMountCount);
        Assert.Contains(fixture.Runtime.Status.Mounts, mount => mount.State == RelayMountStatusState.Failed && mount.RootKind == RelayMountRootKind.Movie);
        Assert.Contains(fixture.Runtime.Status.Mounts, mount => mount.State == RelayMountStatusState.Mounted && mount.RootKind == RelayMountRootKind.Tv);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void HealthStatusDoesNotSerializePathsOrExceptions()
    {
        using var fixture = new RuntimeFixture(new FusePluginConfiguration { RelayEnabled = true });
        var controller = new RelayHealthController(fixture.Runtime);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.Get().Result);
        var json = JsonSerializer.Serialize(response.Value);

        Assert.DoesNotContain("/tmp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Eventually(Func<bool> condition, Func<string>? details = null)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.True(condition(), details?.Invoke());
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly ConfigurationProvider<FusePluginConfiguration> _provider;
        private readonly RelayMountOperations _operations;
        private readonly Dictionary<string, Action> _unexpectedStops = new(StringComparer.Ordinal);
        private TaskCompletionSource? _startGate;
        private int _startReleased;
        private Action? _currentStop;
        private Action? _currentDaemonStop;

        public FakeSystem System { get; } = new();
        public FakeVideo Video { get; }
        public FakeConfiguration Configuration { get; }
        public RelayRuntime Runtime { get; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int StopAttempts { get; private set; }
        public int Invalidations { get; private set; }
        public List<int> SeriesInvalidations { get; } = [];
        public bool FailMovies { get; set; }
        public bool FailStops { get; set; }
        public bool PendingStart { get; set; }
        public RelayMountLease? PendingLease { get; private set; }
        public TaskCompletionSource? PendingCleanupNotification { get; private set; }

        public void RaiseUnexpectedStop() => _unexpectedStops.Values.Single()();
        public void RaiseCurrentUnexpectedStop() => _currentStop!();
        public void RaiseCurrentDaemonStopped() => _currentDaemonStop!();
        public void PreparePendingCleanupNotification() => PendingCleanupNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNextStart()
        {
            Volatile.Write(ref _startReleased, 0);
            _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseStart()
        {
            Volatile.Write(ref _startReleased, 1);
            _startGate?.TrySetResult();
        }
        public int? FailFolderId { get; set; }

        public RuntimeFixture(FusePluginConfiguration configuration, IReadOnlyList<FolderSpec>? folders = null)
        {
            Video = new FakeVideo(folders);
            Configuration = new FakeConfiguration(configuration);
            _provider = new ConfigurationProvider<FusePluginConfiguration>(Configuration.Proxy);
            _operations = new RelayMountOperations(Start);
            Runtime = new RelayRuntime(
                System.Proxy,
                Video.Proxy,
                new FakeHashing().Proxy,
                new FakeRelocation().Proxy,
                new FakeRelease().Proxy,
                DispatchProxy.Create<IMetadataService, EmptyProxy>(),
                _provider,
                new FakePaths().Proxy,
                _operations,
                NullLogger<RelayRuntime>.Instance
            );
        }

        private async Task<RelayMountLease> Start(
            RelayMountTarget target,
            FusePluginConfiguration configuration,
            IReadOnlyList<IReadOnlyList<int>> overrides,
            CancellationToken cancellationToken,
            Action stopped,
            Action daemonStopped,
            Action cleanupCompleted)
        {
            Starts++;
            if ((FailMovies && target.RootKind == RelayMountRootKind.Movie) || FailFolderId == target.ManagedFolderId)
                throw new InvalidOperationException("test failure");
            if (_startGate is { } gate)
            {
                if (Volatile.Read(ref _startReleased) == 0)
                    await gate.Task;
                _startGate = null;
            }
            _unexpectedStops[target.TargetPath] = stopped;
            _currentStop = stopped;
            _currentDaemonStop = daemonStopped;
            var lease = new RelayMountLease(
                target,
                () => Invalidations++,
                _ =>
                {
                    StopAttempts++;
                    if (FailStops)
                        throw new InvalidOperationException("stop failure");
                    Stops++;
                    return ValueTask.CompletedTask;
                },
                stopped,
                daemonStopped,
                () =>
                {
                    PendingCleanupNotification?.TrySetResult();
                    cleanupCompleted();
                },
                seriesId =>
                {
                    lock (SeriesInvalidations)
                        SeriesInvalidations.Add(seriesId);
                });
            if (PendingStart)
            {
                PendingStart = false;
                PendingLease = lease;
                throw new RelayMountStartException(new OperationCanceledException(), lease);
            }
            return lease;
        }

        public void Dispose()
        {
            _provider.Dispose();
        }
    }

    private sealed class FakeConfiguration
    {
        public FusePluginConfiguration Current { get; set; }
        public bool ThrowOnLoad { get; set; }
        public IConfigurationService Proxy { get; }
        public int Loads { get; private set; }
        private event EventHandler<ConfigurationSavedEventArgs>? Saved;

        public FakeConfiguration(FusePluginConfiguration current)
        {
            Current = current;
            var proxy = DispatchProxy.Create<IConfigurationService, ConfigurationProxy>();
            ((ConfigurationProxy)(object)proxy).Owner = this;
            Proxy = proxy;
        }

        public void RaiseSaved() => Saved?.Invoke(this, new ConfigurationSavedEventArgs { ConfigurationInfo = null! });

        public FusePluginConfiguration Load()
        {
            Loads++;
            if (ThrowOnLoad)
                throw new InvalidOperationException("configuration failure");
            return Current;
        }

        private class ConfigurationProxy : DispatchProxy
        {
            internal FakeConfiguration Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                return method?.Name switch
                {
                    "add_Saved" => Add(args),
                    "remove_Saved" => Remove(args),
                    "GetConfigurationInfo" => null,
                    "Load" => Owner.Load(),
                    _ => Default(method?.ReturnType),
                };
            }

            private object? Add(object?[]? args)
            {
                Owner.Saved += (EventHandler<ConfigurationSavedEventArgs>)args![0]!;
                return null;
            }

            private object? Remove(object?[]? args)
            {
                Owner.Saved -= (EventHandler<ConfigurationSavedEventArgs>)args![0]!;
                return null;
            }
        }
    }

    private sealed class FakeSystem
    {
        private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISystemService Proxy { get; }
        public bool WaitCalled { get; set; }

        public FakeSystem()
        {
            var proxy = DispatchProxy.Create<ISystemService, SystemProxy>();
            ((SystemProxy)(object)proxy).Owner = this;
            ((SystemProxy)(object)proxy).Startup = _startup.Task;
            Proxy = proxy;
        }

        public void CompleteStartup() => _startup.SetResult();

        private class SystemProxy : DispatchProxy
        {
            internal FakeSystem Owner { get; set; } = null!;
            internal Task Startup { get; set; } = Task.CompletedTask;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method?.Name == "WaitForStartupAsync")
                {
                    Owner.WaitCalled = true;
                    return Startup;
                }
                return Default(method?.ReturnType);
            }
        }
    }

    private sealed class FakeVideo
    {
        private readonly List<IManagedFolderIgnoreRule> _rules = [];
        private IReadOnlyList<IManagedFolder> _folders = [];
        public bool ThrowOnRead { get; set; }
        public IVideoService Proxy { get; }
        public IManagedFolder Folder => _folders[0];
        private EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderAdded;
        private EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderUpdated;
        private EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderRemoved;

        public FakeVideo(IReadOnlyList<FolderSpec>? folders = null)
        {
            SetFolders(folders ?? [new FolderSpec(1, "Library", "/tmp", DropFolderType.Excluded)]);
            var proxy = DispatchProxy.Create<IVideoService, VideoProxy>();
            ((VideoProxy)(object)proxy).Owner = this;
            Proxy = proxy;
        }

        public void RaiseDetected() => ((VideoProxy)(object)Proxy).Detected?.Invoke(this, null!);
        public void RaiseHashed(params int[] seriesIds)
        {
            var video = DispatchProxy.Create<IVideo, HashedVideoProxy>();
            ((HashedVideoProxy)(object)video).SeriesIds = seriesIds;
            var args = new VideoFileHashedEventArgs("/hashed.mkv", Folder, DispatchProxy.Create<IVideoFile, EmptyProxy>(), video)
            {
                UsedExistingHashes = false,
                IsNewVideo = true,
                IsNewFile = true,
                Hashes = [],
            };
            Hashed?.Invoke(this, args);
        }
        public void RaiseManagedFolderAdded(IManagedFolder folder) => ManagedFolderAdded?.Invoke(this, new ManagedFolderChangedEventArgs { Folder = folder });
        public void RaiseManagedFolderRemoved(IManagedFolder folder) => ManagedFolderRemoved?.Invoke(this, new ManagedFolderChangedEventArgs { Folder = folder });
        public void RaiseTopology() => ManagedFolderUpdated?.Invoke(this, null!);

        public IManagedFolder GetFolder(int id) => _folders.Single(folder => folder.ID == id);

        public void SetFolders(IReadOnlyList<FolderSpec> folders)
        {
            _folders = folders.Select(spec =>
            {
                var folder = DispatchProxy.Create<IManagedFolder, FolderProxy>();
                ((FolderProxy)(object)folder).Owner = this;
                ((FolderProxy)(object)folder).Spec = spec;
                return folder;
            }).ToArray();
        }

        private EventHandler<VideoFileDetectedEventArgs>? Detected;
        private EventHandler<VideoFileHashedEventArgs>? Hashed;

        private class HashedVideoProxy : DispatchProxy
        {
            internal int[] SeriesIds { get; set; } = [];

            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
            {
                "get_Series" => SeriesIds.Select(id =>
                {
                    var series = DispatchProxy.Create<Shoko.Abstractions.Metadata.Shoko.IShokoSeries, SeriesIdProxy>();
                    ((SeriesIdProxy)(object)series).Id = id;
                    return (Shoko.Abstractions.Metadata.Shoko.IShokoSeries)series;
                }).ToArray(),
                _ => Default(method?.ReturnType),
            };
        }

        private class SeriesIdProxy : DispatchProxy
        {
            internal int Id { get; set; }

            protected override object? Invoke(MethodInfo? method, object?[]? args) =>
                method?.Name == "get_ID" ? Id : Default(method?.ReturnType);
        }

        private class VideoProxy : DispatchProxy
        {
            internal FakeVideo Owner { get; set; } = null!;
            internal EventHandler<VideoFileDetectedEventArgs>? Detected
            {
                get => Owner.Detected;
                set => Owner.Detected = value;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
            {
                "add_VideoFileDetected" => Add(ref Owner.Detected, args),
                "remove_VideoFileDetected" => Remove(ref Owner.Detected, args),
                "add_VideoFileHashed" => Add(ref Owner.Hashed, args),
                "remove_VideoFileHashed" => Remove(ref Owner.Hashed, args),
                "add_ManagedFolderAdded" => Add(ref Owner.ManagedFolderAdded, args),
                "remove_ManagedFolderAdded" => Remove(ref Owner.ManagedFolderAdded, args),
                "add_ManagedFolderUpdated" => Add(ref Owner.ManagedFolderUpdated, args),
                "remove_ManagedFolderUpdated" => Remove(ref Owner.ManagedFolderUpdated, args),
                "add_ManagedFolderRemoved" => Add(ref Owner.ManagedFolderRemoved, args),
                "remove_ManagedFolderRemoved" => Remove(ref Owner.ManagedFolderRemoved, args),
                "get_IgnoreRules" => Owner._rules,
                "AddParts" => AddParts(args),
                "GetAllManagedFolders" => Owner.ThrowOnRead ? throw new InvalidOperationException("folder read failure") : Owner._folders,
                _ => Default(method?.ReturnType),
            };

            private object? AddParts(object?[]? args)
            {
                if (Owner._rules.Count == 0)
                    Owner._rules.AddRange((IEnumerable<IManagedFolderIgnoreRule>)args![0]!);
                return null;
            }
        }

        private class FolderProxy : DispatchProxy
        {
            internal FakeVideo Owner { get; set; } = null!;
            internal FolderSpec Spec { get; set; } = null!;
            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
            {
                "get_ID" => Spec.Id,
                "get_Name" => Spec.Name,
                "get_Path" => Spec.Path,
                "get_DropFolderType" => Spec.DropFolderType,
                _ => Default(method?.ReturnType),
            };
        }
    }

    private sealed record FolderSpec(int Id, string Name, string Path, DropFolderType DropFolderType);

    private static FolderSpec Folder(int id, string name, string path) =>
        new(id, name, path, DropFolderType.Excluded);

    private sealed class FakeHashing
    {
        public IVideoHashingService Proxy { get; } = DispatchProxy.Create<IVideoHashingService, EmptyProxy>();
    }

    private sealed class FakeRelocation
    {
        public IVideoRelocationService Proxy { get; } = DispatchProxy.Create<IVideoRelocationService, EmptyProxy>();
    }

    private sealed class FakeRelease
    {
        public IVideoReleaseService Proxy { get; } = DispatchProxy.Create<IVideoReleaseService, EmptyProxy>();
    }

    private sealed class FakePaths
    {
        public IApplicationPaths Proxy { get; }
        public FakePaths()
        {
            var proxy = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
            ((PathsProxy)(object)proxy).Data = "/tmp";
            Proxy = proxy;
        }

        private class PathsProxy : DispatchProxy
        {
            internal string Data { get; set; } = "";
            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_DataPath" ? Data : Default(method?.ReturnType);
        }
    }

    private class EmptyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Default(method?.ReturnType);
    }

    private static object? Default(Type? type) => type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

    private static object? Add<T>(ref T? field, object?[]? args) where T : Delegate
    {
        field = (T?)Delegate.Combine(field, (T)args![0]!);
        return null;
    }

    private static object? Remove<T>(ref T? field, object?[]? args) where T : Delegate
    {
        field = (T?)Delegate.Remove(field, (T)args![0]!);
        return null;
    }
}
