using System.Net;
using System.Text;
using System.Text.Json;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// Verifies that AnimeThemes Theme.mp3 files (which ShokoRelay's plugin no longer
/// symlinks into the VFS when Settings.Advanced.UseExternalVfs=true) are surfaced
/// in the daemon's FUSE view as <c>&lt;TvRoot&gt;/&lt;seriesFolder&gt;/Theme.mp3</c>.
/// </summary>
public sealed class ThemeMp3ExposeTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly string _managedFoldersJson;
        private readonly string _folderFilesJson;
        private readonly string _allSeriesJson;
        private readonly string _seriesEpisodesJson;

        public MockHttpHandler(string managedFolders, string folderFiles, string allSeries, string seriesEpisodes)
        {
            _managedFoldersJson = managedFolders;
            _folderFilesJson = folderFiles;
            _allSeriesJson = allSeries;
            _seriesEpisodesJson = seriesEpisodes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";

            string body;
            if (path.EndsWith("/api/v3/ManagedFolder") && !path.Contains("/ManagedFolder/"))
                body = _managedFoldersJson;
            else if (path.Contains("/ManagedFolder/") && path.Contains("/File"))
                body = _folderFilesJson;
            else if (path.Contains("/api/v3/Series") && path.Contains("/Episode"))
                body = _seriesEpisodesJson;
            else if (path.Contains("/api/v3/Series"))
                body = query.Contains("page=1") ? _allSeriesJson : "{\"Total\":0,\"List\":[]}";
            else if (path.Contains("/api/v3/TMDB/Show/") && path.Contains("/Episode"))
                body = "{\"Total\":0,\"List\":[]}";
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"not found: {path}{query}", Encoding.UTF8, "text/plain"),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void ThemeMp3SurfacedAsSeriesExtra()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoko-vfs-theme-test-{Guid.NewGuid():N}");
        var seriesFolder = Path.Combine(root, "Some Series");
        Directory.CreateDirectory(seriesFolder);
        File.WriteAllBytes(Path.Combine(seriesFolder, "Theme.mp3"), new byte[16]);
        File.WriteAllBytes(Path.Combine(seriesFolder, "ep1.mkv"), new byte[] { 0x42 });

        try
        {
            // Minimal JSON envelopes — keep these aligned with ShokoSeriesDto / ShokoEpisodeDto / FileDto / FileLocationDto.
            var folders = "[{\"ID\":1,\"Name\":\"Some Series\",\"Path\":\"" + JsonEncodedText.Encode(root).ToString() + "\",\"DropFolderType\":3}]";
            var fileInner = "{\"ID\":10,\"Size\":1,\"IsVariation\":false,\"Locations\":[{\"ManagedFolderID\":1,\"RelativePath\":\"Some Series/ep1.mkv\"}],\"SeriesIDs\":[{\"SeriesID\":{\"ID\":7},\"EpisodeIDs\":[{\"ID\":1}]}]}";
            var files = "{\"Total\":1,\"List\":[" + fileInner + "]}";
            var seriesInner = "{\"IDs\":{\"ID\":7},\"AniDB\":{\"ID\":99,\"Type\":0,\"Titles\":[]},\"TMDB\":{\"Shows\":[],\"Movies\":[]}}";
            var series = "{\"Total\":1,\"List\":[" + seriesInner + "]}";
            var episodesInner = "{\"IDs\":{\"ID\":1},\"IsHidden\":false,\"AniDB\":{\"ID\":1,\"Type\":1,\"EpisodeNumber\":1,\"Titles\":[]},\"TMDB\":{\"Episodes\":[]},\"Files\":[{\"ID\":10,\"Size\":1,\"IsVariation\":false,\"Locations\":[{\"ManagedFolderID\":1,\"RelativePath\":\"Some Series/ep1.mkv\"}],\"SeriesIDs\":[]}]}";
            var episodes = "{\"Total\":1,\"List\":[" + episodesInner + "]}";

            var handler = new MockHttpHandler(folders, files, series, episodes);
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
            var client = new ShokoRestClient(http, "http://test/");

            var options = new RelayPathDataSourceOptions(1, root)
            {
                ManagedFolderName = "Some Series",
                ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
                SeriesTitleLanguage = "SHOKO",
                EpisodeTitleLanguage = "SHOKO",
                MoveCommonSeriesTitlePrefixes = true,
                TmdbEpGroupNames = true,
                TmdbEpNumbering = true,
                MergeTmdbSeries = false,
                PlexLocalExtras = true,
                FolderExclusions = "",
                ManagedFolderExclusions = "",
                ManualOverrideGroups = [],
                RelayTvFolderName = "!ShokoRelayVFS",
                RelayMovieFolderName = "!ShokoRelayMovieVFS",
            };

            var ds = new ShokoRelayDataSource(client, options, TimeSpan.FromSeconds(30), maxDegree: 1);
            var all = ds.GetAllSeries();
            Assert.Single(all);
            var s = all[0];
            Assert.NotNull(s.Extras);
            Assert.Single(s.Extras!);
            Assert.Equal("Theme.mp3", s.Extras![0].Name);
            Assert.Equal(Path.Combine(seriesFolder, "Theme.mp3"), s.Extras![0].SourcePath);

            var resolver = new ShokoPathResolver(ds, new RelayNamingStrategy());
            resolver.Rebuild();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!resolver.HasSnapshot && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            Assert.True(resolver.HasSnapshot);

            // RelayNamingStrategy.TvRootFolderName is null, so series folders sit at root.
            var rootEntries = resolver.ReadDirectory("");
            Assert.NotEmpty(rootEntries);
            var seriesEntry = rootEntries.FirstOrDefault(e => e.Name == "7");
            Assert.NotNull(seriesEntry);
            var seriesChildren = resolver.ReadDirectory("7");
            Assert.Contains(seriesChildren, e => e.Name == "Theme.mp3" && e.IsFile);
            Assert.Equal(
                Path.Combine(seriesFolder, "Theme.mp3"),
                resolver.GetSourcePath("7/Theme.mp3"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
