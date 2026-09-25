namespace Batcomputer;

/// <summary>Claims a verified startup notification once per transaction, never on ordinary launches.</summary>
internal static class AppUpdateCompletion
{
    internal static Dialog.Model Presentation(string version, bool preview = false) => new()
    {
        WindowTitle = "Batcomputer — Update complete" + (preview ? " · Preview" : ""),
        Title = "You're up to date.",
        Subtitle = "Batcomputer v" + version + "  •  Ready for your next build",
        Severity = Dialog.Level.Good,
        Chips = new() { ("Files verified", Theme.Good), ("Startup confirmed", Theme.Good) },
        Message = "Your settings, projects and installed mods are right where you left them.",
        CalloutTitle = "A backup is ready if you need it",
        CalloutDetail = "Find your previous application files in Updates → Settings & recovery.",
        PrimaryText = "Back to workshop",
        // Preview construction has no update, health, settings or notification-claim side effects.
    };

    internal static string? Claim(string root, string token, string version)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return null;
        try
        {
            var transaction = Path.Combine(root, AppUpdateInstaller.WorkFolder, token);
            _ = AppUpdateInstaller.InstallRoot(transaction);
            if (!File.Exists(Path.Combine(transaction, "journal.json"))
                || !File.Exists(Path.Combine(transaction, "healthy.txt"))) return null;
            if (AppVersion.Compare(AppUpdateInstaller.ReadManifest(transaction).Version, version) != 0
                || AppVersion.Compare(File.ReadAllText(Path.Combine(transaction, "healthy.txt")).Trim(), version) != 0) return null;
            // CreateNew prevents repeated prompts if the same restart argument is replayed.
            using var claim = new FileStream(Path.Combine(transaction, "completion-shown.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(claim); writer.Write(version);
            return $"Update completed successfully.\n\nYou're now running Batcomputer v{version}.\n\nYour settings, projects and installed mods have been kept. Previous application files are available under Updates → Settings & recovery.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or FormatException)
        { return null; }
    }

    internal static void AfterShown(Form owner, string token)
    {
        // Mark healthy before a modal prompt: waiting for OK must not hold up installer health checks.
        AppUpdateInstaller.ConfirmStartup(token);
        owner.BeginInvoke(new Action(() =>
        {
            if (owner.IsDisposed || owner.Disposing) return;
            if (Claim(AppSettings.ToolRoot, token, AppVersion.Current) != null)
                Dialog.Show(owner, Presentation(AppVersion.Current));
        }));
    }
}
