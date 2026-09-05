using System.Net;
using System.Text;
using System.Text.Json;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// End-to-end diagnostic: drives the real <see cref="ShokoRelayDataSource"/> against saved
/// Shoko API JSON, bypassing the network. Finds out whether the One Piece projection bug
/// is in the typed DTO deserialization / <c>ExtractSeriesAsync</c> path or earlier.
/// </summary>
public sealed class OnePieceEndToEndDiagnostic
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly string _managedFoldersJson;
        private readonly string _gerdubFilesJson;
        private readonly string _allSeriesConcatenated; // 20 concatenated JSON objects (legacy)
        private readonly string _seriesEpisodesJson;
        private readonly string _allSeriesMinimalP1; // single-page: just One Piece
        private readonly string _allSeriesMinimalEmpty; // empty follow-up page
        private readonly bool _useMinimal;
        private List<JsonElement>? _allSeriesObjects;

        public MockHttpHandler(string dataDir)
        {
            _managedFoldersJson = File.ReadAllText(Path.Combine(dataDir, "managed_folders.json"));
            _gerdubFilesJson = File.ReadAllText(Path.Combine(dataDir, "gerdub_files.json"));
            _allSeriesConcatenated = File.ReadAllText(Path.Combine(dataDir, "all_series.json"));
            _seriesEpisodesJson = File.ReadAllText(Path.Combine(dataDir, "series_episodes.json"));
            var minimalP1 = Path.Combine(dataDir, "all_series_minimal_p1.json");
            var minimalEmpty = Path.Combine(dataDir, "all_series_minimal_empty.json");
            _useMinimal = File.Exists(minimalP1) && File.Exists(minimalEmpty);
            _allSeriesMinimalP1 = _useMinimal ? File.ReadAllText(minimalP1) : "";
            _allSeriesMinimalEmpty = _useMinimal ? File.ReadAllText(minimalEmpty) : "";
            _allSeriesObjects = _useMinimal ? null : SplitConcatenatedJsonObjects(_allSeriesConcatenated);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";

            string body;

            if (path == "/" || path == "/api/v3/ManagedFolder" && !path.Contains("/ManagedFolder/"))
            {
                body = _managedFoldersJson;
            }
            else if (path.Contains("/ManagedFolder/") && path.Contains("/File"))
            {
                body = _gerdubFilesJson;
            }
            else if (path.Contains("/api/v3/Series") && path.Contains("/Episode"))
            {
                // One Piece episodes — serve for any series id (we only test One Piece)
                body = _seriesEpisodesJson;
            }
            else if (path.Contains("/api/v3/Series"))
            {
                // Paginated all-series. Parse ?page=N and slice accordingly.
                var page = ParsePage(query);
                body = SerializePage(page);
            }
            else if (path.Contains("/api/v3/TMDB/Show/") && path.Contains("/Episode"))
            {
                // TMDB show episodes — return empty (no preferred ordering expected for One Piece)
                body = "{\"Total\":0,\"List\":[]}";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"not found: {path}{query}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static int ParsePage(string query)
        {
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                if (part.StartsWith("page=", StringComparison.Ordinal))
                    return int.Parse(part.Substring(5));
            }
            return 1;
        }

        private string SerializePage(int page)
        {
            if (_useMinimal)
            {
                return page == 1 ? _allSeriesMinimalP1 : _allSeriesMinimalEmpty;
            }
            if (_allSeriesObjects is null || page < 1 || page > _allSeriesObjects.Count)
                return "{\"Total\":0,\"List\":[]}";

            var obj = _allSeriesObjects[page - 1];
            // Re-emit as compact JSON so the typed deserializer is happy.
            return obj.GetRawText();
        }

        private static List<JsonElement> SplitConcatenatedJsonObjects(string text)
        {
            var result = new List<JsonElement>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var slice = text.Substring(start, i - start + 1);
                        using var doc = JsonDocument.Parse(slice);
                        result.Add(doc.RootElement.Clone());
                    }
                }
            }
            return result;
        }
    }

    [Fact]
    public void RunOnePieceEndToEndDiagnostic()
    {
        var dataDir = Environment.GetEnvironmentVariable("ONE_PIECE_TEST_DATA")
            ?? "/tmp/opencode/onepiece_test_data";
        Assert.True(Directory.Exists(dataDir), $"Test data not found at {dataDir}");

        var handler = new MockHttpHandler(dataDir);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/");

        // Find GerDub managed folder id from the JSON
        using var mfDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "managed_folders.json")));
        var folders = mfDoc.RootElement;
        int gerdubId = -1;
        foreach (var f in folders.EnumerateArray())
        {
            var name = f.GetProperty("Name").GetString() ?? "";
            if (name.Contains("GerDub", StringComparison.OrdinalIgnoreCase))
            {
                gerdubId = f.GetProperty("ID").GetInt32();
                Console.WriteLine($"GerDub ID: {gerdubId}");
                break;
            }
        }
        Assert.True(gerdubId > 0, "GerDub folder not found in test data");

        var options = new RelayPathDataSourceOptions(gerdubId, "/mnt/user/array/Anime/Shows/GerDub/")
        {
            ManagedFolderName = "Anime-Shows-GerDub",
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

        // Call the public entry point — exercises the full pipeline.
        // Pass a non-zero cacheTtl so the internal _cache is constructed (otherwise
        // GetAllSeriesCoreAsync NREs on _cache.MaxDegree at line 140).
        var ds = new ShokoRelayDataSource(client, options, TimeSpan.FromSeconds(30), maxDegree: 1);
        var all = ds.GetAllSeries();

        Console.WriteLine($"Total series returned: {all.Count}");
        // Dump a few titles to see what's there
        Console.WriteLine("First 10 series titles:");
        foreach (var s in all.Take(10))
            Console.WriteLine($"  SeriesId={s.SeriesId} Title={s.DisplayTitle}");

        // Try several search strategies
        var op = all.FirstOrDefault(s => s.DisplayTitle?.Contains("One Piece", StringComparison.OrdinalIgnoreCase) == true)
            ?? all.FirstOrDefault(s => s.DisplayTitle?.Contains("One", StringComparison.OrdinalIgnoreCase) == true && s.DisplayTitle.Contains("Piece", StringComparison.OrdinalIgnoreCase));
        if (op is null)
        {
            Console.WriteLine("Could not find One Piece by title — searching by series id");
            // One Piece's ShokoID is 1070; SeriesData.SeriesId in the projection is the Shoko series id
            op = all.FirstOrDefault(s => s.SeriesId == 1070);
        }
        if (op is null)
        {
            Console.WriteLine("Could not find One Piece by any means. Series ids:");
            foreach (var s in all.Take(20))
                Console.WriteLine($"  SeriesId={s.SeriesId}");
            return;
        }

        Console.WriteLine($"One Piece: SeriesId={op.SeriesId} DisplayTitle={op.DisplayTitle} IsMovie={op.IsMovie} Mappings={op.Mappings.Count}");
        var bySeason = op.Mappings.GroupBy(m => m.Season).OrderBy(g => g.Key)
            .Select(g => $"  Season {g.Key}: {g.Count()} mappings");
        foreach (var line in bySeason) Console.WriteLine(line);
    }

    [Fact]
    public void RunOnePieceResolverDiagnostic()
    {
        var dataDir = Environment.GetEnvironmentVariable("ONE_PIECE_TEST_DATA")
            ?? "/tmp/opencode/onepiece_test_data";
        Assert.True(Directory.Exists(dataDir), $"Test data not found at {dataDir}");

        // Find GerDub ID from managed_folders.json
        using var mfDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "managed_folders.json")));
        int gerdubId = -1;
        foreach (var f in mfDoc.RootElement.EnumerateArray())
        {
            var name = f.GetProperty("Name").GetString() ?? "";
            if (name.Contains("GerDub", StringComparison.OrdinalIgnoreCase))
            {
                gerdubId = f.GetProperty("ID").GetInt32();
                break;
            }
        }
        Assert.True(gerdubId > 0, "GerDub folder not found in test data");

        var handler = new MockHttpHandler(dataDir);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/");

        var options = new RelayPathDataSourceOptions(gerdubId, "/mnt/user/array/Anime/Shows/GerDub/")
        {
            ManagedFolderName = "Anime-Shows-GerDub",
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
        var resolver = new ShokoPathResolver(ds, new RelayNamingStrategy());

        // Trigger rebuild and wait for snapshot
        resolver.Rebuild();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!resolver.HasSnapshot && DateTime.UtcNow < deadline)
            Thread.Sleep(100);

        Console.WriteLine($"Snapshot ready: {resolver.HasSnapshot}");
        if (!resolver.HasSnapshot)
        {
            Console.WriteLine("Snapshot did not become ready in 30s.");
            return;
        }

        // 1. Series count in the snapshot
        var root = resolver.ReadDirectory("");
        Console.WriteLine($"Root children count: {root.Count}");
        var rootIds = root.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).Take(20).ToList();
        Console.WriteLine("Root children (first 20): " + string.Join(", ", rootIds));

        // 2. One Piece directory. Test BOTH "69" (AniDB id) and "1070" (Shoko id) since
        //    RelayRawSeries.SeriesId is set to series.IDs.ID = ShokoID, but the user's
        //    actual daemon VFS shows AniDB ID. Need to know which one is used.
        foreach (var candidate in new[] { "1070", "69" })
        {
            var d = resolver.ReadDirectory(candidate);
            Console.WriteLine($"ReadDirectory(\"{candidate}\") -> {d.Count} entries");
            if (d.Count > 0)
            {
                var entries = d.Select(e => $"{e.Name} ({e.NodeType})")
                    .OrderBy(n => n, StringComparer.Ordinal).Take(40).ToList();
                foreach (var s in entries) Console.WriteLine($"  {s}");
            }
        }

        // 3. Drill into the directory that actually has children, count files
        var opDir = resolver.ReadDirectory("1070");
        var totalFiles = 0;
        var seasonFileCounts = new List<(string season, int files)>();
        foreach (var entry in opDir.Where(e => e.NodeType == Shoko.VFS.FUSE.Models.VirtualNodeType.Directory))
        {
            var sub = resolver.ReadDirectory($"1070/{entry.Name}");
            var fileCount = sub.Count(e => e.NodeType == Shoko.VFS.FUSE.Models.VirtualNodeType.File);
            seasonFileCounts.Add((entry.Name, fileCount));
            totalFiles += fileCount;
        }
        Console.WriteLine();
        Console.WriteLine($"One Piece total file entries across seasons: {totalFiles}");
        Console.WriteLine("Per-season file counts:");
        foreach (var (s, f) in seasonFileCounts.OrderBy(t => t.season, StringComparer.Ordinal))
            Console.WriteLine($"  {s}: {f} files");
    }
}
