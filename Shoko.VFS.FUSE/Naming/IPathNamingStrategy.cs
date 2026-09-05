namespace Shoko.VFS.FUSE.Naming;

/// <summary>
/// Generates virtual filesystem paths and filenames according to a media server's conventions.
/// Implementations produce the exact paths that relay/fin expect to see on disk.
/// </summary>
public interface IPathNamingStrategy
{
    /// <summary>Consumer identifier (e.g., "relay", "fin").</summary>
    string Consumer { get; }

    /// <summary>TV VFS root folder, or null when the layout is flat.</summary>
    string? TvRootFolderName { get; }

    /// <summary>Movie VFS root folder, or null when the layout is flat.</summary>
    string? MovieRootFolderName { get; }

    /// <summary>Whether a season is a mapped TV extras season.</summary>
    bool IsTvExtraSeason(int season);

    /// <summary>
    /// Generates the display name for a series folder.
    /// </summary>
    string FormatSeriesFolder(int seriesId, string? title);

    /// <summary>
    /// Generates the season folder name for a given season number.
    /// Season 0 = "Specials", season N = "Season N".
    /// </summary>
    string FormatSeasonFolder(int seasonNumber);

    /// <summary>
    /// Generates the filename for an episode file.
    /// </summary>
    string FormatEpisodeFileName(EpisodeFileContext ctx);

    /// <summary>
    /// Generates the filename for a movie file.
    /// </summary>
    string FormatMovieFileName(MovieFileContext ctx);

    /// <summary>
    /// Generates the directory name for a movie folder (movie VFS only).
    /// </summary>
    string FormatMovieFolder(int episodeId);

    /// <summary>
    /// Generates the subfolder name for extras inside a movie folder.
    /// </summary>
    string FormatExtrasFolder(int season);

    /// <summary>
    /// Generates the filename for an extras file.
    /// </summary>
    string FormatExtrasFileName(ExtrasFileContext ctx);
}
