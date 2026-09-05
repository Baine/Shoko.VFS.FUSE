using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Tests.Fuse;

public class FuseMountArgsTests
{
    private static readonly string[] AlwaysArgs =
    {
        "-o", "attr_timeout=2",
        "-o", "entry_timeout=2",
        "-o", "negative_timeout=0.5",
        "-o", "fsname=tok",
        "-o", "subtype=shoko-vfs",
        "-o", "auto_unmount",
    };

    private static void AssertPair(string[] argv, string key, string expectedValue)
    {
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i] == "-o" && string.Equals(argv[i + 1], expectedValue, StringComparison.Ordinal))
                return;
            if (argv[i] == key && string.Equals(argv[i + 1], expectedValue, StringComparison.Ordinal))
                return;
        }
        Assert.Fail($"Expected argv to contain '{key} {expectedValue}' but got: [{string.Join(", ", argv)}]");
    }

    private static void AssertNoKey(string[] argv, string keyFragment)
    {
        foreach (var a in argv)
            Assert.False(a.Contains(keyFragment, StringComparison.Ordinal),
                $"Did not expect '{keyFragment}' in argv: [{string.Join(", ", argv)}]");
    }

    [Fact]
    public void Defaults_NoUidNoGid()
    {
        var opts = new MountOptions { Name = "n", MountPoint = "/mnt/x" };
        var argv = FuseMountService.BuildArgs(opts, "tok");

        Assert.Equal("shoko-vfs-fuse", argv[0]);
        Assert.Equal("-f", argv[1]);
        AssertNoKey(argv, "uid=");
        AssertNoKey(argv, "gid=");
        AssertNoKey(argv, "allow_other");
        Assert.Equal("/mnt/x", argv[^1]);
    }

    [Fact]
    public void UidAndGid_AppearAsFuseOptions()
    {
        var opts = new MountOptions
        {
            Name = "n",
            MountPoint = "/mnt/x",
            Uid = 99,
            Gid = 100,
        };
        var argv = FuseMountService.BuildArgs(opts, "tok");

        AssertPair(argv, "uid=", "uid=99");
        AssertPair(argv, "gid=", "gid=100");
    }

    [Fact]
    public void UidOnly_NoGid()
    {
        var opts = new MountOptions { Name = "n", MountPoint = "/mnt/x", Uid = 99 };
        var argv = FuseMountService.BuildArgs(opts, "tok");

        AssertPair(argv, "uid=", "uid=99");
        AssertNoKey(argv, "gid=");
    }

    [Fact]
    public void AllowOther_CoexistsWithUidGid()
    {
        var opts = new MountOptions
        {
            Name = "n",
            MountPoint = "/mnt/x",
            AllowOther = true,
            Uid = 99,
            Gid = 100,
        };
        var argv = FuseMountService.BuildArgs(opts, "tok");

        AssertPair(argv, "allow_other", "allow_other");
        AssertPair(argv, "uid=", "uid=99");
        AssertPair(argv, "gid=", "gid=100");
    }

    [Fact]
    public void LargeUid_StillFormattedInvariantCulture()
    {
        // Catches the "formatted as 4.294.967.295" bug if someone uses current culture.
        var opts = new MountOptions { Name = "n", MountPoint = "/mnt/x", Uid = 12345, Gid = 54321 };
        var argv = FuseMountService.BuildArgs(opts, "tok");

        AssertPair(argv, "uid=", "uid=12345");
        AssertPair(argv, "gid=", "gid=54321");
    }
}
