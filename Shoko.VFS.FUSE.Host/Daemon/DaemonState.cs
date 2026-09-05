using Shoko.VFS.FUSE.Host.Daemon;

namespace Shoko.VFS.FUSE.Host.Daemon;

/// <summary>
/// Overall daemon state, mirroring the plugin's <c>RelayRuntimeState</c> semantics.
/// </summary>
public enum DaemonState
{
    /// <summary>Daemon has not been fully started yet.</summary>
    Starting,

    /// <summary>Waiting for the Shoko server to become reachable.</summary>
    WaitingForServer,

    /// <summary>Reconcile is in progress.</summary>
    Reconciling,

    /// <summary>All mounts are healthy.</summary>
    Healthy,

    /// <summary>Some mounts have errors or the server is unreachable.</summary>
    Degraded,

    /// <summary>Daemon is shutting down.</summary>
    Stopped,
}

/// <summary>
/// Global daemon state container, updated by the reconciler on each reconcile cycle
/// and in response to SignalR events. Used by the health endpoint.
/// </summary>
public sealed class DaemonStateTracker
{
    public DaemonState State { get; internal set; } = DaemonState.Starting;
    public IReadOnlyList<DaemonMountStatus> Mounts { get; internal set; } = [];
    public DateTime? LastReconcileAt { get; internal set; }
    public string? LastReconcileError { get; internal set; }
    public int ReconcileCount { get; internal set; }
    public string? SignalRState { get; internal set; }

    /// <summary>Whether the Shoko server is currently reachable (per the availability monitor).</summary>
    public bool? ServerUp { get; internal set; }

    /// <summary>Whether the Shoko server API is responding to authenticated requests.</summary>
    public bool? ServerReady { get; internal set; }

    /// <summary>Last successful probe of the server (UTC).</summary>
    public DateTime? LastServerProbeAt { get; internal set; }

    /// <summary>Last probe error message, if any.</summary>
    public string? LastServerProbeError { get; internal set; }
}