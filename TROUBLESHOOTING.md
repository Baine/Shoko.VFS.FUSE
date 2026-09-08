# Troubleshooting

This page covers the issues we hit while developing and operating Shoko.VFS.FUSE.
If something is missing, open an issue with the daemon log attached (set
`LogLevel: Debug` via `Logging__LogLevel__Default=Debug` for richer context).

## Repeated `404` / `TaskCanceledException` in the logs

**Symptom**: Daemon logs flood with `HttpRequestException` (404) or
`TaskCanceledException` (HttpClient.Timeout of 100 seconds) every few seconds
while the Shoko Server is unavailable or rebooting.

**Cause**: The aggregation cache only kept data in memory. When TTL expired or
SignalR reconnected, the next FUSE access triggered a rebuild, which hit the
down server and threw.

**Fix (in this repo)**: The host daemon now

- tracks server availability with a background ping loop
  (`ServerAvailabilityMonitor`),
- freezes per-mount aggregation caches while the server is down — reads keep
  returning the last known snapshot instead of triggering rebuilds,
- persists snapshots to disk on clean shutdown, and
- raises `HttpClient.Timeout` from the .NET default 100 s to a configurable
  `RequestTimeout` (default 10 minutes) so big-library aggregations are not
  aborted mid-fetch.

See `ServerPollInterval`, `MaxServerProbeFailures`, `SnapshotCacheDir`, and
`RequestTimeout` in `config.sample.json`.

If you still see timeouts on a slow server, raise `RequestTimeout` in
`~/.config/shoko-vfs-fuse/config.json`.

## Stale FUSE mounts after a crash

**Symptom**: After the daemon is killed (`kill -9`, OOM, power loss) and
restarted, the new instance cannot start because the old mountpoint is already
occupied.

**Fix**: The daemon now parses `/proc/self/mountinfo` on startup, finds any
mount whose mountpoint contains `!ShokoRelayVFS` / `!ShokoRelayMovieVFS`, and
runs `fusermount3 -u` against each. If unmount fails (kernel still holds a
reference), reboot or `umount -l` the path manually.

## Mount works but directories are empty

**Symptom**: `ls <mountpoint>` returns immediately with no entries; logs show
a healthy server connection but no errors.

**Likely causes**:

1. **Path mapping mismatch** — the server's managed folder paths differ from
   the host paths the daemon sees. Set `ServerPathRoot` and
   `ManagedFolderPathRoot` to rewrite. The daemon logs
   `MOUNT <path> FAILED PATH VALIDATION` for affected mounts and refuses to
   serve them. Verify by running
   `Shoko.VFS.FUSE.Host -- --dry-run` and comparing reported vs. on-disk paths.

2. **Source-only folder** — a managed folder flagged as Source-only on the
   server is not eligible for VFS. Add it to `ManagedFolderExclusions` only
   if you want to silence the warning; the daemon will not mount it either
   way.

3. **Cached empty snapshot** — if the previous run shutdown during a rebuild,
   the persisted snapshot may be empty. Delete
   `~/.local/share/shoko-vfs-fuse/snapshots/<managedFolderId>_<rootKind>.snapshot.json`
   and restart.

## Relay mounts are empty over NFS (fine locally and via SMB)

**Symptom**: `!ShokoRelay*` directories list their content on the Unraid host
and via SMB, but over NFS they show up empty; `stat` over NFS reports
different owner/mode than on the host.

**Cause**: The Linux NFS server cannot cross into FUSE submounts. The relay
mounts are FUSE filesystems nested inside `/mnt/user` (itself FUSE-based
shfs), so an export of `/mnt/user/array` — even with `crossmnt` — serves the
empty underlying directory instead of the mount. This is a knfsd limitation,
not a permission or `allow_other` problem.

**Fix**: Export every relay mount individually; knfsd serves a FUSE
filesystem directly without issue. Enable the opt-in helper and let the
daemon maintain the exports (see `deploy/README.md`, "NFS Export"):

```sh
# /path/to/shoko-vfs-fuse.env (beside start-shoko-vfs-fuse.sh)
INSTALL_NFS_EXPORTS=1
```

then restart the daemon. NFSv4 clients pick the new exports up on their next
lookup; if a formerly empty directory still lists empty, drop the client's
stale handles once: `sudo systemctl restart mnt-array.automount` (unit name
derived from the mount path).

## `Failed to build the Shoko VFS resolver snapshot` with `duplicate key`

**Symptom**: Log shows `System.ArgumentException: An item with the same key
has already been added. Key: <seriesId>` thrown from
`RelaySeriesGrouper.GroupIds` during `BuildSnapshot`; relay folders keep
serving stale (cached) content.

**Cause**: `GET /api/v3/Series` is paginated without a unique sort key, so a
series row can shift between two page requests while the daemon enumerates
(e.g. an import is running) and be returned twice. The duplicate series ID
crashed the grouping step and aborted the whole snapshot build.

**Fix**: Fixed in the daemon (client-side dedupe by series ID, first-wins in
the grouper). Update to a version containing the fix and restart.

## `Theme.mp3` does not appear in the VFS

**Symptom**: Plex/Jellyfin clients do not see `Theme.mp3` next to the season
directories, even though the file exists in the source managed folder.

**Cause**: ShokoRelay's AnimeThemesMp3Generator creates `Theme.mp3` in the
non-VFS source folder, then symlinks it into each VFS series directory. When
ShokoRelay runs with `Advanced.UseExternalVfs = true` (the recommended mode
for this host daemon), it skips the symlink step.

**Fix (in this repo)**: The daemon discovers `Theme.mp3` files in source
series folders during aggregation and exposes them as virtual entries at
`<TvRoot>/<seriesId>/Theme.mp3` and `<MovieRoot>/<episodeId>/Theme.mp3`.
FUSE reads are proxied to the real source file — same effective result as the
symlink, with no filesystem race against the mountpoint.

If the entry still does not show up:

- Confirm `Theme.mp3` actually exists at the source path (the daemon logs
  the absolute path it tried).
- The discovery walks the same REST endpoint the aggregation uses
  (`/api/v3/ManagedFolder/{id}/File?pageSize=0&include=XRefs`); if the file
  has no Shoko XRef (e.g. a stray standalone Theme.mp3 in an unrelated
  folder), it is invisible to the daemon by design.

## Daemon exits with `ServerStartupTimeout`

**Symptom**: Daemon refuses to start with
`Shoko server at <url> did not become reachable within 120s`.

**Fix**: Raise `ServerStartupTimeout` in `config.json` if your Shoko Server
takes longer than 120 s to boot (large libraries can take several minutes).
Or set `--config <path>` to a config with `ServerStartupTimeout: 600s`.

## Health endpoint reports `serverReady: false` but `serverUp: true`

**Cause**: The Shoko Server is reachable (HTTP responds) but the API is not
ready — usually because the database is still initializing or the admin
auth context is invalid.

**Action**: Wait. The monitor re-checks every `ServerPollInterval` and flips
`serverReady` to true once an authenticated call succeeds. If this state
persists, verify `SHOKO_API_KEY` / `SHOKO_USER`+`SHOKO_PASS` are correct.

## SELinux / AppArmor blocks `fusermount3`

**Symptom**: Mount fails with `Permission denied` even though `user_allow_other`
is in `/etc/fuse.conf`.

**Fix**: Allow the daemon's confined domain to execute `fusermount3` and
mount FUSE filesystems. The exact policy depends on the distro; consult the
distro's FUSE SELinux guide.
