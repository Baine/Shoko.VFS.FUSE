using Newtonsoft.Json;
using Shoko.VFS.FUSE.Host.Config;

namespace Shoko.VFS.FUSE.Tests.Host;

public sealed class HostConfigTests
{
    [Fact]
    public void NfsExportSettingsDefaultOffAndBindFromJson()
    {
        var config = new HostConfig();
        Assert.False(config.InstallNfsExports);
        Assert.Equal("*", config.NfsExportClients);

        var loaded = JsonConvert.DeserializeObject<HostConfig>(
            """{ "InstallNfsExports": true, "NfsExportClients": "192.168.178.20 10.0.0.5" }""");
        Assert.True(loaded!.InstallNfsExports);
        Assert.Equal("192.168.178.20 10.0.0.5", loaded.NfsExportClients);
    }
}
