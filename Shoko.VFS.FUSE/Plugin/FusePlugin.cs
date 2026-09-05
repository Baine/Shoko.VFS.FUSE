using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Plugin;

public sealed class FusePlugin : IPlugin
{
    public static readonly Guid StaticID = new("c6b59b26-9fd1-4220-ab6d-0b2d92e19022");

    public Guid ID => StaticID;

    public string Name => "Shoko.VFS.FUSE";

    public string? Description => "Linux FUSE virtual filesystem for Shoko.";
}

public sealed class FusePluginServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton(provider =>
            provider.GetRequiredService<IConfigurationService>().CreateProvider<FusePluginConfiguration>());
        serviceCollection.AddSingleton<RelayRuntime>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<RelayRuntime>());
        serviceCollection.AddSingleton<FinRuntime>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<FinRuntime>());
    }
}
