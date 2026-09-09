namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// Controls how movie-type series are placed across the TV and movie VFS roots.
/// Values match ShokoRelay and <c>FusePluginConfiguration.MovieGenerationMode</c>.
/// </summary>
/// <remarks>
/// Semantics mirror ShokoRelay's <c>MovieGenerationMode</c> (Config/RelayConfig.cs):
/// <list type="bullet">
/// <item><description><see cref="Disabled"/>: single TV root per managed folder containing shows and movies; no movie root.</description></item>
/// <item><description><see cref="EnabledMaintain"/>: movie root with movies only, plus movies kept in the TV root as TV-shaped paths.</description></item>
/// <item><description><see cref="EnabledRemove"/>: movie root with movies only; movies removed from the TV root.</description></item>
/// </list>
/// </remarks>
public enum MovieGenerationMode
{
    /// <summary>Single TV root containing shows and movies; no standalone movie root.</summary>
    Disabled = 0,

    /// <summary>Generate standalone movie folders but keep movies in the TV root as well (ShokoRelay "Maintain Movies in Standard VFS").</summary>
    EnabledMaintain = 1,

    /// <summary>Generate standalone movie folders and remove movies from the TV root (ShokoRelay "Remove Movies from Standard VFS").</summary>
    EnabledRemove = 2
}
