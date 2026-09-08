// REST-based lazy data source for the Relay path resolver.
//
// Replicates the aggregation chain of the in-process RelayShokoPathDataSource
// (Shoko.VFS.FUSE/Resolvers/RelayShokoPathDataSource.cs) over the Shoko
// Server REST API instead of IMetadataService — split into a cheap structure
// pass (series-level nodes only) plus per-series subtrees fetched on demand:
//
// Structure pass (GetSeriesStructure):
//   1. GET /api/v3/ManagedFolder/{id}/File?pageSize=10000&page={n}&include=XRefs -> seed ids
//   2. GET /api/v3/Series?includeDataFrom=AniDB,TMDB      -> grouping metadata for all series
//   3. RelaySeriesGrouper.GetGroupClosure + Group         -> closed series with empty Mappings
//   4. GET /api/v3/Series/{id}/Episode (bare, per movie)  -> main episode id for folder names
//
// Per-series pass (GetSeriesData, cached + invalidated per series):
//   5. GET /api/v3/Series/{id}/Episode (AniDB+TMDB+Files+XRefs) -> RelayRaw* extraction
//   6. RelayMappingProjector.Project + RelaySeriesGrouper.Merge -> full SeriesData (+Theme.mp3)
//
// Title/coordinate semantics mirror the plugin 1:1, including the TMDB
// alternate-ordering handling (see ExtractTmdbEpisodesAsync).

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.VFS.FUSE.Host.Api;
using Shoko.VFS.FUSE.Host.Api.Models;
using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Host.Models;
using Shoko.VFS.FUSE.Resolvers;
using AbstractionsDate = Shoko.Abstractions.Metadata.PartialDateOnly;
using AbstractionsDropFolderType = Shoko.Abstractions.Video.Enums.DropFolderType;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

public sealed class ShokoRelayDataSource : IShokoPathDataSource, ILazyShokoPathDataSource, IDisposable
{
    private static readonly Regex s_seriesPrefixRegex = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
    private static readonly Regex s_defaultTitleRegex = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex s_localExtraDirRegex = new(@"^(Behind The Scenes|Deleted Scenes|Featurettes|Interviews|Scenes|Shorts|Trailers|Other)(\s+[sS](\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlySet<string> s_ambiguousTitles = new HashSet<string>(
        ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex s_movieDescriptorRegex = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);

    private readonly ShokoRestClient _client;
    private readonly RelayPathDataSourceOptions _options;
    private readonly int _managedFolderId;
    private readonly string _managedFolderRoot;
    private readonly StringComparison _pathComparison;
    private readonly DataSourceCache? _cache;
    private readonly TimeSpan _ttl;
    private readonly int _maxDegree;
    private readonly FileSnapshotStore? _snapshotStore;
    private readonly string? _snapshotKey;

    /// <summary>Warm per-series data keyed by PRIMARY series id (merged groups share one entry).</summary>
    private readonly ConcurrentDictionary<int, SeriesCacheEntry> _seriesCache = new();

    /// <summary>In-flight per-series builds for single-flight outside the resolver's own per-series gate.</summary>
    private readonly ConcurrentDictionary<int, Task<SeriesData?>> _seriesFetches = new();

    /// <summary>Primary series id → all member ids of its group (from the last structure pass).</summary>
    private readonly ConcurrentDictionary<int, IReadOnlyList<int>> _groupMembers = new();

    /// <summary>Any member id → primary series id (from the last structure pass).</summary>
    private readonly ConcurrentDictionary<int, int> _memberToPrimary = new();

    /// <summary>File id → cross-referenced series ids, from the structure pass' file listing. Immutable swap.</summary>
    private volatile IReadOnlyDictionary<int, int[]> _fileSeriesIndex = new Dictionary<int, int[]>();

    /// <summary>Last non-empty structure-pass series count, to detect flapping to empty.</summary>
    private int _lastSeriesCount;

    public ShokoRelayDataSource(ShokoRestClient client, RelayPathDataSourceOptions options,
        TimeSpan cacheTtl = default, int maxDegree = 4,
        FileSnapshotStore? snapshotStore = null, string? snapshotKey = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (options.ManagedFolderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.ManagedFolderId));
        if (string.IsNullOrWhiteSpace(options.ManagedFolderRoot))
            throw new ArgumentException("Managed folder root must be specified.", nameof(options.ManagedFolderRoot));

        try
        {
            _managedFolderRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ManagedFolderRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Managed folder root is not a valid path.", nameof(options.ManagedFolderRoot), ex);
        }

        if (string.IsNullOrWhiteSpace(_managedFolderRoot))
            throw new ArgumentException("Managed folder root must be specified.", nameof(options.ManagedFolderRoot));

        _managedFolderId = options.ManagedFolderId;
        _options = options;
        _ttl = cacheTtl;
        _maxDegree = Math.Max(1, maxDegree);
        _snapshotStore = snapshotStore;
        _snapshotKey = snapshotKey;
        if (snapshotStore is not null && string.IsNullOrWhiteSpace(snapshotKey))
            throw new ArgumentException("snapshotKey required when snapshotStore is set.", nameof(snapshotKey));
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // The cache now fronts the cheap structure pass only; per-series data has its own
        // per-id cache below. Snapshot persistence is handled directly against the store
        // (it persists structure + warm per-series entries combined).
        if (cacheTtl > TimeSpan.Zero)
            _cache = new DataSourceCache(GetSeriesStructureCoreAsync, cacheTtl, maxDegree);
    }

    /// <summary>Synchronous entry point required by <see cref="IShokoPathDataSource"/>.</summary>
    public IReadOnlyList<SeriesData> GetAllSeries()
        => GetAllSeriesAsync().GetAwaiter().GetResult();

    /// <summary>Structure entries plus all warm per-series entries (appended), the snapshot-store shape.</summary>
    public async Task<IReadOnlyList<SeriesData>> GetAllSeriesAsync()
    {
        var structure = _cache is not null
            ? await _cache.GetAsync().ConfigureAwait(false)
            : await GetSeriesStructureCoreAsync().ConfigureAwait(false);
        return CombineWithWarmEntries(structure);
    }

    #region ILazyShokoPathDataSource

    /// <summary>
    /// Cheap series-level layout: seeds + grouping closure with empty Mappings (plus one
    /// synthetic main-episode mapping per movie group so its episode-id folder is listed).
    /// Cached + single-flighted via <see cref="_cache"/>.
    /// </summary>
    public IReadOnlyList<SeriesData> GetSeriesStructure()
        => (_cache is not null
                ? _cache.GetAsync()
                : GetSeriesStructureCoreAsync())
            .GetAwaiter().GetResult();

    /// <summary>
    /// Full per-series data (Mappings + Extras) for one series (or merged group), fetched
    /// on first access and cached per series id under the same TTL semantics as before.
    /// </summary>
    public SeriesData? GetSeriesData(int seriesId)
    {
        int primary = _memberToPrimary.GetValueOrDefault(seriesId, seriesId);
        if (IsFrozen)
            return _seriesCache.TryGetValue(primary, out var pinned) ? pinned.Data : null;

        if (_ttl > TimeSpan.Zero
            && _seriesCache.TryGetValue(primary, out var entry)
            && DateTime.UtcNow - entry.CachedAt < _ttl)
            return entry.Data;

        return FetchSeriesAsync(primary).GetAwaiter().GetResult();
    }

    /// <summary>Drop the warm cache for one series (or all + the structure cache when null).</summary>
    public void Invalidate(int? seriesId)
    {
        if (IsFrozen)
            return;
        if (seriesId is null)
        {
            _cache?.Invalidate();
            _seriesCache.Clear();
            return;
        }
        _seriesCache.TryRemove(_memberToPrimary.GetValueOrDefault(seriesId.Value, seriesId.Value), out _);
    }

    /// <summary>Maps member series id → merged-group primary id (identity when unmapped).</summary>
    public int ResolvePrimarySeriesId(int seriesId) => _memberToPrimary.GetValueOrDefault(seriesId, seriesId);

    /// <summary>
    /// Series ids cross-referenced by a file, from the structure pass' file listing.
    /// Null when the file is unknown (new/unindexed — caller should fully invalidate).
    /// </summary>
    public IReadOnlyList<int>? GetSeriesIdsForFile(int fileId)
        => _fileSeriesIndex.TryGetValue(fileId, out var ids) ? ids : null;

    #endregion

    /// <summary>Forces the aggregation cache to rebuild on the next request.</summary>
    public void Invalidate() => Invalidate((int?)null);

    /// <summary>Pins the current cache so the FUSE mount keeps serving the last known snapshot.</summary>
    public void Freeze() => _cache?.Freeze();

    /// <summary>Releases the freeze; the next request rebuilds from the server.</summary>
    public void Unfreeze() => _cache?.Unfreeze();

    /// <summary>Whether the cache is currently frozen (serving stale data).</summary>
    public bool IsFrozen => _cache?.IsFrozen ?? false;

    /// <summary>
    /// Loads the persisted snapshot from the configured <see cref="FileSnapshotStore"/>:
    /// entries with real (FileId&gt;0) mappings prime the per-series cache (zero refetch);
    /// the rest prime the structure cache.
    /// </summary>
    public bool TryLoadFromStore()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return false;
        var loaded = _snapshotStore.TryLoad(_snapshotKey);
        if (loaded is null)
            return false;

        var structure = new List<SeriesData>();
        foreach (var entry in loaded)
        {
            if (IsStructureEntry(entry))
                structure.Add(entry);
            else if (_ttl > TimeSpan.Zero)
                _seriesCache[entry.SeriesId] = new SeriesCacheEntry(entry, DateTime.UtcNow);
        }

        _cache?.Prime(structure);
        return true;
    }

    /// <summary>Persists structure entries plus all warm per-series entries via the snapshot store.</summary>
    public void SaveToStore()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return;
        var snapshot = GetSnapshot();
        if (snapshot is null)
            return;
        _snapshotStore.Save(_snapshotKey, snapshot);
    }

    /// <summary>
    /// Current snapshot payload (structure + warm per-series entries) without writing to
    /// the store. Null when the aggregation cache is unavailable (TTL disabled or the
    /// structure pass has not run yet).
    /// </summary>
    public IReadOnlyList<SeriesData>? GetSnapshot()
        => _cache?.PeekCached() is { } structure ? CombineWithWarmEntries(structure) : null;

    /// <summary>True when a snapshot store is configured and the last shutdown was clean.</summary>
    public bool WasLastShutdownClean()
        => _snapshotStore is not null && _snapshotKey is not null
           && _snapshotStore.WasLastShutdownClean(_snapshotKey);

    /// <summary>Discards any persisted snapshot + clean-shutdown marker.</summary>
    public void InvalidateStored()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return;
        _snapshotStore.Clear(_snapshotKey);
    }

    public void Dispose() { }

    private IReadOnlyList<SeriesData> CombineWithWarmEntries(IReadOnlyList<SeriesData> structure)
    {
        var warm = _seriesCache.Values
            .Where(entry => entry.Data is not null && !IsStructureEntry(entry.Data))
            .Select(entry => entry.Data)
            .ToList();
        return warm.Count == 0 ? structure : [.. structure, .. warm];
    }

    /// <summary>
    /// Structure entries carry no real mappings: empty, or synthetic movie folder-name
    /// placeholders (FileId 0). Anything with a FileId&gt;0 mapping is a full per-series entry.
    /// </summary>
    private static bool IsStructureEntry(SeriesData entry) =>
        entry.Mappings is null || entry.Mappings.All(mapping => mapping.FileId == 0);

    private async Task<SeriesData?> FetchSeriesAsync(int primaryId)
    {
        // Single-flight per primary id (the resolver already serializes per series; this also
        // covers direct callers). One fetch per burst, everyone awaits the same task.
        var tcs = new TaskCompletionSource<SeriesData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _seriesFetches.GetOrAdd(primaryId, tcs.Task);
        if (!ReferenceEquals(existing, tcs.Task))
            return await existing.ConfigureAwait(false);

        try
        {
            var data = await BuildSeriesDataAsync(primaryId).ConfigureAwait(false);
            if (data is not null && _ttl > TimeSpan.Zero)
                _seriesCache[primaryId] = new SeriesCacheEntry(data, DateTime.UtcNow);
            tcs.SetResult(data);
            return data;
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            throw;
        }
        finally
        {
            _seriesFetches.TryRemove(primaryId, out _);
        }
    }

    /// <summary>
    /// Structure pass: file seeds + series-level grouping only. No full per-series episode
    /// fetches — just one bare (lite) episode listing per movie group for its folder name.
    /// </summary>
    private async Task<IReadOnlyList<SeriesData>> GetSeriesStructureCoreAsync(CancellationToken ct = default)
    {
        if (!IsEligibleManagedFolder())
            return [];

        // Seed: series that have at least one file located in this managed folder.
        // Also builds the file→series xref index used for per-file event invalidation.
        var folderFiles = await _client.GetManagedFolderFilesAsync(_managedFolderId).ConfigureAwait(false);
        var (fileSeries, localSeedIds) = BuildSeedIndex(folderFiles);
        if (localSeedIds.Count == 0)
        {
            // An empty seed set is either genuine (the folder really holds no indexed
            // files) or a transient Shoko state (server mid-restart, import in flight,
            // xrefs not materialized). Verify against the API before trusting it.
            if (folderFiles.Count == 0)
            {
                var recheck = await _client.GetManagedFolderFilesAsync(_managedFolderId).ConfigureAwait(false);
                if (recheck.Count > 0)
                {
                    folderFiles = recheck;
                    (fileSeries, localSeedIds) = BuildSeedIndex(folderFiles);
                }
            }

            if (localSeedIds.Count == 0)
            {
                int librarySeries = (await _client.GetAllSeriesAsync().ConfigureAwait(false)).Count;
                bool verified = folderFiles.Count == 0 && librarySeries > 0;
                string detail = folderFiles.Count == 0
                    ? verified
                        ? $"verified empty: two consecutive file listings returned 0 files while the library reports {librarySeries} series"
                        : $"file listing returned 0 files and the series library reports 0 series (Shoko likely mid-restart)"
                    : $"{folderFiles.Count} files listed but none cross-referenced to a series (xrefs not materialized?)";
                ThrowIfTransientEmpty(0, verified, detail);
                _fileSeriesIndex = fileSeries;
                return [];
            }
        }
        _fileSeriesIndex = fileSeries;

        bool enforceTmdbNumbering = _options.TmdbEpNumbering || _options.MergeTmdbSeries;
        var manualOverrides = enforceTmdbNumbering ? _options.ManualOverrideGroups : [];

        var allSeries = await _client.GetAllSeriesAsync().ConfigureAwait(false);
        var groupingMetadata = allSeries
            .Select(series => new RelaySeriesGroupingMetadata(
                series.IDs.ID,
                series.AniDB?.ID ?? 0,
                _options.MergeTmdbSeries ? MapAirDate(series.AniDB?.AirDate) : null,
                _options.MergeTmdbSeries ? series.TMDB?.Shows.FirstOrDefault()?.ID : null
            ))
            .ToArray();
        var groupClosure = RelaySeriesGrouper.GetGroupClosure(
            groupingMetadata,
            localSeedIds,
            _options.MergeTmdbSeries,
            manualOverrides
        );

        // Series-level projection: same title/IsMovie semantics as the eager Project+Merge
        // chain (isMovie = UseTmdbNumbering ? TmdbMovies.Any() : AniDB.Type == Movie), but
        // without touching episodes or files.
        var inputs = new List<RelaySeriesGroupingInput>();
        foreach (var series in allSeries.Where(s => groupClosure.Contains(s.IDs.ID)))
        {
            var data = new SeriesData(
                series.IDs.ID,
                ResolveSeriesTitle(series),
                enforceTmdbNumbering
                    ? (series.TMDB?.Movies.Count ?? 0) > 0
                    : series.AniDB?.Type == AnimeType.Movie,
                Array.Empty<EpisodeData>());
            inputs.Add(new RelaySeriesGroupingInput(
                data,
                series.AniDB?.ID ?? 0,
                _options.MergeTmdbSeries ? MapAirDate(series.AniDB?.AirDate) : null,
                _options.MergeTmdbSeries ? series.TMDB?.Shows.FirstOrDefault()?.ID : null
            ));
        }

        // Merge over structure entries (empty Mappings concatenate fine) so group
        // consolidation — primary id, member ids — matches the eager path.
        var groups = RelaySeriesGrouper.Group(inputs, _options.MergeTmdbSeries, manualOverrides);
        // Guard before clobbering the group maps: a throw keeps the previous maps intact
        // so per-series lookups keep working against the still-published snapshot.
        ThrowIfTransientEmpty(groups.Count, verified: false, "series grouping produced no groups despite non-empty seeds");
        _groupMembers.Clear();
        _memberToPrimary.Clear();
        foreach (var group in groups)
        {
            _groupMembers[group.PrimarySeriesId] = group.SeriesIds;
            foreach (var id in group.SeriesIds)
                _memberToPrimary[id] = group.PrimarySeriesId;
        }

        // Movie folder names are FormatMovieFolder(main episode id): fetch a bare episode
        // listing per movie member and synthesize one placeholder mapping so the resolver
        // lists the folder (and routes accesses into the lazy subtree). Extras and real
        // files belong to the per-series pass.
        var moviePlaceholders = new ConcurrentDictionary<int, IReadOnlyList<EpisodeData>>();
        await Parallel.ForEachAsync(
            groups.Where(group => group.Data.IsMovie),
            new ParallelOptions { MaxDegreeOfParallelism = _maxDegree, CancellationToken = ct },
            async (group, _) =>
            {
                var mappings = new List<EpisodeData>();
                foreach (var memberId in group.SeriesIds)
                {
                    try
                    {
                        var episodes = await _client.GetSeriesEpisodesLiteAsync(memberId).ConfigureAwait(false);
                        if (PickMainEpisode(episodes) is { } main)
                            mappings.Add(SyntheticMovieMapping(main));
                    }
                    catch
                    {
                        // ponytail: a failed lite fetch just leaves that movie folder-less in
                        // the structure until the next rebuild; the per-series pass still works.
                    }
                }
                if (mappings.Count > 0)
                    moviePlaceholders[group.PrimarySeriesId] = mappings;
            }).ConfigureAwait(false);

        return groups
            .Select(group => moviePlaceholders.TryGetValue(group.PrimarySeriesId, out var placeholders)
                ? group.Data with { Mappings = placeholders }
                : group.Data)
            .ToArray();
    }

    /// <summary>
    /// Full per-series fetch: episodes + files for the whole merged group, projected and
    /// merged exactly like the eager path, plus this group's Theme.mp3 extra.
    /// </summary>
    private async Task<SeriesData?> BuildSeriesDataAsync(int primaryId)
    {
        try
        {
            if (!IsEligibleManagedFolder())
                return null;

            var folders = (await _client.GetManagedFoldersAsync().ConfigureAwait(false))
                .ToDictionary(folder => folder.ID, folder => folder);
            var memberIds = _groupMembers.TryGetValue(primaryId, out var members)
                ? members
                : new[] { primaryId };

            bool enforceTmdbNumbering = _options.TmdbEpNumbering || _options.MergeTmdbSeries;
            // ponytail: TMDB show-episode listings are cached per per-series fetch, not across
            // fetches; add a persistent cache when the daemon needs one.
            var tmdbShowEpisodeCache = new ConcurrentDictionary<int, Task<IReadOnlyList<TmdbEpisodeDto>>>();

            var raw = new List<RelayRawSeries>();
            foreach (var memberId in memberIds)
            {
                var series = await _client.GetSeriesAsync(memberId).ConfigureAwait(false);
                if (series is null)
                    continue;
                var episodes = await _client.GetSeriesEpisodesAsync(memberId).ConfigureAwait(false);
                raw.Add(await ExtractSeriesAsync(series, episodes, folders, tmdbShowEpisodeCache, enforceTmdbNumbering).ConfigureAwait(false));
            }
            if (raw.Count == 0)
                return null;

            var projected = raw.Select(RelayMappingProjector.Project).ToList();
            var groupingInputs = raw
                .Zip(projected, (r, data) => new RelaySeriesGroupingInput(data, r.AnidbAnimeId, r.AirDate, r.TmdbSeriesId))
                .ToArray();
            var merged = RelaySeriesGrouper.Merge(groupingInputs, _options.MergeTmdbSeries, enforceTmdbNumbering ? _options.ManualOverrideGroups : []);
            var result = merged.FirstOrDefault(data => data.SeriesId == primaryId) ?? merged.FirstOrDefault();
            if (result is null)
                return null;

            // AnimeThemes Theme.mp3 next to this group's own source files (probes each
            // mapping's source folder once). Mirrors the symlink work ShokoRelay's
            // AnimeThemesMp3Generator skips when Settings.Advanced.UseExternalVfs=true.
            var theme = FindThemeFile(result.Mappings);
            return theme is null ? result : result with { Extras = [theme] };
        }
        catch
        {
            // ponytail: a failed series returns null → the resolver falls back to the
            // structure node and refetches on the next access; never abort the mount.
            return null;
        }
    }

    /// <summary>Bare-DTO main episode: first unhidden Type==Episode, else first (unhidden) by number.</summary>
    private static ShokoEpisodeDto? PickMainEpisode(IReadOnlyList<ShokoEpisodeDto> episodes)
    {
        if (episodes.Count == 0)
            return null;
        var visible = episodes.Where(episode => !episode.IsHidden).ToList();
        var candidates = visible.Count > 0 ? visible : episodes;
        return candidates.FirstOrDefault(episode => episode.AniDB?.Type == EpisodeType.Episode)
            ?? candidates.OrderBy(EpisodeNumberOf).FirstOrDefault();
    }

    private static int EpisodeNumberOf(ShokoEpisodeDto episode) => episode.AniDB?.EpisodeNumber ?? episode.IndexNumber;

    /// <summary>
    /// Synthetic structure-only mapping for a movie series. FileId 0 marks it as a placeholder
    /// (skipped by path validation and snapshot priming); the non-blank SourcePath only exists
    /// so the resolver's HasSource yields the episode-id folder — any access to that path
    /// routes into the lazily materialized subtree, so this path is never actually served.
    /// </summary>
    private EpisodeData SyntheticMovieMapping(ShokoEpisodeDto episode) => new(
        0,
        episode.IDs.ID,
        episode.AniDB?.Type == EpisodeType.Special ? 0 : 1,
        EpisodeNumberOf(episode),
        null,
        null,
        1,
        false,
        true,
        episode.Name,
        _managedFolderRoot,
        0,
        "");

    /// <summary>
    /// Probes the source folders behind a series' resolved mappings for a Theme.mp3 file
    /// (first match wins) and returns it as a series-folder extra.
    /// </summary>
    private ExtraFile? FindThemeFile(IReadOnlyList<EpisodeData> mappings)
    {
        var probedFolders = new HashSet<string>(StringComparer.FromComparison(_pathComparison));
        foreach (var mapping in mappings)
        {
            if (mapping.FileId == 0 || string.IsNullOrEmpty(mapping.SourcePath))
                continue;
            string? sourceFolder = Path.GetDirectoryName(mapping.SourcePath);
            if (sourceFolder is null || !probedFolders.Add(sourceFolder))
                continue;

            var themePath = Path.Combine(sourceFolder, "Theme.mp3");
            if (!File.Exists(themePath))
                continue;

            long size;
            try { size = new FileInfo(themePath).Length; }
            catch { size = 0; }
            return new ExtraFile("Theme.mp3", themePath, size);
        }
        return null;
    }

    private async Task<RelayRawSeries> ExtractSeriesAsync(
        ShokoSeriesDto series,
        IReadOnlyList<ShokoEpisodeDto> episodes,
        IReadOnlyDictionary<int, ManagedFolderDto> folders,
        ConcurrentDictionary<int, Task<IReadOnlyList<TmdbEpisodeDto>>> tmdbShowEpisodeCache,
        bool enforceTmdbNumbering)
    {
        var tmdbShows = series.TMDB?.Shows ?? [];
        var preferredOrdering = tmdbShows.FirstOrDefault();
        string? rawPreferredOrderingId = preferredOrdering?.AlternateOrderingID;
        string? preferredOrderingId = rawPreferredOrderingId;
        if (preferredOrdering is { } tmdbShow
            && string.Equals(preferredOrderingId, tmdbShow.ID.ToString(), StringComparison.OrdinalIgnoreCase))
            preferredOrderingId = null;
        var tmdbMovies = series.TMDB?.Movies ?? [];
        string displayTitle = ResolveSeriesTitle(series);
        var videos = new Dictionary<int, RelayRawVideo>();
        var seriesEpisodes = new List<RelayRawEpisode>();
        foreach (var episode in episodes)
        {
            if (episode.IsHidden || episode.AniDB is not { } anidbEpisode)
                continue;

            var episodeVideos = new List<RelayRawVideo>();
            foreach (var file in episode.Files ?? [])
            {
                if (!videos.TryGetValue(file.ID, out var rawVideo))
                {
                    rawVideo = ExtractVideo(file, folders);
                    videos.Add(file.ID, rawVideo);
                }
                if (!rawVideo.IsExcluded)
                    episodeVideos.Add(rawVideo);
            }

            var episodeType = anidbEpisode.Type;
            var rawEpisode = new RelayRawEpisode(
                episode.IDs.ID,
                episodeType,
                anidbEpisode.EpisodeNumber,
                // Mirrors IShokoEpisode.SeasonNumber (the REST AnidbEpisode DTO carries no season).
                episodeType switch { EpisodeType.Episode => 1, EpisodeType.Special => 0, _ => (int?)null },
                false,
                ResolveEpisodeTitle(episode, series, displayTitle),
                episodeVideos
            );
            rawEpisode = rawEpisode with
            {
                TmdbEpisodes = await ExtractTmdbEpisodesAsync(episode, tmdbShowEpisodeCache, enforceTmdbNumbering && !string.IsNullOrWhiteSpace(rawPreferredOrderingId)),
            };
            seriesEpisodes.Add(rawEpisode);
        }

        return new RelayRawSeries(series.IDs.ID, series.AniDB?.Type ?? AnimeType.Unknown, displayTitle, seriesEpisodes)
        {
            UseTmdbNumbering = enforceTmdbNumbering,
            IsTmdbMovie = enforceTmdbNumbering ? tmdbMovies.Any() : series.AniDB?.Type == AnimeType.Movie,
            PreferredTmdbOrderingId = preferredOrderingId,
            RawPreferredTmdbOrderingId = rawPreferredOrderingId,
            AnidbAnimeId = series.AniDB?.ID ?? 0,
            AirDate = _options.MergeTmdbSeries ? MapAirDate(series.AniDB?.AirDate) : null,
            TmdbSeriesId = _options.MergeTmdbSeries ? tmdbShows.FirstOrDefault()?.ID : null,
        };
    }

    /// <summary>Converts the DTO date shape onto the Abstractions struct used by the RelayRaw* records.</summary>
    private static AbstractionsDate? MapAirDate(PartialDateOnly? wire) =>
        wire is { Year: > 0 } date ? new AbstractionsDate(date.Year, date.Month, date.Day) : null;

    private string ResolveSeriesTitle(ShokoSeriesDto series)
    {
        string raw = GetTitleByLanguage(series.Name, series.AniDB?.Titles, _options.SeriesTitleLanguage);
        return _options.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(raw)
            ? s_seriesPrefixRegex.Replace(raw, "$2 — $1")
            : raw;
    }

    private string ResolveEpisodeTitle(ShokoEpisodeDto episode, ShokoSeriesDto series, string displaySeriesTitle)
    {
        // Baseline title: the Episode DTO's Name (= server's override ?? preferred ?? default),
        // matching the plugin's IWithTitles.PreferredTitle baseline.
        string raw = GetTitleByLanguage(episode.Name ?? "", episode.AniDB?.Titles, _options.EpisodeTitleLanguage);
        string? tmdbTitle = episode.TMDB?.Episodes.FirstOrDefault()?.Title;

        if (episode.AniDB?.EpisodeNumber == 1 && s_ambiguousTitles.Contains(raw))
        {
            string title = displaySeriesTitle;
            if (title == raw)
                title = tmdbTitle ?? GetTitleByLanguage(series.Name, series.AniDB?.Titles, "en");
            if (title != raw && !title.Contains(raw, StringComparison.Ordinal))
            {
                string result = raw == "Complete Movie" ? s_movieDescriptorRegex.Replace(title, "").Trim() : title;
                return $"{result} — {raw}";
            }
            return title;
        }

        if (_options.TmdbEpGroupNames && (episode.TMDB?.Episodes.Count ?? 0) > 1 && !string.IsNullOrEmpty(tmdbTitle))
            return tmdbTitle;

        return !string.IsNullOrEmpty(tmdbTitle) && s_defaultTitleRegex.IsMatch(raw) && !s_defaultTitleRegex.IsMatch(tmdbTitle)
            ? tmdbTitle
            : raw;
    }

    private static string GetTitleByLanguage(string preferredTitle, IReadOnlyList<TitleDto>? titles, string languageSetting)
    {
        string preferred = preferredTitle ?? "";
        if (string.IsNullOrWhiteSpace(languageSetting))
            return preferred;

        foreach (var language in languageSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (language.Equals("SHOKO", StringComparison.OrdinalIgnoreCase))
                return preferred;

            var title = (titles ?? [])
                .Where(candidate => candidate.Type != TitleType.Short)
                .OrderBy(candidate => candidate.Type switch
                {
                    TitleType.Main => 1,
                    TitleType.Official => 2,
                    TitleType.Synonym => 3,
                    _ => 4,
                })
                .FirstOrDefault(candidate => candidate.Language.Equals(language, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(candidate.Name));
            if (title is not null)
                return title.Name;
        }

        return preferred;
    }

    /// <summary>
    /// A managed folder that resolved non-empty before must not flap to empty when Shoko
    /// transiently reports no files/series. Throwing keeps the last published snapshot
    /// alive (the resolver's refresh loop retains it and retries after its backoff) and
    /// the <see cref="DataSourceCache"/> keeps its previous entry, instead of publishing
    /// an empty snapshot that empties every directory in the mount.
    /// </summary>
    /// <param name="count">Series/groups resolved in this pass.</param>
    /// <param name="verified">True when the API independently confirmed the folder is
    /// genuinely empty (two consecutive empty file listings + a non-empty library).</param>
    /// <param name="detail">Diagnostic text included in the exception when transient.</param>
    private void ThrowIfTransientEmpty(int count, bool verified, string detail)
    {
        if (count > 0)
        {
            Volatile.Write(ref _lastSeriesCount, count);
            return;
        }

        if (verified)
        {
            Volatile.Write(ref _lastSeriesCount, 0);
            return;
        }

        int last = Volatile.Read(ref _lastSeriesCount);
        if (last > 0)
            throw new InvalidOperationException(
                $"Relay source for managed folder {_managedFolderId} transiently resolved 0 series (last known: {last}; {detail}); keeping the previous snapshot.");
    }

    private static (Dictionary<int, int[]> FileSeries, HashSet<int> SeedIds) BuildSeedIndex(IReadOnlyList<FileDto> files)
    {
        var fileSeries = new Dictionary<int, int[]>();
        var seeds = new HashSet<int>();
        foreach (var file in files)
        {
            var ids = (file.SeriesIDs ?? [])
                .Select(group => group.SeriesID.ID)
                .Where(id => id is > 0)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            if (ids.Length == 0)
                continue;
            fileSeries[file.ID] = ids;
            foreach (var id in ids)
                seeds.Add(id);
        }
        return (fileSeries, seeds);
    }

    private bool IsEligibleManagedFolder()
    {
        if (_options.ManagedFolderType.HasFlag(AbstractionsDropFolderType.Source) && !_options.ManagedFolderType.HasFlag(AbstractionsDropFolderType.Destination))
            return false;

        return !SplitLines(_options.ManagedFolderExclusions).Any(exclusion =>
            string.Equals(exclusion, _managedFolderId.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(exclusion, _options.ManagedFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsPathExcluded(string path)
    {
        var excluded = new HashSet<string>(SplitLines(_options.FolderExclusions), StringComparer.OrdinalIgnoreCase)
        {
            _options.RelayTvFolderName,
            _options.RelayMovieFolderName,
        };

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (excluded.Contains(segment))
                return true;
            if (_options.PlexLocalExtras && s_localExtraDirRegex.IsMatch(segment))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitLines(string? value) =>
        (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Same as <see cref="ShokoRestClient.GetTmdbShowEpisodesAsync"/>, but treats any failure
    /// (timeout, 5xx, network reset) as "no alternate-ordering data for this show" instead of
    /// throwing and aborting the whole snapshot build. The empty-list result is cached too, so
    /// a slow/failing show doesn't get re-attempted once per episode in its series.
    /// </summary>
    private async Task<IReadOnlyList<TmdbEpisodeDto>> SafeGetTmdbShowEpisodesAsync(
        ConcurrentDictionary<int, Task<IReadOnlyList<TmdbEpisodeDto>>> cache,
        int showId)
    {
        return await cache.GetOrAdd(showId, async id =>
        {
            try
            {
                return await _client.GetTmdbShowEpisodesAsync(id).ConfigureAwait(false);
            }
            catch
            {
                // ponytail: swallow + cache empty so one slow show doesn't kill a 15-min build.
                // Log a warning if/when a logger is plumbed in.
                return (IReadOnlyList<TmdbEpisodeDto>)Array.Empty<TmdbEpisodeDto>();
            }
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RelayRawTmdbEpisode>> ExtractTmdbEpisodesAsync(
        ShokoEpisodeDto episode,
        ConcurrentDictionary<int, Task<IReadOnlyList<TmdbEpisodeDto>>> tmdbShowEpisodeCache,
        bool loadAlternateOrderings)
    {
        if (episode.TMDB is not { } tmdbData || tmdbData.Episodes.Count == 0)
            return [];

        // Without a preferred alternate ordering the series-episode endpoint already carries
        // the (default) coordinates; no per-ordering data is needed by the projector.
        if (!loadAlternateOrderings)
            return tmdbData.Episodes.Select(ToRelayTmdbEpisode).ToArray();

        // With a preferred alternate ordering, rebuild from the TMDB show's episode list
        // (forced to the default ordering) so each entry keeps its base coordinates plus the
        // full ordering list — the same shape the in-process ITmdbEpisode.AllOrderings exposes.
        int? tmdbShowId = null;
        foreach (var id in episode.IDs.TMDB.Show)
        {
            if (id > 0)
            {
                tmdbShowId = id;
                break;
            }
        }
        int? firstShowId = tmdbData.Episodes.FirstOrDefault()?.ShowID;
        tmdbShowId ??= firstShowId is { } sid && sid > 0 ? sid : null;
        var showEpisodes = tmdbShowId is { } showId
            ? await SafeGetTmdbShowEpisodesAsync(tmdbShowEpisodeCache, showId).ConfigureAwait(false)
            : [];
        if (showEpisodes.Count == 0)
            return tmdbData.Episodes.Select(ToRelayTmdbEpisode).ToArray();

        var byId = showEpisodes.ToDictionary(entry => entry.ID);
        var ids = (episode.IDs.TMDB.Episode.Count > 0 ? episode.IDs.TMDB.Episode : tmdbData.Episodes.Select(entry => entry.ID))
            .Distinct()
            .ToList();
        return ids
            .Where(byId.ContainsKey)
            .Select(entry => byId[entry])
            .Select(ToRelayTmdbEpisode)
            .ToArray();
    }

    private static RelayRawTmdbEpisode ToRelayTmdbEpisode(TmdbEpisodeDto tmdb) => new(
        tmdb.SeasonNumber,
        tmdb.EpisodeNumber,
        tmdb.AlternateOrderingID,
        tmdb.Ordering is { Count: > 0 } orderings
            ? orderings.Select(ordering => new RelayRawTmdbOrdering(ordering.OrderingID, ordering.SeasonNumber, ordering.EpisodeNumber)).ToArray()
            : Array.Empty<RelayRawTmdbOrdering>(),
        tmdb.Title
    );

    private RelayRawVideo ExtractVideo(FileDto file, IReadOnlyDictionary<int, ManagedFolderDto> folders)
    {
        var locations = file.Locations;
        var selected = SelectSource(locations, file.Size, folders, out bool excluded);
        var source = selected?.ManagedFolderId == _managedFolderId ? selected : null;
        var sortPath = selected?.RelativePath ?? locations
            .Select(location => NormalizeForSort(location.RelativePath))
            .FirstOrDefault();
        var crossReferences = (file.SeriesIDs ?? [])
            .SelectMany(group => group.EpisodeIDs, (group, episode) => new RelayRawCrossReference(episode.ID, group.SeriesID.ID))
            .ToArray();

        return new RelayRawVideo(file.ID, file.IsVariation, sortPath, source?.AbsolutePath, selected?.Size ?? 0, selected?.Extension ?? "", crossReferences)
        {
            IsExcluded = excluded,
        };
    }

    private SelectedSource? SelectSource(IEnumerable<FileLocationDto> locations, long fileSize, IReadOnlyDictionary<int, ManagedFolderDto> folders, out bool excluded)
    {
        excluded = false;
        foreach (var location in locations)
        {
            if (!IsFolderEligible(location, folders))
                continue;
            if (!TryResolveSource(location.RelativePath, out var resolved))
                continue;
            if (IsPathExcluded(resolved.RelativePath))
            {
                excluded = true;
                return null;
            }

            return new SelectedSource(
                resolved.AbsolutePath,
                resolved.RelativePath,
                location.ManagedFolderID,
                fileSize,
                resolved.Extension
            );
        }

        return null;
    }

    private bool IsFolderEligible(FileLocationDto location, IReadOnlyDictionary<int, ManagedFolderDto> folders)
    {
        if (!folders.TryGetValue(location.ManagedFolderID, out var folder))
            return false;

        if (folder.DropFolderType.HasFlag(DropFolderType.Source) && !folder.DropFolderType.HasFlag(DropFolderType.Destination))
            return false;

        foreach (var exclusion in SplitLines(_options.ManagedFolderExclusions))
        {
            if (string.Equals(exclusion, folder.ID.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(exclusion, folder.Name, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private bool TryResolveSource(string relativePath, out ResolvedSource source)
    {
        source = default!;
        string relative = NormalizeSeparators(relativePath ?? "");
        bool hasSingleLeadingSeparator = relative.Length > 0
            && relative[0] == Path.DirectorySeparatorChar
            && (relative.Length == 1 || relative[1] != Path.DirectorySeparatorChar);
        if (relative.Length == 0
            || (Path.IsPathRooted(relative) && !hasSingleLeadingSeparator))
            return false;

        relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative) || IsDriveRooted(relative))
            return false;

        try
        {
            string candidate = Path.GetFullPath(Path.Combine(_managedFolderRoot, relative));
            if (!IsContained(candidate))
                return false;

            source = new ResolvedSource(candidate, relative, Path.GetExtension(candidate));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
        }

        return false;
    }

    private bool IsContained(string candidate)
    {
        string relative = Path.GetRelativePath(_managedFolderRoot, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", _pathComparison)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, _pathComparison)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, _pathComparison);
    }

    private static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
        && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);

    private static string NormalizeSeparators(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string NormalizeForSort(string? path) => NormalizeSeparators(path ?? "");

    private sealed record ResolvedSource(string AbsolutePath, string RelativePath, string Extension);

    private sealed record SelectedSource(string AbsolutePath, string RelativePath, int ManagedFolderId, long Size, string Extension);

    private sealed record SeriesCacheEntry(SeriesData Data, DateTime CachedAt);
}