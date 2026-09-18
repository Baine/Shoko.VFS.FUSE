using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Api.Models;
using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// Verifies the host data source's lazy split: a cheap structure pass with no full
/// per-series episode fetches, per-series data fetched + cached + invalidated by id,
/// and snapshot save/load priming both caches without refetching warm series.
/// </summary>
public sealed class ShokoRelayDataSourceLazyTests : IDisposable
{
    private const int TvSeriesId = 7;
    private const int MovieSeriesId = 9;
    private const int ManagedFolderId = 1;

    private readonly string _root;

    public ShokoRelayDataSourceLazyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shoko-vfs-lazy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "Show"));
        Directory.CreateDirectory(Path.Combine(_root, "Film"));
        File.WriteAllText(Path.Combine(_root, "Show", "ep1.mkv"), "x");
        File.WriteAllText(Path.Combine(_root, "Show", "Theme.mp3"), "theme");
        File.WriteAllText(Path.Combine(_root, "Film", "movie.mkv"), "y");
        // The host mirrors upstream ResolveSourcePath: only files that exist on disk are
        // eligible, so every fixture location must be a real file.
        File.WriteAllText(Path.Combine(_root, "Film", "special.mkv"), "s");
        File.WriteAllText(Path.Combine(_root, "Film", "movie2.mkv"), "m2");
        File.WriteAllText(Path.Combine(_root, "Film", "movie_merged.mkv"), "mm");
        File.WriteAllText(Path.Combine(_root, "Film", "movie2-trailer.mkv"), "mt");
        Directory.CreateDirectory(Path.Combine(_root, "!AnimeThemes"));
        File.WriteAllText(Path.Combine(_root, "!AnimeThemes", "movie_themes.mkv"), "th");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void StructurePass_MakesNoFullPerSeriesFetches_AndAnchorsMovieFolders()
    {
        var handler = CreateFixture(out var client);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        Assert.Equal(0, handler.FullEpisodeFetches);
        // One AniDB-classified listing for the single movie group; none for the TV series.
        Assert.Equal(1, handler.AniDbEpisodeFetches);

        var tv = Assert.Single(structure, s => s.SeriesId == TvSeriesId);
        Assert.False(tv.IsMovie);
        Assert.Empty(tv.Mappings);

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.True(movie.IsMovie);
        // Relay-mirror: exactly the file-backed main episodes get a folder. The un-backed
        // main (22) and the file-backed special (24) must not produce one.
        var placeholder = Assert.Single(movie.Mappings);
        Assert.Equal(0, placeholder.FileId);        // structure-only marker
        Assert.Equal(21, placeholder.EpisodeId);    // main episode → movie folder name
        Assert.True(placeholder.IsMain);
    }

    [Fact]
    public void StructurePass_AnchorsEverySourcedMainEpisode_MirroringRelayTree()
    {
        var handler = CreateMultiMainFixture(out var client);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.True(movie.IsMovie);
        Assert.Equal(
            new[] { 21, 22 },
            movie.Mappings.Select(m => m.EpisodeId).Order().ToArray());
        Assert.All(movie.Mappings, m => Assert.Equal(0, m.FileId));
    }

    [Fact]
    public void StructurePass_MergedMultiPartMovieFile_AnchorsOnlyFirstEpisode()
    {
        var handler = CreateFixtureCore(out var client, addSpecial: true, mergedMovieFile: true);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.True(movie.IsMovie);
        // A single file xref'd to main episode 21 (#1) and part episode 25 (#2) anchors
        // only its first covered episode, matching upstream's per-mapping mainEpIds rule.
        var placeholder = Assert.Single(movie.Mappings);
        Assert.Equal(21, placeholder.EpisodeId);
        Assert.Equal(0, placeholder.FileId);
    }

    [Fact]
    public void StructurePass_HiddenFirstCoveredEpisode_IsNotAnchored()
    {
        // F5: the merged file xrefs hidden episode 20 (first) and visible 21 — upstream
        // filters hidden episodes out of mapping inputs, so the visible one anchors.
        CreateFixtureCore(out var client, addSpecial: true, mergedMovieFile: true, hiddenFirst: true);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.Equal(new[] { 21 }, movie.Mappings.Select(m => m.EpisodeId).ToArray());
    }

    [Fact]
    public void StructurePass_XrefOrderAgainstCoordinates_AnchorsEarliestCrossReferencedEpisode()
    {
        // Full-xref-priority parity: the merged file xrefs part episode 25 (#2) BEFORE
        // main episode 21 (#1). Upstream's DeduplicateByCoords moves the first
        // cross-reference to the front unconditionally, so 25 becomes the mapping
        // primary and gets the folder — a coordinate sort would have anchored 21 instead.
        CreateFixtureCore(out var client, addSpecial: true, mergedMovieFile: true, xrefFirst: true);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.Equal(new[] { 21, 25 }, movie.Mappings.Select(m => m.EpisodeId).Order().ToArray());
    }

    [Fact]
    public void StructurePass_MergedFileUnderIgnoredFeatureRoot_IsNotEligible()
    {
        // F7: upstream's ignore set includes the !AnimeThemes root; a merged file that
        // only lives there must not anchor its part episode.
        CreateFixtureCore(out var client, addSpecial: true, mergedMovieFile: true, mergedLocation: "!AnimeThemes/movie_themes.mkv");
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.Equal(new[] { 21 }, movie.Mappings.Select(m => m.EpisodeId).ToArray());
    }

    [Fact]
    public void StructurePass_InlineLocalExtraFile_IsNotEligible()
    {
        // F8: "movie2 -trailer.mkv" beside "movie2.mkv" is a Plex inline extra — upstream
        // IsPathIgnored drops it, so episode 22 keeps its "no sourced file" shape... but
        // here episode 21's real file still anchors its own folder.
        CreateFixtureCore(out var client, addSpecial: true, inlineExtraSecondMain: true);
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.Equal(new[] { 21 }, movie.Mappings.Select(m => m.EpisodeId).ToArray());
    }

    [Fact]
    public void StructurePass_FilesMissingOnDisk_DropSeriesNodesEntirely()
    {
        // F3 + F2: with the group's only sources deleted, upstream neither links nor
        // lists the series folder — the structure pass must drop the mapping-less shell.
        CreateFixture(out var client);
        File.Delete(Path.Combine(_root, "Show", "ep1.mkv"));
        File.Delete(Path.Combine(_root, "Film", "movie.mkv"));
        File.Delete(Path.Combine(_root, "Film", "special.mkv"));
        var ds = CreateDataSource(client);

        var structure = ds.GetSeriesStructure();

        Assert.Empty(structure);
    }

    [Fact]
    public void PerSeriesData_ExcludedLocationVideoStillCountsTowardPartIndices()
    {
        // F10: upstream keeps location-excluded videos in the mapping inputs (they count
        // toward the part split) and skips them only at link time.
        var nvfsDir = Path.Combine(_root, "Show", "NoVFS");
        Directory.CreateDirectory(nvfsDir);
        File.WriteAllText(Path.Combine(_root, "Show", "ep cd1.mkv"), "a");
        File.WriteAllText(Path.Combine(nvfsDir, "ep cd2.mkv"), "b");

        var fileA = new FileDto
        {
            ID = 40,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Show/ep cd1.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = TvSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 1 }],
            }],
        };
        var fileB = new FileDto
        {
            ID = 41,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Show/NoVFS/ep cd2.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = TvSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 1 }],
            }],
        };
        var episode = Episode(1, TvSeriesId, 1007, EpisodeType.Episode, fileA);
        episode.Files = [fileA, fileB];
        var tvSeries = new ShokoSeriesDto
        {
            IDs = new SeriesIdsDto { ID = TvSeriesId },
            Name = "Show",
            AniDB = new AnidbAnimeDto { ID = 1007, Type = AnimeType.TV },
        };
        var handler = new FakeShokoHandler(
            folders: [new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = _root, DropFolderType = DropFolderType.Both }],
            files: [fileA, fileB],
            series: [tvSeries],
            fullEpisodes: new() { [TvSeriesId] = [episode] },
            aniDbEpisodes: new() { [TvSeriesId] = [episode] });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/");
        var ds = new ShokoRelayDataSource(client, new RelayPathDataSourceOptions(ManagedFolderId, _root)
        {
            ManagedFolderName = "Import",
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
            FolderExclusions = "NoVFS",
        }, cacheTtl: TimeSpan.FromMinutes(5), maxDegree: 2);

        var data = ds.GetSeriesData(TvSeriesId);

        Assert.NotNull(data);
        var kept = Assert.Single(data!.Mappings, m => !string.IsNullOrEmpty(m.SourcePath));
        Assert.Equal(40, kept.FileId);
        Assert.Equal(1, kept.PartIndex);
        Assert.Equal(2, kept.PartCount);
        var excluded = Assert.Single(data.Mappings, m => string.IsNullOrEmpty(m.SourcePath));
        Assert.Equal(41, excluded.FileId);
        Assert.Equal(2, excluded.PartIndex);
    }

    [Fact]
    public void PerSeriesData_EpisodeTitlePrefersAniDbPreferredTitleOverDefaultName()
    {
        // Live API: the episode DTO's Name is the DEFAULT title; the preferred title
        // (override ?? preferred ?? default, = plugin's PreferredTitle) is AniDB.Title.
        var file = new FileDto
        {
            ID = 40,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Show/ep1.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = TvSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 1 }],
            }],
        };
        var episode = Episode(1, TvSeriesId, 1007, EpisodeType.Episode, file);
        episode.Name = "Digimon Adventure";
        episode.AniDB!.Title = "US Chopjob";
        var tvSeries = new ShokoSeriesDto
        {
            IDs = new SeriesIdsDto { ID = TvSeriesId },
            Name = "Show",
            AniDB = new AnidbAnimeDto { ID = 1007, Type = AnimeType.TV },
        };
        var handler = new FakeShokoHandler(
            folders: [new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = _root, DropFolderType = DropFolderType.Both }],
            files: [file],
            series: [tvSeries],
            fullEpisodes: new() { [TvSeriesId] = [episode] },
            aniDbEpisodes: new() { [TvSeriesId] = [episode] });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/");
        var ds = new ShokoRelayDataSource(client, new RelayPathDataSourceOptions(ManagedFolderId, _root)
        {
            ManagedFolderName = "Import",
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
        }, cacheTtl: TimeSpan.FromMinutes(5), maxDegree: 2);

        var data = ds.GetSeriesData(TvSeriesId);

        var mapping = Assert.Single(data!.Mappings);
        Assert.Equal("US Chopjob", mapping.EpisodeTitle);
    }

    [Fact]
    public void PerSeriesData_ExtraInForeignManagedFolder_ResolvesServerTranslatedRoot()
    {
        // Live parity case: the extra's ONLY location is in another managed folder whose
        // REST root is SERVER-side (untranslated); the fallback must apply the
        // ServerPathRoot→ManagedFolderPathRoot prefix swap before the file-exists check.
        Directory.CreateDirectory(Path.Combine(_root, "host", "GerSub"));
        File.WriteAllText(Path.Combine(_root, "host", "GerSub", "extra.mkv"), "e");
        var file = new FileDto
        {
            ID = 60,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = 2, RelativePath = "/extra.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = TvSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 2 }],
            }],
        };
        var special = Episode(2, TvSeriesId, 1007, EpisodeType.Special, file);
        var tvSeries = new ShokoSeriesDto
        {
            IDs = new SeriesIdsDto { ID = TvSeriesId },
            Name = "Show",
            AniDB = new AnidbAnimeDto { ID = 1007, Type = AnimeType.TV },
        };
        var handler = new FakeShokoHandler(
            folders:
            [
                new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = _root, DropFolderType = DropFolderType.Both },
                new ManagedFolderDto { ID = 2, Name = "GerSub", Path = Path.Combine(_root, "srv", "GerSub"), DropFolderType = DropFolderType.Both },
            ],
            files: [file],
            series: [tvSeries],
            fullEpisodes: new() { [TvSeriesId] = [special] },
            aniDbEpisodes: new() { [TvSeriesId] = [special] });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/");
        var ds = new ShokoRelayDataSource(client, new RelayPathDataSourceOptions(ManagedFolderId, _root)
        {
            ManagedFolderName = "Import",
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
            ServerPathRoot = Path.Combine(_root, "srv"),
            ManagedFolderPathRoot = Path.Combine(_root, "host"),
        }, cacheTtl: TimeSpan.FromMinutes(5), maxDegree: 2);

        var data = ds.GetSeriesData(TvSeriesId);

        var mapping = Assert.Single(data!.Mappings);
        Assert.Equal(Path.Combine(_root, "host", "GerSub", "extra.mkv"), mapping.SourcePath);
    }

    [Fact]
    public void PerSeriesData_FetchedLazily_Cached_AndInvalidatedById()
    {
        var handler = CreateFixture(out var client);
        var ds = CreateDataSource(client);
        ds.GetSeriesStructure();

        var data = ds.GetSeriesData(TvSeriesId);
        Assert.NotNull(data);
        var mapping = Assert.Single(data!.Mappings);
        Assert.Equal(10, mapping.FileId);
        Assert.Equal(Path.Combine(_root, "Show", "ep1.mkv"), mapping.SourcePath);
        Assert.Equal(1, handler.FullEpisodeFetches);

        // Cached: repeat access does not refetch.
        ds.GetSeriesData(TvSeriesId);
        Assert.Equal(1, handler.FullEpisodeFetches);

        // Per-series invalidation drops only that series' cache entry.
        ds.Invalidate(TvSeriesId);
        Assert.NotNull(ds.GetSeriesData(TvSeriesId));
        Assert.Equal(2, handler.FullEpisodeFetches);
        ds.GetSeriesData(MovieSeriesId);
        Assert.Equal(3, handler.FullEpisodeFetches);
        ds.Invalidate(MovieSeriesId);
        ds.GetSeriesData(TvSeriesId);
        Assert.Equal(3, handler.FullEpisodeFetches);

        // Extras come with the per-series data, not the structure pass.
        var movie = ds.GetSeriesData(MovieSeriesId);
        Assert.NotNull(movie);
        Assert.Equal(4, handler.FullEpisodeFetches);
        var theme = Assert.Single(ds.GetSeriesData(TvSeriesId)!.Mappings.SelectMany(mapping => mapping.SeriesAssets ?? []));
        Assert.Equal("Theme.mp3", theme.Name);
        Assert.Equal(Path.Combine(_root, "Show", "Theme.mp3"), theme.SourcePath);
    }

    [Fact]
    public void PerSeriesData_DiscoversLocalAssetsNextToSourceFiles()
    {
        // Local assets next to the indexed episode: sidecar, series artwork, inline extra.
        File.WriteAllText(Path.Combine(_root, "Show", "ep1.en.srt"), "sub");
        File.WriteAllBytes(Path.Combine(_root, "Show", "poster.jpg"), new byte[2]);
        File.WriteAllText(Path.Combine(_root, "Show", "ep1-trailer.mkv"), "trailer");
        var handler = CreateFixture(out var client);
        var ds = CreateDataSource(client);
        ds.GetSeriesStructure();

        var data = ds.GetSeriesData(TvSeriesId);

        Assert.NotNull(data);
        var mapping = Assert.Single(data!.Mappings);
        Assert.Equal(
            Path.Combine(_root, "Show", "ep1.en.srt"),
            Assert.Single(mapping.Sidecars ?? [], sidecar => sidecar.Suffix == ".en.srt").SourcePath);
        Assert.Equal(
            Path.Combine(_root, "Show", "poster.jpg"),
            Assert.Single(mapping.SeriesAssets ?? [], asset => asset.Name == "poster.jpg").SourcePath);
        // Inline Plex extras are excluded from mapping eligibility (F8/F10) but resurface as
        // inline-local-extra entries beside the parent video (LinkLocalExtras TV pass).
        Assert.Equal(
            Path.Combine(_root, "Show", "ep1-trailer.mkv"),
            Assert.Single(mapping.InlineLocalExtras ?? [], inline => inline.Suffix == "-trailer.mkv").SourcePath);
        Assert.Equal(1, handler.FullEpisodeFetches);
    }

    [Fact]
    public void SnapshotRoundTrip_PrimesStructureAndWarmSeries_WithoutRefetch()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shoko-vfs-lazy-snap-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSnapshotStore(dir);
            var handler = CreateFixture(out var client);
            var first = CreateDataSource(client, store, "m1");
            first.GetSeriesStructure();
            Assert.NotNull(first.GetSeriesData(TvSeriesId)); // warm exactly one series
            first.SaveToStore();

            var second = CreateDataSource(client, store, "m1");
            Assert.True(second.TryLoadFromStore());
            int fetchesAfterSave = handler.FullEpisodeFetches;

            // Warm series serves from the loaded snapshot: zero refetches.
            var restored = second.GetSeriesData(TvSeriesId);
            Assert.NotNull(restored);
            Assert.Single(restored!.Mappings);
            Assert.Equal(fetchesAfterSave, handler.FullEpisodeFetches);

            // Structure is restored too (movie placeholder intact, no episode fetches).
            var structure = second.GetSeriesStructure();
            Assert.Equal(2, structure.Count);
            Assert.Contains(structure, s => s.SeriesId == MovieSeriesId && s.Mappings.Any(m => m.FileId == 0));
            Assert.Equal(fetchesAfterSave, handler.FullEpisodeFetches);

            // The combined view exposes warm full entries alongside structure entries.
            var combined = second.GetAllSeries();
            Assert.Contains(combined, s => s.SeriesId == TvSeriesId && s.Mappings.Any(m => m.FileId > 0));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private ShokoRelayDataSource CreateDataSource(ShokoRestClient client, FileSnapshotStore? store = null, string? key = null) =>
        new(client, new RelayPathDataSourceOptions(ManagedFolderId, _root)
        {
            ManagedFolderName = "Import",
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
            TmdbEpNumbering = false,
            MergeTmdbSeries = false,
        }, cacheTtl: TimeSpan.FromMinutes(5), maxDegree: 2, snapshotStore: store, snapshotKey: key);

    private FakeShokoHandler CreateFixture(out ShokoRestClient client) =>
        CreateFixtureCore(out client, addSpecial: true);

    private FakeShokoHandler CreateMultiMainFixture(out ShokoRestClient client)
    {
        var handler = CreateFixtureCore(out client, addSpecial: true, secondMain: true);
        return handler;
    }

    private FakeShokoHandler CreateFixtureCore(
        out ShokoRestClient client,
        bool addSpecial,
        bool secondMain = false,
        bool mergedMovieFile = false,
        string mergedLocation = "Film/movie_merged.mkv",
        bool hiddenFirst = false,
        bool xrefFirst = false,
        bool inlineExtraSecondMain = false)
    {
        var tvFile = new FileDto
        {
            ID = 10,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Show/ep1.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = TvSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 1 }],
            }],
        };
        var movieFile = new FileDto
        {
            ID = 12,
            Size = 1,
            Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Film/movie.mkv" }],
            SeriesIDs = [new FileCrossRefGroupDto
            {
                SeriesID = new FileSeriesIdsDto { ID = MovieSeriesId },
                EpisodeIDs = [new FileEpisodeIdsDto { ID = 21 }],
            }],
        };
        var files = new List<FileDto> { tvFile, movieFile };
        if (addSpecial)
        {
            files.Add(new FileDto
            {
                ID = 14,
                Size = 1,
                Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = "Film/special.mkv" }],
                SeriesIDs = [new FileCrossRefGroupDto
                {
                    SeriesID = new FileSeriesIdsDto { ID = MovieSeriesId },
                    EpisodeIDs = [new FileEpisodeIdsDto { ID = 24 }],
                }],
            });
        }
        if (secondMain || inlineExtraSecondMain)
        {
            // Episode 22 backed by its own file → its own folder (each file anchors its
            // own primary). The inline-extra variant sits next to "movie2.mkv" with a Plex
            // extra suffix, which upstream IsPathIgnore drops as a source.
            files.Add(new FileDto
            {
                ID = 16,
                Size = 1,
                Locations = [new FileLocationDto
                {
                    ManagedFolderID = ManagedFolderId,
                    RelativePath = inlineExtraSecondMain ? "Film/movie2-trailer.mkv" : "Film/movie2.mkv",
                }],
                SeriesIDs = [new FileCrossRefGroupDto
                {
                    SeriesID = new FileSeriesIdsDto { ID = MovieSeriesId },
                    EpisodeIDs = [new FileEpisodeIdsDto { ID = 22 }],
                }],
            });
        }
        FileDto? mergedFile = null;
        if (mergedMovieFile)
        {
            // One merged file covering the main episode (21, #1) AND a part episode:
            // upstream creates a single folder anchored on the mapping primary. hiddenFirst
            // adds a hidden lower-id episode as covered id (must not anchor); xrefFirst
            // lists the part episode (#2) BEFORE the main (#1) in the xref order,
            // discriminating xref priority from coordinate order.
            var covered = hiddenFirst
                ? new[] { 21, 20 }
                : xrefFirst
                    ? new[] { 25, 21 }
                    : new[] { 21, 25 };
            mergedFile = new FileDto
            {
                ID = 18,
                Size = 1,
                Locations = [new FileLocationDto { ManagedFolderID = ManagedFolderId, RelativePath = mergedLocation }],
                SeriesIDs = [new FileCrossRefGroupDto
                {
                    SeriesID = new FileSeriesIdsDto { ID = MovieSeriesId },
                    EpisodeIDs = [.. covered.Select(id => new FileEpisodeIdsDto { ID = id })],
                }],
            };
            files.Add(mergedFile);
        }

        var tvSeries = new ShokoSeriesDto
        {
            IDs = new SeriesIdsDto { ID = TvSeriesId },
            Name = "Show",
            AniDB = new AnidbAnimeDto { ID = 1007, Type = AnimeType.TV },
        };
        var movieSeries = new ShokoSeriesDto
        {
            IDs = new SeriesIdsDto { ID = MovieSeriesId },
            Name = "Film",
            AniDB = new AnidbAnimeDto { ID = 1009, Type = AnimeType.Movie },
        };

        var tvEpisodes = new List<ShokoEpisodeDto> { Episode(1, TvSeriesId, 1007, EpisodeType.Episode, tvFile) };
        // Main episode backed by a file (folder anchor), plus edge cases:
        // 22 = main episode without any file in the folder → no folder (relay parity),
        // 24 = file-backed special → no folder (relay parity).
        // secondMain makes 22 file-backed → folder (multi-movie mirror parity).
        var movieEpisodes = new List<ShokoEpisodeDto>
        {
            Episode(21, MovieSeriesId, 1009, EpisodeType.Episode, movieFile),
            Episode(22, MovieSeriesId, 1009, EpisodeType.Episode, secondMain || inlineExtraSecondMain ? files[3] : null),
        };
        if (addSpecial)
            movieEpisodes.Add(Episode(24, MovieSeriesId, 1009, EpisodeType.Special, files[2]));
        if (hiddenFirst)
            movieEpisodes.Add(Episode(20, MovieSeriesId, 1009, EpisodeType.Episode, mergedFile, hidden: true));
        if (mergedMovieFile)
            movieEpisodes.Add(Episode(25, MovieSeriesId, 1009, EpisodeType.Episode, mergedFile, episodeNumber: 2));

        var handler = new FakeShokoHandler(
            folders: [new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = _root, DropFolderType = DropFolderType.Both }],
            files: files,
            series: [tvSeries, movieSeries],
            fullEpisodes: new()
            {
                [TvSeriesId] = tvEpisodes,
                [MovieSeriesId] = movieEpisodes,
            },
            aniDbEpisodes: new()
            {
                [TvSeriesId] = tvEpisodes,
                [MovieSeriesId] = movieEpisodes,
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        client = new ShokoRestClient(http, "http://test/");
        return handler;
    }

    private static ShokoEpisodeDto Episode(int episodeId, int seriesId, int anidbAnimeId, EpisodeType type, FileDto? file, int episodeNumber = 1, bool hidden = false) => new()
    {
        IDs = new EpisodeIdsDto { ID = episodeId, ParentSeries = seriesId },
        Name = $"Episode {episodeId}",
        IsHidden = hidden,
        AniDB = new AnidbEpisodeDto { ID = episodeId, AnimeID = anidbAnimeId, Type = type, EpisodeNumber = episodeNumber },
        Files = file is null ? [] : [file],
    };

    private sealed class FakeShokoHandler : HttpMessageHandler
    {
        private readonly string _foldersJson;
        private readonly string _filesJson;
        private readonly string _seriesJson;
        private readonly Dictionary<int, string> _singleSeriesJson;
        private readonly Dictionary<int, string> _fullEpisodesJson;
        private readonly Dictionary<int, string> _aniDbEpisodesJson;
        private readonly ConcurrentQueue<string> _requests = new();

        public FakeShokoHandler(
            IReadOnlyList<ManagedFolderDto> folders,
            IReadOnlyList<FileDto> files,
            IReadOnlyList<ShokoSeriesDto> series,
            Dictionary<int, IReadOnlyList<ShokoEpisodeDto>> fullEpisodes,
            Dictionary<int, IReadOnlyList<ShokoEpisodeDto>> aniDbEpisodes)
        {
            _foldersJson = Serialize(folders);
            _filesJson = Serialize(new ListResult<FileDto> { Total = files.Count, List = files });
            _seriesJson = Serialize(new ListResult<ShokoSeriesDto> { Total = series.Count, List = series });
            _singleSeriesJson = series.ToDictionary(s => s.IDs.ID, s => Serialize(s));
            _fullEpisodesJson = fullEpisodes.ToDictionary(
                pair => pair.Key,
                pair => Serialize(new ListResult<ShokoEpisodeDto> { Total = pair.Value.Count, List = pair.Value }));
            _aniDbEpisodesJson = aniDbEpisodes.ToDictionary(
                pair => pair.Key,
                pair => Serialize(new ListResult<ShokoEpisodeDto>
                {
                    Total = pair.Value.Count,
                    // Simulate the AniDB-only server response: type/number, no Files/XRefs/TMDB.
                    List = pair.Value.Select(e => new ShokoEpisodeDto
                    {
                        IDs = e.IDs,
                        Name = e.Name,
                        IndexNumber = e.IndexNumber,
                        IsHidden = e.IsHidden,
                        AniDB = e.AniDB,
                    }).ToList(),
                }));
        }

        public int FullEpisodeFetches => _requests.Count(r => r.Contains("/Episode") && r.Contains("includeFiles"));

        public int AniDbEpisodeFetches => _requests.Count(r => r.Contains("/Episode") && r.Contains("includeDataFrom=AniDB") && !r.Contains("includeFiles"));

        private static string Serialize(object value) => JsonConvert.SerializeObject(value);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";
            _requests.Enqueue(path + "?" + query);

            string? body = null;
            if (path.EndsWith("/api/v3/ManagedFolder"))
                body = _foldersJson;
            else if (Regex.IsMatch(path, @"/api/v3/ManagedFolder/\d+/File$"))
                body = _filesJson;
            else if (Regex.IsMatch(path, @"/api/v3/Series/\d+/Episode$"))
            {
                int id = int.Parse(Regex.Match(path, @"/api/v3/Series/(\d+)/").Groups[1].Value);
                body = query.Contains("includeFiles")
                    ? _fullEpisodesJson.GetValueOrDefault(id)
                    : _aniDbEpisodesJson.GetValueOrDefault(id);
            }
            else if (Regex.IsMatch(path, @"/api/v3/Series/\d+$"))
            {
                int id = int.Parse(Regex.Match(path, @"/api/v3/Series/(\d+)$").Groups[1].Value);
                body = _singleSeriesJson.GetValueOrDefault(id);
            }
            else if (path.EndsWith("/api/v3/Series"))
                body = query.Contains("page=1") ? _seriesJson : "{\"Total\":0,\"List\":[]}";

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"not found: {path}{query}", Encoding.UTF8, "text/plain") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
