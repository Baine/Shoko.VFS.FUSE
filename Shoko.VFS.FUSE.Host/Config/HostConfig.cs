using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Host.Config;

/// <summary>
/// Host daemon configuration. Mirrors the plugin's <c>FusePluginConfiguration</c> relay fields,
/// plus host-specific settings for connection, FUSE, health, and reconcile behaviour.
/// </summary>
public sealed class HostConfig
{
    // -- Connection --

    /// <summary>Shoko Server base URL, e.g. <c>http://localhost:8888</c>.</summary>
    public string? ShokoUrl { get; set; }

    /// <summary>Shoko admin user name.</summary>
    public string? ShokoUser { get; set; }

    /// <summary>Shoko admin password.</summary>
    public string? ShokoPass { get; set; }

    /// <summary>Pre-existing Shoko API key (bypasses login).</summary>
    public string? ShokoApiKey { get; set; }

    // -- Relay settings (mirrors FusePluginConfiguration relay fields) --

    /// <summary>Enable the Relay mount. Default: <c>true</c>.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>TV VFS root folder name inside each managed folder. Default: <c>!ShokoRelayVFS</c>.</summary>
    public string RelayTvFolderName { get; set; } = "!ShokoRelayVFS";

    /// <summary>Movie VFS root folder name inside each managed folder. Default: <c>!ShokoRelayMovieVFS</c>.</summary>
    public string RelayMovieFolderName { get; set; } = "!ShokoRelayMovieVFS";

    /// <summary>
    /// Movie generation mode: 0=Disabled, 1=EnabledMaintain, 2=EnabledRemove.
    /// Default: 0.
    /// </summary>
    public MovieGenerationMode MovieGenerationMode { get; set; } = MovieGenerationMode.Disabled;

    /// <summary>Whether Relay uses TMDB episode numbering. Default: <c>true</c>.</summary>
    public bool TmdbEpNumbering { get; set; } = true;

    /// <summary>Whether Relay merges series linked to the same TMDB show. Default: <c>false</c>.</summary>
    public bool MergeTmdbSeries { get; set; }

    /// <summary>Whether Relay applies Plex local-extra exclusions to indexed files. Default: <c>true</c>.</summary>
    public bool PlexLocalExtras { get; set; } = true;

    /// <summary>Newline-separated source path segments excluded from Relay indexing.</summary>
    public string FolderExclusions { get; set; } = "";

    /// <summary>Newline-separated managed-folder IDs or names excluded from Relay indexing.</summary>
    public string ManagedFolderExclusions { get; set; } = "";

    /// <summary>Comma-separated Relay series title language preference. Default: <c>SHOKO</c>.</summary>
    public string SeriesTitleLanguage { get; set; } = "SHOKO";

    /// <summary>Comma-separated Relay episode title language preference. Default: <c>SHOKO</c>.</summary>
    public string EpisodeTitleLanguage { get; set; } = "SHOKO";

    /// <summary>Whether common Relay series-title prefixes move to the end. Default: <c>true</c>.</summary>
    public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

    /// <summary>Whether TMDB group names are preferred for multi-link episodes. Default: <c>true</c>.</summary>
    public bool TmdbEpGroupNames { get; set; } = true;

    // -- FUSE --

    /// <summary>FUSE allow_other option. Default: <c>false</c>.</summary>
    public bool FuseAllowOther { get; set; }

    /// <summary>
    /// UID reported by FUSE for files on the mount. Default: <c>99</c> (Unraid's <c>nobody</c>).
    /// </summary>
    public uint FuseMountUid { get; set; } = 99;

    /// <summary>
    /// GID reported by FUSE for files on the mount. Default: <c>100</c> (Unraid's <c>users</c>).
    /// </summary>
    public uint FuseMountGid { get; set; } = 100;

    /// <summary>Attribute cache timeout in seconds. Default: 2.0.</summary>
    public double AttrTimeout { get; set; } = 2.0;

    /// <summary>Entry (dentry) cache timeout in seconds. Default: 2.0.</summary>
    public double EntryTimeout { get; set; } = 2.0;

    /// <summary>Negative entry cache timeout for ENOENT lookups. Default: 0.5.</summary>
    public double NegativeTimeout { get; set; } = 0.5;

    // -- Host path mapping --

    /// <summary>
    /// Server-side path prefix to strip when re-mapping under <see cref="ManagedFolderPathRoot"/>.
    /// When empty or equal to <see cref="ManagedFolderPathRoot"/>, paths are identity-mapped.
    /// Example: ServerPathRoot=/mnt/shoko, ManagedFolderPathRoot=/mnt → /mnt/shoko/import becomes /mnt/import.
    /// </summary>
    public string? ServerPathRoot { get; set; }

    /// <summary>
    /// Host-side path prefix to prepend when re-mapping server paths.
    /// When empty or equal to <see cref="ServerPathRoot"/>, server paths are used as-is.
    /// </summary>
    public string? ManagedFolderPathRoot { get; set; }

    // -- Health --

    /// <summary>
    /// Health endpoint port (binds on 127.0.0.1; 0 = disabled). Default: 8790.
    /// </summary>
    public int HealthPort { get; set; } = 8790;

    // -- Reconcile --

    /// <summary>
    /// Safety-reconcile interval (fallback if events are lost). Default: 300 seconds.
    /// Set to zero to disable periodic safety reconciles.
    /// </summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Content-dirty event coalescing window. Events within this window trigger one rebuild.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan ContentDirtyCooldown { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Topology-dirty event coalescing window (managed folder add/update/remove).
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan TopologyDirtyCooldown { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Server startup wait timeout before giving up. Default: 120 seconds.
    /// </summary>
    public TimeSpan ServerStartupTimeout { get; set; } = TimeSpan.FromSeconds(120);

    // -- Cache --

    /// <summary>
    /// TTL for the ShokoRelayDataSource aggregation cache. Default: 30 seconds.
    /// Set to zero to disable caching (every call rebuilds).
    /// </summary>
    public TimeSpan AggregationCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum concurrent per-series fetches during aggregation. Default: 4.
    /// Only used when aggregation caching is enabled.
    /// </summary>
    public int AggregationFetchDegree { get; set; } = 4;

    /// <summary>
    /// Directory where per-mount snapshot files + clean-shutdown markers are persisted.
    /// Default: <c>$XDG_DATA_HOME/shoko-vfs-fuse/snapshots</c> or
    /// <c>~/.local/share/shoko-vfs-fuse/snapshots</c>. Empty disables persistence.
    /// </summary>
    public string? SnapshotCacheDir { get; set; }

    /// <summary>
    /// Whether to freeze (serve stale) the aggregation cache while the Shoko server is
    /// detected as down. Default: <c>true</c>.
    /// </summary>
    public bool CacheFreezeOnOutage { get; set; } = true;

    /// <summary>
    /// Background ping interval for the server-availability monitor. Default: 5 seconds.
    /// </summary>
    public TimeSpan ServerPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive failed probes before the server is declared down. Default: 3.
    /// One successful probe is enough to declare it back up.
    /// </summary>
    public int MaxServerProbeFailures { get; set; } = 3;

    /// <summary>
    /// Per-request retry budget for transient HTTP failures (5xx, connection resets,
    /// request timeouts). Default: 2 retries with exponential backoff (100ms, 400ms).
    /// Set to 0 to disable.
    /// </summary>
    public int HttpRetries { get; set; } = 2;

    /// <summary>
    /// Hard ceiling for a single HTTP request. Default: 10 minutes — covers large-library
    /// aggregations where <c>GET /api/v3/ManagedFolder/{id}/File?pageSize=0&amp;include=XRefs</c>
    /// can take several minutes on slow disks. The default <c>HttpClient.Timeout</c> (100s)
    /// aborts these.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    // -- Validation --

    /// <summary>
    /// Number of sample resolved SourcePaths to check after first aggregation. Default: 5.
    /// </summary>
    public int PathValidationSamples { get; set; } = 5;

    /// <summary>
    /// Max source path length for sampling (skip very long paths). Default: 500 characters.
    /// </summary>
    public int PathValidationMaxPathLength { get; set; } = 500;

    /// <summary>
    /// Loads configuration from an optional JSON file (Newtonsoft, JSONC comments allowed),
    /// then applies environment-variable overrides. Missing/absent values keep their defaults.
    /// </summary>
    public static HostConfig Load(string? configPath = null)
    {
        var config = new HostConfig();
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                Converters = { new TimeSpanStringConverter() },
            };
            JsonConvert.PopulateObject(File.ReadAllText(configPath), config, settings);
        }

        config.ShokoUrl = GetEnv("SHOKO_URL") ?? config.ShokoUrl;
        config.ShokoUser = GetEnv("SHOKO_USER") ?? config.ShokoUser;
        config.ShokoPass = GetEnv("SHOKO_PASS") ?? config.ShokoPass;
        config.ShokoApiKey = GetEnv("SHOKO_API_KEY") ?? config.ShokoApiKey;
        return config;
    }

    /// <summary>Parses relaxed TimeSpan strings such as <c>300s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c> or <c>ms</c>.</summary>
    public static TimeSpan ParseTimeSpan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        var match = Regex.Match(value.Trim(), @"^(?<v>\d+(?:[.,]\d+)?)(?<u>ms|s|m|h|d)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new FormatException($"Cannot parse TimeSpan '{value}'.");
        double v = double.Parse(match.Groups["v"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(v),
            "s" => TimeSpan.FromSeconds(v),
            "m" => TimeSpan.FromMinutes(v),
            "h" => TimeSpan.FromHours(v),
            "d" => TimeSpan.FromDays(v),
            _ => throw new FormatException($"Cannot parse TimeSpan '{value}'."),
        };
    }

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed class TimeSpanStringConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) =>
            objectType == typeof(TimeSpan) || objectType == typeof(TimeSpan?);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType == JsonToken.Integer)
                return TimeSpan.FromSeconds(Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture));
            return ParseTimeSpan(reader.Value?.ToString());
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            => writer.WriteValue(value is null ? null : value.ToString());
    }
}