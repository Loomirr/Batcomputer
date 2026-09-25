namespace Batcomputer;

internal sealed record AppUpdateActivity(string Text, bool StartupConfirmed, string? Transaction = null, bool ScheduleFailed = false)
{
    internal static AppUpdateActivity ReadLatest(string root)
    {
        try
        {
            var directory = Path.Combine(root, AppUpdateInstaller.WorkFolder);
            var latest = Directory.Exists(directory) ? Directory.EnumerateDirectories(directory)
                .Where(d => Guid.TryParseExact(Path.GetFileName(d), "N", out _))
                .Select(d => Path.Combine(d, "status.txt")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (latest == null) return new("No update activity yet.\r\n\r\nDownload and installation details will appear here automatically.", false);
            var text = File.ReadAllText(latest);
            var healthy = Path.Combine(Path.GetDirectoryName(latest)!, "healthy.txt");
            // Rollback retains the historical health marker; don't report that as a completed update.
            var confirmed = File.Exists(healthy) && text.StartsWith("Installed ", StringComparison.Ordinal)
                && AppVersion.Compare(File.ReadAllText(healthy).Trim(), AppVersion.Current) == 0;
            return new(text + "\r\n\r\nUpdated " + File.GetLastWriteTime(latest).ToString("g") + "\r\nBackup details are available through Backups & recovery.", confirmed,
                Path.GetDirectoryName(latest), text.StartsWith("Update did not complete:", StringComparison.Ordinal));
        }
        catch (Exception ex) { return new("Update activity could not be read yet. It will refresh automatically.\r\n" + ex.Message, false); }
    }
}

internal static class AppUpdateTestEnvironment
{
    internal static Uri? Feed
    {
        get
        {
            var marker = Path.Combine(AppSettings.ToolRoot, AppUpdateInstaller.SandboxMarker);
            if (!File.Exists(marker)) return null;
            var uri = new Uri(File.ReadAllText(marker).Trim());
            if (uri.Scheme != "http" || uri.Host != "127.0.0.1") throw new InvalidDataException("Invalid local updater test feed.");
            return uri;
        }
    }
    internal static bool FullApp => Feed != null && File.Exists(Path.Combine(AppSettings.ToolRoot, ".batcomputer-updater-full-app"));
}
