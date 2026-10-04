#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'Usage: %s <numeric-version>\n' "$(basename "$0")" >&2
    exit 2
fi

for tool in dotnet zip unzip sha256sum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Missing required tool: %s\n' "$tool" >&2
        exit 1
    fi
done

version=$1
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
project="$root_dir/Shoko.VFS.FUSE/Shoko.VFS.FUSE.csproj"
artifacts_dir="$root_dir/artifacts"
stage_dir="$root_dir/.package/Shoko.VFS.FUSE-$version-linux-x64"
publish_dir="$root_dir/.package/publish-$version-linux-x64"
archive_extract_dir=''
archive="$artifacts_dir/Shoko.VFS.FUSE-$version-linux-x64.zip"
checksum="$archive.sha256"

cleanup() {
    if [[ -n ${stage_dir:-} && -d "$stage_dir" ]]; then
        rm -rf -- "$stage_dir"
    fi
    if [[ -n ${publish_dir:-} && -d "$publish_dir" ]]; then
        rm -rf -- "$publish_dir"
    fi
    if [[ -n ${archive_extract_dir:-} && -d "$archive_extract_dir" ]]; then
        rm -rf -- "$archive_extract_dir"
    fi
}
trap cleanup EXIT

[[ -f "$project" ]] || { printf 'Project not found: %s\n' "$project" >&2; exit 1; }
[[ -f "$root_dir/LICENSE" ]] || { printf 'Missing root LICENSE\n' >&2; exit 1; }
[[ -f "$root_dir/README.md" ]] || { printf 'Missing root README.md\n' >&2; exit 1; }
[[ -f "$root_dir/LICENSES/THIRD-PARTY-NOTICES.md" ]] || {
    printf 'Missing third-party notices\n' >&2
    exit 1
}
verifier_project="$root_dir/tools/PackageVerifier/PackageVerifier.csproj"
[[ -f "$verifier_project" ]] || { printf 'Missing package verifier\n' >&2; exit 1; }

mkdir -p -- "$artifacts_dir" "${stage_dir%/*}"
rm -rf -- "$stage_dir"
rm -rf -- "$publish_dir"
rm -f -- "$archive" "$checksum"
mkdir -- "$stage_dir"
mkdir -- "$publish_dir"

printf 'Publishing linux-x64 Release payload...\n'
dotnet publish "$project" \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -p:UseAppHost=false \
    -p:CopyLocalLockFileAssemblies=true \
    -p:Version="$version" \
    --output "$publish_dir"

required_runtime_files=(
    Shoko.VFS.FUSE.dll
    Shoko.VFS.FUSE.deps.json
    FuseDotNet.dll
    LTRData.Extensions.dll
    LTRData.Extensions.Native.dll
    Microsoft.Bcl.HashCode.dll
)
for file in "${required_runtime_files[@]}"; do
    [[ -f "$publish_dir/$file" ]] || {
        printf 'Required published runtime file is missing: %s\n' "$file" >&2
        exit 1
    }
    cp -- "$publish_dir/$file" "$stage_dir/$file"
done

cp -- "$root_dir/LICENSE" "$stage_dir/LICENSE"
cp -- "$root_dir/README.md" "$stage_dir/README.md"
cp -- "$root_dir/scripts/check-fuse-host.sh" "$stage_dir/check-fuse-host.sh"
mkdir -- "$stage_dir/LICENSES"
cp -- "$root_dir/LICENSES/THIRD-PARTY-NOTICES.md" "$stage_dir/LICENSES/THIRD-PARTY-NOTICES.md"

required_files=(
    LICENSE
    README.md
    check-fuse-host.sh
    LICENSES/THIRD-PARTY-NOTICES.md
    Shoko.VFS.FUSE.dll
    Shoko.VFS.FUSE.deps.json
    FuseDotNet.dll
    LTRData.Extensions.dll
    LTRData.Extensions.Native.dll
    Microsoft.Bcl.HashCode.dll
)

for file in "${required_files[@]}"; do
    [[ -f "$stage_dir/$file" ]] || {
        printf 'Required payload file is missing: %s\n' "$file" >&2
        exit 1
    }
done

expected_list=$(mktemp)
actual_list=$(mktemp)
trap 'rm -f -- "$expected_list" "$actual_list"; cleanup' EXIT
printf '%s\n' "${required_files[@]}" | LC_ALL=C sort > "$expected_list"

(
    cd -- "$stage_dir"
    LC_ALL=C find . -type f -printf '%P\n' | LC_ALL=C sort
) > "$actual_list"
if ! cmp -s "$expected_list" "$actual_list"; then
    printf 'Unexpected publish payload; allowed files are:\n' >&2
    diff -u "$expected_list" "$actual_list" >&2 || true
    exit 1
fi

dotnet run --project "$verifier_project" -c Release -- "$stage_dir" "$version"

source_date_epoch=${SOURCE_DATE_EPOCH:-315532800}
if [[ ! $source_date_epoch =~ ^[0-9]+$ ]]; then
    printf 'SOURCE_DATE_EPOCH must be a non-negative integer\n' >&2
    exit 1
fi
while IFS= read -r file; do
    touch -d "@$source_date_epoch" "$stage_dir/$file"
done < "$actual_list"

(
    cd -- "$stage_dir"
    LC_ALL=C find . -type f -printf '%P\n' | LC_ALL=C sort | zip -q -X "$archive" -@
)

(
    cd -- "$artifacts_dir"
    sha256sum "$(basename -- "$archive")" > "$(basename -- "$checksum")"
    sha256sum -c "$(basename -- "$checksum")"
    unzip -Z1 "$(basename -- "$archive")" | LC_ALL=C sort > "$actual_list"
)
if ! cmp -s "$expected_list" "$actual_list"; then
    printf 'Archive contents differ from the validated payload\n' >&2
    diff -u "$expected_list" "$actual_list" >&2 || true
    exit 1
fi

archive_extract_dir=$(mktemp -d)
unzip -q "$archive" -d "$archive_extract_dir"
dotnet run --project "$verifier_project" -c Release --no-build --no-restore -- "$archive_extract_dir" "$version"

printf 'Created %s\n' "$archive"
printf 'Created %s\n' "$checksum"
