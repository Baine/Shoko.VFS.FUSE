namespace Shoko.VFS.FUSE.Models;

/// <summary>
/// How the FUSE daemon reads source file data.
/// </summary>
public enum ReadMode
{
    /// <summary>
    /// FUSE daemon reads the source file and serves data through the FUSE channel.
    /// Default. Works everywhere. Slight overhead per read but only affects initial open.
    /// </summary>
    DirectRead,

    /// <summary>
    /// After open, return the source file descriptor to the kernel.
    /// Zero overhead on reads, but requires the source to be accessible from
    /// the FUSE process and may not work with all FUSE implementations.
    /// </summary>
    FdPassthrough
}

/// <summary>
/// Configuration for a single FUSE mount point.
/// </summary>
public sealed class MountOptions
{
    /// <summary>Display name for this mount (for logging).</summary>
    public required string Name { get; init; }

    /// <summary>The directory path where the FUSE filesystem will be mounted.</summary>
    public required string MountPoint { get; init; }

    /// <summary>Whether this mount is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How to serve file reads.</summary>
    public ReadMode ReadMode { get; init; } = ReadMode.DirectRead;

    /// <summary>FUSE allow_other option: allow non-root users to access the mount.</summary>
    public bool AllowOther { get; init; }

    /// <summary>
    /// Owner UID reported by FUSE for every file/directory on the mount. <c>null</c> means
    /// "don't pass <c>uid=</c> — let the FUSE kernel module pick (usually the mounter's UID,
    /// i.e. root). Set to a numeric UID (e.g. 99 for Unraid's <c>nobody</c>) so the mount
    /// shows up as a specific user in <c>ls -l</c> regardless of who mounted it.
    /// </summary>
    public uint? Uid { get; init; }

    /// <summary>
    /// Owner GID reported by FUSE for every file/directory on the mount. See <see cref="Uid"/>.
    /// </summary>
    public uint? Gid { get; init; }

    /// <summary>Attribute cache timeout in seconds. Higher = better perf, less freshness.</summary>
    public double AttrTimeout { get; init; } = 2.0;

    /// <summary>Entry (dentry) cache timeout in seconds.</summary>
    public double EntryTimeout { get; init; } = 2.0;

    /// <summary>Negative entry cache timeout (for ENOENT lookups).</summary>
    public double NegativeTimeout { get; init; } = 0.5;

    /// <summary>Maximum read size per FUSE read request.</summary>
    public int MaxReadSize { get; init; } = 128 * 1024; // 128 KB
}
