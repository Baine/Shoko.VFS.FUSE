using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Fuse;

/// <summary>Walks a Relay resolver through a real FUSE mount using real source files.</summary>
[Collection("FUSE integration")]
public sealed class ShokoPathResolverMountTests : IAsyncLifetime
{
    private string _tmpRoot = "";
    private string _mountPoint = "";
    private string _episodeOne = "";
    private string _episodeTwo = "";
    private string _movie = "";
    private string _trailer = "";

    public async Task InitializeAsync()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "shoko-vfs-mount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpRoot);
        _mountPoint = Path.Combine(_tmpRoot, "mnt");
        _episodeOne = Path.Combine(_tmpRoot, "s1e1.mkv");
        _episodeTwo = Path.Combine(_tmpRoot, "s1e2.mkv");
        _movie = Path.Combine(_tmpRoot, "movie.mkv");
        _trailer = Path.Combine(_tmpRoot, "trailer.mkv");

        await File.WriteAllTextAsync(_episodeOne, "ep1-content");
        await File.WriteAllTextAsync(_episodeTwo, "ep2-content");
        await File.WriteAllTextAsync(_movie, "movie-content");
        await File.WriteAllTextAsync(_trailer, "trailer-content");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tmpRoot))
                Directory.Delete(_tmpRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a failed mount may still hold the directory open.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RelayResolver_MountWalkthrough_ReadsTvMovieAndExtras()
    {
        var (canMount, _) = await MountIntegrationTests.CanMountFuse();
        if (!canMount)
            return;

        var source = new MutableDataSource();
        source.Set(
            new SeriesData(
                111,
                "TV Show",
                false,
                [
                    new EpisodeData(11101, 1111, 1, 1, null, null, null, false, true, null, _episodeOne, new FileInfo(_episodeOne).Length, ".mkv"),
                    new EpisodeData(11102, 1112, 1, 2, null, null, null, false, true, null, _episodeTwo, new FileInfo(_episodeTwo).Length, ".mkv"),
                ]
            ),
            new SeriesData(
                222,
                "Movie Show",
                true,
                [
                    new EpisodeData(22201, 222, 1, 1, null, null, null, false, true, null, _movie, new FileInfo(_movie).Length, ".mkv"),
                    new EpisodeData(22202, 2222, -2, 1, null, null, null, false, false, "Trailer", _trailer, new FileInfo(_trailer).Length, ".mkv"),
                ]
            )
        );

        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { Shows = true, MoviesAsTv = true, StandaloneMovies = false }
        );
        var options = new MountOptions
        {
            Name = "shoko-resolver-mount",
            MountPoint = _mountPoint,
        };
        var service = new FuseMountService(options, resolver, NullLogger.Instance);

        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));

            var roots = Directory.GetDirectories(_mountPoint).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "111", "222" }, roots);

            var tvSeries = Path.Combine(_mountPoint, "111");
            var tvSeason = Path.Combine(tvSeries, "Season 1");
            Assert.Equal(new[] { "Season 1" }, Directory.GetDirectories(tvSeries).Select(Path.GetFileName));

            var tvFiles = Directory.GetFiles(tvSeason).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "S01E01 [11101].mkv", "S01E02 [11102].mkv" }, tvFiles);
            Assert.Equal(new FileInfo(_episodeOne).Length, new FileInfo(Path.Combine(tvSeason, "S01E01 [11101].mkv")).Length);
            Assert.Equal("ep1-content", File.ReadAllText(Path.Combine(tvSeason, "S01E01 [11101].mkv")));

            var movieSeries = Path.Combine(_mountPoint, "222");
            Assert.Equal("movie-content", File.ReadAllText(Path.Combine(movieSeries, "Season 1", "S01E01 [22201].mkv")));

            var trailerFolder = Path.Combine(movieSeries, "Trailers");
            // ShokoRelay uses one-digit extra padding until a bucket has more than nine mappings.
            Assert.Equal(new[] { "T1 ❯ Trailer.mkv" }, Directory.GetFiles(trailerFolder).Select(Path.GetFileName));
            Assert.Equal("trailer-content", File.ReadAllText(Path.Combine(trailerFolder, "T1 ❯ Trailer.mkv")));

            Assert.False(File.Exists(Path.Combine(tvSeason, "unknown-deep-path", "missing.mkv")));
        }
        finally
        {
            await service.StopAsync();
        }

        Assert.False(service.IsRunning);
    }

    private sealed class MutableDataSource : IShokoPathDataSource
    {
        private IReadOnlyList<SeriesData> _series = Array.Empty<SeriesData>();

        public IReadOnlyList<SeriesData> GetAllSeries() => _series;

        public void Set(params SeriesData[] series) => _series = series;
    }
}

[CollectionDefinition("FUSE integration", DisableParallelization = true)]
public sealed class FuseIntegrationCollectionDefinition;
