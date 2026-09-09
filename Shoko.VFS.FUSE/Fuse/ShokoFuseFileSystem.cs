using System.Runtime.InteropServices;
using System.Text;
using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Fuse;

/// <summary>
/// FUSE filesystem implementation backed by an <see cref="IVirtualPathResolver"/>.
/// Maps every mount-relative path callback to a lookup/read in the resolver.
/// </summary>
public static class NativeUserInfo
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getgid();

    public static readonly uint Uid = GetUid();
    public static readonly uint Gid = GetGid();

    private static uint GetUid()
    {
        try { return getuid(); }
        catch { return 0; }
    }

    private static uint GetGid()
    {
        try { return getgid(); }
        catch { return 0; }
    }
}

/// <summary>
/// FUSE filesystem implementation backed by an <see cref="IVirtualPathResolver"/>.
/// Maps every mount-relative path callback to a lookup/read in the resolver.
/// </summary>
public sealed class ShokoFuseFileSystem : IFuseOperations
{
    private readonly IVirtualPathResolver _resolver;
    private readonly ILogger _logger;
    private readonly int _maxReadSize;
    private readonly uint _uid;
    private readonly uint _gid;

    /// <param name="uid">Owner UID reported by getattr; null = daemon process UID.</param>
    /// <param name="gid">Owner GID reported by getattr; null = daemon process GID.</param>
    /// <remarks>
    /// The kernel's user_id/group_id mount options are set by fusermount3 to the
    /// mounting user and libfuse3 does not honor uid=/gid= as attribute overrides,
    /// so file ownership must be reported here.
    /// </remarks>
    public ShokoFuseFileSystem(IVirtualPathResolver resolver, ILogger logger, int maxReadSize = 128 * 1024,
        uint? uid = null, uint? gid = null)
    {
        _resolver = resolver;
        _logger = logger;
        _maxReadSize = maxReadSize;
        _uid = uid ?? NativeUserInfo.Uid;
        _gid = gid ?? NativeUserInfo.Gid;
    }

    public void Init(ref FuseConnInfo fuse_conn_info)
    {
        // Leave kernel defaults in place; per-op timeouts come from mount options.
    }

    public void Dispose()
    {
    }

    internal static string GetPath(ReadOnlySpan<byte> bytes)
    {
        // Paths arrive as null-terminated UTF-8 byte spans.
        int len = bytes.IndexOf((byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.UTF8.GetString(bytes[..len]);
    }

    internal static FuseFileStat BuildStat(VirtualEntry entry, uint uid, uint gid)
    {
        var stat = new FuseFileStat();
        FillStat(ref stat, entry, uid, gid);
        return stat;
    }

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        stat = new FuseFileStat();
        var entry = _resolver.Lookup(GetPath(fileNamePtr.Span));
        if (entry is null)
            return PosixResult.ENOENT;

        FillStat(ref stat, entry);
        return PosixResult.Success;
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        entries = [];
        var children = _resolver.ReadDirectory(GetPath(fileNamePtr.Span));

        var list = new List<FuseDirEntry>(children.Count + 2);
        list.AddRange(FuseHelper.DotEntries);

        foreach (var child in children)
        {
            var childStat = new FuseFileStat();
            FillStat(ref childStat, child);
            // Offset 0: directory listing is regenerated on each call and
            // does not support resuming at a specific offset.
            list.Add(new FuseDirEntry(child.Name, 0, 0, childStat));
        }

        entries = list;
        return PosixResult.Success;
    }

    public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var entry = _resolver.Lookup(GetPath(fileNamePtr.Span));
        if (entry is null)
            return PosixResult.ENOENT;
        if (!entry.IsFile)
            return PosixResult.EISDIR;

        // Store the source path in the per-open-handle context so Read can use it.
        fileInfo.Context = entry.SourcePath;
        return PosixResult.Success;
    }

    public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo)
    {
        readLength = 0;
        var sourcePath = fileInfo.Context as string;
        if (string.IsNullOrEmpty(sourcePath))
            return PosixResult.EIO;

        try
        {
            using var fs = File.OpenRead(sourcePath);
            fs.Position = position;

            int toRead = (int)Math.Min(buffer.Length, _maxReadSize);
            var span = buffer.Span;
            readLength = fs.Read(span[..toRead]);
            return PosixResult.Success;
        }
        catch (FileNotFoundException)
        {
            return PosixResult.ENOENT;
        }
        catch (DirectoryNotFoundException)
        {
            return PosixResult.ENOENT;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Read failed on {Path}", sourcePath);
            return PosixResult.EIO;
        }
    }

    public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        fileInfo.Context = null;
        return PosixResult.Success;
    }

    public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
        => PosixResult.Success;

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        return _resolver.Lookup(GetPath(fileNamePtr.Span))?.IsDirectory == true ? PosixResult.Success : PosixResult.ENOTDIR;
    }

    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
        => PosixResult.Success;

    public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        statvfs = new FuseVfsStat();
        return PosixResult.Success;
    }

    // Read-only filesystem: all mutation ops are denied.
    public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo) => PosixResult.EROFS;
    public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.EROFS;
    public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr) => PosixResult.EROFS;
    public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr) => PosixResult.EROFS;
    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.EROFS;
    public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.EROFS;
    public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid) => PosixResult.EROFS;
    public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size) => PosixResult.EROFS;
    public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo) => PosixResult.EROFS;
    public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo)
    {
        writtenLength = 0;
        return PosixResult.EROFS;
    }
    public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fileInfo) => PosixResult.EROFS;
    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target)
    {
        var entry = _resolver.Lookup(GetPath(fileNamePtr.Span));
        if (entry?.SymlinkTarget is not string linkTarget)
            return PosixResult.ENOENT;

        var bytes = System.Text.Encoding.UTF8.GetBytes(linkTarget);
        var span = target.Span;
        if (bytes.Length >= span.Length)
            return PosixResult.ENAMETOOLONG;
        bytes.CopyTo(span);
        span[bytes.Length] = 0;
        return PosixResult.Success;
    }
    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, IntPtr arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, IntPtr data) => PosixResult.ENOSYS;

    private void FillStat(ref FuseFileStat stat, VirtualEntry entry)
    {
        FillStat(ref stat, entry, _uid, _gid);
    }

    private static void FillStat(ref FuseFileStat stat, VirtualEntry entry, uint uid, uint gid)
    {
        stat.st_mode = (PosixFileMode)(entry.Mode != 0 ? entry.Mode : VirtualEntry.DefaultMode(entry.NodeType));
        stat.st_size = entry.Size;
        stat.st_nlink = entry.IsDirectory ? 2 : 1;
        stat.st_uid = uid;
        stat.st_gid = gid;
        var ts = new TimeSpec(entry.LastModified);
        stat.st_atim = ts;
        stat.st_ctim = ts;
        stat.st_mtim = ts;
        stat.st_birthtim = ts;
    }
}
