using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Api.Models;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// Host-side AnimeThemes xref CSV resolution order (daemon only — the plugin keeps its
/// IApplicationPaths auto-detection): (a) existing explicit AnimeThemesXrefCsvPath, no API
/// call; (b) GET /api/v3/Plugin → ShokoRelay GUID (display-name fallback) → probe
/// &lt;ShokoConfigDir&gt;/&lt;id&gt;/anidb_animethemes_xrefs.csv; (c) glob fallback when the
/// API fails, the plugin is absent, or its dir has no CSV; (d) nothing → Shorts skipped.
/// Resolution runs once per datasource (Lazy-cached), surfaced via the status/path
/// properties. No live API is ever hit here.
/// </summary>
public sealed class ShokoRelayDataSourceAnimeThemesTests : IDisposable
{
    private const int TvSeriesId = 7;
    private const int ManagedFolderId = 1;
    private const string RelayPluginId = "2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a";
    private const string CsvFileName = "anidb_animethemes_xrefs.csv";

    private readonly string _root;
    private readonly string _configDir;

    public ShokoRelayDataSourceAnimeThemesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shoko-vfs-at-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "Show"));
        File.WriteAllText(Path.Combine(_root, "Show", "ep1.mkv"), "x");
        _configDir = Path.Combine(_root, "configuration");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ExplicitExistingPath_WinsWithoutPluginLookup()
    {
        string csv = WriteCsv(RelayPluginId);
        var ds = CreateDataSource(CreateClient(RelayPluginsJson), explicitCsv: csv);

        ds.GetSeriesStructure();

        Assert.Equal("explicit", ds.AnimeThemesXrefCsvStatus);
        Assert.Equal(csv, ds.AnimeThemesXrefCsvPath);
    }

    [Fact]
    public void PluginGuidMatch_ResolvesCsvInPluginConfigDir_AndIsCached()
    {
        string csv = WriteCsv(RelayPluginId);
        var handler = CreateHandler(RelayPluginsJson);
        var ds = CreateDataSource(CreateClient(handler));

        ds.GetSeriesStructure();
        ds.GetSeriesStructure(); // second build reuses the cached structure AND the one-shot resolution

        Assert.Equal("discovered", ds.AnimeThemesXrefCsvStatus);
        Assert.Equal(csv, ds.AnimeThemesXrefCsvPath);
        Assert.Equal(1, handler.PluginCalls);
    }

    [Fact]
    public void PluginNameFallback_UsesReturnedPluginDirectory()
    {
        string csv = WriteCsv("11111111-2222-3333-4444-555555555555");
        var plugins = JsonConvert.SerializeObject(new[]
        {
            new { ID = "11111111-2222-3333-4444-555555555555", Name = "Shoko Relay", Version = "0.17.5" },
        });
        var ds = CreateDataSource(CreateClient(plugins));

        ds.GetSeriesStructure();

        Assert.Equal("discovered", ds.AnimeThemesXrefCsvStatus);
        Assert.Equal(csv, ds.AnimeThemesXrefCsvPath);
    }

    [Fact]
    public void PluginApiFails_FallsBackToGlob()
    {
        string csv = WriteCsv("99999999-8888-7777-6666-555555555555");
        var ds = CreateDataSource(CreateClient(pluginsJson: null)); // /api/v3/Plugin → 404

        ds.GetSeriesStructure();

        Assert.Equal("discovered", ds.AnimeThemesXrefCsvStatus);
        Assert.Equal(csv, ds.AnimeThemesXrefCsvPath);
    }

    [Fact]
    public void PluginDirWithoutCsv_FallsBackToGlob()
    {
        string csv = WriteCsv("other-plugin-dir");
        // Plugin list knows the relay GUID, but that directory carries no CSV.
        var ds = CreateDataSource(CreateClient(RelayPluginsJson));

        ds.GetSeriesStructure();

        Assert.Equal("discovered", ds.AnimeThemesXrefCsvStatus);
        Assert.Equal(csv, ds.AnimeThemesXrefCsvPath);
    }

    [Fact]
    public void NothingFound_StatusIsNotFound_AndStructureStillBuilds()
    {
        var ds = CreateDataSource(CreateClient(RelayPluginsJson));

        var structure = ds.GetSeriesStructure();

        Assert.Equal("not-found", ds.AnimeThemesXrefCsvStatus);
        Assert.Null(ds.AnimeThemesXrefCsvPath);
        // VFS output is unchanged by discovery: the TV group still anchors normally.
        Assert.Contains(structure, s => s.SeriesId == TvSeriesId);
    }

    [Fact]
    public void NothingConfigured_StatusIsUnconfigured_WithoutApiCall()
    {
        var handler = CreateHandler(RelayPluginsJson);
        var ds = CreateDataSource(CreateClient(handler), configDir: "");

        ds.GetSeriesStructure();

        Assert.Equal("unconfigured", ds.AnimeThemesXrefCsvStatus);
        Assert.Null(ds.AnimeThemesXrefCsvPath);
        Assert.Equal(0, handler.PluginCalls);
    }

    private string WriteCsv(string dirName)
    {
        var dir = Path.Combine(_configDir, dirName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, CsvFileName);
        File.WriteAllText(path, $"# Shoko Relay AniDB AnimeThemes Xrefs ##\nAniDB_AnimeID,Type,Filename\n{TvSeriesId},OP,opening.webm\n");
        return path;
    }

    private static string RelayPluginsJson => JsonConvert.SerializeObject(new[]
    {
        new { ID = "2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a", Name = "Shoko Relay", Version = "0.17.5" },
    });

    private ShokoRelayDataSource CreateDataSource(
        ShokoRestClient client, string? explicitCsv = null, string? configDir = null) =>
        new(client, new RelayPathDataSourceOptions(ManagedFolderId, _root)
        {
            ManagedFolderName = "Import",
            ManagedFolderType = Shoko.Abstractions.Video.Enums.DropFolderType.Destination,
            TmdbEpNumbering = false,
            MergeTmdbSeries = false,
            AnimeThemesXrefCsvPath = explicitCsv ?? "",
            ShokoConfigDir = configDir ?? _configDir,
        }, cacheTtl: TimeSpan.FromMinutes(5), maxDegree: 2);

    private static ShokoRestClient CreateClient(string? pluginsJson) =>
        CreateClient(new FakeApiHandler(pluginsJson));

    private static ShokoRestClient CreateClient(FakeApiHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://test/") }, "http://test/");

    private FakeApiHandler CreateHandler(string? pluginsJson) => new(pluginsJson);

    private sealed class FakeApiHandler : HttpMessageHandler
    {
        private readonly string _foldersJson;
        private readonly string _filesJson;
        private readonly string _seriesJson;
        private readonly string _episodesJson;
        private readonly string? _pluginsJson;
        private readonly List<string> _requests = [];

        public FakeApiHandler(string? pluginsJson)
        {
            _pluginsJson = pluginsJson;
            _foldersJson = Serialize(new[] { new ManagedFolderDto { ID = ManagedFolderId, Name = "Import", Path = "", DropFolderType = DropFolderType.Both } });
            var file = new FileDto
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
            _filesJson = Serialize(new ListResult<FileDto> { Total = 1, List = [file] });
            var series = new ShokoSeriesDto
            {
                IDs = new SeriesIdsDto { ID = TvSeriesId },
                Name = "Show",
                AniDB = new AnidbAnimeDto { ID = 1007, Type = Shoko.Abstractions.Metadata.Enums.AnimeType.TV },
            };
            _seriesJson = Serialize(new ListResult<ShokoSeriesDto> { Total = 1, List = [series] });
            var episode = new ShokoEpisodeDto
            {
                IDs = new EpisodeIdsDto { ID = 1, ParentSeries = TvSeriesId },
                Name = "Episode 1",
                IsHidden = false,
                AniDB = new AnidbEpisodeDto { ID = 1, AnimeID = 1007, Type = Shoko.Abstractions.Metadata.Enums.EpisodeType.Episode, EpisodeNumber = 1 },
            };
            _episodesJson = Serialize(new ListResult<ShokoEpisodeDto> { Total = 1, List = [episode] });
        }

        public int PluginCalls => _requests.Count(r => r.Contains("/api/v3/Plugin"));

        private static string Serialize(object value) => JsonConvert.SerializeObject(value);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";
            _requests.Add(path + "?" + query);

            string? body = null;
            if (path.EndsWith("/api/v3/ManagedFolder"))
                body = _foldersJson;
            else if (Regex.IsMatch(path, @"/api/v3/ManagedFolder/\d+/File$"))
                body = _filesJson;
            else if (Regex.IsMatch(path, @"/api/v3/Series/\d+/Episode$"))
                body = _episodesJson;
            else if (Regex.IsMatch(path, @"/api/v3/Series/\d+$"))
                body = _seriesJson;
            else if (path.EndsWith("/api/v3/Series"))
                body = query.Contains("page=1") ? _seriesJson : "{\"Total\":0,\"List\":[]}";
            else if (path.EndsWith("/api/v3/Plugin"))
                body = _pluginsJson;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"not found: {path}{query}", Encoding.UTF8, "text/plain") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
