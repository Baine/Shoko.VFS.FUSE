namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// Controls whether movie paths are generated alongside TV paths.
/// Values match ShokoRelay and <c>FusePluginConfiguration.MovieGenerationMode</c>.
/// </summary>
public enum MovieGenerationMode
{
    /// <summary>Generate TV paths only.</summary>
    Disabled = 0,

    /// <summary>Generate TV and movie paths.</summary>
    EnabledMaintain = 1,

    /// <summary>Generate movie paths only.</summary>
    EnabledRemove = 2
}
