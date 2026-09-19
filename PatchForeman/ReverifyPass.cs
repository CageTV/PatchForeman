// The "re-verify" half of PatchForeman: re-runs each of the 3 sibling tools'
// OWN detection logic (SeamDetector for height, TextureLayerDetector for
// texture, FloatingObjectFixer's own detector for placed-object floating)
// against the load order WITH the freshly-merged plugin appended as the
// highest-priority entry, and compares the result to a BASELINE run of the
// same detectors WITHOUT it.
//
// Reuses the sibling tools' existing, already-verified detection code
// directly (SeamDetector.RunForResolvedPlugins / TextureLayerDetector.
// RunForResolvedPlugins / FloatingObjectFixer.RunDetectionForResolvedPlugins)
// - this is "wire it up," not "invent new detection logic." Road Mask Merger
// has no equivalent standalone detector (it's generative, not a detect/fix
// pair like the other three) so it isn't part of this pass; its own
// cross-cell continuity check already runs as part of its own generation.
//
// Why compare against a baseline instead of just running detection once
// against the merged result: a single post-merge run can't tell you whether
// a remaining flagged cell is (a) a PRE-EXISTING gap none of the 4 tools
// ever closed on their own, or (b) a NEW regression the merge itself
// introduced by picking the wrong field to combine. Appending PatchForeman
// as highest-priority means it wins every record it touches regardless of
// whether the original 4 tools' outputs stay active - so this reflects the
// real end state (PatchForeman replacing them) without needing to actually
// remove anything from the resolved list.

using SeamFinder.Core;

namespace PatchForeman;

public record ReverifySummary(
    int HeightSeamsBefore, int HeightSeamsAfter,
    int TextureMismatchesBefore, int TextureMismatchesAfter,
    int FloatingObjectsFlaggedBefore, int FloatingObjectsFlaggedAfter,
    // The AFTER-merge CSV lines (PatchForeman appended as highest priority) -
    // i.e. the residual state once the merged plugin is actually winning,
    // same report shape each sibling tool's own CLI already writes to its own
    // *Report.csv. Exposed here (not just used internally for LogNewRows) so
    // Program.cs can write them out the same way the 3 sibling tools do -
    // this pass already computes them, so withholding them just because
    // MergeEngine.cs itself doesn't need them would silently make PatchForeman
    // the only one of the 5 tools with no CSV output.
    List<string> HeightSeamReportCsvLines,
    List<string> TextureMismatchReportCsvLines,
    List<string> FloatingObjectReportCsvLines);

public static class ReverifyPass
{
    public static ReverifySummary Run(
        List<Mo2Resolver.ResolvedPlugin> originalLoadOrder,
        string patchForemanEspPath,
        Func<string, string?>? resolveAssetPath,
        Action<string> log,
        bool trustNorthernRoads,
        float floatingThresholdUnits,
        string? floatingWorldspaceFilter)
    {
        log("=== Re-verify pass, step 1/2: baseline (before PatchForeman) ===");
        var baselineHeight = SeamDetector.RunForResolvedPlugins(originalLoadOrder, log, trustNorthernRoads);
        var baselineTexture = TextureLayerDetector.RunForResolvedPlugins(originalLoadOrder, log);
        var baselineFloating = FloatingObjectFixer.RunDetectionForResolvedPlugins(
            originalLoadOrder, log, floatingThresholdUnits, floatingWorldspaceFilter, resolveAssetPath);

        log("");
        log("=== Re-verify pass, step 2/2: with PatchForeman appended as highest priority ===");
        var extended = new List<Mo2Resolver.ResolvedPlugin>(originalLoadOrder)
        {
            new("PatchForeman.esp", patchForemanEspPath),
        };
        var afterHeight = SeamDetector.RunForResolvedPlugins(extended, log, trustNorthernRoads);
        var afterTexture = TextureLayerDetector.RunForResolvedPlugins(extended, log);
        var afterFloating = FloatingObjectFixer.RunDetectionForResolvedPlugins(
            extended, log, floatingThresholdUnits, floatingWorldspaceFilter, resolveAssetPath);

        var summary = new ReverifySummary(
            baselineHeight.SeamCount, afterHeight.SeamCount,
            baselineTexture.MismatchCount, afterTexture.MismatchCount,
            baselineFloating.Flagged, afterFloating.Flagged,
            afterHeight.ReportCsvLines, afterTexture.ReportCsvLines, afterFloating.ReportCsvLines);

        log("");
        log("=== Re-verify summary ===");
        LogComparison(log, "Height seams", summary.HeightSeamsBefore, summary.HeightSeamsAfter);
        LogComparison(log, "Texture mismatches", summary.TextureMismatchesBefore, summary.TextureMismatchesAfter);
        LogComparison(log, "Floating objects flagged", summary.FloatingObjectsFlaggedBefore, summary.FloatingObjectsFlaggedAfter);

        // On a regression, name the actual new rows, not just the count - a
        // bare "+61" number gives no way to judge whether this is a real
        // problem or noise. Diffs the two runs' CSV report lines by their
        // stable cell-boundary identity (worldspace/edge/both cells' grid
        // coords - the first 6 CSV columns), independent of column order.
        if (summary.TextureMismatchesAfter > summary.TextureMismatchesBefore)
            LogNewRows(log, "texture mismatch", baselineTexture.ReportCsvLines, afterTexture.ReportCsvLines);
        if (summary.HeightSeamsAfter > summary.HeightSeamsBefore)
            LogNewRows(log, "height seam", baselineHeight.ReportCsvLines, afterHeight.ReportCsvLines);

        return summary;
    }

    static void LogNewRows(Action<string> log, string label, List<string> before, List<string> after)
    {
        // First line of each report is the CSV header - skip it, and key
        // each data row by the pure geometric boundary identity (columns
        // 0,1,2,3,5,6 = Worldspace,Edge,CellA_X,CellA_Y,CellB_X,CellB_Y -
        // both detectors share this exact column layout) rather than the
        // whole line. Deliberately excludes PluginA/PluginB (columns 4,7):
        // PatchForeman winning a cell that some other mod used to win is
        // an EXPECTED, harmless identity change, not a new problem - only a
        // genuinely new geometric boundary counts as a real regression here.
        static string Key(string csvLine)
        {
            var f = csvLine.Split(',');
            return string.Join(",", f[0], f[1], f[2], f[3], f[5], f[6]);
        }
        var beforeKeys = before.Skip(1).Select(Key).ToHashSet();
        var newRows = after.Skip(1).Where(line => !beforeKeys.Contains(Key(line))).ToList();

        log($"  New {label} row(s) introduced by the merge (up to 20 shown, {newRows.Count} total):");
        foreach (var row in newRows.Take(20))
            log($"    {row}");
    }

    static void LogComparison(Action<string> log, string label, int before, int after)
    {
        var verdict = after > before
            ? $" - REGRESSION: the merge introduced {after - before} new one(s) that weren't there before"
            : after < before
                ? $" (improved by {before - after} - likely just this run's own natural detection variance, not something PatchForeman intentionally fixes; it doesn't re-run the other tools' FIX passes, only their detectors)"
                : " (unchanged)";
        log($"{label}: {before} before -> {after} after{verdict}");
    }
}
