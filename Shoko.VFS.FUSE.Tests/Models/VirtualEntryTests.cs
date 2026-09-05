using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Tests.Models;

public class VirtualEntryTests
{
    [Fact]
    public void DirectoryEntry_HasCorrectDefaults()
    {
        var entry = new VirtualEntry
        {
            Name = "test",
            NodeType = VirtualNodeType.Directory
        };

        Assert.True(entry.IsDirectory);
        Assert.False(entry.IsFile);
        Assert.Equal(0, entry.Size);
        Assert.Null(entry.SourcePath);
    }

    [Fact]
    public void FileEntry_HasSourcePath()
    {
        var entry = new VirtualEntry
        {
            Name = "episode.mkv",
            NodeType = VirtualNodeType.File,
            Size = 1_500_000_000,
            SourcePath = "/media/anime/series/episode.mkv"
        };

        Assert.False(entry.IsDirectory);
        Assert.True(entry.IsFile);
        Assert.Equal(1_500_000_000, entry.Size);
        Assert.Equal("/media/anime/series/episode.mkv", entry.SourcePath);
    }

    [Fact]
    public void DefaultMode_Directory_ReturnsDrwxrXrX()
    {
        uint mode = VirtualEntry.DefaultMode(VirtualNodeType.Directory);
        // S_IFDIR (0x4000) | 0755 (0x1ED) = 0x41ED
        Assert.Equal(0x41EDu, mode);
    }

    [Fact]
    public void DefaultMode_File_ReturnsRwRR()
    {
        uint mode = VirtualEntry.DefaultMode(VirtualNodeType.File);
        // S_IFREG (0x8000) | 0644 (0x1A4) = 0x81A4
        Assert.Equal(0x81A4u, mode);
    }

    [Fact]
    public void DefaultMode_Symlink_ReturnsLrwxrwxrwx()
    {
        uint mode = VirtualEntry.DefaultMode(VirtualNodeType.Symlink);
        // S_IFLNK (0xA000) | 0777 (0x1FF) = 0xA1FF
        Assert.Equal(0xA1FFu, mode);
    }

    [Fact]
    public void LastModified_DefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = new VirtualEntry { Name = "x", NodeType = VirtualNodeType.File };
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(entry.LastModified, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void SymlinkEntry_HasTarget()
    {
        var entry = new VirtualEntry
        {
            Name = "link",
            NodeType = VirtualNodeType.Symlink,
            SymlinkTarget = "/real/path"
        };

        Assert.Equal("/real/path", entry.SymlinkTarget);
    }
}
