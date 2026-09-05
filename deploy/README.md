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

## NFS Export

The FUSE mounts appear under `/mnt/user` on Unraid. To share them via NFS:

1. **Unraid GUI** → Settings → NFS → Shares.
2. Add or verify an export for `/mnt/user` (or the specific managed folder subdirectory).
3. Clients mount with: `mount -t nfs <unraid-ip>:/mnt/user /mnt/nfs-shoko`.

The virtual files are then accessible at:
```
/nfs-shoko/<managed-folder>/!ShokoRelayVFS/<series>/<episode>.mkv
```

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
