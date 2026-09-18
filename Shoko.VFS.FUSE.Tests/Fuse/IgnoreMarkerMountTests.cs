using Shoko.VFS.FUSE.Fuse;
using Xunit;

namespace Shoko.VFS.FUSE.Tests.Fuse;

/// <summary>
/// Unit tests for the mount-point tolerance behind <c>CreateIgnoreFiles</c>:
/// a directory containing only <c>.ignore</c> markers counts as acceptable.
/// </summary>
public class IgnoreMarkerMountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vfs-ignore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void EmptyDirectory_IsMountable()
    {
        Directory.CreateDirectory(_dir);
        Assert.True(FuseMountService.IsEmptyOrIgnoreOnly(_dir));
    }

    [Fact]
    public void IgnoreOnlyDirectory_IsMountable()
    {
        Directory.CreateDirectory(_dir);
        File.Create(Path.Combine(_dir, ".ignore")).Dispose();
        Assert.True(FuseMountService.IsEmptyOrIgnoreOnly(_dir));
    }

    [Fact]
    public void IgnoreWithOtherContent_IsNotMountable()
    {
        Directory.CreateDirectory(_dir);
        File.Create(Path.Combine(_dir, ".ignore")).Dispose();
        File.Create(Path.Combine(_dir, "other.txt")).Dispose();
        Assert.False(FuseMountService.IsEmptyOrIgnoreOnly(_dir));
    }
}
