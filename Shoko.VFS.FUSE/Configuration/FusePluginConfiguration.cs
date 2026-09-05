using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Attributes;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Configuration;

/// <summary>
/// Top-level plugin configuration. Maps to IConfiguration in Shoko Server.
/// </summary>
[StorageLocation(FileName = "Shoko.VFS.FUSE.json")]
public sealed class FusePluginConfiguration : IConfiguration, IConfigurationWithCustomValidation<FusePluginConfiguration>
{
    /// <summary>Enable the relay (Plex) mount.</summary>
    public bool RelayEnabled { get; set; }

    /// <summary>Enable the fin (Jellyfin) mount.</summary>
    public bool FinEnabled { get; set; }

    // -- Relay configuration --

    /// <summary>
    /// Movie generation mode. Matches relay's MovieGenerationMode.
    /// 0 = Disabled, 1 = EnabledMaintain, 2 = EnabledRemove.
    /// </summary>
    public MovieGenerationMode MovieGenerationMode { get; set; } = MovieGenerationMode.Disabled;

    /// <summary>Folder name for the TV VFS root (inside each import folder). Default: "!ShokoRelayVFS".</summary>
    public string RelayTvFolderName { get; set; } = "!ShokoRelayVFS";

    /// <summary>Folder name for the movie VFS root (inside each import folder). Default: "!ShokoRelayMovieVFS".</summary>
    public string RelayMovieFolderName { get; set; } = "!ShokoRelayMovieVFS";

    /// <summary>Whether Relay uses TMDB episode numbering.</summary>
    public bool TmdbEpNumbering { get; set; } = true;

    /// <summary>Whether Relay merges series linked to the same TMDB show.</summary>
    public bool MergeTmdbSeries { get; set; }

    /// <summary>Whether Relay applies Plex local-extra exclusions to indexed files.</summary>
    public bool PlexLocalExtras { get; set; } = true;

    /// <summary>Newline-separated source path segments excluded from Relay indexing.</summary>
    public string FolderExclusions { get; set; } = "";

    /// <summary>Newline-separated managed-folder IDs or names excluded from Relay indexing.</summary>
    public string ManagedFolderExclusions { get; set; } = "";

    /// <summary>Comma-separated Relay series title language preference.</summary>
    public string SeriesTitleLanguage { get; set; } = "SHOKO";

    /// <summary>Comma-separated Relay episode title language preference.</summary>
    public string EpisodeTitleLanguage { get; set; } = "SHOKO";

    /// <summary>Whether common Relay series-title prefixes move to the end.</summary>
    public bool MoveCommonSeriesTitlePrefixes { get; set; } = true;

    /// <summary>Whether TMDB group names are preferred for multi-link episodes.</summary>
    public bool TmdbEpGroupNames { get; set; } = true;

    // -- Fin configuration --

    /// <summary>Mount point for the fin (Jellyfin) VFS. Default: "{ShokoDataPath}/VFS-Fuse".</summary>
    public string FinMountPoint { get; set; } = "";

    // -- Shared configuration --

    /// <summary>How to serve file reads.</summary>
    public ReadMode ReadMode { get; set; } = ReadMode.DirectRead;

    /// <summary>FUSE allow_other option.</summary>
    public bool FuseAllowOther { get; set; }

    /// <summary>
    /// UID reported by FUSE for files on the mount. Default: <c>99</c> (Unraid's
    /// <c>nobody</c>), which matches what Unraid shows for files in its own shares.
    /// </summary>
    public uint FuseMountUid { get; set; } = 99;

    /// <summary>
    /// GID reported by FUSE for files on the mount. Default: <c>100</c> (Unraid's
    /// <c>users</c> group).
    /// </summary>
    public uint FuseMountGid { get; set; } = 100;

    /// <summary>Attribute cache timeout in seconds.</summary>
    public double AttrTimeout { get; set; } = 2.0;

    /// <summary>Entry cache timeout in seconds.</summary>
    public double EntryTimeout { get; set; } = 2.0;

    /// <summary>Negative entry cache timeout in seconds.</summary>
    public double NegativeTimeout { get; set; } = 0.5;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(
        FusePluginConfiguration config,
        IConfigurationService configurationService,
        IPluginManager pluginManager)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>();
        if (!double.IsFinite(config.AttrTimeout) || config.AttrTimeout < 0)
            errors[nameof(AttrTimeout)] = ["AttrTimeout must be finite and greater than or equal to zero."];
        if (!double.IsFinite(config.EntryTimeout) || config.EntryTimeout < 0)
            errors[nameof(EntryTimeout)] = ["EntryTimeout must be finite and greater than or equal to zero."];
        if (!double.IsFinite(config.NegativeTimeout) || config.NegativeTimeout < 0)
            errors[nameof(NegativeTimeout)] = ["NegativeTimeout must be finite and greater than or equal to zero."];

        // uint range is implicit; just guard against the impossible upper bound (0xFFFFFFFF).
        if (config.FuseMountUid == uint.MaxValue)
            errors[nameof(FuseMountUid)] = ["FuseMountUid must be a valid UID."];
        if (config.FuseMountGid == uint.MaxValue)
            errors[nameof(FuseMountGid)] = ["FuseMountGid must be a valid GID."];

        ValidateRootName(config.RelayTvFolderName, nameof(RelayTvFolderName), errors);
        ValidateRootName(config.RelayMovieFolderName, nameof(RelayMovieFolderName), errors);
        if (IsValidRootName(config.RelayTvFolderName) && IsValidRootName(config.RelayMovieFolderName)
            && string.Equals(config.RelayTvFolderName.Trim(), config.RelayMovieFolderName.Trim(), StringComparison.OrdinalIgnoreCase))
            errors[nameof(RelayMovieFolderName)] = ["Relay root names must be distinct."];

        if (config.FinEnabled)
        {
            if (string.IsNullOrWhiteSpace(config.FinMountPoint))
                errors[nameof(FinMountPoint)] = ["FinMountPoint must be nonblank when Fin is enabled."];
            else if (!Path.IsPathRooted(config.FinMountPoint))
                errors[nameof(FinMountPoint)] = ["FinMountPoint must be an absolute rooted path."];
        }

        return errors;
    }

    private static void ValidateRootName(string? value, string propertyName, IDictionary<string, IReadOnlyList<string>> errors)
    {
        if (!IsValidRootName(value))
            errors[propertyName] = ["Relay root names must be nonblank single path segments and cannot be rooted, '.', or '..'."];
    }

    private static bool IsValidRootName(string? value)
    {
        string name = value?.Trim() ?? "";
        return name.Length > 0
            && !Path.IsPathRooted(name)
            && name is not "." and not ".."
            && !name.Contains('/', StringComparison.Ordinal)
            && !name.Contains('\\');
    }
}
