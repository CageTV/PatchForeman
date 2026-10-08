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
using Mutagen.Bethesda.Plugins.Analysis;
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
    string FloatingObjectFixer = "FloatingObjectFixes.esp",
    // Third-party (a friend's closed-source tool, not one of this project's
    // own 4) - see the SnowFixer merge pass below for why it's treated more
    // conservatively than the other 4.
    string SnowFixer = "SnowFixer.esp");

public record CellMergeStats(
    int CellsFromOneSource,
    int CellsHeightAndTextureMerged,
    int CellsWaterFieldsDisagreed,
    int PlacedRefsForwarded,
    int CellsSnowFlagApplied,
    int CellsSnowFixerOnlySkipped,
    int SnowFixerBaseRecordsForwarded,
    NeighborMatchStats? NeighborMatch = null);

// Outcome of the optional neighbor-match pass (see NeighborChainMatcher.cs).
// Every candidate neighbor lands in exactly one bucket, so the buckets sum to
// NeighborsConsidered - nothing is silently dropped.
public record NeighborMatchStats(
    int NeighborsConsidered,
    int Matched,
    int SkippedNorthernRoads,
    int SkippedNoChainData,
    int SkippedAlreadyMatchesChain,
    int SkippedWater,
    int SkippedReference,
    int SkippedNoImprovement);

// OutputPath is the base plugin (PatchForeman.esp). When the merged patch needs
// more than the engine's 255-master limit it is written as several adjacent
// plugins (PatchForeman.esp, PatchForeman_2.esp, ...); OutputPaths lists every
// one in load order, and always starts with OutputPath.
public record MergeResult(CellMergeStats Stats, string OutputPath, bool DryRun, IReadOnlyList<string>? AllOutputPaths = null)
{
    public IReadOnlyList<string> OutputPaths =>
        AllOutputPaths ?? (string.IsNullOrEmpty(OutputPath) ? Array.Empty<string>() : new[] { OutputPath });
}

public static class MergeEngine
{
    const int MaxLayersPerQuadrant = 7; // same verified-against-real-game-data cap every sibling tool uses

    public static MergeResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        SourceToolPlugins sources,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool dryRun,
        bool matchNeighborsToTrustedChain = false)
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
            return GenerateCore(env.LinkCache, env.LoadOrder, priorityIndex, mergedFolder, sources, outputPluginName, outputDirectory, log, dryRun, loadOrder, matchNeighborsToTrustedChain);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    // Vortex/Direct mode: unlike MO2, there's no per-mod virtual filesystem
    // to emulate - Vortex deploys (hardlinks/reparse-points) and a manual
    // Direct install both already sit physically merged in ONE Data folder,
    // and the game's own plugins.txt (not a profile-specific one) is the
    // real active/priority order. Mutagen's own GameEnvironmentBuilder
    // already knows how to find and read that - no staging copy needed at
    // all, matching every sibling tool's own RunForDirectDataFolder shape
    // (see e.g. RoadTerrainMerger.RunForDirectDataFolder / SeamFixer.
    // GenerateFixPluginForDirectDataFolder).
    public static MergeResult RunForDirectDataFolder(
        string dataFolderPath,
        SourceToolPlugins sources,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool dryRun,
        bool matchNeighborsToTrustedChain = false)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        // GenerateCore's own "loadOrder" param and IsPluginPresent check only
        // need the filename list (not real disk paths - it never reads these
        // paths itself, only checks membership) - build the same shape
        // Mo2Resolver.ResolvedPlugin gives the MO2 path, from the env's own
        // resolved load order, so GenerateCore needs no Vortex/Direct-aware
        // branch of its own.
        var loadOrder = env.LoadOrder.ListedOrder
            .Select(listing => new Mo2Resolver.ResolvedPlugin(listing.ModKey.FileName, Path.Combine(dataFolderPath, listing.ModKey.FileName)))
            .ToList();

        return GenerateCore(env.LinkCache, env.LoadOrder, priorityIndex, dataFolderPath, sources, outputPluginName, outputDirectory, log, dryRun, loadOrder, matchNeighborsToTrustedChain);
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
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        bool matchNeighborsToTrustedChain)
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
            SnowFixer = !string.IsNullOrEmpty(sources.SnowFixer) && IsPluginPresent(loadOrder, sources.SnowFixer),
        };
        log($"Source plugins found active: LandscapeSeamFixer={present.SeamFixer}, RoadMaskMerger={present.RoadMask}, " +
            $"LandscapeTextureFixer={present.TextureFixer}, FloatingObjectFixer={present.FloatingFixer}, SnowFixer={present.SnowFixer}");
        if (!present.SeamFixer && !present.RoadMask && !present.TextureFixer && !present.FloatingFixer && !present.SnowFixer)
        {
            throw new InvalidOperationException(
                "None of the 5 source tool outputs were found active in the load order - nothing to merge. " +
                "Run at least one of Landscape Seam Fixer / Road Mask Merger / Landscape Texture Fixer / " +
                "Floating Object Fixer / Snow Fixer first, and make sure its output plugin is enabled.");
        }

        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);

        int cellsFromOneSource = 0, cellsMerged = 0, waterDisagreements = 0, refsForwarded = 0;
        int cellsSnowFlagApplied = 0, cellsSnowFixerOnlySkipped = 0, snowFixerBaseRecordsForwarded = 0;
        int cellsFieldsOnlyForwarded = 0, cellsLandscapeFromOtherTool = 0, cellsSnowOnlyApplied = 0;

        // Every tool-generated plugin (this family's, Snow Fixer, and this
        // run's own output) - read AROUND these to find "what the mods
        // underneath say" for a cell.
        var toolOutputs = new HashSet<string>(TrustResolver.SiblingToolOutputs, StringComparer.OrdinalIgnoreCase);
        toolOutputs.UnionWith(TrustResolver.ThirdPartyGeneratedOutputs);
        toolOutputs.UnionWith(new[] { sources.LandscapeSeamFixer, sources.RoadMaskMerger, sources.LandscapeTextureFixer,
            sources.FloatingObjectFixer, sources.SnowFixer, outputPluginName });
        toolOutputs.Remove(string.Empty);

        // The terrain PatchForeman itself ends up writing for each exterior
        // cell, keyed by (worldspace, grid) - the neighbor-match pass needs to
        // know which cells are "ours" and what their final edges look like.
        var writtenLandscapes = new Dictionary<(FormKey Worldspace, int X, int Y), ILandscapeGetter>();
        void RecordWritten(IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter> ctx, ICellGetter c, ILandscapeGetter? land)
        {
            if (land is null || c.Grid is null) return;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var ws)) return;
            writtenLandscapes[(ws.Record.FormKey, c.Grid.Point.X, c.Grid.Point.Y)] = land;
        }

        // --- Landscape/Cell merge (Landscape Seam Fixer + Road Mask Merger + Landscape Texture Fixer) ---
        foreach (var winningCellContext in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            // Read around a previous PatchForeman.esp that is still active -
            // see TryResolveContextExcluding.
            var context = winningCellContext.ModKey.FileName.String.Equals(outputPluginName, StringComparison.OrdinalIgnoreCase)
                ? linkCache.ResolveAllContexts<Cell, ICellGetter>(winningCellContext.Record.FormKey, ResolveTarget.Winner)
                    .FirstOrDefault(c => !c.ModKey.FileName.String.Equals(outputPluginName, StringComparison.OrdinalIgnoreCase))
                : winningCellContext;
            if (context is null) continue;
            var cell = context.Record;
            if (cell.Grid is null) continue; // interior cell - none of the 3 landscape tools ever touch these

            // For each of the 3 landscape-touching tools, find THAT SPECIFIC
            // plugin's own override of this cell (not the load-order winner -
            // we need to know precisely which of the 3 tools did what, so we
            // can combine their contributions field-by-field below).
            ICellGetter? seamFixerCell = null, roadMaskCell = null, textureFixerCell = null, snowFixerCell = null;
            foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell.FormKey, ResolveTarget.Winner))
            {
                string fileName = ctx.ModKey.FileName;
                if (present.SeamFixer && fileName.Equals(sources.LandscapeSeamFixer, StringComparison.OrdinalIgnoreCase))
                    seamFixerCell = ctx.Record;
                else if (present.RoadMask && fileName.Equals(sources.RoadMaskMerger, StringComparison.OrdinalIgnoreCase))
                    roadMaskCell = ctx.Record;
                else if (present.TextureFixer && fileName.Equals(sources.LandscapeTextureFixer, StringComparison.OrdinalIgnoreCase))
                    textureFixerCell = ctx.Record;
                else if (present.SnowFixer && fileName.Equals(sources.SnowFixer, StringComparison.OrdinalIgnoreCase))
                    snowFixerCell = ctx.Record;
            }

            // Snow Fixer is deliberately NOT one of the 3 height/texture
            // "landscape-touching tools" counted here, and is never eligible
            // to be the sole verbatim-forward source below. It's a friend's
            // closed-source tool - unlike the other 4 (each independently
            // debugged over multiple sessions for exactly this class of
            // Water/Persistent/Temporary preservation bug, see RoadTerrainMerger.cs's
            // and MergeEngine.cs's own 2026-09-15 fixes), there's no way to
            // verify it correctly preserves everything else about a cell it
            // touches. Its only verified, narrow contribution is the
            // Landscape.Flags per-quadrant snow bits (confirmed via houseCARL
            // conflict-tree diff against a real SnowFixer.esp: it changes
            // Landscape.Flags only, never VertexHeightMap or Layers) - so it
            // only ever LAYERS that one field onto a cell one of the 3
            // trusted tools already established the rest of, exactly the
            // same "additive only, never the foundation" principle the
            // texture-layer union above already uses.
            var landscapeTouchCount = (seamFixerCell?.Landscape is not null ? 1 : 0)
                + (roadMaskCell?.Landscape is not null ? 1 : 0)
                + (textureFixerCell?.Landscape is not null ? 1 : 0);
            var snowTouches = snowFixerCell?.Landscape is not null;

            // Cell-level fields (Water/WaterHeight/Flags/refs) come from the
            // first of the 3 trusted tools that overrode the cell AT ALL - the
            // same order the merged branch below uses. That is NOT necessarily
            // a tool that touched the terrain: in-game, LAND is its own record,
            // so a tool's cell-only override (e.g. Seam Fixer's water pass) and
            // another tool's LAND both apply at once. See the 2026-09-26 fix below.
            var cellFieldSource = seamFixerCell ?? roadMaskCell ?? textureFixerCell;

            if (landscapeTouchCount == 0)
            {
                // Snow Fixer-only cells (CHANGED 2026-09-26, user's choice:
                // "snow flags only"). These used to be skipped, so SnowFixer.esp
                // could never be disabled after merging (~3,650 cells on the
                // live profile). Still never trusts Snow Fixer's own copy of the
                // cell or its terrain: cell fields come from a trusted tool if
                // one overrode the cell, else from the highest plugin below the
                // tools; terrain comes from the highest non-tool plugin; only
                // Snow Fixer's verified snow changes are applied on top.
                var cellSourceForWrite = cellFieldSource
                    ?? (snowTouches ? WinningCellExcluding(cell.FormKey, linkCache, toolOutputs) : null);
                if (cellSourceForWrite is not null)
                {
                    var writableOnly = context.GetOrAddAsOverride(patchMod);
                    // FIXED 2026-09-26: a trusted tool changed only cell-level
                    // fields here (Seam Fixer's water restoration, 462 cells on
                    // the live profile) and this branch used to drop the cell -
                    // so disabling the source plugins after merging silently
                    // lost those fixes.
                    CopyCellVerbatim(cellSourceForWrite, writableOnly);
                    writableOnly.Landscape = null;
                    if (cellFieldSource is not null) cellsFieldsOnlyForwarded++;

                    if (snowTouches)
                    {
                        var baseLandscape = WinningLandscapeExcluding(cell.FormKey, linkCache, toolOutputs);
                        if (baseLandscape is null)
                        {
                            cellsSnowFixerOnlySkipped++;
                            log($"  ({cell.Grid.Point.X},{cell.Grid.Point.Y}): Snow Fixer touched this cell but no non-tool plugin has terrain for it - snow changes not applied.");
                        }
                        else
                        {
                            var snowApplied = baseLandscape.DeepCopy();
                            ApplySnowChanges(snowApplied, snowFixerCell!.Landscape!);
                            writableOnly.Landscape = snowApplied;
                            cellsSnowOnlyApplied++;
                        }
                    }
                }
                continue;
            }

            if (landscapeTouchCount == 1 && !snowTouches)
            {
                // Only one of the 3 trusted tools touched this cell, and Snow
                // Fixer didn't - that tool's own output already correctly
                // preserves everything else about the cell (Persistent/
                // Temporary/Water/Flags), so just forward its WHOLE cell
                // body verbatim. No combination needed.
                //
                // FIXED 2026-09-26 (user report: Northern Roads terrain missing
                // around Riften, cells 00BD18/00BD19/00BD37/00BD38): this took
                // "the first tool that overrode the cell" as the whole source -
                // `seamFixerCell ?? roadMaskCell ?? ...` - and Seam Fixer is
                // checked first. Where Seam Fixer had only a cell-level override
                // (no LAND) and Road Mask Merger or Texture Fixer had the terrain,
                // the terrain-less Seam Fixer cell was copied and the real terrain
                // thrown away. 174 cells on the live profile. With the source
                // plugins disabled after merging, the un-roaded terrain from
                // whatever loaded before them (CS Water Mod.esp at Riften) won in-game.
                var landSource = seamFixerCell?.Landscape is not null ? seamFixerCell
                    : roadMaskCell?.Landscape is not null ? roadMaskCell
                    : textureFixerCell!;
                var soleWritable = context.GetOrAddAsOverride(patchMod);
                CopyCellVerbatim(cellFieldSource!, soleWritable);
                soleWritable.Landscape = landSource.Landscape!.DeepCopy();
                if (!ReferenceEquals(cellFieldSource, landSource)) cellsLandscapeFromOtherTool++;
                RecordWritten(context, cell, landSource.Landscape);
                cellsFromOneSource++;
                continue;
            }

            // Either multiple of the 3 trusted tools touched this cell, or
            // exactly one did AND Snow Fixer also wants to layer its flag on
            // top - either way, build a merged Landscape rather than a
            // verbatim copy.
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

            // Snow Fixer's ONE verified contribution: the Landscape.Flags
            // per-quadrant snow bits. Applied on top of whatever height/
            // texture the 3 trusted tools already established - never
            // touches VertexHeightMap or Layers, so it can't undo anything
            // the union/height-priority logic above just did.
            if (snowFixerCell?.Landscape is { } snowLandscape)
            {
                ApplySnowChanges(mergedLandscape, snowLandscape);
                cellsSnowFlagApplied++;
                log($"  ({cell.Grid.Point.X},{cell.Grid.Point.Y}): applied Snow Fixer's Landscape.Flags on top of {heightSourceName}'s height/texture.");
            }

            writable.Landscape = mergedLandscape;
            RecordWritten(context, cell, mergedLandscape);

            // Cell-level fields (Water/WaterHeight/Flags/Persistent/Temporary):
            // every one of the 3 tools already forwards these UNCHANGED from
            // whichever cell was actually winning before it ran, so all 3
            // copies SHOULD agree. Don't just silently pick one - check, and
            // log if they don't (would mean one of the source tools has its
            // own preservation bug, worth surfacing rather than masking).
            // Deliberately excludes snowFixerCell - Snow Fixer's own
            // handling of these fields is unverified (see the note where
            // landscapeTouchCount is computed above), so it never becomes
            // fieldSource even if it's the only OTHER thing touching a cell
            // alongside a single trusted tool.
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

        log($"Single-source cells whose terrain came from a different tool than their cell-level fields: {cellsLandscapeFromOtherTool}. " +
            $"Cells forwarded with cell-level fields only (a trusted tool changed no terrain there, e.g. Seam Fixer water): {cellsFieldsOnlyForwarded}. " +
            $"Snow Fixer-only cells with its snow changes applied onto the mods-underneath terrain: {cellsSnowOnlyApplied} ({cellsSnowFixerOnlySkipped} had no terrain to apply them to).");

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
                    if (!TryResolveContextExcluding<PlacedObject, IPlacedObjectGetter>(linkCache, placedObj.FormKey, outputPluginName, out var winningCtx)) continue;
                    var writable = winningCtx.GetOrAddAsOverride(patchMod);
                    if (writable.Placement is null) { log($"  WARNING: {placedObj.FormKey} - winning context has no Placement to set Position on; skipped."); continue; }
                    writable.Placement.Position = placedObj.Placement.Position;
                    refsForwarded++;
                }
                foreach (var placedNpc in fofMod.EnumerateMajorRecords<IPlacedNpcGetter>())
                {
                    if (placedNpc.Placement is null) continue;
                    if (!TryResolveContextExcluding<PlacedNpc, IPlacedNpcGetter>(linkCache, placedNpc.FormKey, outputPluginName, out var winningCtx)) continue;
                    var writable = winningCtx.GetOrAddAsOverride(patchMod);
                    if (writable.Placement is null) { log($"  WARNING: {placedNpc.FormKey} - winning context has no Placement to set Position on; skipped."); continue; }
                    writable.Placement.Position = placedNpc.Placement.Position;
                    refsForwarded++;
                }
            }
        }

        // --- Snow Fixer's base-record contribution: Static/Furniture/
        // MoveableStatic "ConsideredSnow"-family Flags overrides. Confirmed
        // via houseCARL (read_plugin_file against a real SnowFixer.esp) that
        // these are all overrides of EXISTING base records (vanilla/mod-
        // defined statics like "RockCliff01Snow01_LightSN"), never new
        // record definitions, and every sampled record's only change is its
        // own Flags field (e.g. Static.Flags gaining ConsideredSnow) - no
        // overlap risk with anything the other 4 tools touch (none of them
        // write STAT/FURN/MSTT base records at all), so this is forwarded
        // the same verbatim-override way as Floating Object Fixer's
        // PlacedObject/PlacedNpc contribution above, just on base records
        // instead of placed references.
        if (present.SnowFixer)
        {
            var snowModKey = ModKey.FromFileName(sources.SnowFixer);
            if (!loadOrderMods.TryGetValue(snowModKey, out var snowListing) || snowListing.Mod is null)
            {
                log($"  WARNING: {sources.SnowFixer} was reported present but its mod data couldn't be loaded - skipping its base-record contribution.");
            }
            else
            {
                var snowMod = snowListing.Mod;
                foreach (var stat in snowMod.EnumerateMajorRecords<IStaticGetter>())
                {
                    if (!TryResolveContextExcluding<Static, IStaticGetter>(linkCache, stat.FormKey, outputPluginName, out var winningCtx)) continue;
                    winningCtx.GetOrAddAsOverride(patchMod).Flags = stat.Flags;
                    snowFixerBaseRecordsForwarded++;
                }
                foreach (var furn in snowMod.EnumerateMajorRecords<IFurnitureGetter>())
                {
                    if (!TryResolveContextExcluding<Furniture, IFurnitureGetter>(linkCache, furn.FormKey, outputPluginName, out var winningCtx)) continue;
                    winningCtx.GetOrAddAsOverride(patchMod).Flags = furn.Flags;
                    snowFixerBaseRecordsForwarded++;
                }
                foreach (var mstt in snowMod.EnumerateMajorRecords<IMoveableStaticGetter>())
                {
                    if (!TryResolveContextExcluding<MoveableStatic, IMoveableStaticGetter>(linkCache, mstt.FormKey, outputPluginName, out var winningCtx)) continue;
                    winningCtx.GetOrAddAsOverride(patchMod).Flags = mstt.Flags;
                    snowFixerBaseRecordsForwarded++;
                }
                if (snowFixerBaseRecordsForwarded > 0)
                    log($"  Forwarded {snowFixerBaseRecordsForwarded} Snow Fixer base-record (Static/Furniture/MoveableStatic) Flags override(s).");
            }
        }

        // --- Optional: match untouched neighbors to the trusted chain ---
        // Runs after the landscape merge (it needs PatchForeman's own final
        // edges) and only when ticked. See NeighborChainMatcher.cs.
        NeighborMatchStats? neighborStats = null;
        if (matchNeighborsToTrustedChain)
        {
            var mastersByPlugin = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var listing in loadOrderMods.ListedOrder)
            {
                if (listing.Mod is null) continue;
                mastersByPlugin[listing.ModKey.FileName] = listing.Mod.ModHeader.MasterReferences
                    .Select(m => m.Master.FileName.String)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            var excluded = new HashSet<string>(TrustResolver.SiblingToolOutputs, StringComparer.OrdinalIgnoreCase)
            {
                sources.LandscapeSeamFixer, sources.RoadMaskMerger, sources.LandscapeTextureFixer,
                sources.FloatingObjectFixer, sources.SnowFixer, outputPluginName,
            };
            excluded.Remove(string.Empty);
            neighborStats = NeighborChainMatcher.Run(linkCache, priorityIndex, mastersByPlugin, outputPluginName,
                excluded, writtenLandscapes, patchMod, log);
        }

        var stats = new CellMergeStats(cellsFromOneSource, cellsMerged, waterDisagreements, refsForwarded,
            cellsSnowFlagApplied, cellsSnowFixerOnlySkipped, snowFixerBaseRecordsForwarded, neighborStats);

        if (dryRun)
        {
            log($"[DRY RUN] Would write {outputPluginName} with {cellsFromOneSource + cellsMerged} cell(s) touched " +
                $"({cellsFromOneSource} from a single source tool, {cellsMerged} merged across tools, {cellsSnowFlagApplied} with a Snow Fixer flag applied, " +
                $"{cellsSnowFixerOnlySkipped} skipped as Snow-Fixer-only) and {refsForwarded} " +
                $"placed-reference override(s) from Floating Object Fixer, {snowFixerBaseRecordsForwarded} base-record override(s) from Snow Fixer" +
                (neighborStats is null ? "" : $", {neighborStats.Matched} untouched neighbor(s) matched to the trusted chain") + ". Nothing written.");
            return new MergeResult(stats, string.Empty, DryRun: true);
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);

        var written = WritePatch(patchMod, outputPath, dataFolderForWrite, log);
        return new MergeResult(stats, outputPath, DryRun: false, written);
    }

    // Writes the merged patch. A plugin can reference at most 255 masters; on a
    // big load order the overrides this patch carries can come from more
    // plugins than that. Mutagen's auto-split (the same mechanism Synthesis's
    // "Split Files if Max Masters Exceeded" uses) then writes adjacent siblings
    // - Name.esp, Name_2.esp, Name_3.esp - each within the limit, with a shared
    // master list where they overlap. When nothing overflows the output is a
    // single plugin exactly as before. Returns every file written, base first.
    public static IReadOnlyList<string> WritePatch(SkyrimMod patchMod, string outputPath, string dataFolderForWrite, Action<string> log)
    {
        RetireStaleSplitSiblings(outputPath, log);

        var eslResult = EslEligibility.CheckAndFlag(patchMod);
        log(eslResult.Summary);

        log($"Writing merged patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .WithAutoSplit()
            .Write(patchMod);

        var files = EnumerateSplitSiblings(outputPath);
        files.Insert(0, outputPath);
        if (files.Count > 1)
        {
            log($"Master limit exceeded: the patch was split into {files.Count} plugins. ALL of them must be enabled, " +
                "and they must sit next to each other in the load order, in this order:");
            foreach (var f in files) log("  " + Path.GetFileName(f));
        }
        return files;
    }

    // Existing PatchForeman_2.esp, _3.esp, ... next to the output, sorted by index.
    static List<string> EnumerateSplitSiblings(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        var found = new List<(int Index, string Path)>();
        foreach (var candidate in Directory.EnumerateFiles(dir, baseName + "_*" + ext))
        {
            if (!Path.GetExtension(candidate).Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (MultiModFileAnalysis.IsSplitFileName(Path.GetFileNameWithoutExtension(candidate), baseName, out var index))
                found.Add((index, candidate));
        }
        return found.OrderBy(x => x.Index).Select(x => x.Path).ToList();
    }

    // A previous run that split leaves PatchForeman_2.esp behind; if this run
    // fits in one plugin that file would linger as an orphan sibling. Move it
    // aside under the toolkit's backup naming (never deleted, never loadable).
    static void RetireStaleSplitSiblings(string outputPath, Action<string> log)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        foreach (var stale in EnumerateSplitSiblings(outputPath))
        {
            var bak = stale + ".bak_" + stamp;
            File.Move(stale, bak);
            log($"Moved leftover split plugin from a previous run aside: {Path.GetFileName(stale)} -> {Path.GetFileName(bak)}");
        }
    }

    // FIXED 2026-09-26: every "copy the current winner, then change one field"
    // step used the load-order winner as its base - which, on any re-run, is
    // the PREVIOUS PatchForeman.esp still active in the profile. That fed old
    // output back in, and crashed the write outright on the live profile:
    // the previous PatchForeman.esp's copy of RockCliff01Rocks01SnowLight
    // (02ED76:Skyrim.esm) could not be deep-copied ("Specified argument was out
    // of the range of valid values" in StaticBinaryOverlay.get_Unused).
    static bool TryResolveContextExcluding<TMajor, TMajorGetter>(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, FormKey formKey, string excludePlugin,
        out IModContext<ISkyrimMod, ISkyrimModGetter, TMajor, TMajorGetter> context)
        where TMajor : class, IMajorRecord, TMajorGetter
        where TMajorGetter : class, IMajorRecordGetter
    {
        context = linkCache.ResolveAllContexts<TMajor, TMajorGetter>(formKey, ResolveTarget.Winner)
            .FirstOrDefault(c => !c.ModKey.FileName.String.Equals(excludePlugin, StringComparison.OrdinalIgnoreCase))!;
        return context is not null;
    }

    // Snow Fixer's verified LAND changes, confirmed on LAND 00AB35 against the
    // plugins under it (2026-09-26): Flags (the vertex-colour bit cleared,
    // 31 -> 29) and VertexColors removed. Heights and texture layers identical.
    // Copying only Flags (the old behavior) left the flag saying "no vertex
    // colours" while the data still had them.
    static void ApplySnowChanges(Landscape target, ILandscapeGetter snow)
    {
        target.Flags = snow.Flags;
        if (snow.VertexColors is null) target.VertexColors = null;
    }

    // Highest-priority version of the cell / its terrain from any plugin that
    // is NOT a tool output (ResolveAllContexts yields winner first).
    static ICellGetter? WinningCellExcluding(FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, HashSet<string> excluded) =>
        linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner)
            .FirstOrDefault(c => !excluded.Contains(c.ModKey.FileName.String))?.Record;

    static ILandscapeGetter? WinningLandscapeExcluding(FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, HashSet<string> excluded) =>
        linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner)
            .FirstOrDefault(c => c.Record.Landscape is not null && !excluded.Contains(c.ModKey.FileName.String))?.Record.Landscape;

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
