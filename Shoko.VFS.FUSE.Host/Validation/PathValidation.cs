using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Host.Validation;

/// <summary>
/// Validates that resolved source paths actually exist on disk after aggregation.
/// This catches server-path / host-path mapping mismatches early (not silently at mount time).
/// </summary>
public static class PathValidation
{
    /// <summary>
    /// Samples up to <c>sampleCount</c> resolved source paths from the aggregated series data.
    /// Returns <c>true</c> if all samples exist on disk, <c>false</c> otherwise.
    /// Logs the first few mismatches via <c>log</c>.
    /// </summary>
    public static bool ValidatePaths(IReadOnlyList<SeriesData> series, int sampleCount = 5, int maxPathLength = 500, Action<string>? log = null)
    {
        var samples = series
            .SelectMany(s => s.Mappings)
            .Select(m => m.SourcePath)
            .Where(p => p is not null && !string.IsNullOrEmpty(p))
            .Take(sampleCount)
            .ToList();

        if (samples.Count == 0)
            return true; // No paths to validate — not a failure.

        int mismatches = 0;
        foreach (var path in samples)
        {
            if (path is null || path.Length > maxPathLength || !File.Exists(path))
            {
                if (mismatches < 5)
                    log?.Invoke($"PATH MISMATCH: {path} does not exist on disk");
                mismatches++;
            }
        }

        if (mismatches > 0)
            log?.Invoke($"Path validation failed: {mismatches}/{samples.Count} sample paths missing from disk. " +
                "Check ServerPathRoot / ManagedFolderPathRoot mapping. The mount will be marked failed.");

        return mismatches == 0;
    }
}