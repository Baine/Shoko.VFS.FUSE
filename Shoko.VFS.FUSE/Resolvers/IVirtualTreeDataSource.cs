using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers;

/// <summary>Supplies a flat, pre-resolved arbitrary-depth virtual tree.</summary>
public interface IVirtualTreeDataSource
{
    /// <summary>
    /// Gets mount-root-relative entries. The resolver copies the returned
    /// collection while building its immutable snapshot.
    /// </summary>
    IReadOnlyList<VirtualTreeEntryData> GetEntries();
}
