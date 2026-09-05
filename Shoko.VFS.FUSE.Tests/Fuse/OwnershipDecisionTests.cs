using Shoko.VFS.FUSE.Fuse;

namespace Shoko.VFS.FUSE.Tests.Fuse;

/// <summary>
/// Pure unit tests for <see cref="OwnershipDecision.Evaluate"/>.
/// No kernel or filesystem interaction required.
/// </summary>
public class OwnershipDecisionTests
{
    [Fact]
    public void MissingParent_Blocks()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: false,
            targetExists: false,
            targetIsEmptyDir: false,
            targetIsFile: false,
            targetIsMounted: false);

        Assert.True(d.Block);
        Assert.False(d.Create);
        Assert.Equal(FuseStartReason.MountAccessDenied, d.BlockReason);
    }

    [Fact]
    public void MissingTarget_Creates()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: false,
            targetIsEmptyDir: false,
            targetIsFile: false,
            targetIsMounted: false);

        Assert.True(d.Create);
        Assert.False(d.Block);
        Assert.Null(d.BlockReason);
    }

    [Fact]
    public void EmptyDirectory_Accepts()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: true,
            targetIsEmptyDir: true,
            targetIsFile: false,
            targetIsMounted: false);

        Assert.True(d.AcceptEmpty);
        Assert.False(d.Create);
        Assert.False(d.Block);
    }

    [Fact]
    public void NonEmptyDirectory_Blocks()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: true,
            targetIsEmptyDir: false,
            targetIsFile: false,
            targetIsMounted: false);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountTargetBlocked, d.BlockReason);
    }

    [Fact]
    public void TargetIsFile_Blocks()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: false,
            targetIsEmptyDir: false,
            targetIsFile: true,
            targetIsMounted: false);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountTargetBlocked, d.BlockReason);
    }

    [Fact]
    public void AlreadyMountedUnknown_Blocks()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: true,
            targetIsEmptyDir: true,
            targetIsFile: false,
            targetIsMounted: true);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountAlreadyMountedUnknown, d.BlockReason);
    }

    [Fact]
    public void MountedNonEmpty_BlocksWithMountPriority()
    {
        // When both mounted and non-empty, mounted takes priority (checked first)
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: true,
            targetIsEmptyDir: false,
            targetIsFile: false,
            targetIsMounted: true);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountAlreadyMountedUnknown, d.BlockReason);
    }

    [Fact]
    public void MountedFile_BlocksWithMountPriority()
    {
        var d = OwnershipDecision.Evaluate(
            parentExists: true,
            targetExists: false,
            targetIsEmptyDir: false,
            targetIsFile: true,
            targetIsMounted: true);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountAlreadyMountedUnknown, d.BlockReason);
    }

    [Fact]
    public void MissingParentAndMounted_BlocksParentFirst()
    {
        // parent check is first, so MountAccessDenied wins
        var d = OwnershipDecision.Evaluate(
            parentExists: false,
            targetExists: false,
            targetIsEmptyDir: false,
            targetIsFile: false,
            targetIsMounted: true);

        Assert.True(d.Block);
        Assert.Equal(FuseStartReason.MountAccessDenied, d.BlockReason);
    }
}
