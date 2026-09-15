// PatchForeman: the "5th app" - merges the separately-generated output ESPs
// of the 4 sibling fixer tools (Landscape Seam Fixer, Road Mask Merger,
// Landscape Texture Fixer, Floating Object Fixer) into one final plugin.
//
// NOT a blind "copy every record forward" merge. Two of the four tools'
// outputs genuinely OVERLAP on the same records:
//   - Landscape.VertexHeightMap (height): Landscape Seam Fixer AND Road Mask
//     Merger can both touch the SAME cell's height.
//   - Landscape.Layers (texture): Road Mask Merger (its 2026-09-14 road-
//     texture-preservation addition) AND Landscape Texture Fixer can both
//     add layers to the SAME cell's texture stack.
// A naive "whichever loads last wins" merge would silently discard one
// tool's real work on any cell where two tools both touched it - exactly
// the failure class this whole toolkit spent 2026-09-14 fixing at the
// individual-tool level. This engine merges at the FIELD level instead:
// one chosen height source per cell, and texture layers UNIONED (deduped
// by quadrant+texture identity) across every tool that added one.
//
// Floating Object Fixer's contribution has NO overlap risk with the other
// three - it overrides individual PlacedObject/PlacedNpc records' own
// Position.Z directly (confirmed by reading FloatingObjectFixer.cs's own
// write path: `refContext.GetOrAddAsOverride(patchMod)` on the REFERENCE
// itself, not the parent Cell) - so those overrides are just forwarded
// verbatim, no combination logic needed.
//
// STATUS 2026-09-14: first cut, built and verified against real data in
// DETECTION-ONLY mode (reads the real active load order, logs exactly what
// it WOULD merge, writes nothing but its own log + a dry-run summary). Does
// NOT yet write a live output plugin - see NOTES.md for what's deliberately
// not done yet (the re-verify pass, the ESL-eligibility check, the unified
// CLI launcher for the other 4 tools) before this is safe to run for real.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SeamFinder.Core;

namespace PatchForeman;

public record SourceToolPlugins(
    string LandscapeSeamFixer = "LandscapeSeamFixes.esp",
    string RoadMaskMerger = "RoadMaskMerge.esp",
    string LandscapeTextureFixer = "LandscapeTextureFixes.esp",
    string FloatingObjectFixer = "FloatingObjectFixes.esp");

public record CellMergeStats(
    int CellsFromOneSource,
    int CellsHeightAndTextureMerged,
    int CellsWaterFieldsDisagreed,
    int PlacedRefsForwarded);

public record MergeResult(CellMergeStats Stats, string OutputPath, bool DryRun);

public static class MergeEngine
{
    const int MaxLayersPerQuadrant = 7; // same verified-against-real-game-data cap every sibling tool uses

    public static MergeResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        SourceToolPlugins sources,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool dryRun)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "PatchForeman-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);
        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();
            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            return GenerateCore(env.LinkCache, env.LoadOrder, priorityIndex, mergedFolder, sources, outputPluginName, outputDirectory, log, dryRun, loadOrder);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    static bool IsPluginPresent(List<Mo2Resolver.ResolvedPlugin> loadOrder, string fileName) =>
        loadOrder.Any(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    static MergeResult GenerateCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> loadOrderMods,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        SourceToolPlugins sources,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool dryRun,
        List<Mo2Resolver.ResolvedPlugin> loadOrder)
    {
        // Fail loud, not silent, if a source tool's output isn't even active -
        // a merge that quietly skips a whole tool's contribution is worse
        // than one that tells you it can't find it. Each is independently
        // optional (the user may not have run/installed all 4 yet), so this
        // is a log line, not a thrown exception.
        var present = new
        {
            SeamFixer = IsPluginPresent(loadOrder, sources.LandscapeSeamFixer),
            RoadMask = IsPluginPresent(loadOrder, sources.RoadMaskMerger),
            TextureFixer = IsPluginPresent(loadOrder, sources.LandscapeTextureFixer),
            FloatingFixer = IsPluginPresent(loadOrder, sources.FloatingObjectFixer),
        };
        log($"Source plugins found active: LandscapeSeamFixer={present.SeamFixer}, RoadMaskMerger={present.RoadMask}, " +
            $"LandscapeTextureFixer={present.TextureFixer}, FloatingObjectFixer={present.FloatingFixer}");
        if (!present.SeamFixer && !present.RoadMask && !present.TextureFixer && !present.FloatingFixer)
        {
            throw new InvalidOperationException(
                "None of the 4 source tool outputs were found active in the load order - nothing to merge. " +
                "Run at least one of Landscape Seam Fixer / Road Mask Merger / Landscape Texture Fixer / " +
                "Floating Object Fixer first, and make sure its output plugin is enabled.");
        }

        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);

        int cellsFromOneSource = 0, cellsMerged = 0, waterDisagreements = 0, refsForwarded = 0;

        // --- Landscape/Cell merge (Landscape Seam Fixer + Road Mask Merger + Landscape Texture Fixer) ---
        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue; // interior cell - none of the 3 landscape tools ever touch these

            // For each of the 3 landscape-touching tools, find THAT SPECIFIC
            // plugin's own override of this cell (not the load-order winner -
            // we need to know precisely which of the 3 tools did what, so we
            // can combine their contributions field-by-field below).
            ICellGetter? seamFixerCell = null, roadMaskCell = null, textureFixerCell = null;
            foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell.FormKey, ResolveTarget.Winner))
            {
                string fileName = ctx.ModKey.FileName;
                if (present.SeamFixer && fileName.Equals(sources.LandscapeSeamFixer, StringComparison.OrdinalIgnoreCase))
                    seamFixerCell = ctx.Record;
                else if (present.RoadMask && fileName.Equals(sources.RoadMaskMerger, StringComparison.OrdinalIgnoreCase))
                    roadMaskCell = ctx.Record;
                else if (present.TextureFixer && fileName.Equals(sources.LandscapeTextureFixer, StringComparison.OrdinalIgnoreCase))
                    textureFixerCell = ctx.Record;
            }

            var touchCount = (seamFixerCell?.Landscape is not null ? 1 : 0)
                + (roadMaskCell?.Landscape is not null ? 1 : 0)
                + (textureFixerCell?.Landscape is not null ? 1 : 0);
            if (touchCount == 0) continue; // none of the 3 landscape tools touched this cell

            if (touchCount == 1)
            {
                // Only one tool touched this cell - that tool's own output
                // already correctly preserves everything else about the cell
                // (Persistent/Temporary/Water/Flags), so just forward its
                // WHOLE cell body verbatim. No combination needed.
                var soleSource = seamFixerCell ?? roadMaskCell ?? textureFixerCell!;
                var soleWritable = context.GetOrAddAsOverride(patchMod);
                CopyCellVerbatim(soleSource, soleWritable);
                cellsFromOneSource++;
                continue;
            }

            // Multiple tools touched this cell - build a merged Landscape.
            cellsMerged++;
            var writable = context.GetOrAddAsOverride(patchMod);

            // Height priority: Road Mask Merger's road-blend is the more
            // SPECIFIC, deliberate edit (it only ever touches a cell because
            // a real road/path genuinely runs there); Landscape Seam Fixer's
            // correction is the more GENERAL "wrong mod is winning here" fix.
            // Where both touched the same cell, Road Mask Merger's height
            // wins - it already incorporates the road-source mod's own
            // (trusted) terrain data as its input, so it isn't "overwriting a
            // correct fix with a worse one," it's applying the more specific
            // one on top.
            string heightSourceName;
            ILandscapeGetter heightSource;
            if (roadMaskCell?.Landscape is { } rm) { heightSource = rm; heightSourceName = "Road Mask Merger"; }
            else if (seamFixerCell?.Landscape is { } sf) { heightSource = sf; heightSourceName = "Landscape Seam Fixer"; }
            else { heightSource = textureFixerCell!.Landscape!; heightSourceName = "Landscape Texture Fixer"; }
            var mergedLandscape = heightSource.DeepCopy();

            // Texture layers: union every layer present in EITHER Road Mask
            // Merger's or Landscape Texture Fixer's own copy that ISN'T
            // already in the chosen height source's copy, deduped by
            // (quadrant, texture) identity - mirrors the exact additive-only
            // preservation logic both of those tools already use internally,
            // just applied one level up, across tool boundaries instead of
            // within one tool's own pass. (Re-scanning the height source's
            // OWN layers as a "candidate" too is harmless, not a correctness
            // bug - the alreadyPresent check below just finds them already
            // there and skips them - so this list doesn't need to exclude it.)
            var candidateLayerSources = new[] { roadMaskCell?.Landscape, textureFixerCell?.Landscape }
                .Where(l => l is not null)
                .Select(l => l!);
            int addedFromUnion = 0;
            foreach (var candidate in candidateLayerSources)
            {
                foreach (var layer in candidate.Layers)
                {
                    if (layer is not IAlphaLayerGetter alpha) continue; // never union Base layers - too big a change to make implicitly
                    var alreadyPresent = mergedLandscape.Layers.Any(l =>
                        l.Header.Quadrant == alpha.Header.Quadrant && l.Header.Texture.FormKey == alpha.Header.Texture.FormKey);
                    if (alreadyPresent) continue;

                    var quadrantCount = mergedLandscape.Layers.Count(l => l.Header.Quadrant == alpha.Header.Quadrant);
                    if (quadrantCount >= MaxLayersPerQuadrant)
                    {
                        log($"  ({cell.Grid.Point.X},{cell.Grid.Point.Y}) {alpha.Header.Quadrant}: could not union in a texture layer from another tool's output - quadrant already at the {MaxLayersPerQuadrant}-layer cap.");
                        continue;
                    }

                    var copiedData = new ExtendedList<AlphaLayerData>();
                    foreach (var d in alpha.AlphaLayerData)
                        copiedData.Add(new AlphaLayerData { Position = d.Position, Opacity = d.Opacity });

                    mergedLandscape.Layers.Add(new AlphaLayer
                    {
                        Header = new LayerHeader
                        {
                            Texture = new FormLink<ILandscapeTextureGetter>(alpha.Header.Texture.FormKey),
                            Quadrant = alpha.Header.Quadrant,
                            LayerNumber = (ushort)quadrantCount,
                        },
                        AlphaLayerData = copiedData,
                    });
                    addedFromUnion++;
                }
            }
            if (addedFromUnion > 0)
                log($"  ({cell.Grid.Point.X},{cell.Grid.Point.Y}): merged height from {heightSourceName}, unioned {addedFromUnion} texture layer(s) from the other tool(s).");

            writable.Landscape = mergedLandscape;

            // Cell-level fields (Water/WaterHeight/Flags/Persistent/Temporary):
            // every one of the 3 tools already forwards these UNCHANGED from
            // whichever cell was actually winning before it ran, so all 3
            // copies SHOULD agree. Don't just silently pick one - check, and
            // log if they don't (would mean one of the source tools has its
            // own preservation bug, worth surfacing rather than masking).
            var candidates = new[] { seamFixerCell, roadMaskCell, textureFixerCell }.Where(c => c is not null).Select(c => c!).ToList();
            var waterHeights = candidates.Select(c => c.WaterHeight).Distinct().Count();
            if (waterHeights > 1)
            {
                waterDisagreements++;
                log($"  ({cell.Grid.Point.X},{cell.Grid.Point.Y}): WARNING - the {candidates.Count} source tools' own copies of this cell disagree on WaterHeight ({string.Join(", ", candidates.Select(c => c.WaterHeight))}) - one of them has a preservation bug. Used the first one found.");
            }
            var fieldSource = candidates[0];
            // FIXED 2026-09-15 (same root cause as RoadTerrainMerger.cs, see its
            // NOTES): only assign Water when the source genuinely has a link -
            // an unconditional assignment forces an explicit null XCWT subrecord
            // even for cells with no water at all, which is pure noise (and here
            // would re-introduce the bug PatchForeman is otherwise just carrying
            // forward from whichever source tool cell it copied).
            if (fieldSource.Water.FormKeyNullable.HasValue)
                writable.Water = fieldSource.Water.AsSetter().AsNullable();
            writable.WaterHeight = fieldSource.WaterHeight;
            writable.Flags = fieldSource.Flags;
            foreach (var p in fieldSource.Persistent) writable.Persistent.Add((IPlaced)p.DeepCopy());
            foreach (var t in fieldSource.Temporary) writable.Temporary.Add((IPlaced)t.DeepCopy());
        }

        // --- Floating Object Fixer's contribution: individual PlacedObject/
        // PlacedNpc overrides, no overlap with the landscape tools above.
        // Reads directly from FOF's OWN plugin file (via EnumerateMajorRecords
        // on that one mod, not a whole-load-order winner scan) so this picks
        // up every ref FOF touched regardless of whether something else
        // currently wins over it in-game - PatchForeman's job here is to
        // faithfully carry FOF's own contribution INTO the merged patch
        // content, not to re-decide who wins (load order still does that). ---
        if (present.FloatingFixer)
        {
            var fofModKey = ModKey.FromFileName(sources.FloatingObjectFixer);
            if (!loadOrderMods.TryGetValue(fofModKey, out var fofListing) || fofListing.Mod is null)
            {
                log($"  WARNING: {sources.FloatingObjectFixer} was reported present but its mod data couldn't be loaded - skipping its contribution.");
            }
            else
            {
                var fofMod = fofListing.Mod;
                foreach (var placedObj in fofMod.EnumerateMajorRecords<IPlacedObjectGetter>())
                {
                    if (placedObj.Placement is null) continue; // matches FloatingObjectFixer.cs's own guard - a ref with no placement has nothing for it to have corrected
                    if (!linkCache.TryResolveContext<PlacedObject, IPlacedObjectGetter>(placedObj.FormKey, out var winningCtx)) continue;
                    var writable = winningCtx.GetOrAddAsOverride(patchMod);
                    if (writable.Placement is null) { log($"  WARNING: {placedObj.FormKey} - winning context has no Placement to set Position on; skipped."); continue; }
                    writable.Placement.Position = placedObj.Placement.Position;
                    refsForwarded++;
                }
                foreach (var placedNpc in fofMod.EnumerateMajorRecords<IPlacedNpcGetter>())
                {
                    if (placedNpc.Placement is null) continue;
                    if (!linkCache.TryResolveContext<PlacedNpc, IPlacedNpcGetter>(placedNpc.FormKey, out var winningCtx)) continue;
                    var writable = winningCtx.GetOrAddAsOverride(patchMod);
                    if (writable.Placement is null) { log($"  WARNING: {placedNpc.FormKey} - winning context has no Placement to set Position on; skipped."); continue; }
                    writable.Placement.Position = placedNpc.Placement.Position;
                    refsForwarded++;
                }
            }
        }

        var stats = new CellMergeStats(cellsFromOneSource, cellsMerged, waterDisagreements, refsForwarded);

        if (dryRun)
        {
            log($"[DRY RUN] Would write {outputPluginName} with {cellsFromOneSource + cellsMerged} cell(s) touched " +
                $"({cellsFromOneSource} from a single source tool, {cellsMerged} merged across tools) and {refsForwarded} " +
                "placed-reference override(s) from Floating Object Fixer. Nothing written.");
            return new MergeResult(stats, string.Empty, DryRun: true);
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);

        var eslResult = EslEligibility.CheckAndFlag(patchMod);
        log(eslResult.Summary);

        log($"Writing merged patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .Write(patchMod);

        return new MergeResult(stats, outputPath, DryRun: false);
    }

    static void CopyCellVerbatim(ICellGetter source, Cell writable)
    {
        writable.Landscape = source.Landscape?.DeepCopy();
        // Same fix as above and in RoadTerrainMerger.cs - don't force an
        // explicit null XCWT subrecord for a cell that never had one.
        if (source.Water.FormKeyNullable.HasValue)
            writable.Water = source.Water.AsSetter().AsNullable();
        writable.WaterHeight = source.WaterHeight;
        writable.Flags = source.Flags;
        foreach (var p in source.Persistent) writable.Persistent.Add((IPlaced)p.DeepCopy());
        foreach (var t in source.Temporary) writable.Temporary.Add((IPlaced)t.DeepCopy());
    }
}
