using System.ComponentModel;
using System.Diagnostics;

namespace Batcomputer;

internal static class WinFormsDesignerSupport
{
    public static bool IsInDesigner()
    {
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
        {
            return true;
        }

        try
        {
            var processName = Process.GetCurrentProcess().ProcessName;
            return processName.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("DesignToolsServer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
