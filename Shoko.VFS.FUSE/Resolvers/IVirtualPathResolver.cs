using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers;

/// <summary>
/// Resolves mount-relative paths to virtual filesystem entries.
/// This is the core abstraction that maps Shoko's data model to a virtual tree.
/// </summary>
public interface IVirtualPathResolver
{
    /// <summary>
    /// Lists the children of a directory at the given mount-relative path.
    /// For the root path, returns top-level entries (series folders, etc.).
    /// </summary>
    IReadOnlyList<VirtualEntry> ReadDirectory(string mountRelativePath);

    /// <summary>
    /// Looks up a single entry by its full mount-relative path.
    /// Returns null if the path does not exist.
    /// </summary>
    VirtualEntry? Lookup(string mountRelativePath);

    /// <summary>
    /// Returns the source file path for a given mount-relative file path.
    /// Returns null if the path doesn't map to a file.
    /// </summary>
    string? GetSourcePath(string mountRelativePath);

    /// <summary>
    /// Requests an asynchronous background refresh for a given mount-relative path and optionally its children.
    /// Returns immediately; the current snapshot is retained until the refresh completes.
    /// </summary>
    void Invalidate(string mountRelativePath, bool includeChildren = false);

    /// <summary>
    /// Requests an asynchronous background rebuild of the virtual tree from Shoko data.
    /// Returns immediately; the current snapshot is retained until the rebuild completes.
    /// </summary>
    void Rebuild();
}
