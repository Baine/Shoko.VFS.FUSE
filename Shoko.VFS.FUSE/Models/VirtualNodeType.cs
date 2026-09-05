namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// Type of a virtual filesystem entry.
/// </summary>
public enum VirtualNodeType
{
    Directory,
    File,
    Symlink
}
