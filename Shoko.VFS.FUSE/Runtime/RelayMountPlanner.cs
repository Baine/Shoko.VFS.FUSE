using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Runtime;

public enum RelayMountRootKind
{
    Tv,
    Movie,
}

public enum RelayMountPolicyState
{
    Disabled,
    Ready,
    Degraded,
    Blocked,
}

/// <summary>The filesystem-free managed-folder values needed for Relay planning.</summary>
public sealed record RelayManagedFolderSnapshot(int Id, string Name, string Path, DropFolderType DropFolderType);

/// <summary>A direct-child Relay root desired below one managed folder.</summary>
public sealed record RelayMountTarget(
    int ManagedFolderId,
    string ManagedFolderName,
    string ManagedFolderPath,
    string RootName,
    string TargetPath,
    RelayMountRootKind RootKind,
    PathResolverOptions ResolverOptions);

/// <summary>A safe planner diagnostic which contains no filesystem inspection result.</summary>
public sealed record RelayMountDiagnostic(string Code, string Message, bool Blocking = false);

public sealed record RelayMountPlan(
    IReadOnlyList<RelayMountTarget> Targets,
    IReadOnlyList<RelayMountDiagnostic> Diagnostics,
    RelayMountPolicyState State,
    IReadOnlyList<RelayManagedFolderSnapshot> EligibleFolders,
    IReadOnlyList<RelayManagedFolderSnapshot> ExcludedFolders)
{
    public bool IsBlocked => State == RelayMountPolicyState.Blocked;
    public bool IsDegraded => State == RelayMountPolicyState.Degraded;
}

/// <summary>Builds Relay mount topology without touching the filesystem or mount APIs.</summary>
public sealed class RelayMountPlanner
{
    public RelayMountPlan Plan(
        FusePluginConfiguration configuration,
        IEnumerable<RelayManagedFolderSnapshot> managedFolders,
        bool manualOverridesPresent = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var diagnostics = new List<RelayMountDiagnostic>();
        var validation = FusePluginConfiguration.Validate(configuration, null!, null!);
        foreach (var property in validation.Keys)
            diagnostics.Add(new RelayMountDiagnostic("InvalidConfiguration", property, true));

        if (!configuration.RelayEnabled)
            return new RelayMountPlan([], diagnostics, RelayMountPolicyState.Disabled, [], []);

        if (validation.Keys.Any(name => name is nameof(FusePluginConfiguration.RelayTvFolderName) or nameof(FusePluginConfiguration.RelayMovieFolderName)))
            return new RelayMountPlan([], diagnostics, RelayMountPolicyState.Blocked, [], []);

        if (!Enum.IsDefined(configuration.MovieGenerationMode))
        {
            diagnostics.Add(new RelayMountDiagnostic("InvalidMovieGenerationMode", "MovieGenerationMode", true));
            return new RelayMountPlan([], diagnostics, RelayMountPolicyState.Blocked, [], []);
        }

        if (configuration.PlexLocalExtras)
            diagnostics.Add(new RelayMountDiagnostic("LocalExtrasUnsupported", "Indexed local extras are excluded; unmanaged local extras are not scanned.", false));

        var exclusions = SplitLines(configuration.ManagedFolderExclusions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligible = new List<RelayManagedFolderSnapshot>();
        var excluded = new List<RelayManagedFolderSnapshot>();
        foreach (var folder in managedFolders ?? [])
        {
            bool sourceOnly = folder.DropFolderType.HasFlag(DropFolderType.Source) && !folder.DropFolderType.HasFlag(DropFolderType.Destination);
            bool excludedByPolicy = sourceOnly
                || exclusions.Contains(folder.Id.ToString())
                || exclusions.Contains(folder.Name);
            if (excludedByPolicy)
            {
                excluded.Add(folder);
                diagnostics.Add(new RelayMountDiagnostic(sourceOnly ? "SourceOnlyFolder" : "ManagedFolderExcluded", folder.Name));
                continue;
            }

            if (!TryCanonicalPath(folder.Path, out var folderPath))
            {
                diagnostics.Add(new RelayMountDiagnostic("InvalidManagedFolderPath", folder.Name, true));
                continue;
            }

            eligible.Add(folder with { Path = folderPath });
        }

        var targets = new List<RelayMountTarget>();
        foreach (var folder in eligible)
        {
            AddTarget(targets, folder, folder.Path, configuration.RelayTvFolderName.Trim(), RelayMountRootKind.Tv, TvOptions(configuration.MovieGenerationMode));
            if (configuration.MovieGenerationMode != MovieGenerationMode.Disabled)
                AddTarget(targets, folder, folder.Path, configuration.RelayMovieFolderName.Trim(), RelayMountRootKind.Movie, MovieOptions(configuration));
        }

        for (int i = 0; i < targets.Count; i++)
        for (int j = i + 1; j < targets.Count; j++)
        {
            var first = targets[i];
            var second = targets[j];
            if (PathsEqual(first.TargetPath, second.TargetPath))
                diagnostics.Add(new RelayMountDiagnostic("DuplicateRoot", $"{first.ManagedFolderId}:{first.RootName} conflicts with {second.ManagedFolderId}:{second.RootName}", true));
            else if (IsDescendant(first.TargetPath, second.TargetPath) || IsDescendant(second.TargetPath, first.TargetPath))
                diagnostics.Add(new RelayMountDiagnostic("NestedRoot", $"{first.ManagedFolderId}:{first.RootName} overlaps {second.ManagedFolderId}:{second.RootName}", true));
        }

        var state = diagnostics.Any(diagnostic => diagnostic.Blocking)
            ? RelayMountPolicyState.Blocked
            : configuration.PlexLocalExtras
                ? RelayMountPolicyState.Degraded
                : RelayMountPolicyState.Ready;
        return new RelayMountPlan(targets, diagnostics, state, eligible, excluded);
    }

    public RelayMountPlan Plan(
        FusePluginConfiguration configuration,
        IEnumerable<IManagedFolder> managedFolders,
        bool manualOverridesPresent = false) =>
        Plan(
            configuration,
            (managedFolders ?? [])
                .Select(folder => new RelayManagedFolderSnapshot(folder.ID, folder.Name, folder.Path, folder.DropFolderType)),
            manualOverridesPresent);

    public static RelayMountPlan Build(
        FusePluginConfiguration configuration,
        IEnumerable<RelayManagedFolderSnapshot> managedFolders,
        bool manualOverridesPresent = false) =>
        new RelayMountPlanner().Plan(configuration, managedFolders, manualOverridesPresent);

    private static void AddTarget(
        ICollection<RelayMountTarget> targets,
        RelayManagedFolderSnapshot folder,
        string folderPath,
        string rootName,
        RelayMountRootKind rootKind,
        PathResolverOptions options)
    {
        targets.Add(new RelayMountTarget(
            folder.Id,
            folder.Name,
            folderPath,
            rootName,
            Path.Combine(folderPath, rootName),
            rootKind,
            options));
    }

    private static PathResolverOptions TvOptions(MovieGenerationMode mode) => mode switch
    {
        MovieGenerationMode.EnabledRemove => new PathResolverOptions { Shows = true, MoviesAsTv = false },
        _ => new PathResolverOptions { Shows = true, MoviesAsTv = true },
    };

    private static PathResolverOptions MovieOptions(FusePluginConfiguration configuration) => new()
    {
        Shows = false,
        MoviesAsTv = false,
        StandaloneMovies = true,
        IncludeMovieExtras = configuration.PlexLocalExtras,
    };

    private static IEnumerable<string> SplitLines(string? value) =>
        (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool PathsEqual(string first, string second) => string.Equals(CanonicalPath(first), CanonicalPath(second), PathComparison);

    private static bool IsDescendant(string parent, string child)
    {
        string relative = Path.GetRelativePath(CanonicalPath(parent), CanonicalPath(child));
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, ".", PathComparison)
            && !string.Equals(relative, "..", PathComparison)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool TryCanonicalPath(string? path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            canonical = CanonicalPath(path);
            return canonical.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
