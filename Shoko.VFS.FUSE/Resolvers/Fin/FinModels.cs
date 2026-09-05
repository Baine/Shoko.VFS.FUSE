using System.Collections.ObjectModel;
using System.Collections.Frozen;

namespace Shoko.VFS.FUSE.Resolvers.Fin;

/// <summary>Immutable identity and multi-series policy for one Fin library.</summary>
public sealed record FinLibraryProfile
{
    public const int DefaultMultiSeriesAniDbId = 3651;

    public FinLibraryProfile(
        Guid libraryId,
        IEnumerable<int>? multiSeriesAniDbAllowlist = null)
    {
        LibraryId = libraryId;
        MultiSeriesAniDbAllowlist = (multiSeriesAniDbAllowlist ?? [DefaultMultiSeriesAniDbId]).ToFrozenSet();
    }

    public Guid LibraryId { get; }

    public IReadOnlySet<int> MultiSeriesAniDbAllowlist { get; }

    public string VirtualRootSegment => LibraryId.ToString();

    public string GetVirtualRootPath(string globalVfsRoot) => Path.Join(globalVfsRoot, LibraryId.ToString());
}

/// <summary>Immutable mapping from one Shoko managed folder into a Fin library.</summary>
public sealed record FinMediaFolderMapping
{
    public FinMediaFolderMapping(
        Guid libraryId,
        int managedFolderId,
        string managedFolderSubPath,
        IEnumerable<string> mediaFolderPaths)
    {
        LibraryId = libraryId;
        ManagedFolderId = managedFolderId;
        ManagedFolderSubPath = managedFolderSubPath ?? throw new ArgumentNullException(nameof(managedFolderSubPath));
        MediaFolderPaths = new ReadOnlyCollection<string>((mediaFolderPaths ?? throw new ArgumentNullException(nameof(mediaFolderPaths))).ToArray());
    }

    public Guid LibraryId { get; }
    public int ManagedFolderId { get; }
    public string ManagedFolderSubPath { get; }
    public IReadOnlyList<string> MediaFolderPaths { get; }
}

/// <summary>One raw managed-folder location for a Shokofin file.</summary>
public sealed record FinRawLocation(int ManagedFolderId, string RelativePath);

/// <summary>One raw Shoko cross-reference for a file.</summary>
public sealed record FinRawFileCrossReference(
    int? ShokoSeriesId,
    int AniDbId,
    bool AllEpisodesHaveShokoId)
{
    public int AniDBId => AniDbId;
}

/// <summary>Raw file metadata needed by Fin source admission.</summary>
public sealed record FinRawFile
{
    public FinRawFile(
        int fileId,
        IEnumerable<FinRawLocation> locations,
        IEnumerable<FinRawFileCrossReference> crossReferences)
    {
        FileId = fileId;
        Locations = new ReadOnlyCollection<FinRawLocation>((locations ?? throw new ArgumentNullException(nameof(locations))).ToArray());
        CrossReferences = new ReadOnlyCollection<FinRawFileCrossReference>((crossReferences ?? throw new ArgumentNullException(nameof(crossReferences))).ToArray());
    }

    public int FileId { get; }
    public IReadOnlyList<FinRawLocation> Locations { get; }
    public IReadOnlyList<FinRawFileCrossReference> CrossReferences { get; }
}

/// <summary>A source file admitted for one Shoko series by the Fin selector.</summary>
public sealed record FinAdmittedFile(
    string SourcePath,
    int FileId,
    int ShokoSeriesId,
    int AniDbId)
{
    public int SeriesId => ShokoSeriesId;
    public int AniDBId => AniDbId;
}

/// <summary>Episode types needed by the pure Fin path projector.</summary>
public enum FinEpisodeType
{
    Episode = 1,
    Special = 3,
    Trailer = 4,
    Credits = 5,
    OpeningSong = 6,
    EndingSong = 7,
    Parody = 8,
    Interview = 9,
    Extra = 10,
    Other = 2,
}

/// <summary>Extra categories used by Shokofin's virtual path routing.</summary>
public enum FinExtraType
{
    ThemeSong,
    ThemeVideo,
    Trailer,
    BehindTheScenes,
    DeletedScene,
    Clip,
    Interview,
    Scene,
    Sample,
    Featurette,
    Other,
}

/// <summary>Episode conversion modes mirrored from Shokofin's SeriesEpisodeConversion.</summary>
public enum SeriesEpisodeConversion
{
    None = 0,
    EpisodesAsSpecials = 1,
    SpecialsAsEpisodes = 2,
    SpecialsAsExtraFeaturettes = 3,
}

/// <summary>Specials placement modes mirrored from Shokofin's SpecialOrderType.</summary>
public enum SpecialOrderingType
{
    Excluded = 1,
    AfterSeason = 2,
    InBetweenSeasonMixed = 3,
    InBetweenSeasonByAirDate = 4,
    InBetweenSeasonByOtherData = 5,
}

/// <summary>Series type mirrored from Shokofin's SeriesType.</summary>
public enum FinSeriesType
{
    None = 0,
    Unknown = 1,
    Other = 2,
    TV = 3,
    TVSpecial = 4,
    Web = 5,
    Movie = 6,
    OVA = 7,
    MusicVideo = 8,
}

/// <summary>Already-resolved show data required for path generation.</summary>
public sealed record FinShowProjectionInput(
    int ShowId,
    string? DefaultAniDbTitle,
    string? DefaultTmdbTitle,
    int EpisodePadding);

/// <summary>Already-resolved season and episode placement data.</summary>
public sealed record FinEpisodeProjectionInput(
    int SeasonId,
    int EpisodeId,
    int EpisodeNumber,
    int SeasonNumber,
    FinEpisodeType EpisodeType,
    bool IsSpecial,
    bool IsExtra,
    bool IsAlternate,
    string? EnglishAniDbTitle);

/// <summary>One already-resolved multipart file and its one-based output part index.</summary>
public sealed record FinMultipartPart(int FileId, int PartIndex);

/// <summary>Optional already-resolved release group details.</summary>
public sealed record FinReleaseGroup(int Id, string? ShortName, string? Name);

/// <summary>Path-affecting switches matching the pinned Fin defaults.</summary>
public sealed record FinProjectorOptions
{
    public bool AddTrailers { get; init; } = true;
    public bool AddCreditsAsThemeVideos { get; init; } = true;
    public bool AddCreditsAsSpecialFeatures { get; init; }
    public bool MovieSpecialsAsExtraFeaturettes { get; init; }
    public bool AddReleaseGroup { get; init; }
    public bool AddResolution { get; init; }
}

/// <summary>One episode fed into the season ordering engine.</summary>
public sealed record FinOrderingEpisode(
    string SeasonId,
    int EpisodeId,
    FinEpisodeType Type,
    int AniDbEpisodeNumber,
    DateTimeOffset? AiredAt,
    bool IsHidden,
    bool IsMainEntry,
    FinExtraType? ExtraType,
    string? Title);

/// <summary>Season ordering engine inputs for one season.</summary>
public sealed record FinSeasonOrderingInput(
    string SeasonId,
    IReadOnlyList<string> ExtraIds,
    bool OrderByAirdate,
    SeriesEpisodeConversion EpisodeConversion,
    SpecialOrderingType SpecialsPlacement,
    IReadOnlyList<FinOrderingEpisode> Episodes,
    FinSeriesType Type = FinSeriesType.TV,
    bool IsCustomType = false);

/// <summary>Output bucket for one season after ordering.</summary>
public sealed record FinSeasonOrderingResult(
    string SeasonId,
    IReadOnlyList<FinPlacedEpisode> Episodes,
    IReadOnlyList<FinPlacedEpisode> AlternateEpisodes,
    IReadOnlyList<FinPlacedEpisode> Specials,
    IReadOnlyList<FinPlacedEpisode> Extras,
    int SeasonNumber,
    bool WasPromotedToAlt,
    string? PromotedSeriesType,
    FinSeriesType SeriesType);

/// <summary>An episode with its final placement within a bucket.</summary>
public sealed record FinPlacedEpisode(
    int EpisodeId,
    int EpisodeNumber,
    int SeasonNumber,
    bool IsSpecial,
    bool IsExtra,
    bool IsAlternate);

/// <summary>All already-resolved values needed to project one admitted source file.</summary>
public sealed record FinProjectionInput
{
    public FinProjectionInput(
        FinAdmittedFile admittedFile,
        FinShowProjectionInput show,
        FinEpisodeProjectionInput episode,
        bool isMovieLibrary,
        DateTimeOffset createdAt,
        DateTimeOffset? importedAt = null,
        string sourceExtension = "",
        FinExtraType? extraType = null,
        IEnumerable<int>? availableMovieEpisodeIds = null,
        IEnumerable<FinMultipartPart>? multipartParts = null,
        FinReleaseGroup? releaseGroup = null,
        string? resolution = null)
    {
        AdmittedFile = admittedFile ?? throw new ArgumentNullException(nameof(admittedFile));
        Show = show ?? throw new ArgumentNullException(nameof(show));
        Episode = episode ?? throw new ArgumentNullException(nameof(episode));
        IsMovieLibrary = isMovieLibrary;
        CreatedAt = createdAt;
        ImportedAt = importedAt;
        SourceExtension = sourceExtension ?? throw new ArgumentNullException(nameof(sourceExtension));
        ExtraType = extraType;
        AvailableMovieEpisodeIds = new ReadOnlyCollection<int>((availableMovieEpisodeIds ?? []).ToArray());
        MultipartParts = new ReadOnlyCollection<FinMultipartPart>((multipartParts ?? []).ToArray());
        ReleaseGroup = releaseGroup;
        Resolution = resolution;
    }

    public FinAdmittedFile AdmittedFile { get; }
    public FinShowProjectionInput Show { get; }
    public FinEpisodeProjectionInput Episode { get; }
    public bool IsMovieLibrary { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ImportedAt { get; }
    public string SourceExtension { get; }
    public FinExtraType? ExtraType { get; }
    public IReadOnlyList<int> AvailableMovieEpisodeIds { get; }
    public IReadOnlyList<FinMultipartPart> MultipartParts { get; }
    public FinReleaseGroup? ReleaseGroup { get; }
    public string? Resolution { get; }
}
