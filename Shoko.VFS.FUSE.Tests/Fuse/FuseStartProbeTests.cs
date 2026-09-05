using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Tests.Fuse;

/// <summary>
/// Tests that probe seams produce classified <see cref="FuseStartException"/> failures
/// without touching the kernel or real FUSE.
/// </summary>
public class FuseStartProbeTests
{
    [Fact]
    public async Task MissingDevFuse_ThrowsFuseDeviceUnavailable()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = new MountOptions { Name = "probe-devfuse", MountPoint = Path.Combine(tmp, "mnt") };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
                probeFuseDevice: () => false,
                probeFusermount: () => true);

            var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
            Assert.Equal(FuseStartReason.FuseDeviceUnavailable, ex.Reason);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFusermount_ThrowsFuseMountHelperUnavailable()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = new MountOptions { Name = "probe-fusermount", MountPoint = Path.Combine(tmp, "mnt") };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
                probeFuseDevice: () => true,
                probeFusermount: () => false);

            var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
            Assert.Equal(FuseStartReason.FuseMountHelperUnavailable, ex.Reason);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task NonLinux_ThrowsUnsupportedPlatform()
    {
        if (OperatingSystem.IsLinux())
            return; // skip: requires non-Linux to trigger platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = new MountOptions { Name = "probe-platform", MountPoint = Path.Combine(tmp, "mnt") };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance);

            var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
            Assert.Equal(FuseStartReason.UnsupportedPlatform, ex.Reason);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task NonEmptyDir_ThrowsMountTargetBlocked()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var mnt = Path.Combine(tmp, "mnt");
        Directory.CreateDirectory(mnt);
        await File.WriteAllTextAsync(Path.Combine(mnt, "stale-file.txt"), "leftover");
        try
        {
            var opts = new MountOptions { Name = "probe-nonempty", MountPoint = mnt };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
                probeFuseDevice: () => true,
                probeFusermount: () => true);

            var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
            Assert.Equal(FuseStartReason.MountTargetBlocked, ex.Reason);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task FileTarget_ThrowsMountTargetBlocked()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var fileTarget = Path.Combine(tmp, "file-not-dir");
        await File.WriteAllTextAsync(fileTarget, "not a dir");
        try
        {
            var opts = new MountOptions { Name = "probe-filetarget", MountPoint = fileTarget };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
                probeFuseDevice: () => true,
                probeFusermount: () => true);

            var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
            Assert.Equal(FuseStartReason.MountTargetBlocked, ex.Reason);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task MissingParent_ThrowsMountAccessDenied()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var opts = new MountOptions
        {
            Name = "probe-noparent",
            MountPoint = "/tmp/shoko-nonexistent-parent-absolutely-fake/mnt"
        };
        var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
            probeFuseDevice: () => true,
            probeFusermount: () => true);

        var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
        Assert.Equal(FuseStartReason.MountAccessDenied, ex.Reason);
    }

    [Fact]
    public async Task TimedOutTargetExistenceProbeFailsClosed()
    {
        if (!OperatingSystem.IsLinux())
            return; // skip: requires Linux for platform check

        var tmp = Path.Combine(Path.GetTempPath(), "shoko-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var mnt = Path.Combine(tmp, "mnt");
        Directory.CreateDirectory(tmp);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var opts = new MountOptions { Name = "probe-timeout", MountPoint = mnt };
            var svc = new FuseMountService(opts, new InMemoryResolver(), NullLogger.Instance,
                probeFuseDevice: () => true,
                probeFusermount: () => true,
                probeDirectoryExists: path =>
                {
                    if (path == mnt)
                    {
                        started.SetResult();
                        release.Task.GetAwaiter().GetResult();
                        return false;
                    }

                    return Directory.Exists(path);
                });

            var start = svc.StartAsync(CancellationToken.None);
            await started.Task;
            var completed = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(start, completed);
            var ex = await Assert.ThrowsAsync<FuseStartException>(() => start);
            Assert.Equal(FuseStartReason.MountAccessDenied, ex.Reason);
            Assert.False(Directory.Exists(mnt));
            release.SetResult();
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
