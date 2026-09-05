using System.Runtime.InteropServices;
using System.Text;
using FuseDotNet;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Naming;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

public sealed class VirtualTreeResolverTests
{
    [Fact]
    public void RelayOutputRetainsLegacyPathsOrderAndMetadataThroughTreeTraversal()
    {
        var source = new SeriesData(
            7,
            "Show",
            false,
            [
                Mapping(701, 701, episode: 1, size: 11),
                Mapping(702, 702, episode: 1, size: 22),
                Mapping(703, 703, episode: 2, partIndex: 1, partCount: 2, size: 33),
                Mapping(704, 704, episode: 2, partIndex: 2, partCount: 2, size: 44),
                Mapping(705, 705, season: -2, title: "Trailer", size: 55),
            ]
        );
        var movie = new SeriesData(
            8,
            "Movie",
            true,
            [
                Mapping(801, 800, size: 66),
                Mapping(802, 802, season: -4, isMain: false, title: "Making Of", size: 77),
            ]
        );
        var resolver = new ShokoPathResolver(
            new SeriesSource(source, movie),
            new RelayNamingStrategy(),
            new PathResolverOptions { StandaloneMovies = true }
        );

        try
        {
            Ready(resolver);

            Assert.Equal(new[] { "7", "8", "800" }, Names(resolver.ReadDirectory("")));
            Assert.Equal(new[] { "Trailers", "Season 1" }, Names(resolver.ReadDirectory("7")));
            Assert.Equal(
                new[] { "S01E01-dup1 [701].mkv", "S01E01-dup2 [702].mkv", "S01E02-pt1.mkv", "S01E02-pt2.mkv" },
                Names(resolver.ReadDirectory("7/Season 1"))
            );

            var episode = resolver.Lookup("7/Season 1/S01E01-dup1 [701].mkv");
            Assert.NotNull(episode);
            Assert.Equal("/source/701.mkv", episode.SourcePath);
            Assert.Equal(11, episode.Size);
            Assert.Equal(VirtualEntry.DefaultMode(VirtualNodeType.File), episode.Mode);
            Assert.Equal(
                new[] { "T1 ❯ Trailer.mkv" },
                Names(resolver.ReadDirectory("7/Trailers"))
            );
            Assert.Equal("/source/705.mkv", resolver.GetSourcePath("7/Trailers/T1 ❯ Trailer.mkv"));
            Assert.Equal(new[] { "Movie [801].mkv", "Featurettes" }, Names(resolver.ReadDirectory("800")));
            Assert.Equal("/source/802.mkv", resolver.GetSourcePath("800/Featurettes/O1 ❯ Making Of.mkv"));
        }
        finally
        {
            resolver.Stop();
        }
    }

    [Fact]
    public void GenericTreeSupportsArbitraryNesting()
    {
        var resolver = NewResolver([
            File("one/two/three/four/file.mkv", "/source/file.mkv", 9),
        ]);

        try
        {
            Assert.Equal(new[] { "one" }, Names(resolver.ReadDirectory("")));
            Assert.True(resolver.Lookup("one")!.IsDirectory);
            Assert.True(resolver.Lookup("one/two")!.IsDirectory);
            Assert.True(resolver.Lookup("one/two/three")!.IsDirectory);
            Assert.True(resolver.Lookup("one/two/three/four")!.IsDirectory);
            Assert.Equal("/source/file.mkv", resolver.GetSourcePath("one/two/three/four/file.mkv"));
        }
        finally
        {
            resolver.Stop();
        }
    }

    [Fact]
    public void GenericTreeRejectsUnsafePathsAndInvalidPayloads()
    {
        var invalidPaths = new[] { "", " ", "/file", "\\file", "file/", "file//child", "file/./child", "file/../child", "../file", "C:/file" };
        foreach (var path in invalidPaths)
        {
            Assert.Throws<ArgumentException>(() => Build(File(path, "/source/file.mkv")));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Build(File("file", "/source/file.mkv", -1)));
        Assert.Throws<ArgumentException>(() => Build(new VirtualTreeEntryData("file", VirtualNodeType.File)));
        Assert.Throws<ArgumentException>(() => Build(new VirtualTreeEntryData("file", VirtualNodeType.File, SourcePath: "relative")));
        Assert.Throws<ArgumentException>(() => Build(new VirtualTreeEntryData("link", VirtualNodeType.Symlink)));
        Assert.Throws<ArgumentException>(() => Build(new VirtualTreeEntryData("link", VirtualNodeType.Symlink, SourcePath: "/source/file")));
        Assert.Throws<ArgumentException>(() => Build(new VirtualTreeEntryData("dir", VirtualNodeType.Directory, SourcePath: "/source/file")));
    }

    [Fact]
    public void GenericTreeRejectsFileDirectoryAndDuplicateCollisions()
    {
        Assert.Throws<InvalidOperationException>(() => Build(
            File("same", "/source/file.mkv"),
            new VirtualTreeEntryData("same/child", VirtualNodeType.Directory)
        ));
        Assert.Throws<InvalidOperationException>(() => Build(
            File("same", "/source/one.mkv"),
            File("same", "/source/two.mkv")
        ));
    }

    [Fact]
    public void GenericTreeSynthesizesIntermediateDirectories()
    {
        var resolver = NewResolver([
            File("a/b/c/file.mkv", "/source/file.mkv"),
            File("a/b/other.mkv", "/source/other.mkv"),
        ]);

        try
        {
            Assert.Equal(new[] { "a" }, Names(resolver.ReadDirectory("")));
            Assert.Equal(new[] { "b" }, Names(resolver.ReadDirectory("a")));
            Assert.Equal(new[] { "c", "other.mkv" }, Names(resolver.ReadDirectory("a/b")));
        }
        finally
        {
            resolver.Stop();
        }
    }

    [Fact]
    public void GenericMetadataAndSymlinkKeepStatReadlinkAndOpenSemantics()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var timestamp = new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));
        var resolver = NewResolver([
            new VirtualTreeEntryData("file.mkv", VirtualNodeType.File, "/source/file.mkv", Size: 123, Mode: 0x81A0, LastModified: timestamp),
            new VirtualTreeEntryData("link", VirtualNodeType.Symlink, SymlinkTarget: "file.mkv", LastModified: timestamp),
        ]);

        try
        {
            var file = resolver.Lookup("file.mkv");
            Assert.NotNull(file);
            Assert.Equal("/source/file.mkv", file.SourcePath);
            Assert.Equal(123, file.Size);
            Assert.Equal(0x81A0u, file.Mode);
            Assert.Equal(timestamp.ToUniversalTime(), file.LastModified);

            var link = resolver.Lookup("link");
            Assert.NotNull(link);
            Assert.Equal("file.mkv", link.SymlinkTarget);
            Assert.Equal(0u, link.Mode);

            var filesystem = new ShokoFuseFileSystem(resolver, NullLogger.Instance);
            using var pathMemory = new NativeAllocation("link");
            var fileInfo = new FuseFileInfo();
            Assert.Equal(PosixResult.Success, filesystem.GetAttr(pathMemory.ReadOnly, out var stat, ref fileInfo));
            Assert.Equal((PosixFileMode)VirtualEntry.DefaultMode(VirtualNodeType.Symlink), stat.st_mode);
            Assert.Equal(PosixResult.EISDIR, filesystem.Open(pathMemory.ReadOnly, ref fileInfo));

            using var targetMemory = new NativeAllocation(64);
            Assert.Equal(PosixResult.Success, filesystem.ReadLink(pathMemory.ReadOnly, targetMemory.Writable));
            Assert.Equal("file.mkv", targetMemory.ReadString());
        }
        finally
        {
            resolver.Stop();
        }
    }

    [Fact]
    public void GenericSnapshotCopiesDatasourceCollectionAndRetainsStaleSnapshot()
    {
        var source = new MutableTreeSource([
            File("old/file.mkv", "/source/old.mkv", 1),
        ]);
        var resolver = new ShokoPathResolver(
            source,
            new PathResolverOptions { CacheTtl = TimeSpan.FromHours(1) }
        );

        try
        {
            Ready(resolver);
            source.Entries.Clear();
            source.Entries.Add(File("new/file.mkv", "/source/new.mkv", 2));

            Assert.Equal("/source/old.mkv", resolver.GetSourcePath("old/file.mkv"));
            Assert.Null(resolver.GetSourcePath("new/file.mkv"));

            resolver.Rebuild();
            Assert.True(SpinWait.SpinUntil(() => resolver.GetSourcePath("new/file.mkv") == "/source/new.mkv", TimeSpan.FromSeconds(5)));
            Assert.Null(resolver.GetSourcePath("old/file.mkv"));
        }
        finally
        {
            resolver.Stop();
        }
    }

    private static VirtualTreeSnapshot Build(params VirtualTreeEntryData[] entries) =>
        VirtualTreeSnapshot.Build(entries, legacyFirstWins: false, DateTimeOffset.UtcNow);

    private static ShokoPathResolver NewResolver(IReadOnlyList<VirtualTreeEntryData> entries)
    {
        var resolver = new ShokoPathResolver(new MutableTreeSource(entries.ToList()));
        Ready(resolver);
        return resolver;
    }

    private static void Ready(ShokoPathResolver resolver)
    {
        resolver.ReadDirectory("");
        Assert.True(SpinWait.SpinUntil(() => resolver.HasSnapshot, TimeSpan.FromSeconds(5)));
    }

    private static string[] Names(IEnumerable<VirtualEntry> entries) => entries.Select(entry => entry.Name).ToArray();

    private static VirtualTreeEntryData File(string path, string? sourcePath, long size = 0) =>
        new(path, VirtualNodeType.File, SourcePath: sourcePath, Size: size);

    private static EpisodeData Mapping(
        int fileId,
        int episodeId,
        int season = 1,
        int episode = 1,
        int? partIndex = null,
        int? partCount = null,
        string? title = null,
        bool isMain = true,
        long size = 0) => new(
            fileId,
            episodeId,
            season,
            episode,
            null,
            partIndex,
            partCount,
            false,
            isMain,
            title,
            $"/source/{fileId}.mkv",
            size,
            ".mkv"
        );

    private sealed class SeriesSource(params SeriesData[] series) : IShokoPathDataSource
    {
        public IReadOnlyList<SeriesData> GetAllSeries() => series;
    }

    private sealed class MutableTreeSource(List<VirtualTreeEntryData> entries) : IVirtualTreeDataSource
    {
        public List<VirtualTreeEntryData> Entries { get; } = entries;

        public IReadOnlyList<VirtualTreeEntryData> GetEntries() => Entries;
    }

    private sealed class NativeAllocation : IDisposable
    {
        private readonly IntPtr _address;
        private readonly int _length;

        public NativeAllocation(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            _length = bytes.Length;
            _address = Marshal.AllocHGlobal(_length);
            Marshal.Copy(bytes, 0, _address, _length);
        }

        public NativeAllocation(int length)
        {
            _length = length;
            _address = Marshal.AllocHGlobal(length);
        }

        public ReadOnlyNativeMemory<byte> ReadOnly => new(_address, _length);
        public NativeMemory<byte> Writable => new(_address, _length);

        public string ReadString()
        {
            var bytes = new byte[_length];
            Marshal.Copy(_address, bytes, 0, _length);
            int length = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
        }

        public void Dispose() => Marshal.FreeHGlobal(_address);
    }
}
