using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

public sealed record RelayPathDataSourceOptions(int ManagedFolderId, string ManagedFolderRoot)
{
    public string ManagedFolderName { get; init; } = "";

    /// <summary>
    /// Server→host path prefix mapping (Host daemon only): the REST managed-folder listing
    /// carries SERVER-side roots while only the mounted root is translated at startup, so the
    /// cross-folder fallback re-maps foreign roots with this pair — the exact
    /// <c>ServerPathRoot → ManagedFolderPathRoot</c> prefix swap of <c>DaemonOrchestrator.MapPath</c>.
    /// Empty (the in-process plugin default) means identity.
    /// </summary>
    public string ServerPathRoot { get; init; } = "";
    public string ManagedFolderPathRoot { get; init; } = "";
    public DropFolderType ManagedFolderType { get; init; } = DropFolderType.Excluded;
    public bool TmdbEpNumbering { get; init; } = true;
    public bool MergeTmdbSeries { get; init; }
    public bool PlexLocalExtras { get; init; } = true;
    public string FolderExclusions { get; init; } = "";
    public string ManagedFolderExclusions { get; init; } = "";
    public string RelayTvFolderName { get; init; } = "!ShokoRelayVFS";
    public string RelayMovieFolderName { get; init; } = "!ShokoRelayMovieVFS";
    public string SeriesTitleLanguage { get; init; } = "SHOKO";
    public string EpisodeTitleLanguage { get; init; } = "SHOKO";
    public bool MoveCommonSeriesTitlePrefixes { get; init; } = true;
    public bool TmdbEpGroupNames { get; init; } = true;
    public IReadOnlyList<IReadOnlyList<int>> ManualOverrideGroups { get; init; } = [];
    public TimeSpan SeriesCacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ordered subtitle language token replacements applied to sidecar suffixes during asset
    /// discovery — mirrors upstream <c>Settings.Advanced.SubtitleLanguageMappings</c>
    /// (default empty, which takes upstream's fast path).
    /// </summary>
    public IReadOnlyList<SubtitleLanguageMapping> SubtitleLanguageMappings { get; init; } = [];

    /// <summary>
    /// Optional absolute path to ShokoRelay's <c>anidb_animethemes_xrefs.csv</c>. When set and
    /// present, AnimeThemes <c>Shorts/*.webm</c> are exposed per AniDB id (upstream
    /// VfsBuilder.cs:704-733). Empty disables Shorts exposure.
    /// </summary>
    public string AnimeThemesXrefCsvPath { get; init; } = "";

    /// <summary>
    /// Host-visible path to Shoko's <c>configuration</c> directory root. Used by the Host
    /// daemon only, to auto-discover <c>&lt;ShokoConfigDir&gt;/&lt;ShokoRelayPluginId&gt;/
    /// anidb_animethemes_xrefs.csv</c> (via <c>GET /api/v3/Plugin</c>) when
    /// <see cref="AnimeThemesXrefCsvPath"/> is not configured. Ignored by the in-process
    /// plugin, which resolves the CSV from <c>IApplicationPaths</c> directly.
    /// </summary>
    public string ShokoConfigDir { get; init; } = "";

    public DropFolderType ManagedFolderDropFolderType
    {
        get => ManagedFolderType;
        init => ManagedFolderType = value;
    }
}

/// <summary>
/// One ordered subtitle-language token replacement for sidecar renaming — the
/// equivalent of one entry in upstream's <c>Advanced.SubtitleLanguageMappings</c>
/// <c>OrderedDictionary</c> (e.g. token <c>en</c> → replacement <c>English</c>).
/// </summary>
/// <param name="Token">Token matched case-insensitively against dot-separated suffix parts.</param>
/// <param name="Replacement">Replacement text; ignored when blank, dotted, or unsafe as a filename.</param>
public sealed record SubtitleLanguageMapping(string Token, string? Replacement);

public sealed class RelayShokoPathDataSource : IShokoPathDataSource, ILazyShokoPathDataSource
{
    private static readonly Regex s_seriesPrefixRegex = new(@"^(Gekijou ?(?:ban(?: 3D)?|Tanpen|Remix Ban|Henshuuban|Soushuuhen)|Eiga|OVA) (.*$)", RegexOptions.Compiled);
    private static readonly Regex s_defaultTitleRegex = new(@"^(Episode|Volume|Special|Short|(Short )?Movie) [S0]?[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex s_localExtraDirRegex = new(@"^(Behind The Scenes|Deleted Scenes|Featurettes|Interviews|Scenes|Shorts|Trailers|Other)(\s+[sS](\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlySet<string> s_ambiguousTitles = new HashSet<string>(
        ["Complete Movie", "Music Video", "OAD", "OVA", "Short Movie", "Special", "TV Special", "Web"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex s_movieDescriptorRegex = new(@"(?i)(:? The)?( Movie| Motion Picture)", RegexOptions.Compiled);

    private static readonly string[] s_localExtraDirectories =
        ["Behind The Scenes", "Deleted Scenes", "Featurettes", "Interviews", "Scenes", "Shorts", "Trailers", "Other"];

    private readonly IMetadataService _metadataService;
    private readonly IVideoService? _videoService;
    private readonly RelayPathDataSourceOptions _options;
    private readonly int _managedFolderId;
    private readonly string _managedFolderRoot;
    private readonly StringComparison _pathComparison;
    private readonly object _structureGate = new();
    private readonly ConcurrentDictionary<int, SeriesCacheEntry> _seriesCache = new();
    private int _lastSeriesCount;
    private volatile IReadOnlyDictionary<int, int> _primaryBySeriesId = new Dictionary<int, int>();
    private volatile IReadOnlyDictionary<int, IReadOnlyList<int>> _membersByPrimary = new Dictionary<int, IReadOnlyList<int>>();

    public RelayShokoPathDataSource(IMetadataService metadataService, RelayPathDataSourceOptions options, IVideoService? videoService = null)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
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
        _videoService = videoService;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>
    /// Maps any series in a consolidation group to the group's primary series id
    /// (the id the resolver routes subtrees by). Unknown ids map to themselves.
    /// </summary>
    public int MapToPrimarySeriesId(int seriesId) =>
        _primaryBySeriesId.GetValueOrDefault(seriesId, seriesId);

    public IReadOnlyList<SeriesData> GetAllSeries()
    {
        if (!IsEligibleManagedFolder())
            return [];

        var allSeries = LoadAllSeries();
        var (seedIds, verifiedEmpty) = CollectSeedSeriesIds(allSeries);
        if (seedIds.Count == 0)
        {
            ThrowIfTransientEmpty(0, verifiedEmpty, "seed enumeration (file listing and series walk) returned no series");
            return [];
        }

        var closedSeries = FilterClosedSeries(allSeries, seedIds);
        var rawSeries = closedSeries
            .Select(ExtractSeries)
            .ToList();
        ThrowIfTransientEmpty(rawSeries.Count, verified: false, "grouping produced no series despite non-empty seeds");
        var groups = BuildGroups(rawSeries);
        PublishGroupMaps(groups);
        var anidbBySeriesId = rawSeries.ToDictionary(raw => raw.SeriesId, raw => raw.AnidbAnimeId);

        // Discover local assets (series metadata/artwork/Theme.mp3, episode sidecars,
        // attachments, Plex local extras, AnimeThemes Shorts) in the groups' source folders —
        // the discovery mirror of upstream's VfsAssetLinker. Replaces the older Theme.mp3-only
        // probe and keeps the Host REST path and this in-process path consistent.
        return groups
            .Select(group => RelayLocalAssetLinker.Discover(
                group.Data,
                _options,
                _managedFolderRoot,
                group.SeriesIds.Select(id => anidbBySeriesId.GetValueOrDefault(id)).Where(id => id > 0).ToArray(),
                _pathComparison,
                IsShokoManagedFile,
                MapToPrimarySeriesId))
            .ToList();
    }

    /// <summary>
    /// A managed folder that resolved non-empty before must not flap to empty when
    /// Shoko transiently reports no files/series (DB hiccup, import in flight, video
    /// entity not yet loadable). Throwing keeps the last published snapshot alive —
    /// the resolver's refresh loop retains the previous snapshot on exceptions and
    /// retries after its backoff — instead of publishing an empty snapshot that
    /// empties every directory in the mount.
    /// </summary>
    /// <param name="count">Series/groups resolved in this pass.</param>
    /// <param name="verified">True when the API independently confirmed the folder is
    /// genuinely empty (file listing and series walk agree, library non-empty).</param>
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

    /// <summary>
    /// Structure-only pass: series/movie directory nodes with empty TV mappings, so the
    /// resolver can publish the mount layout without materializing any TV series'
    /// episodes/videos/files. Movie-closure groups carry every sourced main mapping
    /// (one folder per mapping in the relay tree, so the episode-ID folders must all
    /// appear for routing and root listing parity).
    /// </summary>
    public IReadOnlyList<SeriesData> GetSeriesStructure()
    {
        lock (_structureGate)
        {
            _seriesCache.Clear();
            if (!IsEligibleManagedFolder())
            {
                PublishGroupMaps([]);
                return [];
            }

            var allSeries = LoadAllSeries();
            var (seedIds, verifiedEmpty) = CollectSeedSeriesIds(allSeries);
            if (seedIds.Count == 0)
            {
                ThrowIfTransientEmpty(0, verifiedEmpty, "seed enumeration (file listing and series walk) returned no series");
                PublishGroupMaps([]);
                return [];
            }

            var closedSeries = FilterClosedSeries(allSeries, seedIds);
            var byId = closedSeries.ToDictionary(series => series.ID);
            var shellInputs = closedSeries
                .Select(series => BuildGroupingInput(ExtractSeriesShell(series)))
                .ToList();
            var groups = BuildGroupsFromInputs(shellInputs);
            ThrowIfTransientEmpty(groups.Count, verified: false, "grouping produced no groups despite non-empty seeds");
            PublishGroupMaps(groups);

            var result = new List<SeriesData>(groups.Count);
            foreach (var group in groups)
            {
                var data = group.Data;
                if (data.IsMovie)
                    data = data with { Mappings = MovieStructureMappings(group.SeriesIds, byId) };
                result.Add(data);
            }

            return result;
        }
    }

    public SeriesData? GetSeriesData(int seriesId)
    {
        if (_seriesCache.TryGetValue(seriesId, out var cached) && IsFresh(cached))
            return cached.Data;

        var data = LoadSeriesFromShoko(seriesId);
        _seriesCache[seriesId] = new SeriesCacheEntry(data, DateTimeOffset.UtcNow);
        return data;
    }

    public void Invalidate(int? seriesId)
    {
        if (seriesId is not int id)
        {
            _seriesCache.Clear();
            return;
        }

        _seriesCache.TryRemove(id, out _);
        if (_primaryBySeriesId.TryGetValue(id, out var primary))
            _seriesCache.TryRemove(primary, out _);
    }

    private SeriesData? LoadSeriesFromShoko(int seriesId)
    {
        var memberIds = _membersByPrimary.TryGetValue(seriesId, out var members) ? members : new[] { seriesId };
        var loaded = new List<IShokoSeries>(memberIds.Count);
        foreach (var id in memberIds)
        {
            if (_metadataService.GetShokoSeriesByID(id) is { } series)
                loaded.Add(series);
        }

        if (loaded.Count == 0)
            return null;

        var data = loaded.Count == 1
            ? RelayMappingProjector.Project(ExtractSeries(loaded[0]))
            : BuildGroups(loaded.Select(ExtractSeries).ToList()).First().Data;

        return RelayLocalAssetLinker.Discover(
            data,
            _options,
            _managedFolderRoot,
            loaded.Select(series => series.AnidbAnimeID).Where(id => id > 0).ToArray(),
            _pathComparison,
            IsShokoManagedFile,
            MapToPrimarySeriesId);
    }

    private IReadOnlyList<EpisodeData> MovieStructureMappings(IReadOnlyList<int> memberIds, Dictionary<int, IShokoSeries> byId)
    {
        var raws = memberIds
            .Where(id => byId.ContainsKey(id))
            .Select(id => ExtractSeries(byId[id]))
            .ToList();
        var mappings = (raws.Count == 1
                ? [RelayMappingProjector.Project(raws[0])]
                : BuildGroups(raws).Select(group => group.Data))
            .SelectMany(series => series.Mappings ?? []);
        var main = mappings
            .Where(mapping => mapping.IsMain && !string.IsNullOrWhiteSpace(mapping.SourcePath))
            .ToArray();
        return main;
    }

    private bool IsFresh(SeriesCacheEntry entry) =>
        _options.SeriesCacheTtl > TimeSpan.Zero
        && DateTimeOffset.UtcNow - entry.CompletedAt < _options.SeriesCacheTtl;

    private List<IShokoSeries> LoadAllSeries() =>
        (_metadataService.GetAllShokoSeries() ?? Array.Empty<IShokoSeries>()).ToList();

    /// <summary>
    /// Series with at least one indexed file in the managed folder. Prefers the cheap
    /// per-folder file listing (one query plus per-video cross-references) over walking
    /// every series' episodes/videos/files; falls back to the full scan when no
    /// IVideoService was supplied — and also when the file listing comes back empty, so
    /// an empty result is verified against the independent series-walk path before it
    /// is trusted (Shoko can transiently report no files during an import or DB hiccup).
    /// Verified-empty requires the walk to agree while the series library is non-empty.
    /// </summary>
    private (HashSet<int> Ids, bool VerifiedEmpty) CollectSeedSeriesIds(IReadOnlyList<IShokoSeries> allSeries)
    {
        if (_videoService is { } videoService)
        {
            var folder = (videoService.GetAllManagedFolders() ?? Array.Empty<IManagedFolder>())
                .FirstOrDefault(candidate => candidate.ID == _managedFolderId);
            if (folder is not null)
            {
                var ids = new HashSet<int>();
                var seenVideos = new HashSet<int>();
                foreach (var file in videoService.GetVideoFilesInManagedFolder(folder) ?? Array.Empty<IVideoFile>())
                {
                    if (file.ManagedFolderID != _managedFolderId)
                        continue;

                    IVideo? video;
                    try
                    {
                        video = file.Video;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
                    {
                        continue;
                    }

                    if (video is null || !seenVideos.Add(video.ID))
                        continue;

                    foreach (var series in video.Series ?? Array.Empty<IShokoSeries>())
                        ids.Add(series.ID);
                }

                if (ids.Count > 0)
                    return (ids, false);

                var walkIds = CollectSeedIdsBySeriesWalk(allSeries);
                return (walkIds, walkIds.Count == 0 && allSeries.Count > 0);
            }
        }

        return (CollectSeedIdsBySeriesWalk(allSeries), false);
    }

    private HashSet<int> CollectSeedIdsBySeriesWalk(IReadOnlyList<IShokoSeries> allSeries) =>
        allSeries
            .Where(series => (series.Episodes ?? Array.Empty<IShokoEpisode>())
                .Any(episode => (episode.Videos ?? Array.Empty<IVideo>())
                    .Any(video => (video.Files ?? Array.Empty<IVideoFile>())
                        .Any(file => file.ManagedFolderID == _managedFolderId))))
            .Select(series => series.ID)
            .ToHashSet();

    private List<IShokoSeries> FilterClosedSeries(IReadOnlyList<IShokoSeries> allSeries, HashSet<int> localSeedIds)
    {
        bool enforceTmdbNumbering = _options.TmdbEpNumbering || _options.MergeTmdbSeries;
        var manualOverrides = enforceTmdbNumbering ? _options.ManualOverrideGroups : [];
        var groupingMetadata = allSeries
            .Select(series => new RelaySeriesGroupingMetadata(
                series.ID,
                series.AnidbAnimeID,
                _options.MergeTmdbSeries ? series.AirDate : null,
                _options.MergeTmdbSeries ? (series.TmdbShows ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbShow>()).FirstOrDefault()?.ID : null
            ))
            .ToArray();
        var groupClosure = RelaySeriesGrouper.GetGroupClosure(
            groupingMetadata,
            localSeedIds,
            _options.MergeTmdbSeries,
            manualOverrides
        );
        return allSeries
            .Where(series => groupClosure.Contains(series.ID))
            .ToList();
    }

    private IReadOnlyList<RelaySeriesGroup> BuildGroups(IReadOnlyList<RelayRawSeries> rawSeries) =>
        BuildGroupsFromInputs(BuildGroupingInputs(rawSeries));

    private IReadOnlyList<RelaySeriesGroup> BuildGroupsFromInputs(IReadOnlyList<RelaySeriesGroupingInput> groupingInputs) =>
        RelaySeriesGrouper.Group(groupingInputs, _options.MergeTmdbSeries, EffectiveManualOverrides());

    private static IReadOnlyList<RelaySeriesGroupingInput> BuildGroupingInputs(IReadOnlyList<RelayRawSeries> rawSeries) =>
        rawSeries
            .Select(raw => BuildGroupingInput(raw))
            .ToList();

    private static RelaySeriesGroupingInput BuildGroupingInput(RelayRawSeries raw) =>
        new(RelayMappingProjector.Project(raw), raw.AnidbAnimeId, raw.AirDate, raw.TmdbSeriesId);

    private void PublishGroupMaps(IReadOnlyList<RelaySeriesGroup> groups)
    {
        var primaryBySeriesId = new Dictionary<int, int>();
        var membersByPrimary = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var group in groups)
        {
            membersByPrimary[group.PrimarySeriesId] = group.SeriesIds;
            foreach (var id in group.SeriesIds)
                primaryBySeriesId[id] = group.PrimarySeriesId;
        }

        _membersByPrimary = membersByPrimary;
        _primaryBySeriesId = primaryBySeriesId;
    }

    private IReadOnlyList<IReadOnlyList<int>> EffectiveManualOverrides() =>
        _options.TmdbEpNumbering || _options.MergeTmdbSeries ? _options.ManualOverrideGroups : [];

    private sealed record SeriesCacheEntry(SeriesData? Data, DateTimeOffset CompletedAt);

    /// <summary>
    /// Upstream's local-extra "managed by Shoko" gate (VfsAssetLinker.cs:303,321): a physical
    /// file with an episode cross-reference is never exposed as a Plex local extra. Without an
    /// IVideoService (unit tests) nothing is considered managed, matching the REST datasource's
    /// conservative fallback.
    /// </summary>
    private bool IsShokoManagedFile(string absolutePath)
    {
        if (_videoService is not { } videoService)
            return false;
        try
        {
            return videoService.GetVideoFileByAbsolutePath(absolutePath)?.Video
                ?.CrossReferences?.Any(reference => reference.ShokoEpisode != null) == true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private RelayRawSeries ExtractSeries(IShokoSeries series)
    {
        bool enforceTmdbNumbering = _options.TmdbEpNumbering || _options.MergeTmdbSeries;
        var preferredOrdering = (series.TmdbShows ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbShow>()).FirstOrDefault()?.PreferredOrdering;
        string? rawPreferredOrderingId = preferredOrdering?.OrderingID;
        string displayTitle = ResolveSeriesTitle(series);
        var videos = new Dictionary<int, RelayRawVideo>();
        var episodes = new List<RelayRawEpisode>();
        foreach (var episode in series.Episodes ?? Array.Empty<IShokoEpisode>())
        {
            if (episode.IsHidden)
                continue;

            var episodeVideos = new List<RelayRawVideo>();
            foreach (var video in episode.Videos ?? Array.Empty<IVideo>())
            {
                if (!videos.TryGetValue(video.ID, out var rawVideo))
                {
                    // Main/extra is decided by the episode first processed for this video id
                    // (the series-level cache shares one extraction across xrefs, mirroring
                    // the projector's single primary-driven mapping per video).
                    rawVideo = ExtractVideo(video, episode.Type == EpisodeType.Episode);
                    videos.Add(video.ID, rawVideo);
                }
                // Upstream keeps location-excluded videos in the mapping inputs — they count
                // toward the part/dup split (MapHelper) — and hides them only at link time
                // (VfsShared.ResolveSourcePath null, VfsBuilder.cs:560-579). Their SourcePath
                // stays empty here, so the resolver publishes no entry for them.
                episodeVideos.Add(rawVideo);
            }

            episodes.Add(
                new RelayRawEpisode(
                    episode.ID,
                    episode.Type,
                    episode.EpisodeNumber,
                    episode.SeasonNumber,
                    false,
                    ResolveEpisodeTitle(episode, series, displayTitle),
                    episodeVideos
                )
            );
            episodes[^1] = episodes[^1] with
            {
                TmdbEpisodes = ExtractTmdbEpisodes(episode, enforceTmdbNumbering && !string.IsNullOrWhiteSpace(rawPreferredOrderingId))
            };
        }

        return BuildSeriesModel(series, displayTitle, episodes);
    }

    /// <summary>
    /// Series-level fields only (no episode/video/file traversal) for the structure pass.
    /// </summary>
    private RelayRawSeries ExtractSeriesShell(IShokoSeries series) =>
        BuildSeriesModel(series, ResolveSeriesTitle(series), []);

    private RelayRawSeries BuildSeriesModel(IShokoSeries series, string displayTitle, List<RelayRawEpisode> episodes)
    {
        bool enforceTmdbNumbering = _options.TmdbEpNumbering || _options.MergeTmdbSeries;
        var tmdbShows = series.TmdbShows ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbShow>();
        var preferredOrdering = tmdbShows.FirstOrDefault()?.PreferredOrdering;
        string? rawPreferredOrderingId = preferredOrdering?.OrderingID;
        string? preferredOrderingId = rawPreferredOrderingId;
        if (tmdbShows.FirstOrDefault() is { } tmdbShow
            && string.Equals(preferredOrderingId, tmdbShow.ID.ToString(), StringComparison.OrdinalIgnoreCase))
            preferredOrderingId = null;
        var tmdbMovies = series.TmdbMovies ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbMovie>();

        return new RelayRawSeries(series.ID, series.Type, displayTitle, episodes)
        {
            UseTmdbNumbering = enforceTmdbNumbering,
            IsTmdbMovie = enforceTmdbNumbering ? tmdbMovies.Any() : series.Type == AnimeType.Movie,
            PreferredTmdbOrderingId = preferredOrderingId,
            RawPreferredTmdbOrderingId = rawPreferredOrderingId,
            AnidbAnimeId = series.AnidbAnimeID,
            AirDate = _options.MergeTmdbSeries ? series.AirDate : null,
            TmdbSeriesId = _options.MergeTmdbSeries ? tmdbShows.FirstOrDefault()?.ID : null,
        };
    }

    private RelayRawVideo ExtractVideo(IVideo video, bool isMainEpisode)
    {
        var locations = video.Files ?? Array.Empty<IVideoFile>();
        var selected = SelectSource(locations, out bool excluded);
        var source = selected?.ManagedFolderId == _managedFolderId ? selected : null;
        // Extras resolve across all eligible managed folders (upstream links them from any
        // import root); main files keep the strict mounted-folder gate above.
        if (source is null && !isMainEpisode && !excluded)
        {
            source = SelectCrossFolderSource(locations, out bool fallbackExcluded);
            excluded |= fallbackExcluded;
        }
        // Upstream sorts part/dup candidates by the FIRST physical location's basename,
        // regardless of eligibility (MapHelper.cs:167-170).
        var sortPath = NormalizeForSort(locations.FirstOrDefault()?.RelativePath);
        var crossReferences = (video.CrossReferences ?? Array.Empty<IVideoCrossReference>())
            .Select(reference => new RelayRawCrossReference(reference.ShokoEpisode?.ID, reference.ShokoEpisode?.SeriesID))
            .ToArray();

        return new RelayRawVideo(video.ID, video.IsVariation, sortPath, source?.AbsolutePath, source?.Size ?? selected?.Size ?? 0, source?.Extension ?? selected?.Extension ?? "", crossReferences)
        {
            IsExcluded = excluded,
        };
    }

    private SelectedSource? SelectSource(IEnumerable<IVideoFile> locations, out bool excluded)
    {
        excluded = false;
        foreach (var location in locations)
        {
            if (!IsFolderEligible(location))
                continue;
            if (!TryResolveSource(location, out var resolved))
                continue;
            if (IsPathExcluded(resolved.RelativePath, resolved.AbsolutePath))
            {
                excluded = true;
                return null;
            }

            return new SelectedSource(
                resolved.AbsolutePath,
                resolved.RelativePath,
                location.ManagedFolderID,
                location.Size,
                resolved.Extension
            );
        }

        return null;
    }

    /// <summary>
    /// Extras-only fallback: resolve each location against ITS OWN managed folder's root so a
    /// file indexed in a different eligible folder than the mounted one still links (upstream
    /// resolves extra sources across all import roots). Mounted folder is preferred first to
    /// keep the current resolution when it works; eligibility and path exclusions still apply.
    /// </summary>
    private SelectedSource? SelectCrossFolderSource(IEnumerable<IVideoFile> locations, out bool excluded)
    {
        excluded = false;
        foreach (var location in locations.OrderBy(location => location.ManagedFolderID == _managedFolderId ? 0 : 1))
        {
            if (!IsFolderEligible(location))
                continue;
            string root = location.ManagedFolderID == _managedFolderId
                ? _managedFolderRoot
                : TranslateServerPath(location.ManagedFolder?.Path ?? "");
            if (string.IsNullOrWhiteSpace(root))
                continue;
            if (!TryResolveSource(location, root, out var resolved))
                continue;
            if (IsPathExcluded(resolved.RelativePath, resolved.AbsolutePath))
            {
                excluded = true;
                return null;
            }

            return new SelectedSource(
                resolved.AbsolutePath,
                resolved.RelativePath,
                location.ManagedFolderID,
                location.Size,
                resolved.Extension
            );
        }

        return null;
    }

    /// <summary>
    /// Prefix-swaps a SERVER-side managed-folder root to a host path
    /// (<c>ServerPathRoot → ManagedFolderPathRoot</c>, identity when unset — the in-process
    /// plugin case). Mirrors <c>DaemonOrchestrator.MapPath</c>, which only ever sees the
    /// mounted root before it reaches the data source.
    /// </summary>
    private string TranslateServerPath(string serverPath)
    {
        string from = _options.ServerPathRoot.TrimEnd('/');
        string to = _options.ManagedFolderPathRoot.TrimEnd('/');
        if (from.Length == 0 || to.Length == 0 || !serverPath.StartsWith(from, StringComparison.Ordinal))
            return serverPath;
        return to + serverPath[from.Length..];
    }

    private bool IsFolderEligible(IVideoFile location)
    {
        var folder = location.ManagedFolder;
        if (folder is null)
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

    private bool TryResolveSource(IVideoFile location, out ResolvedSource source) =>
        TryResolveSource(location, _managedFolderRoot, out source);

    private bool TryResolveSource(IVideoFile location, string root, out ResolvedSource source)
    {
        source = default!;
        string relative = NormalizeSeparators(location.RelativePath ?? "");
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
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
            if (!IsContained(candidate, normalizedRoot))
                return false;
            // Upstream ResolveSourcePath only resolves files that physically exist
            // (VfsShared.cs:74-88); a stale DB row must not produce a VFS entry.
            if (!File.Exists(candidate))
                return false;

            source = new ResolvedSource(candidate, relative, Path.GetExtension(candidate));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
        }

        return false;
    }

    private bool IsContained(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);
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

    private IReadOnlyList<RelayRawTmdbEpisode> ExtractTmdbEpisodes(IShokoEpisode episode, bool loadAlternateOrderings) =>
        (episode.TmdbEpisodes ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbEpisode>())
            .Select(tmdb => new RelayRawTmdbEpisode(
                tmdb.SeasonNumber,
                tmdb.EpisodeNumber,
                tmdb.OrderingID,
                loadAlternateOrderings
                    ? (tmdb.AllOrderings ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbEpisodeOrderingInformation>())
                        .Select(ordering => new RelayRawTmdbOrdering(ordering.OrderingID, ordering.SeasonNumber, ordering.EpisodeNumber))
                        .ToArray()
                    : Array.Empty<RelayRawTmdbOrdering>(),
                tmdb.PreferredTitle?.Value
            ))
            .ToArray();

    private string ResolveSeriesTitle(IShokoSeries series)
    {
        string raw = GetTitleByLanguage(series, _options.SeriesTitleLanguage);
        return _options.MoveCommonSeriesTitlePrefixes && !string.IsNullOrWhiteSpace(raw)
            ? s_seriesPrefixRegex.Replace(raw, "$2 — $1")
            : raw;
    }

    private string ResolveEpisodeTitle(IShokoEpisode episode, IShokoSeries series, string displaySeriesTitle)
    {
        string raw = GetTitleByLanguage(episode, _options.EpisodeTitleLanguage);
        var tmdbEpisodes = episode.TmdbEpisodes ?? Array.Empty<Shoko.Abstractions.Metadata.Tmdb.ITmdbEpisode>();
        string? tmdbTitle = tmdbEpisodes.FirstOrDefault()?.PreferredTitle?.Value;

        if (episode.EpisodeNumber == 1 && s_ambiguousTitles.Contains(raw))
        {
            string title = displaySeriesTitle;
            if (title == raw)
                title = tmdbTitle ?? GetTitleByLanguage(series, "en");
            if (title != raw && !title.Contains(raw, StringComparison.Ordinal))
            {
                string result = raw == "Complete Movie" ? s_movieDescriptorRegex.Replace(title, "").Trim() : title;
                return $"{result} — {raw}";
            }
            return title;
        }

        if (_options.TmdbEpGroupNames && tmdbEpisodes.Count > 1 && !string.IsNullOrEmpty(tmdbTitle))
            return tmdbTitle;

        return !string.IsNullOrEmpty(tmdbTitle) && s_defaultTitleRegex.IsMatch(raw) && !s_defaultTitleRegex.IsMatch(tmdbTitle)
            ? tmdbTitle
            : raw;
    }

    private static string GetTitleByLanguage(IWithTitles? item, string languageSetting)
    {
        if (item is null)
            return "";

        string preferred = item.PreferredTitle?.Value ?? "";
        if (string.IsNullOrWhiteSpace(languageSetting))
            return preferred;

        foreach (var language in languageSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (language.Equals("SHOKO", StringComparison.OrdinalIgnoreCase))
                return preferred;

            var title = (item.Titles ?? Array.Empty<ITitle>())
                .Where(candidate => candidate.Type != TitleType.Short)
                .OrderBy(candidate => candidate.Type switch
                {
                    TitleType.Main => 1,
                    TitleType.Official => 2,
                    TitleType.Synonym => 3,
                    _ => 4,
                })
                .FirstOrDefault(candidate => candidate.LanguageCode.Equals(language, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(candidate.Value));
            if (title is not null)
                return title.Value;
        }

        return preferred;
    }

    private bool IsEligibleManagedFolder()
    {
        if (_options.ManagedFolderType.HasFlag(DropFolderType.Source) && !_options.ManagedFolderType.HasFlag(DropFolderType.Destination))
            return false;

        return !SplitLines(_options.ManagedFolderExclusions).Any(exclusion =>
            string.Equals(exclusion, _managedFolderId.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(exclusion, _options.ManagedFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsPathExcluded(string path, string absoluteSourcePath)
    {
        var excluded = new HashSet<string>(SplitLines(_options.FolderExclusions), StringComparer.OrdinalIgnoreCase)
        {
            _options.RelayTvFolderName,
            _options.RelayMovieFolderName,
            // Upstream GetIgnoredFolderNames also hides the two feature roots (VfsShared.cs:159-182).
            RelayIgnoreRules.AnimeThemesRootName,
            RelayIgnoreRules.CollectionImagesRootName,
        };

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (excluded.Contains(segment))
                return true;
            if (_options.PlexLocalExtras && s_localExtraDirRegex.IsMatch(segment))
                return true;
        }

        // Upstream IsPathIgnored tail: inline "-trailer"-style files next to a matching video.
        return _options.PlexLocalExtras && RelayIgnoreRules.IsInlineLocalExtraFile(absoluteSourcePath);
    }

    private static IEnumerable<string> SplitLines(string? value) =>
        (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record ResolvedSource(string AbsolutePath, string RelativePath, string Extension);

    private sealed record SelectedSource(string AbsolutePath, string RelativePath, int ManagedFolderId, long Size, string Extension);
}
