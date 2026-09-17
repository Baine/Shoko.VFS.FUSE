using System.Text.RegularExpressions;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

/// <summary>
/// ShokoRelay ignore-rule mirrors that both the Host daemon and the in-tree plugin apply
/// when deciding whether a source path may back a VFS entry: the extra feature-root folder
/// names (upstream VfsShared.GetIgnoredFolderNames) and the inline local-extra FILE rule
/// (upstream VfsShared.IsPathIgnored tail: a Plex extra suffix plus a same-base sibling video).
/// </summary>
public static class RelayIgnoreRules
{
    /// <summary>Upstream ShokoRelayConstants.FolderAnimeThemesDefault.</summary>
    public const string AnimeThemesRootName = "!AnimeThemes";

    /// <summary>Upstream ShokoRelayConstants.FolderCollectionImagesDefault.</summary>
    public const string CollectionImagesRootName = "!CollectionImages";

    // Upstream VfsHelper.s_localExtraFileRegex, verbatim.
    private static readonly Regex s_localExtraFileRegex = new(
        @"-(?:behindthescenes|deleted|featurette|interview|scene|short|trailer|other)\d*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ponytail: Shoko's default allowed-video extension set; wire to the server's
    // IVideoService list if the daemon ever surfaces it (upstream queries the service).
    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mp4", ".m4v", ".mkv", ".mov", ".wmv", ".mpg", ".mpeg", ".m2ts", ".ts", ".webm", ".flv", ".iso",
    };

    /// <summary>True when the file has an extension Shoko accepts as video (upstream
    /// <c>videoService.IsAllowedVideoExtension</c>, used for local-extra scanning).</summary>
    public static bool IsAllowedVideoExtension(string path) =>
        s_videoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// True when the source file is a Plex inline local extra: its name carries an extra
    /// suffix and any file matching the trimmed base name in the same directory has a video
    /// extension. The extra file can itself be that sibling — upstream enumerates the same
    /// pattern and counts it, so we keep that behavior deliberately.
    /// </summary>
    public static bool IsInlineLocalExtraFile(string absoluteSourcePath)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(absoluteSourcePath);
        if (nameWithoutExt.Length == 0)
            return false;

        var match = s_localExtraFileRegex.Match(nameWithoutExt);
        if (!match.Success)
            return false;

        string searchPattern = string.Concat(nameWithoutExt.AsSpan(0, match.Index), ".*");
        string dir = Path.GetDirectoryName(absoluteSourcePath) is { Length: > 0 } parent ? parent : ".";
        try
        {
            foreach (var sibling in Directory.EnumerateFiles(dir, searchPattern))
                if (s_videoExtensions.Contains(Path.GetExtension(sibling)))
                    return true;
        }
        catch
        {
            // Upstream swallows enumeration errors here too (path vanished, permissions).
        }

        return false;
    }
}
