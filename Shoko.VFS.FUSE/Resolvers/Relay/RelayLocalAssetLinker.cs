using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Shoko.VFS.FUSE.Naming;

namespace Shoko.VFS.FUSE.Resolvers.Relay;

/// <summary>
/// Discovery-time mirror of ShokoRelay's <c>VfsAssetLinker</c> (ShokoRelay/Vfs/VfsAssetLinker.cs)
/// plus the series-level parts of <c>VfsBuilder</c> that scan the physical source folders.
/// Both relay datasources (in-process plugin and host REST) run <see cref="Discover"/> in their
/// per-series pass — the cheap structure passes never touch the filesystem beyond the existing
/// path resolution — and the resolver then places the results with the active naming strategy so
/// sidecar basenames always pair with their video entries.
/// Directory results are cached per source folder (one enumeration each, mirroring the
/// <c>FindThemeFile</c> probe-once pattern) so repeated mappings in the same folder don't re-stat.
/// </summary>
internal static class RelayLocalAssetLinker
{
    private static readonly RelayNamingStrategy s_naming = new();

    // PlexConstants.LocalMediaAssets.Artwork.Keys (ShokoRelay/Plex/PlexConstants.cs:135-138).
    private static readonly HashSet<string> s_artworkExtensions = new(
        [".bmp", ".gif", ".jpe", ".jpeg", ".jpg", ".png", ".tbn", ".tif", ".tiff", ".webp"],
        StringComparer.OrdinalIgnoreCase);
    // PlexConstants.cs:141 — series-level theme audio + NFO metadata.
    private static readonly string[] s_seriesMetadataExtensions = [".mp3", ".nfo"];
    // PlexConstants.cs:144 — supported subtitle extensions.
    private static readonly HashSet<string> s_subtitleExtensions = new(
        [".ass", ".idx", ".smi", ".srt", ".ssa", ".sub", ".sup", ".vtt"],
        StringComparer.OrdinalIgnoreCase);
    // PlexConstants.cs:147 — subtitle modifiers skipped during language remapping.
    private static readonly HashSet<string> s_subtitleModifiers = new(
        ["cc", "default", "forced", "sdh"],
        StringComparer.OrdinalIgnoreCase);
    // s_seriesMetadataExtensions (VfsAssetLinker.cs:16-18): artwork ∪ series metadata.
    private static readonly HashSet<string> s_seriesAssetExtensions =
        Merge(s_artworkExtensions, s_seriesMetadataExtensions);
    // s_episodeMetadataExtensions (VfsAssetLinker.cs:21-23): artwork ∪ subtitles ∪ .nfo/.xml/.chp.
    private static readonly HashSet<string> s_episodeAssetExtensions =
        Merge(s_artworkExtensions, [.. s_subtitleExtensions, ".nfo", ".xml", ".chp"]);
    // PlexConstants.cs:153 — attachment folder suffixes.
    private static readonly string[] s_attachmentFolderSuffixes = ["_attach", "_attachments"];
    // PlexConstants.cs:110 — Plex show/season-level extra subdirectories.
    private static readonly string[] s_localExtraDirs = ["Behind The Scenes", "Deleted Scenes", "Featurettes", "Interviews", "Scenes", "Shorts", "Trailers", "Other"];
    // PlexConstants.cs:113 — Plex episode-level inline extra filename suffixes.
    private static readonly string[] s_localExtraSuffixes = ["-behindthescenes", "-deleted", "-featurette", "-interview", "-scene", "-short", "-trailer", "-other"];

    // VfsHelper.cs:22 — verbatim.
    private static readonly Regex s_localExtraDirRegex = new(
        $@"^({string.Join("|", s_localExtraDirs)})(\s+[sS](\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Enriches one merged group's <see cref="SeriesData"/> with every local asset upstream
    /// links from the group's source folders: episode sidecars/attachments (per mapping),
    /// series-level metadata into the TV + movie roots (per-mapping <c>SeriesAssets</c>),
    /// Plex local-extra subdirectories (TV root only) and AnimeThemes <c>Shorts/*.webm</c>.
    /// </summary>
    /// <param name="data">Projected (merged) series data with resolved source paths.</param>
    /// <param name="options">Relay datasource options (subtitle mappings, PlexLocalExtras, xref CSV path).</param>
    /// <param name="managedFolderRoot">Absolute managed-folder (import) root — upstream importRoot.</param>
    /// <param name="groupAnidbIds">AniDB ids of every member series (upstream OverrideHelper.GetGroup).</param>
    /// <param name="pathComparison">OS path comparison used by the caller's path caches.</param>
    /// <param name="isShokoManagedFile">True when a physical file is managed by Shoko with an episode cross-reference.</param>
    /// <param name="resolvePrimarySeriesId">Series id → consolidation-group primary (upstream OverrideHelper.GetPrimary).</param>
    internal static SeriesData Discover(
        SeriesData data,
        RelayPathDataSourceOptions options,
        string managedFolderRoot,
        IReadOnlyCollection<int> groupAnidbIds,
        StringComparison pathComparison,
        Func<string, bool> isShokoManagedFile,
        Func<int, int> resolvePrimarySeriesId)
    {
        var mappings = data.Mappings ?? Array.Empty<EpisodeData>();
        var videoBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceDirs = new List<string>();
        var seenDirs = new HashSet<string>(StringComparer.FromComparison(pathComparison));
        foreach (var mapping in mappings)
        {
            if (!IsSourced(mapping))
                continue;
            videoBases.Add(Path.GetFileNameWithoutExtension(mapping.SourcePath)!);
            var dir = Path.GetDirectoryName(mapping.SourcePath!)!;
            if (seenDirs.Add(dir))
                sourceDirs.Add(dir);
        }

        if (sourceDirs.Count == 0)
            return data;

        var probed = new Dictionary<string, ProbedDir>(StringComparer.FromComparison(pathComparison));
        var sidecars = new List<LocalAssetFile>?[mappings.Count];
        var inlineExtras = new List<LocalAssetFile>?[mappings.Count];
        var seriesAssets = new List<ExtraFile>?[mappings.Count];
        var suppressTv = new bool[mappings.Count];

        // Per-mapping: episode sidecars + attachments + that folder's series-level metadata.
        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            if (!IsSourced(mapping))
                continue;
            var dir = Path.GetDirectoryName(mapping.SourcePath!)!;
            var probe = Probe(dir, probed);
            suppressTv[i] = IsCrossover(mapping, resolvePrimarySeriesId);
            var found = DiscoverEpisodeAssets(mapping.SourcePath!, probe, options);
            if (found.Count > 0)
                sidecars[i] = found;
            var assets = DiscoverSeriesAssets(probe, videoBases);
            if (assets.Count > 0)
                seriesAssets[i] = assets;
        }

        var tvLocalExtras = new Dictionary<string, ExtraFile>(StringComparer.Ordinal);
        if (options.PlexLocalExtras)
        {
            // VfsAssetLinker.LinkLocalExtras (VfsAssetLinker.cs:277-341) — evaluated once per
            // distinct source dir; the file set broadcasts across the TV root and season dirs.
            foreach (var dir in sourceDirs)
            {
                var probe = Probe(dir, probed);
                DiscoverLocalExtraDirs(dir, probe, tvLocalExtras, isShokoManagedFile);
                DiscoverInlineExtras(mappings, probe, videoBases, isShokoManagedFile, inlineExtras);
            }
        }

        // AnimeThemes Shorts/*.webm (VfsBuilder.cs:704-733) — every resolved series/movie root.
        var shorts = new Dictionary<string, ExtraFile>(StringComparer.Ordinal);
        DiscoverShorts(shorts, options, managedFolderRoot, groupAnidbIds);

        var rebuilt = new List<EpisodeData>(mappings.Count);
        for (int i = 0; i < mappings.Count; i++)
            rebuilt.Add(mappings[i] with
            {
                Sidecars = sidecars[i],
                InlineLocalExtras = inlineExtras[i],
                SeriesAssets = seriesAssets[i],
                SuppressTvLocalAssets = suppressTv[i],
            });

        var extras = shorts.Count > 0 ? shorts.Values.ToArray() : data.Extras;
        var tvExtras = tvLocalExtras.Count > 0 ? tvLocalExtras.Values.ToArray() : data.TvLocalExtras;
        return data with { Mappings = rebuilt, Extras = extras, TvLocalExtras = tvExtras };
    }

    private static bool IsSourced(EpisodeData mapping) =>
        mapping.FileId != 0 && !string.IsNullOrWhiteSpace(mapping.SourcePath);

    /// <summary>Upstream gate: sidecars/series-assets are skipped for files whose episode
    /// cross-references resolve to more than one primary series (VfsBuilder.cs:490-516).</summary>
    private static bool IsCrossover(EpisodeData mapping, Func<int, int> resolvePrimarySeriesId) =>
        mapping.XrefSeriesIds is { Count: > 1 } ids
        && ids.Select(resolvePrimarySeriesId).Distinct().Count() > 1;

    /// <summary>
    /// VfsAssetLinker.LinkEpisodeMetadata (VfsAssetLinker.cs:73-172) + LinkAttachmentFolder
    /// (186-233): every episode-metadata file whose name starts with the source video's base
    /// name (next char non-alphanumeric) links as <c>&lt;destBase&gt;&lt;suffix&gt;</c>; the
    /// suffix passes through the subtitle language remapping when mappings are configured.
    /// </summary>
    private static List<LocalAssetFile> DiscoverEpisodeAssets(
        string sourceFile,
        ProbedDir probe,
        RelayPathDataSourceOptions options)
    {
        var result = new List<LocalAssetFile>();
        string originalBase = Path.GetFileNameWithoutExtension(sourceFile);
        var mappings = options.SubtitleLanguageMappings;

        // Fast path for unmapped sidecars (VfsAssetLinker.cs:95-119): suffix passthrough in
        // enumeration order, no target checks/sorting — upstream's default (empty mappings).
        if (mappings is not { Count: > 0 })
        {
            foreach (var file in probe.Files)
            {
                string name = Path.GetFileName(file);
                if (!s_episodeAssetExtensions.Contains(Path.GetExtension(name)))
                    continue;
                if (!MatchesSidecar(name, originalBase))
                    continue;
                result.Add(new LocalAssetFile(name[originalBase.Length..], file, GetSize(file)));
            }
        }
        else
        {
            var pending = new List<(string Source, string Name, int Priority)>();
            foreach (var file in probe.Files)
            {
                string name = Path.GetFileName(file);
                if (!s_episodeAssetExtensions.Contains(Path.GetExtension(name)))
                    continue;
                if (!MatchesSidecar(name, originalBase))
                    continue;
                string ext = Path.GetExtension(file);
                // Exclude unavailable subtitle sources before they can win a collision (line 131).
                if (s_subtitleExtensions.Contains(ext) && !File.Exists(file))
                    continue;
                string suffix = name[originalBase.Length..];
                var (mappedSuffix, priority) = s_subtitleExtensions.Contains(ext)
                    ? RenameSubtitleSuffix(suffix, mappings)
                    : (suffix, -1);
                pending.Add((file, mappedSuffix, priority));
            }

            // Unchanged originals (priority -1) precede conversions; converted collisions keep
            // the earliest link (upstream skips them via linkedNames — AddFile is first-wins).
            var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (source, suffix, priority) in pending
                         .OrderBy(entry => entry.Priority)
                         .ThenBy(entry => entry.Source, StringComparer.Ordinal))
            {
                if (priority >= 0 && linked.Contains(suffix))
                    continue;
                // ponytail: upstream's keep-original-suffix fallback (line 150-155) only fires
                // when the physical symlink creation fails; a virtual tree never fails here.
                if (linked.Add(suffix))
                    result.Add(new LocalAssetFile(suffix, source, GetSize(source)));
            }
        }

        // Attachment folders (VfsAssetLinker.cs:186-233): first subdir matching
        // <originalBase><_attach|_attachments>; contents mirror to <destBase>_attach recursively.
        string? attachSource = probe.Directories.FirstOrDefault(dir =>
            Path.GetFileName(dir) is var dirName
            && dirName.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase)
            && Array.Exists(s_attachmentFolderSuffixes, suffix =>
                string.Equals(dirName[originalBase.Length..], suffix, StringComparison.OrdinalIgnoreCase)));
        if (attachSource is not null)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(attachSource, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(attachSource, file).Replace('\\', '/');
                    result.Add(new LocalAssetFile("_attach/" + rel, file, GetSize(file)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Upstream would abort the mapping; we just drop what was unreadable.
            }
        }

        return result;
    }

    // VfsAssetLinker.cs:101 — StartsWith + the next character must not be alphanumeric.
    private static bool MatchesSidecar(string name, string originalBase) =>
        name.StartsWith(originalBase, StringComparison.OrdinalIgnoreCase)
        && (name.Length == originalBase.Length || !char.IsLetterOrDigit(name[originalBase.Length]));

    /// <summary>
    /// VfsAssetLinker.RenameSubtitleSuffix (VfsAssetLinker.cs:239-268): replaces language
    /// tokens once (never the extension, never modifiers), rejecting unsafe replacements;
    /// priority is the earliest mapping used, -1 when nothing changed.
    /// </summary>
    private static (string Suffix, int Priority) RenameSubtitleSuffix(
        string suffix,
        IReadOnlyList<SubtitleLanguageMapping> mappings)
    {
        var parts = suffix.Split('.');
        int priority = int.MaxValue;
        bool modified = false;

        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (s_subtitleModifiers.Contains(parts[i]))
                continue;

            for (int j = 0; j < mappings.Count; j++)
            {
                var mapping = mappings[j];
                if (!parts[i].Equals(mapping.Token?.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                string? replacement = mapping.Replacement?.Trim();
                if (!string.IsNullOrEmpty(replacement)
                    && !replacement.Contains('.')
                    && RelayNamingStrategy.SanitizeName(replacement) == replacement)
                {
                    parts[i] = replacement;
                    priority = Math.Min(priority, j);
                    modified = true;
                }
                break;
            }
        }

        return modified ? (string.Join('.', parts), priority) : (suffix, -1);
    }

    /// <summary>
    /// VfsAssetLinker.LinkSeriesMetadata (VfsAssetLinker.cs:36-58): artwork + .mp3/.nfo files
    /// that are not episode sidecars (base not among the video base names); a file based
    /// "Specials" renames to <c>Season-Specials-Poster</c> (upstream expects Specials.jpg in
    /// season folders and links it where Plex reads season artwork at the series root).
    /// </summary>
    private static List<ExtraFile> DiscoverSeriesAssets(ProbedDir probe, HashSet<string> videoBases)
    {
        var result = new List<ExtraFile>();
        foreach (var file in probe.Files)
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(name);
            if (!s_seriesAssetExtensions.Contains(ext))
                continue;
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (videoBases.Contains(baseName))
                continue;
            string destName = baseName.Equals("Specials", StringComparison.OrdinalIgnoreCase)
                ? "Season-Specials-Poster" + ext
                : name;
            result.Add(new ExtraFile(destName, file, GetSize(file)));
        }

        return result;
    }

    /// <summary>
    /// VfsAssetLinker.LinkLocalExtras show/season-level half (VfsAssetLinker.cs:289-315):
    /// unmanaged videos in <c>Trailers</c>/<c>Interviews S2</c> style subdirectories mirror to
    /// <c>&lt;seriesRoot&gt;/[Season N/]&lt;PlexDir&gt;/&lt;file&gt;</c> (names preserved).
    /// </summary>
    private static void DiscoverLocalExtraDirs(
        string sourceDir,
        ProbedDir probe,
        Dictionary<string, ExtraFile> tvExtras,
        Func<string, bool> isShokoManagedFile)
    {
        foreach (var subDir in probe.Directories)
        {
            var match = s_localExtraDirRegex.Match(Path.GetFileName(subDir));
            if (!match.Success)
                continue;

            string type = match.Groups[1].Value;
            string seasonNum = match.Groups[3].Value;
            string plexDirName = s_localExtraDirs.First(dir =>
                string.Equals(dir, type, StringComparison.OrdinalIgnoreCase));
            // Upstream: VfsHelper.SanitizeName(PlexMapping.GetSeasonFolder(n)) — the relay
            // format already yields sanitized "Season N"/"Specials" names.
            string seasonFolder = string.IsNullOrEmpty(seasonNum)
                ? ""
                : s_naming.FormatSeasonFolder(int.Parse(seasonNum));

            string[] files;
            try
            {
                files = Directory.GetFiles(subDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!RelayIgnoreRules.IsAllowedVideoExtension(file) || isShokoManagedFile(file))
                    continue;
                string destRel = (seasonFolder.Length > 0 ? seasonFolder + "/" : "")
                    + plexDirName + "/" + Path.GetFileName(file);
                // Later source dirs overwrite earlier ones (upstream TryCreateLink replaces).
                tvExtras[destRel] = new ExtraFile(destRel, file, GetSize(file));
            }
        }
    }

    /// <summary>
    /// VfsAssetLinker.LinkLocalExtras episode-level inline half (VfsAssetLinker.cs:317-339):
    /// unmanaged videos named <c>&lt;parentBase&gt;&lt;-trailer|...&gt;</c> beside a known
    /// parent base mirror next to the parent mapping with that parent's VFS base name.
    /// The files are never Shoko mappings themselves (upstream IsPathIgnored drops them —
    /// mirrored by IsPathExcluded/RelayIgnoreRules), so here they resurface as extra entries.
    /// </summary>
    private static void DiscoverInlineExtras(
        IReadOnlyList<EpisodeData> mappings,
        ProbedDir probe,
        HashSet<string> videoBases,
        Func<string, bool> isShokoManagedFile,
        List<LocalAssetFile>?[] inlineExtras)
    {
        foreach (var file in probe.Files)
        {
            if (!RelayIgnoreRules.IsAllowedVideoExtension(file))
                continue;
            string name = Path.GetFileNameWithoutExtension(file);
            if (videoBases.Contains(name) || isShokoManagedFile(file))
                continue;

            // Upstream splits on the suffix (ordinal) and keeps the longest head that is a
            // known video base name. ponytail: mirrors upstream's exact quirk — a head with
            // trailing whitespace only matches when the source filename is spelled that way.
            string parentBase = s_localExtraSuffixes
                .Select(suffix => name.Split(suffix)[0])
                .OrderByDescending(head => head.Length)
                .FirstOrDefault(videoBases.Contains) ?? string.Empty;
            if (parentBase.Length == 0)
                continue;

            string destSuffix = name[parentBase.Length..] + Path.GetExtension(file);
            long size = GetSize(file);
            for (int i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i];
                if (!IsSourced(mapping))
                    continue;
                if (!Path.GetFileNameWithoutExtension(mapping.SourcePath!)
                        .Equals(parentBase, StringComparison.OrdinalIgnoreCase))
                    continue;
                (inlineExtras[i] ??= []).Add(new LocalAssetFile(destSuffix, file, size));
            }
        }
    }

    /// <summary>
    /// VfsBuilder.cs:704-733 — AnimeThemes <c>Shorts/*.webm</c>. Upstream registers every
    /// theme of the group's AniDB ids whose file physically exists under
    /// <c>&lt;importRoot&gt;/!AnimeThemes/</c>, mirroring <c>Shorts/&lt;FinalName&gt;</c> into
    /// the TV series folder and each movie folder alike. The per-series attribution lives in
    /// ShokoRelay's <c>anidb_animethemes_xrefs.csv</c> (plugin config, invisible to REST),
    /// so discovery only runs when <see cref="RelayPathDataSourceOptions.AnimeThemesXrefCsvPath"/>
    /// points at that CSV.
    /// </summary>
    private static void DiscoverShorts(
        Dictionary<string, ExtraFile> shorts,
        RelayPathDataSourceOptions options,
        string managedFolderRoot,
        IReadOnlyCollection<int> groupAnidbIds)
    {
        if (string.IsNullOrWhiteSpace(options.AnimeThemesXrefCsvPath) || groupAnidbIds.Count == 0)
            return;

        var themeMappings = LoadThemeMappings(options.AnimeThemesXrefCsvPath);
        if (themeMappings.Count == 0)
            return;

        Dictionary<string, string>? themeIndex = null;
        foreach (var anidbId in groupAnidbIds)
        {
            if (!themeMappings.TryGetValue(anidbId, out var themes))
                continue;

            foreach (var theme in themes)
            {
                // GetThemeSourcePath (VfsHelper.cs:363-388): cache the webm enumeration per
                // theme root — probed once per folder, like FindThemeFile's probedFolders.
                themeIndex ??= BuildThemeIndex(
                    Path.Combine(managedFolderRoot, RelayIgnoreRules.AnimeThemesRootName));
                string rel = theme.RelativePath.Replace('\\', '/').TrimStart('/');
                if (!themeIndex.TryGetValue(rel, out string? source))
                    continue;
                string destName = "Shorts/" + theme.FinalName;
                shorts[destName] = new ExtraFile(destName, source, GetSize(source));
            }
        }
    }

    private static Dictionary<string, string> BuildThemeIndex(string themeRoot)
    {
        // ponytail: theme file relative paths are matched case-insensitively like upstream's
        // OrdinalIgnoreCase cache (VfsHelper.cs:369); the OS path case rule only affects the
        // root directory itself, which comes from the datasource verbatim.
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(themeRoot))
                foreach (var file in Directory.EnumerateFiles(themeRoot, "*.webm", SearchOption.AllDirectories))
                    index[Path.GetRelativePath(themeRoot, file).Replace('\\', '/').TrimStart('/')] = Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return index;
    }

    #region AnimeThemes xref CSV (mirrors AnimeThemesMapping.LoadThemeMappings + AnimeThemesHelper)

    private sealed record ThemeMapItem(string FinalName, string RelativePath);

    private sealed record ThemeEntry(
        bool NC,
        string Slug,
        int Version,
        string ArtistName,
        string SongTitle,
        bool Lyrics,
        bool Subbed,
        bool Uncen,
        bool NSFW,
        bool Spoiler,
        string Overlap);

    // ponytail: cache the parsed CSV per path+write-time; ShokoRelay reloads it per build
    // session, and our per-series pass must not re-read a multi-MB file per series.
    private static readonly ConcurrentDictionary<string, (DateTime UpdatedAt, Dictionary<int, List<ThemeMapItem>> ByAnidbId)> s_themeMappingCache = new(StringComparer.Ordinal);

    private static Dictionary<int, List<ThemeMapItem>> LoadThemeMappings(string csvPath)
    {
        DateTime updated;
        string content;
        try
        {
            updated = File.GetLastWriteTimeUtc(csvPath);
            content = File.ReadAllText(csvPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<int, List<ThemeMapItem>>();
        }

        if (s_themeMappingCache.TryGetValue(csvPath, out var cached) && cached.UpdatedAt == updated)
            return cached.ByAnidbId;

        var mappings = ParseThemeMappings(content);
        s_themeMappingCache[csvPath] = (updated, mappings);
        return mappings;
    }

    /// <summary>
    /// AnimeThemesHelper.ParseMappingContentWithComments (AnimeThemesHelper.cs:226-280) +
    /// AnimeThemesMapping.LoadThemeMappings (AnimeThemesMapping.cs:28-49): every entry becomes
    /// a <c>ThemeMapItem(BuildNewFileName(lookup, ".webm"), filePath)</c> grouped by AniDB id.
    /// </summary>
    private static Dictionary<int, List<ThemeMapItem>> ParseThemeMappings(string content)
    {
        var mappings = new Dictionary<int, List<ThemeMapItem>>();
        foreach (var raw in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var f = line.Split(',');
            if (f.Length < 17 || !int.TryParse(f[1], out _) || !int.TryParse(f[2], out int aid) || !int.TryParse(f[5], out int ver))
                continue;

            var entry = new ThemeEntry(
                NC: f[3] == "1",
                Slug: f[4],
                Version: ver,
                ArtistName: UnescapeUnicode(f[7]),
                SongTitle: UnescapeUnicode(f[6]),
                Lyrics: f[8] == "1",
                Subbed: f[9] == "1",
                Uncen: f[10] == "1",
                NSFW: f[11] == "1",
                Spoiler: f[12] == "1",
                Overlap: f[16]);
            if (!mappings.TryGetValue(aid, out var list))
                mappings[aid] = list = [];
            list.Add(new ThemeMapItem(BuildThemeFileName(entry), f[0].TrimStart('/', '\\')));
        }

        return mappings;
    }

    /// <summary>
    /// AnimeThemesHelper.BuildNewFileName (AnimeThemesHelper.cs:405-453) with overrideIndex 0
    /// and AnimeThemesAppendTags=true (upstream default) — the LoadThemeMappings shape used by
    /// VfsBuilder's Shorts registration.
    /// </summary>
    private static string BuildThemeFileName(ThemeEntry lookup)
    {
        var (baseSlug, slugSuffix) = ParseSlug(lookup.Slug ?? "");
        string nc = lookup.NC ? "NC" : "";
        string slug = string.IsNullOrWhiteSpace(baseSlug) ? "Theme" : baseSlug;

        const string Zwsp = "\u200B",
            Hsp = "\u200A";

        // Hsp/Zwsp defeat Plex stripping OP/ED prefixes and keep Openings sorted first.
        if (slug.StartsWith("OP", StringComparison.OrdinalIgnoreCase))
            slug = $"{Hsp}O{Zwsp}P{slug[2..]}";
        else if (slug.StartsWith("ED", StringComparison.OrdinalIgnoreCase))
            slug = $"E{Zwsp}D{slug[2..]}";

        string ver = lookup.Version > 1 ? $"v{lookup.Version}" : "";
        string title = string.IsNullOrWhiteSpace(lookup.SongTitle) ? "" : " ❯ " + lookup.SongTitle;
        string slugTag = FormatSlugTag(slugSuffix);

        var artistList = !string.IsNullOrWhiteSpace(lookup.ArtistName)
            ? lookup.ArtistName.Split(" / ", StringSplitOptions.RemoveEmptyEntries)
            : [];
        string artistDisplay = artistList.Length switch
        {
            >= 4 => "Various Artists",
            3 => $"{artistList[0]}, {artistList[1]} & {artistList[2]}",
            2 => $"{artistList[0]} & {artistList[1]}",
            1 => artistList[0],
            _ => "",
        };
        string artistStr = string.IsNullOrWhiteSpace(artistDisplay) ? "" : " ❯ " + artistDisplay;

        var attr = new List<string>();
        if (lookup.Lyrics)
            attr.Add("LYRICS");
        if (lookup.Subbed)
            attr.Add("SUBS");
        if (lookup.Uncen)
            attr.Add("UNCEN");
        if (lookup.NSFW)
            attr.Add("NSFW");
        if (lookup.Spoiler)
            attr.Add("SPOIL");
        if (lookup.Overlap == "Transition")
            attr.Add("TRANS");
        else if (lookup.Overlap == "Over")
            attr.Add("OVER");

        string attrStr = attr.Count > 0 ? $" [{string.Join(", ", attr)}]" : "";
        return RelayNamingStrategy.CleanEpisodeTitleForFilename($"{nc}{slug}{ver}{title}{slugTag}{artistStr}{attrStr}.webm");
    }

    // AnimeThemesHelper.ParseSlug (AnimeThemesHelper.cs:468-477).
    private static (string BaseSlug, string? Suffix) ParseSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return ("", null);
        int dash = slug.IndexOf('-');
        string b = dash < 0 ? slug : slug[..dash];
        if (b.Equals("OP1", StringComparison.OrdinalIgnoreCase) || b.Equals("ED1", StringComparison.OrdinalIgnoreCase))
            b = b[..2];
        return (b, dash < 0 ? null : slug[(dash + 1)..]);
    }

    // AnimeThemesHelper.s_slugFormatting (AnimeThemesHelper.cs:143-169).
    private static readonly Dictionary<string, string> s_slugFormatting = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Animax", "Animax" },
        { "ATX", "AT-X" },
        { "BD", "Blu-ray" },
        { "BestSelection", "Best Selection" },
        { "EN", "English" },
        { "HD", "High Definition" },
        { "KO", "Korean" },
        { "NoitaminA", "noitaminA" },
        { "ORIGINAL", "Original" },
        { "Oversea", "Overseas" },
        { "Plus", "Plus" },
        { "RepeatShow", "Repeat Show" },
        { "ShounenHen", "Shounen-hen" },
        { "Sound", "Sound" },
        { "Theatrical2", "Theatrical 2" },
        { "Theatrical3", "Theatrical 3" },
        { "Theatrical4", "Theatrical 4" },
        { "Theatrical5", "Theatrical 5" },
        { "Theatrical6", "Theatrical 6" },
        { "Theatrical7", "Theatrical 7" },
        { "Theatrical8", "Theatrical 8" },
        { "TV", "Broadcast" },
        { "WEB", "Web" },
        { "YorinukiGintamaSan", "Yorinuki Gintama-san" },
    };

    // AnimeThemesHelper.FormatSlugTag (AnimeThemesHelper.cs:482).
    private static string FormatSlugTag(string? suffix) =>
        string.IsNullOrWhiteSpace(suffix)
            ? ""
            : $" ({(s_slugFormatting.TryGetValue(suffix.Trim(), out var formatted) ? formatted : suffix)})";

    // TextHelper.UnescapeUnicode (TextHelper.cs:69-70).
    private static readonly Regex s_unicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    private static string UnescapeUnicode(string value) =>
        string.IsNullOrEmpty(value) || !value.Contains(@"\u", StringComparison.Ordinal)
            ? value
            : s_unicodeEscapeRegex.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    #endregion

    #region Directory probing

    private sealed record ProbedDir(string Path, string[] Files, string[] Directories);

    private static ProbedDir Probe(string dir, Dictionary<string, ProbedDir> cache)
    {
        if (cache.TryGetValue(dir, out var hit))
            return hit;

        string[] files = [];
        string[] directories = [];
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            directories = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var probe = new ProbedDir(dir, files, directories);
        cache[dir] = probe;
        return probe;
    }

    private static long GetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static HashSet<string> Merge(IEnumerable<string> first, IEnumerable<string> second)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in first)
            set.Add(value);
        foreach (var value in second)
            set.Add(value);
        return set;
    }

    #endregion
}
