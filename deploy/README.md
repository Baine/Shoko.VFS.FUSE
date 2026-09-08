# Shoko VFS FUSE — Deployment Guide

> **Unraid-specific.** This guide targets Unraid: the daemon runs on the **host**
> (not inside a container) as **root**, installed under **`/boot`** (flash), with
> path mapping from the Shoko container's `/mnt/array` to the host's
> `/mnt/user/array`. **Other systems must adapt** — see
> [Adapting for non-Unraid hosts](#adapting-for-non-unraid-hosts) below.

## TL;DR

A one-command publish script builds a **self-contained** (runtime-bundled)
`linux-x64` tarball laid out for `/boot/config/plugins/shoko-vfs-fuse/`:

```sh
# On your build machine (this PC):
./scripts/publish-host.sh          # → artifacts/shoko-vfs-fuse-host-linux-x64.tar.gz

# On the Unraid host (flash, survives reboot):
mkdir -p /boot/config/plugins/shoko-vfs-fuse
tar -xzf shoko-vfs-fuse-host-linux-x64.tar.gz -C /boot/config/plugins/shoko-vfs-fuse
```

The tarball contains the executable, `start-shoko-vfs-fuse.sh`, and
`config.example.json`. Everything persists on `/boot` — nothing under `/` or
`/opt` (both are a RAM overlay wiped on reboot).

## Quick Start (Unraid)

### 1. Install the daemon binary (flash)

Run the publish script (build machine); it bundles the .NET runtime so nothing
needs installing on Unraid:

```sh
./scripts/publish-host.sh
# → artifacts/shoko-vfs-fuse-host-linux-x64.tar.gz
```

Copy and extract onto the **flash drive** (not `/opt`!):

```sh
# From the build machine:
scp artifacts/shoko-vfs-fuse-host-linux-x64.tar.gz <unraid-ip>:/tmp/

# On the Unraid host (or via the Unraid web terminal):
mkdir -p /boot/config/plugins/shoko-vfs-fuse
tar -xzf /tmp/shoko-vfs-fuse-host-linux-x64.tar.gz -C /boot/config/plugins/shoko-vfs-fuse
chmod +x /boot/config/plugins/shoko-vfs-fuse/shoko-vfs-fuse-host
rm /tmp/shoko-vfs-fuse-host-linux-x64.tar.gz
```

### 2. Create the config file

```sh
mkdir -p /boot/config/plugins/shoko-vfs-fuse
cat > /boot/config/plugins/shoko-vfs-fuse/config.json <<'EOF'
{
  "ShokoUrl": "http://192.168.1.10:8888",
  "ShokoApiKey": "your-api-key-here",

  "RelayEnabled": true,
  "FuseAllowOther": true,

  "ServerPathRoot": "/mnt/array",
  "ManagedFolderPathRoot": "/mnt/user/array",

  "MovieGenerationMode": 0
}
EOF
```

Replace the URL and API key with your Shoko Server credentials.
Use `SHOKO_USER`/`SHOKO_PASS` instead of `ShokoApiKey` if preferred.

### 3. Add to Unraid's go script

The publish tarball already drops `start-shoko-vfs-fuse.sh` beside the binary
(from `deploy/start-shoko-vfs-fuse.sh`). Add a single line to `/boot/config/go`:

```sh
# /boot/config/go — runs at boot as root
/boot/config/plugins/shoko-vfs-fuse/start-shoko-vfs-fuse.sh &
```

That same shell line is also handy to re-run by hand after an update
(SIGTERM + restart the daemon) without rebooting.

The script:
- Ensures `/etc/fuse.conf` has `user_allow_other` (idempotent).
- Launches the daemon in the background with nohup.
- Writes PID to `/var/run/shoko-vfs-fuse.pid`.
- Logs to `/var/log/shoko-vfs-fuse/daemon.log`.

## NFS Export (opt-in)

The relay mounts are FUSE filesystems nested under `/mnt/user` (itself the
FUSE-based shfs). The Linux NFS server **cannot cross into FUSE submounts**:
an export of `/mnt/user/array` with `crossmnt` serves the array content fine,
but the `!ShokoRelay*` mountpoints appear empty to NFS clients — even though
they work locally and via SMB. This is a knfsd limitation with FUSE-in-FUSE
mounts, not a daemon or permission problem.

The fix is one dedicated export entry per relay mount: knfsd serves a FUSE
filesystem directly without problems (Unraid's own `/mnt/user` export is
exactly that). The daemon package ships `install-nfs-exports.sh`, which
regenerates `/etc/exports.d/shoko-vfs.exports` from the live mount list
(stable hash-based fsids so client file handles survive re-runs) and reloads
the exports.

**This is disabled by default** — installations that do not use NFS never run
it and don't need to think about it. To enable, set in `config.json`:

```jsonc
{
  "InstallNfsExports": true,
  // optional: space-separated client specs (default: "*", all_squash to nobody:users)
  "NfsExportClients": "192.168.178.20 192.168.178.21"
}
```

After every reconcile the daemon runs the helper (idempotent, best-effort:
failures are logged, never fatal), so new or removed relay mounts are picked
up automatically. Requirements:

- The daemon must run as root (its usual mode on Unraid) — `exportfs` and
  `/etc/exports.d` need it.
- The NFS server must be enabled on Unraid (Settings → NFS).
- On Unraid, `/etc` is tmpfs: the exports file is restored at the next daemon
  reconcile, not at boot. To share the mounts before the daemon has started
  since boot, run the helper once manually:
  `sudo /mnt/cache/appdata/shoko-vfs-fuse/install-nfs-exports.sh`

Clients need no extra mounts: an NFSv4 mount of the array (or `/mnt/user`)
transparently crosses into the per-mount exports on first access. If a
directory that was previously empty still shows empty, the client has stale
cached handles — restart its automount or remount once:
`sudo systemctl restart mnt-array.automount` (unit name derived from the
mount path).

## Container Access (Docker)

Containers on Unraid typically mount `/mnt/array` (the raw array path).
Because the daemon maps server paths (`/mnt/array` → `/mnt/user/array`),
the FUSE mounts live under `/mnt/user/array/...`.

**Option A**: Mount `/mnt/user` into the container:
```sh
docker run -v /mnt/user:/mnt/user ...
```

**Option B**: Adjust the path mapping so FUSE mounts land under `/mnt/array`:
```json
{
  "ServerPathRoot": "",
  "ManagedFolderPathRoot": ""
}
```
(Identity mapping — server paths are used as-is.)

## Movie Separation

When `MovieGenerationMode` is set to `1` (EnabledMaintain) or `2` (EnabledRemove),
an additional `!ShokoRelayMovieVFS` root is created alongside the TV root:

```
/mnt/user/array/Anime Title/!ShokoRelayVFS/     ← TV series
/mnt/user/array/Anime Title/!ShokoRelayMovieVFS/ ← movies
```

## Health Endpoint

The daemon exposes a health check on `http://127.0.0.1:8790/` (configurable via `HealthPort`).

```sh
curl http://localhost:8790/
# {"state":"Healthy","signalrState":"connected","mounts":[...],...}
```

## Self-Test

Run the built-in self-test (no server required):

```sh
/boot/config/plugins/shoko-vfs-fuse/shoko-vfs-fuse-host --selftest
# selftest: OK (15 assertions)
```

A dry run (connects to Shoko, prints the mount plan, mounts nothing) is a good
pre-flight check after any config/environment change:

```sh
/boot/config/plugins/shoko-vfs-fuse/shoko-vfs-fuse-host \
    --dry-run --config /boot/config/plugins/shoko-vfs-fuse/config.json
```

## Warmup (cache pre-prime)

For large libraries (1k+ series), the first aggregation can take 15–45 minutes
during which the daemon serves an empty cache. Run `--warmup` once to prime the
per-mount snapshot files + clean-shutdown marker; subsequent daemon starts load
the warm snapshot and skip the cold aggregation.

```sh
/boot/config/plugins/shoko-vfs-fuse/start-shoko-vfs-fuse.sh warmup
# ...waits 15–45 min...
# Warmup complete; per-mount snapshots + clean-shutdown marker persisted.
# Next normal daemon start will load the snapshot and skip the cold aggregation.

/boot/config/plugins/shoko-vfs-fuse/start-shoko-vfs-fuse.sh start
# → near-instant start; FUSE mount serves the loaded snapshot immediately.
```

To chain warmup + start in one command (blocks until warmup finishes):

```sh
WARMUP_BEFORE_START=1 /boot/config/plugins/shoko-vfs-fuse/start-shoko-vfs-fuse.sh start
```

For periodic refresh (e.g. after large imports), schedule as a systemd timer or
cron job that runs the `warmup` action on a cadence. The action is idempotent —
re-running it overwrites the snapshot files with the freshest aggregation.
See `BENCHMARKS.md` for the per-library-size timing estimates.

`--warmup` requires the FUSE mountpoints to exist as regular directories (it
briefly mounts then unmounts each managed folder during the reconcile step).
If you changed `ServerPathRoot` / `ManagedFolderPathRoot` since the last run,
ensure the target directories exist before warmup.

## Adapting for non-Unraid hosts

The guide is Unraid-specific in four ways; adapt for other systems:

1. **Install location.** Unraid's `/, /opt, /usr, /var` are a RAM overlay wiped
   on reboot; only `/boot` (flash) and `/mnt/user` (array/cache) persist — so
   the binary, config and helper live under `/boot/config/plugins/shoko-vfs-fuse`.
   Most non-Unraid hosts (Debian/Fedora/Proxmox/NAS distros) have a real `/` and
   keep files across reboots — install anywhere, e.g. `/opt/shoko-vfs-fuse`, and
   override the publish script's install dir: `INSTALL_DIR=/opt/shoko-vfs-fuse`.

2. **Autostart.** Unraid uses `/boot/config/go` (a plain root shell script).
   Other systems use `systemd` (a `.service` unit), SysV init, or a login shell
   — launch `shoko-vfs-fuse-host` the same way, as root, with `--config`.

3. **Path mapping.** Unraid maps the Shoko container's `/mnt/array` to the host's
   `/mnt/user/array`, so the config sets `ServerPathRoot=/mnt/array` and
   `ManagedFolderPathRoot=/mnt/user/array`. On systems where the daemon sees the
   same paths Shoko reports, leave **both empty** (identity mapping) — the daemon
   then uses server paths as-is.

4. **`allow_other` + `/etc/fuse.conf`.** Required for other users/containers/NFS
   to read the mounts. Unraid pick: the deploy helper appends `user_allow_other`.
   Wherever the daemon runs, `FuseAllowOther=true` needs `/etc/fuse.conf`'s
   `user_allow_other`, and the daemon warns at startup if it's missing.

Also note the publish target: `scripts/publish-host.sh` builds `linux-x64`
self-contained. For a different architecture (e.g. `linux-arm64`) run
`dotnet publish ... -r <rid> --self-contained true` directly, and the same
install steps apply. The mount path mapping, `allow_other` and autostart notes
above still apply.
