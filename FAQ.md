# FAQ

## What is this project?

A Linux FUSE virtual filesystem for [Shoko Server](https://github.com/ShokoAnime/ShokoServer).
Two deliverables ship from the same repo:

1. **Shoko.VFS.FUSE** — the in-tree plugin that runs inside Shoko Server's
   process. The "classic" deployment.
2. **Shoko.VFS.FUSE.Host** — a standalone host daemon that talks to Shoko
   over REST and SignalR, mounts the FUSE filesystem from outside Shoko.
   Recommended for setups where the Shoko Server runs in a container or on a
   different host from the FUSE consumers.

## Why is there a host daemon instead of just the plugin?

Because mounting FUSE filesystems from inside Shoko Server is awkward when
Shoko is running in Docker / under a different user / on a remote host. The
host daemon keeps Shoko as the source of truth (REST + SignalR) but lets the
mount live wherever it makes sense.

## How does this relate to ShokoRelay?

ShokoRelay is a Shoko plugin that bundles metadata helpers, a Plex integration,
and an in-process VFS. The in-process VFS part is what this project replaces
when you run the host daemon — ShokoRelay's `Settings.Advanced.UseExternalVfs`
flag tells ShokoRelay to stop materializing the VFS and let this daemon own
the filesystem instead. ShokoRelay's other features (metadata, Plex, AnimeThemes
Theme.mp3 generation) continue to work; the daemon consumes the `Theme.mp3`
files ShokoRelay writes into the source folders and exposes them in the VFS.

## How does `MovieGenerationMode` affect what appears in each VFS root?

It mirrors ShokoRelay's own semantics exactly (see ShokoRelay's
`MovieGenerationMode` enum in `Config/RelayConfig.cs`):

| Value | `!ShokoRelayVFS` (TV root) | `!ShokoRelayMovieVFS` (movie root) |
|---|---|---|
| `0` — Disabled | Shows **and** movies (movies as TV-shaped paths) | not created |
| `1` — EnabledMaintain | Shows **and** movies (movies as TV-shaped paths) | movies only |
| `2` — EnabledRemove | Shows only | movies only |

Mode 1 keeps a duplicate TV-shaped view of every movie in the TV root **by
design** — upstream ShokoRelay documents it as "Generate standalone movie
folders but keep them in the standard VFS as well". If you want movies to
appear only in the movie root (no mixing), use mode `2`.

Consequently, a movie entry is not "misclassified" when it appears in the TV
root under mode `1`; that is the Maintain behavior. Movie-root folders are
named by the series' main Shoko episode ID (e.g. `10769/Movie [18625].mkv`),
not by the series ID.

## Why are my logs full of 404s / connection errors?

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md#repeated-404--taskcanceledexception-in-the-logs).
Short version: the daemon now freezes its in-memory snapshot when the Shoko
server is unreachable, so FUSE reads keep working with stale data instead of
failing on every access.

## Why are the relay mounts empty over NFS (but fine via SMB)?

The Linux NFS server cannot cross into FUSE submounts, and the relay mounts
are FUSE filesystems nested inside `/mnt/user` (itself FUSE-based shfs) —
so exporting the parent with `crossmnt` serves empty directories. The fix
is one export per relay mount; the daemon ships an opt-in helper
(`"InstallNfsExports": true` in `config.json`, disabled by default) that
maintains them automatically. See `TROUBLESHOOTING.md` and the "NFS Export"
section of `deploy/README.md`.

## Where do snapshots live on disk?

`$XDG_DATA_HOME/shoko-vfs-fuse/snapshots/` (or
`~/.local/share/shoko-vfs-fuse/snapshots/` if `XDG_DATA_HOME` is unset), one
file per mount:

```
<managedFolderId>_<TvRoot|MovieRoot>.snapshot.json
<managedFolderId>_<TvRoot|MovieRoot>.clean_shutdown   # present iff last exit was graceful
```

If a mountpoint was killed mid-write, the `.clean_shutdown` marker is missing
and the daemon rebuilds from the server on the next start. To force a rebuild
without changing anything else, delete the marker (or the whole snapshot
directory).

## How do I migrate from the in-tree plugin to the host daemon?

1. Stop Shoko Server.
2. Disable the in-tree plugin's Relay mount in plugin settings
   (`RelayEnabled = false`).
3. Enable `Advanced.UseExternalVfs` in ShokoRelay (this is the default if
   the host daemon is detected).
4. Configure and start `Shoko.VFS.FUSE.Host` with the same Relay root folder
   names (`!ShokoRelayVFS`, `!ShokoRelayMovieVFS`) under each managed folder.
5. The host daemon mounts the FUSE filesystems in the same paths the plugin
   used, so Plex/Jellyfin keep working without reconfiguration.

## Why doesn't the daemon use a NuGet reference for `Shoko.Abstractions`?

It does. A prerelease NuGet of `Shoko.Abstractions` is published on
[nuget.org](https://www.nuget.org/packages/Shoko.Abstractions/) (currently
`6.0.0-alpha.84`), and all three projects reference that package instead of a
source checkout — no sibling `ShokoServer` clone is required to build this
repo.

The plugin pins `ExcludeAssets="runtime"`: it compiles against the package but
never ships the DLL, since Shoko Server itself provides
`Shoko.Abstractions.dll` at runtime. Keep the package version in sync with the
Shoko Server version you deploy against.

## How do I run only the host daemon's self-test?

```sh
dotnet run --project Shoko.VFS.FUSE.Host -- --selftest
```

No network, no Shoko Server — runs 15 invariant assertions across the relay
projection, override CSV, dirty coalescer, data-source cache, and path
validation. CI uses this.

## Does the daemon require root?

Only if the FUSE mountpoint itself requires it. The daemon runs as an
ordinary user; `fusermount3` is invoked via setuid (or the `fusermount3`
binary must be +s).

## What's the relationship between `RequestTimeout` and `HttpRetries`?

- `RequestTimeout` is the hard ceiling on a single HTTP request (default
  10 minutes). Raise it for very large libraries.
- `HttpRetries` is how many times the daemon retries a transient failure
  (5xx, 408, 429, connection resets, request timeouts) with exponential
  backoff (100 ms, 400 ms, 1.6 s by default). Set to 0 to disable.

Both work together: a single slow request gets up to `RequestTimeout`
seconds; each transient failure gets up to `HttpRetries + 1` attempts.

## What happens if I have both the plugin and the host daemon mounting the same paths?

Don't. They will fight over the mountpoint. Pick one. The host daemon is
preferred for containerized / remote Shoko setups; the plugin is preferred
when Shoko and the FUSE consumers share a host and a user.

## How do I uninstall?

For the plugin: remove its directory under Shoko's `PluginsPath` and restart
Shoko. For the host daemon: stop the process, unmount each FUSE mountpoint
(`fusermount3 -u <path>` or `umount <path>`), and delete the systemd unit /
init script. The on-disk snapshot directory can stay or be deleted — it is
recreated on next run.
