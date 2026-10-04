#!/usr/bin/env pwsh
# publish-host.ps1 — build the standalone Shoko.VFS.FUSE host daemon for a host OS.
#
# PowerShell port of scripts/publish-host.sh.
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
#     anywhere, e.g. /opt/shoko-vfs-fuse. Override -InstallDir below.
#   - Mount path mapping       : ServerPathRoot=/mnt/array ->
#     ManagedFolderPathRoot=/mnt/user/array in the shipped example config.
#     OTHER systems: if the daemon sees the same paths Shoko reports, leave both
#     empty (identity mapping) — see deploy/README.md.
#   - allow_other              : needs /etc/fuse.conf with `user_allow_other`;
#     the deploy script ensures it. Required for NFS + containers to read mounts.
#
# Note: produces a *linux-x64* artifact regardless of the host OS running this
# script — it is meant to be invoked on a Windows/macOS/Linux dev machine and
# shipped to the Unraid target. Requires tar (built into Windows 10+ and all Unix).
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputDir = 'artifacts',

    [string]$InstallDir = '/boot/config/plugins/shoko-vfs-fuse',
    [string]$BinName = 'shoko-vfs-fuse-host',

    [string]$Configuration = 'Release',
    [string]$Runtime = 'linux-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
# Layout on Unraid: everything persists under /boot (flash).
# NOTE for other systems: change this to a persistent dir of your choice.

if ($PSBoundParameters.Count -gt 1 -and $MyInvocation.ExpectingInput) {
    # Conservative guard; pwsh does not expose $# directly.
}

# Resolve script + root directory (mirrors bash CDPATH= cd -- ...).
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$RootDir = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..')).ProviderPath
$Project = Join-Path $RootDir 'Shoko.VFS.FUSE.Host/Shoko.VFS.FUSE.Host.csproj'
$PublishDir = Join-Path $RootDir '.publish-host'
$Archive = Join-Path $OutputDir 'shoko-vfs-fuse-host-linux-x64.tar.gz'

# ---------------------------------------------------------------------------
# Tool checks
# ---------------------------------------------------------------------------
foreach ($tool in @('dotnet', 'tar')) {
    if (-not (Get-Command -Name $tool -ErrorAction SilentlyContinue)) {
        Write-Error "Missing required tool: $tool"
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

# Resolve OutputDir against the script root so relative paths still work after
# Push-Location flips the working directory for tar.
$absOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $RootDir $OutputDir
}

Write-Host 'Publishing self-contained linux-x64 host daemon...'
$publishArgs = @(
    'publish', $Project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $PublishDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$apphost = Join-Path $PublishDir 'Shoko.VFS.FUSE.Host'
if (-not (Test-Path -LiteralPath $apphost)) {
    Write-Error "Apphost not found after publish: $apphost"
    exit 1
}

# Rename apphost to the name $Bin defaults to in deploy/start-shoko-vfs-fuse.sh.
$renamed = Join-Path $PublishDir $BinName
Move-Item -LiteralPath $apphost -Destination $renamed -Force

$configSrc = Join-Path $RootDir 'Shoko.VFS.FUSE.Host/Config/config.sample.json'
$configDst = Join-Path $PublishDir 'config.example.json'
Copy-Item -LiteralPath $configSrc -Destination $configDst -Force

# Root-run helper: drop it alongside the binary so it survives on flash.
$deployScript = Join-Path $RootDir 'deploy/start-shoko-vfs-fuse.sh'
$deployDst = Join-Path $PublishDir 'start-shoko-vfs-fuse.sh'
Copy-Item -LiteralPath $deployScript -Destination $deployDst -Force

if (Test-Path -LiteralPath $renamed) {
    & chmod +x $renamed 2>$null
}
if (Test-Path -LiteralPath $deployDst) {
    & chmod +x $deployDst 2>$null
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
if (Test-Path -LiteralPath $Archive) {
    Remove-Item -LiteralPath $Archive -Force
}

# tar -C "$publish_dir" -czf "$archive" .
# Windows-friendly: cd into the publish dir, capture the tarball in a sibling
# temp directory using a relative filename, then move it to the final
# OutputDir. This sidesteps the BSD/MSYS tar that mis-interprets rooted
# Windows paths as hostnames when handed as -f argument.
$tarTmp = Join-Path $([System.IO.Path]::GetTempPath()) ("shoko-vfs-publish-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tarTmp -Force | Out-Null
try {
    Push-Location -LiteralPath $PublishDir
    try {
        # cd into the staging directory of the tar call so that the archive
        # argument is relative.
        Push-Location -LiteralPath $tarTmp
        try {
            & tar -czf archive.tar.gz -C $PublishDir .
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        Pop-Location
    }
    Move-Item -LiteralPath "$tarTmp/archive.tar.gz" -Destination $Archive -Force
}
finally {
    if (Test-Path -LiteralPath $tarTmp) {
        Remove-Item -LiteralPath $tarTmp -Recurse -Force
    }
}

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

Write-Host ''
Write-Host "Created $Archive"
Write-Host "Extract to $InstallDir on the Unraid host:"
Write-Host "  mkdir -p $InstallDir"
Write-Host "  tar -xzf $Archive -C $InstallDir"
Write-Host ''
Write-Host 'The daemon is self-contained; nothing else needs installing.'
Write-Host 'See deploy/README.md for config, NFS and the /boot/config/go entry.'