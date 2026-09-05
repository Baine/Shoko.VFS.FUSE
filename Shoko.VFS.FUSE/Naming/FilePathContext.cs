namespace Shoko.VFS.FUSE.Naming;

public sealed record EpisodeFileContext(
    int Season,
    int Episode,
    int? EndEpisode,
    int EpisodePad,
    int FileId,
    string Extension,
    string? SeriesTitle,
    bool OmitFileId,
    int? PartIndex,
    int? PartCount,
    int? VersionIndex,
    bool IsVariation);

public sealed record MovieFileContext(
    int EpisodeId,
    int FileId,
    string Extension,
    bool OmitFileId,
    int? PartIndex,
    int? PartCount,
    int? VersionIndex,
    bool IsVariation);

public sealed record ExtrasFileContext(
    int Season,
    int Episode,
    int EpisodePad,
    string Title,
    string Extension,
    int? PartIndex,
    int? PartCount,
    int? VersionIndex,
    bool IsVariation);
