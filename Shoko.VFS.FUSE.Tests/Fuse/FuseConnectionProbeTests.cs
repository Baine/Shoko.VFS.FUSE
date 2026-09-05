using Shoko.VFS.FUSE.Fuse;

namespace Shoko.VFS.FUSE.Tests.Fuse;

public sealed class FuseConnectionProbeTests
{
    private const string MountPoint = "/tmp/shoko-connection-probe";
    private const string SourceToken = "Shoko.VFS.FUSE:connection-probe";
    private static readonly MountIdentity Identity = new(MountPoint, 42, "fuse.shoko-vfs", SourceToken);
    private static readonly IReadOnlyList<MountInfoEntry> MountInfo =
        [new MountInfoEntry(MountPoint, 42, "fuse.shoko-vfs", SourceToken)];

    [Fact]
    public async Task DelayedProbeWithinDeadlineUsesOneProbe()
    {
        int probeCount = 0;

        await FuseMountService.WaitForConnectionAsync(
            MountPoint,
            Identity,
            SourceToken,
            _ => Task.FromResult(MountInfo),
            async (_, ct) =>
            {
                probeCount++;
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                return true;
            },
            () => false,
            CancellationToken.None,
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task NeverCompletingProbeTimesOutWithoutRetry()
    {
        int probeCount = 0;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<FuseStartException>(() => FuseMountService.WaitForConnectionAsync(
            MountPoint,
            Identity,
            SourceToken,
            _ => Task.FromResult(MountInfo),
            (_, _) =>
            {
                probeCount++;
                return pending.Task;
            },
            () => false,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(100)));

        Assert.Equal(FuseStartReason.MountNotResponsive, exception.Reason);
        Assert.Equal(1, probeCount);
        pending.TrySetResult(false);
    }

    [Fact]
    public async Task FastFalseProbeRetriesAndThenSucceeds()
    {
        int probeCount = 0;

        await FuseMountService.WaitForConnectionAsync(
            MountPoint,
            Identity,
            SourceToken,
            _ => Task.FromResult(MountInfo),
            (_, _) => Task.FromResult(++probeCount > 1),
            () => false,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));

        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CancellationStopsAwaitWithoutLaunchingAnotherProbe()
    {
        int probeCount = 0;
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = FuseMountService.WaitForConnectionAsync(
            MountPoint,
            Identity,
            SourceToken,
            _ => Task.FromResult(MountInfo),
            (_, ct) =>
            {
                probeCount++;
                started.SetResult();
                return pending.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            },
            () => false,
            cancellation.Token,
            TimeSpan.FromSeconds(10));

        await started.Task;
        cancellation.Cancel();

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(wait, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, probeCount);
        pending.TrySetResult(false);
    }
}
