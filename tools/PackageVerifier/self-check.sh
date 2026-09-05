#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
    printf 'Usage: %s <package-zip> <numeric-version>\n' "$(basename "$0")" >&2
    exit 2
fi
for tool in dotnet unzip perl; do
    command -v "$tool" >/dev/null 2>&1 || {
        printf 'Missing self-check tool: %s\n' "$tool" >&2
        exit 1
    }
done

archive=$(realpath -- "$1")
version=$2
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project="$script_dir/PackageVerifier.csproj"
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT

dotnet build "$project" -c Release --nologo
valid_dir="$work_dir/valid"
mkdir -- "$valid_dir"
unzip -q "$archive" -d "$valid_dir"
verifier=(dotnet run --project "$project" -c Release --no-build --)

"${verifier[@]}" "$valid_dir" "$version" >/dev/null

expect_failure() {
    if "${verifier[@]}" "$1" "$2" >/dev/null 2>&1; then
        printf 'Self-check case unexpectedly passed: %s\n' "$3" >&2
        exit 1
    fi
}

missing_dir="$work_dir/missing-runtime"
cp -a -- "$valid_dir" "$missing_dir"
rm -- "$missing_dir/FuseDotNet.dll"
expect_failure "$missing_dir" "$version" 'missing runtime asset'

extra_dir="$work_dir/extra-runtime"
cp -a -- "$valid_dir" "$extra_dir"
cp -- "$extra_dir/LICENSE" "$extra_dir/Unexpected.dll"
expect_failure "$extra_dir" "$version" 'extra runtime asset'

wrong_version_dir="$work_dir/wrong-version"
cp -a -- "$valid_dir" "$wrong_version_dir"
perl -pi -e "s/Shoko\\.VFS\\.FUSE\\/$version/Shoko.VFS.FUSE\\/0.1.1/g" "$wrong_version_dir/Shoko.VFS.FUSE.deps.json"
expect_failure "$wrong_version_dir" '0.1.1' 'wrong AssemblyVersion'

wrong_id_dir="$work_dir/wrong-id"
cp -a -- "$valid_dir" "$wrong_id_dir"
perl -pi -e 's/c6b59b26-9fd1-4220-ab6d-0b2d92e19022/00000000-0000-0000-0000-000000000000/g' "$wrong_id_dir/Shoko.VFS.FUSE.dll"
expect_failure "$wrong_id_dir" "$version" 'wrong PackageID metadata'

wrong_rid_dir="$work_dir/wrong-rid"
cp -a -- "$valid_dir" "$wrong_rid_dir"
perl -pi -e 's/linux-x64/linux-musl/g' "$wrong_rid_dir/Shoko.VFS.FUSE.dll"
expect_failure "$wrong_rid_dir" "$version" 'wrong RuntimeIdentifier metadata'

printf 'Package verifier self-check: PASS (valid, missing, extra, version, PackageID, and RID cases)\n'
