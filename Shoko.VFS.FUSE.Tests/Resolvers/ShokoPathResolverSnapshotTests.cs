using System.Diagnostics;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public sealed class ShokoPathResolverSnapshotTests
{
    [Fact]
    public void InitialRefreshStartsWithoutAResolverCallback()
    {
        var source = new ControlledDataSource(Series(1, 101));
        source.BlockNextBuild();
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());

        try
        {
            Assert.True(source.BuildStarted!.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(resolver.HasSnapshot);
            source.ReleaseBuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            source.ReleaseBuild();
            resolver.Stop();
        }
    }

    [Fact]
    public void ColdCallbacksReturnWithoutWaitingForDatasource()
    {
        var source = new ControlledDataSource(Series(1, 101));
        source.BlockNextBuild();
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());

        try
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.Empty(resolver.ReadDirectory(""));
            Assert.Null(resolver.Lookup("1"));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
            Assert.True(source.BuildStarted!.Wait(TimeSpan.FromSeconds(2)));

            source.ReleaseBuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
            Assert.Equal("/source/101.mkv", resolver.GetSourcePath("1/Season 1/S01E01 [101].mkv"));
        }
        finally
        {
            source.ReleaseBuild();
            resolver.Stop();
        }
    }

    [Fact]
    public void ForcedRefreshRetainsOldSnapshotUntilNewOneIsPublished()
    {
        var source = new ControlledDataSource(Series(1, 101));
        var resolver = NewReadyResolver(source);
        const string oldPath = "1/Season 1/S01E01 [101].mkv";
        const string newPath = "1/Season 1/S01E02 [102].mkv";

        try
        {
            source.Set(Series(1, 102, episode: 2));
            source.BlockNextBuild();
            resolver.Rebuild();
            Assert.True(source.BuildStarted!.Wait(TimeSpan.FromSeconds(2)));

            Assert.Equal("/source/101.mkv", resolver.GetSourcePath(oldPath));
            Assert.Null(resolver.GetSourcePath(newPath));

            source.ReleaseBuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath(newPath) == "/source/102.mkv", TimeSpan.FromSeconds(5)));
        }
        finally
        {
            source.ReleaseBuild();
            resolver.Stop();
        }
    }

    [Fact]
    public async Task ConcurrentReadsDuringRefreshSeeOneCompleteSnapshot()
    {
        var source = new ControlledDataSource(Series(1, 101));
        var resolver = NewReadyResolver(source);

        try
        {
            source.Set(Series(1, 102, episode: 2));
            source.BlockNextBuild();
            resolver.Invalidate("1", includeChildren: true);
            Assert.True(source.BuildStarted!.Wait(TimeSpan.FromSeconds(2)));

            var reads = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => resolver.ReadDirectory("1/Season 1").Select(entry => entry.Name).ToArray()))
                .ToArray();
            var results = await Task.WhenAll(reads);

            Assert.All(results, read => Assert.Equal(new[] { "S01E01 [101].mkv" }, read));
        }
        finally
        {
            source.ReleaseBuild();
            resolver.Stop();
        }
    }

    [Fact]
    public void FailedRefreshKeepsOldSnapshotAndForcedRetryRecovers()
    {
        var source = new ControlledDataSource(Series(1, 101));
        var resolver = NewReadyResolver(source);
        const string oldPath = "1/Season 1/S01E01 [101].mkv";
        const string newPath = "1/Season 1/S01E02 [102].mkv";

        try
        {
            source.Set(Series(1, 102, episode: 2));
            source.FailNextBuild();
            resolver.Rebuild();
            Assert.True(SpinWait.SpinUntil(() => source.BuildCalls >= 2, TimeSpan.FromSeconds(5)));
            Assert.Equal("/source/101.mkv", resolver.GetSourcePath(oldPath));
            Assert.Null(resolver.GetSourcePath(newPath));

            resolver.Rebuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath(newPath) == "/source/102.mkv", TimeSpan.FromSeconds(5)));
        }
        finally
        {
            resolver.Stop();
        }
    }

    [Fact]
    public void EmptySnapshotIsNotRebuiltOnEveryLookup()
    {
        var source = new ControlledDataSource();
        var resolver = NewReadyResolver(source);

        try
        {
            int calls = source.BuildCalls;
            Assert.Empty(resolver.ReadDirectory(""));
            Assert.Null(resolver.Lookup("missing"));
            Thread.Sleep(50);
            Assert.Equal(calls, source.BuildCalls);
        }
        finally
        {
            resolver.Stop();
        }
    }

    private static ShokoPathResolver NewReadyResolver(ControlledDataSource source)
    {
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { CacheTtl = TimeSpan.FromHours(1) }
        );
        resolver.Lookup("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        return resolver;
    }

    private static SeriesData Series(int seriesId, int fileId, int episode = 1) =>
        new(
            seriesId,
            "Show",
            false,
            [new EpisodeData(fileId, fileId, 1, episode, null, null, null, false, true, null, $"/source/{fileId}.mkv", 0, ".mkv")]
        );

    private sealed class ControlledDataSource(params SeriesData[] initialSeries) : IShokoPathDataSource
    {
        private IReadOnlyList<SeriesData> _series = initialSeries;
        private int _buildCalls;
        private int _blockNextBuild;
        private int _failNextBuild;
        private ManualResetEventSlim? _buildStarted;
        private ManualResetEventSlim? _releaseBuild;

        public int BuildCalls => Volatile.Read(ref _buildCalls);
        public ManualResetEventSlim? BuildStarted => _buildStarted;

        public IReadOnlyList<SeriesData> GetAllSeries()
        {
            Interlocked.Increment(ref _buildCalls);
            if (Interlocked.Exchange(ref _blockNextBuild, 0) != 0)
            {
                _buildStarted!.Set();
                _releaseBuild!.Wait();
            }

            if (Interlocked.Exchange(ref _failNextBuild, 0) != 0)
                throw new InvalidOperationException("test failure");

            return Volatile.Read(ref _series);
        }

        public void Set(params SeriesData[] series) => Volatile.Write(ref _series, series);

        public void BlockNextBuild()
        {
            _buildStarted = new ManualResetEventSlim();
            _releaseBuild = new ManualResetEventSlim();
            Volatile.Write(ref _blockNextBuild, 1);
        }

        public void ReleaseBuild() => _releaseBuild?.Set();

        public void FailNextBuild() => Volatile.Write(ref _failNextBuild, 1);
    }
}
