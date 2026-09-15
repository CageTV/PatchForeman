// Core landscape-seam detection logic, shared between the console CLI and
// the WPF UI. See Mo2Resolver.cs for how an MO2 instance gets turned into
// a real load order without needing MO2's own VFS.
//
// Landscape is a property embedded directly in each plugin's own copy of a
// CELL record (not a separately FormID-linked/resolved record), so the
// overall "winning CELL override" is not guaranteed to be the same plugin
// that actually owns the winning landscape edit - some cell overrides don't
// touch landscape at all and have a null Landscape. ResolveWinningLandscape
// resolves that independently by walking every plugin's own copy of the
// cell and picking the highest-priority one that actually has non-null
// landscape data.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// IsCorrectable mirrors SeamFixer.CellData's field of the same name - see
// its comment there. True only when this cell's actual data is identical to
// PURE VANILLA (Skyrim.esm/Update.esm/the three DLCs), not merely to the
// wider trusted chain - a plugin being trusted doesn't mean a real edit of
// its is safe to overwrite.
public record DecodedCell(float[,] Heights, string Plugin, bool IsCorrectable);

public record DetectionResult(
    List<string> ReportCsvLines,
    int PluginCount,
    int CellsProcessed,
    int WorldspaceCount,
    int SkippedInterior,
    int SkippedNoWorldspace,
    int SkippedNoLandscape,
    int OrderingMismatchWarnings,
    int SeamCount);

public static class SeamDetector
{
    public const float ToleranceUnits = 8.0f; // one raw heightmap step; ignore anything smaller as float noise

    /// Runs detection against an already-resolved list of active plugin
    /// files (e.g. from Mo2Resolver), by materializing them into a single
    /// merged folder and building a Mutagen environment from it.
    public static DetectionResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        Action<string> log,
        bool trustNorthernRoads = false)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "SeamFinderMerged-" + Guid.NewGuid().ToString("N"));
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
            return RunDetection(env.LinkCache, priorityIndex, modKeys.Length, log, trustNorthernRoads);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    /// Runs detection against a plain Data folder (no mod-manager awareness -
    /// e.g. a non-MO2-managed install, or a folder where mods were dumped
    /// directly into Data). Uses whatever plugins.txt that folder's install
    /// normally resolves to via Mutagen's own auto-detection.
    public static DetectionResult RunForDirectDataFolder(string dataFolderPath, Action<string> log, bool trustNorthernRoads = false)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        return RunDetection(env.LinkCache, priorityIndex, env.LoadOrder.Count, log, trustNorthernRoads);
    }

    public static DetectionResult RunDetection(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        int loadOrderCount,
        Action<string> log,
        bool trustNorthernRoads = false)
    {
        log($"Load order: {loadOrderCount} plugins.");

        var cellsByWorldspace = new Dictionary<FormKey, Dictionary<(int X, int Y), DecodedCell>>();
        int processed = 0, skippedInterior = 0, skippedNoLandscape = 0, skippedNoWorldspace = 0, orderingMismatchWarnings = 0;

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null)
            {
                skippedInterior++;
                continue;
            }

            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext))
            {
                skippedNoWorldspace++;
                continue;
            }
            var worldspaceKey = wsContext.Record.FormKey;

            var (landscape, ownerModKey) = ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.VertexHeightMap is null)
            {
                skippedNoLandscape++;
                continue;
            }

            // Sanity cross-check: the plugin we picked as Landscape's own
            // winner should never be *later* in load order than the plugin
            // that won the overall CELL record - if it is, the "highest
            // load-order index" selection below has gone wrong somehow.
            var cellWinnerIndex = priorityIndex.GetValueOrDefault(context.ModKey, -1);
            var landscapeWinnerIndex = priorityIndex.GetValueOrDefault(ownerModKey, -1);
            if (landscapeWinnerIndex > cellWinnerIndex)
                orderingMismatchWarnings++;

            var heights = HeightmapDecoder.DecodeHeights(landscape.VertexHeightMap);

            // See SeamFixer.CellData.IsCorrectable for the full reasoning:
            // gated on matching PURE VANILLA (Skyrim.esm/Update.esm/DLCs),
            // not the wider trusted chain - trusted-chain membership answers
            // "should I believe this plugin," not "has anyone actually
            // edited this cell," and only the latter says whether reshaping
            // it is safe. Kept in sync with SeamFixer's own computation so
            // this report's ModVsBaseEdge column matches what a Fix run
            // would actually queue.
            var isPureVanillaOwner = PureVanillaMasters.Contains(ownerModKey.FileName);
            var isCorrectable = isPureVanillaOwner;
            if (!isPureVanillaOwner)
            {
                var (vanillaLandscape, _) = ResolvePureVanillaLandscape(cell.FormKey, linkCache, priorityIndex);
                if (vanillaLandscape?.VertexHeightMap is not null)
                {
                    var vanillaHeights = HeightmapDecoder.DecodeHeights(vanillaLandscape.VertexHeightMap);
                    isCorrectable = HeightmapDecoder.HeightsMatch(heights, vanillaHeights);
                }
            }

            if (!cellsByWorldspace.TryGetValue(worldspaceKey, out var cellDict))
            {
                cellDict = new Dictionary<(int, int), DecodedCell>();
                cellsByWorldspace[worldspaceKey] = cellDict;
            }
            cellDict[(cell.Grid.Point.X, cell.Grid.Point.Y)] = new DecodedCell(heights, ownerModKey.FileName, isCorrectable);
            processed++;
        }

        log($"Processed {processed} exterior cells across {cellsByWorldspace.Count} worldspace(s).");
        log($"Skipped: {skippedInterior} interior, {skippedNoWorldspace} no worldspace parent, {skippedNoLandscape} no landscape data.");
        if (orderingMismatchWarnings > 0)
            log($"WARNING: {orderingMismatchWarnings} cells had an unexpected override-priority ordering - " +
                "decode results for those may be unreliable, investigate before trusting the report.");

        var report = new List<string> {
            "Worldspace,Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,ModVsBaseEdge,LocalRoughness,MaxDeltaUnits,WorstVertex"
        };

        foreach (var (wsKey, cellDict) in cellsByWorldspace)
        {
            foreach (var ((x, y), cellA) in cellDict)
            {
                if (cellDict.TryGetValue((x + 1, y), out var cellBEast))
                    CompareEdge(report, wsKey, "East", x, y, cellA, x + 1, y, cellBEast, trustNorthernRoads);

                if (cellDict.TryGetValue((x, y + 1), out var cellBNorth))
                    CompareEdge(report, wsKey, "North", x, y, cellA, x, y + 1, cellBNorth, trustNorthernRoads);
            }
        }

        var seamCount = report.Count - 1;
        log($"Found {seamCount} seam edges above {ToleranceUnits} unit tolerance.");

        return new DetectionResult(
            report, loadOrderCount, processed, cellsByWorldspace.Count,
            skippedInterior, skippedNoWorldspace, skippedNoLandscape,
            orderingMismatchWarnings, seamCount);
    }

    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllSimpleContexts<ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }

    // Same walk as ResolveWinningLandscape, but restricted to trusted/base-
    // equivalent plugins only - see SeamFixer.ResolveTrustedOnlyLandscape
    // (kept in sync with that copy).
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveTrustedOnlyLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        bool trustNorthernRoads)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllSimpleContexts<ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            if (!IsBaseGamePlugin(ctx.ModKey.FileName, trustNorthernRoads)) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }

    // Same walk again, restricted to the literal game masters only - see
    // SeamFixer.ResolvePureVanillaLandscape (kept in sync with that copy).
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolvePureVanillaLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllSimpleContexts<ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            if (!PureVanillaMasters.Contains(ctx.ModKey.FileName)) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }

    // VHGT decode/compare logic now lives in HeightmapDecoder.cs, shared
    // with SeamFixer.cs (was duplicated identically in both before).

    // Trusted plugins - not just official base-game masters, also community
    // fix mods (USSEP, Legacy of the Dragonborn, Landscape Seam Fixes.esp)
    // whose data is believed over an untrusted mod's. NOT the same question
    // as "has anyone actually edited this cell" - see PureVanillaMasters and
    // IsCorrectable below for why that distinction matters.
    static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "Unofficial Skyrim Special Edition Patch.esp", "Legacy of the Dragonborn.esm",
        // Third-party Nexus mod (spaces in the name - distinct from this
        // tool's own no-spaces output filename), built specifically as a
        // companion to Landscape and Water Fixes: nexusmods.com/.../59687.
        "Landscape Seam Fixes.esp",
    };

    // The actual game master files - a strict subset of BaseGamePlugins
    // above, with zero deliberate edits by anyone. See
    // SeamFixer.PureVanillaMasters (kept in sync with that copy) for the
    // full reasoning: trusted-chain membership answers "should I believe
    // this plugin," not "has anyone actually changed this cell," and only
    // the latter decides whether a cell is safe to reshape.
    static readonly HashSet<string> PureVanillaMasters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    // "Landscape and Water Fixes" and its whole patch family (matched by
    // prefix - dozens of variously-named compatibility patches all carry
    // the same trust) are treated as trusted/base too, same reasoning as
    // SeamFixer.IsBaseGamePlugin: the community treats it as a repair to
    // landscape mistakes Bethesda itself left in, not as new content. Kept
    // in sync with the fixer's own copy so the report's ModVsBaseEdge
    // column always matches what the fixer will actually act on.
    // Northern Roads is opt-in, not always-on - see SeamFixer.IsBaseGamePlugin
    // for why (two differently-scoped downloads share the exact same
    // installed filename, so the tool can't tell them apart on its own).
    static bool IsBaseGamePlugin(string plugin, bool trustNorthernRoads) =>
        BaseGamePlugins.Contains(plugin)
        || plugin.StartsWith("Landscape and Water Fixes", StringComparison.OrdinalIgnoreCase)
        || (trustNorthernRoads && plugin.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase));

    static void CompareEdge(List<string> report, FormKey worldspace, string edgeName,
        int ax, int ay, DecodedCell a, int bx, int by, DecodedCell b, bool trustNorthernRoads)
    {
        float maxDelta = 0;
        int worst = -1;
        for (int i = 0; i <= 32; i++)
        {
            float ha, hb;
            if (edgeName == "East")
            {
                ha = a.Heights[32, i];
                hb = b.Heights[0, i];
            }
            else
            {
                ha = a.Heights[i, 32];
                hb = b.Heights[i, 0];
            }
            var d = Math.Abs(ha - hb);
            if (d > maxDelta) { maxDelta = d; worst = i; }
        }

        if (maxDelta > ToleranceUnits)
        {
            // A mod's isolated edit touching untouched base-game terrain is
            // usually a small, standalone patch (e.g. around a player home)
            // in ordinary terrain - much easier to navigate to and visually
            // confirm than two big overhaul mods overlapping deep in a
            // mountain range. True only when exactly one side is base game.
            var aIsBase = a.IsCorrectable;
            var bIsBase = b.IsCorrectable;
            var modVsBaseEdge = aIsBase != bIsBase;

            // Standard deviation of every vertex in both cells, as a rough
            // "how rugged is the ground right here" signal - a real mismatch
            // in otherwise-flat terrain reads as an obvious out-of-place
            // cliff, while the same delta in already-jagged mountain terrain
            // blends in and is hard to visually confirm.
            var roughness = Math.Max(ComputeRoughness(a.Heights), ComputeRoughness(b.Heights));

            report.Add($"{worldspace},{edgeName},{ax},{ay},{a.Plugin},{bx},{by},{b.Plugin}," +
                $"{a.Plugin == b.Plugin},{modVsBaseEdge},{roughness:0.0},{maxDelta:0.0},{worst}");
        }
    }

    static double ComputeRoughness(float[,] heights)
    {
        double sum = 0, sumSq = 0;
        const int n = 33 * 33;
        foreach (var h in heights)
        {
            sum += h;
            sumSq += (double)h * h;
        }
        var mean = sum / n;
        var variance = sumSq / n - mean * mean;
        return Math.Sqrt(Math.Max(0, variance));
    }
}
