# PatchForeman

**Current version: 0.2.0** — see [CHANGELOG.md](CHANGELOG.md) for what's new.

> **Confirmed working by the maintainer** on a real, large (1000+ plugin) load order as of v0.2.0.
> Still young — review the log carefully (especially any regressions the re-verify pass flags) before
> installing the generated patch on your own list.

A standalone tool for Skyrim Special Edition / Anniversary Edition that merges the separately-generated
output plugins of four sibling landscape/reference fixer tools into **one final plugin**, with a real
field-aware merge — not a blind "whichever loads last wins" forward.

## Why this exists

If you run more than one of [Landscape Seam Fixer](https://github.com/CageTV/landscape-seam-fixer),
[Road Mask Merger](https://github.com/CageTV/Road-Mask-Merger),
[Landscape Texture Fixer](https://github.com/CageTV/LandscapeTextureFixes), and
[Floating Object Fixer](https://github.com/CageTV/floating-reference-fixer), their outputs can genuinely
overlap on the same records:

- **Landscape height**: Landscape Seam Fixer and Road Mask Merger can both touch the same cell's terrain
  height, for different reasons.
- **Landscape texture layers**: Road Mask Merger's own road-texture preservation and Landscape Texture
  Fixer can both add new texture layers to the same cell.

Just loading all four plugins active at once means whichever one happens to load last silently discards
the other's real work on any cell they both touched. PatchForeman merges them properly instead: forwards
a cell verbatim when only one tool touched it, and when multiple did, takes height from whichever tool's
edit is more specific and **unions** the texture layers from every contributing tool (respecting the
game's 7-layer-per-quadrant cap).

**Optional 5th input: Snow Fixer.** If you also run a Snow Fixer-style tool (its entire contribution is a
per-record "considered snow" flag), PatchForeman can fold that in too via `--snow-fixer-esp` /
the UI's Snow Fixer checkbox. It's treated as additive-only, never as a cell's primary source of
truth — only its snow changes are applied, onto the terrain your other mods produce. See
[CHANGELOG.md](CHANGELOG.md) v0.2.3 for exactly what that means.

**Since v0.2.3 the merged plugin replaces all five source outputs**, so you can disable them after
merging and free those plugin slots.

## What it does

1. **Merge**: reads each of the four sibling tools' own output plugins from your load order and produces
   one combined plugin (`PatchForeman.esp` by default).
2. **Re-verify**: after writing the merged plugin, automatically re-runs Landscape Seam Fixer's,
   Landscape Texture Fixer's, and Floating Object Fixer's own detection logic against it — once on your
   original load order, once with the merged plugin layered on top — and reports whether anything got
   *worse* (a real regression) rather than just declaring success.
3. **ESL flagging**: automatically checks whether the merged plugin qualifies for the ESL flag (using the
   same algorithm as SSEEdit's own "Find ESP plugins which could be turned into ESL" script) and flags it
   if so — this tool's output almost never adds brand-new records, so it qualifies essentially every run.

## What it doesn't (yet) do

- **No ESL FormID compacting.** If a merged plugin ever did exceed the ESL FormID range, this tool
  reports why it wasn't flagged rather than attempting to fix it.
- **No unified multi-app launcher.** You still need to run each of the four sibling tools yourself first;
  PatchForeman only merges their *outputs*.
- **Optional include/exclude per tool** and an **optional "disable the other four" step** exist in the UI.

## Requirements

- Windows, .NET 10 runtime (bundled in the self-contained release zip — no separate install needed)
- The four sibling tools' output plugins, already generated and active in your load order
- Mod Organizer 2, Vortex, or a direct (non-managed) install — all three are supported

For full floating-object collision-awareness in the re-verify pass, this tool bundles a trimmed copy of
[PyNifly](https://github.com/BadDogSkyrim/PyNifly) (GPL-3.0 — see `pynifly/NOTICE.md`) and needs a Python
3 interpreter on your `PATH`. Without Python, the re-verify pass still runs, just falling back to
heightmap-only floating-object detection (a graceful degradation, not a failure).

## Usage (GUI)

1. Run `PatchForeman.UI.exe`.
2. Point it at your MO2 instance and profile — it auto-detects the game Data folder and lists your
   profiles.
3. Confirm (or rename) each of the four sibling tools' own output esp names — untick any tool whose
   output you don't want folded into the merge.
4. Click **Run Detection** for a dry run (nothing written), or **Generate Merged Plugin** to actually
   write it and run the re-verify pass.
5. Review the log, then install `PatchForeman.esp` (and, optionally, disable the four source plugins it
   superseded — the UI can do this for you, after a confirmation).

## Usage (CLI)

```
PatchForeman.exe --mo2 <instancePath> <profileName> [gameDataPath]
    [--output="PatchForeman.esp"] [--write] [--skip-reverify]
    [--seam-fixer-esp="LandscapeSeamFixes.esp"]
    [--road-mask-esp="RoadMaskMerge.esp"]
    [--texture-fixer-esp="LandscapeTextureFixes.esp"]
    [--floating-fixer-esp="FloatingObjectFixes.esp"]
    [--snow-fixer-esp="SnowFixer.esp"]
    [--trust-northern-roads] [--floating-threshold=96] [--floating-worldspace="Tamriel"]
    [--match-neighbors-to-trusted-chain]
```

Defaults to a dry run — pass `--write` to actually produce the merged plugin.

## Project layout

- `SeamFinder.Core/` — the subset of the shared library this tool needs: `Mo2Resolver`,
  `HeightmapDecoder`, `SeamDetector`, `TextureLayerDetector`, `FloatingObjectFixer` (detection),
  `CollisionRaycaster`, `EslEligibility`
- `PatchForeman/` — console CLI + the merge engine (`MergeEngine.cs`) and re-verify pass
  (`ReverifyPass.cs`)
- `PatchForeman.UI/` — WPF desktop app
- `pynifly/`, `collision-raycast/` — bundled so the re-verify pass's floating-object detection works out
  of the box (see Requirements above)

`SeamFinder.Core` here is a trimmed copy shared with three sibling tools (Landscape Seam Fixer, Road Mask
Merger, Landscape Texture Fixer) — each ships only the files it actually uses, not the whole shared
library.

## License

MIT (see [LICENSE](LICENSE)). The bundled `pynifly/` folder is GPL-3.0 (see `pynifly/LICENSE` and
`pynifly/NOTICE.md`) — it runs as a separate process invoked via a Python script, never linked into this
tool's own binary.

## Contributors

See [CONTRIBUTORS.md](CONTRIBUTORS.md).
