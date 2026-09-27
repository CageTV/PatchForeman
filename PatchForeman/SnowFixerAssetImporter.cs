// Brings Snow Fixer's generated loose assets (meshes/, textures/,
// PBRNifPatcher/, ...) into PatchForeman's own output folder, so once
// SnowFixer.esp is folded into PatchForeman.esp the Snow Fixer output mod
// has nothing left that needs to stay enabled.
//
// Snow Fixer writes into an empty, dedicated output folder of the user's
// choosing (its Nexus page ships an empty "SnowFixer Output" mod for this;
// the path is persisted as OutputLocation in %AppData%\SnowFixer\settings.json).
// Everything in it is taken EXCEPT its plugin and the mod manager's meta.ini -
// the plugin's records are already merged, and meta.ini belongs to that mod.
//
// Stale-asset handling: every file this importer places is listed in a
// manifest inside PatchForeman's output. The next import that finds new
// assets first deletes exactly the files in that manifest (never anything
// else), so a mesh Snow Fixer stopped generating doesn't linger forever. An
// import that finds NO assets (already moved by a previous run, Snow Fixer
// not re-run since) keeps the previous set untouched.

namespace PatchForeman;

public record SnowFixerImportResult(int Imported, int PreviousRemoved, int Skipped, bool KeptPrevious, string ManifestPath);

public static class SnowFixerAssetImporter
{
    public const string ManifestFileName = "PatchForeman_SnowFixerAssets.txt";

    // Files PatchForeman writes into its own output folder - a Snow Fixer file
    // of the same name must never overwrite one of these.
    static readonly string[] OwnFiles =
    [
        "log.txt", "settings-used.json", "meta.ini", ManifestFileName,
        "PatchForeman_HeightSeamReport.csv", "PatchForeman_TextureMismatchReport.csv", "PatchForeman_FloatingObjectReport.csv",
    ];

    static readonly string[] PluginExtensions = [".esp", ".esm", ".esl"];

    // Reads Snow Fixer's own last-used output folder, for pre-filling the UI.
    public static string? ReadSnowFixerOutputLocation()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowFixer", "settings.json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("OutputLocation", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static SnowFixerImportResult Import(string snowFixerOutputFolder, string outputFolder, string outputPluginName, bool move, Action<string> log)
    {
        var source = Path.GetFullPath(snowFixerOutputFolder).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar);
        var manifestPath = Path.Combine(target, ManifestFileName);

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Snow Fixer output folder not found: {source}");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Snow Fixer output folder is the same as PatchForeman's output folder - pick Snow Fixer's own folder.");
        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Snow Fixer output folder and PatchForeman's output folder must not be inside each other.");

        var candidates = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f))
            .Where(rel => IsImportable(rel, outputPluginName))
            .ToList();

        if (candidates.Count == 0)
        {
            var kept = File.Exists(manifestPath) ? File.ReadAllLines(manifestPath).Count(l => l.Length > 0) : 0;
            log($"Snow Fixer assets: nothing new in {source} (already moved, or Snow Fixer hasn't been re-run). " +
                $"Keeping the {kept} file(s) imported last time.");
            return new SnowFixerImportResult(0, 0, 0, true, manifestPath);
        }

        // Remove the previous import - only paths it recorded, only inside target.
        int removed = 0;
        if (File.Exists(manifestPath))
        {
            foreach (var rel in File.ReadAllLines(manifestPath).Where(l => l.Length > 0))
            {
                var full = Path.GetFullPath(Path.Combine(target, rel));
                if (!full.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) { File.Delete(full); removed++; }
            }
            DeleteEmptyDirectories(target);
        }

        int imported = 0, skipped = 0;
        var manifest = new List<string>();
        foreach (var rel in candidates)
        {
            var from = Path.Combine(source, rel);
            var to = Path.Combine(target, rel);
            if (OwnFiles.Any(o => rel.Equals(o, StringComparison.OrdinalIgnoreCase)))
            {
                log($"  skipped {rel} (would overwrite PatchForeman's own file)");
                skipped++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (move) File.Move(from, to, overwrite: true);
            else File.Copy(from, to, overwrite: true);
            manifest.Add(rel);
            imported++;
        }

        if (move) DeleteEmptyDirectories(source);
        File.WriteAllLines(manifestPath, manifest);

        log($"Snow Fixer assets: {(move ? "moved" : "copied")} {imported} file(s) from {source} into {target}" +
            (removed > 0 ? $" (replaced {removed} file(s) from the previous import)" : "") + ".");
        return new SnowFixerImportResult(imported, removed, skipped, false, manifestPath);
    }

    // Only files that belong in a game Data folder: not Snow Fixer's (or any)
    // plugin, not the mod manager's meta.ini, and not PatchForeman's own plugin.
    static bool IsImportable(string relativePath, string outputPluginName)
    {
        var name = Path.GetFileName(relativePath);
        var atRoot = !relativePath.Contains(Path.DirectorySeparatorChar);
        if (atRoot && name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) return false;
        if (atRoot && PluginExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        if (name.Equals(outputPluginName, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static void DeleteEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }
}
