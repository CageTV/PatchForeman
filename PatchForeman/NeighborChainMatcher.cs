// Optional pass (UI tick box / --match-neighbors-to-trusted-chain): closes
// seams between a cell PatchForeman wrote and an UNTOUCHED neighbor by
// giving that neighbor the terrain a fixed, user-chosen trusted chain
// authored for it.
//
// Why: the 3 landscape tools each restore/merge the cells they touch, but a
// neighbor none of them touched keeps whatever terrain happens to win there -
// often an untrusted mod's copy (or Snow Fixer's, which carries whatever
// heights won when IT ran). Where that neighbor's edge disagrees with our
// cell's edge, the result is a seam PatchForeman itself can't see.
//
// The chain, lowest to highest priority (user-specified 2026-09-26, "last in
// the list wins"):
//   Skyrim.esm > Update.esm > Dawnguard.esm > HearthFires.esm > Dragonborn.esm
//   > Landscape and Water Fixes.esp > UniqueLocationsRiverwoodForest.esp > Lux Via.esp
// The last 3 also count their own compatibility patches (TrustResolver's
// "<Mod> - ..." / non-standard-prefix / masters rules); the 5 masters count
// by exact name only, since nearly every plugin masters Skyrim.esm and the
// masters rule would otherwise make the whole load order "a Skyrim patch".
//
// Same no-blending philosophy as SeamFixer: nothing is computed, the
// chain's own heights + normals are copied verbatim. Texture layers, vertex
// colours and Landscape.Flags (e.g. Snow Fixer's snow bits) stay the
// current winner's - only the SHAPE changes.
//
// Gates, all conservative - a neighbor is only changed when:
//   - it borders a PatchForeman cell with a real seam (> SeamDetector's 8-unit tolerance),
//   - Northern Roads / its patches never genuinely edited it ("not part of Northern Roads"),
//   - the chain has terrain for it that differs from what currently wins,
//   - it has no water plane, and no placed reference's ground moves > 20 units
//     (both SeamFixer's own thresholds),
//   - the swap shrinks a seam against a PatchForeman cell, doesn't raise the
//     cell's total seamed-edge count, and doesn't push ANY of its 4 edges
//     (against PatchForeman cells or ordinary ones) past tolerance where it
//     wasn't already that bad.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

namespace PatchForeman;

public static class NeighborChainMatcher
{
    public static readonly string[] TrustedChain =
    [
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "Landscape and Water Fixes.esp", "UniqueLocationsRiverwoodForest.esp", "Lux Via.esp",
    ];
    const int FirstModRank = 5; // chain entries from here on also accept their patch families

    const float SeamTolerance = SeamDetector.ToleranceUnits;
    const float MaxReferenceMove = 20f; // same as SeamFixer's reference-safety gate

    static readonly (int Dx, int Dy)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static NeighborMatchStats Run(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        Dictionary<string, HashSet<string>> mastersByPlugin,
        string outputPluginName,
        HashSet<string> excludedPlugins,
        Dictionary<(FormKey Worldspace, int X, int Y), ILandscapeGetter> writtenLandscapes,
        SkyrimMod patchMod,
        Action<string> log)
    {
        TrustResolver.SetMastersContext(mastersByPlugin, outputPluginName);
        log("");
        log("=== Neighbor match: untouched neighbors -> trusted chain ===");
        log("Chain (last wins): " + string.Join(" > ", TrustedChain));

        // Every exterior cell's winning context, by (worldspace, grid) - the
        // neighbor lookup needs arbitrary grid access, not just a stream.
        var cells = new Dictionary<(FormKey, int, int), IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>();
        var wsNames = new Dictionary<FormKey, string>();
        foreach (var winning in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            if (winning.Record.Grid is null) continue;
            // A previous PatchForeman.esp is usually still active while this
            // runs. Read around it: otherwise a neighbor it already fixed
            // looks "already on chain terrain" and the fix silently drops out
            // of the regenerated plugin.
            var ctx = winning;
            if (IsOutput(ctx.ModKey))
            {
                var prior = linkCache.ResolveAllContexts<Cell, ICellGetter>(winning.Record.FormKey, ResolveTarget.Winner)
                    .FirstOrDefault(c => !IsOutput(c.ModKey));
                if (prior is null) continue;
                ctx = prior;
            }
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var ws)) continue;
            cells[(ws.Record.FormKey, winning.Record.Grid.Point.X, winning.Record.Grid.Point.Y)] = ctx;
            wsNames.TryAdd(ws.Record.FormKey, ws.Record.EditorID ?? ws.Record.FormKey.ToString());
        }
        bool IsOutput(ModKey k) => k.FileName.String.Equals(outputPluginName, StringComparison.OrdinalIgnoreCase);

        // Same as HeightmapDecoder.ResolveWinningLandscape, minus our own prior output.
        (ILandscapeGetter? Land, ModKey Owner) WinningLandscape(FormKey cellFormKey)
        {
            ILandscapeGetter? best = null;
            ModKey bestKey = default;
            int bestIdx = -1;
            foreach (var c in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
            {
                if (c.Record.Landscape is null || IsOutput(c.ModKey)) continue;
                var idx = priorityIndex.GetValueOrDefault(c.ModKey, -1);
                if (idx > bestIdx) { bestIdx = idx; best = c.Record.Landscape; bestKey = c.ModKey; }
            }
            return (best, bestKey);
        }

        // Effective heights as they'll look with PatchForeman installed:
        // our written cells, then any neighbor this pass already replaced,
        // then the ordinary winner.
        var replaced = new Dictionary<(FormKey, int, int), float[,]>();
        var decodedCache = new Dictionary<(FormKey, int, int), float[,]?>();
        float[,]? Effective((FormKey, int, int) key)
        {
            if (replaced.TryGetValue(key, out var r)) return r;
            if (decodedCache.TryGetValue(key, out var cached)) return cached;
            float[,]? h = null;
            if (writtenLandscapes.TryGetValue(key, out var ours))
                h = ours.VertexHeightMap is null ? null : HeightmapDecoder.DecodeHeights(ours.VertexHeightMap);
            else if (cells.TryGetValue(key, out var c))
            {
                var (land, _) = WinningLandscape(c.Record.FormKey);
                h = land?.VertexHeightMap is null ? null : HeightmapDecoder.DecodeHeights(land.VertexHeightMap);
            }
            decodedCache[key] = h;
            return h;
        }

        // Candidates: untouched neighbors with a real seam against one of our cells.
        var candidates = new SortedSet<(FormKey Ws, int X, int Y)>(Comparer<(FormKey Ws, int X, int Y)>.Create((a, b) =>
        {
            var c = string.CompareOrdinal(a.Ws.ToString(), b.Ws.ToString());
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        }));
        foreach (var (ws, x, y) in writtenLandscapes.Keys)
        {
            var mine = Effective((ws, x, y));
            if (mine is null) continue;
            foreach (var (dx, dy) in Directions)
            {
                var nKey = (ws, x + dx, y + dy);
                if (writtenLandscapes.ContainsKey(nKey) || !cells.ContainsKey(nKey)) continue;
                var theirs = Effective(nKey);
                if (theirs is null) continue;
                if (EdgeDelta(theirs, mine, -dx, -dy) > SeamTolerance) candidates.Add(nKey);
            }
        }

        // Diagnostic for a seam this pass can't fix from the neighbor's side:
        // for each seamed edge against one of our cells, show how far EVERY
        // plugin's version of our cell is from the neighbor along that edge,
        // so it's visible which plugin's terrain stops lining up.
        void LogOurSideOfSeam((FormKey, int, int) nKey, float[,] neighborHeights)
        {
            var (ws, x, y) = nKey;
            foreach (var (dx, dy) in Directions)
            {
                var ourKey = (ws, x + dx, y + dy);
                if (!writtenLandscapes.TryGetValue(ourKey, out var ours) || ours.VertexHeightMap is null) continue;
                var written = EdgeDelta(neighborHeights, HeightmapDecoder.DecodeHeights(ours.VertexHeightMap), dx, dy);
                if (written <= SeamTolerance) continue;
                var perPlugin = new List<string>();
                if (cells.TryGetValue(ourKey, out var ourCtx))
                {
                    foreach (var c in linkCache.ResolveAllContexts<Cell, ICellGetter>(ourCtx.Record.FormKey, ResolveTarget.Winner).Reverse())
                    {
                        if (c.Record.Landscape?.VertexHeightMap is null || IsOutput(c.ModKey)) continue;
                        var d = EdgeDelta(neighborHeights, HeightmapDecoder.DecodeHeights(c.Record.Landscape.VertexHeightMap), dx, dy);
                        perPlugin.Add($"{c.ModKey.FileName}={d:F0}");
                    }
                }
                log($"      our cell ({x + dx},{y + dy}) on the {DirName(dx, dy)}: PatchForeman wrote an edge {written:F0} units off. " +
                    $"Each plugin's version of that edge (load order, low->high): {string.Join(", ", perPlugin)}");
                // And the mirror image: every plugin's version of the NEIGHBOR,
                // measured against what PatchForeman wrote for our cell.
                var oursDecoded = HeightmapDecoder.DecodeHeights(ours.VertexHeightMap);
                var neighborPlugins = new List<string>();
                foreach (var c in linkCache.ResolveAllContexts<Cell, ICellGetter>(cells[nKey].Record.FormKey, ResolveTarget.Winner).Reverse())
                {
                    if (c.Record.Landscape?.VertexHeightMap is null || IsOutput(c.ModKey)) continue;
                    var d = EdgeDelta(HeightmapDecoder.DecodeHeights(c.Record.Landscape.VertexHeightMap), oursDecoded, dx, dy);
                    neighborPlugins.Add($"{c.ModKey.FileName}={d:F0}");
                }
                log($"      neighbor ({x},{y}) - each plugin's version vs what PatchForeman wrote: {string.Join(", ", neighborPlugins)}");
            }
        }

        int matched = 0, skipNr = 0, skipNoChain = 0, skipSame = 0, skipWater = 0, skipRef = 0, skipNoGain = 0;
        foreach (var key in candidates)
        {
            var (ws, x, y) = key;
            var where = $"[{wsNames.GetValueOrDefault(ws, ws.ToString())}] ({x},{y})";
            var ctx = cells[key];
            var cell = ctx.Record;

            var (chainLand, chainOwner, nrOwner) = ResolveChain(cell.FormKey, linkCache, priorityIndex, excludedPlugins);
            if (nrOwner is not null) { skipNr++; log($"  {where}: skipped - part of Northern Roads ({nrOwner})."); continue; }
            if (chainLand?.VertexHeightMap is null) { skipNoChain++; log($"  {where}: skipped - no trusted-chain terrain for this cell."); continue; }

            var (winLand, winOwner) = WinningLandscape(cell.FormKey);
            if (winLand?.VertexHeightMap is null) { skipNoChain++; continue; }
            var current = HeightmapDecoder.DecodeHeights(winLand.VertexHeightMap);
            var chain = HeightmapDecoder.DecodeHeights(chainLand.VertexHeightMap);
            if (HeightmapDecoder.HeightsMatch(current, chain))
            {
                skipSame++;
                log($"  {where}: skipped - already carries {chainOwner.FileName}'s terrain (winner {winOwner.FileName}); the seam is on PatchForeman's side.");
                LogOurSideOfSeam(key, current);
                continue;
            }

            var hasWater = (cell.WaterHeight.HasValue && cell.WaterHeight.Value < 1_000_000f) || cell.Water.FormKeyNullable.HasValue;
            if (hasWater) { skipWater++; log($"  {where}: skipped - has its own water plane (swapping terrain could strand it)."); continue; }

            var worstMove = WorstReferenceMove(cell, current, chain);
            if (worstMove > MaxReferenceMove)
            {
                skipRef++;
                log($"  {where}: skipped - {chainOwner.FileName}'s terrain would move a placed reference's ground by {worstMove:F0} units.");
                continue;
            }

            // Score all 4 edges before vs after.
            int seamsBefore = 0, seamsAfter = 0;
            bool worsens = false, improvesOurs = false;
            var detail = new List<string>();
            foreach (var (dx, dy) in Directions)
            {
                var other = Effective((ws, x + dx, y + dy));
                if (other is null) continue;
                var before = EdgeDelta(current, other, dx, dy);
                var after = EdgeDelta(chain, other, dx, dy);
                if (before > SeamTolerance) seamsBefore++;
                if (after > SeamTolerance) seamsAfter++;
                if (after > SeamTolerance && after > before + 0.5f) worsens = true;
                var isOurs = writtenLandscapes.ContainsKey((ws, x + dx, y + dy));
                if (isOurs && before > SeamTolerance && after < before) improvesOurs = true;
                detail.Add($"{DirName(dx, dy)}{(isOurs ? "*" : "")} {before:F0}->{after:F0}");
            }
            var edges = string.Join(", ", detail);
            if (!improvesOurs || worsens || seamsAfter > seamsBefore)
            {
                skipNoGain++;
                log($"  {where}: skipped - {chainOwner.FileName}'s terrain doesn't help overall (edge max deltas {edges}; * = PatchForeman cell).");
                continue;
            }

            var writable = ctx.GetOrAddAsOverride(patchMod);
            // The Floating Object Fixer pass can already have created this
            // cell's override (as a ref's parent) - don't duplicate its refs.
            var present = writable.Persistent.Select(p => p.FormKey).Concat(writable.Temporary.Select(t => t.FormKey)).ToHashSet();
            foreach (var p in cell.Persistent) if (!present.Contains(p.FormKey)) writable.Persistent.Add((IPlaced)p.DeepCopy());
            foreach (var t in cell.Temporary) if (!present.Contains(t.FormKey)) writable.Temporary.Add((IPlaced)t.DeepCopy());

            var newLand = winLand.DeepCopy();
            var chainCopy = chainLand.DeepCopy();
            newLand.VertexHeightMap = chainCopy.VertexHeightMap;
            newLand.VertexNormals = chainCopy.VertexNormals;
            writable.Landscape = newLand;

            replaced[key] = chain;
            matched++;
            log($"  {where}: matched to {chainOwner.FileName} (was {winOwner.FileName}); edge max deltas {edges}; seamed edges {seamsBefore}->{seamsAfter}.");
        }

        var stats = new NeighborMatchStats(candidates.Count, matched, skipNr, skipNoChain, skipSame, skipWater, skipRef, skipNoGain);
        log($"Neighbor match: {stats.NeighborsConsidered} seamed untouched neighbor(s) considered - {matched} matched to the chain, " +
            $"{skipNr} Northern Roads, {skipNoChain} no chain terrain, {skipSame} already chain terrain, {skipWater} water, " +
            $"{skipRef} reference move, {skipNoGain} no overall improvement.");
        var sum = matched + skipNr + skipNoChain + skipSame + skipWater + skipRef + skipNoGain;
        if (sum != stats.NeighborsConsidered)
            log($"  ACCOUNTING MISMATCH: buckets sum to {sum}, not {stats.NeighborsConsidered}.");
        return stats;
    }

    // Highest-ranked trusted-chain Landscape for this cell, plus the first
    // Northern Roads-family plugin that genuinely edited it (differs from
    // Skyrim.esm), if any.
    static (ILandscapeGetter? Land, ModKey Owner, string? NorthernRoadsOwner) ResolveChain(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        HashSet<string> excludedPlugins)
    {
        ILandscapeGetter? best = null, vanilla = null;
        ModKey bestKey = default;
        int bestRank = -1, bestIdx = -1;
        bool bestIsPatch = false;
        var nrEdits = new List<(string File, ILandscapeGetter Land)>();

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            var land = ctx.Record.Landscape;
            if (land is null) continue;
            var file = ctx.ModKey.FileName.String;
            if (file.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) vanilla = land;
            if (TrustResolver.IsNorthernRoadsOrItsPatch(file, trustNorthernRoads: true)) nrEdits.Add((file, land));
            if (excludedPlugins.Contains(file)) continue;

            var (rank, isPatch) = ChainRank(file);
            if (rank < 0) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            var wins = best is null
                || rank > bestRank
                || (rank == bestRank && isPatch && !bestIsPatch)
                || (rank == bestRank && isPatch == bestIsPatch && idx > bestIdx);
            if (!wins) continue;
            best = land; bestKey = ctx.ModKey; bestRank = rank; bestIdx = idx; bestIsPatch = isPatch;
        }

        string? nrOwner = null;
        foreach (var (file, land) in nrEdits)
        {
            if (land.VertexHeightMap is null) continue;
            var genuine = vanilla?.VertexHeightMap is null
                || !HeightmapDecoder.HeightsMatch(HeightmapDecoder.DecodeHeights(land.VertexHeightMap), HeightmapDecoder.DecodeHeights(vanilla.VertexHeightMap));
            if (genuine) { nrOwner = file; break; }
        }
        return (best, bestKey, nrOwner);
    }

    static (int Rank, bool IsPatch) ChainRank(string file)
    {
        for (int i = TrustedChain.Length - 1; i >= 0; i--)
        {
            if (file.Equals(TrustedChain[i], StringComparison.OrdinalIgnoreCase)) return (i, false);
            if (i >= FirstModRank && TrustResolver.IsPatchOfTrustedBase(file, TrustedChain[i])) return (i, true);
        }
        return (-1, false);
    }

    // Max vertex delta along the edge `mine` shares with the cell at (dx,dy) from it.
    static float EdgeDelta(float[,] mine, float[,] other, int dx, int dy)
    {
        float max = 0f;
        for (int i = 0; i <= 32; i++)
        {
            float a, b;
            if (dx == 1) { a = mine[32, i]; b = other[0, i]; }
            else if (dx == -1) { a = mine[0, i]; b = other[32, i]; }
            else if (dy == 1) { a = mine[i, 32]; b = other[i, 0]; }
            else { a = mine[i, 0]; b = other[i, 32]; }
            max = Math.Max(max, Math.Abs(a - b));
        }
        return max;
    }

    static string DirName(int dx, int dy) => dx == 1 ? "E" : dx == -1 ? "W" : dy == 1 ? "N" : "S";

    // Same check as SeamFixer's reference-safety gate.
    static float WorstReferenceMove(ICellGetter cell, float[,] current, float[,] replacement)
    {
        var originX = cell.Grid!.Point.X * 4096f;
        var originY = cell.Grid.Point.Y * 4096f;
        float worst = 0f;
        foreach (var r in cell.Persistent.Concat(cell.Temporary))
        {
            if (r.Placement is null) continue;
            var lx = r.Placement.Position.X - originX;
            var ly = r.Placement.Position.Y - originY;
            if (lx is < 0f or > 4096f || ly is < 0f or > 4096f) continue;
            var vx = Math.Clamp((int)Math.Round(lx / 128f), 0, 32);
            var vy = Math.Clamp((int)Math.Round(ly / 128f), 0, 32);
            worst = Math.Max(worst, Math.Abs(replacement[vx, vy] - current[vx, vy]));
        }
        return worst;
    }
}
