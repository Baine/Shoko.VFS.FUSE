using System.Reflection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public sealed class RelayShokoPathDataSourceTests
{
    [Fact]
    public void ConstructorRejectsInvalidOptions()
    {
        var metadata = Metadata();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelayShokoPathDataSource(metadata, new RelayPathDataSourceOptions(0, "/tmp")));
        Assert.Throws<ArgumentException>(() => new RelayShokoPathDataSource(metadata, new RelayPathDataSourceOptions(1, " ")));
    }

    [Fact]
    public void ExtractsPublicGraphFiltersHiddenEpisodesAndUsesVideoId()
    {
        string root = NewDirectory();
        try
        {
            string sourcePath = WriteFile(root, "episode.mkv");
            var video = Video(123, [Location(7, true, sourcePath, "/episode.mkv", 123)]);
            var visible = Episode(1001, EpisodeType.Episode, 1, null, "Episode", false, video);
            var hidden = Episode(1002, EpisodeType.Episode, 2, null, "Hidden", true);
            var series = Series(55, AnimeType.TV, "Series", visible, hidden);

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var mapping = data.Mappings.Single();

            Assert.Equal(55, data.SeriesId);
            Assert.Equal("Series", data.DisplayTitle);
            Assert.False(data.IsMovie);
            Assert.Equal(123, mapping.FileId);
            Assert.Equal(1001, mapping.EpisodeId);
            Assert.Equal(1, mapping.Season);
            Assert.Equal(1, mapping.Episode);
            Assert.True(mapping.IsMain);
            Assert.Equal(Path.GetFullPath(sourcePath), mapping.SourcePath);
            Assert.Equal(123, mapping.Size);
            Assert.Equal(".mkv", mapping.Extension);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ProjectsTmdbCoordinatesAndUsesTmdbMoviePredicate()
    {
        string root = NewDirectory();
        try
        {
            string sourcePath = WriteFile(root, "tmdb.mkv");
            var tmdbEpisode = TmdbEpisode(1, 1, "default", "TMDB title", TmdbEpisodeOrdering("preferred", 2, 5));
            var episode = EpisodeWithTmdb(1101, EpisodeType.Episode, 1, 1, "Episode 1", false, [tmdbEpisode], [], Video(501, [Location(7, true, sourcePath, "/tmdb.mkv")]));
            var series = SeriesWithTmdb(
                56,
                AnimeType.TV,
                "TMDB series",
                [TmdbShow(900, "preferred")],
                [TmdbMovie()],
                5600,
                new PartialDateOnly(2020, 1, 1),
                [],
                episode
            );

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var mapping = data.Mappings.Single();

            Assert.True(data.IsMovie);
            Assert.Equal(2, mapping.Season);
            Assert.Equal(5, mapping.Episode);
            Assert.Contains("get_AllOrderings", ((StrictDispatchProxy)(object)tmdbEpisode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TmdbNumberingWithoutPreferredOrderingDoesNotReadAlternateOrderings()
    {
        string root = NewDirectory();
        try
        {
            var tmdbEpisode = TmdbEpisodeWithoutAlternateOrderings(2, 7, "default");
            var episode = EpisodeWithTmdb(
                1201,
                EpisodeType.Episode,
                3,
                9,
                "Episode 3",
                false,
                [tmdbEpisode],
                [],
                Video(1201, [Location(7, true, "/unused", "/default.mkv")])
            );
            var series = SeriesWithTmdb(120, AnimeType.TV, "Default ordering", [TmdbShow(902, null)], [], 1200, null, [], episode);

            var mapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries()
                .Single()
                .Mappings
                .Single();

            Assert.Equal((2, 7), (mapping.Season, mapping.Episode));
            Assert.DoesNotContain("get_AllOrderings", ((StrictDispatchProxy)(object)tmdbEpisode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void UnrelatedPreferredOrderingSeriesIsNotExtracted()
    {
        string root = NewDirectory();
        try
        {
            var unrelatedOrdering = TmdbEpisodeWithoutAlternateOrderings(1, 1, "default");
            var unrelated = SeriesWithTmdb(
                130,
                AnimeType.TV,
                "Unrelated",
                [TmdbShow(913, "preferred")],
                [],
                1300,
                null,
                [],
                EpisodeWithTmdb(
                    1301,
                    EpisodeType.Episode,
                    1,
                    1,
                    "Unrelated episode",
                    false,
                    [unrelatedOrdering],
                    [],
                    Video(1301, [Location(99, false, "/must-not-read", "/unrelated.mkv")])
                )
            );
            var local = Series(
                131,
                AnimeType.TV,
                "Local",
                Episode(1311, EpisodeType.Episode, 1, 1, "Local episode", false, Video(1311, [Location(7, true, "/unused", "/local.mkv")]))
            );

            var data = new RelayShokoPathDataSource(Metadata(local, unrelated), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries();

            var mapping = Assert.Single(Assert.Single(data).Mappings);
            Assert.Equal(1311, mapping.FileId);
            Assert.DoesNotContain("get_AllOrderings", ((StrictDispatchProxy)(object)unrelatedOrdering).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void UnrelatedSourceProbeReadsOnlyTheAllowedGraph()
    {
        string root = NewDirectory();
        try
        {
            var file = Proxy<IVideoFile>(
                ("get_ManagedFolderID", 99)
            );
            var video = Proxy<IVideo>(
                ("get_Files", (IReadOnlyList<IVideoFile>)[file])
            );
            var episode = Proxy<IShokoEpisode>(
                ("get_Videos", (IReadOnlyList<IVideo>)[video])
            );
            var unrelated = Proxy<IShokoSeries>(
                ("get_ID", 132),
                ("get_AnidbAnimeID", 1320),
                ("get_Episodes", (IReadOnlyList<IShokoEpisode>)[episode])
            );
            var local = Series(
                133,
                AnimeType.TV,
                "Local",
                Episode(1331, EpisodeType.Episode, 1, 1, "Local episode", false, Video(1331, [Location(7, true, "/unused", "/local.mkv")]))
            );

            var data = new RelayShokoPathDataSource(Metadata(local, unrelated), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries();

            Assert.Equal(133, Assert.Single(data).SeriesId);
            Assert.Equal(new[] { "get_ManagedFolderID" }, ((StrictDispatchProxy)(object)file).Reads);
            Assert.DoesNotContain("get_IsAvailable", ((StrictDispatchProxy)(object)file).Reads);
            Assert.DoesNotContain("get_Path", ((StrictDispatchProxy)(object)file).Reads);
            Assert.DoesNotContain("get_RelativePath", ((StrictDispatchProxy)(object)file).Reads);
            Assert.DoesNotContain("get_Size", ((StrictDispatchProxy)(object)file).Reads);
            Assert.DoesNotContain("get_TmdbEpisodes", ((StrictDispatchProxy)(object)episode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ManualClosureRetainsNonLocalPrimaryAndOnlyLocalSource()
    {
        string root = NewDirectory();
        try
        {
            var primary = Series(
                140,
                AnimeType.TV,
                "Primary",
                Episode(1401, EpisodeType.Episode, 1, 1, "Primary episode", false, Video(1401, [Location(99, false, "/unused", "/primary.mkv")]))
            );
            var local = Series(
                141,
                AnimeType.TV,
                "Local",
                Episode(1411, EpisodeType.Episode, 1, 1, "Local episode", false, Video(1411, [Location(7, true, "/unused", "/local.mkv")]))
            );
            var options = new RelayPathDataSourceOptions(7, root) { ManualOverrideGroups = [[140, 141]] };

            var data = new RelayShokoPathDataSource(Metadata(local, primary), options).GetAllSeries();

            var merged = Assert.Single(data);
            Assert.Equal(140, merged.SeriesId);
            Assert.Null(merged.Mappings.Single(mapping => mapping.FileId == 1401).SourcePath);
            Assert.Equal(Path.Combine(root, "local.mkv"), merged.Mappings.Single(mapping => mapping.FileId == 1411).SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void AutomaticClosureRetainsEarlierNonLocalPrimary()
    {
        string root = NewDirectory();
        try
        {
            var primary = SeriesWithTmdb(
                150,
                AnimeType.TV,
                "Earlier",
                [TmdbShow(915, null)],
                [],
                1500,
                new PartialDateOnly(2020, 1, 1),
                [],
                Episode(1501, EpisodeType.Episode, 1, 1, "Earlier episode", false, Video(1501, [Location(99, false, "/unused", "/earlier.mkv")]))
            );
            var local = SeriesWithTmdb(
                151,
                AnimeType.TV,
                "Local",
                [TmdbShow(915, null)],
                [],
                1510,
                new PartialDateOnly(2021, 1, 1),
                [],
                Episode(1511, EpisodeType.Episode, 1, 1, "Local episode", false, Video(1511, [Location(7, true, "/unused", "/local.mkv")]))
            );

            var data = new RelayShokoPathDataSource(Metadata(local, primary), new RelayPathDataSourceOptions(7, root) { MergeTmdbSeries = true })
                .GetAllSeries();

            var merged = Assert.Single(data);
            Assert.Equal(150, merged.SeriesId);
            Assert.Null(merged.Mappings.Single(mapping => mapping.FileId == 1501).SourcePath);
            Assert.Equal(Path.Combine(root, "local.mkv"), merged.Mappings.Single(mapping => mapping.FileId == 1511).SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SelectedClosureRetainsNonLocalMappingsForRelayLayoutCounts()
    {
        string root = NewDirectory();
        try
        {
            var primary = Series(
                160,
                AnimeType.TV,
                "Primary",
                Episode(1601, EpisodeType.Episode, 100, 1, "Non-local", false, Video(1601, [Location(99, false, "/unused", "/non-local.mkv")]))
            );
            var local = Series(
                161,
                AnimeType.TV,
                "Local",
                Episode(1611, EpisodeType.Episode, 1, 1, "Local", false, Video(1611, [Location(7, true, "/unused", "/local.mkv")]))
            );
            var options = new RelayPathDataSourceOptions(7, root) { ManualOverrideGroups = [[160, 161]] };

            var data = new RelayShokoPathDataSource(Metadata(local, primary), options).GetAllSeries().Single();

            Assert.Equal(new[] { 1601, 1611 }, data.Mappings.Select(mapping => mapping.FileId));
            Assert.Null(data.Mappings.Single(mapping => mapping.FileId == 1601).SourcePath);
            var resolver = Ready(data);
            Assert.Equal(new[] { "S01E001 [1611].mkv" }, Names(resolver.ReadDirectory("160/Season 1")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void UnrelatedAutomaticGroupDoesNotLoadEpisodeOrderings()
    {
        string root = NewDirectory();
        try
        {
            var firstOrdering = TmdbEpisodeWithoutAlternateOrderings(1, 1, "default");
            var secondOrdering = TmdbEpisodeWithoutAlternateOrderings(1, 2, "default");
            var first = SeriesWithTmdb(
                170,
                AnimeType.TV,
                "Unrelated first",
                [TmdbShow(916, "preferred")],
                [],
                1700,
                new PartialDateOnly(2020, 1, 1),
                [],
                EpisodeWithTmdb(1701, EpisodeType.Episode, 1, 1, "First", false, [firstOrdering], [], Video(1701, [Location(99, false, "/unused", "/first.mkv")]))
            );
            var second = SeriesWithTmdb(
                171,
                AnimeType.TV,
                "Unrelated second",
                [TmdbShow(916, "preferred")],
                [],
                1710,
                new PartialDateOnly(2021, 1, 1),
                [],
                EpisodeWithTmdb(1711, EpisodeType.Episode, 2, 1, "Second", false, [secondOrdering], [], Video(1711, [Location(99, false, "/unused", "/second.mkv")]))
            );
            var local = Series(
                172,
                AnimeType.TV,
                "Local",
                Episode(1721, EpisodeType.Episode, 1, 1, "Local", false, Video(1721, [Location(7, true, "/unused", "/local.mkv")]))
            );

            var data = new RelayShokoPathDataSource(
                Metadata(local, first, second),
                new RelayPathDataSourceOptions(7, root) { MergeTmdbSeries = true }
            ).GetAllSeries();

            Assert.Equal(172, Assert.Single(data).SeriesId);
            Assert.DoesNotContain("get_AllOrderings", ((StrictDispatchProxy)(object)firstOrdering).Reads);
            Assert.DoesNotContain("get_AllOrderings", ((StrictDispatchProxy)(object)secondOrdering).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void RawPreferredOrderingMatchingShowIdLoadsAlternateRowsForJoinedRanges()
    {
        string root = NewDirectory();
        try
        {
            var firstTmdbEpisode = TmdbEpisode(1, 1, "default", null, TmdbEpisodeOrdering("900", 2, 5));
            var secondTmdbEpisode = TmdbEpisode(1, 2, "default", null, TmdbEpisodeOrdering("900", 2, 6));
            var video = Video(1202, [Location(7, true, "/unused", "/joined.mkv")]);
            var first = EpisodeWithTmdb(12021, EpisodeType.Episode, 1, 1, "One", false, [firstTmdbEpisode], [], video);
            var second = EpisodeWithTmdb(12022, EpisodeType.Episode, 2, 1, "Two", false, [secondTmdbEpisode], [], video);
            var series = SeriesWithTmdb(121, AnimeType.TV, "Raw ordering", [TmdbShow(900, "900")], [], 1210, null, [], first, second);

            var mapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries()
                .Single()
                .Mappings
                .Single();

            Assert.Equal((2, 5, 6), (mapping.Season, mapping.Episode, mapping.EndEpisode));
            Assert.Contains("get_AllOrderings", ((StrictDispatchProxy)(object)firstTmdbEpisode).Reads);
            Assert.Contains("get_AllOrderings", ((StrictDispatchProxy)(object)secondTmdbEpisode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TmdbNumberingDisabledDoesNotReadAlternateOrderings()
    {
        string root = NewDirectory();
        try
        {
            var tmdbEpisode = TmdbEpisodeWithoutAlternateOrderings(2, 7, "default");
            var episode = EpisodeWithTmdb(
                1203,
                EpisodeType.Episode,
                8,
                4,
                "Episode 8",
                false,
                [tmdbEpisode],
                [],
                Video(1203, [Location(7, true, "/unused", "/disabled.mkv")])
            );
            var series = SeriesWithTmdb(122, AnimeType.TV, "Shoko ordering", [TmdbShow(903, "preferred")], [], 1220, null, [], episode);

            var mapping = new RelayShokoPathDataSource(
                Metadata(series),
                new RelayPathDataSourceOptions(7, root) { TmdbEpNumbering = false }
            ).GetAllSeries().Single().Mappings.Single();

            Assert.Equal((4, 8), (mapping.Season, mapping.Episode));
            Assert.DoesNotContain("get_AllOrderings", ((StrictDispatchProxy)(object)tmdbEpisode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void MissingSelectedOrderingReadsAlternatesThenFallsBackToPrimaryCoordinates()
    {
        string root = NewDirectory();
        try
        {
            var tmdbEpisode = TmdbEpisode(3, 9, "default");
            var episode = EpisodeWithTmdb(
                1204,
                EpisodeType.Episode,
                8,
                4,
                "Episode 8",
                false,
                [tmdbEpisode],
                [],
                Video(1204, [Location(7, true, "/unused", "/missing-ordering.mkv")])
            );
            var series = SeriesWithTmdb(123, AnimeType.TV, "Missing ordering", [TmdbShow(904, "preferred")], [], 1230, null, [], episode);

            var mapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries().Single().Mappings.Single();

            Assert.Equal((3, 9), (mapping.Season, mapping.Episode));
            Assert.Contains("get_AllOrderings", ((StrictDispatchProxy)(object)tmdbEpisode).Reads);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExcludesSourceOnlyManagedFoldersAndExcludedPathSegmentsBeforeProjection()
    {
        string root = NewDirectory();
        try
        {
            string allowed = WriteFile(root, "allowed.mkv");
            string blockedFolder = WriteFile(root, Path.Combine("blocked", "folder.mkv"));
            string blockedRoot = WriteFile(root, Path.Combine("!ShokoRelayVFS", "root.mkv"));
            string blockedExtra = WriteFile(root, Path.Combine("Trailers", "extra.mkv"));
            var series = Series(
                57,
                AnimeType.TV,
                "Excluded",
                Episode(5701, EpisodeType.Episode, 1, 1, "Allowed", false, Video(601, [Location(7, true, allowed, "/allowed.mkv")])),
                Episode(5702, EpisodeType.Episode, 2, 1, "Folder", false, Video(602, [Location(7, true, blockedFolder, "/blocked/folder.mkv")])),
                Episode(5703, EpisodeType.Episode, 3, 1, "Root", false, Video(603, [Location(7, true, blockedRoot, "/!ShokoRelayVFS/root.mkv")])),
                Episode(5704, EpisodeType.Episode, 4, 1, "Extra", false, Video(604, [Location(7, true, blockedExtra, "/Trailers/extra.mkv")]))
            );

            var options = new RelayPathDataSourceOptions(7, root) { FolderExclusions = "blocked" };
            var data = new RelayShokoPathDataSource(Metadata(series), options).GetAllSeries().Single();

            Assert.Equal(new[] { 601 }, data.Mappings.Select(mapping => mapping.FileId));

            Assert.Empty(
                new RelayShokoPathDataSource(
                    Metadata(series),
                    options with { ManagedFolderType = DropFolderType.Source }
                ).GetAllSeries()
            );
            Assert.Empty(
                new RelayShokoPathDataSource(
                    Metadata(series),
                    options with { ManagedFolderExclusions = "7" }
                ).GetAllSeries()
            );
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void RelayTitlesPreferConfiguredLanguageAndRepairGenericAmbiguousAndTmdbGroupNames()
    {
        string root = NewDirectory();
        try
        {
            var ambiguous = EpisodeWithTmdb(
                5801,
                EpisodeType.Trailer,
                1,
                null,
                "Special",
                false,
                [],
                [Title("Special", "en", TitleType.Main)],
                Video(701, [Location(7, true, WriteFile(root, "ambiguous.mkv"), "/ambiguous.mkv")])
            );
            var generic = EpisodeWithTmdb(
                5802,
                EpisodeType.Episode,
                2,
                1,
                "Episode 2",
                false,
                [TmdbEpisode(1, 2, "default", "Actual title")],
                [Title("Episode 2", "en", TitleType.Synonym), Title("Episode 2", "en", TitleType.Main)],
                Video(702, [Location(7, true, WriteFile(root, "generic.mkv"), "/generic.mkv")])
            );
            var grouped = EpisodeWithTmdb(
                5803,
                EpisodeType.Trailer,
                2,
                null,
                "Trailer",
                false,
                [TmdbEpisode(1, 2, "default", "TMDB group title"), TmdbEpisode(1, 3, "default", "Second title")],
                [Title("Trailer", "en", TitleType.Main)],
                Video(703, [Location(7, true, WriteFile(root, "group.mkv"), "/group.mkv")])
            );
            var series = SeriesWithTmdb(
                58,
                AnimeType.TV,
                "OVA Example",
                [],
                [],
                5800,
                null,
                [Title("OVA Example", "en", TitleType.Main), Title("Wrong", "en", TitleType.Synonym)],
                ambiguous,
                generic,
                grouped
            );

            var data = new RelayShokoPathDataSource(
                Metadata(series),
                new RelayPathDataSourceOptions(7, root)
                {
                    SeriesTitleLanguage = "en",
                    EpisodeTitleLanguage = "en",
                }
            ).GetAllSeries().Single();
            var mappings = data.Mappings.ToDictionary(mapping => mapping.EpisodeId);

            Assert.Equal("Example — OVA", data.DisplayTitle);
            Assert.Equal("Example — OVA — Special", mappings[5801].EpisodeTitle);
            Assert.Equal("Actual title", mappings[5802].EpisodeTitle);
            Assert.Equal("TMDB group title", mappings[5803].EpisodeTitle);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void UsesTmdbMovieClassificationAndMergesSeriesByTmdbShow()
    {
        string root = NewDirectory();
        try
        {
            var first = SeriesWithTmdb(
                59,
                AnimeType.TV,
                "First",
                [TmdbShow(777, null)],
                [TmdbMovie()],
                1001,
                new PartialDateOnly(2020, 1, 1),
                [],
                Episode(5901, EpisodeType.Episode, 1, 1, "One", false, Video(711, [Location(7, true, WriteFile(root, "first.mkv"), "/first.mkv")]))
            );
            var second = SeriesWithTmdb(
                60,
                AnimeType.TV,
                "Second",
                [TmdbShow(777, null)],
                [],
                1002,
                new PartialDateOnly(2021, 1, 1),
                [],
                Episode(6001, EpisodeType.Episode, 1, 2, "Two", false, Video(712, [Location(7, true, WriteFile(root, "second.mkv"), "/second.mkv")]))
            );

            var data = new RelayShokoPathDataSource(
                Metadata(first, second),
                new RelayPathDataSourceOptions(7, root) { MergeTmdbSeries = true }
            ).GetAllSeries();

            var merged = Assert.Single(data);
            Assert.True(merged.IsMovie);
            Assert.Equal(59, merged.SeriesId);
            Assert.Equal(new[] { 711, 712 }, merged.Mappings.Select(mapping => mapping.FileId));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ManualOverridesOnlyApplyWhenTmdbNumberingIsEnforced()
    {
        string root = NewDirectory();
        try
        {
            var first = Series(61, AnimeType.TV, "First", Episode(6101, EpisodeType.Episode, 1, 1, "One", false, Video(721, [Location(7, true, WriteFile(root, "first-manual.mkv"), "/first-manual.mkv")])));
            var second = Series(
                62,
                AnimeType.TV,
                "Second",
                Episode(
                    6201,
                    EpisodeType.Episode,
                    2,
                    1,
                    "Two",
                    false,
                    Video(722, [Location(7, true, WriteFile(root, "second-manual.mkv"), "/second-manual.mkv")])
                )
            );
            var options = new RelayPathDataSourceOptions(7, root)
            {
                TmdbEpNumbering = false,
                ManualOverrideGroups = [[62, 61]],
            };

            var individual = new RelayShokoPathDataSource(Metadata(first, second), options).GetAllSeries();
            Assert.Equal(new[] { 61, 62 }, individual.Select(series => series.SeriesId));

            var merged = new RelayShokoPathDataSource(
                Metadata(first, second),
                options with { TmdbEpNumbering = true }
            ).GetAllSeries();
            var group = Assert.Single(merged);
            Assert.Equal(62, group.SeriesId);
            Assert.Equal(new[] { 722, 721 }, group.Mappings.Select(mapping => mapping.FileId));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void MissingPreferredTitleDoesNotFallbackToItemTitle()
    {
        string root = NewDirectory();
        try
        {
            var episode = EpisodeWithTmdb(
                6301,
                EpisodeType.Episode,
                1,
                1,
                "Legacy episode title",
                false,
                [],
                [Title("Short title", "en", TitleType.Short)],
                Video(731, [Location(7, true, WriteFile(root, "untitled.mkv"), "/untitled.mkv")])
            );
            var series = SeriesWithTmdb(
                63,
                AnimeType.TV,
                "Legacy series title",
                [],
                [],
                6300,
                null,
                [Title("Short series title", "en", TitleType.Short)],
                episode
            );

            var data = new RelayShokoPathDataSource(
                Metadata(series),
                new RelayPathDataSourceOptions(7, root)
                {
                    SeriesTitleLanguage = "fr",
                    EpisodeTitleLanguage = "fr",
                }
            ).GetAllSeries().Single();

            Assert.Equal("", data.DisplayTitle);
            Assert.Equal("", data.Mappings.Single().EpisodeTitle);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SplitTagsCreatePartsForOrdinaryCrossReferences()
    {
        string root = NewDirectory();
        try
        {
            string partTwoPath = WriteFile(root, "part2.mkv");
            string partOnePath = WriteFile(root, "part1.mkv");
            var partOne = Video(301, [Location(7, true, partOnePath, "/part1.mkv")], false, CrossReference(ReferenceEpisode(1001, 88), 100, 1));
            var partTwo = Video(302, [Location(7, true, partTwoPath, "/part2.mkv")], false, CrossReference(ReferenceEpisode(1001, 88), 100, 1));
            var series = Series(88, AnimeType.TV, "Parts", Episode(1001, EpisodeType.Episode, 1, 1, "Episode", false, partTwo, partOne));
            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var resolver = Ready(data);

            Assert.Equal(new[] { "S01E01-pt1.mkv", "S01E01-pt2.mkv" }, Names(resolver.ReadDirectory("88/Season 1")));
            Assert.Equal(Path.GetFullPath(partOnePath), resolver.GetSourcePath("88/Season 1/S01E01-pt1.mkv"));
            Assert.Equal(1, data.Mappings.Single(mapping => mapping.FileId == 301).PartIndex);
            Assert.Equal(2, data.Mappings.Single(mapping => mapping.FileId == 302).PartIndex);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void PercentageGroupSizeDoesNotCreatePartsOrOmitFileIdsWithoutSplitTags()
    {
        string root = NewDirectory();
        try
        {
            string firstPath = WriteFile(root, "episode-a.mkv");
            string secondPath = WriteFile(root, "episode-b.mkv");
            var first = Video(401, [Location(7, true, firstPath, "/episode-a.mkv")], false, CrossReference(ReferenceEpisode(1002, 89), 50, 2));
            var second = Video(402, [Location(7, true, secondPath, "/episode-b.mkv")], false, CrossReference(ReferenceEpisode(1002, 89), 50, 2));
            var series = Series(89, AnimeType.TV, "Versions", Episode(1002, EpisodeType.Episode, 1, 1, "Episode", false, first, second));
            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var resolver = Ready(data);

            Assert.All(data.Mappings, mapping =>
            {
                Assert.Null(mapping.PartIndex);
                Assert.Equal(1, mapping.PartCount);
            });
            Assert.Equal(
                new[] { "S01E01-dup1 [401].mkv", "S01E01-dup2 [402].mkv" },
                Names(resolver.ReadDirectory("89/Season 1"))
            );
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExtractsOrderedShokoEpisodeIdsAndSeriesIdsBeforeFilteringMixedTypes()
    {
        string root = NewDirectory();
        try
        {
            string partTwoPath = WriteFile(root, "part2.mkv");
            string partOnePath = WriteFile(root, "part1.mkv");
            var trailerReferenceEpisode = ReferenceEpisode(1004, 101);
            var normalReferenceEpisode = ReferenceEpisode(1003, 100);
            var partOne = Video(
                403,
                [Location(7, true, partOnePath, "/part1.mkv")],
                false,
                CrossReference(trailerReferenceEpisode),
                CrossReference(normalReferenceEpisode)
            );
            var partTwo = Video(
                404,
                [Location(7, true, partTwoPath, "/part2.mkv")],
                false,
                CrossReference(trailerReferenceEpisode),
                CrossReference(normalReferenceEpisode)
            );
            var series = Series(
                90,
                AnimeType.TV,
                "Mixed",
                Episode(1003, EpisodeType.Episode, 1, 1, "Normal", false, partTwo, partOne),
                Episode(1004, EpisodeType.Trailer, 1, null, "Trailer", false, partTwo, partOne)
            );

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var resolver = Ready(data);

            Assert.Contains("get_ID", ((StrictDispatchProxy)(object)trailerReferenceEpisode).Reads);
            Assert.Contains("get_SeriesID", ((StrictDispatchProxy)(object)trailerReferenceEpisode).Reads);
            Assert.All(data.Mappings, mapping =>
            {
                Assert.Equal(1004, mapping.EpisodeId);
                Assert.Equal(-2, mapping.Season);
            });
            Assert.Equal(new[] { "Trailers" }, Names(resolver.ReadDirectory("90")));
            Assert.Equal(new[] { "T1-pt1 ❯ Trailer.mkv", "T1-pt2 ❯ Trailer.mkv" }, Names(resolver.ReadDirectory("90/Trailers")));
            Assert.Empty(resolver.ReadDirectory("90/Season 1"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SelectsFirstContainedSourceInOriginalOrderWithoutAvailabilityProbes()
    {
        string root = NewDirectory();
        string outsideRoot = NewDirectory();
        try
        {
            string first = WriteFile(root, "a.mkv");
            string second = WriteFile(root, "b.mkv");
            string fallback = WriteFile(root, "fallback.mkv");
            string unavailable = WriteFile(root, "unavailable.mkv");
            string wrongFolder = WriteFile(root, "wrong-folder.mkv");
            string outside = WriteFile(outsideRoot, "outside.mkv");

            var deterministic = Video(
                201,
                [
                    Location(7, true, second, "/b.mkv"),
                    Location(7, true, first, "/a.mkv"),
                ]
            );
            var relativeFallback = Video(202, [Location(7, true, Path.Combine(root, "missing.mkv"), "\\fallback.mkv")]);
            var unavailableVideo = Video(203, [Location(7, false, unavailable, "/unavailable.mkv")]);
            var outsideVideo = Video(204, [Location(7, true, outside, "..\\outside.mkv")]);
            var wrongFolderVideo = Video(205, [Location(99, true, wrongFolder, "/wrong-folder.mkv")]);
            var series = Series(
                77,
                AnimeType.TV,
                "Sources",
                Episode(701, EpisodeType.Episode, 1, 1, "A", false, deterministic),
                Episode(702, EpisodeType.Episode, 2, 1, "B", false, relativeFallback),
                Episode(703, EpisodeType.Episode, 3, 1, "C", false, unavailableVideo),
                Episode(704, EpisodeType.Episode, 4, 1, "D", false, outsideVideo),
                Episode(705, EpisodeType.Episode, 5, 1, "E", false, wrongFolderVideo)
            );

            var mappings = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single().Mappings.ToDictionary(mapping => mapping.FileId);

            Assert.Equal(Path.GetFullPath(second), mappings[201].SourcePath);
            Assert.Equal(Path.GetFullPath(fallback), mappings[202].SourcePath);
            Assert.Equal(Path.GetFullPath(unavailable), mappings[203].SourcePath);
            Assert.Null(mappings[204].SourcePath);
            Assert.Null(mappings[205].SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void UsesMissingRecordedRelativePathAndRejectsWrongFolderAndTraversal()
    {
        string root = NewDirectory();
        try
        {
            var valid = Video(206, [Location(7, false, "/must-not-read", "/missing.mkv", 456, throwOnSourceProbe: true)]);
            var wrongFolder = Video(207, [Location(99, false, "/must-not-read", "/wrong-folder.mkv", 789, throwOnSourceProbe: true)]);
            var traversal = Video(208, [Location(7, false, "/must-not-read", "../outside.mkv", 987, throwOnSourceProbe: true)]);
            var series = Series(
                78,
                AnimeType.TV,
                "Relative paths",
                Episode(706, EpisodeType.Episode, 1, 1, "Valid", false, valid),
                Episode(707, EpisodeType.Episode, 2, 1, "Wrong folder", false, wrongFolder),
                Episode(708, EpisodeType.Episode, 3, 1, "Traversal", false, traversal)
            );

            var mappings = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries()
                .Single()
                .Mappings;

            var validMapping = Assert.Single(mappings, mapping => mapping.FileId == 206);
            Assert.Equal(Path.Combine(root, "missing.mkv"), validMapping.SourcePath);
            Assert.Equal(456, validMapping.Size);
            Assert.Null(mappings.Single(mapping => mapping.FileId == 207).SourcePath);
            Assert.Null(mappings.Single(mapping => mapping.FileId == 208).SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SourceOnlyFolderIsGloballyIneligibleAndLaterEligibleFolderWins()
    {
        string root = NewDirectory();
        try
        {
            var video = Video(
                209,
                [
                    Location(8, true, "/unused", "/source-only.mkv", folderType: DropFolderType.Source),
                    Location(7, true, "/unused", "/second.mkv"),
                ]
            );
            var series = Series(79, AnimeType.TV, "Ineligible folder", Episode(709, EpisodeType.Episode, 1, 1, "Episode", false, video));

            var mapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries().Single().Mappings.Single();

            Assert.Equal(Path.Combine(root, "second.mkv"), mapping.SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void GloballyFirstEligibleLocationOwnsVideoSoLaterMountEmitsNothing()
    {
        string firstRoot = NewDirectory();
        string secondRoot = NewDirectory();
        try
        {
            string firstPath = WriteFile(firstRoot, "first.mkv");
            string secondPath = WriteFile(secondRoot, "second.mkv");
            var video = Video(
                210,
                [
                    Location(8, true, firstPath, "/first.mkv"),
                    Location(7, true, secondPath, "/second.mkv"),
                ]
            );
            var series = Series(80, AnimeType.TV, "Ownership", Episode(710, EpisodeType.Episode, 1, 1, "Episode", false, video));

            var firstMapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(8, firstRoot))
                .GetAllSeries().Single().Mappings.Single();
            var secondMapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, secondRoot))
                .GetAllSeries().Single().Mappings.Single();

            Assert.Equal(Path.Combine(firstRoot, "first.mkv"), firstMapping.SourcePath);
            Assert.Null(secondMapping.SourcePath);
        }
        finally
        {
            DeleteDirectory(firstRoot);
            DeleteDirectory(secondRoot);
        }
    }

    [Fact]
    public void PathExcludedGlobalWinnerBlocksFallbackToLaterLocation()
    {
        string root = NewDirectory();
        try
        {
            var excluded = Video(
                211,
                [
                    Location(7, true, "/must-not-read", "/Excluded/first.mkv"),
                    Location(7, true, "/must-not-read", "/second.mkv", throwOnSourceProbe: true),
                ]
            );
            var ok = Video(2110, [Location(7, true, "/must-not-read", "/ok.mkv", throwOnSourceProbe: true)]);
            var series = Series(
                81, AnimeType.TV, "Exclusion blocks",
                Episode(711, EpisodeType.Episode, 1, 1, "Excluded", false, excluded),
                Episode(712, EpisodeType.Episode, 2, 1, "OK", false, ok)
            );
            var options = new RelayPathDataSourceOptions(7, root) { FolderExclusions = "Excluded" };

            var data = new RelayShokoPathDataSource(Metadata(series), options).GetAllSeries();

            var single = Assert.Single(data);
            Assert.Single(single.Mappings, mapping => mapping.FileId == 2110);
            Assert.DoesNotContain(single.Mappings, mapping => mapping.FileId == 211);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void NoFilesystemProbesOnGlobalOwnershipCheck()
    {
        string root = NewDirectory();
        try
        {
            var video = Video(
                212,
                [
                    Location(8, false, "/must-not-read", "/folder8.mkv", throwOnSourceProbe: true, folderType: DropFolderType.Source),
                    Location(7, true, "/must-not-read", "/folder7.mkv", throwOnSourceProbe: true),
                ]
            );
            var series = Series(82, AnimeType.TV, "No probes", Episode(712, EpisodeType.Episode, 1, 1, "Episode", false, video));

            var mapping = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root))
                .GetAllSeries().Single().Mappings.Single();

            Assert.Equal(Path.Combine(root, "folder7.mkv"), mapping.SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static IMetadataService Metadata(params IShokoSeries[] series) =>
        Proxy<IMetadataService>(
            ("GetAllShokoSeries", (IEnumerable<IShokoSeries>)series)
        );

    private static IShokoSeries Series(int id, AnimeType type, string title, params IShokoEpisode[] episodes) =>
        Proxy<IShokoSeries>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_Title", title),
            ("get_PreferredTitle", Title(title, "shoko", TitleType.Main)),
            ("get_Titles", (IReadOnlyList<ITitle>)Array.Empty<ITitle>()),
            ("get_AnidbAnimeID", id),
            ("get_AirDate", null),
            ("get_TmdbShows", (IReadOnlyList<ITmdbShow>)Array.Empty<ITmdbShow>()),
            ("get_TmdbMovies", (IReadOnlyList<ITmdbMovie>)Array.Empty<ITmdbMovie>()),
            ("get_Episodes", (IReadOnlyList<IShokoEpisode>)episodes)
        );

    private static IShokoSeries SeriesWithTmdb(
        int id,
        AnimeType type,
        string title,
        IReadOnlyList<ITmdbShow> tmdbShows,
        IReadOnlyList<ITmdbMovie> tmdbMovies,
        int anidbAnimeId,
        PartialDateOnly? airDate,
        IReadOnlyList<ITitle> titles,
        params IShokoEpisode[] episodes) =>
        Proxy<IShokoSeries>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_Title", title),
            ("get_PreferredTitle", null),
            ("get_Titles", titles),
            ("get_AnidbAnimeID", anidbAnimeId),
            ("get_AirDate", airDate),
            ("get_TmdbShows", tmdbShows),
            ("get_TmdbMovies", tmdbMovies),
            ("get_Episodes", (IReadOnlyList<IShokoEpisode>)episodes)
        );

    private static IShokoEpisode Episode(int id, EpisodeType type, int number, int? season, string title, bool hidden, params IVideo[] videos) =>
        Proxy<IShokoEpisode>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_EpisodeNumber", number),
            ("get_SeasonNumber", season),
            ("get_Title", title),
            ("get_PreferredTitle", Title(title, "shoko", TitleType.Main)),
            ("get_Titles", (IReadOnlyList<ITitle>)Array.Empty<ITitle>()),
            ("get_TmdbEpisodes", (IReadOnlyList<ITmdbEpisode>)Array.Empty<ITmdbEpisode>()),
            ("get_IsHidden", hidden),
            ("get_Videos", (IReadOnlyList<IVideo>)videos)
        );

    private static IShokoEpisode EpisodeWithTmdb(
        int id,
        EpisodeType type,
        int number,
        int? season,
        string title,
        bool hidden,
        IReadOnlyList<ITmdbEpisode> tmdbEpisodes,
        IReadOnlyList<ITitle> titles,
        params IVideo[] videos) =>
        Proxy<IShokoEpisode>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_EpisodeNumber", number),
            ("get_SeasonNumber", season),
            ("get_Title", title),
            ("get_PreferredTitle", null),
            ("get_Titles", titles),
            ("get_TmdbEpisodes", tmdbEpisodes),
            ("get_IsHidden", hidden),
            ("get_Videos", (IReadOnlyList<IVideo>)videos)
        );

    private static ITitle Title(string value, string languageCode, TitleType type) =>
        Proxy<ITitle>(
            ("get_Value", value),
            ("get_LanguageCode", languageCode),
            ("get_Type", type)
        );

    private static ITmdbShow TmdbShow(int id, string? preferredOrderingId) =>
        Proxy<ITmdbShow>(
            ("get_ID", id),
            ("get_PreferredOrdering", preferredOrderingId is null ? null : TmdbShowOrdering(preferredOrderingId))
        );

    private static ITmdbShowOrderingInformation TmdbShowOrdering(string orderingId) =>
        Proxy<ITmdbShowOrderingInformation>(
            ("get_OrderingID", orderingId)
        );

    private static ITmdbEpisode TmdbEpisode(
        int? season,
        int episode,
        string orderingId,
        string? title = null,
        params ITmdbEpisodeOrderingInformation[] allOrderings) =>
        Proxy<ITmdbEpisode>(
            ("get_SeasonNumber", season),
            ("get_EpisodeNumber", episode),
            ("get_OrderingID", orderingId),
            ("get_AllOrderings", (IReadOnlyList<ITmdbEpisodeOrderingInformation>)allOrderings),
            ("get_PreferredTitle", title is null ? null : Title(title, "en", TitleType.Official))
        );

    private static ITmdbEpisode TmdbEpisodeWithoutAlternateOrderings(int? season, int episode, string orderingId) =>
        Proxy<ITmdbEpisode>(
            ("get_SeasonNumber", season),
            ("get_EpisodeNumber", episode),
            ("get_OrderingID", orderingId),
            ("get_PreferredTitle", null)
        );

    private static ITmdbEpisodeOrderingInformation TmdbEpisodeOrdering(string orderingId, int season, int episode) =>
        Proxy<ITmdbEpisodeOrderingInformation>(
            ("get_OrderingID", orderingId),
            ("get_SeasonNumber", season),
            ("get_EpisodeNumber", episode)
        );

    private static ITmdbMovie TmdbMovie() => Proxy<ITmdbMovie>();

    private static IVideo Video(int id, IReadOnlyList<IVideoFile> files, bool variation = false, params IVideoCrossReference[] crossReferences) =>
        Proxy<IVideo>(
            ("get_ID", id),
            ("get_IsVariation", variation),
            ("get_Files", files),
            ("get_CrossReferences", (IReadOnlyList<IVideoCrossReference>)crossReferences)
        );

    private static IShokoEpisode ReferenceEpisode(int id, int seriesId) =>
        Proxy<IShokoEpisode>(
            ("get_ID", id),
            ("get_SeriesID", seriesId)
        );

    private static IVideoCrossReference CrossReference(IShokoEpisode episode, int percentage = 100, int groupSize = 1) =>
        Proxy<IVideoCrossReference>(
            ("get_ShokoEpisode", episode),
            ("get_Percentage", percentage),
            ("get_PercentageGroupSize", groupSize)
        );

    private static IVideoFile Location(int managedFolderId, bool available, string path, string relativePath, long size = 0, bool throwOnSourceProbe = false, DropFolderType folderType = DropFolderType.Destination)
    {
        var folder = Proxy<IManagedFolder>(
            ("get_ID", managedFolderId),
            ("get_Name", $"Folder {managedFolderId}"),
            ("get_DropFolderType", folderType),
            ("get_Path", ""),
            ("get_AvailableFreeSpace", 0L),
            ("get_WatchForNewFiles", false)
        );
        var location = Proxy<IVideoFile>(
            ("get_ManagedFolderID", managedFolderId),
            ("get_IsAvailable", available),
            ("get_Path", path),
            ("get_RelativePath", relativePath),
            ("get_Size", size),
            ("get_ManagedFolder", folder)
        );
        if (throwOnSourceProbe)
        {
            var proxy = (StrictDispatchProxy)(object)location;
            proxy.ThrowingMembers.Add("get_IsAvailable");
            proxy.ThrowingMembers.Add("get_Path");
        }
        return location;
    }

    private static T Proxy<T>(params (string Name, object? Value)[] members)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictDispatchProxy>();
        ((StrictDispatchProxy)(object)proxy).Members = members.ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        return proxy;
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "shoko-relay-source-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private static string[] Names(IEnumerable<VirtualEntry> entries) => entries.Select(entry => entry.Name).ToArray();

    private static ShokoPathResolver Ready(SeriesData series)
    {
        var resolver = new ShokoPathResolver(new SingleDataSource(series), new RelayNamingStrategy());
        resolver.ReadDirectory("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
        return resolver;
    }

    private sealed class SingleDataSource(SeriesData series) : IShokoPathDataSource
    {
        public IReadOnlyList<SeriesData> GetAllSeries() => [series];
    }

    private class StrictDispatchProxy : DispatchProxy
    {
        internal Dictionary<string, object?> Members { get; set; } = new(StringComparer.Ordinal);
        internal List<string> Reads { get; } = [];
        internal HashSet<string> ThrowingMembers { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException("Missing proxy method.");
            Reads.Add(targetMethod.Name);
            if (ThrowingMembers.Contains(targetMethod.Name))
                throw new InvalidOperationException($"Unexpected source probe: {targetMethod.Name}");
            if (Members.TryGetValue(targetMethod.Name, out var value))
                return value;
            throw new InvalidOperationException($"Unexpected member read: {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
        }
    }
}
