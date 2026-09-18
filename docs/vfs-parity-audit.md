# VFS Parity Audit: ShokoRelay vs Shoko.VFS.FUSE (daemon + in-tree plugin)

Audit date: 2026-09-18. **Both root causes below are FIXED** (2026-09-18):
cross-folder source fallback for extras (host + plugin datasources) and the
host's episode "SHOKO" title baseline (`AniDB.Title` instead of default-titled
`Name`). Builds clean; test suite green (155 relay tests incl. 1 new
regression test; 3 pre-existing env-dependent OnePiece diagnostics excluded). Scope: how/where/why each system creates its VFS entries,
the naming schemes, the extras/Featurettes classification, and the root causes of
observed divergences (name mismatch, missing featurette).

Assumption used throughout: in the user's comparison, `!ShokoRelayMovieVFS` is
Relay's symlink tree and `!ShokoRelayMovieVFS_old` is the FUSE mount.

---

## 1. ShokoRelay (symlink tree)

**Mechanism.** Relay writes a real symlink tree into each Shoko import root.
Single OS primitive: `File.CreateSymbolicLink` (`Vfs/VfsShared.cs:235`), only
reached via `VfsShared.TryCreateLink` (`VfsShared.cs:196-246`). All builds are
serialized by a global `SemaphoreSlim VfsLock` (`VfsShared.cs:21`).

**Triggers.** `GET /vfs` (`Controllers/ShokoController.cs:41`), `GET /vfs/audit`,
the `VfsWatcher` background loop on Shoko import events (`VfsWatcher.cs:180`),
AnimeThemes apply, manual `POST /map-symlinks` (`Services/SourceLinkService.cs`).

**Link sites** (all in `ShokoRelay/ShokoRelay/`):

| Site | Location | Links |
|---|---|---|
| TV episode file | `VfsBuilder.cs:483` | video |
| Movie main file | `VfsBuilder.cs:621` | video |
| Movie extras | `VfsBuilder.cs:692` | non-main episodes → `<movieRoot>/<epId>/<ExtrasFolder>/` |
| AnimeThemes webm/mp3 | `VfsBuilder.cs:722`, `AnimeThemesMp3Generator.cs:418` | `Shorts/` |
| Series metadata | `VfsAssetLinker.cs:55` | posters/NFO |
| Episode sidecars | `VfsAssetLinker.cs:105,148,154` | subs/NFO/chapters |
| Attachment dirs | `VfsAssetLinker.cs:221` | `_attach` |
| Plex local extras | `VfsAssetLinker.cs:277` (call site `VfsBuilder.cs:527`, **TV branch only**) | unmanaged physical extra dirs/files |
| Manual source map | `SourceLinkService.cs:121,123` | user mapping file |

**Roots / structure.**
- Roots: `!ShokoRelayVFS` and `!ShokoRelayMovieVFS` (`ShokoRelayConstants.cs:49,52`,
  configurable; `ConfigProvider.cs:472` forces a `_Fallback` suffix if equal).
- TV: `<importRoot>/!ShokoRelayVFS/<primarySeriesId>/<SeasonFolder>/`.
- Movie: keyed by **main episode id** (`VfsBuilder.cs:593`), not series id.
- Season folders (`PlexConstants.cs:116`): `-1`→Shorts, `-2`→Trailers,
  `-3`→Scenes, `-4`→**Featurettes**, `-9`→Other, `0`→Specials, else `Season N`.
- TV/Movie split: `MapHelper.GetGenerationModes` (`MapHelper.cs:46`),
  `IsMovie` (`MapHelper.cs:109`).

**Episode-type → season coordinate** (`PlexMapping.GetPlexCoordinates`,
`PlexMapping.cs:51`): Episode→1, Special→0, Credits→-1, Trailer→-2, Parody→-3,
**Other→-4** (Featurettes, prefix `O`).

**`-4` promotion remap** (`MapHelper.BuildFileMappings`, `MapHelper.cs:206-210`):
a featurette keeps its `Featurettes` season only when the series has a Standard
season **and** Specials; otherwise it is remapped to Season 1 / Specials (TV tree
only — the movie extras loop uses a hard-coded `("Featurettes","featurette")`
fallback, `VfsBuilder.cs:672`).

**Extras file name** (`VfsHelper.BuildExtrasFileName`, `VfsHelper.cs:240-260`):
```
{prefix}{episode:Dn}[-ptN|-dupN] ❯ {episode title}[variation].ext
```
Prefix map (`VfsHelper.cs:35`): featurette→`O`, short→`C`, trailer→`T`,
scene/sample→`P`, other→`U`. The post-`❯` text is the **episode title** from
`TextHelper.ResolveEpisodeTitle` (`TextHelper.cs:174-204`):

```csharp
string raw = GetTitleByLanguage(ep, Settings.EpisodeTitleLanguage);   // default "SHOKO" = Shoko preferred title
string? tmdbTitle = (ep as IShokoEpisode)?.TmdbEpisodes.FirstOrDefault()?.PreferredTitle?.Value;
if (ep.EpisodeNumber == 1 && s_ambiguousTitles.Contains(raw)) { … series/TMDB fallback … }
if (Settings.TmdbEpGroupNames && ep is IShokoEpisode { TmdbEpisodes.Count: > 1 } && tmdbTitle != null)
    return tmdbTitle;                       // TMDB override, only when >1 TMDB xrefs
return tmdbTitle != null && s_defaultTitleRegex.IsMatch(raw) && !s_defaultTitleRegex.IsMatch(tmdbTitle)
    ? tmdbTitle : raw;                      // placeholder-title override
```

**Movie extras fan-out (important):** the movie extras loop links each extra
mapping into **every movie folder of the series group** across all import roots
(`foreach (var (importRoot, folderName, moviePath) in movieDirs)`,
`VfsBuilder.cs:~700`), gated on `Settings.Advanced.PlexLocalExtras`.

**Drop filters:** hidden episodes (`MapHelper.cs:144`); unresolvable/missing
source (`VfsBuilder.cs:560` `[Missing/Source-Only]`); path-ignore rules
(`VfsShared.IsPathIgnored`, `VfsShared.cs:272`: VFS roots, `FolderExclusions`,
local-extra dir names like `Featurettes/Trailers/…` when `PlexLocalExtras` on,
inline extra-file suffixes); drop/source-only managed folders and
`ManagedFolderExclusions` (`VfsShared.cs:45`); TV/movie gating.

**Key config knobs** (`Config/RelayConfig.cs`, defaults): `MovieGenerationMode`
(default **Disabled** — no movie tree at all unless changed), `PlexLocalExtras=true`,
`EpisodeTitleLanguage="SHOKO"`, `TmdbEpGroupNames=true`,
`TmdbEpNumbering`/`MergeTmdbSeries`, `VfsRootPath`/`MovieVfsRootPath`,
`DisableVfsGeneration` (no-op mode for external FUSE emulation).

---

## 2. Shoko.VFS.FUSE (in-tree plugin + external daemon)

**Architecture.** Two deployable surfaces building the *same* tree from shared
code (`Resolvers/Relay/*`, `Naming/RelayNamingStrategy.cs`):

- **In-process plugin** (`Plugin/FusePlugin.cs` → `Runtime/RelayRuntime.cs` →
  `Runtime/RelayMountOperations.cs:172-273`): runs inside Shoko, queries
  `IMetadataService`/`IVideoService` directly via
  `Resolvers/Relay/RelayShokoPathDataSource.cs`, mounts FUSE in-process.
- **External daemon** (`Shoko.VFS.FUSE.Host/`): separate process;
  `Daemon/DaemonMounter.cs:26-61` builds `ShokoRelayDataSource`
  (`Shoko.VFS.FUSE.Host/Relay/ShokoRelayDataSource.cs:534-597`) which re-derives
  the same chain over **REST + SignalR** (`GET /api/v3/ManagedFolder/{id}/File`,
  `/api/v3/Series?includeDataFrom=AniDB,TMDB`, `/api/v3/Series/{id}/Episode`).

The tree is assembled by `Resolvers/ShokoPathResolver.cs`:
`BuildSnapshot` (234-315) → `BuildTvModel` (661-744) / `BuildMovieModels`
(746-845) → `VirtualTreeSnapshot.Build` (`Resolvers/VirtualTreeSnapshot.cs:13-147`).
Projection: `Resolvers/Relay/RelayMappingProjector.Project` (62-178).
Local-asset discovery: `Resolvers/Relay/RelayLocalAssetLinker.cs`.

**Naming** (`Naming/RelayNamingStrategy.cs`, doc comment: "Matches ShokoRelay's
VFS conventions exactly"):
- `FormatExtrasFileName` (92-107): identical shape — prefix from subtype map
  (20-28: featurette→`O` etc.) + padded episode number + ` ❯ ` + title +
  `[variation]`; same sanitization (`CleanEpisodeTitleForFilename` 136-156,
  `SanitizeName` 158-179).
- `FormatSeasonFolder` (71-73) / `FormatExtrasFolder` (90): same season map,
  `Featurettes` as fallback for unmapped seasons.
- Roots default to the literal `!ShokoRelayVFS` / `!ShokoRelayMovieVFS`
  (`Configuration/FusePluginConfiguration.cs:31-36`).
- Series folder = series id; movie folder = `FormatMovieFolder(episodeId)` —
  **episode id, matching Relay**.

**Title resolution** (`RelayShokoPathDataSource.ResolveEpisodeTitle`, 679-704;
daemon mirror `ShokoRelayDataSource.ResolveEpisodeTitle`, 720-746): **logic
identical to Relay's `TextHelper.ResolveEpisodeTitle`** (verified line-by-line),
including the `TmdbEpGroupNames && TmdbEpisodes.Count > 1` gate. Defaults are
also identical: `EpisodeTitleLanguage="SHOKO"`, `TmdbEpGroupNames=true`
(`FusePluginConfiguration.cs:56,62`; `HostConfig.cs`; `RelayConfig.cs:182,222`).

**Extras classification** (`RelayMappingProjector.GetShokoCoordinates`, 240-262):
same type→season map (Other→-4). Same `-4` promotion remap (129-134). Movie
extras: `BuildMovieModels` renders non-main mappings (`IsMain` =
`primary.Type == EpisodeType.Episode`, `RelayMappingProjector.cs:166`) when
`_options.IncludeMovieExtras` (795). Extras dict is attached to **every** movie
model (fan-out replicated, `ShokoPathResolver.cs:813-838`).
`IncludeMovieExtras` is bound to `configuration.PlexLocalExtras`
(`Runtime/RelayMountPlanner.cs:177`).

**Drop filters:** same categories as Relay, plus notable implementation
differences:
1. **Source resolution gated on a single managed folder per mount**
   (`RelayShokoPathDataSource.TryResolveSource`, 558-637:
   `location.ManagedFolderID == _managedFolderId`, folder eligible,
   `File.Exists`). A file living in a *different* managed folder than the
   current mount is silently dropped — Relay resolves across all import roots.
2. **`IsPathExcluded`** (747-768): same local-extra dir regex as Relay; files
   under `Featurettes/Trailers/…` dirs lose their source path.
3. **Local extras re-surfacing is TV-only**: `RelayLocalAssetLinker.DiscoverLocalExtraDirs`
   (325-367) feeds `TvLocalExtras`, rendered only in `BuildTvModel`
   (`ShokoPathResolver.cs:676-677`); `BuildMovieModels` never reads it, and
   `AppendLocalAssets(..., tvRender: false)` skips inline extras in movie mode
   (790). Same limitation exists in Relay (`LinkLocalExtras` call site is in
   the TV branch only).
4. Hidden episodes (`ExtractSeries`, 472), no-video episodes
   (`RelayMappingProjector.cs:64`), series seed/closure pruning
   (`CollectSeedSeriesIds` 332-373, `FilterClosedSeries` 384-405).
5. **Daemon-only drift:** the REST path reconstructs `SeasonNumber` from a
   switch because the REST AniDB DTO lacks it (`ShokoRelayDataSource.cs:683`),
   and its TMDB-episode list comes from `episode.TMDB?.Episodes` — a shape that
   can differ from the in-process `IShokoEpisode.TmdbEpisodes` (e.g. count
   relevant to the `TmdbEpGroupNames` gate).

**Config split:** plugin reads `FusePluginConfiguration`, daemon reads
`HostConfig` — the two can diverge from each other and from Relay's `RelayConfig`.

---

## 3. Parity matrix

| Aspect | Relay | FUSE | Match |
|---|---|---|---|
| Root names | `!ShokoRelayVFS` / `!ShokoRelayMovieVFS` | same defaults | ✅ |
| Movie folder key | main episode id | episode id | ✅ |
| Season folder map, extras prefix map | identical | identical | ✅ |
| Type→season coordinates, `-4` promotion | identical | identical | ✅ |
| Extras name logic + title resolver | identical | identical logic | ✅ |
| Default title/extras config | SHOKO / true / true | same defaults | ✅ |
| Movie extras fan-out to all group movies | yes | yes | ✅ |
| Local-extra dir exclusion (PlexLocalExtras) | yes | yes | ✅ |
| Local extras re-surfacing scope | TV branch only | TV model only | ✅ (both gap-prone) |
| Source resolution scope | any eligible import root | **single managed folder per mount** | ⚠️ |
| Metadata access | live API (IShokoEpisode) | plugin: live API; **daemon: REST DTOs** (shape drift) | ⚠️ |
| Config source | RelayConfig | FusePluginConfiguration / HostConfig (separate files) | ⚠️ |

---

## 4. Root causes of the observed divergences

### 4.1 Name mismatch: `O1 ❯ US Chopjob.mkv` (Relay) vs `O1 ❯ Digimon Adventure.mkv` (FUSE) — **RESOLVED**

**Confirmed root cause: the host daemon resolves the wrong "SHOKO" baseline title.**
Verified against the live Shoko instance for the Digimon featurette (series 23,
EP 1412, `EpisodeType.Other`, episode number 1):

- REST `/api/v3/Episode/1412` returns `Name = "Digimon Adventure"` (the **default**
  title) while `AniDB.Titles[0]` is `{ Name: "US Chopjob", Language: "en",
  Type: Main, Preferred: true }` and `AniDB.Title = "US Chopjob"`.
  TMDB episodes = 0, so no TMDB override can fire on either side.
- Relay resolves the in-process `PreferredTitle` → "US Chopjob". ✅
- The host's `ResolveEpisodeTitle` (`ShokoRelayDataSource.cs:720-746`) passes
  **`episode.Name`** as the "SHOKO" baseline:
  ```csharp
  string raw = GetTitleByLanguage(episode.Name ?? "", episode.AniDB?.Titles, _options.EpisodeTitleLanguage);
  ```
  and `GetTitleByLanguage` (:748) returns that baseline verbatim for `SHOKO`
  — the comment claims "server's override ?? preferred ?? default", but the
  live API proves `Name` is the *default* title, not the preferred one. The
  `TitleDto.Preferred` flag (:362) is present and ignored. → "Digimon Adventure".

**Fix:** for `EpisodeTitleLanguage = "SHOKO"` (and `SeriesTitleLanguage`
likewise, `:714`), select the title with `Preferred == true` from
`AniDB.Titles`, falling back to `Name`. The same check applies to the
in-process plugin only if `IShokoEpisode.PreferredTitle` diverges from the
AniDB preferred title (not expected — the plugin reads the same metadata
service as Relay).

~~1. **TMDB xref count differs by data source (most likely).**~~ Superseded:
the TMDB group-name override (`Count > 1`) never fires for this episode
(`TMDB.Episodes` is empty on both sides).

2. ~~Deployed config drift~~ Ruled out: defaults are identical and the
   mismatch reproduces with default config.

### 4.2 Missing featurette: Relay has `!ShokoRelayMovieVFS/1964/Featurettes/…`,
FUSE's `1964/` has no Featurettes folder at all — **RESOLVED**

**Confirmed root cause: managed-folder mismatch gate.** Verified against the
live Shoko instance:

- Series 46 has 2 episodes: EP 1964 (`Episode`, the main movie) and EP 1972
  (`Special`, the "Omake" featurette, file 3862, not hidden).
- File locations: main movie file (71094) → **ManagedFolderID 15**; special
  file (3862) → **ManagedFolderID 16**. The special's path contains no
  local-extra dir segment and is accessible.
- The host daemon mounts **one** managed folder and hard-gates every mapping:
  `ShokoRelayDataSource.cs:1060`
  `var source = selected?.ManagedFolderId == _managedFolderId ? selected : null;`
  → the special's `SourcePath` is null → `HasSource` false → skipped at
  `ShokoPathResolver.BuildMovieModels:800` (`if (!HasSource(mapping)) continue;`)
  → no `Featurettes` entry, nothing anywhere else under `1964/`.
- Relay instead resolves the source across **all eligible import roots** and
  its movie extras loop links every non-main mapping into every movie folder
  regardless of where the extra's file lives → it links the mf-16 file into
  `1964/Featurettes/`.
- Naming/classification is *not* at fault: `FormatExtrasFolder(0)` and
  `FormatExtrasFileName(0)` both fall back to `Featurettes`/`O1 ❯` exactly
  like Relay's hard-coded fallback.

**Parity fix options:** resolve extras' sources across all eligible managed
folders (like Relay's `ResolveLoc`), or gate only main files on the mounted
folder and let extras fall back to a resolvable location in any eligible
folder.

Ruled out: fan-out (replicated), `PlexLocalExtras`/`IncludeMovieExtras` gate
(default true, and other movies' extras render), `-4` promotion (only touches
season −4; this is season 0), hidden episode, missing video xref, local-extra
dir exclusion, REST shape drift (host requests `includeFiles=true` correctly).

### 4.3 Verification checklist (Shoko DB / API for series 1964 and 1409)

- `GET /api/v3/Series/{id}/Episode` for 1964: does the `EpisodeType.Other`
  episode with a video xref exist? `IsHidden`? `TMDB.Episodes.Count`?
- Where does the featurette's physical file live — same managed folder as the
  main movie file? Inside a `Featurettes*` dir?
- Compare `TmdbEpGroupNames` / `EpisodeTitleLanguage` across
  `RelayConfig`, `FusePluginConfiguration`, and FUSE `HostConfig` config files.
- For the daemon: check whether `episode.TMDB.Episodes.Count > 1` for the
  1409 featurette episode.
