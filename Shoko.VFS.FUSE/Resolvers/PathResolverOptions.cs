using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers;

/// <summary>
/// Configuration for the path resolver's derived model and movie handling.
/// </summary>
public sealed class PathResolverOptions
{
    /// <summary>Legacy compatibility value; resolver output is controlled by the independent flags below.</summary>
    [Obsolete("Use Shows, MoviesAsTv, and StandaloneMovies.")]
    public MovieGenerationMode Mode { get; init; } = MovieGenerationMode.Disabled;

    /// <summary>Whether to expose regular show content.</summary>
    public bool Shows { get; init; } = true;

    /// <summary>Whether to expose movie series through the TV-shaped layout.</summary>
    public bool MoviesAsTv { get; init; } = true;

    /// <summary>Whether to expose standalone movie folders.</summary>
    public bool StandaloneMovies { get; init; }

    /// <summary>Stale-while-revalidate interval for the published resolver snapshot.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Stale interval for lazily materialized per-series subtrees (lazy data sources only).</summary>
    public TimeSpan SeriesCacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether extras are linked into movie folders.</summary>
    public bool IncludeMovieExtras { get; init; } = true;
}
