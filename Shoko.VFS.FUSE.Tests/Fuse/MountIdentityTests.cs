using FuseDotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Tests.Fuse;

public sealed class MountIdentityTests
{
    [Fact]
    public void MountInfoParserDecodesOctalEscapes()
    {
        var line = "42 35 0:31 / /tmp/Relay\\040Root\\011One rw,relatime - fuse.shoko-vfs Shoko.VFS.FUSE:token rw";

        Assert.True(FuseMountService.TryParseMountInfo(line, out var entry));
        Assert.Equal(42, entry.MountId);
        Assert.Equal("/tmp/Relay Root\tOne", entry.CanonicalPath);
        Assert.Equal("fuse.shoko-vfs", entry.FileSystemType);
        Assert.Equal("Shoko.VFS.FUSE:token", entry.SourceToken);
    }

    [Fact]
    public void ForeignOrReplacementMountDoesNotMatchIdentity()
    {
        var expected = new MountIdentity("/tmp/Relay", 42, "fuse.shoko-vfs", "Shoko.VFS.FUSE:token");
        var replacement = new MountIdentity("/tmp/Relay", 43, "fuse.shoko-vfs", "Shoko.VFS.FUSE:replacement");
        var foreign = new MountInfoEntry("/tmp/Relay", 43, "fuse.shoko-vfs", "foreign");

        Assert.False(FuseMountService.MountIdentityMatches(expected, replacement));
        Assert.False(FuseMountService.IsExpectedMount(foreign, "/tmp/Relay", expected.SourceToken));
    }

    [Fact]
    public void CancellationCleanupCanAdoptOnlyThisStartIdentity()
    {
        var line = "42 35 0:31 / /tmp/Relay rw,relatime - fuse.shoko-vfs Shoko.VFS.FUSE:token rw";
        Assert.True(FuseMountService.TryParseMountInfo(line, out var matching));
        Assert.True(FuseMountService.TryAdoptMountIdentity([matching], "/tmp/Relay", "Shoko.VFS.FUSE:token", out var identity));
        Assert.Equal(matching.Identity, identity);

        var foreign = matching with { SourceToken = "foreign" };
        Assert.False(FuseMountService.TryAdoptMountIdentity([foreign], "/tmp/Relay", "Shoko.VFS.FUSE:token", out _));
    }

    [Fact]
    public async Task VanishedOwnedMount_DisposesStillRunningDaemon()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var mountPoint = Path.Combine(Path.GetTempPath(), "shoko-vanished-" + Guid.NewGuid().ToString("N"));
        var fuse = new FuseService(
            new ShokoFuseFileSystem(new InMemoryResolver(), NullLogger.Instance),
            ["shoko-vfs-fuse", mountPoint]);
        var service = new FuseMountService(
            new MountOptions { Name = "vanished", MountPoint = mountPoint },
            new InMemoryResolver(),
            NullLogger.Instance,
            probeFuseDevice: null,
            probeFusermount: null,
            probeFuseRunning: () => true,
            fuseForTesting: fuse);

        await service.StopAsync();

        Assert.True(fuse.IsDisposed);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void UnexpectedStopSubscribersAreContainedIndividually()
    {
        var service = new FuseMountService(
            new MountOptions { Name = "test", MountPoint = "/tmp/test" },
            new InMemoryResolver(),
            NullLogger.Instance);
        int called = 0;
        service.UnexpectedStopped += () => throw new InvalidOperationException("subscriber failure");
        service.UnexpectedStopped += () => called++;

        service.NotifyUnexpectedStopped();

        Assert.Equal(1, called);
    }

    [Fact]
    public async Task BoundedProbeHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = FuseMountService.RunBoundedProbeAsync(
            () =>
            {
                started.SetResult();
                release.Task.GetAwaiter().GetResult();
                return true;
            },
            cancellation.Token,
            TimeSpan.FromMinutes(1));

        await started.Task;
        cancellation.Cancel();
        var completed = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(1)));
        release.SetResult();

        Assert.Same(probe, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }
}
