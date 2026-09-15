// Reads the OTHER 4 sibling tools' own %AppData%\<Tool>\settings.json files so
// PatchForeman can sanity-check its own esp-name text boxes against where
// each tool ACTUALLY last wrote its output, rather than trusting a hardcoded
// default silently. Read-only - never writes to another tool's settings.
//
// IMPORTANT LIMITATION, confirmed by reading all 4 sibling tools' own
// MainWindow.xaml.cs 2026-09-14: none of the 4 currently support a CUSTOM
// output ESP FILENAME at all - each hardcodes its own plugin name as a
// `const string pluginName = "..."` literal in code, and none of their
// PersistedSettings records has a field for it. So there is nothing to read
// back for "did the user rename the esp" in the literal sense - only
// OutputFolder is a real, persisted field all 4 share. This reader uses that:
// it checks whether a plugin matching PatchForeman's OWN configured esp-name
// box actually exists at each sibling's last-used output folder, and warns
// if not (most likely meaning that tool hasn't been run since, or the file
// really was renamed/moved by hand outside any of these tools' own UIs).

using System.IO;
using System.Text.Json;

namespace PatchForeman.UI;

public record SiblingCheckResult(string ToolName, string? OutputFolder, bool SettingsFound, bool? EspFoundAtOutputFolder);

public static class SiblingSettingsReader
{
    // (Display name, %AppData% subfolder name, default esp-name-box getter) -
    // the subfolder names are each tool's own SettingsFilePath constant,
    // confirmed directly from their source, not guessed.
    static readonly (string ToolName, string AppDataFolder)[] Tools =
    [
        ("Landscape Seam Fixer", "SeamFinder"),
        ("Road Mask Merger", "RoadMaskMerger"),
        ("Landscape Texture Fixer", "TextureSeamFixer"),
        ("Floating Object Fixer", "FloatingObjectFixer"),
    ];

    record MinimalSettings(string? OutputFolder);

    public static IReadOnlyList<SiblingCheckResult> CheckAll(IReadOnlyList<string> currentEspNames)
    {
        var results = new List<SiblingCheckResult>();
        for (int i = 0; i < Tools.Length; i++)
        {
            var (toolName, appDataFolder) = Tools[i];
            var espName = i < currentEspNames.Count ? currentEspNames[i] : null;
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appDataFolder, "settings.json");

            string? outputFolder = null;
            bool settingsFound = false;
            try
            {
                if (File.Exists(settingsPath))
                {
                    settingsFound = true;
                    var s = JsonSerializer.Deserialize<MinimalSettings>(File.ReadAllText(settingsPath));
                    outputFolder = s?.OutputFolder;
                }
            }
            catch
            {
                // Corrupt/unreadable sibling settings - report as "not found," not a crash.
            }

            bool? espFound = null;
            if (!string.IsNullOrEmpty(outputFolder) && !string.IsNullOrEmpty(espName))
            {
                try { espFound = File.Exists(Path.Combine(outputFolder, espName)); }
                catch { espFound = null; }
            }

            results.Add(new SiblingCheckResult(toolName, outputFolder, settingsFound, espFound));
        }
        return results;
    }
}
