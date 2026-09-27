// Tells whether PatchForeman was launched from MO2's executables list, and
// whether MO2 is open at all - which decides whether a plugins.txt edit
// sticks (see DisableMergedSourceTools).
//
// A program MO2 launches gets MO2's virtual filesystem DLL (usvfs_x64.dll)
// injected into it before it starts; nothing else loads that DLL.

using System.Diagnostics;
using System.IO;

namespace PatchForeman.UI;

public static class Mo2Session
{
    public static bool IsRunningUnderMo2()
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var name = Path.GetFileName(module.FileName);
                if (name.Equals("usvfs_x64.dll", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // Module enumeration can fail on locked-down systems - treat as "not under MO2".
        }
        return false;
    }

    public static bool IsMo2Open()
    {
        try { return Process.GetProcessesByName("ModOrganizer").Length > 0; }
        catch { return false; }
    }
}
