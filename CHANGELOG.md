# PatchForeman — Changelog

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
