# Shoko.VFS.FUSE

Linux FUSE virtual filesystem for Shoko Server. Two deliverables from the same
repository:

- **Shoko.VFS.FUSE** — in-tree plugin that runs inside the Shoko Server
  process. The "classic" deployment for setups where Shoko and the FUSE
  consumers share a host and user.
- **Shoko.VFS.FUSE.Host** — standalone daemon that talks to Shoko over REST
  and SignalR, mounts the FUSE filesystem from outside Shoko. Recommended
  for containerized Shoko, remote Shoko hosts, or when you want the FUSE
  mount to outlive a Shoko Server crash.

Both projects share the same Relay projection / resolver / naming code from
`Shoko.VFS.FUSE`. The host daemon adds an outage-resilient cache layer,
AnimeThemes `Theme.mp3` exposure (replacing the symlink step ShokoRelay skips
under `Advanced.UseExternalVfs`), and a health endpoint suitable for
container orchestration.

- Troubleshooting: see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
- Common questions: see [FAQ.md](FAQ.md).
- Vulnerability reporting: see [SECURITY.md](SECURITY.md).
- Change log: see [CHANGELOG.md](CHANGELOG.md).

## Release archive

The archive is framework-dependent and targets `linux-x64`. It carries the
plugin's private managed dependencies, but does not carry a .NET runtime or
native FUSE components.

Install through the Shoko plugin package manager when possible. Every install or
upgrade requires a Shoko **RESTART** so hosted-service registration is rebuilt.
For a manual installation, stop Shoko, remove or replace the old dedicated
plugin directory (never overlay it), extract the ZIP contents as one direct
child of `PluginsPath`, and restart Shoko. Keep the archive's flat layout intact.
For upgrades, stop Shoko, remove or replace the old dedicated plugin directory
(never overlay it), extract the new archive, and restart Shoko.

Verify the checksum before installation:

```sh
sha256sum -c Shoko.VFS.FUSE-<version>-linux-x64.zip.sha256
```

## Host prerequisites

The host must provide all of the following; the plugin never installs them:

- Linux x86-64 and a .NET 10 host runtime.
- This plugin requires a compatible Shoko Server exposing the referenced
  `Shoko.Abstractions`/.NET 10 ABI.
- Kernel FUSE support and `/dev/fuse` accessible to the Shoko process.
- One of the compatible dynamically resolvable `libfuse3` runtimes
  `libfuse3.so.3`, `libfuse3.so.4`, or `libfuse3.so`, plus an executable
  `fusermount3`.
- No `libfuse3-dev` package or unversioned development symlink is required when
  a supported versioned runtime is present.
- A usable mount namespace and either a permitted `fusermount3` helper or the
  capability required by the host's FUSE setup.

Run the non-installing checker before deployment from the repository root:

```sh
scripts/check-fuse-host.sh
```

The release archive also contains the checker at its root:

```sh
./check-fuse-host.sh
```

The checker hard-fails only missing core static prerequisites and reports
device permissions, namespace, and capability concerns as warnings or unknown.
It does not use sudo, a package manager, or modify the host. Static checks do
not replace a real mount/read/unmount test.

## Configuration and health

Relay is disabled by default. Enable the Relay mount in the plugin
configuration. Relay roots are created as
direct children of eligible managed folders using the configured
`!ShokoRelayVFS` and, when enabled, `!ShokoRelayMovieVFS` names. The plugin
refuses to claim non-empty legacy roots or mounts it does not own.

Fin is unsupported. Local extras/assets are unsupported; physical Relay
materialization, link cleanup, and asset creation are not performed by this
virtual filesystem.

Runtime health is available at:

```text
/api/plugin/Shoko.VFS.FUSE/health
```

The response contains sanitized states, reason/capability codes, managed-folder
IDs, and counts; it does not expose source paths or exception text.

## Target-host evidence

The final clean-install/discovery target-host sequence remains pending in this
development environment. Its compatible `libfuse3` runtime is detected and the
focused real-FUSE tests pass. On a FUSE-capable target:

1. Extract the archive to a clean, dedicated child of `PluginsPath`.
2. Restart Shoko.
3. Verify plugin discovery identity, version, `linux-x64` RID, and dependency
   loading.
4. Query `/api/plugin/Shoko.VFS.FUSE/health`.
5. Mount, read a representative file, and unmount; confirm cleanup and status.

## Building a release

From the repository root:

```sh
scripts/package-linux-x64.sh <version>
```

`<version>` must be a dotted numeric version such as `0.1.0`. The script
publishes Release `linux-x64` framework-dependent output, rejects unexpected
files and host assemblies, validates the plugin metadata and dependency graph,
and writes:

```text
artifacts/Shoko.VFS.FUSE-<version>-linux-x64.zip
artifacts/Shoko.VFS.FUSE-<version>-linux-x64.zip.sha256
```

The archive includes `LICENSE`, `LICENSES/THIRD-PARTY-NOTICES.md`, and this
README, plus the root `check-fuse-host.sh`. Native FUSE support remains a
deployment-host responsibility.

For focused dependency-free verifier coverage, run:

```sh
tools/PackageVerifier/self-check.sh \
  artifacts/Shoko.VFS.FUSE-<version>-linux-x64.zip <version>
```

The self-check covers valid payloads, missing and extra runtime DLLs, wrong
version, and wrong `PackageID`/`RuntimeIdentifier` metadata.
