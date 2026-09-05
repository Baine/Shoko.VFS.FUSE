using System.Runtime.InteropServices;
using System.Text;
using FuseDotNet;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shoko.VFS.FUSE.Tests.Fuse;

public class ShokoFuseFileSystemTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("!ShokoRelayVFS")]
    [InlineData("12345/Season 01/S01E05 [999].mkv")]
    public void GetPath_DecodesUtf8(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input).Concat(new byte[] { 0 }).ToArray();
        Assert.Equal(input, ShokoFuseFileSystem.GetPath(bytes));
    }

    [Fact]
    public void GetPath_HandlesUtf8Unicode()
    {
        var input = "アニメ/S01E01.mkv";
        var bytes = Encoding.UTF8.GetBytes(input);
        Assert.Equal(input, ShokoFuseFileSystem.GetPath(bytes));
    }

    [Fact]
    public void GetPath_NoNullTerminator_ReturnsAllBytes()
    {
        var input = "hello";
        var bytes = Encoding.UTF8.GetBytes(input);
        Assert.Equal(input, ShokoFuseFileSystem.GetPath(bytes));
    }

    [Fact]
    public void BuildStat_File_ReflectsEntry()
    {
        var entry = new VirtualEntry
        {
            Name = "ep.mkv",
            NodeType = VirtualNodeType.File,
            Size = 1_000_000,
            Mode = VirtualEntry.DefaultMode(VirtualNodeType.File),
            LastModified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero)
        };

        var stat = ShokoFuseFileSystem.BuildStat(entry);

        Assert.Equal(1_000_000, stat.st_size);
        Assert.Equal(1, stat.st_nlink);
        Assert.Equal(VirtualEntry.DefaultMode(VirtualNodeType.File), (uint)stat.st_mode);
        Assert.Equal(entry.LastModified.ToUnixTimeSeconds(), (long)stat.st_mtim.tv_sec);
    }

    [Fact]
    public void BuildStat_Directory_HasTwoLinks()
    {
        var entry = new VirtualEntry
        {
            Name = "series",
            NodeType = VirtualNodeType.Directory,
            Mode = VirtualEntry.DefaultMode(VirtualNodeType.Directory)
        };

        var stat = ShokoFuseFileSystem.BuildStat(entry);

        Assert.Equal(2, stat.st_nlink);
        Assert.Equal(0, stat.st_size);
        Assert.Equal(VirtualEntry.DefaultMode(VirtualNodeType.Directory), (uint)stat.st_mode);
    }

    [Fact]
    public void BuildStat_Symlink_ReflectsSymlinkMode()
    {
        var entry = new VirtualEntry
        {
            Name = "link",
            NodeType = VirtualNodeType.Symlink,
            Mode = VirtualEntry.DefaultMode(VirtualNodeType.Symlink),
            SymlinkTarget = "/some/target"
        };

        var stat = ShokoFuseFileSystem.BuildStat(entry);

        Assert.Equal(VirtualEntry.DefaultMode(VirtualNodeType.Symlink), (uint)stat.st_mode);
    }

    [Fact]
    public void GetAttr_UsesMetadataSizeWithoutStattingSource()
    {
        string sourcePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(sourcePath, "source size");
            var resolver = new InMemoryResolver().Add("file.mkv", new VirtualEntry
            {
                Name = "file.mkv",
                NodeType = VirtualNodeType.File,
                Size = 0,
                SourcePath = sourcePath,
            });
            var filesystem = new ShokoFuseFileSystem(resolver, NullLogger.Instance);
            var bytes = Encoding.UTF8.GetBytes("file.mkv\0");
            IntPtr address = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
                var name = new ReadOnlyNativeMemory<byte>(address, bytes.Length);
                var fileInfo = new FuseFileInfo();

                Assert.Equal(0, (int)filesystem.GetAttr(name, out var stat, ref fileInfo));
                Assert.Equal(0, stat.st_size);
            }
            finally
            {
                Marshal.FreeHGlobal(address);
            }
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
