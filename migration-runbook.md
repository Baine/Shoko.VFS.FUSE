# ShokoRelay to FUSE Relay VFS Migration

## Scope

This swaps ShokoRelay's **physical symlink VFS** for FUSE's **dynamic mount** at the same managed-folder roots. It does not move, copy, or rename source media files.

Do not let both implementations own the same root. ShokoRelay builds and cleans physical directory trees; FUSE mounts a filesystem over an empty or missing target directory.

## Configuration

FUSE and ShokoRelay have independent configuration files. Do not move or rename ShokoRelay's configuration when enabling FUSE.

### FUSE configuration migration

The legacy FUSE configuration location is:

```text
<DataPath>/Shoko.VFS.FUSE/config.json
```

The current plugin stores its configuration at:

```text
<DataPath>/configuration/c6b59b26-9fd1-4220-ab6d-0b2d92e19022/Shoko.VFS.FUSE.json
```

The configuration service does not migrate the legacy file. Before restarting with the updated plugin, copy it so the old file remains a rollback backup:

```bash
mkdir -p "<DataPath>/configuration/c6b59b26-9fd1-4220-ab6d-0b2d92e19022"
cp -a "<DataPath>/Shoko.VFS.FUSE/config.json" \
  "<DataPath>/configuration/c6b59b26-9fd1-4220-ab6d-0b2d92e19022/Shoko.VFS.FUSE.json"
```

ShokoRelay uses its own `preferences.json` and may prefer a legacy `config` directory beside its installed assembly. Its modern location is:

```text
<DataPath>/configuration/2b0f5a7e-3d2b-4f3d-9e6b-7f0a6b2d8c9a/preferences.json
```

Do not copy ShokoRelay settings into FUSE. Configure FUSE explicitly.

## Migration

1. Back up each existing ShokoRelay VFS root and both plugins' configuration files. Back up generated VFS trees for rollback; source media remains in its managed-folder location.

2. Record ShokoRelay's configured TV and movie root names. The defaults are `!ShokoRelayVFS` and `!ShokoRelayMovieVFS`, but ShokoRelay can use custom names. Set FUSE's `RelayTvFolderName` and, when movies are enabled, `RelayMovieFolderName` to the intended roots.

3. In the local ShokoRelay fork, enable **Advanced → Use External VFS**. It suppresses ShokoRelay's physical VFS build, cleanup, audit, AnimeThemes VFS links, and `Theme.mp3` VFS links. On an unmodified ShokoRelay build, stop or disable every VFS build and cleanup task instead. ShokoRelay's unrelated features may remain enabled only if they will not write these roots.

4. Confirm FUSE is not mounted at any target before changing its physical directory:

   ```bash
   mountpoint -q "<managed-folder>/<root>" && echo "still mounted"
   ```

5. Rename each physical ShokoRelay root after ShokoRelay has stopped modifying it. Do not delete it during the initial migration:

   ```bash
   mv "<managed-folder>/!ShokoRelayVFS" \
     "<managed-folder>/!ShokoRelayVFS.shokorelay-backup"
   mv "<managed-folder>/!ShokoRelayMovieVFS" \
     "<managed-folder>/!ShokoRelayMovieVFS.shokorelay-backup"
   ```

   Skip the movie root if ShokoRelay did not generate one. FUSE rejects a non-empty mount target, so the original tree must be moved away first. FUSE creates a missing target directory or accepts an empty one.

6. Enable FUSE Relay. It mounts the configured root in every eligible managed folder; `MovieGenerationMode` must not be disabled for the movie root. Use FUSE's managed-folder exclusions to limit the first deployment to a single managed folder if desired.

7. Verify the intended paths are mounted and browse them from the consuming media server. Check representative shows, movies, and source-file reads. A mounted FUSE root is dynamic; it is not a replacement symlink tree on disk.

## Rollback

1. Disable FUSE Relay and confirm every target has unmounted.
2. Remove only the now-empty FUSE mountpoint directory, if it exists:

   ```bash
   rmdir "<managed-folder>/!ShokoRelayVFS"
   rmdir "<managed-folder>/!ShokoRelayMovieVFS"
   ```

   Skip a missing movie root. `rmdir` deliberately fails rather than remove a non-empty directory.

3. Rename the backed-up ShokoRelay roots back to their original names.
4. Re-enable ShokoRelay VFS generation and run its build.

## Safety rules

- Never run a ShokoRelay VFS build or cleanup against a FUSE-mounted root.
- Never rename, delete, or inspect a mounted root as though it were the underlying physical directory; unmount FUSE first.
- Do not claim a conflict is harmless: ShokoRelay can delete and recreate its roots during cleanup, while FUSE refuses non-empty or already-mounted targets.
- The migration changes VFS presentation only. Moving or deleting source media affects both implementations.
