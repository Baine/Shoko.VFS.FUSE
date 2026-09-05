using System.Net;
using System.Text;
using Newtonsoft.Json;
using Shoko.VFS.FUSE.Host.Api.Models;
using Shoko.VFS.FUSE.Host.Models;

namespace Shoko.VFS.FUSE.Host.Api;

/// <summary>
/// Thin REST client for the Shoko Server API (v3 + legacy <c>/api/auth</c>).
/// Modeled on Shokofin's <c>ShokoApiClient</c>: admin login against <c>POST /api/auth</c>,
/// then every request carries the returned key in the <c>apikey</c> header.
/// </summary>
public sealed class ShokoRestClient
{
    static ShokoRestClient()
    {
        // The Shoko server serializes PartialDateOnly inconsistently (ISO string vs object).
        // JsonConvert.DefaultSettings is not consulted by the no-settings DeserializeObject<T>
        // overloads we use in GetJsonAsync/GetJsonOrNullAsync, so attach the converter
        // explicitly per call rather than relying on a global default.
    }

    private static readonly JsonConverter[] _converters = { new PartialDateOnlyConverter() };

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly int _retries;
    private readonly TimeSpan _baseBackoff;
    private string? _apiKey;

    public ShokoRestClient(HttpClient http, string baseUrl, int retries = 2, TimeSpan? baseBackoff = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL must be specified.", nameof(baseUrl));
        _baseUrl = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _retries = Math.Max(0, retries);
        _baseBackoff = baseBackoff ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>The API key obtained from <see cref="LoginAsync"/>, or null before login.</summary>
    public string? ApiKey => _apiKey;

    /// <summary>Sets a pre-existing API key (bypasses <see cref="LoginAsync"/>).</summary>
    public void SetApiKey(string apiKey) => _apiKey = apiKey;

    /// <summary>
    /// Reachability probe: any HTTP response (regardless of status) means the server
    /// is up; connection refusal / timeout means it is not yet reachable.
    /// </summary>
    public async Task<bool> PingAsync(HttpClient? http = null, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task LoginAsync(string username, string password, string device = "Shoko.VFS.FUSE.Host")
    {
        var body = JsonConvert.SerializeObject(new Dictionary<string, string>
        {
            ["user"] = username,
            ["pass"] = password,
            ["device"] = device,
        });

        using var response = await _http.PostAsync(
            new Uri(_baseUrl, "api/auth"),
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Login failed with {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonConvert.DeserializeObject<AuthResponse>(json);
        if (string.IsNullOrEmpty(dto?.Apikey))
            throw new HttpRequestException("Login succeeded but no API key was returned.");

        _apiKey = dto.Apikey;
    }

    /// <summary><c>GET /api/v3/ManagedFolder</c> — all managed folders.</summary>
    public Task<IReadOnlyList<ManagedFolderDto>> GetManagedFoldersAsync()
        => GetJsonAsync<IReadOnlyList<ManagedFolderDto>>("api/v3/ManagedFolder");

    /// <summary><c>GET /api/v3/ManagedFolder/{id}</c>.</summary>
    public Task<ManagedFolderDto?> GetManagedFolderAsync(int managedFolderId)
        => GetJsonOrNullAsync<ManagedFolderDto>($"api/v3/ManagedFolder/{managedFolderId}");

/// <summary>
/// <c>GET /api/v3/ManagedFolder/{id}/File?pageSize=0&amp;include=XRefs</c> —
/// every file in the managed folder, with per-series cross-reference groups.
/// </summary>
public async Task<IReadOnlyList<FileDto>> GetManagedFolderFilesAsync(int managedFolderId)
{
    var result = await GetJsonOrNullAsync<ListResult<FileDto>>(
        $"api/v3/ManagedFolder/{managedFolderId}/File?pageSize=0&include=XRefs");
    return result?.List ?? [];
}

    /// <summary>
    /// <c>GET /api/v3/Series?pageSize=100&amp;page={n}&amp;includeDataFrom=AniDB,TMDB</c> —
    /// all series (paginated; the server caps pageSize at 100), with AniDB + TMDB blocks.
    /// </summary>
    public async Task<IReadOnlyList<ShokoSeriesDto>> GetAllSeriesAsync()
    {
        var all = new List<ShokoSeriesDto>();
        int page = 1;
        while (true)
        {
            var result = await GetJsonAsync<ListResult<ShokoSeriesDto>>(
                $"api/v3/Series?pageSize=100&page={page}&includeDataFrom=AniDB,TMDB");
            all.AddRange(result.List);
            if (result.List.Count == 0 || all.Count >= result.Total)
                break;
            page++;
        }

        return all;
    }

    /// <summary><c>GET /api/v3/Series/{id}?includeDataFrom=AniDB,TMDB</c>.</summary>
    public Task<ShokoSeriesDto?> GetSeriesAsync(int seriesId)
        => GetJsonOrNullAsync<ShokoSeriesDto>($"api/v3/Series/{seriesId}?includeDataFrom=AniDB,TMDB");

    /// <summary>
    /// <c>GET /api/v3/Series/{seriesId}/Episode</c> — every episode of the series with its
    /// AniDB block (type/number/titles), TMDB block (show/move links with ordering data),
    /// files (locations) and cross-references. Hidden/missing/unaired episodes included.
    /// </summary>
    public async Task<IReadOnlyList<ShokoEpisodeDto>> GetSeriesEpisodesAsync(int seriesId)
    {
        var result = await GetJsonOrNullAsync<ListResult<ShokoEpisodeDto>>(
            "api/v3/Series/" + seriesId +
            "/Episode?pageSize=0&includeHidden=true&includeMissing=true&includeUnaired=true" +
            "&includeDataFrom=AniDB,TMDB&includeFiles=true&includeXRefs=true");
        return result?.List ?? [];
    }

    /// <summary>
    /// <c>GET /api/v3/Series/{seriesId}/TMDB/Show?include=Ordering</c> — TMDB shows linked
    /// to the series, including the orderings in use.
    /// </summary>
    public Task<IReadOnlyList<TmdbShowDto>> GetSeriesTmdbShowsAsync(int seriesId)
        => GetJsonAsync<IReadOnlyList<TmdbShowDto>>($"api/v3/Series/{seriesId}/TMDB/Show?include=Ordering");

    /// <summary>
    /// <c>GET /api/v3/TMDB/Show/{showId}/Episode</c> — every TMDB episode of the show with
    /// its full ordering list, forced to the default ordering so base coordinates come back
    /// (<c>alternateOrderingID=default</c>).
    /// </summary>
    public async Task<IReadOnlyList<TmdbEpisodeDto>> GetTmdbShowEpisodesAsync(int tmdbShowId)
    {
        var all = new List<TmdbEpisodeDto>();
        int page = 1;
        while (true)
        {
            var result = await GetJsonAsync<ListResult<TmdbEpisodeDto>>(
                $"api/v3/TMDB/Show/{tmdbShowId}/Episode?include=Ordering&alternateOrderingID=default&pageSize=1000&page={page}");
            all.AddRange(result.List);
            if (result.List.Count == 0 || all.Count >= result.Total)
                break;
            page++;
        }

        return all;
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUrl, path));
        if (_apiKey is not null)
            request.Headers.Add("apikey", _apiKey);
        return request;
    }

    /// <summary>
    /// Sends the request, retrying transient failures (connection errors, 5xx, request
    /// timeouts) up to <see cref="_retries"/> times with exponential backoff. 4xx (other
    /// than 408 / 429) is treated as a permanent failure and propagates immediately.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _retries; attempt++)
        {
            try
            {
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (attempt < _retries && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception ex) when (attempt < _retries && IsTransientException(ex))
            {
                last = ex;
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new HttpRequestException("Send failed after retries with no captured exception.");
    }

    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.RequestTimeout      // 408
        || status == HttpStatusCode.TooManyRequests      // 429
        || (int)status >= 500;                           // 5xx

    private static bool IsTransientException(Exception ex)
        => ex is HttpRequestException
        || ex is TaskCanceledException                  // HttpClient.Timeout
        || ex is IOException
        || ex is OperationCanceledException;

    private async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        // 100ms, 400ms, 1.6s, ... (×4 per attempt).
        var delay = TimeSpan.FromMilliseconds(_baseBackoff.TotalMilliseconds * Math.Pow(4, attempt));
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled; let the outer call observe.
            throw;
        }
    }

    private async Task<T> GetJsonAsync<T>(string path)
    {
        using var request = CreateRequest(path);
        using var response = await SendWithRetryAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {path} failed with {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<T>(json, _converters)
            ?? throw new HttpRequestException($"GET {path} returned an empty body.");
    }

    private async Task<T?> GetJsonOrNullAsync<T>(string path)
    {
        using var request = CreateRequest(path);
        using var response = await SendWithRetryAsync(request).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {path} failed with {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<T>(json, _converters);
    }
}