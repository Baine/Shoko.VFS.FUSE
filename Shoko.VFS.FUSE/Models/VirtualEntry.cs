namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// A single entry in the virtual filesystem tree.
/// </summary>
public sealed class VirtualEntry
{
    /// <summary>The entry name (file or directory name, not the full path).</summary>
    public required string Name { get; init; }

    /// <summary>Whether this is a directory, file, or symlink.</summary>
    public required VirtualNodeType NodeType { get; init; }

    /// <summary>File size in bytes. 0 for directories.</summary>
    public long Size { get; init; }

    /// <summary>Unix file mode (permissions). Defaults to 0644 for files, 0755 for directories.</summary>
    public uint Mode { get; init; }

    /// <summary>Last modification time in UTC.</summary>
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>For files: the absolute path to the source file on disk.</summary>
    public string? SourcePath { get; init; }

    /// <summary>For symlinks: the link target path.</summary>
    public string? SymlinkTarget { get; init; }

    /// <summary>Whether this entry is a directory that can have children.</summary>
    public bool IsDirectory => NodeType == VirtualNodeType.Directory;

    /// <summary>Whether this entry is a regular file.</summary>
    public bool IsFile => NodeType == VirtualNodeType.File;

    /// <summary>Get the default POSIX mode based on node type.</summary>
    public static uint DefaultMode(VirtualNodeType type) => type switch
    {
        // PosixFileMode values: Directory=0x4000, Regular=0x8000, SymbolicLink=0xA000
        // Permission bits: OwnerAll=0x1C0, GroupReadExecute=0x28, OthersReadExecute=0x05,
        // OwnerReadWrite=0x180, GroupRead=0x20, OthersRead=0x04, All=0x1FF
        VirtualNodeType.Directory => 0x4000 | 0x1C0 | 0x28 | 0x05,   // drwxr-xr-x (0755)
        VirtualNodeType.File => 0x8000 | 0x180 | 0x20 | 0x04,        // -rw-r--r-- (0644)
        VirtualNodeType.Symlink => 0xA000 | 0x1FF,                    // lrwxrwxrwx (0777)
        _ => 0x8000 | 0x180 | 0x20 | 0x04
    };
}
