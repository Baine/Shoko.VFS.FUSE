using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Fuse;

namespace Shoko.VFS.FUSE.Tests.Fuse;

public sealed class FuseNativeLibraryResolverTests
{
    [Fact]
    public void ResolveUsesSafeCandidateOrderAndRetainsLoadedHandle()
    {
        var candidates = new List<string>();
        int registrations = 0;
        var resolver = new FuseNativeLibraryResolver(
            isLinux: () => true,
            loader: (string name, Assembly _, DllImportSearchPath? _, out IntPtr handle) =>
            {
                candidates.Add(name);
                handle = name == "libfuse3.so.4" ? new IntPtr(4) : IntPtr.Zero;
                return handle != IntPtr.Zero;
            },
            register: (_, _) => registrations++);

        var assembly = typeof(FuseNativeLibraryResolverTests).Assembly;
        resolver.Register(assembly);
        resolver.Register(assembly);
        Assert.Equal(new IntPtr(4), resolver.Resolve("fuse3", assembly, null));
        Assert.Equal(new IntPtr(4), resolver.Resolve("fuse3", assembly, null));
        Assert.Equal(["libfuse3.so.3", "libfuse3.so.4"], candidates);
        Assert.Equal(1, registrations);
    }

    [Fact]
    public void ResolveFallsBackToUnversionedCandidate()
    {
        var candidates = new List<string>();
        var resolver = new FuseNativeLibraryResolver(
            isLinux: () => true,
            loader: (string name, Assembly _, DllImportSearchPath? _, out IntPtr handle) =>
            {
                candidates.Add(name);
                handle = name == "libfuse3.so" ? new IntPtr(1) : IntPtr.Zero;
                return handle != IntPtr.Zero;
            });

        Assert.Equal(new IntPtr(1), resolver.Resolve("fuse3", typeof(FuseNativeLibraryResolverTests).Assembly, null));
        Assert.Equal(["libfuse3.so.3", "libfuse3.so.4", "libfuse3.so"], candidates);
    }

    [Fact]
    public void ResolveIgnoresOtherLibrariesAndNonLinux()
    {
        int loads = 0;
        var resolver = new FuseNativeLibraryResolver(
            isLinux: () => false,
            loader: (string _, Assembly _, DllImportSearchPath? _, out IntPtr handle) =>
            {
                loads++;
                handle = new IntPtr(1);
                return true;
            });

        Assert.Equal(IntPtr.Zero, resolver.Resolve("fuse3", typeof(FuseNativeLibraryResolverTests).Assembly, null));
        Assert.Equal(IntPtr.Zero, resolver.Resolve("libc", typeof(FuseNativeLibraryResolverTests).Assembly, null));
        Assert.Equal(0, loads);

        var linuxResolver = new FuseNativeLibraryResolver(
            isLinux: () => true,
            loader: (string _, Assembly _, DllImportSearchPath? _, out IntPtr handle) =>
            {
                loads++;
                handle = new IntPtr(1);
                return true;
            });
        Assert.Equal(IntPtr.Zero, linuxResolver.Resolve("libc", typeof(FuseNativeLibraryResolverTests).Assembly, null));
        Assert.Equal(0, loads);
    }

    [Fact]
    public void ExistingResolverIsClassifiedAndRegistrationIsNotRetried()
    {
        int registrations = 0;
        var resolver = new FuseNativeLibraryResolver(
            isLinux: () => true,
            loader: (string _, Assembly _, DllImportSearchPath? _, out IntPtr handle) =>
            {
                handle = IntPtr.Zero;
                return false;
            },
            register: (_, _) =>
            {
                registrations++;
                throw new InvalidOperationException("a resolver already exists");
            });

        Assert.Throws<FuseLibraryUnavailableException>(() => resolver.Register(typeof(FuseNativeLibraryResolverTests).Assembly));
        Assert.Throws<FuseLibraryUnavailableException>(() => resolver.Register(typeof(FuseNativeLibraryResolverTests).Assembly));
        Assert.Equal(1, registrations);
    }

    [Fact]
    public async Task ResolverRegistrationFailureIsClassifiedWithoutNativeEntryPoint()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var service = new FuseMountService(
            new MountOptions { Name = "resolver-failure", MountPoint = "/tmp/resolver-failure" },
            new InMemoryResolver(),
            NullLogger.Instance,
            ensureNativeResolver: () => throw new FuseLibraryUnavailableException());

        var exception = await Assert.ThrowsAsync<FuseStartException>(() => service.StartAsync(CancellationToken.None));

        Assert.Equal(FuseStartReason.FuseLibraryUnavailable, exception.Reason);
    }

    [Fact]
    public async Task MountRegistrationRunsBeforeAnyFuseConstructionBoundary()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "shoko-resolver-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "stale-file"), "legacy");
        int registrations = 0;
        try
        {
            var service = new FuseMountService(
                new MountOptions { Name = "resolver-order", MountPoint = root },
                new InMemoryResolver(),
                NullLogger.Instance,
                ensureNativeResolver: () => registrations++);

            var exception = await Assert.ThrowsAsync<FuseStartException>(() => service.StartAsync(CancellationToken.None));

            Assert.Equal(FuseStartReason.MountTargetBlocked, exception.Reason);
            Assert.Equal(1, registrations);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
