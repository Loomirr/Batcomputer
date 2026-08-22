using System.Security.Cryptography;

namespace Batcomputer;

/// <summary>
/// Installs an IoStore trio as one recoverable destination transaction. All fresh files are
/// staged beside their final paths and every pre-install destination state is backed up before
/// the first replacement is attempted.
/// </summary>
internal sealed class TrioInstallTransactionService
{
    private const int OperationAttempts = 4;

    internal sealed record FileSpec(
        string SourcePath,
        string FileName,
        string Sha256,
        long Size);

    internal sealed record InstallPlanEntry(
        FileSpec File,
        string DestinationPath,
        string StagedPath,
        string BackupPath);

    internal sealed record Result(
        bool Success,
        bool DestinationConsistent,
        string Detail,
        IReadOnlyList<string> Warnings);

    private sealed class DestinationSnapshot(InstallPlanEntry plan)
    {
        public InstallPlanEntry Plan { get; } = plan;
        public bool HadOriginal { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public long OriginalSize { get; set; }
    }

    public Result Install(IReadOnlyList<FileSpec> files, string destinationDirectory) =>
        InstallCore(files, destinationDirectory, beforeCommit: null);

    /// <summary>Injects a deterministic failure before one commit step for rollback regression coverage.</summary>
    internal Result InstallForTest(
        IReadOnlyList<FileSpec> files,
        string destinationDirectory,
        int failBeforeCommitIndex) =>
        InstallCore(
            files,
            destinationDirectory,
            index =>
            {
                if (index == failBeforeCommitIndex)
                {
                    throw new IOException($"Injected install failure before commit {index}.");
                }
            });

    internal static IReadOnlyList<InstallPlanEntry> BuildPlanForTest(
        IReadOnlyList<FileSpec> files,
        string destinationDirectory,
        string transactionId) =>
        BuildPlan(files, destinationDirectory, transactionId);

    private static Result InstallCore(
        IReadOnlyList<FileSpec> files,
        string destinationDirectory,
        Action<int>? beforeCommit)
    {
        var warnings = new List<string>();
        IReadOnlyList<InstallPlanEntry> plan;
        try
        {
            plan = BuildPlan(files, destinationDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetFullPath(destinationDirectory));
        }
        catch (Exception ex)
        {
            return new Result(
                false,
                true,
                "Could not prepare the trio install transaction: " + ex.Message,
                warnings);
        }

        var snapshots = plan.Select(entry => new DestinationSnapshot(entry)).ToList();
        var commitStarted = false;
        var rollbackIncomplete = false;
        try
        {
            // A staging failure occurs before any game file is touched.
            foreach (var entry in plan)
            {
                VerifyFile(entry.File.SourcePath, entry.File.Sha256, entry.File.Size, "certified source");
                RunWithRetry(() => File.Copy(entry.File.SourcePath, entry.StagedPath, overwrite: true));
                VerifyFile(entry.StagedPath, entry.File.Sha256, entry.File.Size, "destination-side staged copy");
            }

            // Snapshot all three prior states before committing any replacement. An absent path is
            // also a state and is restored by deleting a newly installed file during rollback.
            foreach (var snapshot in snapshots)
            {
                var destinationPath = snapshot.Plan.DestinationPath;
                if (Directory.Exists(destinationPath))
                {
                    throw new IOException($"The trio destination is a directory: {destinationPath}");
                }

                snapshot.HadOriginal = File.Exists(destinationPath);
                if (!snapshot.HadOriginal)
                {
                    continue;
                }

                (snapshot.OriginalSha256, snapshot.OriginalSize) = FingerprintWithRetry(destinationPath);
                RunWithRetry(() => File.Copy(destinationPath, snapshot.Plan.BackupPath, overwrite: true));
                VerifyFile(
                    snapshot.Plan.BackupPath,
                    snapshot.OriginalSha256,
                    snapshot.OriginalSize,
                    "destination backup");
            }

            for (var index = 0; index < plan.Count; index++)
            {
                commitStarted = true;
                beforeCommit?.Invoke(index);
                var entry = plan[index];
                RunWithRetry(() => File.Move(entry.StagedPath, entry.DestinationPath, overwrite: true));
            }

            // Treat post-commit corruption as a failed transaction and restore the complete old state.
            foreach (var entry in plan)
            {
                VerifyFile(entry.DestinationPath, entry.File.Sha256, entry.File.Size, "installed trio file");
            }

            return new Result(
                true,
                true,
                "Installed the complete certified trio transactionally.",
                warnings);
        }
        catch (Exception ex)
        {
            var rollbackErrors = commitStarted
                ? RestoreSnapshots(snapshots)
                : new List<string>();
            rollbackIncomplete = rollbackErrors.Count > 0;
            var detail = "Trio install failed: " + ex.Message;
            if (commitStarted && rollbackErrors.Count == 0)
            {
                detail += " The previous destination trio was restored.";
            }
            else if (rollbackErrors.Count > 0)
            {
                detail += " Rollback was incomplete: " + string.Join(" | ", rollbackErrors);
            }

            return new Result(false, !rollbackIncomplete, detail, warnings);
        }
        finally
        {
            foreach (var entry in plan)
            {
                TryDelete(entry.StagedPath, warnings, "staged install file");
                if (!rollbackIncomplete)
                {
                    TryDelete(entry.BackupPath, warnings, "install backup");
                }
            }

            if (rollbackIncomplete)
            {
                var retained = plan
                    .Select(entry => entry.BackupPath)
                    .Where(File.Exists)
                    .ToList();
                if (retained.Count > 0)
                {
                    warnings.Add("Recovery backups were retained: " + string.Join(", ", retained));
                }
            }
        }
    }

    private static IReadOnlyList<InstallPlanEntry> BuildPlan(
        IReadOnlyList<FileSpec> files,
        string destinationDirectory,
        string transactionId)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count != 3)
        {
            throw new ArgumentException("An IoStore install transaction requires exactly three files.", nameof(files));
        }
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("The install destination is empty.", nameof(destinationDirectory));
        }
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            throw new ArgumentException("The install transaction ID is not filename-safe.", nameof(transactionId));
        }

        var requiredExtensions = new HashSet<string>([".pak", ".ucas", ".utoc"], StringComparer.OrdinalIgnoreCase);
        var names = files.Select(file => file.FileName).ToList();
        if (names.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            !requiredExtensions.SetEquals(names.Select(name => Path.GetExtension(name) ?? "")) ||
            names.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            throw new ArgumentException("The install set is not one matching .pak/.ucas/.utoc trio.", nameof(files));
        }
        if (files.Any(file => file.Size < 0 || string.IsNullOrWhiteSpace(file.Sha256)))
        {
            throw new ArgumentException("Every install file requires a certified size and SHA-256 hash.", nameof(files));
        }

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        return files.Select(file =>
        {
            var sourcePath = Path.GetFullPath(file.SourcePath);
            var destinationPath = Path.Combine(destinationRoot, file.FileName);
            var artifactPrefix = $".{file.FileName}.{transactionId}";
            return new InstallPlanEntry(
                file with { SourcePath = sourcePath },
                destinationPath,
                Path.Combine(destinationRoot, artifactPrefix + ".installing"),
                Path.Combine(destinationRoot, artifactPrefix + ".backup"));
        }).ToList();
    }

    private static List<string> RestoreSnapshots(IReadOnlyList<DestinationSnapshot> snapshots)
    {
        var errors = new List<string>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (snapshot.HadOriginal)
                {
                    if (FileMatches(
                            snapshot.Plan.DestinationPath,
                            snapshot.OriginalSha256,
                            snapshot.OriginalSize))
                    {
                        // This destination was not replaced before the failure (or was already
                        // restored), so do not needlessly overwrite a possibly locked old file.
                        continue;
                    }

                    if (!File.Exists(snapshot.Plan.BackupPath))
                    {
                        // A prior rollback attempt may already have moved the backup into place.
                        VerifyFile(
                            snapshot.Plan.DestinationPath,
                            snapshot.OriginalSha256,
                            snapshot.OriginalSize,
                            "restored destination");
                        continue;
                    }

                    RunWithRetry(() =>
                        File.Move(snapshot.Plan.BackupPath, snapshot.Plan.DestinationPath, overwrite: true));
                    VerifyFile(
                        snapshot.Plan.DestinationPath,
                        snapshot.OriginalSha256,
                        snapshot.OriginalSize,
                        "restored destination");
                }
                else
                {
                    RunWithRetry(() => File.Delete(snapshot.Plan.DestinationPath));
                    if (File.Exists(snapshot.Plan.DestinationPath))
                    {
                        throw new IOException("The newly created destination file still exists.");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(snapshot.Plan.DestinationPath)}: {ex.Message}");
            }
        }

        return errors;
    }

    private static bool FileMatches(string path, string expectedSha256, long expectedSize)
    {
        try
        {
            var (sha256, size) = FingerprintWithRetry(path);
            return size == expectedSize &&
                   sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyFile(string path, string expectedSha256, long expectedSize, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {label} is missing.", path);
        }

        var (sha256, size) = FingerprintWithRetry(path);
        if (size != expectedSize || !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The {label} does not match its certified hash and size: {Path.GetFileName(path)}");
        }
    }

    private static (string Sha256, long Size) FingerprintWithRetry(string path) =>
        RunWithRetry(() =>
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
        });

    private static void RunWithRetry(Action operation) =>
        RunWithRetry(() =>
        {
            operation();
            return true;
        });

    private static T RunWithRetry<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (
                attempt + 1 < OperationAttempts &&
                (ex is IOException || ex is UnauthorizedAccessException))
            {
                System.Threading.Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static void TryDelete(string path, List<string> warnings, string label)
    {
        try
        {
            RunWithRetry(() => File.Delete(path));
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not remove {label} '{path}': {ex.Message}");
        }
    }
}
