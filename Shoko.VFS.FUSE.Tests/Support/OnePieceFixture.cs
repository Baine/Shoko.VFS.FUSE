using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Shoko.VFS.FUSE.Tests.Support;

/// <summary>
/// Records the One Piece diagnostic fixtures from a live Shoko server on first use,
/// so the <see cref="Host.OnePieceEndToEndDiagnostic"/> / <c>OnePieceProjectionDiagnostic</c>
/// tests can run offline afterwards via their mock HTTP handler.
///
/// Server URL + API key come from the SHOKO_URL / SHOKO_API_KEY environment variables
/// or the repository's <c>.env</c> file. The key is only ever sent as a request header —
/// it is never logged or echoed.
///
/// Recorded payloads (same shapes the ShokoRelayDataSource consumes live):
/// - managed_folders.json  GET /api/v3/ManagedFolder
/// - gerdub_files.json     GET /api/v3/ManagedFolder/{id}/File?pageSize=10000&page=1&include=XRefs
///   (Total is patched to the page size: the mock serves one body for every page,
///   so multi-page folders are recorded as a single page)
/// - all_series.json       concatenated GET /api/v3/Series?pageSize=100&page=N&includeDataFrom=AniDB,TMDB
/// - series_episodes.json  GET /api/v3/Series/{onePieceId}/Episode?pageSize=0&includeHidden=true
///                         &includeMissing=true&includeUnaired=true&includeDataFrom=AniDB,TMDB
///                         &includeFiles=true&includeXRefs=true
/// - op_eps.json           copy of series_episodes.json (projection diagnostic input)
/// </summary>
public static class OnePieceFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ListResult
    {
        public int Total { get; set; }
        public System.Text.Json.JsonElement List { get; set; }
    }

    /// <summary>Records <paramref name="dataDir"/> (and the op_eps.json copy) if not present. Thread-safe.</summary>
    public static async Task EnsureRecordedAsync(string dataDir, string opEpsPath)
    {
        if (File.Exists(Path.Combine(dataDir, "managed_folders.json"))
            && File.Exists(Path.Combine(dataDir, "gerdub_files.json"))
            && File.Exists(Path.Combine(dataDir, "all_series.json"))
            && File.Exists(Path.Combine(dataDir, "series_episodes.json"))
            && File.Exists(opEpsPath))
            return;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate (a parallel class already recorded).
            if (File.Exists(Path.Combine(dataDir, "series_episodes.json")) && File.Exists(opEpsPath))
                return;

            var (baseUrl, apiKey) = ResolveCredentials();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Add("apikey", apiKey);

            Directory.CreateDirectory(dataDir);

            // 1. Managed folders → find the Anime-Shows-GerDub folder id.
            var folders = await http.GetFromJsonAsync<List<JsonElement>>("api/v3/ManagedFolder").ConfigureAwait(false)
                ?? throw new InvalidOperationException("GET /api/v3/ManagedFolder returned nothing.");
            int gerdubId = folders
                .Where(f => (f.TryGetProperty("Name", out var n) ? n.GetString() : null)?
                    .Contains("Anime-Shows-GerDub", StringComparison.OrdinalIgnoreCase) == true)
                .Select(f => f.GetProperty("ID").GetInt32())
                .FirstOrDefault();
            if (gerdubId <= 0)
                throw new InvalidOperationException("No 'Anime-Shows-GerDub' managed folder found on the server.");

            await File.WriteAllTextAsync(
                Path.Combine(dataDir, "managed_folders.json"),
                JsonSerializer.Serialize(folders)).ConfigureAwait(false);

            // 2. GerDub files (single page; see class doc for the Total patch).
            var files = await http.GetFromJsonAsync<ListResult>(
                $"api/v3/ManagedFolder/{gerdubId}/File?pageSize=10000&page=1&include=XRefs").ConfigureAwait(false)
                ?? throw new InvalidOperationException("GET managed folder files returned nothing.");
            files.Total = Math.Max(files.Total, 10000);
            await File.WriteAllTextAsync(
                Path.Combine(dataDir, "gerdub_files.json"),
                JsonSerializer.Serialize(files)).ConfigureAwait(false);

            // 3. All series, page by page, concatenated (legacy mock format).
            var allSeries = new StringBuilder();
            int total = -1, page = 1, onePieceId = -1;
            while (true)
            {
                var result = await http.GetFromJsonAsync<ListResult>(
                    $"api/v3/Series?pageSize=100&page={page}&includeDataFrom=AniDB,TMDB").ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GET /api/v3/Series page {page} returned nothing.");
                if (page == 1)
                    total = result.Total;
                allSeries.Append(JsonSerializer.Serialize(result.List)).Append('\n');
                foreach (var series in result.List.EnumerateArray())
                {
                    if ((series.TryGetProperty("Name", out var name) ? name.GetString() : null) == "One Piece"
                        && series.TryGetProperty("IDs", out var ids))
                        onePieceId = ids.GetProperty("ID").GetInt32();
                }
                page++;
                if (result.List.GetArrayLength() == 0 || (page - 1) * 100 >= total)
                    break;
            }
            await File.WriteAllTextAsync(Path.Combine(dataDir, "all_series.json"), allSeries.ToString()).ConfigureAwait(false);

            if (onePieceId <= 0)
                throw new InvalidOperationException("Series named 'One Piece' not found on the server.");

            // 4. One Piece episodes (with files, xrefs, AniDB + TMDB blocks).
            var episodes = await http.GetStringAsync(
                $"api/v3/Series/{onePieceId}/Episode?pageSize=0&includeHidden=true&includeMissing=true" +
                "&includeUnaired=true&includeDataFrom=AniDB,TMDB&includeFiles=true&includeXRefs=true").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dataDir, "series_episodes.json"), episodes).ConfigureAwait(false);

            // 5. Same payload feeds the projection diagnostic.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opEpsPath))!);
            await File.WriteAllTextAsync(opEpsPath, episodes).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static (string BaseUrl, string ApiKey) ResolveCredentials()
    {
        var url = Environment.GetEnvironmentVariable("SHOKO_URL");
        var key = Environment.GetEnvironmentVariable("SHOKO_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            (url, key) = ReadDotEnv();

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "One Piece fixtures missing and no credentials found. Set SHOKO_API_KEY/SHOKO_URL " +
                "(env or repository .env) to record them from a live Shoko server, or point " +
                "ONE_PIECE_TEST_DATA / ONE_PIECE_JSON at existing fixture files.");
        return (string.IsNullOrWhiteSpace(url) ? "http://localhost:8111" : url.TrimEnd('/'), key);
    }

    /// <summary>Reads SHOKO_URL/SHOKO_API_KEY from the repository .env (searching upwards). Values are not echoed.</summary>
    private static (string Url, string Key) ReadDotEnv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(path))
                continue;

            string? url = null, key = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("SHOKO_URL=", StringComparison.Ordinal))
                    url = line["SHOKO_URL=".Length..].Trim().Trim('"', '\'');
                else if (line.StartsWith("SHOKO_API_KEY=", StringComparison.Ordinal))
                    key = line["SHOKO_API_KEY=".Length..].Trim().Trim('"', '\'');
            }
            return (url ?? "", key ?? "");
        }
        return ("", "");
    }
}
