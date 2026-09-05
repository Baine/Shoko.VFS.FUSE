// REST-based data source for the Relay path resolver.
//
// Replicates the aggregation chain of the in-process RelayShokoPathDataSource
// (Shoko.VFS.FUSE/Resolvers/Relay/RelayShokoPathDataSource.cs) over the Shoko
// Server REST API instead of IMetadataService:
//
//   1. GET /api/v3/ManagedFolder/{id}/File?include=XRefs  -> series seed ids
//   2. GET /api/v3/Series?includeDataFrom=AniDB,TMDB      -> grouping metadata for all series
//   3. RelaySeriesGrouper.GetGroupClosure                 -> series closure around the seeds
//   4. GET /api/v3/Series/{id}/Episode (AniDB+TMDB+Files+XRefs) -> RelayRaw* extraction
//   5. RelayMappingProjector.Project + RelaySeriesGrouper.Merge -> SeriesData
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

public sealed class ShokoRelayDataSource : IShokoPathDataSource, IDisposable
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
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (cacheTtl > TimeSpan.Zero)
            _cache = new DataSourceCache(GetAllSeriesCoreAsync, cacheTtl, maxDegree, snapshotStore, snapshotKey);
    }

    /// <summary>Synchronous entry point required by <see cref="IShokoPathDataSource"/>.</summary>
    public IReadOnlyList<SeriesData> GetAllSeries()
        => GetAllSeriesAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<SeriesData>> GetAllSeriesAsync()
        => _cache is not null
            ? _cache.GetAsync()
            : GetAllSeriesCoreAsync();

    /// <summary>Forces the aggregation cache to rebuild on the next request.</summary>
    public void Invalidate() => _cache?.Invalidate();

    /// <summary>Pins the current cache so the FUSE mount keeps serving the last known snapshot.</summary>
    public void Freeze() => _cache?.Freeze();

    /// <summary>Releases the freeze; the next request rebuilds from the server.</summary>
    public void Unfreeze() => _cache?.Unfreeze();

    /// <summary>Whether the cache is currently frozen (serving stale data).</summary>
    public bool IsFrozen => _cache?.IsFrozen ?? false;

    /// <summary>Loads the persisted snapshot from the configured <see cref="FileSnapshotStore"/>.</summary>
    public bool TryLoadFromStore() => _cache?.TryLoadFromStore() ?? false;

    /// <summary>Persists the current snapshot via the configured <see cref="FileSnapshotStore"/>.</summary>
    public void SaveToStore() => _cache?.SaveToStore();

    /// <summary>True when a snapshot store is configured and the last shutdown was clean.</summary>
    public bool WasLastShutdownClean() => _cache?.WasLastShutdownClean() ?? false;

    /// <summary>Discards any persisted snapshot + clean-shutdown marker.</summary>
    public void InvalidateStored() => _cache?.InvalidateStored();

    public void Dispose() { }

    private async Task<IReadOnlyList<SeriesData>> GetAllSeriesCoreAsync(CancellationToken ct = default)
    {
        if (!IsEligibleManagedFolder())
            return [];

        // Folder metadata for per-location eligibility checks.
        var folders = (await _client.GetManagedFoldersAsync().ConfigureAwait(false))
            .ToDictionary(folder => folder.ID, folder => folder);

        // Seed: series that have at least one file located in this managed folder.
        var folderFiles = await _client.GetManagedFolderFilesAsync(_managedFolderId).ConfigureAwait(false);
        var localSeedIds = folderFiles
            .SelectMany(file => file.SeriesIDs ?? [])
            .Select(group => group.SeriesID.ID)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .ToHashSet();
        if (localSeedIds.Count == 0)
            return [];

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

        // ponytail: TMDB show-episode listings are cached per GetAllSeriesAsync call, not across
        // calls; add a persistent cache when the daemon needs one.
        var tmdbShowEpisodeCache = new ConcurrentDictionary<int, Task<IReadOnlyList<TmdbEpisodeDto>>>();

        // Per-series episode fetches dominate the build on large libraries (e.g. 1.4k series × ~200ms
        // serially = 5+ minutes). Bounded concurrency cuts this by ~MaxDegreeOfParallelism.
        var targetSeries = allSeries.Where(series => groupClosure.Contains(series.IDs.ID)).ToArray();
        var rawSeries = new RelayRawSeries?[targetSeries.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targetSeries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _cache.MaxDegree, CancellationToken = ct },
            async (i, token) =>
            {
                var series = targetSeries[i];
                try
                {
                    var episodes = await _client.GetSeriesEpisodesAsync(series.IDs.ID).ConfigureAwait(false);
                    rawSeries[i] = await ExtractSeriesAsync(series, episodes, folders, tmdbShowEpisodeCache, enforceTmdbNumbering).ConfigureAwait(false);
                }
                catch
                {
                    // ponytail: skip one bad series rather than aborting the whole snapshot.
                    // The slot stays null and is filtered out below.
                }
            }).ConfigureAwait(false);

        // Drop any null slots from per-series failures; the rest still produce a valid snapshot.
        var raw = rawSeries.Where(r => r is not null).Select(r => r!).ToList();
        var projected = raw.Select(RelayMappingProjector.Project).ToList();

        // Discover AnimeThemes Theme.mp3 (and similar non-Shoko assets) by walking the
        // same folder file list and probing each source series folder once. Mirrors the
        // symlink work ShokoRelay's AnimeThemesMp3Generator skips when
        // Settings.Advanced.UseExternalVfs=true — the daemon exposes the file directly.
        var extrasBySeries = DiscoverThemeExtras(folderFiles);

        var groupingInputs = raw
            .Zip(projected, (r, data) => new RelaySeriesGroupingInput(data, r.AnidbAnimeId, r.AirDate, r.TmdbSeriesId))
            .ToArray();
        var merged = RelaySeriesGrouper.Merge(groupingInputs, _options.MergeTmdbSeries, manualOverrides);
        if (extrasBySeries.Count == 0)
            return merged;
        return merged
            .Select(s => extrasBySeries.TryGetValue(s.SeriesId, out var extra)
                ? s with { Extras = new[] { extra } }
                : s)
            .ToList();
    }

    /// <summary>
    /// Probes every distinct source-series folder in the managed folder for a Theme.mp3
    /// file and produces a seriesId → ExtraFile map (first match wins per series). Used by
    /// <see cref="GetAllSeriesCoreAsync"/> to surface the theme in the VFS mount.
    /// </summary>
    private Dictionary<int, ExtraFile> DiscoverThemeExtras(IReadOnlyList<FileDto> folderFiles)
    {
        var result = new Dictionary<int, ExtraFile>();
        if (folderFiles.Count == 0)
            return result;

        // Cache probed absolute folders so we stat each one at most once per build.
        var probedFolders = new HashSet<string>(StringComparer.FromComparison(_pathComparison));
        foreach (var file in folderFiles)
        {
            if (file.Locations is null) continue;
            foreach (var loc in file.Locations)
            {
                if (loc.ManagedFolderID != _managedFolderId) continue;
                if (string.IsNullOrWhiteSpace(loc.RelativePath)) continue;

                var sourceFolder = ResolveSourceFolder(loc.RelativePath);
                if (sourceFolder is null || !probedFolders.Add(sourceFolder)) continue;

                var themePath = Path.Combine(sourceFolder, "Theme.mp3");
                if (!File.Exists(themePath)) continue;

                long size;
                try { size = new FileInfo(themePath).Length; }
                catch { size = 0; }

                foreach (var sr in file.SeriesIDs ?? [])
                {
                    int? sidNullable = sr.SeriesID.ID;
                    if (sidNullable is not int sid || sid <= 0) continue;
                    result.TryAdd(sid, new ExtraFile("Theme.mp3", themePath, size));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves the absolute source series folder that contains a file at the given
    /// managed-folder-relative path. Returns null on any validation failure (empty,
    /// rooted outside the managed folder, etc.).
    /// </summary>
    private string? ResolveSourceFolder(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        string relative = NormalizeSeparators(relativePath);
        bool hasSingleLeadingSeparator = relative.Length > 0
            && relative[0] == Path.DirectorySeparatorChar
            && (relative.Length == 1 || relative[1] != Path.DirectorySeparatorChar);
        if (relative.Length == 0
            || (Path.IsPathRooted(relative) && !hasSingleLeadingSeparator))
            return null;

        relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0) return null;

        string parent = Path.GetDirectoryName(relative);
        if (string.IsNullOrEmpty(parent)) return null;

        try
        {
            string candidate = Path.GetFullPath(Path.Combine(_managedFolderRoot, parent));
            if (!IsContained(candidate)) return null;
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
}