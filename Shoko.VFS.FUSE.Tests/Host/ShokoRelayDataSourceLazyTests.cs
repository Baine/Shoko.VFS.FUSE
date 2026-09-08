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
        // One bare (lite) listing for the single movie group; none for the TV series.
        Assert.Equal(1, handler.LiteEpisodeFetches);

        var tv = Assert.Single(structure, s => s.SeriesId == TvSeriesId);
        Assert.False(tv.IsMovie);
        Assert.Empty(tv.Mappings);

        var movie = Assert.Single(structure, s => s.SeriesId == MovieSeriesId);
        Assert.True(movie.IsMovie);
        var placeholder = Assert.Single(movie.Mappings);
        Assert.Equal(0, placeholder.FileId);        // structure-only marker
        Assert.Equal(21, placeholder.EpisodeId);    // main episode → movie folder name
        Assert.True(placeholder.IsMain);
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
        var theme = ds.GetSeriesData(TvSeriesId)!.Extras!.Single();
        Assert.Equal("Theme.mp3", theme.Name);
        Assert.Equal(Path.Combine(_root, "Show", "Theme.mp3"), theme.SourcePath);
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

    private FakeShokoHandler CreateFixture(out ShokoRestClient client)
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
        var handler = new FakeShokoHandler(
            folders: [new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = _root, DropFolderType = DropFolderType.Both }],
            files: [tvFile, movieFile],
            series: [tvSeries, movieSeries],
            fullEpisodes: new()
            {
                [TvSeriesId] = [FullEpisode(1, TvSeriesId, 1007, tvFile)],
                [MovieSeriesId] = [FullEpisode(21, MovieSeriesId, 1009, movieFile)],
            },
            liteEpisodes: new()
            {
                [TvSeriesId] = [LiteEpisode(1, TvSeriesId)],
                [MovieSeriesId] = [LiteEpisode(21, MovieSeriesId)],
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        client = new ShokoRestClient(http, "http://test/");
        return handler;
    }

    private static ShokoEpisodeDto FullEpisode(int episodeId, int seriesId, int anidbAnimeId, FileDto file) => new()
    {
        IDs = new EpisodeIdsDto { ID = episodeId, ParentSeries = seriesId },
        Name = $"Episode {episodeId}",
        IsHidden = false,
        AniDB = new AnidbEpisodeDto { ID = episodeId, AnimeID = anidbAnimeId, Type = EpisodeType.Episode, EpisodeNumber = 1 },
        Files = [file],
    };

    // The lite endpoint carries no includeDataFrom blocks on the real server: bare IDs + number.
    private static ShokoEpisodeDto LiteEpisode(int episodeId, int seriesId) => new()
    {
        IDs = new EpisodeIdsDto { ID = episodeId, ParentSeries = seriesId },
        IndexNumber = 1,
        IsHidden = false,
    };

    private sealed class FakeShokoHandler : HttpMessageHandler
    {
        private readonly string _foldersJson;
        private readonly string _filesJson;
        private readonly string _seriesJson;
        private readonly Dictionary<int, string> _singleSeriesJson;
        private readonly Dictionary<int, string> _fullEpisodesJson;
        private readonly Dictionary<int, string> _liteEpisodesJson;
        private readonly ConcurrentQueue<string> _requests = new();

        public FakeShokoHandler(
            IReadOnlyList<ManagedFolderDto> folders,
            IReadOnlyList<FileDto> files,
            IReadOnlyList<ShokoSeriesDto> series,
            Dictionary<int, IReadOnlyList<ShokoEpisodeDto>> fullEpisodes,
            Dictionary<int, IReadOnlyList<ShokoEpisodeDto>> liteEpisodes)
        {
            _foldersJson = Serialize(folders);
            _filesJson = Serialize(new ListResult<FileDto> { Total = files.Count, List = files });
            _seriesJson = Serialize(new ListResult<ShokoSeriesDto> { Total = series.Count, List = series });
            _singleSeriesJson = series.ToDictionary(s => s.IDs.ID, s => Serialize(s));
            _fullEpisodesJson = fullEpisodes.ToDictionary(
                pair => pair.Key,
                pair => Serialize(new ListResult<ShokoEpisodeDto> { Total = pair.Value.Count, List = pair.Value }));
            _liteEpisodesJson = liteEpisodes.ToDictionary(
                pair => pair.Key,
                pair => Serialize(new ListResult<ShokoEpisodeDto>
                {
                    Total = pair.Value.Count,
                    // Simulate the real server: no AniDB/TMDB/Files without includeDataFrom.
                    List = pair.Value.Select(e => new ShokoEpisodeDto
                    {
                        IDs = e.IDs,
                        Name = e.Name,
                        IndexNumber = e.IndexNumber,
                        IsHidden = e.IsHidden,
                    }).ToList(),
                }));
        }

        public int FullEpisodeFetches => _requests.Count(r => r.Contains("/Episode") && r.Contains("includeDataFrom"));

        public int LiteEpisodeFetches => _requests.Count(r => r.Contains("/Episode") && !r.Contains("includeDataFrom"));

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
                body = query.Contains("includeDataFrom")
                    ? _fullEpisodesJson.GetValueOrDefault(id)
                    : _liteEpisodesJson.GetValueOrDefault(id);
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
