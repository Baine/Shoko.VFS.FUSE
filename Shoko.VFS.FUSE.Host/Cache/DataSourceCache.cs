using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Host.Cache;

/// <summary>
/// TTL-based aggregation cache with single-flight for <c>ShokoRelayDataSource.GetAllSeriesAsync</c>.
///
/// When multiple concurrent callers request data:
/// - If a fetch is in progress, they all await that ONE in-flight build (single-flight).
/// - After TTL expiry, the cache is invalidated and the next request triggers a rebuild.
/// - Explicit <c>Invalidate()</c> marks the cache dirty immediately.
/// - <c>Freeze()</c> pins the cached data so callers keep getting the last known snapshot
///   while the upstream is unreachable (no rebuild attempts, no TTL-driven rebuilds).
///
/// This absorbs event storms: 100 file events → 1 rebuild.
/// </summary>
public sealed class DataSourceCache
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<SeriesData>>> _fetch;
    private readonly TimeSpan _ttl;
    private readonly int _maxDegree;
    private readonly FileSnapshotStore? _snapshotStore;
    private readonly string? _snapshotKey;

    private IReadOnlyList<SeriesData>? _cachedData;
    private DateTime _cachedAt;
    private Task<IReadOnlyList<SeriesData>>? _inFlight;
    private int _dirty;
    private int _frozen;

    /// <summary>
    /// Creates a new cache.
    /// </summary>
    /// <param name="fetch">Async delegate that performs the data aggregation. Called at most once per TTL window.
    /// Receives a cancellation token so the underlying fetcher can cancel a long build.</param>
    /// <param name="ttl">Time-to-live for cached data. Zero disables caching.</param>
    /// <param name="maxDegree">Maximum concurrent per-series fetches (only used during aggregation).</param>
    /// <param name="snapshotStore">Optional persistent snapshot store. When set, <see cref="SaveAsync"/>
    /// flushes the current cache and the constructor / <see cref="TryLoadFromStore"/> can restore it.</param>
    /// <param name="snapshotKey">Per-instance key used with <paramref name="snapshotStore"/>. Required if store is set.</param>
    public DataSourceCache(
        Func<CancellationToken, Task<IReadOnlyList<SeriesData>>> fetch,
        TimeSpan ttl,
        int maxDegree = 4,
        FileSnapshotStore? snapshotStore = null,
        string? snapshotKey = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _ttl = ttl;
        _maxDegree = Math.Max(1, maxDegree);
        _snapshotStore = snapshotStore;
        _snapshotKey = snapshotKey;
        if (_snapshotStore is not null && string.IsNullOrWhiteSpace(_snapshotKey))
            throw new ArgumentException("snapshotKey required when snapshotStore is set.", nameof(snapshotKey));
    }

    /// <summary>Configured maximum concurrent per-series fetches. Read-only.</summary>
    public int MaxDegree => _maxDegree;

    /// <summary>Whether the cache is currently frozen (serving stale data, skipping rebuilds).</summary>
    public bool IsFrozen => Volatile.Read(ref _frozen) != 0;

    /// <summary>Whether a cached snapshot is currently loaded in memory.</summary>
    public bool HasCachedData => _cachedData is not null;

    /// <summary>
    /// Gets the current aggregated data. Returns cached data if fresh; otherwise
    /// initiates a single-flight fetch and returns the result. While frozen, always
    /// returns the cached snapshot (no rebuild attempts).
    /// </summary>
    public async Task<IReadOnlyList<SeriesData>> GetAsync(CancellationToken ct = default)
    {
        // Frozen: never rebuild, never TTL-out. Serve whatever we have (may be null on first call).
        if (Volatile.Read(ref _frozen) != 0)
            return _cachedData ?? Array.Empty<SeriesData>();

        // Fast path: return cached data if still fresh and not dirty.
        if (_cachedData is not null && _ttl > TimeSpan.Zero
            && Interlocked.CompareExchange(ref _dirty, 0, 0) == 0
            && DateTime.UtcNow - _cachedAt < _ttl)
            return _cachedData;

        // Slow path: single-flight or full rebuild.
        return await SingleFlightAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the cache immediately, forcing a rebuild on the next <c>GetAsync</c>.
    /// No-op while frozen.
    /// </summary>
    public void Invalidate()
    {
        if (Volatile.Read(ref _frozen) != 0)
            return;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Pins the current cached data so subsequent <c>GetAsync</c> calls keep returning it
    /// without consulting TTL, the dirty flag, or the upstream fetcher. Used while the
    /// Shoko server is unreachable — keeps the FUSE mount responsive with stale data.
    /// </summary>
    public void Freeze() => Volatile.Write(ref _frozen, 1);

    /// <summary>
    /// Releases the freeze and marks the cache dirty so the next <c>GetAsync</c> rebuilds.
    /// Safe to call when not frozen.
    /// </summary>
    public void Unfreeze()
    {
        Volatile.Write(ref _frozen, 0);
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Returns the currently cached snapshot, or null if no data has been aggregated yet.
    /// Does not trigger a fetch.
    /// </summary>
    public IReadOnlyList<SeriesData>? PeekCached() => _cachedData;

    /// <summary>
    /// Loads the persisted snapshot from the configured <see cref="FileSnapshotStore"/> and
    /// primes the in-memory cache. Returns true if a snapshot was loaded. Does not touch the
    /// upstream fetcher.
    /// </summary>
    public bool TryLoadFromStore()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return false;
        var loaded = _snapshotStore.TryLoad(_snapshotKey);
        if (loaded is null)
            return false;
        _cachedData = loaded;
        _cachedAt = DateTime.UtcNow;
        // Loaded snapshot starts clean; freeze decides whether to honour TTL later.
        Interlocked.Exchange(ref _dirty, 0);
        return true;
    }

    /// <summary>
    /// Persists the current snapshot (if any) via the configured <see cref="FileSnapshotStore"/>.
    /// Writes a clean-shutdown marker so the next start can distinguish a graceful exit from
    /// a crash. No-op when no snapshot store is configured.
    /// </summary>
    public void SaveToStore()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return;
        var snapshot = _cachedData;
        if (snapshot is null)
            return;
        _snapshotStore.Save(_snapshotKey, snapshot);
    }

    /// <summary>
    /// Invalidates the persisted snapshot and its clean-shutdown marker. Use on startup when
    /// <see cref="FileSnapshotStore.WasLastShutdownClean"/> returned false, or after a manual
    /// refresh-from-server.
    /// </summary>
    public void InvalidateStored()
    {
        if (_snapshotStore is null || _snapshotKey is null)
            return;
        _snapshotStore.Clear(_snapshotKey);
    }

    /// <summary>True when a snapshot store is configured and the last shutdown was clean.</summary>
    public bool WasLastShutdownClean()
        => _snapshotStore is not null && _snapshotKey is not null
           && _snapshotStore.WasLastShutdownClean(_snapshotKey);

    private async Task<IReadOnlyList<SeriesData>> SingleFlightAsync(CancellationToken ct)
    {
        // A build is already in flight: await it rather than starting another.
        var existing = Volatile.Read(ref _inFlight);
        if (existing is not null)
            return await existing.ConfigureAwait(false);

        Task<IReadOnlyList<SeriesData>> build = BuildAsync(ct);

        // Atomically claim the build slot. The loser awaits the winner's task,
        // so exactly one upstream fetch runs for a burst of concurrent callers.
        var winner = Interlocked.CompareExchange(ref _inFlight, build, null);
        if (winner is not null)
            return await winner.ConfigureAwait(false);

        try
        {
            var result = await build.ConfigureAwait(false);
            _ = Interlocked.Exchange(ref _inFlight, null);
            return result;
        }
        catch
        {
            _ = Interlocked.Exchange(ref _inFlight, null);
            throw;
        }
    }

    private async Task<IReadOnlyList<SeriesData>> BuildAsync(CancellationToken ct)
    {
        var data = await _fetch(ct).ConfigureAwait(false);
        _cachedData = data;
        _cachedAt = DateTime.UtcNow;
        Interlocked.Exchange(ref _dirty, 0);
        return data;
    }
}
