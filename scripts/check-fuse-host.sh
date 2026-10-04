#!/usr/bin/env bash
set -euo pipefail

failures=0
warnings=0
fail() {
    printf 'FAIL: %s\n' "$1"
    failures=$((failures + 1))
}
warn() {
    printf 'WARN: %s\n' "$1"
    warnings=$((warnings + 1))
}

if [[ $(uname -s) != Linux ]]; then
    fail 'the host OS must be Linux'
fi

architecture=$(uname -m)
if [[ "$architecture" != x86_64 && "$architecture" != amd64 ]]; then
    fail "the host architecture must be x86-64 (found $architecture)"
fi

if [[ ! -e /dev/fuse ]]; then
    fail '/dev/fuse is missing'
elif [[ ! -c /dev/fuse ]]; then
    fail '/dev/fuse is not a character device'
elif [[ ! -r /dev/fuse || ! -w /dev/fuse ]]; then
    warn '/dev/fuse exists but device access is not readable and writable by this process'
fi

fusermount3=$(command -v fusermount3 || true)
if [[ -z "$fusermount3" || ! -x "$fusermount3" ]]; then
    fail 'an executable fusermount3 helper is missing'
else
    if [[ ! -u "$fusermount3" ]]; then
        cap_eff=''
        if [[ -r /proc/self/status ]]; then
            while read -r name value _; do
                if [[ "$name" == CapEff: ]]; then
                    cap_eff=$value
                    break
                fi
            done < /proc/self/status
        fi
        if [[ -z "$cap_eff" ]]; then
            warn 'fusermount3 is not setuid; CAP_SYS_ADMIN state is unknown'
        elif ! (( (16#$cap_eff & 0x200000) != 0 )); then
            warn 'fusermount3 is not setuid and CAP_SYS_ADMIN is not effective'
        fi
    fi
fi

if [[ ! -r /proc/self/mountinfo || ! -e /proc/self/ns/mnt ]]; then
    warn 'the mount namespace could not be confirmed through /proc'
fi

if [[ ! -r /proc/self/status ]]; then
    warn 'process capability state could not be inspected'
elif ! LC_ALL=C grep -q '^CapEff:' /proc/self/status; then
    warn 'effective process capabilities could not be inspected'
fi

has_libfuse=false
is_supported_libfuse_name() {
    case "$1" in
        libfuse3.so.3|libfuse3.so.4|libfuse3.so)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}
if command -v ldconfig >/dev/null 2>&1; then
    ldconfig_cache=$(ldconfig -p 2>/dev/null || true)
    while read -r soname _; do
        if is_supported_libfuse_name "$soname"; then
            has_libfuse=true
            break
        fi
    done <<< "$ldconfig_cache"
fi
if [[ "$has_libfuse" != true ]]; then
    for directory in \
        /lib* /usr/lib* /usr/local/lib* \
        /lib/*-linux-gnu /usr/lib/*-linux-gnu /usr/local/lib/*-linux-gnu; do
        [[ -d "$directory" ]] || continue
        for soname in libfuse3.so.3 libfuse3.so.4 libfuse3.so; do
            if [[ -e "$directory/$soname" ]]; then
                has_libfuse=true
                break 2
            fi
        done
    done
fi
if [[ "$has_libfuse" != true ]]; then
    fail 'no compatible libfuse3 runtime is available'
fi

if (( failures != 0 )); then
    printf 'FUSE host prerequisites: FAILED (%d issue(s))\n' "$failures"
    exit 1
fi

printf 'FUSE host static checks: PASS (Linux x86-64)\n'
printf 'A real FUSE mount/read/unmount is still required.\n'
if (( warnings != 0 )); then
    printf 'FUSE host checks: %d warning(s) remain environment-dependent\n' "$warnings"
fi
