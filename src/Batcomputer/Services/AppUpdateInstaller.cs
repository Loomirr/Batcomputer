using System.Diagnostics;
using System.Text.Json;

namespace Batcomputer;

internal sealed record UpdateBackup(UpdateFile NewFile, UpdateFile? OldFile, bool Remove = false);
internal sealed record UpdateJournal(string Version, List<UpdateBackup> Files);

/// <summary>Runs from a private copy of the current editor, after all instances release their app lock.</summary>
internal static class AppUpdateInstaller
{
    internal const string WorkFolder = ".batcomputer-updates";
    internal const string LockFile = ".batcomputer-update.lock";
    internal const string SandboxMarker = ".batcomputer-updater-sandbox";
    internal const string GitHubTestMarker = ".batcomputer-updater-github-test";

    internal static string NewTransaction(string installRoot)
    {
        UpdatePaths.RejectLinks(installRoot);
        var path = Path.Combine(installRoot, WorkFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Status(path, "Download staging created. Installed application files are unchanged.");
        return path;
    }

    internal static string InstallRoot(string transaction)
    {
        transaction = Path.GetFullPath(transaction).TrimEnd(Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(transaction);
        if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.Parent?.Name != WorkFolder || directory.Parent.Parent == null)
            throw new InvalidDataException("Invalid update transaction folder.");
        UpdatePaths.RejectLinks(transaction);
        return directory.Parent.Parent.FullName;
    }

    // Multiple app instances may read; the installer requires exclusive access after they all close.
    internal static FileStream AcquireAppLock(string root) => new(Path.Combine(root, LockFile),
        FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

    internal static string PrepareHelper(string transaction)
    {
        var root = InstallRoot(transaction);
        if (!Path.GetFullPath(AppSettings.ToolRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The updater can only schedule changes to this app's own folder.");
        var helper = Path.Combine(transaction, "helper");
        Directory.CreateDirectory(helper);
        // Debug/folder distributions need their managed dependencies. Single-file releases use the same path.
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("Batcomputer.exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name is "Batcomputer.deps.json" or "Batcomputer.runtimeconfig.json")
            {
                UpdatePaths.RejectLinks(file);
                File.Copy(file, Path.Combine(helper, name), overwrite: false);
            }
        }
        var runtimeRoot = Path.Combine(root, "runtimes");
        if (Directory.Exists(runtimeRoot)) CopyHelperTree(runtimeRoot, Path.Combine(helper, "runtimes"));
        var appRoot = Path.Combine(root, "app");
        if (Directory.Exists(appRoot)) CopyHelperTree(appRoot, Path.Combine(helper, "app"));
        var exe = Path.Combine(helper, "Batcomputer.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Cannot locate this portable app's executable.");
        return exe;
    }

    private static void CopyHelperTree(string source, string target)
    {
        UpdatePaths.RejectLinks(source);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            UpdatePaths.RejectLinks(file); File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var child in Directory.EnumerateDirectories(source)) CopyHelperTree(child, Path.Combine(target, Path.GetFileName(child)));
    }

    internal static void Schedule(StagedAppUpdate update)
    {
        var helper = PrepareHelper(update.Directory);
        var process = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add("--apply-app-update"); start.ArgumentList.Add(update.Directory);
        start.ArgumentList.Add(process.Id.ToString()); start.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString());
        using var helperProcess = Process.Start(start) ?? throw new IOException("Could not start the update helper. Batcomputer will stay open.");
    }

    internal static int RunHelper(string[] args)
    {
        var transaction = Path.GetFullPath(args[1]);
        try
        {
            var root = InstallRoot(transaction);
            var expectedHelper = Path.Combine(transaction, "helper", "Batcomputer.exe");
            if (!string.Equals(Environment.ProcessPath, expectedHelper, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run the recovery helper from its original update folder.");
            using var schedulingLock = new FileStream(Path.Combine(root, WorkFolder, "installer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Status(transaction, "Waiting for Batcomputer to close. No files have changed.");
            if (args[0] == "--apply-app-update" && args.Length >= 4)
            {
                try
                {
                    using var parent = Process.GetProcessById(int.Parse(args[2]));
                    if (parent.StartTime.ToUniversalTime().Ticks == long.Parse(args[3]) &&
                        string.Equals(parent.MainModule?.FileName, Path.Combine(root, "Batcomputer.exe"), StringComparison.OrdinalIgnoreCase)
                        && !parent.WaitForExit(600000))
                        throw new TimeoutException("Batcomputer stayed open for ten minutes. Check for updates again when ready.");
                }
                catch (ArgumentException) { /* Parent already exited. */ }
            }
            using var exclusive = WaitForAppLock(root);
            if (args[0] == "--rollback-app-update")
            {
                Rollback(transaction);
                Status(transaction, "Rolled back. Your settings and projects were not changed.");
                return 0;
            }
            Apply(transaction);
            // Do not hold the exclusive lock while the restarted editor tries to acquire its read lock.
            exclusive.Dispose();
            var start = new ProcessStartInfo(Path.Combine(root, "Batcomputer.exe")) { UseShellExecute = false, WorkingDirectory = root };
            start.ArgumentList.Add("--app-update-health"); start.ArgumentList.Add(Path.GetFileName(transaction));
            using var launched = Process.Start(start) ?? throw new IOException("Could not restart Batcomputer.");
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(transaction, "healthy.txt")) && !launched.HasExited)
                Thread.Sleep(250);
            if (File.Exists(Path.Combine(transaction, "healthy.txt")))
                Status(transaction, "Installed " + ReadManifest(transaction).Version + ". Startup confirmed. Backup retained.");
            else if (launched.HasExited)
            {
                using var rollbackLock = WaitForAppLock(root);
                Rollback(transaction);
                Status(transaction, "The new app exited before startup completed. Previous files restored; reopen Batcomputer.");
            }
            else Status(transaction, "Installed; startup has not been confirmed yet. The app was left running. Close it before using recovery if needed.");
            return 0;
        }
        catch (Exception ex)
        {
            // Apply handles transactional errors itself. A restart failure also needs restoration.
            try
            {
                if (File.Exists(Path.Combine(transaction, "journal.json")) && !File.Exists(Path.Combine(transaction, "healthy.txt")))
                {
                    using var recoveryLock = new FileStream(Path.Combine(InstallRoot(transaction), LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    Rollback(transaction);
                }
            }
            catch (Exception rollback) { ex = new AggregateException(ex, rollback); }
            try { Status(transaction, "Update did not complete: " + ex.Message + "\nUse the retained recovery helper after closing Batcomputer."); } catch { }
            return 1;
        }
    }

    private static FileStream WaitForAppLock(string root)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            try { return new FileStream(Path.Combine(root, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(500); }
        }
    }

    internal static UpdateManifest ReadManifest(string transaction)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(transaction, AppUpdateService.ManifestName)), AppUpdateService.Json)
            ?? throw new InvalidDataException("Missing update manifest.");
        AppUpdateService.ValidateManifest(manifest); return manifest;
    }

    internal static void Apply(string transaction, Action<int>? afterFile = null)
    {
        var root = InstallRoot(transaction);
        var manifest = ReadManifest(transaction);
        var installedVersion = AppUpdateService.ProductVersion(Path.Combine(root, "Batcomputer.exe"));
        var payloadVersion = AppUpdateService.ProductVersion(Path.Combine(transaction, "payload", "Batcomputer.exe"));
        if (installedVersion == null || payloadVersion == null || AppVersion.Compare(manifest.Version, installedVersion) <= 0
            || AppVersion.Compare(payloadVersion, manifest.Version) != 0)
            throw new InvalidDataException("The package must match its manifest and be newer than the installed executable.");
        AppUpdateService.ValidatePayloadVersion(Path.Combine(transaction, "payload"), manifest.Version);
        ApplyFiles(transaction, afterFile);
    }

    internal static void ApplyFiles(string transaction, Action<int>? afterFile = null)
    {
        var root = InstallRoot(transaction);
        var manifest = ReadManifest(transaction);
        if (File.Exists(Path.Combine(transaction, "journal.json"))) throw new IOException("Transaction already started. Recover it before retrying.");
        var payload = Path.Combine(transaction, "payload"); var backup = Path.Combine(transaction, "backup");
        long required = manifest.Files.Sum(f => f.Size);
        foreach (var file in manifest.Files)
        {
            AppUpdateService.VerifyFile(UpdatePaths.Resolve(payload, file.Path), file);
            var target = UpdatePaths.Resolve(root, file.Path);
            if (File.Exists(target)) required = checked(required + new FileInfo(target).Length);
        }
        AppUpdateService.CheckSpace(root, required);
        var records = new List<UpdateBackup>();
        foreach (var file in manifest.Files)
        {
            var target = UpdatePaths.Resolve(root, file.Path);
            // Leave exact matching files in place; they need neither replacement nor rollback.
            if (File.Exists(target) && new FileInfo(target).Length == file.Size
                && AppUpdateService.Hash(target).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            UpdateFile? old = null;
            if (File.Exists(target))
            {
                var saved = UpdatePaths.Resolve(backup, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(target, saved, false);
                using (var durableBackup = new FileStream(saved, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    durableBackup.Flush(flushToDisk: true);
                old = new(file.Path, new FileInfo(saved).Length, AppUpdateService.Hash(saved));
            }
            records.Add(new(file, old));
        }
        // Archive only recognized legacy app files during flat -> app/ migration.
        // Unknown or modified third-party DLLs are left alone, never swept by extension.
        if (manifest.Files.Any(f => f.Path.Equals("app/Batcomputer.dll", StringComparison.OrdinalIgnoreCase)))
        {
            var migrations = new List<UpdateFile>();
            foreach (var file in manifest.Files.Where(f => f.Path.StartsWith("app/", StringComparison.Ordinal)))
            {
                var legacy = file.Path[4..];
                if (manifest.Files.Any(f => f.Path.Equals(legacy, StringComparison.OrdinalIgnoreCase))) continue;
                var target = UpdatePaths.Resolve(root, legacy);
                if (!File.Exists(target)) continue;
                var old = new UpdateFile(legacy, new FileInfo(target).Length, AppUpdateService.Hash(target));
                var applicationFile = legacy is "Batcomputer.dll" or "Batcomputer.deps.json" or "Batcomputer.runtimeconfig.json";
                if (!applicationFile && !old.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                migrations.Add(old);
            }
            AppUpdateService.CheckSpace(root, required + migrations.Sum(f => f.Size));
            foreach (var old in migrations)
            {
                var saved = UpdatePaths.Resolve(backup, old.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(UpdatePaths.Resolve(root, old.Path), saved, false);
                using (var durableBackup = new FileStream(saved, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) durableBackup.Flush(true);
                AppUpdateService.VerifyFile(saved, old);
                records.Add(new(old, old, Remove: true));
            }
        }
        // A complete durable undo record precedes the first replacement. Recovery is idempotent.
        WriteDurable(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(new UpdateJournal(manifest.Version, records), AppUpdateService.Json));
        try
        {
            var count = 0;
            foreach (var record in records)
            {
                var file = record.NewFile;
                if (record.Remove)
                {
                    var target = UpdatePaths.Resolve(root, file.Path);
                    AppUpdateService.VerifyFile(target, file); // Refuse a last-moment user change.
                    File.Delete(target); // Original is retained in this transaction's verified backup.
                }
                else ReplaceFile(UpdatePaths.Resolve(payload, file.Path), UpdatePaths.Resolve(root, file.Path));
                afterFile?.Invoke(++count);
            }
            Status(transaction, "Application files installed. Waiting for startup confirmation.");
        }
        catch
        {
            Rollback(transaction); throw;
        }
    }

    internal static void Rollback(string transaction)
    {
        var root = InstallRoot(transaction);
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(transaction, "journal.json")), AppUpdateService.Json)
            ?? throw new InvalidDataException("Missing recovery journal.");
        AppUpdateService.ValidateManifest(new(1, journal.Version, journal.Files.Select(f => f.NewFile).ToList()), requireExecutable: false);
        // Validate *all* originals before making recovery changes, including files not yet replaced.
        foreach (var record in journal.Files)
        {
            if (record.OldFile is { } old)
            {
                if (old.Path != record.NewFile.Path) throw new InvalidDataException("Recovery paths differ.");
                AppUpdateService.VerifyFile(UpdatePaths.Resolve(Path.Combine(transaction, "backup"), old.Path), old);
            }
            var target = UpdatePaths.Resolve(root, record.NewFile.Path);
            if (File.Exists(target))
            {
                var hash = AppUpdateService.Hash(target);
                if (!hash.Equals(record.NewFile.Sha256, StringComparison.OrdinalIgnoreCase)
                    && !hash.Equals(record.OldFile?.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Recovery stopped to preserve a file changed since the update: " + record.NewFile.Path);
            }
        }
        foreach (var record in journal.Files.AsEnumerable().Reverse())
        {
            var target = UpdatePaths.Resolve(root, record.NewFile.Path);
            if (record.OldFile is { } old)
            {
                if (!File.Exists(target) || !AppUpdateService.Hash(target).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
                    ReplaceFile(UpdatePaths.Resolve(Path.Combine(transaction, "backup"), old.Path), target);
            }
            else if (File.Exists(target)) File.Delete(target); // Only a verified new app file recorded as absent before install.
        }
        Status(transaction, "Previous application files restored. Backup retained.");
    }

    private static void ReplaceFile(string source, string target)
    {
        UpdatePaths.RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(flushToDisk: true); }
            // Same-directory rename avoids ReplaceFile's ACL-merging permission requirements
            // on portable/OneDrive folders while keeping the destination switch atomic.
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDurable(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true);
    }
    private static void Status(string transaction, string text) => File.WriteAllText(Path.Combine(transaction, "status.txt"), text);

    internal static void ConfirmStartup(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return;
        var transaction = Path.Combine(AppSettings.ToolRoot, WorkFolder, token);
        try
        {
            _ = InstallRoot(transaction);
            if (AppVersion.Compare(ReadManifest(transaction).Version, AppVersion.Current) == 0)
                File.WriteAllText(Path.Combine(transaction, "healthy.txt"), AppVersion.Current);
        }
        catch { /* A missing transaction must not prevent ordinary startup. */ }
    }
}
