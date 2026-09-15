// Disables (unchecks) specific plugins in an MO2 profile's plugins.txt, for
// the "disable the 4 source tools after generating the merge" option.
//
// plugins.txt format (confirmed against a real profile 2026-09-14):
// a line starting with "*" is CHECKED/active ("*SomeMod.esp"); a bare line
// with no "*" is present but unchecked ("SomeMod.esp"); lines starting with
// "#" are comments MO2 writes itself ("# This file is used by Skyrim...").
// This ONLY ever removes a leading "*" from an already-checked target line -
// never adds one, never touches any other line, never renames/moves/deletes
// the file itself except for the one explicit backup copy made first.

using System.IO;

namespace PatchForeman.UI;

public record DisableResult(List<string> Disabled, List<string> AlreadyUnchecked, List<string> NotFound, string BackupPath);

public static class Mo2PluginsTxtEditor
{
    public static DisableResult DisableIfChecked(string instancePath, string profileName, IReadOnlyList<string> espNames)
    {
        var pluginsTxtPath = Path.Combine(instancePath, "profiles", profileName, "plugins.txt");
        if (!File.Exists(pluginsTxtPath))
            throw new FileNotFoundException("plugins.txt not found for this profile.", pluginsTxtPath);

        var backupPath = pluginsTxtPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.Copy(pluginsTxtPath, backupPath);

        var lines = File.ReadAllLines(pluginsTxtPath).ToList();
        var disabled = new List<string>();
        var alreadyUnchecked = new List<string>();
        var notFound = new List<string>();

        foreach (var espName in espNames)
        {
            if (string.IsNullOrWhiteSpace(espName)) continue;

            var checkedIndex = lines.FindIndex(l => l.Equals("*" + espName, StringComparison.OrdinalIgnoreCase));
            if (checkedIndex >= 0)
            {
                lines[checkedIndex] = espName;
                disabled.Add(espName);
                continue;
            }

            var uncheckedIndex = lines.FindIndex(l => l.Equals(espName, StringComparison.OrdinalIgnoreCase));
            if (uncheckedIndex >= 0)
                alreadyUnchecked.Add(espName);
            else
                notFound.Add(espName);
        }

        if (disabled.Count > 0)
            File.WriteAllLines(pluginsTxtPath, lines);
        else
            File.Delete(backupPath); // nothing changed - don't leave a pointless backup behind

        return new DisableResult(disabled, alreadyUnchecked, notFound, disabled.Count > 0 ? backupPath : "");
    }
}
