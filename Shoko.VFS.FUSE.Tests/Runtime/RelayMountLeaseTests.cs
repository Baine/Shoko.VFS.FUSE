using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Tests.Runtime;

public sealed class RelayMountLeaseTests
{
    [Fact]
    public async Task FailedStopRemainsRetryable()
    {
        int attempts = 0;
        bool fail = true;
        var target = new RelayMountTarget(
            1,
            "Library",
            "/tmp/library",
            "!Relay",
            "/tmp/library/!Relay",
            RelayMountRootKind.Tv,
            new PathResolverOptions());
        var lease = new RelayMountLease(
            target,
            () => { },
            _ =>
            {
                attempts++;
                if (fail)
                    throw new InvalidOperationException("stop failure");
                return ValueTask.CompletedTask;
            },
            () => { });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
        fail = false;
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(2, attempts);
    }
}
