using System.Reflection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

/// <summary>
/// Covers the lazy split: structure pass without episode materialization,
/// per-series fetch/caching/invalidation, and group→primary id remapping.
/// </summary>
public sealed class RelayShokoPathDataSourceLazyTests
{
    [Fact]
    public void GetSeriesStructure_DoesNotMaterializeTvEpisodes_ButKeepsOneMovieMapping()
    {
        string root = NewDirectory();
        try
        {
            string tvFile = WriteFile(root, "tv/ep1.mkv");
            string movieFile = WriteFile(root, "movie/main.mkv");
            var (tv, _) = TvSeries(1, 11, tvFile, "/tv/ep1.mkv");
            var (movie, _) = MovieSeries(2, 21, 2001, movieFile, "/movie/main.mkv");
            var (remote, _) = TvSeries(3, 31, "/elsewhere/ep.mkv", "/elsewhere/ep.mkv", managedFolderId: 99);
            var metadata = new FakeMetadata([tv, movie, remote]);
            var videoService = VideoService(
                Place(7, tvFile, "/tv/ep1.mkv", series: tv),
                Place(7, movieFile, "/movie/main.mkv", series: movie));

            var dataSource = new RelayShokoPathDataSource(
                metadata.Proxy,
                new RelayPathDataSourceOptions(7, root),
                videoService.Proxy);

            var structure = dataSource.GetSeriesStructure();

            var tvEntry = Assert.Single(structure, entry => entry.SeriesId == 1);
            Assert.False(tvEntry.IsMovie);
            Assert.Empty(tvEntry.Mappings);
            Assert.Empty(tvEntry.Extras ?? []);

            var movieEntry = Assert.Single(structure, entry => entry.SeriesId == 2);
            Assert.True(movieEntry.IsMovie);
            var mapping = Assert.Single(movieEntry.Mappings);
            Assert.Equal(2001, mapping.EpisodeId);
            Assert.Equal(Path.Combine(root, "movie/main.mkv"), mapping.SourcePath);
            Assert.True(mapping.IsMain);

            // The TV series graph was never walked past series-level scalars,
            // and the unseeded (remote-files-only) series is excluded entirely.
            Assert.DoesNotContain("get_Episodes", ((CountingProxy)(object)tv).Reads);
            Assert.DoesNotContain("get_Episodes", ((CountingProxy)(object)remote).Reads);

            // The movie structure entry needs its episode graph once; no per-series ID loads.
            Assert.Contains("get_Episodes", ((CountingProxy)(object)movie).Reads);
            Assert.Equal(0, metadata.GetByIdCalls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void GetSeriesData_FetchesLazily_Caches_AndInvalidateRefetches()
    {
        string root = NewDirectory();
        try
        {
            string sourcePath = WriteFile(root, "tv/ep1.mkv");
            WriteFile(root, "tv/Theme.mp3");
            var (tv, _) = TvSeries(1, 11, sourcePath, "/tv/ep1.mkv");
            var metadata = new FakeMetadata([tv]);
            var dataSource = new RelayShokoPathDataSource(
                metadata.Proxy,
                new RelayPathDataSourceOptions(7, root),
                VideoService(Place(7, sourcePath, "/tv/ep1.mkv", series: tv)).Proxy);

            // Structure alone never resolves per-series data or theme extras.
            var structure = dataSource.GetSeriesStructure();
            Assert.Null(Assert.Single(structure, entry => entry.SeriesId == 1).Extras);
            Assert.Equal(0, metadata.GetByIdCalls);

            var first = dataSource.GetSeriesData(1);
            Assert.NotNull(first);
            Assert.NotEmpty(first!.Mappings);
            var extra = Assert.Single(first.Extras!);
            Assert.Equal("Theme.mp3", extra.Name);
            Assert.Equal(Path.Combine(root, "tv", "Theme.mp3"), extra.SourcePath);
            Assert.Equal(1, metadata.GetByIdCalls);

            // Cached until invalidated.
            Assert.Same(first, dataSource.GetSeriesData(1));
            Assert.Equal(1, metadata.GetByIdCalls);

            dataSource.Invalidate(1);
            Assert.NotNull(dataSource.GetSeriesData(1));
            Assert.Equal(2, metadata.GetByIdCalls);

            dataSource.Invalidate(null);
            Assert.NotNull(dataSource.GetSeriesData(1));
            Assert.Equal(3, metadata.GetByIdCalls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void GetSeriesData_ServesMergedGroupAtPrimaryId_AndMemberInvalidateDropsIt()
    {
        string root = NewDirectory();
        try
        {
            string firstPath = WriteFile(root, "first/ep1.mkv");
            string secondPath = WriteFile(root, "second/ep1.mkv");
            var (first, _) = TvSeries(1, 11, firstPath, "/first/ep1.mkv", anidbId: 500);
            var (second, _) = TvSeries(2, 12, secondPath, "/second/ep1.mkv", anidbId: 501);
            var metadata = new FakeMetadata([first, second]);
            var dataSource = new RelayShokoPathDataSource(
                metadata.Proxy,
                new RelayPathDataSourceOptions(7, root) { ManualOverrideGroups = [[500, 501]] },
                VideoService(
                    Place(7, firstPath, "/first/ep1.mkv", series: first),
                    Place(7, secondPath, "/second/ep1.mkv", series: second)).Proxy);

            var structure = dataSource.GetSeriesStructure();
            var group = Assert.Single(structure);
            Assert.Equal(1, group.SeriesId);
            Assert.Equal(1, dataSource.MapToPrimarySeriesId(1));
            Assert.Equal(1, dataSource.MapToPrimarySeriesId(2));
            Assert.Equal(42, dataSource.MapToPrimarySeriesId(42));

            var merged = dataSource.GetSeriesData(1);
            Assert.NotNull(merged);
            Assert.Equal(new[] { 11, 12 }, merged!.Mappings.Select(mapping => mapping.FileId).Order().ToArray());
            Assert.Equal(2, metadata.GetByIdCalls);

            // A content event naming the member series must drop the primary-keyed cache.
            dataSource.Invalidate(2);
            Assert.NotNull(dataSource.GetSeriesData(1));
            Assert.Equal(4, metadata.GetByIdCalls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void GetSeriesData_ForUnknownSeries_CachesNullWithoutThrowing()
    {
        string root = NewDirectory();
        try
        {
            var dataSource = new RelayShokoPathDataSource(
                new FakeMetadata([]).Proxy,
                new RelayPathDataSourceOptions(7, root),
                VideoService().Proxy);

            Assert.Null(dataSource.GetSeriesData(999));
            Assert.Null(dataSource.GetSeriesData(999));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private sealed class FakeMetadata
    {
        private readonly Dictionary<int, IShokoSeries> _byId;

        public FakeMetadata(IReadOnlyList<IShokoSeries> series)
        {
            _byId = series.ToDictionary(item => item.ID);
            All = series;
        }

        public IReadOnlyList<IShokoSeries> All { get; }
        public int GetByIdCalls { get; private set; }

        public IMetadataService Proxy
        {
            get
            {
                var proxy = DispatchProxy.Create<IMetadataService, MetadataProxy>();
                ((MetadataProxy)(object)proxy).Owner = this;
                return proxy;
            }
        }

        public IShokoSeries? Load(int id)
        {
            GetByIdCalls++;
            return _byId.GetValueOrDefault(id);
        }

        private class MetadataProxy : DispatchProxy
        {
            internal FakeMetadata Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
            {
                "GetAllShokoSeries" => Owner.All,
                "GetShokoSeriesByID" => Owner.Load((int)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected metadata call: {method?.Name}"),
            };
        }
    }

    private sealed class FakeVideoService
    {
        private readonly Dictionary<int, List<IVideoFile>> _placesByFolderId = new();
        private readonly List<IManagedFolder> _folders = [];

        public IVideoService Proxy
        {
            get
            {
                var proxy = DispatchProxy.Create<IVideoService, VideoProxy>();
                ((VideoProxy)(object)proxy).Owner = this;
                return proxy;
            }
        }

        public FakeVideoService AddPlace(int folderId, IVideoFile place)
        {
            if (!_folders.Any(folder => folder.ID == folderId))
            {
                _folders.Add(Proxy<IManagedFolder>(
                    ("get_ID", folderId),
                    ("get_Name", $"Folder {folderId}"),
                    ("get_Path", ""),
                    ("get_DropFolderType", DropFolderType.Destination)));
            }

            if (!_placesByFolderId.TryGetValue(folderId, out var places))
                _placesByFolderId[folderId] = places = [];
            places.Add(place);
            return this;
        }

        private class VideoProxy : DispatchProxy
        {
            internal FakeVideoService Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
            {
                "GetAllManagedFolders" => Owner._folders,
                "GetVideoFilesInManagedFolder" => Owner._placesByFolderId.GetValueOrDefault(((IManagedFolder)args![0]!).ID) ?? [],
                _ => throw new InvalidOperationException($"Unexpected video service call: {method?.Name}"),
            };
        }
    }

    private static FakeVideoService VideoService(params IVideoFile[] places)
    {
        var service = new FakeVideoService();
        foreach (var place in places)
            service.AddPlace((int)((CountingProxy)(object)place).Members["get_ManagedFolderID"]!, place);
        return service;
    }

    private static (IShokoSeries Series, List<string> Reads) TvSeries(
        int id,
        int videoId,
        string path,
        string relativePath,
        int managedFolderId = 7,
        int? anidbId = null) =>
        SeriesWithFiles(id, anidbId ?? id, [], Episode(1000 + id, EpisodeType.Episode, 1, 1, Video(videoId, path, relativePath, managedFolderId)));

    private static (IShokoSeries Series, List<string> Reads) MovieSeries(
        int id,
        int videoId,
        int episodeId,
        string path,
        string relativePath,
        int managedFolderId = 7) =>
        SeriesWithFiles(id, id, [Proxy<ITmdbMovie>()], Episode(episodeId, EpisodeType.Episode, 1, 1, Video(videoId, path, relativePath, managedFolderId)));

    private static (IShokoSeries Series, List<string> Reads) SeriesWithFiles(
        int id,
        int anidbId,
        IReadOnlyList<ITmdbMovie> tmdbMovies,
        IShokoEpisode episode)
    {
        var series = Proxy<IShokoSeries>(
            ("get_ID", id),
            ("get_Type", AnimeType.TV),
            ("get_PreferredTitle", null),
            ("get_Titles", Array.Empty<ITitle>()),
            ("get_AnidbAnimeID", anidbId),
            ("get_AirDate", null),
            ("get_TmdbShows", Array.Empty<ITmdbShow>()),
            ("get_TmdbMovies", tmdbMovies),
            ("get_Episodes", (IReadOnlyList<IShokoEpisode>)[episode])
        );
        return (series, ((CountingProxy)(object)series).Reads);
    }

    private static IShokoEpisode Episode(int id, EpisodeType type, int number, int? season, IVideo video) =>
        Proxy<IShokoEpisode>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_EpisodeNumber", number),
            ("get_SeasonNumber", season),
            ("get_PreferredTitle", null),
            ("get_Titles", Array.Empty<ITitle>()),
            ("get_TmdbEpisodes", Array.Empty<ITmdbEpisode>()),
            ("get_IsHidden", false),
            ("get_Videos", (IReadOnlyList<IVideo>)[video])
        );

    private static IVideo Video(int id, string path, string relativePath, int managedFolderId) =>
        Proxy<IVideo>(
            ("get_ID", id),
            ("get_IsVariation", false),
            ("get_Files", (IReadOnlyList<IVideoFile>)[Place(managedFolderId, path, relativePath)]),
            ("get_CrossReferences", Array.Empty<IVideoCrossReference>())
        );

    private static IVideoFile Place(int managedFolderId, string path, string relativePath, IShokoSeries? series = null)
    {
        IVideo linkedVideo = Proxy<IVideo>(
            ("get_ID", 9000 + Math.Abs(relativePath.GetHashCode())),
            ("get_Series", (IReadOnlyList<IShokoSeries>)(series is null ? [] : [series])));
        return Proxy<IVideoFile>(
            ("get_ManagedFolderID", managedFolderId),
            ("get_IsAvailable", true),
            ("get_Path", path),
            ("get_RelativePath", relativePath),
            ("get_Size", 10L),
            ("get_ManagedFolder", Proxy<IManagedFolder>(
                ("get_ID", managedFolderId),
                ("get_Name", $"Folder {managedFolderId}"),
                ("get_DropFolderType", DropFolderType.Destination))),
            ("get_Video", linkedVideo)
        );
    }

    private static T Proxy<T>(params (string Name, object? Value)[] members)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, CountingProxy>();
        ((CountingProxy)(object)proxy).Members = members.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        return proxy;
    }

    private class CountingProxy : DispatchProxy
    {
        internal Dictionary<string, object?> Members { get; set; } = new(StringComparer.Ordinal);
        internal List<string> Reads { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException("Missing proxy method.");
            Reads.Add(targetMethod.Name);
            if (Members.TryGetValue(targetMethod.Name, out var value))
                return value;
            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                return targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            throw new InvalidOperationException($"Unexpected member read: {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "shoko-relay-lazy-test-" + Guid.NewGuid().ToString("N"));
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
}
