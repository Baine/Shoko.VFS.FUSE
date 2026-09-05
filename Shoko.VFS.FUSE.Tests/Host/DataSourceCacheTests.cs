using Shoko.VFS.FUSE.Host.Cache;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// Verifies the data-source cache freeze, save/load and clean-shutdown-marker semantics
/// used by the orchestrator to survive Shoko server outages.
/// </summary>
public sealed class DataSourceCacheTests
{
    private static IReadOnlyList<SeriesData> SampleData(int count = 3) =>
        Enumerable.Range(1, count)
            .Select(i => new SeriesData(i, $"Series {i}", false,
                [new EpisodeData(i * 10, i * 100, 1, 1, null, null, null, false, true, null, $"/source/{i}.mkv", 1024, ".mkv")]))
            .ToList();

    [Fact]
    public async Task FrozenCacheReturnsLastKnownDataAndSkipsRebuild()
    {
        int upstreamCalls = 0;
        var cache = new DataSourceCache(
            _ => { upstreamCalls++; return Task.FromResult<IReadOnlyList<SeriesData>>(SampleData()); },
            TimeSpan.FromSeconds(60));

        await cache.GetAsync();   // first call builds
        Assert.Equal(1, upstreamCalls);

        cache.Freeze();
        // Even with dirty flag set + TTL long past, frozen cache never refetches.
        cache.Invalidate();
        for (int i = 0; i < 5; i++)
            await cache.GetAsync();
        Assert.Equal(1, upstreamCalls);
        Assert.True(cache.IsFrozen);
    }

    [Fact]
    public async Task UnfreezeTriggersRebuildOnNextGet()
    {
        int upstreamCalls = 0;
        var cache = new DataSourceCache(
            _ => { upstreamCalls++; return Task.FromResult<IReadOnlyList<SeriesData>>(SampleData()); },
            TimeSpan.FromSeconds(60));

        await cache.GetAsync();
        cache.Freeze();
        await cache.GetAsync();   // no rebuild
        cache.Unfreeze();
        await cache.GetAsync();   // rebuilds once
        Assert.Equal(2, upstreamCalls);
        Assert.False(cache.IsFrozen);
    }

    [Fact]
    public async Task PersistAndReloadRoundtripsViaFileStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shoko-vfs-cache-test-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSnapshotStore(dir);
            // First cache: build, save.
            var cacheA = new DataSourceCache(
                _ => Task.FromResult<IReadOnlyList<SeriesData>>(SampleData(5)),
                TimeSpan.FromSeconds(60),
                snapshotStore: store,
                snapshotKey: "m1");
            await cacheA.GetAsync();
            cacheA.SaveToStore();
            Assert.True(cacheA.WasLastShutdownClean());

            // Fresh cache instance, same store + key: should load the snapshot without rebuilding.
            int upstreamCalls = 0;
            var cacheB = new DataSourceCache(
                _ => { upstreamCalls++; return Task.FromResult<IReadOnlyList<SeriesData>>(SampleData(5)); },
                TimeSpan.FromSeconds(60),
                snapshotStore: store,
                snapshotKey: "m1");
            Assert.True(cacheB.TryLoadFromStore());
            var data = await cacheB.GetAsync();
            Assert.Equal(5, data.Count);
            Assert.Equal(0, upstreamCalls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MissingCleanShutdownMarkerDiscardsStoredSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shoko-vfs-cache-test-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSnapshotStore(dir);
            // Write a snapshot file but NO clean-shutdown marker (simulates a crash).
            File.WriteAllText(Path.Combine(dir, "m1.snapshot.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(SampleData(2)));

            int upstreamCalls = 0;
            var cache = new DataSourceCache(
                _ => { upstreamCalls++; return Task.FromResult<IReadOnlyList<SeriesData>>(SampleData(2)); },
                TimeSpan.FromSeconds(60),
                snapshotStore: store,
                snapshotKey: "m1");

            Assert.False(cache.WasLastShutdownClean());
            // Caller invalidates + rebuilds when marker is missing.
            cache.InvalidateStored();
            Assert.False(File.Exists(Path.Combine(dir, "m1.snapshot.json")));

            await cache.GetAsync();
            Assert.Equal(1, upstreamCalls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
