#!/bin/sh
# start-shoko-vfs-fuse.sh
#
# Launcher for the self-contained Shoko VFS FUSE host daemon on Unraid.
#
# Recommended layout:
#   /mnt/cache/appdata/shoko-vfs-fuse/
#     ├── shoko-vfs-fuse-host
#     ├── config.json
#     └── start-shoko-vfs-fuse.sh
#
# The script is relocatable: by default, the binary and config.json are
# resolved relative to this script's own directory.
#
# Environment overrides:
#   BIN                  Path to the daemon binary
#   CONFIG               Path to config.json
#   LOGDIR               Directory for daemon.log
#   PIDFILE              PID file path
#   WARMUP_BEFORE_START  If set to 1, run --warmup before launching the
#                        daemon in the start action. Blocks until warmup
#                        completes (15–45 min for a large library).
#
# Connection via environment is supported when CONFIG does not exist:
#   SHOKO_URL
#   SHOKO_USER
#   SHOKO_PASS
#   SHOKO_API_KEY
#
# Usage:
#   ./start-shoko-vfs-fuse.sh [start|stop|restart|status|foreground|warmup]
#   ./start-shoko-vfs-fuse.sh --help
#   ./start-shoko-vfs-fuse.sh --version
#
# Warmup:
#   ./start-shoko-vfs-fuse.sh warmup
#       Runs --warmup once: connects to Shoko, performs the full startup
#       reconcile, persists per-mount snapshots + clean-shutdown marker,
#       exits. After warmup the daemon's start action is near-instant
#       (loads the warm snapshot, skips cold aggregation).
#
#   WARMUP_BEFORE_START=1 ./start-shoko-vfs-fuse.sh start
#       Runs warmup synchronously, then launches the daemon. Useful for
#       fresh installs where the daemon should not serve an empty cache
#       during the first reconcile.

set -eu

VERSION="2.1.0"

SCRIPT_DIR=$(
    CDPATH= cd -- "$(dirname -- "$0")" 2>/dev/null
    pwd -P
)

BIN="${BIN:-$SCRIPT_DIR/shoko-vfs-fuse-host}"
CONFIG="${CONFIG:-$SCRIPT_DIR/config.json}"
LOGDIR="${LOGDIR:-$SCRIPT_DIR/logs}"
PIDFILE="${PIDFILE:-/var/run/shoko-vfs-fuse.pid}"

SHOKO_URL="${SHOKO_URL:-}"
SHOKO_USER="${SHOKO_USER:-}"
SHOKO_PASS="${SHOKO_PASS:-}"
SHOKO_API_KEY="${SHOKO_API_KEY:-}"

ACTION="${1:-start}"

usage() {
    cat <<EOF
start-shoko-vfs-fuse.sh v${VERSION}

Usage:
  $0 [start|stop|restart|status|foreground|warmup]
  $0 --help
  $0 --version

Actions:
  start       Launch the daemon in the background (default).
  stop        Stop the running daemon.
  restart     Stop + start.
  status      Print whether the daemon is running.
  foreground  Run the daemon in the foreground (Ctrl-C to stop).
  warmup      One-shot: connect to Shoko, run the full startup reconcile,
              persist per-mount snapshots + clean-shutdown marker, exit.
              Subsequent start actions load the warm snapshot and skip
              the cold aggregation. Safe to run repeatedly; idempotent.

Defaults:
  Script dir: $SCRIPT_DIR
  Binary:     $BIN
  Config:     $CONFIG
  Log dir:    $LOGDIR
  PID file:   $PIDFILE

Environment overrides:
  BIN
  CONFIG
  LOGDIR
  PIDFILE
  WARMUP_BEFORE_START  Set to 1 to run the warmup action automatically
                       before launching the daemon in the start action.
                       Blocks for the duration of the cold aggregation.

NFS export maintenance is configured in config.json (InstallNfsExports,
NfsExportClients) and runs inside the daemon — see deploy/README.md.

If CONFIG does not exist, connection settings may be supplied via:
  SHOKO_URL
  SHOKO_USER
  SHOKO_PASS
  SHOKO_API_KEY

Examples:
  $0 start
  $0 restart
  $0 status

  CONFIG=/somewhere/config.json $0 start

  SHOKO_URL=http://192.168.178.4:8111 \
  SHOKO_API_KEY=... \
  CONFIG=/nonexistent \
  $0 start

  $0 warmup                                 # one-shot cache prime
  WARMUP_BEFORE_START=1 $0 start            # warmup + start, blocking
  WARMUP_BEFORE_START=1 $0 restart          # stop, warmup, start
EOF
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

log() {
    printf '%s\n' "$*"
}

ensure_fuse_conf() {
    fuse_conf="/etc/fuse.conf"
    directive="user_allow_other"

    [ -e /dev/fuse ] || die "/dev/fuse is missing; FUSE is not available on this host."

    if [ ! -f "$fuse_conf" ]; then
        printf '%s\n' "$directive" > "$fuse_conf"
        log "Created $fuse_conf with $directive."
        return
    fi

    if ! grep -qE "^[[:space:]]*${directive}([[:space:]]|$)" "$fuse_conf"; then
        printf '\n%s\n' "$directive" >> "$fuse_conf"
        log "Added $directive to $fuse_conf."
    fi
}

validate_binary() {
    [ -f "$BIN" ] || die "daemon binary not found: $BIN"

    if [ ! -x "$BIN" ]; then
        chmod u+x "$BIN" 2>/dev/null || true
    fi

    [ -x "$BIN" ] || die "daemon binary is not executable: $BIN"
}

validate_connection() {
    if [ -f "$CONFIG" ]; then
        return
    fi

    if [ -z "$SHOKO_URL" ]; then
        echo "ERROR: no connection configured." >&2
        echo "       Config file not found: $CONFIG" >&2
        echo "       Either create that config file or set SHOKO_URL" >&2
        echo "       (and SHOKO_API_KEY, or SHOKO_USER/SHOKO_PASS)." >&2
        exit 1
    fi
}

read_pid() {
    if [ -f "$PIDFILE" ]; then
        cat "$PIDFILE" 2>/dev/null || true
    fi
}

list_daemon_pids() {
    # Live daemon PIDs: from the PID file plus any process whose command
    # line matches the binary (catches rogue/untracked instances). Deduped.
    for pid in $( { read_pid; pgrep -f -- "$BIN" 2>/dev/null; } | sort -u); do
        kill -0 "$pid" 2>/dev/null && printf '%s\n' "$pid"
    done
}

is_running() {
    [ -n "$(list_daemon_pids)" ]
}

remove_stale_pidfile() {
    if [ -f "$PIDFILE" ] && ! is_running; then
        rm -f "$PIDFILE"
    fi
}

stop_daemon() {
    pids="$(list_daemon_pids)"

    if [ -z "$pids" ]; then
        rm -f "$PIDFILE"
        log "Shoko VFS FUSE is not running."
        return 0
    fi

    log "Stopping Shoko VFS FUSE (PID $(echo $pids | tr '\n' ' '))..."

    for pid in $pids; do
        kill "$pid" 2>/dev/null || true
    done

    i=0
    while [ -n "$(list_daemon_pids)" ]; do
        i=$((i + 1))
        if [ "$i" -ge 10 ]; then
            log "Daemon did not stop cleanly; sending SIGKILL."
            for pid in $(list_daemon_pids); do
                kill -9 "$pid" 2>/dev/null || true
            done
            break
        fi
        sleep 1
    done

    rm -f "$PIDFILE"
    log "Stopped."
}

export_connection_env() {
    export SHOKO_URL
    export SHOKO_USER
    export SHOKO_PASS
    export SHOKO_API_KEY
}

start_daemon() {
    if is_running; then
        pids="$(list_daemon_pids)"
        log "Shoko VFS FUSE is already running (PID $(echo $pids | tr '\n' ' '))."
        return 0
    fi

    validate_binary
    validate_connection
    ensure_fuse_conf

    mkdir -p "$LOGDIR"
    mkdir -p "$(dirname -- "$PIDFILE")"

    export_connection_env

    if [ "${WARMUP_BEFORE_START:-0}" = "1" ]; then
        log "WARMUP_BEFORE_START=1: running --warmup before launching the daemon."
        log "This blocks for the duration of the cold aggregation (15–45 min for a large library)."
        warmup_daemon || {
            log "ERROR: warmup failed; not starting daemon." >&2
            exit 1
        }
    fi

    log "Starting Shoko VFS FUSE host daemon..."
    log "  Binary: $BIN"

    if [ -f "$CONFIG" ]; then
        log "  Config: $CONFIG"
    else
        log "  Config: none (using environment)"
        log "  Server: $SHOKO_URL"
    fi

    log "  Log:    $LOGDIR/daemon.log"

    if [ -f "$CONFIG" ]; then
        nohup "$BIN" --config "$CONFIG" \
            >> "$LOGDIR/daemon.log" 2>&1 &
    else
        nohup "$BIN" \
            >> "$LOGDIR/daemon.log" 2>&1 &
    fi

    pid=$!
    printf '%s\n' "$pid" > "$PIDFILE"

    sleep 1

    if ! kill -0 "$pid" 2>/dev/null; then
        rm -f "$PIDFILE"
        echo "ERROR: daemon exited immediately." >&2
        echo "Last log lines:" >&2
        tail -n 40 "$LOGDIR/daemon.log" >&2 2>/dev/null || true
        exit 1
    fi

    log "Started (PID $pid)."
}

foreground_daemon() {
    validate_binary
    validate_connection
    ensure_fuse_conf
    export_connection_env

    if [ -f "$CONFIG" ]; then
        log "Running Shoko VFS FUSE in foreground with config: $CONFIG"
        exec "$BIN" --config "$CONFIG"
    else
        log "Running Shoko VFS FUSE in foreground using environment."
        exec "$BIN"
    fi
}

warmup_daemon() {
    validate_binary
    validate_connection
    ensure_fuse_conf
    export_connection_env

    if [ -f "$CONFIG" ]; then
        log "Running one-shot warmup with config: $CONFIG"
        "$BIN" --warmup --config "$CONFIG"
    else
        log "Running one-shot warmup using environment."
        "$BIN" --warmup
    fi
}

status_daemon() {
    pids="$(list_daemon_pids)"

    if [ -n "$pids" ]; then
        tracked_pid="$(read_pid)"
        log "Shoko VFS FUSE is running (PID $(echo $pids | tr '\n' ' '))."
        case " $pids " in
            *" $tracked_pid "*) ;;
            *) log "WARNING: running instance(s) not tracked by PID file $PIDFILE." ;;
        esac
        log "Binary: $BIN"
        [ ! -f "$CONFIG" ] || log "Config: $CONFIG"
        log "Log:    $LOGDIR/daemon.log"
        return 0
    fi

    rm -f "$PIDFILE"
    log "Shoko VFS FUSE is not running."
    return 1
}

case "$ACTION" in
    start)
        start_daemon
        ;;
    stop)
        stop_daemon
        ;;
    restart)
        stop_daemon
        start_daemon
        ;;
    status)
        status_daemon
        ;;
    foreground)
        foreground_daemon
        ;;
    warmup)
        warmup_daemon
        ;;
    --help|-h|help)
        usage
        ;;
    --version|-V|version)
        echo "start-shoko-vfs-fuse.sh v${VERSION}"
        ;;
    *)
        echo "Unknown action: $ACTION" >&2
        echo >&2
        usage >&2
        exit 2
        ;;
esac
