using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Tests.Runtime;

public sealed class FinRuntimeTests
{
    [Fact]
    public async Task WaitsForStartupThenMountsWhenEnabled()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "/tmp/fin-vfs" });
        await fixture.Runtime.StartAsync(CancellationToken.None);

        Assert.Equal(FinRuntimeState.WaitingForServer, fixture.Runtime.State);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1, () => $"state={fixture.Runtime.State}, starts={fixture.Starts}");

        Assert.Equal(1, fixture.Starts);
        Assert.Equal(FinRuntimeState.Healthy, fixture.Runtime.State);
        await fixture.Runtime.StopAsync(CancellationToken.None);
        Assert.Equal(FinRuntimeState.Stopped, fixture.Runtime.State);
    }

    [Fact]
    public async Task DisabledWhenFinDisabled()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = false });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Runtime.State == FinRuntimeState.Disabled);

        Assert.Equal(0, fixture.Starts);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InvalidMountPointDisablesMount()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "relative/path" });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Runtime.State == FinRuntimeState.Disabled);

        Assert.Equal(0, fixture.Starts);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigSaveRemounts()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "/tmp/fin-vfs" });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Starts == 2 && fixture.Stops == 1);
        Assert.Equal(FinRuntimeState.Healthy, fixture.Runtime.State);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailureToStartGoesDegradedKeepsRetrying()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "/tmp/fin-vfs" })
        {
            FailStart = true,
        };
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Runtime.State == FinRuntimeState.Degraded);

        fixture.FailStart = false;
        fixture.Configuration.RaiseSaved();
        await Eventually(() => fixture.Starts >= 2 && fixture.Runtime.State == FinRuntimeState.Healthy);
        await fixture.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnexpectedStopRemovesLeaseAndRestartsOnNextSignal()
    {
        using var fixture = new Fixture(new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "/tmp/fin-vfs" });
        await fixture.Runtime.StartAsync(CancellationToken.None);
        fixture.System.CompleteStartup();
        await Eventually(() => fixture.Starts == 1);

        fixture.HoldNextStart();
        fixture.RaiseUnexpectedStop();
        await Eventually(() => fixture.Runtime.State == FinRuntimeState.Degraded);

        fixture.ReleaseStart();
        await Eventually(() => fixture.Starts == 2 && fixture.Runtime.State == FinRuntimeState.Healthy);
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await fixture.Runtime.StopAsync(stopTimeout.Token);
    }

    private static async Task Eventually(Func<bool> condition, Func<string>? details = null)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.True(condition(), details?.Invoke());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ConfigurationProvider<FusePluginConfiguration> _provider;
        private readonly Dictionary<string, Action> _unexpectedStops = new(StringComparer.Ordinal);
        private TaskCompletionSource? _startGate;
        private int _startReleased;
        private Action? _currentStop;

        public FakeSystem System { get; } = new();
        public FakeConfiguration Configuration { get; }
        public FinRuntime Runtime { get; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool FailStart { get; set; }

        public void RaiseUnexpectedStop() => _unexpectedStops.Values.Single()();

        public void HoldNextStart()
        {
            Volatile.Write(ref _startReleased, 0);
            _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseStart()
        {
            Volatile.Write(ref _startReleased, 1);
            _startGate?.TrySetResult();
        }

        public Fixture(FusePluginConfiguration configuration)
        {
            Configuration = new FakeConfiguration(configuration);
            _provider = new ConfigurationProvider<FusePluginConfiguration>(Configuration.Proxy);
            var operations = new FakeMountOperations(this);
            Runtime = new FinRuntime(
                System.Proxy,
                DispatchProxy.Create<IMetadataService, EmptyProxy>(),
                _provider,
                new FakePaths().Proxy,
                NullLoggerFactory.Instance,
                operations,
                NullLogger<FinRuntime>.Instance
            );
        }

        private sealed class FakeMountOperations : IFinMountOperations
        {
            private readonly Fixture _owner;
            public FakeMountOperations(Fixture owner) => _owner = owner;

            public async Task<FinMountLease> StartAsync(
                FusePluginConfiguration configuration,
                IApplicationPaths applicationPaths,
                IMetadataService metadataService,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken,
                Action stopped,
                Action daemonStopped,
                Action cleanupCompleted)
            {
                _owner.Starts++;
                cancellationToken.ThrowIfCancellationRequested();
                if (_owner.FailStart)
                    throw new InvalidOperationException("mount failure");
                if (_owner._startGate is { } gate)
                {
                    if (Volatile.Read(ref _owner._startReleased) == 0)
                        await gate.Task;
                    _owner._startGate = null;
                }
                _owner._unexpectedStops["single"] = stopped;
                _owner._currentStop = stopped;
                return new FinMountLease(
                    () => { },
                    _ =>
                    {
                        _owner.Stops++;
                        return ValueTask.CompletedTask;
                    },
                    stopped,
                    daemonStopped,
                    cleanupCompleted);
            }
        }

        public void Dispose()
        {
            _provider.Dispose();
        }
    }

    private sealed class FakeConfiguration
    {
        public FusePluginConfiguration Current { get; set; }
        public IConfigurationService Proxy { get; }
        private event EventHandler<ConfigurationSavedEventArgs>? Saved;

        public FakeConfiguration(FusePluginConfiguration current)
        {
            Current = current;
            var proxy = DispatchProxy.Create<IConfigurationService, ConfigurationProxy>();
            ((ConfigurationProxy)(object)proxy).Owner = this;
            Proxy = proxy;
        }

        public void RaiseSaved() => Saved?.Invoke(this, new ConfigurationSavedEventArgs { ConfigurationInfo = null! });

        private class ConfigurationProxy : DispatchProxy
        {
            internal FakeConfiguration Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                return method?.Name switch
                {
                    "add_Saved" => Add(args),
                    "remove_Saved" => Remove(args),
                    "GetConfigurationInfo" => null,
                    "Load" => Owner.Current,
                    _ => Default(method?.ReturnType),
                };
            }

            private object? Add(object?[]? args)
            {
                Owner.Saved += (EventHandler<ConfigurationSavedEventArgs>)args![0]!;
                return null;
            }

            private object? Remove(object?[]? args)
            {
                Owner.Saved -= (EventHandler<ConfigurationSavedEventArgs>)args![0]!;
                return null;
            }
        }
    }

    private sealed class FakeSystem
    {
        private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISystemService Proxy { get; }

        public FakeSystem()
        {
            var proxy = DispatchProxy.Create<ISystemService, SystemProxy>();
            ((SystemProxy)(object)proxy).Startup = _startup.Task;
            Proxy = proxy;
        }

        public void CompleteStartup() => _startup.SetResult();

        private class SystemProxy : DispatchProxy
        {
            internal Task Startup { get; set; } = Task.CompletedTask;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method?.Name == "WaitForStartupAsync")
                    return Startup;
                return Default(method?.ReturnType);
            }
        }
    }

    private sealed class FakePaths
    {
        public IApplicationPaths Proxy { get; }

        public FakePaths()
        {
            var proxy = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
            ((PathsProxy)(object)proxy).Data = "/tmp";
            Proxy = proxy;
        }

        private class PathsProxy : DispatchProxy
        {
            internal string Data { get; set; } = "";
            protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_DataPath" ? Data : Default(method?.ReturnType);
        }
    }

    private class EmptyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Default(method?.ReturnType);
    }

    private static object? Default(Type? type) => type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

    private static object? Add<T>(ref T? field, object?[]? args) where T : Delegate
    {
        field = (T?)Delegate.Combine(field, (T)args![0]!);
        return null;
    }

    private static object? Remove<T>(ref T? field, object?[]? args) where T : Delegate
    {
        field = (T?)Delegate.Remove(field, (T)args![0]!);
        return null;
    }
}
