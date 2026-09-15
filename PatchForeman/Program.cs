// PatchForeman CLI - the "5th app" that merges the 4 sibling fixer tools'
// separately-generated output ESPs into one final plugin. See MergeEngine.cs
// for the merge design, ReverifyPass.cs for the post-merge re-verification,
// and NOTES.md for what's built vs. not yet.
//
// Usage:
//   PatchForeman.exe --mo2 <instancePath> <profileName> [gameDataPath]
//       [--output="PatchForeman.esp"] [--write] [--skip-reverify]
//       [--seam-fixer-esp="LandscapeSeamFixes.esp"]
//       [--road-mask-esp="RoadMaskMerge.esp"]
//       [--texture-fixer-esp="LandscapeTextureFixes.esp"]
//       [--floating-fixer-esp="FloatingObjectFixes.esp"]
//       [--trust-northern-roads] [--floating-threshold=96] [--floating-worldspace="Tamriel"]
//
// Defaults to --dry-run - this is a brand-new, not-yet-in-game-tested tool.
// Pass --write explicitly to actually produce the merged plugin on disk.
// The re-verify pass (re-running Landscape Seam Fixer's, Landscape Texture
// Fixer's, and Floating Object Fixer's own detectors against the merged
// result, compared to a baseline without it) only runs after a real --write
// - there's no merged file to layer into a load order otherwise. Skip it
// with --skip-reverify if you just want the merge, faster.

using PatchForeman;
using SeamFinder.Core;

if (args.Length == 0 || args[0] != "--mo2")
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  PatchForeman.exe --mo2 <instancePath> <profileName> [gameDataPath]");
    Console.WriteLine("      [--output=\"PatchForeman.esp\"] [--write] [--skip-reverify]");
    Console.WriteLine("      [--seam-fixer-esp=\"LandscapeSeamFixes.esp\"]");
    Console.WriteLine("      [--road-mask-esp=\"RoadMaskMerge.esp\"]");
    Console.WriteLine("      [--texture-fixer-esp=\"LandscapeTextureFixes.esp\"]");
    Console.WriteLine("      [--floating-fixer-esp=\"FloatingObjectFixes.esp\"]");
    Console.WriteLine("      [--trust-northern-roads] [--floating-threshold=96] [--floating-worldspace=\"Tamriel\"]");
    Console.WriteLine("  (defaults to a DRY RUN - logs what would be merged, writes nothing;");
    Console.WriteLine("   pass --write to actually produce the merged plugin and re-verify it)");
    Pause();
    return;
}

if (args.Length < 3)
{
    Console.WriteLine("Usage: PatchForeman.exe --mo2 <instancePath> <profileName> [gameDataPath] [flags]");
    Pause();
    return;
}

var instancePath = args[1];
var profileName = args[2];
var gameDataPath = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : ReadGamePathFromIni(instancePath);
var outputName = StringArg(args, "--output=") ?? "PatchForeman.esp";
var dryRun = !args.Contains("--write");
var skipReverify = args.Contains("--skip-reverify");
var trustNorthernRoads = args.Contains("--trust-northern-roads");
var floatingThreshold = float.TryParse(StringArg(args, "--floating-threshold="), out var ft) ? ft : 96f;
var floatingWorldspace = StringArg(args, "--floating-worldspace=") ?? "Tamriel";
var sources = new SourceToolPlugins(
    LandscapeSeamFixer: StringArg(args, "--seam-fixer-esp=") ?? "LandscapeSeamFixes.esp",
    RoadMaskMerger: StringArg(args, "--road-mask-esp=") ?? "RoadMaskMerge.esp",
    LandscapeTextureFixer: StringArg(args, "--texture-fixer-esp=") ?? "LandscapeTextureFixes.esp",
    FloatingObjectFixer: StringArg(args, "--floating-fixer-esp=") ?? "FloatingObjectFixes.esp");

Console.WriteLine($"MO2 instance: {instancePath}");
Console.WriteLine($"Profile: {profileName}");
Console.WriteLine($"Game Data path: {gameDataPath}");
Console.WriteLine($"Output plugin: {outputName}");
Console.WriteLine($"Mode: {(dryRun ? "DRY RUN (pass --write to actually produce output)" : "WRITE")}");
Console.WriteLine($"Source plugins: SeamFixer={sources.LandscapeSeamFixer}, RoadMask={sources.RoadMaskMerger}, " +
    $"TextureFixer={sources.LandscapeTextureFixer}, FloatingFixer={sources.FloatingObjectFixer}");
Console.WriteLine();

try
{
    var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
    Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
    if (resolved.MissingPlugins.Count > 0)
    {
        Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
        foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
    }

    var result = MergeEngine.RunForResolvedPlugins(
        resolved.LoadOrder, sources, outputName, AppContext.BaseDirectory, Console.WriteLine, dryRun);

    Console.WriteLine();
    Console.WriteLine($"Cells from a single source tool: {result.Stats.CellsFromOneSource}");
    Console.WriteLine($"Cells merged across multiple tools: {result.Stats.CellsHeightAndTextureMerged}");
    Console.WriteLine($"Cells where source tools disagreed on Water fields (used the first found): {result.Stats.CellsWaterFieldsDisagreed}");
    Console.WriteLine($"Placed-reference overrides forwarded from Floating Object Fixer: {result.Stats.PlacedRefsForwarded}");
    if (!result.DryRun)
        Console.WriteLine($"Output: {result.OutputPath}");

    if (!result.DryRun && !skipReverify)
    {
        Console.WriteLine();
        var reverify = ReverifyPass.Run(
            resolved.LoadOrder, result.OutputPath, resolved.ResolveDataFile, Console.WriteLine,
            trustNorthernRoads, floatingThreshold, floatingWorldspace);
    }
    else if (result.DryRun && !skipReverify)
    {
        Console.WriteLine();
        Console.WriteLine("(Skipping re-verify pass - it needs a real written plugin to layer into a load order; re-run with --write to also re-verify.)");
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("ERROR: " + ex);
}

Pause();

static string? StringArg(string[] a, string prefix)
{
    var arg = a.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return arg?[prefix.Length..].Trim('"');
}

static void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(); } catch (InvalidOperationException) { }
}

static string ReadGamePathFromIni(string instancePath)
{
    var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
    if (!File.Exists(iniPath))
        throw new FileNotFoundException("No gameDataPath given and ModOrganizer.ini not found to read it from.", iniPath);

    foreach (var line in File.ReadAllLines(iniPath))
    {
        if (!line.StartsWith("gamePath=")) continue;
        var value = line["gamePath=".Length..].Trim();
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
            value = value[(start + 1)..end];
        value = value.Replace("\\\\", "\\");
        return Path.Combine(value, "Data");
    }

    throw new InvalidOperationException("Could not find gamePath= in ModOrganizer.ini - pass gameDataPath explicitly instead.");
}
