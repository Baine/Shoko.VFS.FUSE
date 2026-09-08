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

    public DropFolderType ManagedFolderDropFolderType
    {
        get => ManagedFolderType;
        init => ManagedFolderType = value;
    }
}

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
        var merged = groups.Select(group => group.Data).ToList();

        // Discover AnimeThemes Theme.mp3 files written by ShokoRelay's AnimeThemesMp3Generator
        // into the non-VFS source folders, and surface them as series-folder-root extras.
        // Mirrors the host daemon's ShokoRelayDataSource.DiscoverThemeExtras; the resolver
        // renders them at <seriesFolder>/Theme.mp3. Restricted to closedSeries so the same
        // probe discipline applies as the main aggregation path.
        var extrasBySeries = DiscoverThemeExtras(closedSeries);
        if (extrasBySeries.Count == 0)
            return merged;
        return merged
            .Select(s => extrasBySeries.TryGetValue(s.SeriesId, out var extra)
                ? s with { Extras = new[] { extra } }
                : s)
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
    /// Structure-only pass: series/movie directory nodes with empty mappings, so the
    /// resolver can publish the mount layout without materializing any TV series'
    /// episodes/videos/files. Movie-closure groups carry exactly one real main mapping
    /// (the resolver skips source-less mappings when building movie folders, so the
    /// episode-ID folder would otherwise never appear for routing).
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

        var extrasBySeries = DiscoverThemeExtras(loaded);
        if (extrasBySeries.TryGetValue(seriesId, out var theme))
            data = data with { Extras = [theme] };
        return data;
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
        var main = mappings.FirstOrDefault(mapping => mapping.IsMain && !string.IsNullOrWhiteSpace(mapping.SourcePath))
            ?? mappings.FirstOrDefault(mapping => !string.IsNullOrWhiteSpace(mapping.SourcePath));
        return main is null ? [] : [main];
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
    /// Probes every distinct source-series folder in the managed folder for a Theme.mp3 file
    /// and produces a seriesId → ExtraFile map (first match wins per series). Used by
    /// <see cref="GetAllSeries"/> to surface the theme in the VFS mount — replaces the
    /// symlink step that ShokoRelay's AnimeThemesMp3Generator skips when
    /// <c>Advanced.UseExternalVfs=true</c>.
    /// </summary>
    private Dictionary<int, ExtraFile> DiscoverThemeExtras(IReadOnlyList<IShokoSeries> allSeries)
    {
        var result = new Dictionary<int, ExtraFile>();
        if (allSeries.Count == 0)
            return result;

        // Cache probed absolute folders so we stat each one at most once per build.
        var probedFolders = new HashSet<string>(StringComparer.FromComparison(_pathComparison));
        foreach (var series in allSeries)
        {
            foreach (var episode in series.Episodes ?? Array.Empty<IShokoEpisode>())
            {
                if (episode.IsHidden) continue;
                foreach (var video in episode.Videos ?? Array.Empty<IVideo>())
                {
                    foreach (var file in video.Files ?? Array.Empty<IVideoFile>())
                    {
                        if (file.ManagedFolderID != _managedFolderId) continue;
                        if (string.IsNullOrWhiteSpace(file.RelativePath)) continue;

                        var sourceFolder = ResolveSourceFolder(file.RelativePath);
                        if (sourceFolder is null || !probedFolders.Add(sourceFolder)) continue;

                        var themePath = Path.Combine(sourceFolder, "Theme.mp3");
                        if (!File.Exists(themePath)) continue;

                        long size;
                        try { size = new FileInfo(themePath).Length; }
                        catch { size = 0; }

                        result.TryAdd(series.ID, new ExtraFile("Theme.mp3", themePath, size));
                    }
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

        string? parent = Path.GetDirectoryName(relative);
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
                    rawVideo = ExtractVideo(video);
                    videos.Add(video.ID, rawVideo);
                }
                if (!rawVideo.IsExcluded)
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

    private RelayRawVideo ExtractVideo(IVideo video)
    {
        var locations = video.Files ?? Array.Empty<IVideoFile>();
        var selected = SelectSource(locations, out bool excluded);
        var source = selected?.ManagedFolderId == _managedFolderId ? selected : null;
        var sortPath = selected?.RelativePath ?? locations
            .Select(location => NormalizeForSort(location.RelativePath))
            .FirstOrDefault();
        var crossReferences = (video.CrossReferences ?? Array.Empty<IVideoCrossReference>())
            .Select(reference => new RelayRawCrossReference(reference.ShokoEpisode?.ID, reference.ShokoEpisode?.SeriesID))
            .ToArray();

        return new RelayRawVideo(video.ID, video.IsVariation, sortPath, source?.AbsolutePath, selected?.Size ?? 0, selected?.Extension ?? "", crossReferences)
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
            if (IsPathExcluded(resolved.RelativePath))
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

    private bool TryResolveSource(IVideoFile location, out ResolvedSource source)
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

    private sealed record ResolvedSource(string AbsolutePath, string RelativePath, string Extension);

    private sealed record SelectedSource(string AbsolutePath, string RelativePath, int ManagedFolderId, long Size, string Extension);
}
