namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// Immutable input for an arbitrary-depth virtual tree.
/// <paramref name="Path"/> is relative to the mount root.
/// </summary>
public sealed record VirtualTreeEntryData(
    string Path,
    VirtualNodeType NodeType,
    string? SourcePath = null,
    string? SymlinkTarget = null,
    long Size = 0,
    uint Mode = 0,
    DateTimeOffset LastModified = default);
