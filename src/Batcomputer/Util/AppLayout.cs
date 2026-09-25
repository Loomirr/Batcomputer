namespace Batcomputer;

/// <summary>Application binaries may live in app/, but user state and tools stay beside the native launcher.</summary>
internal static class AppLayout
{
    internal static string ToolRoot
    {
        get
        {
            var binaries = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            var launcherRoot = Path.GetDirectoryName(Environment.ProcessPath);
            if (launcherRoot != null && string.Equals(Path.GetFileName(Environment.ProcessPath), "Batcomputer.exe", StringComparison.OrdinalIgnoreCase)
                && binaries.Equals(Path.Combine(launcherRoot, "app"), StringComparison.OrdinalIgnoreCase)) return launcherRoot;
            return binaries;
        }
    }
}
