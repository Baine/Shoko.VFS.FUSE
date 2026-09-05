#!/usr/bin/env bash
# publish-host.sh — build the standalone Shoko.VFS.FUSE host daemon for a host OS.
#
# This targets **Unraid** by default: it produces a self-contained linux-x64
# binary (bundles the .NET 10 runtime, so nothing needs installing on the host)
# packed in a tarball laid out for /boot/config/plugins/shoko-vfs-fuse/.
#
# Unraid-specific assumptions you may need to ADAPT for other systems:
#   - Persistent install root  : /boot/config/plugins/shoko-vfs-fuse  (flash)
#     OTHER systems: Unraid's /, /opt, /usr are a RAM overlay wiped on reboot;
#     only /boot (flash) and /mnt/user (array/cache) persist. Non-Unraid hosts
#     (Debian/Fedora/Proxmox/NAS distros) usually have a real / — install
#     anywhere, e.g. /opt/shoko-vfs-fuse. Override INSTALL_DIR below.
#   - Mount path mapping       : ServerPathRoot=/mnt/array ->
#     ManagedFolderPathRoot=/mnt/user/array in the shipped example config.
#     OTHER systems: if the daemon sees the same paths Shoko reports, leave both
#     empty (identity mapping) — see deploy/README.md.
#   - allow_other              : needs /etc/fuse.conf with `user_allow_other`;
#     the deploy script ensures it. Required for NFS + containers to read mounts.
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
# Layout on Unraid: everything persists under /boot (flash).
# NOTE for other systems: change this to a persistent dir of your choice.
INSTALL_DIR="${INSTALL_DIR:-/boot/config/plugins/shoko-vfs-fuse}"
BIN_NAME="${BIN_NAME:-shoko-vfs-fuse-host}"

if [[ $# -gt 1 ]]; then
    printf 'Usage: %s [output-dir]\n' "$(basename "$0")" >&2
    exit 2
fi
OUT_DIR="${1:-artifacts}"

for tool in dotnet tar; do
    command -v "$tool" >/dev/null 2>&1 || {
        printf 'Missing required tool: %s\n' "$tool" >&2
        exit 1
    }
done

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
project="$root_dir/Shoko.VFS.FUSE.Host/Shoko.VFS.FUSE.Host.csproj"
publish_dir="$root_dir/.publish-host"
archive="$OUT_DIR/shoko-vfs-fuse-host-linux-x64.tar.gz"

cleanup() { rm -rf -- "$publish_dir"; }
trap cleanup EXIT

mkdir -p -- "$OUT_DIR"
rm -rf -- "$publish_dir"

printf 'Publishing self-contained linux-x64 host daemon...\n'
dotnet publish "$project" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$publish_dir"

apphost="$publish_dir/Shoko.VFS.FUSE.Host"
[[ -f "$apphost" ]] || { printf 'Apphost not found after publish: %s\n' "$apphost" >&2; exit 1; }

# Rename apphost to the name $BIN defaults to in deploy/start-shoko-vfs-fuse.sh.
mv -- "$apphost" "$publish_dir/$BIN_NAME"
mkdir -p "$publish_dir"
cp -- "$root_dir/Shoko.VFS.FUSE.Host/Config/config.sample.json" \
      "$publish_dir/config.example.json"

# Root-run helper: drop it alongside the binary so it survives on flash.
cp -- "$root_dir/deploy/start-shoko-vfs-fuse.sh" "$publish_dir/start-shoko-vfs-fuse.sh"
chmod +x "$publish_dir/start-shoko-vfs-fuse.sh" "$publish_dir/$BIN_NAME"

mkdir -p -- "$OUT_DIR"
rm -f -- "$archive"
tar -C "$publish_dir" -czf "$archive" .

printf '\nCreated %s\n' "$archive"
printf 'Extract to %s on the Unraid host:\n' "$INSTALL_DIR"
printf '  mkdir -p %s\n' "$INSTALL_DIR"
printf '  tar -xzf %s -C %s\n' "$archive" "$INSTALL_DIR"
printf '\nThe daemon is self-contained; nothing else needs installing.\n'
printf 'See deploy/README.md for config, NFS and the /boot/config/go entry.\n'
