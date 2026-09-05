using Microsoft.AspNetCore.Mvc;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Api;

/// <summary>Exposes path-free Relay runtime health.</summary>
[ApiController]
[Route("/api/plugin/Shoko.VFS.FUSE")]
public sealed class RelayHealthController(RelayRuntime runtime) : ControllerBase
{
    [HttpGet("health")]
    public ActionResult<RelayHealthStatus> Get() => Ok(runtime.Status);
}
