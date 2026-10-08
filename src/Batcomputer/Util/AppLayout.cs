namespace Batcomputer;

/// <summary>Application binaries may live in app/, but user state and tools stay beside the native launcher.</summary>
internal static class AppLayout
{
    internal static string ToolRoot
    {
        get
        {
            var binaries = Path.TrimEndingDirectorySeparator(NormalizePath(AppContext.BaseDirectory));
            var launcherRoot = Environment.ProcessPath is { } processPath ? Path.GetDirectoryName(NormalizePath(processPath)) : null;
            if (launcherRoot != null && string.Equals(Path.GetFileName(Environment.ProcessPath), "Batcomputer.exe", StringComparison.OrdinalIgnoreCase)
                && binaries.Equals(Path.Combine(launcherRoot, "app"), StringComparison.OrdinalIgnoreCase)) return launcherRoot;
            return binaries;
        }
    }

    // An extended-length launch path keeps the CLR's dependency loader working beyond MAX_PATH.
    // Compare/store ordinary absolute paths so both spellings still resolve to the same portable root.
    internal static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':') path = path[4..];
        return Path.GetFullPath(path);
    }

    internal static string LaunchPath(string path)
    {
        var full = NormalizePath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
