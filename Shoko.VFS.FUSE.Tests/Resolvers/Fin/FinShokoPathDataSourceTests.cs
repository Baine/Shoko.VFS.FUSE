using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Media;
using Shoko.Abstractions.Video.Release;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers.Fin;
using System.Reflection;

namespace Shoko.VFS.FUSE.Tests.Resolvers.Fin;

public sealed class FinShokoPathDataSourceTests
{
    private static readonly Guid Library = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Imported = new(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

    [Fact]
    public void TvSingleSeries_EmitsCanonicalRootShowAndFileAttributes()
    {
        var root = NewDirectory();
        try
        {
            WriteFile(root, "folder/ep.mkv");
            var series = Series(10, AnimeType.TV, "My Show",
                Episode(30, anidbEpisodeId: 1, number: 7, fileId: 99, "folder/ep.mkv"));

            var entries = Build(series, root).GetEntries();

            var symlink = entries.Single(entry => entry.NodeType == VirtualNodeType.Symlink);
            Assert.StartsWith($"{Library}/", symlink.Path, StringComparison.Ordinal);
            Assert.Contains($"/My Show [Shoko Series=10]/", symlink.Path, StringComparison.Ordinal);
            Assert.Contains("[Shoko Series=10] [Shoko File=99]", symlink.Path, StringComparison.Ordinal);
            Assert.EndsWith(".mkv", symlink.Path, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(root, "folder/ep.mkv"), symlink.SymlinkTarget);
            Assert.Equal(Imported, symlink.LastModified);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TvMultipleFiles_SingleShowFolderEmitsOneSymlinkPerFile()
    {
        var root = NewDirectory();
        try
        {
            WriteFile(root, "folder/ep1.mkv");
            WriteFile(root, "folder/ep2.mkv");
            var series = Series(10, AnimeType.TV, "My Show",
                Episode(30, anidbEpisodeId: 1, number: 1, fileId: 99, "folder/ep1.mkv"),
                Episode(31, anidbEpisodeId: 2, number: 2, fileId: 98, "folder/ep2.mkv"));

            var entries = Build(series, root).GetEntries();

            var symlinks = entries.Where(entry => entry.NodeType == VirtualNodeType.Symlink).ToArray();
            Assert.Equal(2, symlinks.Length);
            Assert.All(symlinks, link =>
            {
                Assert.StartsWith($"{Library}/", link.Path, StringComparison.Ordinal);
                Assert.Contains("/My Show [Shoko Series=10]/", link.Path, StringComparison.Ordinal);
            });
            Assert.Contains(symlinks, link => link.Path.Contains("[Shoko File=99]", StringComparison.Ordinal));
            Assert.Contains(symlinks, link => link.Path.Contains("[Shoko File=98]", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void NoManagedFiles_ReturnsEmpty()
    {
        var root = NewDirectory();
        try
        {
            var series = Series(10, AnimeType.TV, "My Show",
                Episode(30, anidbEpisodeId: 1, number: 1, fileId: 99, "missing.mkv"));

            var entries = Build(series, root).GetEntries();

            Assert.Empty(entries);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void OrderedEpisodeNumbers_ComeFromSeasonOrderingEngine()
    {
        var root = NewDirectory();
        try
        {
            WriteFile(root, "folder/ep1.mkv");
            WriteFile(root, "folder/ep2.mkv");
            // Two normal episodes with out-of-order source episode numbers (5, 9);
            // the ordering engine assigns sequential 1-based placement, proving it ran.
            var series = Series(10, AnimeType.TV, "Sort Show",
                Episode(30, anidbEpisodeId: 1, number: 5, fileId: 99, "folder/ep1.mkv"),
                Episode(31, anidbEpisodeId: 2, number: 9, fileId: 98, "folder/ep2.mkv"));

            var entries = Build(series, root).GetEntries();

            var symlinks = entries.Where(entry => entry.NodeType == VirtualNodeType.Symlink).ToArray();
            Assert.Equal(2, symlinks.Length);
            Assert.Contains(symlinks, link => link.Path.Contains("E001", StringComparison.Ordinal));
            Assert.Contains(symlinks, link => link.Path.Contains("E002", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static FinShokoPathDataSource Build(IShokoSeries series, string root) =>
        new(
            Metadata(series),
            new FinLibraryProfile(Library),
            Mappings(5, root),
            new FinShokoPathDataSourceOptions());

    private static List<FinMediaFolderMapping> Mappings(int managedFolderId, string root) =>
    [
        new FinMediaFolderMapping(Library, managedFolderId, "/", [root]),
    ];

    private static IMetadataService Metadata(params IShokoSeries[] series) =>
        Proxy<IMetadataService>(
            ("GetAllShokoSeries", (IEnumerable<IShokoSeries>)series)
        );

    private static IShokoSeries Series(int id, AnimeType type, string title, params EpisodeSpec[] specs)
    {
        var episodes = specs.Select(spec => Episode(id, spec)).ToArray();
        return Proxy<IShokoSeries>(
            ("get_ID", id),
            ("get_Type", type),
            ("get_Title", title),
            ("get_AnidbAnimeID", id),
            ("get_CreatedAt", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("get_AnidbAnime", AnidbAnime(id, title)),
            ("get_TmdbShows", (IReadOnlyList<ITmdbShow>)Array.Empty<ITmdbShow>()),
            ("get_Episodes", (IReadOnlyList<IShokoEpisode>)episodes)
        );
    }

    private static IAnidbAnime AnidbAnime(int id, string title) =>
        Proxy<IAnidbAnime>(
            ("get_ID", id),
            ("get_Title", title),
            ("get_Type", AnimeType.TV)
        );

    private static IShokoEpisode Episode(int seriesId, EpisodeSpec spec)
    {
        var video = Video(spec.FileId, spec.RelativePath, spec.ManagedFolderId, spec.Root, Imported, seriesId, spec.EpisodeId, spec.AnidbEpisodeId);
        return Proxy<IShokoEpisode>(
            ("get_ID", spec.EpisodeId),
            ("get_EpisodeNumber", spec.Number),
            ("get_AnidbEpisodeID", spec.AnidbEpisodeId),
            ("get_Type", EpisodeType.Episode),
            ("get_IsHidden", false),
            ("get_AirDate", (DateOnly?)null),
            ("get_AirDateWithTime", (DateTime?)null),
            ("get_AnidbEpisode", null),
            ("get_Videos", (IReadOnlyList<IVideo>)[video])
        );
    }

    private static IVideo Video(int id, string relativePath, int managedFolderId, string root, DateTimeOffset imported,
        int seriesId, int episodeId, int anidbEpisodeId) =>
        Proxy<IVideo>(
            ("get_ID", id),
            ("get_ImportedAt", imported.UtcDateTime),
            ("get_CrossReferences", (IReadOnlyList<IVideoCrossReference>)new IVideoCrossReference[]
            {
                Proxy<IVideoCrossReference>(
                    ("get_AnidbEpisodeID", anidbEpisodeId),
                    ("get_ShokoSeries", ProxySeriesRef(seriesId)),
                    ("get_ShokoEpisode", ProxyEpisodeRef(episodeId)))
            }),
            ("get_ReleaseInfo", (IReleaseInfo?)null),
            ("get_MediaInfo", (IMediaInfo?)null),
            ("get_Files", (IReadOnlyList<IVideoFile>)new IVideoFile[] { VideoFile(id, relativePath, managedFolderId, root) })
        );

    // Minimal series/episode stubs so the strict adapter only reads `ID`.
    private static IShokoSeries ProxySeriesRef(int id) =>
        Proxy<IShokoSeries>(("get_ID", id));

    private static IShokoEpisode ProxyEpisodeRef(int id) =>
        Proxy<IShokoEpisode>(("get_ID", id));

    private static IVideoFile VideoFile(int id, string relativePath, int managedFolderId, string root) =>
        Proxy<IVideoFile>(
            ("get_ID", id),
            ("get_RelativePath", relativePath),
            ("get_ManagedFolderID", managedFolderId)
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
        string path = Path.Combine(Path.GetTempPath(), "shoko-fin-source-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
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

    private sealed record EpisodeSpec(
        int EpisodeId,
        int AnidbEpisodeId,
        int Number,
        int FileId,
        string RelativePath,
        int ManagedFolderId,
        string Root);

    private static EpisodeSpec Episode(int episodeId, int anidbEpisodeId, int number, int fileId, string relativePath)
        => new(episodeId, anidbEpisodeId, number, fileId, relativePath, ManagedFolderId: 5, Root: "");

    private class StrictDispatchProxy : DispatchProxy
    {
        internal Dictionary<string, object?> Members { get; set; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException("Missing proxy method.");
            if (Members.TryGetValue(targetMethod.Name, out var value))
                return value;
            throw new InvalidOperationException($"Unexpected member read: {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
        }
    }
}
