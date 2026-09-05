using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.VFS.FUSE.Models;
using System.Collections.ObjectModel;

namespace Shoko.VFS.FUSE.Resolvers.Fin;

/// <summary>
/// Fin options carried from the mount/runtime into the data-source build.
/// The mount/runtime layer supplies the global-root and library profile.
/// </summary>
public sealed record FinShokoPathDataSourceOptions
{
    /// <summary>Physical source-availability probe; resolved by the background build, never an FUSE callback.</summary>
    public Func<string, bool>? FileExists { get; init; }

    public FinProjectorOptions ProjectorOptions { get; init; } = new();
}

/// <summary>
/// Resolves real Shoko metadata from public abstractions into the pure Fin
/// layer (FinSourceSelector → FinSeasonOrdering → FinPathProjector) and exposes
/// the resulting nodes as an <see cref="IVirtualTreeDataSource"/> for the
/// generic immutable tree.
///
/// The output is <b>Shokofin default-profile path parity</b> for the common
/// single-series TV/movie case driven by Shoko series/episode metadata. The
/// ordering engine (FinSeasonOrdering) is fed real episodes and its placed
/// episode output (normal/alternate/special/extra + season/episode numbers)
/// drives projection. Deep TMDB-driven alternate-season membership, base-season
/// numbers, and special classification derived from Shokofin's ShowInfo/
/// SeasonInfo REST pipeline are surfaced only when a resolved public
/// cross-reference supplies them; they are never synthesized. See
/// <c>ponytail:</c> notes below.
/// </summary>
public sealed class FinShokoPathDataSource : IVirtualTreeDataSource
{
    private readonly IMetadataService _metadataService;
    private readonly FinLibraryProfile _profile;
    private readonly IReadOnlyList<FinMediaFolderMapping> _mediaMappings;
    private readonly FinShokoPathDataSourceOptions _options;
    private readonly Func<string, bool> _fileExists;

    public FinShokoPathDataSource(
        IMetadataService metadataService,
        FinLibraryProfile profile,
        IEnumerable<FinMediaFolderMapping> mediaMappings,
        FinShokoPathDataSourceOptions? options = null)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _mediaMappings = new ReadOnlyCollection<FinMediaFolderMapping>(
            (mediaMappings ?? throw new ArgumentNullException(nameof(mediaMappings))).ToArray());
        _options = options ?? new FinShokoPathDataSourceOptions();
        _fileExists = _options.FileExists ?? (path => File.Exists(path));
    }

    /// <inheritdoc/>
    public IReadOnlyList<VirtualTreeEntryData> GetEntries()
    {
        var rawFiles = BuildRawFiles();
        var admitted = FinSourceSelector.GetFilesForManagedFolders(_profile, _mediaMappings, rawFiles, _fileExists);
        if (admitted.Count == 0)
            return [];

        var projectedInputs = BuildProjectionInputs(admitted);
        return FinPathProjector.Project(_profile, projectedInputs, _options.ProjectorOptions);
    }

    private IReadOnlyList<FinRawFile> BuildRawFiles()
    {
        var allSeries = (_metadataService.GetAllShokoSeries() ?? Array.Empty<IShokoSeries>()).ToArray();
        var bySeries = allSeries.ToDictionary(series => series.ID);
        var files = new Dictionary<int, (List<FinRawLocation> Locations, List<FinRawFileCrossReference> CrossRefs)>();

        foreach (var series in allSeries)
        {
            foreach (var episode in series.Episodes ?? Array.Empty<IShokoEpisode>())
            {
                foreach (var video in episode.Videos ?? Array.Empty<IVideo>())
                {
                    foreach (var file in video.Files ?? Array.Empty<IVideoFile>())
                    {
                        if (!files.TryGetValue(file.ID, out var entry))
                        {
                            entry = (new List<FinRawLocation>(), new List<FinRawFileCrossReference>());
                            files[file.ID] = entry;
                        }

                        string relative = file.RelativePath ?? "";
                        if (!relative.StartsWith('/'))
                            relative = "/" + relative;
                        entry.Locations.Add(new FinRawLocation(file.ManagedFolderID, relative));

                        // One cross-reference identity (series + AniDB episode) per
                        // resolved Shoko link; AllEpisodesHaveShokoId reflects that
                        // the video is attached to a real resolved Shoko episode.
                        foreach (var crossRef in video.CrossReferences ?? Array.Empty<IVideoCrossReference>())
                        {
                            if (crossRef.ShokoSeries is null || crossRef.ShokoEpisode is null)
                                continue;
                            if (!bySeries.ContainsKey(crossRef.ShokoSeries.ID))
                                continue;

                            var identity = new FinRawFileCrossReference(
                                crossRef.ShokoSeries.ID,
                                crossRef.AnidbEpisodeID,
                                AllEpisodesHaveShokoId: true);
                            if (!entry.CrossRefs.Any(existing =>
                                    existing.ShokoSeriesId == identity.ShokoSeriesId
                                    && existing.AniDbId == identity.AniDbId))
                                entry.CrossRefs.Add(identity);
                        }
                    }
                }
            }
        }

        return files
            .Select(kv => new FinRawFile(
                kv.Key,
                kv.Value.Locations,
                kv.Value.CrossRefs))
            .ToArray();
    }

    private IReadOnlyList<FinProjectionInput> BuildProjectionInputs(IReadOnlyList<FinAdmittedFile> admitted)
    {
        var bySeries = new Dictionary<int, IShokoSeries>();
        foreach (var series in _metadataService.GetAllShokoSeries() ?? Array.Empty<IShokoSeries>())
            bySeries[series.ID] = series;

        // Group admitted files by series so each series runs its own ordering pass.
        var admittedBySeries = admitted
            .GroupBy(file => file.ShokoSeriesId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var inputs = new List<FinProjectionInput>(admitted.Count);
        foreach (var (seriesId, seriesFiles) in admittedBySeries)
        {
            if (!bySeries.TryGetValue(seriesId, out var series))
                continue;

            var (placements, seriesType, availableEpisodeIds) = BuildSeasonPlacements(series);

            foreach (var file in seriesFiles)
            {
                var (episode, video) = FindEpisodeAndVideo(series, file.FileId);
                var placed = episode is not null && placements.TryGetValue(episode.ID, out var p)
                    ? p
                    : new FinPlacedEpisode(file.FileId, 1, 0, IsSpecial: false, IsExtra: false, IsAlternate: false);

                var episodeInput = new FinEpisodeProjectionInput(
                    SeasonId: series.ID,
                    EpisodeId: file.FileId,
                    EpisodeNumber: placed.EpisodeNumber,
                    SeasonNumber: placed.SeasonNumber,
                    EpisodeType: ToFinEpisodeType(episode?.Type),
                    IsSpecial: placed.IsSpecial,
                    IsExtra: placed.IsExtra,
                    IsAlternate: placed.IsAlternate,
                    EnglishAniDbTitle: episode?.AnidbEpisode?.Title);

                var show = new FinShowProjectionInput(
                    ShowId: seriesId,
                    DefaultAniDbTitle: series.AnidbAnime?.Title,
                    DefaultTmdbTitle: series.TmdbShows?.FirstOrDefault()?.Title,
                    EpisodePadding: 3);

                inputs.Add(new FinProjectionInput(
                    file,
                    show,
                    episodeInput,
                    isMovieLibrary: IsMovieLibrary(seriesType, series),
                    createdAt: new DateTimeOffset(series.CreatedAt),
                    importedAt: video is not null && video.ImportedAt is { } imported
                        ? new DateTimeOffset(imported)
                        : null,
                    sourceExtension: Path.GetExtension(file.SourcePath),
                    availableMovieEpisodeIds: availableEpisodeIds,
                    releaseGroup: ResolveReleaseGroup(video),
                    resolution: ResolveResolution(video)));
            }
        }

        return inputs;
    }

    /// <summary>
    /// Run the pure ordering engine over one series' episodes and return the
    /// placed episode per episode ID, the resolved season type, and the IDs of
    /// available (non-hidden) episodes used for movie-extras fan-out (D5).
    /// </summary>
    private static (IReadOnlyDictionary<int, FinPlacedEpisode>, FinSeriesType, IReadOnlyList<int>) BuildSeasonPlacements(IShokoSeries series)
    {
        // ponytail: one season per series using the series ID as the season key.
        // The base season number is 1, matching Shokofin's single-season ShowInfo
        // constructor (ShowInfo.cs:516 SeasonNumberBaseDictionary = { id, 1 }).
        // Resolved multi-season / TMDB base-season numbers come from a later
        // cross-ref pipeline.
        string seasonId = series.ID.ToString();
        var orderingEpisodes = new List<FinOrderingEpisode>(series.Episodes?.Count ?? 0);
        foreach (var episode in series.Episodes ?? Array.Empty<IShokoEpisode>())
        {
            orderingEpisodes.Add(new FinOrderingEpisode(
                SeasonId: seasonId,
                EpisodeId: episode.ID,
                Type: ToFinEpisodeType(episode.Type),
                AniDbEpisodeNumber: episode.AnidbEpisode?.EpisodeNumber ?? episode.EpisodeNumber,
                AiredAt: ToAiredAt(episode),
                IsHidden: episode.IsHidden,
                IsMainEntry: episode.Type == EpisodeType.Episode,
                ExtraType: null,
                Title: episode.AnidbEpisode?.Title));
        }

        var input = new FinSeasonOrderingInput(
            SeasonId: seasonId,
            ExtraIds: [],
            OrderByAirdate: false,
            EpisodeConversion: SeriesEpisodeConversion.None,
            SpecialsPlacement: SpecialOrderingType.AfterSeason,
            Episodes: orderingEpisodes,
            Type: ToFinSeriesType(series.Type));

        // Base season number 1 for the single-season show, per canonical ShowInfo.
        var results = FinSeasonOrdering.Build([input], new Dictionary<string, int> { [seasonId] = 1 });

        var placements = new Dictionary<int, FinPlacedEpisode>();
        if (results.Count == 0)
            return (placements, input.Type, []);

        var result = results[0];
        foreach (var placed in result.Episodes)
            placements[placed.EpisodeId] = placed;
        foreach (var placed in result.AlternateEpisodes)
            placements[placed.EpisodeId] = placed;
        foreach (var placed in result.Specials)
            placements[placed.EpisodeId] = placed;
        foreach (var placed in result.Extras)
            placements[placed.EpisodeId] = placed;

        // D5: available (non-hidden) main episodes for movie-extras fan-out,
        // mirroring canonical season.EpisodeList.Where(e => e.IsAvailable).
        var availableEpisodeIds = (series.Episodes ?? Array.Empty<IShokoEpisode>())
            .Where(e => e.Type == EpisodeType.Episode && !e.IsHidden)
            .Select(e => e.ID)
            .ToArray();

        return (placements, result.SeriesType, availableEpisodeIds);
    }

    private static (IShokoEpisode? Episode, IVideo? Video) FindEpisodeAndVideo(IShokoSeries series, int fileId)
    {
        foreach (var episode in series.Episodes ?? Array.Empty<IShokoEpisode>())
        {
            foreach (var video in episode.Videos ?? Array.Empty<IVideo>())
            {
                foreach (var file in video.Files ?? Array.Empty<IVideoFile>())
                {
                    if (file.ID == fileId)
                        return (episode, video);
                }
            }
        }
        return (null, null);
    }

    private static DateTimeOffset? ToAiredAt(IShokoEpisode episode)
    {
        if (episode.AirDateWithTime is { } exact)
            return new DateTimeOffset(exact);
        if (episode.AirDate is { } date)
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return null;
    }

    private static FinEpisodeType ToFinEpisodeType(EpisodeType? type) => type switch
    {
        EpisodeType.Episode => FinEpisodeType.Episode,
        EpisodeType.Special => FinEpisodeType.Special,
        EpisodeType.Trailer => FinEpisodeType.Trailer,
        EpisodeType.Credits => FinEpisodeType.Credits,
        EpisodeType.Parody => FinEpisodeType.Parody,
        EpisodeType.Other => FinEpisodeType.Other,
        _ => FinEpisodeType.Episode,
    };

    private static FinReleaseGroup? ResolveReleaseGroup(IVideo? video)
    {
        var group = video?.ReleaseInfo?.Group;
        if (group is null)
            return null;
        int id;
        _ = int.TryParse(group.ID, out id);
        return new FinReleaseGroup(id, group.ShortName, group.Name);
    }

    private static string? ResolveResolution(IVideo? video)
        => video?.MediaInfo?.VideoStream?.Resolution;

    private static FinSeriesType ToFinSeriesType(AnimeType type) => type switch
    {
        AnimeType.Movie => FinSeriesType.Movie,
        AnimeType.Web => FinSeriesType.Web,
        AnimeType.OVA => FinSeriesType.OVA,
        AnimeType.MusicVideo => FinSeriesType.MusicVideo,
        AnimeType.TVSpecial => FinSeriesType.TVSpecial,
        AnimeType.Other => FinSeriesType.Other,
        _ => FinSeriesType.TV,
    };

    /// <summary>
    /// A season is a movie library when the <em>resolved</em> season type is
    /// Movie (canonical isMovieSeason = SeasonInfo.Type is Movie). A movie→Web
    /// promotion yields a Web season, so it routes as a TV-style library (D2).
    /// OVA/MusicVideo keep the adapter's existing movie routing.
    /// </summary>
    private static bool IsMovieLibrary(FinSeriesType resolvedType, IShokoSeries series)
        => resolvedType == FinSeriesType.Web
            ? false
            : series.Type is AnimeType.Movie or AnimeType.OVA or AnimeType.MusicVideo;
}
