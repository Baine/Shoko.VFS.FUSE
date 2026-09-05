using System.Text.RegularExpressions;

namespace Shoko.VFS.FUSE.Naming;

/// <summary>Relay (Plex) naming strategy. Matches ShokoRelay's VFS conventions exactly.</summary>
public sealed class RelayNamingStrategy : IPathNamingStrategy
{
    private const string FallbackExtraSubtype = "featurette";

    private static readonly IReadOnlyDictionary<int, (string Folder, string Subtype)> s_extraSeasons =
        new Dictionary<int, (string Folder, string Subtype)>
        {
            [-1] = ("Shorts", "short"),
            [-2] = ("Trailers", "trailer"),
            [-3] = ("Scenes", "sceneOrSample"),
            [-4] = ("Featurettes", "featurette"),
            [-9] = ("Other", "other"),
        };

    private static readonly IReadOnlyDictionary<string, string> s_extraTypePrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["short"] = "C",
            ["trailer"] = "T",
            ["sceneOrSample"] = "P",
            ["featurette"] = "O",
            ["other"] = "U",
        };

    private static readonly Regex s_quotedTextRegex = new("\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex s_whitespaceRegex = new(@"((?![\u200A\u200B])\s)+", RegexOptions.Compiled);
    private static readonly Regex s_condenseSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly (string Find, string Replace)[] s_styledReplacements =
    [
        ("1/2", "½"),
        ("1/6", "⅙"),
        ("-->", "→"),
        ("<--", "←"),
        ("->", "→"),
        ("<-", "←"),
    ];

    private static readonly IReadOnlyDictionary<char, char> s_replacementCharMap =
        new Dictionary<char, char>
        {
            ['\\'] = '⧵',
            ['/'] = '⁄',
            [':'] = '꞉',
            ['*'] = '＊',
            ['?'] = '？',
            ['<'] = '＜',
            ['>'] = '＞',
            ['|'] = '｜',
        };

    private static readonly char[] s_invalidFileNameChars = Path.GetInvalidFileNameChars();

    public string Consumer => "relay";

    public string? TvRootFolderName => null;

    public string? MovieRootFolderName => null;

    public bool IsTvExtraSeason(int season) => s_extraSeasons.ContainsKey(season);

    public RelayNamingStrategy() { }

    public string FormatSeriesFolder(int seriesId, string? title) => seriesId.ToString();

    public string FormatSeasonFolder(int seasonNumber) => s_extraSeasons.TryGetValue(seasonNumber, out var extra)
        ? extra.Folder
        : seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}";

    public string FormatEpisodeFileName(EpisodeFileContext ctx)
    {
        string episodeFormat = $"D{ctx.EpisodePad}";
        string name = ctx.EndEpisode.HasValue && ctx.EndEpisode != ctx.Episode
            ? $"S{ctx.Season:D2}E{ctx.Episode.ToString(episodeFormat)}-E{ctx.EndEpisode.Value.ToString(episodeFormat)}"
            : $"S{ctx.Season:D2}E{ctx.Episode.ToString(episodeFormat)}";

        return FormatFileName(name, ctx.FileId, ctx.Extension, ctx.OmitFileId, ctx.PartIndex, ctx.PartCount, ctx.VersionIndex, ctx.IsVariation);
    }

    public string FormatMovieFileName(MovieFileContext ctx) =>
        FormatFileName("Movie", ctx.FileId, ctx.Extension, ctx.OmitFileId, ctx.PartIndex, ctx.PartCount, ctx.VersionIndex, ctx.IsVariation);

    public string FormatMovieFolder(int episodeId) => episodeId.ToString();

    public string FormatExtrasFolder(int season) => s_extraSeasons.TryGetValue(season, out var extra) ? extra.Folder : "Featurettes";

    public string FormatExtrasFileName(ExtrasFileContext ctx)
    {
        string subtype = s_extraSeasons.TryGetValue(ctx.Season, out var extra) ? extra.Subtype : FallbackExtraSubtype;
        string prefix = s_extraTypePrefixes.TryGetValue(subtype, out var value) ? value : "";
        string episode = prefix + ctx.Episode.ToString($"D{ctx.EpisodePad}");
        int partCount = ctx.PartCount.GetValueOrDefault();
        string part = partCount > 1
            ? $"-pt{ctx.PartIndex}"
            : ctx.VersionIndex.HasValue ? $"-dup{ctx.VersionIndex.Value}" : "";

        string name = $"{episode}{part} ❯ {CleanEpisodeTitleForFilename(ctx.Title)}";
        if (ctx.IsVariation && partCount <= 1)
            name += "[variation]";

        return SanitizeName(name + ctx.Extension);
    }

    private static string FormatFileName(
        string baseName,
        int fileId,
        string extension,
        bool omitFileId,
        int? partIndex,
        int? partCount,
        int? versionIndex,
        bool isVariation)
    {
        int totalParts = partCount.GetValueOrDefault();
        string name = baseName;
        if (totalParts > 1)
            name += $"-pt{partIndex}";
        else if (versionIndex.HasValue)
            name += $"-dup{versionIndex.Value}";

        if (!omitFileId)
            name += $" [{fileId}]";
        if (isVariation && totalParts <= 1)
            name += "[variation]";

        return name + extension;
    }

    private static string CleanEpisodeTitleForFilename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        string cleaned = title;
        foreach (var (find, replace) in s_styledReplacements)
            cleaned = cleaned.Replace(find, replace, StringComparison.Ordinal);
        cleaned = s_quotedTextRegex.Replace(cleaned, "“$1”");

        const string Zwsp = "\u200B", Hsp = "\u200A";
        if (cleaned.StartsWith("Opening", StringComparison.OrdinalIgnoreCase))
            cleaned = $"{Hsp}O{Zwsp}pening{cleaned[7..]}";
        else if (cleaned.StartsWith("Ending", StringComparison.OrdinalIgnoreCase))
            cleaned = $"E{Zwsp}nding{cleaned[6..]}";

        foreach (var (from, to) in s_replacementCharMap)
            cleaned = cleaned.Replace(from, to);

        return s_whitespaceRegex.Replace(cleaned, " ").Trim(' ');
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown";

        string processed = name;
        if (name.IndexOfAny(s_invalidFileNameChars) >= 0)
        {
            processed = string.Create(
                name.Length,
                name,
                (chars, state) =>
                {
                    for (int i = 0; i < state.Length; i++)
                        chars[i] = Array.IndexOf(s_invalidFileNameChars, state[i]) >= 0 ? ' ' : state[i];
                }
            );
        }

        string cleaned = s_condenseSpacesRegex.Replace(processed, " ").Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }
}
