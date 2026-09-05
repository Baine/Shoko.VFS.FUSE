# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — host daemon (Shoko.VFS.FUSE.Host)

- **Outage-resilient caching.** Per-mount aggregation caches freeze while
  the Shoko Server is unreachable; reads keep returning the last known
  snapshot instead of failing on every access. Unfreeze + reconcile on
  recovery.
- **Persistent snapshots.** Caches flush to
  `$XDG_DATA_HOME/shoko-vfs-fuse/snapshots/` on clean shutdown, with a
  `.clean_shutdown` marker so the next start can distinguish a graceful exit
  from a crash. Unclean snapshots are discarded and rebuilt.
- **Server availability monitor.** Background ping loop
  (`ServerPollInterval`, `MaxServerProbeFailures`) with `ServerUp` /
  `ServerDown` / `ServerReady` events. Surfaces `serverUp`, `serverReady`,
  `lastServerProbeAt`, `lastServerProbeError` on the health endpoint.
- **HTTP retry / backoff.** Transient failures (5xx, 408, 429, connection
  resets, request timeouts) retry with exponential backoff
  (`HttpRetries`, default 2). `HttpClient.Timeout` defaults to 10 minutes
  (`RequestTimeout`) instead of the .NET default 100 s, so big-library
  aggregations are not aborted mid-fetch.
- **AnimeThemes Theme.mp3 exposure.** `Theme.mp3` files written by
  ShokoRelay's `AnimeThemesMp3Generator` into source folders are discovered
  during aggregation and surfaced as
  `<TvRoot>/<seriesId>/Theme.mp3` /
  `<MovieRoot>/<episodeId>/Theme.mp3` in the FUSE mount. Replaces the
  symlinking that ShokoRelay skips when
  `Advanced.UseExternalVfs = true`.
- **Stale-mount cleanup.** Parses `/proc/self/mountinfo` on startup,
  unmounts prior-run FUSE mounts of `!ShokoRelayVFS` /
  `!ShokoRelayMovieVFS` automatically.
- **Self-test mode.** `--selftest` runs 15 invariant assertions without
  touching the network or filesystem; suitable for CI.

### Security

- `.gitignore` now excludes `.env`, `.env.*`, `*.local.json`,
  `config.local.json` to prevent accidental secret commits.

[Unreleased]: https://github.com/Baine/Shoko.VFS.FUSE/compare/HEAD
