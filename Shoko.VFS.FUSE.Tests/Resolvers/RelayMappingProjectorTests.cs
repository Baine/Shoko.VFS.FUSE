using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public sealed class RelayMappingProjectorTests
{
    [Fact]
    public void ProjectsNormalExplicitAndAllRelayExtraTypes()
    {
        var normal = Video(1, "/source/normal.mkv") with { Size = 1234 };
        var explicitSeason = Video(2, "/source/explicit.mkv");
        var specials = Video(3, "/source/special.mkv");
        var credits = Video(4, "/source/credits.mkv");
        var trailer = Video(5, "/source/trailer.mkv");
        var parody = Video(6, "/source/parody.mkv");
        var featurette = Video(7, "/source/featurette.mkv");
        var unknown = Video(8, "/source/unknown.mkv");
        var data = Project(
            new RelayRawSeries(
                10,
                AnimeType.TV,
                "Show",
                [
                    Episode(101, EpisodeType.Episode, 1, null, "Episode 1", normal),
                    Episode(102, EpisodeType.Episode, 3, 2, "Episode 3", explicitSeason),
                    Episode(103, EpisodeType.Special, 1, 0, "Special", specials),
                    Episode(104, EpisodeType.Credits, 1, null, "Credits", credits),
                    Episode(105, EpisodeType.Trailer, 1, null, "Trailer", trailer),
                    Episode(106, EpisodeType.Parody, 1, null, "Parody", parody),
                    Episode(107, EpisodeType.Other, 1, null, "Featurette", featurette),
                    Episode(108, (EpisodeType)0, 1, null, "Other", unknown),
                ]
            )
        );
        var resolver = Resolver(data);

        Assert.Equal(new[] { "10" }, Names(resolver.ReadDirectory("")));
        Assert.Equal(new[] { "Other", "Featurettes", "Scenes", "Trailers", "Shorts", "Specials", "Season 1", "Season 2" }, Names(resolver.ReadDirectory("10")));
        Assert.Equal(new[] { "S01E01 [1].mkv" }, Names(resolver.ReadDirectory("10/Season 1")));
        Assert.Equal(new[] { "S02E03 [2].mkv" }, Names(resolver.ReadDirectory("10/Season 2")));
        Assert.Equal(new[] { "S00E01 [3].mkv" }, Names(resolver.ReadDirectory("10/Specials")));
        Assert.Equal(new[] { "C1 ❯ Credits.mkv" }, Names(resolver.ReadDirectory("10/Shorts")));
        Assert.Equal(new[] { "T1 ❯ Trailer.mkv" }, Names(resolver.ReadDirectory("10/Trailers")));
        Assert.Equal(new[] { "P1 ❯ Parody.mkv" }, Names(resolver.ReadDirectory("10/Scenes")));
        Assert.Equal(new[] { "O1 ❯ Featurette.mkv" }, Names(resolver.ReadDirectory("10/Featurettes")));
        Assert.Equal(new[] { "U1 ❯ Other.mkv" }, Names(resolver.ReadDirectory("10/Other")));
        Assert.Equal("/source/normal.mkv", resolver.GetSourcePath("10/Season 1/S01E01 [1].mkv"));
        Assert.Equal(1234, resolver.Lookup("10/Season 1/S01E01 [1].mkv")?.Size);
    }

    [Fact]
    public void OtherRemapsToStandardOrSpecialsWhenThoseBucketsAreAbsent()
    {
        var other = Video(20, "/source/other.mkv");
        var standard = Project(new RelayRawSeries(20, AnimeType.TV, "Other", [Episode(201, EpisodeType.Other, 1, null, "Other", other)]));
        Assert.Equal(new[] { "Season 1" }, Names(Resolver(standard).ReadDirectory("20")));
        Assert.Equal(new[] { "S01E01 [20].mkv" }, Names(Resolver(standard).ReadDirectory("20/Season 1")));

        var otherWithSpecials = Video(21, "/source/other-specials.mkv");
        var special = Video(22, "/source/specials.mkv");
        var specials = Project(
            new RelayRawSeries(
                21,
                AnimeType.TV,
                "Other",
                [
                    Episode(211, EpisodeType.Special, 1, null, "Special", special),
                    Episode(212, EpisodeType.Other, 1, null, "Other", otherWithSpecials),
                ]
            )
        );
        Assert.Equal(new[] { "Specials", "Season 1" }, Names(Resolver(specials).ReadDirectory("21")));
        Assert.Equal(new[] { "S00E01 [22].mkv" }, Names(Resolver(specials).ReadDirectory("21/Specials")));
        Assert.Equal(new[] { "S01E01 [21].mkv" }, Names(Resolver(specials).ReadDirectory("21/Season 1")));

        var otherWithSeason = Video(23, "/source/other-with-season.mkv");
        var standardWithOther = Video(24, "/source/standard-with-other.mkv");
        var seasonAndOther = Project(
            new RelayRawSeries(
                22,
                AnimeType.TV,
                "Other",
                [
                    Episode(221, EpisodeType.Episode, 1, 1, "Standard", standardWithOther),
                    Episode(222, EpisodeType.Other, 1, null, "Other", otherWithSeason),
                ]
            )
        );
        Assert.Equal(new[] { "Specials", "Season 1" }, Names(Resolver(seasonAndOther).ReadDirectory("22")));
        Assert.Equal(new[] { "S00E01 [23].mkv" }, Names(Resolver(seasonAndOther).ReadDirectory("22/Specials")));
        Assert.Equal(new[] { "S01E01 [24].mkv" }, Names(Resolver(seasonAndOther).ReadDirectory("22/Season 1")));
    }

    [Fact]
    public void SameSeasonVideoUsesRangeCoordinates()
    {
        var video = Video(30, "/source/range.mkv");
        var data = Project(
            new RelayRawSeries(
                30,
                AnimeType.TV,
                "Range",
                [
                    Episode(301, EpisodeType.Episode, 1, 1, "One", video),
                    Episode(302, EpisodeType.Episode, 2, 1, "Two", video),
                ]
            )
        );

        var resolver = Resolver(data);
        Assert.Equal(new[] { "S01E01-E02 [30].mkv" }, Names(resolver.ReadDirectory("30/Season 1")));
        Assert.Equal("/source/range.mkv", resolver.GetSourcePath("30/Season 1/S01E01-E02 [30].mkv"));
    }

    [Fact]
    public void TmdbCoordinatesUsePreferredOrderingAndFallbackWhenAbsent()
    {
        var preferred = Video(34, "/source/tmdb.mkv");
        var tmdbEpisode = new RelayRawTmdbEpisode(1, 1, "default", [new RelayRawTmdbOrdering("preferred", 2, 5)], "Title");
        var tmdb = Project(
            new RelayRawSeries(34, AnimeType.TV, "TMDB", [Episode(341, EpisodeType.Episode, 9, 3, "TMDB", preferred) with { TmdbEpisodes = [tmdbEpisode] }])
            {
                UseTmdbNumbering = true,
                PreferredTmdbOrderingId = "preferred",
            }
        );
        Assert.Equal((2, 5), (tmdb.Mappings.Single().Season, tmdb.Mappings.Single().Episode));

        var fallback = Project(
            new RelayRawSeries(35, AnimeType.TV, "Fallback", [Episode(351, EpisodeType.Episode, 9, 3, "Fallback", Video(35, "/source/fallback.mkv"))])
            {
                UseTmdbNumbering = true,
            }
        );
        Assert.Equal((3, 9), (fallback.Mappings.Single().Season, fallback.Mappings.Single().Episode));
    }

    [Fact]
    public void TmdbRangesRemainWithinSeasonAndDropCrossSeasonEnd()
    {
        var sameSeasonVideo = Video(36, "/source/tmdb-same.mkv");
        var sameSeason = Project(
            new RelayRawSeries(
                36,
                AnimeType.TV,
                "Same",
                [
                    Episode(361, EpisodeType.Episode, 1, 1, "One", sameSeasonVideo) with { TmdbEpisodes = [new RelayRawTmdbEpisode(2, 1, "default", [], null)] },
                    Episode(362, EpisodeType.Episode, 2, 1, "Two", sameSeasonVideo) with { TmdbEpisodes = [new RelayRawTmdbEpisode(2, 2, "default", [], null)] },
                ]
            )
            {
                UseTmdbNumbering = true,
            }
        );
        Assert.Equal((2, 1, 2), (sameSeason.Mappings.Single().Season, sameSeason.Mappings.Single().Episode, sameSeason.Mappings.Single().EndEpisode));

        var crossSeasonVideo = Video(37, "/source/tmdb-cross.mkv");
        var crossSeason = Project(
            new RelayRawSeries(
                37,
                AnimeType.TV,
                "Cross",
                [
                    Episode(371, EpisodeType.Episode, 1, 1, "One", crossSeasonVideo) with { TmdbEpisodes = [new RelayRawTmdbEpisode(2, 1, "default", [], null)] },
                    Episode(372, EpisodeType.Episode, 1, 2, "Two", crossSeasonVideo) with { TmdbEpisodes = [new RelayRawTmdbEpisode(3, 1, "default", [], null)] },
                ]
            )
            {
                UseTmdbNumbering = true,
            }
        );
        Assert.Equal((2, 1, null), (crossSeason.Mappings.Single().Season, crossSeason.Mappings.Single().Episode, crossSeason.Mappings.Single().EndEpisode));
    }

    [Fact]
    public void SharedTmdbRangeUsesRawPreferredOrderingAfterPerEpisodeNormalization()
    {
        var video = Video(38, "/source/tmdb-raw-range.mkv");
        var data = Project(
            new RelayRawSeries(
                38,
                AnimeType.TV,
                "Raw ordering",
                [
                    Episode(381, EpisodeType.Episode, 1, 1, "One", video) with
                    {
                        TmdbEpisodes = [new RelayRawTmdbEpisode(1, 1, "default", [new RelayRawTmdbOrdering("900", 2, 5)], null)],
                    },
                    Episode(382, EpisodeType.Episode, 2, 1, "Two", video) with
                    {
                        TmdbEpisodes = [new RelayRawTmdbEpisode(1, 2, "special", [new RelayRawTmdbOrdering("900", 2, 6)], null)],
                    },
                ]
            )
            {
                UseTmdbNumbering = true,
                PreferredTmdbOrderingId = null,
                RawPreferredTmdbOrderingId = "900",
            }
        );

        Assert.Equal((2, 5, 6), (data.Mappings.Single().Season, data.Mappings.Single().Episode, data.Mappings.Single().EndEpisode));
    }

    [Fact]
    public void SplitPartsAreOrderedBySourcePathAndDuplicateVariationUsesResolverRules()
    {
        var partTwo = Video(32, "/source/part2.mkv", new RelayRawCrossReference(401, 31));
        var partOne = Video(31, "/source/part1.mkv", new RelayRawCrossReference(401, 31));
        var splitData = Project(new RelayRawSeries(31, AnimeType.TV, "Parts", [Episode(401, EpisodeType.Episode, 1, 1, "Parted", partTwo, partOne)]));
        var splitResolver = Resolver(splitData);
        Assert.Equal(new[] { "S01E01-pt1.mkv", "S01E01-pt2.mkv" }, Names(splitResolver.ReadDirectory("31/Season 1")));
        Assert.Equal("/source/part1.mkv", splitResolver.GetSourcePath("31/Season 1/S01E01-pt1.mkv"));
        Assert.Equal("/source/part2.mkv", splitResolver.GetSourcePath("31/Season 1/S01E01-pt2.mkv"));

        var duplicateOne = Video(41, "/source/duplicate1.mkv");
        var duplicateTwo = Video(42, "/source/duplicate2.mkv");
        var variation = Video(43, "/source/variation.mkv", isVariation: true);
        var duplicateData = Project(new RelayRawSeries(32, AnimeType.TV, "Duplicates", [Episode(402, EpisodeType.Episode, 2, 1, "Duplicate", duplicateTwo, variation, duplicateOne)]));
        var duplicateResolver = Resolver(duplicateData);
        Assert.Equal(
            new[] { "S01E02-dup1 [41].mkv", "S01E02-dup2 [42].mkv", "S01E02 [43][variation].mkv" },
            Names(duplicateResolver.ReadDirectory("32/Season 1"))
        );
    }

    [Fact]
    public void MovieMainAndMappedExtraUseMovieProjection()
    {
        var main = Video(51, "/source/movie.mkv");
        var extra = Video(52, "/source/trailer.mkv");
        var data = Project(
            new RelayRawSeries(
                50,
                AnimeType.Movie,
                "Movie",
                [
                    Episode(501, EpisodeType.Episode, 1, 1, "Movie", main),
                    Episode(502, EpisodeType.Trailer, 1, null, "Trailer", extra),
                ]
            )
        );
        var resolver = Resolver(data, new PathResolverOptions { Shows = false, MoviesAsTv = false, StandaloneMovies = true, IncludeMovieExtras = true });

        Assert.Equal(new[] { "Movie [51].mkv", "Trailers" }, Names(resolver.ReadDirectory("501")));
        Assert.Equal(new[] { "T1 ❯ Trailer.mkv" }, Names(resolver.ReadDirectory("501/Trailers")));
        Assert.Equal("/source/trailer.mkv", resolver.GetSourcePath("501/Trailers/T1 ❯ Trailer.mkv"));
    }

    [Fact]
    public void NullSourceIsRetainedByProjectionButSkippedByResolver()
    {
        var data = Project(new RelayRawSeries(60, AnimeType.TV, "Missing", [Episode(601, EpisodeType.Episode, 1, 1, "Missing", Video(61, "/source/missing.mkv") with { SourcePath = null })]));

        Assert.Null(data.Mappings.Single().SourcePath);
        Assert.Empty(Resolver(data).ReadDirectory(""));
    }

    private static RelayRawEpisode Episode(int id, EpisodeType type, int number, int? season, string title, params RelayRawVideo[] videos) =>
        new(id, type, number, season, false, title, videos);

    private static RelayRawVideo Video(int id, string sourcePath, params RelayRawCrossReference[] crossReferences) =>
        new(id, false, sourcePath, sourcePath, 0, ".mkv", crossReferences);

    private static RelayRawVideo Video(int id, string sourcePath, bool isVariation) =>
        new(id, isVariation, sourcePath, sourcePath, 0, ".mkv", []);

    private static SeriesData Project(RelayRawSeries series) => RelayMappingProjector.Project(series);

    private static ShokoPathResolver Resolver(SeriesData series, PathResolverOptions? options = null) =>
        Ready(new SingleDataSource(series), options);

    private static ShokoPathResolver Ready(IShokoPathDataSource source, PathResolverOptions? options)
    {
        var resolver = new ShokoPathResolver(source, new RelayNamingStrategy(), options);
        resolver.ReadDirectory("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        return resolver;
    }

    private static string[] Names(IEnumerable<VirtualEntry> entries) => entries.Select(entry => entry.Name).ToArray();

    private sealed class SingleDataSource(SeriesData series) : IShokoPathDataSource
    {
        public IReadOnlyList<SeriesData> GetAllSeries() => [series];
    }
}
