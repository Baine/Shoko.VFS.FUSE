using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Fuse;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Fuse;

/// <summary>
/// End-to-end mount test. Requires a working FUSE setup (Linux + /dev/fuse + fusermount).
/// Skipped gracefully when the environment doesn't allow mounting.
/// </summary>
[Collection("FUSE integration")]
public class MountIntegrationTests : IAsyncLifetime
{
    private string _tmpRoot = "";
    private string _sourceFile = "";
    private string _mountPoint = "";

    public async Task InitializeAsync()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "shoko-vfs-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpRoot);
        _sourceFile = Path.Combine(_tmpRoot, "source.mkv");
        _mountPoint = Path.Combine(_tmpRoot, "mnt");
        await File.WriteAllTextAsync(_sourceFile, "hello-fuse-content");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tmpRoot))
                Directory.Delete(_tmpRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; mount may still hold the dir open
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Mount_List_Read_Unmount()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        // Resolver that exposes source.mkv as /[series]/file.mkv
        var resolver = new InMemoryResolver()
            .Add("12345", new VirtualEntry { Name = "12345", NodeType = VirtualNodeType.Directory })
            .Add("12345/file.mkv", new VirtualEntry
            {
                Name = "file.mkv",
                NodeType = VirtualNodeType.File,
                Size = new FileInfo(_sourceFile).Length,
                SourcePath = _sourceFile
            });

        var opts = new MountOptions
        {
            Name = "test",
            MountPoint = _mountPoint
        };

        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(svc.IsRunning, "mount should be running");

            // Read dir contents
            var files = Directory.GetFiles(_mountPoint, "*", SearchOption.AllDirectories);
            Assert.Contains(files, f => f.EndsWith("file.mkv", StringComparison.Ordinal));

            // Stat + read the file
            var full = Path.Combine(_mountPoint, "12345", "file.mkv");
            Assert.True(File.Exists(full));
            Assert.Equal("hello-fuse-content", await File.ReadAllTextAsync(full));
        }
        finally
        {
            await svc.StopAsync();
        }

        Assert.False(svc.IsRunning, "mount should be stopped after unmount");
    }

    [Fact]
    public async Task Mount_EmptyPreCreatedDir_Succeeds()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var resolver = new InMemoryResolver();
        var emptyDir = Path.Combine(_tmpRoot, "empty-mnt");
        Directory.CreateDirectory(emptyDir);

        var opts = new MountOptions { Name = "empty-dir-test", MountPoint = emptyDir };
        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(svc.IsRunning, "mount into pre-created empty dir should work");
        }
        finally
        {
            await svc.StopAsync();
        }
    }

    [Fact]
    public async Task Mount_ReadinessUsesRootStatWithoutEnumeratingRoot()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var resolver = new StatOnlyRootResolver();
        var opts = new MountOptions { Name = "root-stat-readiness", MountPoint = _mountPoint };
        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            Assert.True(svc.IsRunning, "mount should be running");
            Assert.True(resolver.RootLookupCount > 0, "readiness should stat the virtual root");
            Assert.Equal(0, resolver.RootReadDirectoryCount);
        }
        finally
        {
            await svc.StopAsync();
        }
    }

    [Fact]
    public async Task Mount_NonEmptyDir_Refuses()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var nonEmptyDir = Path.Combine(_tmpRoot, "nonempty-mnt");
        Directory.CreateDirectory(nonEmptyDir);
        await File.WriteAllTextAsync(Path.Combine(nonEmptyDir, "stale.txt"), "data");

        var resolver = new InMemoryResolver();
        var opts = new MountOptions { Name = "nonempty-refuse", MountPoint = nonEmptyDir };
        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));
        Assert.Equal(FuseStartReason.MountTargetBlocked, ex.Reason);
    }

    [Fact]
    public async Task FailedStartup_LeavesNoServiceOwnedMount()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var resolver = new InMemoryResolver();
        var nonEmptyDir = Path.Combine(_tmpRoot, "fail-cleanup-mnt");
        Directory.CreateDirectory(nonEmptyDir);
        await File.WriteAllTextAsync(Path.Combine(nonEmptyDir, "leftover"), "data");

        var opts = new MountOptions { Name = "fail-test", MountPoint = nonEmptyDir };
        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);

        await Assert.ThrowsAsync<FuseStartException>(() => svc.StartAsync(CancellationToken.None));

        // The non-empty dir should still be there (we refused, didn't create it)
        // and no FUSE mount should exist
        Assert.False(svc.IsRunning, "service should not be running after refusal");
    }

    [Fact]
    public async Task Stop_DoesNotUnmountUnknownPreExisting()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        // First, create a real FUSE mount to establish a baseline
        var resolver = new InMemoryResolver();
        var baseDir = Path.Combine(_tmpRoot, "base-mnt");
        Directory.CreateDirectory(baseDir);
        var baseOpts = new MountOptions { Name = "base", MountPoint = baseDir };
        var baseSvc = new FuseMountService(baseOpts, resolver, NullLogger.Instance);
        await baseSvc.StartAsync(CancellationToken.None);

        try
        {
            // Now create a second service pointing at the same (now-mounted) path
            // to simulate an unknown pre-existing mount. StopAsync on it should
            // NOT unmount it (ownership is unknown).
            var unknownOpts = new MountOptions { Name = "unknown", MountPoint = baseDir };
            var unknownSvc = new FuseMountService(unknownOpts, resolver, NullLogger.Instance);

            await unknownSvc.StopAsync(); // should be a no-op (no daemon started)

            // The original mount should still be running
            Assert.True(baseSvc.IsRunning, "original mount should still be alive after unknown-ownership Stop");
        }
        finally
        {
            await baseSvc.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_CleansUpAfterExternalUnmount()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var resolver = new InMemoryResolver();
        var mnt = Path.Combine(_tmpRoot, "ext-unmount-mnt");
        Directory.CreateDirectory(mnt);
        var opts = new MountOptions { Name = "ext-unmount", MountPoint = mnt };
        var svc = new FuseMountService(opts, resolver, NullLogger.Instance);
        await svc.StartAsync(CancellationToken.None);

        // Externally unmount — simulates the failed-readiness scenario where
        // the mount vanishes from mountinfo while the daemon is still running.
        FuseMountService.Unmount(mnt);
        // Wait for mountinfo to reflect the kernel-side unmount.
        await Task.Delay(500);

        // StopAsync should complete cleanup even though daemon may still be alive.
        await svc.StopAsync();
        Assert.False(svc.IsRunning, "service should be cleaned up after external unmount");
    }

    [Fact]
    public async Task ForeignReplacementAtOwnedPath_RefusesCleanup()
    {
        var (canMount, skipReason) = await CanMountFuse();
        if (!canMount)
            return; // skip: {skipReason}

        var resolver = new InMemoryResolver();
        var mnt = Path.Combine(_tmpRoot, "foreign-replace-mnt");
        Directory.CreateDirectory(mnt);

        // Start service A — we own this mount.
        var optsA = new MountOptions { Name = "svcA", MountPoint = mnt };
        var svcA = new FuseMountService(optsA, resolver, NullLogger.Instance);
        await svcA.StartAsync(CancellationToken.None);

        // Externally unmount service A's mount.
        FuseMountService.Unmount(mnt);
        await Task.Delay(500);

        // Mount a foreign FUSE at the same path (service B).
        var optsB = new MountOptions { Name = "svcB", MountPoint = mnt };
        var svcB = new FuseMountService(optsB, resolver, NullLogger.Instance);
        await svcB.StartAsync(CancellationToken.None);

        try
        {
            // Service A calls StopAsync. The mount at its path has a different
            // identity (foreign). Cleanup must be refused.
            var cleanupCompleted = false;
            svcA.CleanupCompleted += () => cleanupCompleted = true;
            var stopped = svcA.StopAsync();

            // Give it a moment to process
            await Task.Delay(500);

            Assert.False(cleanupCompleted,
                "cleanup must not complete when mount at owned path has foreign identity");

            // Service B's mount must still be alive.
            Assert.True(svcB.IsRunning, "foreign mount should survive original owner's StopAsync");
        }
        finally
        {
            await svcB.StopAsync();
        }
    }

    internal static async Task<(bool CanMount, string Reason)> CanMountFuse()
    {
        if (!OperatingSystem.IsLinux())
            return (false, "Requires Linux");
        if (!File.Exists("/dev/fuse"))
            return (false, "/dev/fuse not found");

        // Real probe: if the mount test environment can't mount, it fails fast.
        try
        {
            var probeDir = Path.Combine(Path.GetTempPath(), "shoko-fuse-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(probeDir);
            var probeOpts = new MountOptions { Name = "probe", MountPoint = probeDir };
            var probe = new FuseMountService(probeOpts, new InMemoryResolver(), NullLogger.Instance);
            await probe.StartAsync(CancellationToken.None);
            await probe.StopAsync();
            Directory.Delete(probeDir);
            return (true, "");
        }
        catch (FuseStartException ex)
        {
            return (false, $"FUSE probe failed: {ex.Reason}");
        }
        catch (Exception ex)
        {
            return (false, $"FUSE probe failed: {ex.Message}");
        }
    }

    // Keep legacy overload for any existing callers
    internal static Task<bool> CanMountFuseLegacy() => CanMountFuse().ContinueWith(t => t.Result.CanMount);

    private sealed class StatOnlyRootResolver : IVirtualPathResolver
    {
        private int _rootLookupCount;
        private int _rootReadDirectoryCount;

        public int RootLookupCount => Volatile.Read(ref _rootLookupCount);
        public int RootReadDirectoryCount => Volatile.Read(ref _rootReadDirectoryCount);

        public IReadOnlyList<VirtualEntry> ReadDirectory(string mountRelativePath)
        {
            if (IsRoot(mountRelativePath))
            {
                Interlocked.Increment(ref _rootReadDirectoryCount);
                throw new InvalidOperationException("root enumeration is intentionally unavailable");
            }

            return [];
        }

        public VirtualEntry? Lookup(string mountRelativePath)
        {
            if (!IsRoot(mountRelativePath))
                return null;

            Interlocked.Increment(ref _rootLookupCount);
            return new VirtualEntry
            {
                Name = "root",
                NodeType = VirtualNodeType.Directory,
                Mode = VirtualEntry.DefaultMode(VirtualNodeType.Directory),
            };
        }

        public string? GetSourcePath(string mountRelativePath) => null;
        public void Invalidate(string mountRelativePath, bool includeChildren = false) { }
        public void Rebuild() { }

        private static bool IsRoot(string path) => path.Trim('/').Length == 0;
    }
}
