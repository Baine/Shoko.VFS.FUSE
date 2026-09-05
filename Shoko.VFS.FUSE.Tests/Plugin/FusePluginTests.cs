using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Plugin;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Tests.Plugin;

public class FusePluginTests
{
    [Fact]
    public void Assembly_ExportsExactlyOnePluginAndRegistration()
    {
        var exportedTypes = typeof(FusePlugin).Assembly.GetExportedTypes();

        var pluginTypes = exportedTypes
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IPlugin).IsAssignableFrom(type))
            .ToArray();
        var registrationTypes = exportedTypes
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IPluginServiceRegistration).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal([typeof(FusePlugin)], pluginTypes);
        Assert.Equal([typeof(FusePluginServiceRegistration)], registrationTypes);
    }

    [Fact]
    public void Plugin_HasFixedIdentityAndAssemblyMetadata()
    {
        var assembly = typeof(FusePlugin).Assembly;
        var plugin = Assert.IsAssignableFrom<IPlugin>(Activator.CreateInstance(typeof(FusePlugin)));
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();

        Assert.NotEqual(Guid.Empty, plugin.ID);
        Assert.Equal(FusePlugin.StaticID, plugin.ID);
        Assert.Equal(plugin.ID.ToString(), metadata.Single(attribute => attribute.Key == "PackageID").Value);
        Assert.Equal("Shoko.VFS.FUSE", metadata.Single(attribute => attribute.Key == "PackageName").Value);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Single(attribute => attribute.Key == "PackageOverview").Value));
    }

    [Fact]
    public void Registration_HasExactSignatureAndRegistersRuntimes()
    {
        var method = Assert.Single(typeof(FusePluginServiceRegistration).GetMethods(BindingFlags.Public | BindingFlags.Static));
        var parameters = method.GetParameters();

        Assert.Equal(nameof(IPluginServiceRegistration.RegisterServices), method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IServiceCollection), parameters[0].ParameterType);
        Assert.Equal(typeof(IApplicationPaths), parameters[1].ParameterType);

        var services = new ServiceCollection();
        method.Invoke(null, new object?[] { services, null });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ConfigurationProvider<FusePluginConfiguration>) && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(RelayRuntime) && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(FinRuntime) && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.Lifetime == ServiceLifetime.Singleton));
    }
}
