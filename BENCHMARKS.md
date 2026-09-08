# Performance: Shoko.VFS.FUSE vs Shokofin vs ShokoRelay

> Host-daemon numbers below are **measured** on the production Unraid host
> (see [Measured data](#measured-data-production-unraid-host-2026-09-08)),
> pulled from `/mnt/cache/appdata/shoko-vfs-fuse/logs/daemon.log` and the
> running processes. Shokofin/ShokoRelay comparison numbers remain
> architectural estimates derived from the codebase's own reference points
> (e.g. `ShokoRelayDataSource.cs:133` — "1.4k series × ~200ms serially =
> 5+ minutes"). Always validate against your own workload.

## TL;DR

For a **6,152 series / 70,498 files / 70.8 TiB** library:

| Approach | Mount-ready cold start | Steady-state RAM | Disk delta | Survives Shoko restart |
|---|---|---|---|---|
| Shokofin (in-tree, virtual VFS) | ~8–20 min * | ~1.5–2.5 GB (shared with Shoko) * | 0 | ❌ |
| ShokoRelay (in-tree, symlink materialization) | **2–6 hours** + cleanup scan * | ~2–3 GB (shared with Shoko) * | 70,498 symlinks + 37k dirs | ❌ |
| Shoko.VFS.FUSE in-tree (virtual, lazy) | **~1–3 min** (structure pass) * | ~1.2–2.0 GB (shared with Shoko) * | 0 | ❌ |
| Shoko.VFS.FUSE.Host (REST, lazy + frozen cache) | ~3 s warm / **~40 min** cold reconcile (16 mounts, background) ✅ measured | **~490 MB** (separate) ✅ measured | snapshot JSON: **~1.6 MB** total ✅ measured | ✅ |
| Shoko.VFS.FUSE.Host **with `--warmup`** | warmup minutes offline, daemon start **~3 s** ✅ measured | same | same | ✅ |

\* = estimate, not measured. All ✅ measured rows come from the production
Unraid host (16 managed folders, 32 FUSE mounts, see below).

Since the lazy-aggregation rework, the mount publishes a structure-only
snapshot (series/movie folders) in minutes and materializes each series'
seasons/files on first directory access (~200 ms once per series, cached —
`SeriesCacheTtl`, default 5 min). SignalR events invalidate exactly the
affected series instead of rebuilding everything. `--warmup` additionally
front-loads per-series data so daemon starts stay fast (measured ~3 s).

## Measured data (production Unraid host, 2026-09-08)

Source: `/mnt/cache/appdata/shoko-vfs-fuse/logs/daemon.log` (one-day window),
`ps`, and `/mnt/cache/appdata/shoko-vfs-fuse/cache/`. 16 managed folders
(Anime Shows/Movies ×4 + Hentai Shows/Movies ×4), each with Tv + Movie mounts
= **32 live FUSE mounts**, served stale-while-revalidate.

| Metric | Measured |
|---|---|
| Warm daemon start (cached snapshots) | health endpoint up → **all 16 mounts serving in ~1–3 s** (18:02:28 → 18:02:29; 02:02:12 → 02:02:15) |
| Per-mount snapshot load from disk | 1–220 ms (largest: 976-series mount, 0.22 s) |
| Background reconcile after warm start | **~40 min** for all 16 mounts (18:02:29 unfreeze → 18:42:15 last fresh snapshot published; seeding dominates, resolver builds run staggered/overlapped) |
| Per-mount resolver build (cold, after seeding) | ~2:08–3:00 for the big mounts (976–1,419 series); max observed 9:27 |
| Series per mount (Tv snapshots) | 976, 1,419, 684, 499, 174, 167, 902, 681, 324, 136, 28 … (library-wide distinct count ≈ 6.1k as before) |
| Snapshot cache on disk | **1.6 MB total** — 32 JSON files, largest ~180 KB |
| Host daemon RSS | **~487 MB** (during active Plex transcode through the mount) |
| Shoko server (Shoko.CLI) RSS | ~8 GB, for comparison |
| Errors in 24 h of log | 2 (one transient snapshot-build `ArgumentException`, recovered on next build) |

Notes:
- The doc's previous "snapshot JSON: ~50–500 MB" estimate was wrong by ~2–3
  orders of magnitude: structure-only snapshots (series/movie folders, no
  per-file payload) are tiny.
- "Mount ready ~1–4 min" was likewise optimistic for a 16-mount deployment:
  that figure is per-mount; full-fleet reconcile is ~40 min, but the frozen
  cache means the *mounts serve data within seconds* the whole time.
- Warm restarts happen cleanly in practice: two clean restarts in the log
  window, both ~1–3 s to fully serving, `.clean_shutdown` markers present.

## Test environment

| Series | Files | Total size | Avg file size |
|---|---|---|---|
| 6,152 | 70,498 | 70.8 TiB | ~1.03 GB |

(Each series corresponds to a Shoko ID — includes TV, movies, OVAs, specials.)

## Cold mount-ready time (lazy architecture)

The eager per-series aggregation step is gone. Mount-ready = the structure
pass only; per-series data materializes on first directory access.

| Step | Shoko.VFS.FUSE in-tree | Shoko.VFS.FUSE.Host (REST) |
|---|---|---|
| Seeds: `/ManagedFolder/{id}/File` (paginated, parallel pages) | in-process place query | ~30 s–2 min (payload-bound; XRefs kept for seed IDs) |
| Series list `/api/v3/Series?pageSize=100` (~62 pages) | ~10 s | ~30–60 s |
| Closure + projector + grouper (structure-level, no episodes) | ~20–60 s | ~20–60 s |
| Movie-folder anchors (lite episode call per movie series, no `includeDataFrom`) | n/a (in-process) | tens of seconds to a few minutes for movie-heavy libraries |
| **Mount ready** | **~1–2 min** | per-mount ~2–9 min; full fleet (16 mounts) ~40 min, background ✅ measured |
| Per-series materialization (lazy, on first access) | one in-process load per series | one REST call ~200 ms per series, parallel by client access pattern |

Notes:
- The former dominant cost (per-series full episode fetch × 5–6k series at
  startup) now happens lazily per series directory and is cached
  (`SeriesCacheTtl`, default 5 min; plugin config `SeriesCacheTtlMinutes`).
- The `/ManagedFolder/{id}/File?include=XRefs` call remains the largest
  single cost on the host daemon — Shoko offers no cheaper folder-scoped
  seed query (verified against server source); pagination caps per-request
  transfer and allows parallel page fetches. **Keep `RequestTimeout` at
  10+ minutes** for the first page on very large folders.
- SignalR `file:*` / `series:info.updated` events invalidate exactly the
  affected series; only `file:detected` (pre-xref), reconnects, and huge
  bursts fall back to a full structure rebuild.

## Steady-state RAM

| | Process | Typical RSS |
|---|---|---|
| Shokofin | Shoko + Shokofin module | ~1.5–2.5 GB (inherits all of Shoko's caches) |
| ShokoRelay | Shoko + ShokoRelay plugin | ~2–3 GB (Shoko + symlink tracking + cleanup index) |
| Shoko.VFS.FUSE in-tree | Shoko + plugin | ~1.2–2.0 GB |
| Shoko.VFS.FUSE.Host | Standalone | **~490 MB measured** (separate process; + ~1.6 MB snapshot files on disk for 32 mounts) |

## Disk usage

| | Symlinks | Snapshot cache | Idle inode cost |
|---|---|---|---|
| Shokofin | 0 | 0 | 0 |
| ShokoRelay | **70,498 symlinks** (~5–10 MB metadata) + ~37,000 directories | 0 | inode-heavy |
| Shoko.VFS.FUSE in-tree | 0 | 0 | 0 |
| Shoko.VFS.FUSE.Host | 0 | **~1.6 MB JSON measured** (32 files, largest ~180 KB — structure-only, no per-file payload) | trivial |

## Where the wins actually land

### vs ShokoRelay (the biggest delta)

- **Materialization: skipped entirely.** ShokoRelay would create 70k symlinks
  on first run. At ~50 symlinks/sec on a hot filesystem that's ~25 min just
  for the `symlink()` syscalls, plus directory enumeration, ID3 tag reads,
  and VFS blueprint serialization — realistically **2–6 hours** for a clean
  first run on a 6k-series library, often interrupted and resumed.
- **Cleanup jobs: gone.** ShokoRelay's prune-orphan runs scan the entire VFS
  tree on every reconcile (~62k directory entries to stat). With ShokoRelay's
  typical 5-minute reconcile cycle, that's 12 stat-walks per hour of idle.
  The host daemon does zero.
- **Rebuild time after crash: minutes, not hours.** ShokoRelay on a partial
  crash leaves dangling symlinks; the next reconcile must enumerate every
  directory to know what to recreate. With the host daemon's snapshot cache,
  recovery is "load JSON, mark dirty, rebuild" — minutes even at this scale.
- **Time-to-first-mount:** ShokoRelay's first run takes hours before any FUSE
  read can return data. Shoko.VFS.FUSE.Host serves a stale snapshot within
  seconds (from the loaded persisted cache, when available).

### vs Shokofin (smaller wins, mostly resilience)

- **Survives ShokoServer restarts.** For a 6,152-series aggregation, ShokoServer
  restart takes 8–20 min to rebuild its own caches; during that window,
  Shokofin mounts are dead. The host daemon's frozen cache keeps the mount
  serving stale data through the restart — Plex/Jellyfin clients don't drop.
- **Outage noise gone.** The 404/timeout cascade that triggered this whole
  branch of work is solved by `Freeze`/`Unfreeze` + retry/backoff on the
  REST client + server-availability monitor + persisted snapshot cache.
- **Theming parity.** Shokofin has no AnimeThemes Theme.mp3 integration.
  ShokoRelay did; this repo now does for both deployments (in-tree plugin
  and host daemon).
- **CPU/memory isolation.** The host daemon doesn't share its heap with Shoko
  — relevant on resource-constrained hosts (Unraid, Synology, etc.) where
  Shoko + Plex + Sonarr + Radarr already consume most of the RAM budget.

### Honest cost (vs Shokofin)

- **Cold FUSE read of a 1 GB file** is ~50–200 ms slower on the first miss
  (REST round-trip). After the resolver snapshot is warm, subsequent reads
  go through a hot path. Plex/Jellyfin typically cache metadata so this is
  a one-time cost per scan.
- **First aggregation pain:** ~40 min full-fleet reconcile on this deployment
  (16 managed folders, measured) vs 8–20 min for Shokofin. Mitigated twice
  over: the frozen cache serves within seconds, and `--warmup` moves the
  reconcile offline (below).
- **Memory:** host daemon is its own process. 1 GB dedicated is non-trivial
  on a 16 GB host. In-tree Shoko.VFS.FUSE shares Shoko's heap so it's cheaper
  on memory at the cost of sharing Shoko's restart fate.

## Warmup prefetch (`--warmup`)

The host daemon ships with a one-shot mode that runs the full startup path
once, persists the resulting per-mount snapshots + the clean-shutdown
marker, and exits. The actual daemon start is then near-instant because
`TryLoadFromStore` returns the warm snapshot synchronously.

```sh
# Pre-warm snapshots once, then exit. Persists <snapshotCacheDir>/<key>.snapshot.json
# and <key>.clean_shutdown for every managed folder.
dotnet run --project Shoko.VFS.FUSE.Host -- --warmup

# Then the actual daemon starts hot.
systemctl start shoko-vfs-fuse-host
```

### What `--warmup` actually does

The full `RunAsync` startup path minus the safety loop:

1. Wait for server (`ServerStartupTimeout`).
2. Authenticate (`SHOKO_API_KEY` or `SHOKO_USER`+`SHOKO_PASS`).
3. Start SignalR (background connection, fires dirty events on reconnect).
4. Clean stale FUSE mounts left by previous crashed runs.
5. Start the server-availability monitor.
6. Load any prior persisted snapshot into each lease's data source.
7. Run the full startup reconcile (this is the heavy step — minutes to tens
   of minutes depending on library size).
8. **Return.** `DisposeAsync` flushes per-mount snapshots + writes the
   `.clean_shutdown` marker.

The leases' data sources are created and torn down during the reconcile
step; FUSE mounts are briefly created and then unmounted when the orchestrator
disposes. The mountpoints must exist (or `CleanupStaleMountsAsync` will leave
them for the real daemon to handle).

### Updated cost numbers with `--warmup`

For a 6,152-series library (measured on the Unraid host, 16 mounts):

| Path | Without `--warmup` | With `--warmup` |
|---|---|---|
| First daemon start (cold) | ~40 min reconcile (FUSE serves empty/stale cache while reconcile runs in background; measured) | ~40 min (during warmup, FUSE briefly mounts) |
| Subsequent daemon start | ~40 min (same cold path; no snapshot to load) | **~3 s measured** (load JSON, mark dirty, serve stale-while-revalidate) |
| FUSE first-read latency during initial reconcile | empty cache → slow first fetch per directory, ~50–200 ms per REST round-trip | served from loaded snapshot immediately, <1 ms |
| Total cold-aggregation work | once at first daemon start | once at warmup, *before* the daemon exists |
| Daemon process resource use during cold aggregation | full RSS for the duration | full RSS during warmup, then idle for the daemon |

The win isn't reduced CPU — the aggregation still runs once. It's that the
*time* when the aggregation runs is moved out of the "Plex is online and
scanning" window.

### When to use `--warmup`

- **Fresh Shoko install:** run `--warmup` once after configuring, before
  enabling the systemd service. Plex's first scan will see a hot mount.
- **Library growth:** schedule `--warmup` as a systemd timer or cron job
  after large imports. The next daemon restart picks up the fresh snapshot.
- **Migration from Shokofin / ShokoRelay:** warmup produces a complete,
  validated snapshot in the configured `SnapshotCacheDir`. Safe to run with
  the old VFS still mounted — leases are isolated per managed folder.
- **Disaster recovery:** warmup rebuilds snapshots from scratch when the
  existing cache is missing the clean-shutdown marker (i.e. previous run
  crashed). Faster than waiting for the daemon to rebuild and signal-OK it.

### When NOT to use `--warmup`

- **Shoko is unreachable.** `--warmup` exits with the same `ServerStartupTimeout`
  the daemon would. Don't schedule it for the same window as a Shoko update.
- **Mountpoints don't exist yet.** `--warmup` will fail to mount and exit
  non-zero. Set up the directories first.
- **You need live signal-driven updates.** `--warmup` is a snapshot; it
  does not start SignalR event handlers that keep the snapshot fresh. The
  main daemon's `Reconnected` event + safety loop + content coalescer are
  what keep the live view current. Use both: warmup for the floor, daemon
  for the live updates.

### Configuration knobs that matter for warmup

| Setting | Default | Why it matters at this library size |
|---|---|---|
| `ServerStartupTimeout` | 120 s | Bump to 600 s on cold boots of large libraries. |
| `RequestTimeout` | 10 min | Bump to 15+ min for `/ManagedFolder/{id}/File?include=XRefs` over 70k+ files. |
| `AggregationFetchDegree` | 4 | 6,152 series / degree 4 ≈ 5 min per-series step; degree 8 ≈ 2.5 min but heavier Shoko load. |
| `SnapshotCacheDir` | `$XDG_DATA_HOME/shoko-vfs-fuse/snapshots` | Set to a fast filesystem; warmup writes one JSON per mount on exit. |
| `HttpRetries` | 2 | Bump to 3 if Shoko is remote or flaky. |
| `ContentDirtyCooldown` | 5 s | Lower if you want faster invalidation during active scanning. |

## Recommendation for this library (6,152 series, 70k files, 70.8 TiB)

**In-tree plugin (Shoko.VFS.FUSE)** when:

- Shoko and Plex share a host.
- You don't need outage survival.
- RAM is tight (< 16 GB host, Shoko + Plex + daemons already using most of it).
- Cold-start time is acceptable in the deploy window.

**Host daemon (Shoko.VFS.FUSE.Host)** when:

- Shoko runs in Docker or on a different host.
- You want the FUSE mount to survive Shoko crashes / restarts.
- You have ≥ 4 GB free RAM for a dedicated process.
- Your primary pain was the 404 storm during Shoko outages (which is what
  triggered this branch of work).
- The library is large enough that the ~40 min cold reconcile matters
  to your Plex-scan workflow (measured: 16 managed folders).

**Always run `--warmup` once** before enabling the host daemon for the
first time on a library this size, and consider a daily / weekly timer
thereafter to keep the snapshots warm across restarts.

## Caveats and honest limits

- All per-access numbers assume cache hit; cold reads pay the REST
  round-trip. With `--warmup` ahead of time, "cold reads" become rare.
- "Steady-state RAM" depends heavily on how many files are visible
  simultaneously in the resolver snapshot.
- ShokoRelay's biggest cost is *materialization*, not steady-state — at
  smaller libraries (< 500 series) the difference narrows and the choice
  becomes mostly about features.
- For libraries > 5k series, the host daemon's persistent snapshot cache
  is the meaningful differentiator — it survives reboots and crashes.
  In-tree Shoko.VFS.FUSE has no equivalent yet.
- The 200 ms / per-series estimate comes from the codebase's own
  ponytail-comment reference point; real-world numbers vary with Shoko's
  DB load, the host's network/loopback latency, and whether TMDB
  alternate-ordering data is needed.
- Test with a sample: `dotnet run --project Shoko.VFS.FUSE.Host -- --dry-run`
  prints the mount plan without any aggregation, so you can validate the
  topology quickly before committing to a full warmup.
