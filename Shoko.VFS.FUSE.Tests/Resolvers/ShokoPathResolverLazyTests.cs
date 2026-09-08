using System.Collections.Concurrent;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public class ShokoPathResolverLazyTests
{
    [Fact]
    public void LazySource_PublishesStructureBeforeAnyMaterialization()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234), StructureSeries(5678));
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));

        Assert.Equal(new[] { "1234", "5678" }, Names(resolver.ReadDirectory("")));
        Assert.Equal(0, source.TotalFetches);
        // Series folder itself is served from the structure tree (no fetch for getattr).
        Assert.NotNull(resolver.Lookup("1234"));
        Assert.Equal(0, source.TotalFetches);
    }

    [Fact]
    public void FirstAccess_MaterializesOnce_ServesSeasonsAndExtras()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234));
        source.SetFullData(FullSeries(1234, extraTheme: true));
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));

        Assert.Equal(new[] { "Theme.mp3", "Season 1" }, Names(resolver.ReadDirectory("1234")));
        Assert.Equal(1, source.FetchCount(1234));

        var file = resolver.Lookup("1234/Season 1/S01E01 [101].mkv");
        Assert.NotNull(file);
        Assert.Equal("/source/101.mkv", file.SourcePath);
        Assert.Equal(1, source.FetchCount(1234));

        Assert.Equal(new[] { "Theme.mp3", "Season 1" }, Names(resolver.ReadDirectory("1234")));
        Assert.Equal(1, source.FetchCount(1234));
    }

    [Fact]
    public void StaleSubtree_RefetchesOnNextAccess()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234));
        source.SetFullData(FullSeries(1234));
        var resolver = Ready(new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { CacheTtl = TimeSpan.FromHours(1), SeriesCacheTtl = TimeSpan.FromMilliseconds(10) }
        ));

        Assert.Single(resolver.ReadDirectory("1234/Season 1"));
        Assert.Equal(1, source.FetchCount(1234));

        Thread.Sleep(60);
        source.SetFullData(FullSeries(1234, mappingCount: 2));

        Assert.Equal(
            new[] { "S01E01 [101].mkv", "S01E02 [102].mkv" },
            Names(resolver.ReadDirectory("1234/Season 1"))
        );
        Assert.Equal(2, source.FetchCount(1234));
    }

    [Fact]
    public void InvalidateSeries_ForcesRefetch_ForTheOneSeries()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234), StructureSeries(5678));
        source.SetFullData(FullSeries(1234), FullSeries(5678));
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));
        resolver.ReadDirectory("1234");
        resolver.ReadDirectory("5678");
        Assert.Equal(1, source.FetchCount(1234));
        Assert.Equal(1, source.FetchCount(5678));

        resolver.InvalidateSeries(1234);
        Assert.Contains(1234, source.InvalidatedSeries);

        resolver.ReadDirectory("1234");
        Assert.Equal(2, source.FetchCount(1234));
        Assert.Equal(1, source.FetchCount(5678));
    }

    [Fact]
    public void ResolverInvalidate_DropsAllSubtrees_AndNotifiesDataSource()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234), StructureSeries(5678));
        source.SetFullData(FullSeries(1234), FullSeries(5678));
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));
        resolver.ReadDirectory("1234");
        resolver.ReadDirectory("5678");

        resolver.Invalidate("");
        Assert.Equal(1, source.InvalidateAllCount);

        resolver.ReadDirectory("1234");
        resolver.ReadDirectory("5678");
        Assert.Equal(2, source.FetchCount(1234));
        Assert.Equal(2, source.FetchCount(5678));
    }

    [Fact]
    public void MovieAndTvMixedLayout_MapsMovieFolderToItsSeries()
    {
        var source = new LazyDataSource();
        // Movie series structure keeps its (partial) mappings so the episode-ID
        // folder is listed and mapped; the TV series ships with empty mappings.
        var movieStructure = new SeriesData(5, "Film", true, [Mapping(21, 900)]);
        source.SetStructure(StructureSeries(1234), movieStructure);
        source.SetFullData(FullSeries(1234), new SeriesData(5, "Film", true, [Mapping(21, 900)], [new ExtraFile("Theme.mp3", "/themes/film.mp3", 3)]));
        var resolver = Ready(new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { Shows = true, MoviesAsTv = false, StandaloneMovies = true }
        ));

        Assert.Equal(new[] { "1234", "900" }, Names(resolver.ReadDirectory("")));
        Assert.Equal(new[] { "Theme.mp3", "Movie [21].mkv" }, Names(resolver.ReadDirectory("900")));
        Assert.Equal(1, source.FetchCount(5));
        Assert.Equal(0, source.FetchCount(1234));

        Assert.Equal("/source/21.mkv", resolver.GetSourcePath("900/Movie [21].mkv"));
        Assert.Equal(1, source.FetchCount(5));
    }

    [Fact]
    public void GetSeriesDataReturningNull_FallsBackToStructureTree()
    {
        var source = new LazyDataSource();
        source.SetStructure(StructureSeries(1234));
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));

        Assert.Empty(resolver.ReadDirectory("1234"));
        Assert.Equal(1, source.FetchCount(1234));
        Assert.NotNull(resolver.Lookup("1234"));
    }

    [Fact]
    public void EagerDataSource_KeepsExistingBehavior()
    {
        // Same source class without ILazyShokoPathDataSource → eager path only.
        var source = new EagerDataSource([FullSeries(1234)]);
        var resolver = Ready(new ShokoPathResolver(source, new RelayNamingStrategy()));

        Assert.Equal(new[] { "1234" }, Names(resolver.ReadDirectory("")));
        Assert.Equal(new[] { "Season 1" }, Names(resolver.ReadDirectory("1234")));
        Assert.Equal("/source/101.mkv", resolver.GetSourcePath("1234/Season 1/S01E01 [101].mkv"));
    }

    private static string[] Names(IEnumerable<VirtualEntry> entries) => entries.Select(entry => entry.Name).ToArray();

    private static ShokoPathResolver Ready(ShokoPathResolver resolver)
    {
        resolver.ReadDirectory("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        return resolver;
    }

    private static SeriesData StructureSeries(int id) => new(id, "Show", false, Array.Empty<EpisodeData>());

    private static SeriesData FullSeries(int id, int mappingCount = 1, bool extraTheme = false) => new(
        id,
        "Show",
        false,
        Enumerable.Range(1, mappingCount).Select(index => Mapping(100 + index, 1000 + index, episode: index)).ToArray(),
        extraTheme ? [new ExtraFile("Theme.mp3", $"/themes/{id}.mp3", 1)] : null);

    private static EpisodeData Mapping(int fileId, int episodeId, int season = 1, int episode = 1) => new(
        fileId,
        episodeId,
        season,
        episode,
        null,
        null,
        null,
        false,
        true,
        null,
        $"/source/{fileId}.mkv",
        0,
        ".mkv");

    private sealed class EagerDataSource(IReadOnlyList<SeriesData> series) : IShokoPathDataSource
    {
        public IReadOnlyList<SeriesData> GetAllSeries() => series;
    }

    private sealed class LazyDataSource : IShokoPathDataSource, ILazyShokoPathDataSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, SeriesData> _full = new();
        private readonly ConcurrentDictionary<int, int> _fetches = new();
        private IReadOnlyList<SeriesData> _structure = Array.Empty<SeriesData>();

        public List<int> InvalidatedSeries { get; } = [];

        public int InvalidateAllCount { get; private set; }

        public int TotalFetches => _fetches.Values.Sum();

        public int FetchCount(int seriesId) => _fetches.GetValueOrDefault(seriesId);

        public IReadOnlyList<SeriesData> GetAllSeries() => _structure;

        public IReadOnlyList<SeriesData> GetSeriesStructure() => _structure;

        public SeriesData? GetSeriesData(int seriesId)
        {
            _fetches.AddOrUpdate(seriesId, 1, static (_, count) => count + 1);
            lock (_gate)
                return _full.GetValueOrDefault(seriesId);
        }

        public void Invalidate(int? seriesId)
        {
            if (seriesId is int id)
            {
                lock (_gate)
                    _full.Remove(id);
                InvalidatedSeries.Add(id);
            }
            else
            {
                lock (_gate)
                    _full.Clear();
                InvalidateAllCount++;
            }
        }

        public void SetStructure(params SeriesData[] series) => _structure = series;

        public void SetFullData(params SeriesData[] series)
        {
            lock (_gate)
            {
                // Keep the cached data so tests that only count fetches still resolve;
                // Invalidate() intentionally drops it, refilling via SetFullData.
                foreach (var item in series)
                    _full[item.SeriesId] = item;
            }
        }
    }
}
