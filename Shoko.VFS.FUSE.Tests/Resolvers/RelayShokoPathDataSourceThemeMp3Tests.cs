using System.Reflection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

/// <summary>
/// Verifies that AnimeThemes Theme.mp3 files are surfaced as extras by the in-tree
/// <see cref="RelayShokoPathDataSource"/> (uses <see cref="IMetadataService"/>).
/// </summary>
public sealed class RelayShokoPathDataSourceThemeMp3Tests
{
    [Fact]
    public void ThemeMp3SurfacedAsSeriesExtra()
    {
        string root = NewDirectory();
        try
        {
            string seriesSubdir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(seriesSubdir);
            string themePath = Path.Combine(seriesSubdir, "Theme.mp3");
            File.WriteAllBytes(themePath, new byte[16]);
            string epPath = WriteFile(root, Path.Combine("Some Series", "ep1.mkv"));

            var file = Location(7, true, epPath, "/Some Series/ep1.mkv");
            var video = Video(1, new[] { file });
            var episode = Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, video);
            var series = Series(55, AnimeType.TV, "Test", episode);

            var ds = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root));
            var all = ds.GetAllSeries();
            Assert.Single(all);
            var s = all[0];

            Assert.NotNull(s.Extras);
            var extra = Assert.Single(s.Extras!);
            Assert.Equal("Theme.mp3", extra.Name);
            Assert.Equal(themePath, extra.SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ThemeMp3NoFileReturnsNoExtras()
    {
        string root = NewDirectory();
        try
        {
            string epPath = WriteFile(root, Path.Combine("Some Series", "ep1.mkv"));

            var file = Location(7, true, epPath, "/Some Series/ep1.mkv");
            var video = Video(1, new[] { file });
            var episode = Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, video);
            var series = Series(55, AnimeType.TV, "Test", episode);

            var ds = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root));
            var all = ds.GetAllSeries();
            Assert.Single(all);
            Assert.Null(all[0].Extras);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ThemeMp3SkipsAlreadyProbedFolders()
    {
        string root = NewDirectory();
        try
        {
            string seriesSubdir = Path.Combine(root, "Series A");
            Directory.CreateDirectory(seriesSubdir);
            string themePath = Path.Combine(seriesSubdir, "Theme.mp3");
            File.WriteAllBytes(themePath, new byte[8]);

            // Two files in the same series folder — should probe only once.
            string ep1 = WriteFile(root, Path.Combine("Series A", "ep1.mkv"));
            string ep2 = WriteFile(root, Path.Combine("Series A", "ep2.mkv"));

            var file1 = Location(7, true, ep1, "/Series A/ep1.mkv");
            var file2 = Location(7, true, ep2, "/Series A/ep2.mkv");
            var video = Video(1, new[] { file1, file2 });
            var episode = Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, video);
            var series = Series(55, AnimeType.TV, "Test", episode);

            var ds = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root));
            var all = ds.GetAllSeries();
            Assert.Single(all);
            var extras = all[0].Extras;
            Assert.NotNull(extras);
            Assert.Single(extras!);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ThemeMp3SeparateSeriesGetOwnExtras()
    {
        string root = NewDirectory();
        try
        {
            // Series A has Theme.mp3
            string aDir = Path.Combine(root, "Series A");
            Directory.CreateDirectory(aDir);
            File.WriteAllBytes(Path.Combine(aDir, "Theme.mp3"), new byte[4]);
            string epA = WriteFile(root, Path.Combine("Series A", "ep1.mkv"));

            // Series B also has Theme.mp3
            string bDir = Path.Combine(root, "Series B");
            Directory.CreateDirectory(bDir);
            File.WriteAllBytes(Path.Combine(bDir, "Theme.mp3"), new byte[8]);
            string epB = WriteFile(root, Path.Combine("Series B", "ep1.mkv"));

            var fileA = Location(7, true, epA, "/Series A/ep1.mkv");
            var videoA = Video(1, new[] { fileA });
            var ep1A = Episode(101, EpisodeType.Episode, 1, null, "Ep A1", false, videoA);
            var seriesA = Series(55, AnimeType.TV, "Test A", ep1A);

            var fileB = Location(7, true, epB, "/Series B/ep1.mkv");
            var videoB = Video(2, new[] { fileB });
            var ep1B = Episode(201, EpisodeType.Episode, 1, null, "Ep B1", false, videoB);
            var seriesB = Series(66, AnimeType.TV, "Test B", ep1B);

            var ds = new RelayShokoPathDataSource(Metadata(seriesA, seriesB), new RelayPathDataSourceOptions(7, root));
            var all = ds.GetAllSeries();
            Assert.Equal(2, all.Count);

            var sa = all.First(s => s.SeriesId == 55);
            var sb = all.First(s => s.SeriesId == 66);

            Assert.NotNull(sa.Extras);
            Assert.Equal(Path.Combine(aDir, "Theme.mp3"), sa.Extras![0].SourcePath);

            Assert.NotNull(sb.Extras);
            Assert.Equal(Path.Combine(bDir, "Theme.mp3"), sb.Extras![0].SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ThemeMp3ExposedViaShokoPathResolver()
    {
        string root = NewDirectory();
        try
        {
            string seriesSubdir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(seriesSubdir);
            File.WriteAllBytes(Path.Combine(seriesSubdir, "Theme.mp3"), new byte[16]);
            string epPath = WriteFile(root, Path.Combine("Some Series", "ep1.mkv"));

            var file = Location(7, true, epPath, "/Some Series/ep1.mkv");
            var video = Video(1, new[] { file });
            var episode = Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, video);
            var series = Series(55, AnimeType.TV, "Test", episode);

            var ds = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root));
            var resolver = new ShokoPathResolver(ds, new RelayNamingStrategy());
            resolver.Rebuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));

            // RelayNamingStrategy.TvRootFolderName is null → series folders at root.
            var rootEntries = resolver.ReadDirectory("");
            Assert.NotEmpty(rootEntries);
            var seriesEntry = rootEntries.FirstOrDefault(e => e.Name == "55");
            Assert.NotNull(seriesEntry);

            var seriesChildren = resolver.ReadDirectory("55");
            Assert.Contains(seriesChildren, e => e.Name == "Theme.mp3" && e.IsFile);
            Assert.Equal(
                Path.Combine(seriesSubdir, "Theme.mp3"),
                resolver.GetSourcePath("55/Theme.mp3"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    #region Test helpers

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

    private static IVideo Video(int id, IReadOnlyList<IVideoFile> files, bool variation = false) =>
        Proxy<IVideo>(
            ("get_ID", id),
            ("get_IsVariation", variation),
            ("get_Files", files),
            ("get_CrossReferences", (IReadOnlyList<IVideoCrossReference>)Array.Empty<IVideoCrossReference>())
        );

    private static IVideoFile Location(int managedFolderId, bool available, string path, string relativePath, long size = 0, DropFolderType folderType = DropFolderType.Destination)
    {
        var folder = Proxy<IManagedFolder>(
            ("get_ID", managedFolderId),
            ("get_Name", $"Folder {managedFolderId}"),
            ("get_DropFolderType", folderType),
            ("get_Path", ""),
            ("get_AvailableFreeSpace", 0L),
            ("get_WatchForNewFiles", false)
        );
        return Proxy<IVideoFile>(
            ("get_ManagedFolderID", managedFolderId),
            ("get_IsAvailable", available),
            ("get_Path", path),
            ("get_RelativePath", relativePath),
            ("get_Size", size),
            ("get_ManagedFolder", folder)
        );
    }

    private static ITitle Title(string value, string languageCode, TitleType type) =>
        Proxy<ITitle>(
            ("get_Value", value),
            ("get_LanguageCode", languageCode),
            ("get_Type", type)
        );

    private static T Proxy<T>(params (string Name, object? Value)[] members)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictDispatchProxy>();
        ((StrictDispatchProxy)(object)proxy).Members = members.ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        return proxy;
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "shoko-relay-theme-test-" + Guid.NewGuid().ToString("N"));
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

    #endregion

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
