#!/usr/bin/env bash
# install-nfs-exports.sh — keep the relay FUSE mounts visible over NFS.
#
# knfsd cannot cross into FUSE submounts of an exported FUSE parent (shfs),
# so every fuse.shoko-vfs mount needs its own export entry. This script
# regenerates /etc/exports.d/shoko-vfs.exports from the mounts that exist
# right now and reloads the NFS exports. Idempotent: run it after every
# daemon start (mount set only changes then) — e.g. from the go file,
# User Scripts at array start, or start-shoko-vfs-fuse.sh.
#
# Export policy mirrors the parent array export: rw, all_squash to
# nobody:users, sec=sys, for the clients listed below.
set -euo pipefail

EXPORTS_FILE="${EXPORTS_FILE:-/etc/exports.d/shoko-vfs.exports}"
CLIENTS="${CLIENTS:-192.168.178.20}"

[[ $(id -u) -eq 0 ]] || { echo "must run as root" >&2; exit 1; }

mkdir -p -- "$(dirname -- "$EXPORTS_FILE")"

tmp=$(mktemp)
trap 'rm -f -- "$tmp"' EXIT

# fuse.shoko-vfs mounts, one per line: "<fs> on <path> type fuse.shoko-vfs ..."
# NFSv4 exports of FUSE filesystems require an explicit fsid (no UUID support);
# derive a stable 32-bit one from the path so client file handles survive re-runs.
mount | awk '$0 ~ / type fuse\.shoko-vfs / { print $3 }' | sort -u | while read -r path; do
    fsid=$((16#$(printf %s "$path" | md5sum | cut -c1-8) % 4294967295 + 1))
    for client in $CLIENTS; do
        printf '%s %s(rw,fsid=%d,all_squash,anonuid=99,anongid=100,sec=sys,no_subtree_check)\n' \
            "$path" "$client" "$fsid"
    done
done > "$tmp"

if cmp -s -- "$tmp" "$EXPORTS_FILE" 2>/dev/null; then
    echo "NFS exports up to date ($(grep -c . "$tmp") entries)"
    exit 0
fi

install -m 0644 -- "$tmp" "$EXPORTS_FILE"
exportfs -ra
echo "installed $(grep -c . "$EXPORTS_FILE") NFS export entries to $EXPORTS_FILE"
