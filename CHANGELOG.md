# PatchForeman — Changelog

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
