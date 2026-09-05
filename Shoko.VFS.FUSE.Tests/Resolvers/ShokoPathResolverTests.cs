using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public class ShokoPathResolverTests
{
    private const string RelayTvRoot = "!ShokoRelayVFS";
    private const string RelayMovieRoot = "!ShokoRelayMovieVFS";

    [Fact]
    public void RelayTvTraversal_NormalizesSeparatorsAndResolvesSource()
    {
        var source = new MutableDataSource();
        source.Set(Series(12, "Show", false, Mapping(101, 1001)));
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());
        resolver = Ready(resolver);

        Assert.Equal(new[] { "12" }, Names(resolver.ReadDirectory("/")));
        Assert.Empty(resolver.ReadDirectory(@"\!ShokoRelayVFS\"));
        Assert.Equal(new[] { "Season 1" }, Names(resolver.ReadDirectory("12")));

        const string fileName = "S01E01 [101].mkv";
        const string filePath = @"\12\Season 1\S01E01 [101].mkv";
        var entry = resolver.Lookup(filePath);

        Assert.NotNull(entry);
        Assert.Equal(fileName, entry.Name);
        Assert.Equal(VirtualNodeType.File, entry.NodeType);
        Assert.Equal("/source/101.mkv", entry.SourcePath);
        Assert.Equal("/source/101.mkv", resolver.GetSourcePath("/12/Season 1/" + fileName));
    }

    [Fact]
    public void EnabledRemove_NonMovieSeriesRemainTvOnly()
    {
        var source = new MutableDataSource();
        source.Set(Series(1, "Show", false, Mapping(11, 11)));
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { Shows = true, MoviesAsTv = false }
        );
        resolver = Ready(resolver);

        Assert.Equal(new[] { "1" }, Names(resolver.ReadDirectory("")));
        Assert.Equal(new[] { "Season 1" }, Names(resolver.ReadDirectory("1")));
        Assert.Empty(resolver.ReadDirectory(RelayMovieRoot));
    }

    [Theory]
    [InlineData(MovieGenerationMode.Disabled, false, true)]
    [InlineData(MovieGenerationMode.EnabledMaintain, true, true)]
    [InlineData(MovieGenerationMode.EnabledRemove, true, false)]
    public void MovieGenerationMode_RoutesMovieSeriesAtPublicRoots(MovieGenerationMode mode, bool expectedMovie, bool expectedTv)
    {
        var source = new MutableDataSource();
        source.Set(Series(2, "Movie", true, Mapping(21, 500)));
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            mode switch
            {
                MovieGenerationMode.EnabledMaintain => new PathResolverOptions { Shows = true, MoviesAsTv = true, StandaloneMovies = true },
                MovieGenerationMode.EnabledRemove => new PathResolverOptions { Shows = true, MoviesAsTv = false, StandaloneMovies = true },
                _ => new PathResolverOptions { Shows = true, MoviesAsTv = true },
            }
        );
        resolver = Ready(resolver);

        var expectedChildren = mode switch
        {
            MovieGenerationMode.EnabledMaintain => new[] { "2", "500" },
            MovieGenerationMode.EnabledRemove => new[] { "500" },
            _ => new[] { "2" },
        };
        Assert.Equal(expectedChildren, Names(resolver.ReadDirectory("/")));
        Assert.Equal(expectedTv ? new[] { "Season 1" } : Array.Empty<string>(), Names(resolver.ReadDirectory("2")));
        Assert.Equal(expectedMovie ? new[] { "Movie [21].mkv" } : Array.Empty<string>(), Names(resolver.ReadDirectory("500")));
    }

    [Fact]
    public void DerivedEpisodeNames_UseMaxPaddingPeersAndSourceAwareVersions()
    {
        var source = new MutableDataSource();
        source.Set(
            Series(
                3,
                "Show",
                false,
                Mapping(20, 20, episode: 2, sourcePath: null),
                Mapping(21, 21, episode: 2),
                Mapping(22, 22, episode: 2),
                Mapping(31, 31, episode: 3, partIndex: 1, partCount: 2),
                Mapping(32, 32, episode: 3, partIndex: 2, partCount: 2),
                Mapping(40, 40, episode: 4, variation: true),
                Mapping(100, 100, episode: 100)
            )
        );
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());
        resolver = Ready(resolver);

        var files = resolver.ReadDirectory("3/Season 1");

        Assert.Equal(
            new[]
            {
                "S01E002-dup1 [21].mkv",
                "S01E002-dup2 [22].mkv",
                "S01E003-pt1.mkv",
                "S01E003-pt2.mkv",
                "S01E004 [40][variation].mkv",
                "S01E100 [100].mkv",
            },
            Names(files)
        );
        Assert.Equal("/source/21.mkv", resolver.GetSourcePath("3/Season 1/S01E002-dup1 [21].mkv"));
        Assert.Null(resolver.Lookup("3/Season 1/S01E02 [20].mkv"));
    }

    [Fact]
    public void RelayTvExtras_UseMappedNamesAndCountBasedPaddingWithoutFileIds()
    {
        var mappings = Enumerable
            .Range(1, 10)
            .Select(index => Mapping(200 + index, 200 + index, season: -2, episode: index, title: $"Trailer {index}"))
            .ToList();
        mappings.Add(Mapping(301, 301, season: -1, episode: 1, title: "Credit"));

        var source = new MutableDataSource();
        source.Set(Series(4, "Show", false, mappings.ToArray()));
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());
        resolver = Ready(resolver);

        Assert.Equal(new[] { "Trailers", "Shorts" }, Names(resolver.ReadDirectory("4")));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(index => $"T{index:D2} ❯ Trailer {index}.mkv"),
            Names(resolver.ReadDirectory("4/Trailers"))
        );
        Assert.Equal(new[] { "C1 ❯ Credit.mkv" }, Names(resolver.ReadDirectory("4/Shorts")));
    }

    [Fact]
    public void MovieExtras_AreReplicatedOrOmittedWithoutAffectingTvExtras()
    {
        var source = new MutableDataSource();
        source.Set(
            Series(
                5,
                "Movie",
                true,
                Mapping(501, 900),
                Mapping(502, 901, episode: 2),
                Mapping(503, 903, season: -4, episode: 1, isMain: false, title: "Making Of")
            )
        );
        var relay = new RelayNamingStrategy();

        var withExtras = new ShokoPathResolver(
            source,
            relay,
            new PathResolverOptions { Shows = false, MoviesAsTv = false, StandaloneMovies = true, IncludeMovieExtras = true }
        );
        withExtras = Ready(withExtras);
        Assert.Equal(new[] { "Movie [501].mkv", "Featurettes" }, Names(withExtras.ReadDirectory("900")));
        Assert.Equal(new[] { "Movie [502].mkv", "Featurettes" }, Names(withExtras.ReadDirectory("901")));
        Assert.Equal(
            new[] { "O1 ❯ Making Of.mkv" },
            Names(withExtras.ReadDirectory("900/Featurettes"))
        );
        Assert.Equal("/source/503.mkv", withExtras.GetSourcePath("901/Featurettes/O1 ❯ Making Of.mkv"));

        var withoutExtras = new ShokoPathResolver(
            source,
            relay,
            new PathResolverOptions { Shows = false, MoviesAsTv = false, StandaloneMovies = true, IncludeMovieExtras = false }
        );
        withoutExtras = Ready(withoutExtras);
        Assert.Equal(new[] { "Movie [501].mkv" }, Names(withoutExtras.ReadDirectory("900")));
        Assert.Equal(Array.Empty<string>(), Names(withoutExtras.ReadDirectory("5/Featurettes")));
    }

    [Fact]
    public void ExtrasAlone_DoNotCreateMovieFolders()
    {
        var source = new MutableDataSource();
        source.Set(Series(6, "Extra Only", true, Mapping(601, 601, season: -1, isMain: false, title: "Credit")));
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { Shows = false, MoviesAsTv = false, StandaloneMovies = true }
        );
        resolver = Ready(resolver);

        Assert.Empty(resolver.ReadDirectory(""));
        Assert.Empty(resolver.ReadDirectory("601"));
    }

    [Fact]
    public void MappingsWithoutSourcePath_AreAbsent()
    {
        var source = new MutableDataSource();
        source.Set(Series(7, "Missing", false, Mapping(701, 701, sourcePath: null)));
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());
        resolver = Ready(resolver);

        Assert.Empty(resolver.ReadDirectory(""));
        Assert.Empty(resolver.ReadDirectory("7/Season 1"));
        Assert.Null(resolver.Lookup("7/Season 1/S01E01 [701].mkv"));
        Assert.Null(resolver.GetSourcePath("7/Season 1/S01E01 [701].mkv"));
    }

    [Fact]
    public void LongTtl_RequiresInvalidationOrRebuildToObserveChangedData()
    {
        var source = new MutableDataSource();
        source.Set(Series(8, "Show", false, Mapping(801, 801, episode: 1)));
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { CacheTtl = TimeSpan.FromHours(1) }
        );
        resolver = Ready(resolver);
        const string oldPath = "8/Season 1/S01E01 [801].mkv";
        const string secondPath = "8/Season 1/S01E02 [802].mkv";
        const string thirdPath = "8/Season 1/S01E03 [803].mkv";

        Assert.Equal("/source/801.mkv", resolver.GetSourcePath(oldPath));
        source.Set(Series(8, "Show", false, Mapping(802, 802, episode: 2)));
        Assert.Null(resolver.Lookup(secondPath));

        resolver.Invalidate(oldPath);
        Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath(secondPath) == "/source/802.mkv", TimeSpan.FromSeconds(5)));

        source.Set(Series(8, "Show", false, Mapping(803, 803, episode: 3)));
        resolver.Rebuild();
        Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath(thirdPath) == "/source/803.mkv", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ZeroTtl_ObservesChangedDataOnNextResolve()
    {
        var source = new MutableDataSource();
        source.Set(Series(9, "Show", false, Mapping(901, 901, episode: 1)));
        var resolver = new ShokoPathResolver(
            source,
            new RelayNamingStrategy(),
            new PathResolverOptions { CacheTtl = TimeSpan.Zero }
        );
        resolver = Ready(resolver);
        const string oldPath = "9/Season 1/S01E01 [901].mkv";
        const string newPath = "9/Season 1/S01E02 [902].mkv";

        Assert.Equal("/source/901.mkv", resolver.GetSourcePath(oldPath));
        source.Set(Series(9, "Show", false, Mapping(902, 902, episode: 2)));

        Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath(newPath) == "/source/902.mkv", TimeSpan.FromSeconds(5)));
        Assert.Null(resolver.Lookup(oldPath));
    }

    [Fact]
    public void FinResolver_UsesFlatSeriesRootAndFinNames()
    {
        var source = new MutableDataSource();
        source.Set(Series(10, "Fin Show", false, Mapping(1001, 1001, season: 2, episode: 3)));
        var resolver = new ShokoPathResolver(source, new FinNamingStrategy());
        resolver = Ready(resolver);

        const string seriesFolder = "Fin Show [ShokoSeries=10]";
        const string fileName = "Fin Show S02E03 [ShokoFile=1001].mkv";
        const string filePath = seriesFolder + "/Season 02/" + fileName;

        Assert.Equal(new[] { seriesFolder }, Names(resolver.ReadDirectory("/")));
        Assert.Empty(resolver.ReadDirectory(RelayTvRoot));
        Assert.Equal(new[] { "Season 02" }, Names(resolver.ReadDirectory(seriesFolder)));
        Assert.Equal(new[] { fileName }, Names(resolver.ReadDirectory(seriesFolder + "/Season 02")));
        Assert.Equal("/source/1001.mkv", resolver.Lookup(filePath)?.SourcePath);
        Assert.Equal("/source/1001.mkv", resolver.GetSourcePath(filePath));
    }

    [Fact]
    public void ReadDirectoryAndLookup_UseDeterministicDirectChildSemantics()
    {
        var source = new MutableDataSource();
        source.Set(Series(11, "Show", false, Mapping(1101, 1101)));
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy());
        resolver = Ready(resolver);

        Assert.Empty(resolver.ReadDirectory("unknown"));
        Assert.Empty(resolver.ReadDirectory($"{RelayTvRoot}/11/Season 1/S01E01 [1101].mkv"));
        Assert.Null(resolver.Lookup($"{RelayTvRoot}/11/Season 1/missing.mkv"));
        Assert.Null(resolver.Lookup($"{RelayTvRoot}/11/Season 1/S01E01 [missing].mkv"));
    }

    private static string[] Names(IEnumerable<VirtualEntry> entries) => entries.Select(entry => entry.Name).ToArray();

    private static ShokoPathResolver Ready(ShokoPathResolver resolver)
    {
        resolver.ReadDirectory("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        return resolver;
    }

    private static SeriesData Series(int id, string? title, bool isMovie, params EpisodeData[] mappings) => new(id, title, isMovie, mappings);

    private static EpisodeData Mapping(
        int fileId,
        int episodeId,
        int season = 1,
        int episode = 1,
        int? endEpisode = null,
        int? partIndex = null,
        int? partCount = null,
        bool variation = false,
        bool isMain = true,
        string? title = null,
        string? sourcePath = "auto",
        long size = 0,
        string extension = ".mkv"
    ) => new(
        fileId,
        episodeId,
        season,
        episode,
        endEpisode,
        partIndex,
        partCount,
        variation,
        isMain,
        title,
        sourcePath == "auto" ? $"/source/{fileId}{extension}" : sourcePath,
        size,
        extension
    );

    private sealed class MutableDataSource : IShokoPathDataSource
    {
        private IReadOnlyList<SeriesData> _series = Array.Empty<SeriesData>();

        public IReadOnlyList<SeriesData> GetAllSeries() => _series;

        public void Set(params SeriesData[] series) => _series = series;
    }
}
