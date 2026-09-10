using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Batcomputer;

/// <summary>Commits the content trio and its loose metadata as one recoverable install.</summary>
internal sealed class ModInstallTransactionService
{
    internal sealed record Change(string? Source, string Destination);
    internal sealed record Result(bool Success, bool DestinationConsistent, string Detail, IReadOnlyList<string> Warnings);
    private sealed class Entry(Change change, string id)
    {
        public Change Change = change;
        public string Staged = change.Destination + "." + id + ".installing";
        public string Backup = change.Destination + "." + id + ".backup";
        public string Restore = change.Destination + "." + id + ".restoring";
        public string? Before;
        public string? After;
        public bool Committed;
    }
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal Result Install(string gameRoot, IReadOnlyList<Change> changes) => InstallCore(gameRoot, changes, null);
    internal Result InstallForTest(string gameRoot, IReadOnlyList<Change> changes, Action<int> beforeCommit) =>
        InstallCore(gameRoot, changes, beforeCommit);

    private static Result InstallCore(string gameRoot, IReadOnlyList<Change> changes, Action<int>? beforeCommit)
    {
        var root = Path.GetFullPath(gameRoot);
        lock (Gates.GetOrAdd(root, _ => new object()))
        {
            var warnings = new List<string>();
            var entries = new List<Entry>();
            bool rollbackIncomplete = false;
            try
            {
                if (changes.Count == 0) throw new InvalidDataException("The release has no install files.");
                var id = Guid.NewGuid().ToString("N");
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Resolve and validate the complete plan before writing anything.
                foreach (var change in changes)
                {
                    var destination = Path.GetFullPath(change.Destination);
                    RequireSafeDestination(root, destination);
                    if (!targets.Add(destination)) throw new InvalidDataException("Duplicate install destination: " + destination);
                    var source = change.Source is null ? null : Path.GetFullPath(change.Source);
                    if (source is not null && (!File.Exists(source) || new FileInfo(source).Length == 0))
                        throw new InvalidDataException("Required release file is missing or empty: " + source);
                    if (source is not null && source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Release source and destination must be different.");
                    entries.Add(new Entry(new Change(source, destination), id));
                }
                // Copy and verify all new files, then preserve every existing destination.
                foreach (var entry in entries)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Change.Destination)!);
                    if (entry.Change.Source is { } source)
                    {
                        entry.After = Hash(source);
                        File.Copy(source, entry.Staged, overwrite: false);
                        RequireHash(entry.Staged, entry.After);
                    }
                    if (File.Exists(entry.Change.Destination))
                    {
                        entry.Before = Hash(entry.Change.Destination);
                        File.Copy(entry.Change.Destination, entry.Backup, overwrite: false);
                        RequireHash(entry.Backup, entry.Before);
                    }
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    beforeCommit?.Invoke(i);
                    RequireSafeDestination(root, entry.Change.Destination);
                    if (!Matches(entry.Change.Destination, entry.Before))
                        throw new IOException("An install destination changed during preparation: " + entry.Change.Destination);
                    if (entry.Change.Source is null)
                        File.Delete(entry.Change.Destination);
                    else
                        File.Move(entry.Staged, entry.Change.Destination, overwrite: true);
                    entry.Committed = true;
                }
                foreach (var entry in entries) RequireHash(entry.Change.Destination, entry.After);
                return new Result(true, true, "The complete mod release was installed.", warnings);
            }
            catch (Exception ex)
            {
                var errors = new List<string>();
                foreach (var entry in entries.Where(e => e.Committed).Reverse())
                {
                    try
                    {
                        RequireSafeDestination(root, entry.Change.Destination);
                        if (entry.Before is null) File.Delete(entry.Change.Destination);
                        else
                        {
                            RequireHash(entry.Backup, entry.Before);
                            File.Copy(entry.Backup, entry.Restore, overwrite: false);
                            File.Move(entry.Restore, entry.Change.Destination, overwrite: true);
                        }
                        RequireHash(entry.Change.Destination, entry.Before);
                    }
                    catch (Exception restoreError) { errors.Add(entry.Change.Destination + ": " + restoreError.Message); }
                }
                rollbackIncomplete = errors.Count > 0;
                return new Result(false, !rollbackIncomplete,
                    "Mod installation failed: " + ex.Message + (rollbackIncomplete
                        ? " Recovery is incomplete. Do not start the game. " + string.Join(" | ", errors)
                        : " The previous installation was preserved. Close the game and retry installing; a rebuild is not required."), warnings);
            }
            finally
            {
                foreach (var entry in entries)
                {
                    Clean(entry.Staged, warnings);
                    Clean(entry.Restore, warnings);
                    if (!rollbackIncomplete) Clean(entry.Backup, warnings);
                    else if (File.Exists(entry.Backup)) warnings.Add("Recovery backup retained: " + entry.Backup + " -> " + entry.Change.Destination);
                }
            }
        }
    }

    private static void RequireSafeDestination(string root, string destination)
    {
        if (!FileSystemPathUtil.IsWithinDirectory(destination, root) || Directory.Exists(destination))
            throw new InvalidDataException("Invalid mod install destination: " + destination);
        // Do not follow an existing junction/symlink outside the checked game tree.
        for (string? path = destination; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Install destination contains a link or junction: " + path);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool Matches(string path, string? hash) => hash is null
        ? !File.Exists(path) && !Directory.Exists(path)
        : File.Exists(path) && Hash(path) == hash;
    private static void RequireHash(string path, string? hash)
    {
        if (!Matches(path, hash)) throw new IOException("Install file verification failed: " + path);
    }
    private static void Clean(string path, List<string> warnings)
    {
        try { File.Delete(path); }
        catch (Exception ex) { warnings.Add("Could not remove temporary install file '" + path + "': " + ex.Message); }
    }
}
