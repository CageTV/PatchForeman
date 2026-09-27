# PatchForeman — Changelog

## v0.2.4 — 2026-09-27

- **Fixed: crash when launched from Mod Organizer 2.** .NET 9+ marks programs as compatible with the CPU's
  shadow-stack protection (CET) by default, and MO2's virtual filesystem hooks trip it, so the UI died on
  launch from MO2's executables list before showing a window. Both executables are now built with
  `CETCompat` off, the same as Synthesis.
- **Fixed: "disable the source plugins" didn't stick.** It did uncheck them in `plugins.txt`, but when
  MO2 was open and hadn't launched PatchForeman, MO2 later wrote its own copy back over the change. MO2 only
  re-reads `plugins.txt` after a program it launched closes. PatchForeman now detects whether MO2 launched
  it and says so in the confirmation; if MO2 is open but wasn't the launcher, the confirmation warns that
  the change will be overwritten.
- **New: Snow Fixer's assets come along.** After generating, PatchForeman can move (default) or copy
  everything in Snow Fixer's output folder except its plugin and `meta.ini` into PatchForeman's own output
  folder. The folder is pre-filled from Snow Fixer's own settings and stays editable. A manifest
  (`PatchForeman_SnowFixerAssets.txt`) records what was brought over, so the next import replaces exactly
  those files instead of piling up old ones; if Snow Fixer's folder has nothing new, the previous set is
  kept.
- **New: Reset names to defaults** button for the source plugin names (they stay editable for renamed
  plugins).
- **Fixed: floating-object re-verify could crash when Python is present but PyNifly isn't** (the same fix
  shipped in Floating Object Fixer v2.0.4).

## v0.2.3 — 2026-09-27

The goal of this release: `PatchForeman.esp` now carries everything the five source outputs contribute,
so all five can be disabled after merging. Checked on a 1,380-plugin load order by comparing a generated
plugin against every source output: 0 of 1,734 tool terrain cells missing, 0 of 762 Seam Fixer cell edits
missing, 0 of 4,021 Snow Fixer cells uncovered.

- **Fixed: terrain dropped where Seam Fixer only changed water.** When exactly one tool supplied a cell's
  terrain, the whole cell was copied from "the first tool that overrode it", and Seam Fixer is checked
  first. Where Seam Fixer's override was cell-level only (its water pass, no terrain) and Road Mask Merger
  or Landscape Texture Fixer had the terrain, the terrain-less copy won and the real terrain was lost —
  174 cells on the test load order, visible as holes and bumps on roads once the source plugins were
  disabled. Terrain now always comes from a tool that has terrain; cell-level fields still come from Seam
  Fixer first.
- **Fixed: Seam Fixer's water-only cells were dropped.** Cells where a trusted tool changed only
  cell-level fields (no terrain) were skipped entirely (462 cells on the test load order). They are now
  forwarded.
- **Snow Fixer can now be disabled too.** Cells only Snow Fixer touched used to be skipped. They now get
  Snow Fixer's verified terrain changes — the `Landscape.Flags` value (vertex-colour bit cleared) and
  vertex colours removed — applied onto the terrain the mods underneath produce. Snow Fixer's own copy of
  the cell and its heights are still never used. The merged path now also removes vertex colours, so the
  flag and the data agree.
- **Fixed: crash on re-runs.** Every "copy the current winner" step used the load-order winner, which on
  a re-run is the previous `PatchForeman.esp`. That fed old output back in and could crash the write
  (a static record from the previous output failed to deep-copy). PatchForeman now always reads around
  its own previous output.
- **New option: match untouched neighbors to a trusted chain** (UI tick box /
  `--match-neighbors-to-trusted-chain`, off by default). An untouched neighbor cell with a seam against a
  PatchForeman cell gets its terrain shape from a fixed chain (last wins): Skyrim.esm > Update.esm >
  Dawnguard.esm > HearthFires.esm > Dragonborn.esm > Landscape and Water Fixes > Unique Locations Riverwood
  Forest > Lux Via. Skips Northern Roads cells, cells with water, and cells where a placed object would
  move; only applies when it shrinks the seam without making any edge worse. For a seam it can't fix, the
  log lists every plugin's version of both sides of the edge.
- New log lines count each of the above per run.
- `PatchForeman.esp` is larger (about 15 → 49 MB on the test load order) because the Snow Fixer cells are
  now included. It is still override-only and ESL-flagged.

Versions 0.2.1 and 0.2.2 were internal builds; their changes are included here.

## v0.2.0 — 2026-09-19

- **Snow Fixer as a 5th, additive-only merge input.** Snow Fixer's entire contribution across the 6
  record types it touches (Cell, Landscape, Static, Worldspace, Furniture, MoveableStatic) is a single
  "considered snow" `Flags` bit — confirmed empirically before any merge code was written. Its
  `Landscape.Flags` is layered onto a cell only when at least one of the original 4 trusted tools also
  touched that cell; a cell Snow Fixer touches alone is skipped, not forwarded (`CellsSnowFixerOnlySkipped`,
  logged per-cell). Snow Fixer can never become the source for Water/WaterHeight/Cell.Flags/
  Persistent/Temporary. Static/Furniture/MoveableStatic `Flags` overrides are forwarded directly (no
  overlap risk — none of the other 4 tools write base records of those types). New optional
  `IncludeSnowFixerCheck` in the UI, off by default for anyone with a pre-existing settings file.
- **Vortex and Direct-install modes wired up** (`MergeEngine.RunForDirectDataFolder`), alongside the
  existing MO2 path — both build straight from the game's own `Data` folder and `plugins.txt` instead of
  staging into MO2's virtual filesystem.
- New stats surfaced in both CLI and UI output: `CellsSnowFlagApplied`, `CellsSnowFixerOnlySkipped`,
  `SnowFixerBaseRecordsForwarded`.
- CSV per-category reports (`PatchForeman_HeightSeamReport.csv`, `_TextureMismatchReport.csv`,
  `_FloatingObjectReport.csv`) now written alongside `log.txt`, matching the other 4 tools in the family.
- Version-aligned CLI + UI at 0.2.0 (previously 0.1.3/0.1.4).

Smoke-tested launching and running end-to-end by the maintainer; still recommend reviewing the log
(especially any re-verify regressions) before trusting a merge broadly, same as prior releases.

## v0.1.3 — 2026-09-19

Version-number-only release - no code change from v0.1.2. Not yet on Nexus; the CSV-report
output fix and a UI banner text update are queued for the next real release.

## v0.1.2 — 2026-09-15

**Initial public release.**

Merges the separately-generated output plugins of Landscape Seam Fixer, Road Mask Merger, Landscape
Texture Fixer, and Floating Object Fixer into one final plugin, with a real field-aware merge (height
and texture-layer union across tools where they genuinely overlap, not a blind "last one wins" forward).

- **Re-verify pass**: automatically re-runs the three landscape/reference sibling tools' own detection
  logic against the merged output, before vs. after, and reports any regression.
- **Automatic ESL flagging**: checks and flags the merged plugin as ESL-eligible when it qualifies, using
  the same algorithm as SSEEdit's own "Find ESP plugins which could be turned into ESL" script.
- **UI**: per-tool include/exclude checkboxes, an optional post-merge step to disable the four source
  plugins, settings persistence, and a read-only check against each sibling tool's own last-known output
  location.
- **`log.txt` and `settings-used.json`** written into the output folder alongside the generated plugin,
  matching every other tool in this family.

Still a prototype: not yet confirmed via full in-game testing. Review the log before installing.
