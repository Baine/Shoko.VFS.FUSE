using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
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

    public DropFolderType ManagedFolderDropFolderType
    {
        get => ManagedFolderType;
        init => ManagedFolderType = value;
    }
}

public sealed class RelayShokoPathDataSource : IShokoPathDataSource
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
    private readonly RelayPathDataSourceOptions _options;
    private readonly int _managedFolderId;
    private readonly string _managedFolderRoot;
    private readonly StringComparison _pathComparison;

    public RelayShokoPathDataSource(IMetadataService metadataService, RelayPathDataSourceOptions options)
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
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public IReadOnlyList<SeriesData> GetAllSeries()
    {
        if (!IsEligibleManagedFolder())
            return [];

        var allSeries = (_metadataService.GetAllShokoSeries() ?? Array.Empty<IShokoSeries>()).ToArray();
        var localSeedIds = allSeries
            .Where(series => (series.Episodes ?? Array.Empty<IShokoEpisode>())
                .Any(episode => (episode.Videos ?? Array.Empty<IVideo>())
                    .Any(video => (video.Files ?? Array.Empty<IVideoFile>())
                        .Any(file => file.ManagedFolderID == _managedFolderId))))
            .Select(series => series.ID)
            .ToHashSet();
        if (localSeedIds.Count == 0)
            return [];

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
        var rawSeries = allSeries
            .Where(series => groupClosure.Contains(series.ID))
            .Select(ExtractSeries)
            .ToList();
        var projected = rawSeries.Select(RelayMappingProjector.Project).ToList();
        var groupingInputs = rawSeries
            .Zip(projected, (raw, data) => new RelaySeriesGroupingInput(data, raw.AnidbAnimeId, raw.AirDate, raw.TmdbSeriesId))
            .ToArray();
        return RelaySeriesGrouper.Merge(groupingInputs, _options.MergeTmdbSeries, manualOverrides);
    }

    private RelayRawSeries ExtractSeries(IShokoSeries series)
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
