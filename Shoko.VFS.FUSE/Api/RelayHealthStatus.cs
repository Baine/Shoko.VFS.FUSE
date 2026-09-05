using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Api;

public enum RelayRuntimeState
{
    Disabled,
    WaitingForServer,
    Reconciling,
    Healthy,
    Degraded,
    Stopped,
}

public enum RelayMountStatusState
{
    Mounted,
    Blocked,
    Failed,
}

/// <summary>Path-free Relay runtime health information.</summary>
public sealed record RelayMountStatus(
    int ManagedFolderId,
    RelayMountRootKind RootKind,
    RelayMountStatusState State,
    IReadOnlyList<string> ReasonCodes);

/// <summary>Path-free Relay runtime health information.</summary>
public sealed record RelayHealthStatus(
    RelayRuntimeState State,
    IReadOnlyList<RelayMountStatus> Mounts,
    IReadOnlyList<string> CapabilityCodes,
    int DesiredMountCount,
    int ActiveMountCount,
    int FailedMountCount);
