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
/// F1: discovery mirror of upstream ShokoRelay's VfsAssetLinker — episode sidecars and
/// attachment folders, series-level metadata/artwork, Plex local extras (subdirectories and
/// inline files) and AnimeThemes Shorts. Exercises the in-process relay datasource end to
/// end so both the discovery and the resolver's placement are validated.
/// </summary>
public sealed class RelayLocalAssetLinkerTests
{
    [Fact]
    public void EpisodeSidecarsAndAttachmentFolder_RenameToVfsBaseName()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(Path.Combine(dir, "ep1_attachments", "x"));
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "ep1.en.srt"), "sub");
            File.WriteAllText(Path.Combine(dir, "ep1.jpg"), "img");
            File.WriteAllText(Path.Combine(dir, "ep1.nfo"), "meta");
            File.WriteAllText(Path.Combine(dir, "ep1_attachments", "x", "y.png"), "png");
            // Different base must not be swept in (upstream requires a non-alphanumeric
            // character after the original base).
            File.WriteAllText(Path.Combine(dir, "ep10.srt"), "other");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var mapping = Assert.Single(data.Mappings);
            var sidecars = mapping.Sidecars ?? [];

            Assert.Equal(
                new[] { ".en.srt", ".jpg", ".nfo", "_attach/x/y.png" },
                sidecars.Select(sidecar => sidecar.Suffix).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            Assert.Equal(Path.Combine(dir, "ep1.en.srt"), sidecars.First(s => s.Suffix == ".en.srt").SourcePath);
            Assert.Equal(Path.Combine(dir, "ep1_attachments", "x", "y.png"), sidecars.First(s => s.Suffix == "_attach/x/y.png").SourcePath);

            // Resolver placement: renamed sidecars beside the video, attach folder with
            // a nested directory, and no ep10 sidecar anywhere.
            var resolver = Ready(data);
            var season = resolver.ReadDirectory("55/Season 1");
            Assert.Contains(season, entry => entry.Name == "S01E01 [1].en.srt");
            Assert.Contains(season, entry => entry.Name == "S01E01 [1].jpg");
            Assert.Contains(season, entry => entry.Name == "S01E01 [1].nfo");
            Assert.Contains(season, entry => entry.Name == "S01E01 [1]_attach" && entry.IsDirectory);
            Assert.Equal(
                Path.Combine(dir, "ep1.en.srt"),
                resolver.GetSourcePath("55/Season 1/S01E01 [1].en.srt"));
            Assert.Equal(
                Path.Combine(dir, "ep1_attachments", "x", "y.png"),
                resolver.GetSourcePath("55/Season 1/S01E01 [1]_attach/x/y.png"));
            Assert.DoesNotContain(season, entry => entry.Name.Contains("ep10", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SubtitleLanguageMapping_RenamesTokenAndPreservesModifiers()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "ep1.de.forced.srt"), "forced german");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var options = new RelayPathDataSourceOptions(7, root)
            {
                SubtitleLanguageMappings = [new SubtitleLanguageMapping("de", "German")],
            };
            var data = new RelayShokoPathDataSource(Metadata(series), options).GetAllSeries().Single();
            var sidecar = Assert.Single(Assert.Single(data.Mappings).Sidecars!);
            // "de" → "German", "forced" is a modifier and is preserved.
            Assert.Equal(".German.forced.srt", sidecar.Suffix);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SubtitleMappingCollision_PrefersUnchangedOriginal()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "ep1.en.srt"), "mapped");
            File.WriteAllText(Path.Combine(dir, "ep1.English.srt"), "original");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var options = new RelayPathDataSourceOptions(7, root)
            {
                SubtitleLanguageMappings = [new SubtitleLanguageMapping("en", "English")],
            };
            var data = new RelayShokoPathDataSource(Metadata(series), options).GetAllSeries().Single();
            var sidecars = Assert.Single(data.Mappings).Sidecars!;

            // Both sides map to ".English.srt"; the unmapped original (priority -1) links
            // first and the converted candidate is skipped (VfsAssetLinker.cs:141-145).
            var sidecar = Assert.Single(sidecars);
            Assert.Equal(".English.srt", sidecar.Suffix);
            Assert.Equal(Path.Combine(dir, "ep1.English.srt"), sidecar.SourcePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SeriesAssets_AggregatedAtSeriesRoot_WithSpecialsRenameAndSidecarExclusion()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllBytes(Path.Combine(dir, "poster.jpg"), new byte[1]);
            File.WriteAllBytes(Path.Combine(dir, "fanart.jpg"), new byte[2]);
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), new byte[3]);
            File.WriteAllBytes(Path.Combine(dir, "movie.nfo"), new byte[4]);
            File.WriteAllBytes(Path.Combine(dir, "Theme.mp3"), new byte[5]);
            File.WriteAllBytes(Path.Combine(dir, "Specials.jpg"), new byte[6]);
            // Episode sidecar basenames are excluded from series-level metadata.
            File.WriteAllBytes(Path.Combine(dir, "ep1.nfo"), new byte[7]);
            File.WriteAllBytes(Path.Combine(dir, "ep1.jpg"), new byte[8]);

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();

            var assets = Assert.Single(data.Mappings).SeriesAssets ?? [];
            Assert.Equal(
                new[] { "Season-Specials-Poster.jpg", "Theme.mp3", "fanart.jpg", "logo.png", "movie.nfo", "poster.jpg" },
                assets.Select(asset => asset.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(assets, asset => asset.Name == "ep1.nfo" || asset.Name == "ep1.jpg");

            var resolver = Ready(data);
            var rootEntries = resolver.ReadDirectory("55");
            Assert.Contains(rootEntries, entry => entry.Name == "Season-Specials-Poster.jpg");
            Assert.Contains(rootEntries, entry => entry.Name == "Theme.mp3");
            Assert.DoesNotContain(rootEntries, entry => entry.Name == "ep1.nfo");
            // ep1.nfo/ep1.jpg remain episode sidecars next to the video.
            Assert.Contains(resolver.ReadDirectory("55/Season 1"), entry => entry.Name == "S01E01 [1].nfo");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void PlexLocalExtraSubdirectories_BroadcastToTvRoot_AndGatedBySetting()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(Path.Combine(dir, "Trailers"));
            Directory.CreateDirectory(Path.Combine(dir, "Featurettes S2"));
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "Trailers", "teaser.mkv"), "t");
            File.WriteAllText(Path.Combine(dir, "Featurettes S2", "bts.mkv"), "b");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var tvExtras = data.TvLocalExtras ?? [];
            // "Trailers" lands at the series root; "Featurettes S2" at "Season 2".
            Assert.Equal(
                new[] { "Season 2/Featurettes/bts.mkv", "Trailers/teaser.mkv" },
                tvExtras.Select(extra => extra.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(Path.Combine(dir, "Trailers", "teaser.mkv"), tvExtras.First(e => e.Name == "Trailers/teaser.mkv").SourcePath);
            Assert.Equal(Path.Combine(dir, "Featurettes S2", "bts.mkv"), tvExtras.First(e => e.Name == "Season 2/Featurettes/bts.mkv").SourcePath);

            var resolver = Ready(data);
            var rootEntries = resolver.ReadDirectory("55");
            Assert.Contains(rootEntries, entry => entry.Name == "Trailers" && entry.IsDirectory);
            Assert.Contains(rootEntries, entry => entry.Name == "Season 2" && entry.IsDirectory);
            Assert.Equal(
                Path.Combine(dir, "Trailers", "teaser.mkv"),
                resolver.GetSourcePath("55/Trailers/teaser.mkv"));
            Assert.Equal(
                Path.Combine(dir, "Featurettes S2", "bts.mkv"),
                resolver.GetSourcePath("55/Season 2/Featurettes/bts.mkv"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void PlexLocalExtraSubdirectories_DisabledSetting_EmitsNothing()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(Path.Combine(dir, "Trailers"));
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "Trailers", "teaser.mkv"), "t");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var data = new RelayShokoPathDataSource(
                Metadata(series),
                new RelayPathDataSourceOptions(7, root) { PlexLocalExtras = false }).GetAllSeries().Single();

            Assert.Null(data.TvLocalExtras);
            Assert.Null(Assert.Single(data.Mappings).InlineLocalExtras);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void InlineLocalExtra_LandsInParentSeason_TvOnlyNotMovie()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "ep1-trailer.mkv"), "trailer");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root)).GetAllSeries().Single();
            var inline = Assert.Single(Assert.Single(data.Mappings).InlineLocalExtras!);
            Assert.Equal("-trailer.mkv", inline.Suffix);
            Assert.Equal(Path.Combine(dir, "ep1-trailer.mkv"), inline.SourcePath);

            var resolver = Ready(data);
            Assert.Contains(resolver.ReadDirectory("55/Season 1"), entry => entry.Name == "S01E01 [1]-trailer.mkv");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void MovieSidecars_AndNoInlineExtras_InStandaloneMovieFolder()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Film");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "movie.mkv"), new byte[4]);
            File.WriteAllText(Path.Combine(dir, "movie.en.srt"), "sub");
            File.WriteAllText(Path.Combine(dir, "movie-trailer.mkv"), "trailer");

            var series = Series(
                56,
                AnimeType.Movie,
                "Test Movie",
                Episode(201, EpisodeType.Episode, 1, null, "Movie", false, Video(2, [Location(7, true, Path.Combine(dir, "movie.mkv"), "/Some Film/movie.mkv", 4)])));

            // TmdbEpNumbering off: the series is a movie via its AniDB type (the proxy has no
            // TMDB movie entry, so the default TMDB-first classification would mark it a show).
            var data = new RelayShokoPathDataSource(Metadata(series), new RelayPathDataSourceOptions(7, root) { TmdbEpNumbering = false }).GetAllSeries().Single();
            var mapping = Assert.Single(data.Mappings);
            // Movie sidecars are linked (LinkEpisodeMetadata runs in the movie pass too)…
            Assert.Contains(mapping.Sidecars ?? [], sidecar => sidecar.Suffix == ".en.srt");
            // …but inline Plex extras are TV-only (LinkLocalExtras runs inside the TV pass).
            Assert.NotNull(mapping.InlineLocalExtras);
            Assert.Single(mapping.InlineLocalExtras!);

            var resolver = Ready(data, movieOptions: true);
            // Movie folder: sidecar present, inline extra absent.
            var movieEntries = resolver.ReadDirectory("201");
            Assert.Contains(movieEntries, entry => entry.Name == "Movie [2].mkv");
            Assert.Contains(movieEntries, entry => entry.Name == "Movie [2].en.srt");
            Assert.DoesNotContain(movieEntries, entry => entry.Name.Contains("trailer", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void AnimeThemesShorts_ExposedFromXrefCsv_WhenWebmExists()
    {
        string root = NewDirectory();
        try
        {
            string dir = Path.Combine(root, "Some Series");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(root, "!AnimeThemes", "Shorts"));
            File.WriteAllBytes(Path.Combine(dir, "ep1.mkv"), new byte[4]);
            File.WriteAllBytes(Path.Combine(root, "!AnimeThemes", "Shorts", "Test OP1.webm"), new byte[9]);

            // Upstream CSV columns (AnimeThemesHelper.cs:304): filepath, videoId, anidbId,
            // nc, slug, version, songTitle, artistName, lyrics, subbed, uncen, nsfw, spoiler,
            // source, resolution, episodes, overlap.
            string csvPath = Path.Combine(root, "anidb_animethemes_xrefs.csv");
            File.WriteAllText(csvPath, "# filepath, videoId, anidbId, nc, slug, version, songTitle, artistName, lyrics, subbed, uncen, nsfw, spoiler, source, resolution, episodes, overlap\nShorts/Test OP1.webm,11,55,0,OP1,1,Song Title,Artist,0,0,0,0,0,BD,1080,1-12,None\n");

            var series = Series(
                55,
                AnimeType.TV,
                "Test",
                Episode(101, EpisodeType.Episode, 1, null, "Episode 1", false, Video(1, [Location(7, true, Path.Combine(dir, "ep1.mkv"), "/Some Series/ep1.mkv", 4)])));

            var options = new RelayPathDataSourceOptions(7, root) { AnimeThemesXrefCsvPath = csvPath };
            var data = new RelayShokoPathDataSource(Metadata(series), options).GetAllSeries().Single();

            // FinalName = BuildNewFileName(lookup, ".webm") with slug OP1 → upstream's ParseSlug
// collapses "OP1" to "OP" (AnimeThemesHelper.cs:468-477), then the Hsp/Zwsp prefix
// insertion consumes the remainder — so the name has no trailing "1".
            var extra = Assert.Single(data.Extras!);
            Assert.Equal("Shorts/\u200AO\u200BP ❯ Song Title ❯ Artist.webm", extra.Name);
            Assert.Equal(Path.Combine(root, "!AnimeThemes", "Shorts", "Test OP1.webm"), extra.SourcePath);

            var resolver = Ready(data);
            Assert.Equal(
                Path.Combine(root, "!AnimeThemes", "Shorts", "Test OP1.webm"),
                resolver.GetSourcePath("55/Shorts/\u200AO\u200BP ❯ Song Title ❯ Artist.webm"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void CrossoverFile_SuppressesTvAssets_ButNotMovieAssets()
    {
        // A file cross-referenced to two different primary series (upstream's
        // distinctPrimarySeriesCount > 1 gate, VfsBuilder.cs:490-516).
        var sourcePath = Path.Combine(Path.GetTempPath(), "no-such-shoko-assets-dir", "crossover.mkv");
        var mapping = new EpisodeData(
            1, 101, 1, 1, null, null, 1, false, true, "Ep", sourcePath, 0, ".mkv",
            XrefSeriesIds: [10, 20]);
        var data = new SeriesData(55, "Test", false, [mapping]);

        var enriched = RelayLocalAssetLinker.Discover(
            data,
            new RelayPathDataSourceOptions(7, "/tmp"),
            "/tmp",
            [],
            StringComparison.Ordinal,
            _ => false,
            id => id);

        Assert.True(enriched.Mappings[0].SuppressTvLocalAssets);

        // Two xrefs collapsing to a single primary do not suppress.
        var singlePrimary = data with { Mappings = [mapping with { XrefSeriesIds = [10, 10] }] };
        var enrichedSingle = RelayLocalAssetLinker.Discover(
            singlePrimary,
            new RelayPathDataSourceOptions(7, "/tmp"),
            "/tmp",
            [],
            StringComparison.Ordinal,
            _ => false,
            id => id);
        Assert.False(enrichedSingle.Mappings[0].SuppressTvLocalAssets);
    }

    #region Test helpers (mirrors RelayShokoPathDataSourceTests)

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

    private static IVideo Video(int id, IReadOnlyList<IVideoFile> files) =>
        Proxy<IVideo>(
            ("get_ID", id),
            ("get_IsVariation", false),
            ("get_Files", files),
            ("get_CrossReferences", (IReadOnlyList<IVideoCrossReference>)Array.Empty<IVideoCrossReference>())
        );

    private static IVideoFile Location(int managedFolderId, bool available, string path, string relativePath, long size = 0)
    {
        var folder = Proxy<IManagedFolder>(
            ("get_ID", managedFolderId),
            ("get_Name", $"Folder {managedFolderId}"),
            ("get_DropFolderType", DropFolderType.Destination),
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
        string path = Path.Combine(Path.GetTempPath(), "shoko-relay-assets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
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

    private static ShokoPathResolver Ready(SeriesData series, bool movieOptions = false)
    {
        var options = movieOptions
            ? new PathResolverOptions { Shows = true, MoviesAsTv = true, StandaloneMovies = true }
            : new PathResolverOptions();
        var resolver = new ShokoPathResolver(new SingleDataSource(series), new RelayNamingStrategy(), options);
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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException("Missing proxy method.");
            if (Members.TryGetValue(targetMethod.Name, out var value))
                return value;
            throw new InvalidOperationException($"Unexpected member read: {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
        }
    }

    #endregion
}