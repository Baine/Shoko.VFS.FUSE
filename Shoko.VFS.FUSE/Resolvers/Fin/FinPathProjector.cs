using System.Text.RegularExpressions;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers.Fin;

/// <summary>
/// Pure path/node projection for already-resolved Fin files.
/// Show, season, episode, and multipart resolution happen before this boundary.
/// </summary>
public static class FinPathProjector
{
    private const int NameCutOff = 64;
    private static readonly Regex s_underscoreRegex = new("_{2,}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex s_whitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.Singleline);

    public static IReadOnlyList<VirtualTreeEntryData> Project(
        FinLibraryProfile profile,
        IReadOnlyList<FinProjectionInput> inputs,
        FinProjectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        options ??= new FinProjectorOptions();

        var directories = new List<string>();
        var directoryTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var emittedPaths = new HashSet<string>(StringComparer.Ordinal);
        var symlinks = new List<VirtualTreeEntryData>();
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            ProjectInput(profile, input, options, directories, directoryTimes, emittedPaths, symlinks);
        }

        var result = new List<VirtualTreeEntryData>(directories.Count + symlinks.Count);
        result.AddRange(directories.Select(path => new VirtualTreeEntryData(
            path,
            VirtualNodeType.Directory,
            Mode: VirtualEntry.DefaultMode(VirtualNodeType.Directory),
            LastModified: directoryTimes[path]
        )));
        result.AddRange(symlinks);
        return result.AsReadOnly();
    }

    public static IReadOnlyList<VirtualTreeEntryData> Project(
        FinLibraryProfile profile,
        FinProjectionInput input,
        FinProjectorOptions? options = null)
        => Project(profile, [input], options);

    private static void ProjectInput(
        FinLibraryProfile profile,
        FinProjectionInput input,
        FinProjectorOptions options,
        List<string> directories,
        Dictionary<string, DateTimeOffset> directoryTimes,
        HashSet<string> emittedPaths,
        List<VirtualTreeEntryData> symlinks)
    {
        string showName = Sanitize(input.Show.DefaultAniDbTitle ?? input.Show.DefaultTmdbTitle ?? "");
        if (string.IsNullOrWhiteSpace(showName))
            showName = input.IsMovieLibrary ? "Movie" : "Series";
        showName = TruncateName(showName);

        string episodeName = Sanitize(input.Episode.EnglishAniDbTitle
            ?? (input.Episode.EpisodeType is FinEpisodeType.Episode
                ? $"Episode {input.Episode.EpisodeNumber}"
                : $"{CanonicalEpisodeTypeLabel(input.Episode.EpisodeType)} {input.Episode.EpisodeNumber}"));
        episodeName = TruncateName(episodeName);

        var extraFolders = GetExtraFolders(input, options);
        var folders = new List<string>();
        if (input.IsMovieLibrary)
        {
            if (extraFolders is not null)
            {
                foreach (var extraFolder in extraFolders)
                    foreach (var episodeId in input.AvailableMovieEpisodeIds)
                    {
                        string movieFolder = $"{showName} [Shoko Series={input.Episode.SeasonId}] [Shoko Episode={episodeId}]";
                        folders.Add(JoinVirtualPath(profile.VirtualRootSegment, movieFolder, extraFolder));
                    }
            }
            else
            {
                string movieFolder = $"{showName} [Shoko Series={input.Episode.SeasonId}] [Shoko Episode={input.Episode.EpisodeId}]";
                folders.Add(JoinVirtualPath(profile.VirtualRootSegment, movieFolder));
                episodeName = "Movie";
            }
        }
        else
        {
            int seasonNumber = input.Episode.IsSpecial ? 0 : input.Episode.SeasonNumber;
            string seasonFolder = $"Season {seasonNumber.ToString().PadLeft(2, '0')}";
            string showFolder = $"{showName} [Shoko Series={input.Show.ShowId}]";
            if (extraFolders is not null)
            {
                foreach (var extraFolder in extraFolders)
                {
                    folders.Add(JoinVirtualPath(profile.VirtualRootSegment, showFolder, extraFolder));
                    if (input.Episode.SeasonNumber is not 0)
                        folders.Add(JoinVirtualPath(profile.VirtualRootSegment, showFolder, seasonFolder, extraFolder));
                }
            }
            else
            {
                folders.Add(JoinVirtualPath(profile.VirtualRootSegment, showFolder, seasonFolder));
                episodeName = $"{showName} S{seasonNumber.ToString().PadLeft(2, '0')}E{input.Episode.EpisodeNumber.ToString().PadLeft(input.Show.EpisodePadding, '0')}";
            }
        }

        var details = new List<string>();
        if (options.AddReleaseGroup)
        {
            details.Add(input.ReleaseGroup is { } releaseGroup
                ? !string.IsNullOrEmpty(releaseGroup.ShortName)
                    ? releaseGroup.ShortName
                    : !string.IsNullOrEmpty(releaseGroup.Name)
                        ? releaseGroup.Name
                        : $"Release group {releaseGroup.Id}"
                : "No Group");
        }
        if (options.AddResolution && !string.IsNullOrEmpty(input.Resolution))
            details.Add(input.Resolution);

        string extraDetails = details.Count is 0
            ? ""
            : $"[{string.Join("] [", details.Select(Sanitize))}] ";
        string fileIdList = input.AdmittedFile.FileId.ToString();
        string partSuffix = "";
        if (!input.IsMovieLibrary && input.MultipartParts.Count > 0)
        {
            fileIdList = string.Join(",", input.MultipartParts.Select(part => part.FileId));
            var currentPart = input.MultipartParts.FirstOrDefault(part => part.FileId == input.AdmittedFile.FileId);
            if (currentPart is not null)
                partSuffix = $".pt{currentPart.PartIndex}";
        }

        string fileName = $"{episodeName} {extraDetails}[Shoko Series={input.AdmittedFile.ShokoSeriesId}] [Shoko File={fileIdList}]{partSuffix}{input.SourceExtension}";
        DateTimeOffset lastModified = input.ImportedAt ?? input.CreatedAt;
        foreach (var folder in folders)
        {
            string path = JoinVirtualPath(folder, fileName);
            AddParentDirectories(path, lastModified, directories, directoryTimes);
            if (!emittedPaths.Add(path))
                continue;

            symlinks.Add(new VirtualTreeEntryData(
                path,
                VirtualNodeType.Symlink,
                SymlinkTarget: input.AdmittedFile.SourcePath,
                Mode: VirtualEntry.DefaultMode(VirtualNodeType.Symlink),
                LastModified: lastModified
            ));
        }
    }

    private static IReadOnlyList<string>? GetExtraFolders(FinProjectionInput input, FinProjectorOptions options)
    {
        bool isExtra = input.Episode.IsExtra
            || options.MovieSpecialsAsExtraFeaturettes && input.IsMovieLibrary && input.Episode.IsSpecial;
        return input.ExtraType switch
        {
            null => isExtra ? ["extras"] : null,
            FinExtraType.ThemeSong => ["theme-music"],
            FinExtraType.ThemeVideo => options.AddCreditsAsThemeVideos && options.AddCreditsAsSpecialFeatures
                ? ["backdrops", "extras"]
                : options.AddCreditsAsThemeVideos
                ? ["backdrops"]
                : options.AddCreditsAsSpecialFeatures
                ? ["extras"]
                : [],
            FinExtraType.Trailer => options.AddTrailers ? ["trailers"] : [],
            FinExtraType.BehindTheScenes => ["behind the scenes"],
            FinExtraType.DeletedScene => ["deleted scenes"],
            FinExtraType.Clip => ["clips"],
            FinExtraType.Interview => ["interviews"],
            FinExtraType.Scene => ["scenes"],
            FinExtraType.Sample => ["samples"],
            _ => ["extras"],
        };
    }

    private static void AddParentDirectories(
        string filePath,
        DateTimeOffset timestamp,
        List<string> order,
        Dictionary<string, DateTimeOffset> timestamps)
    {
        var segments = filePath.Split('/');
        for (int count = 1; count < segments.Length; count++)
        {
            string directory = string.Join('/', segments, 0, count);
            if (!timestamps.TryGetValue(directory, out var existing))
            {
                order.Add(directory);
                timestamps.Add(directory, timestamp);
            }
            else if (timestamp < existing)
            {
                timestamps[directory] = timestamp;
            }
        }
    }

    private static string JoinVirtualPath(params string[] parts) =>
        string.Join('/', parts.Select((part, index) => index == 0 ? part.TrimEnd('/') : part.Trim('/')));

    /// <summary>
    /// D4: label for a non-Episode type in the "&lt;Type&gt; &lt;number&gt;"
    /// episode-name fallback, mirroring Shokofin's AniDB EpisodeType names
    /// (VirtualFileSystemService.cs:1059 $"{episode.Type} {episodeNumber}").
    /// Open/Ending credits render as "Credits" like AniDB's single Credits type.
    /// </summary>
    private static string CanonicalEpisodeTypeLabel(FinEpisodeType type) => type switch
    {
        FinEpisodeType.Special => "Special",
        FinEpisodeType.Trailer => "Trailer",
        FinEpisodeType.Credits => "Credits",
        FinEpisodeType.OpeningSong => "Credits",
        FinEpisodeType.EndingSong => "Credits",
        FinEpisodeType.Parody => "Parody",
        FinEpisodeType.Other => "Other",
        _ => type.ToString(),
    };

    private static string Sanitize(string value)
    {
        var ascii = new string(value.Select(character => IsAllowed(character) ? character : '_').ToArray());
        return s_whitespaceRegex.Replace(s_underscoreRegex.Replace(ascii, "_"), " ").Trim();
    }

    private static string TruncateName(string value) => value.Length >= NameCutOff
        ? string.Join(' ', value[..NameCutOff].Split(' ').SkipLast(1)) + "…"
        : value;

    private static bool IsAllowed(char character) =>
        character == 32
        || character is >= '0' and <= '9'
        || character is >= 'A' and <= 'Z'
        || character is >= 'a' and <= 'z';
}
