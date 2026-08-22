using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Isolates destructive release-time preparation from the certified authoring stages.
/// </summary>
public sealed partial class MainForm
{
    private sealed class PackagePreparationStage
    {
        public required string RootDirectory { get; init; }
        public required string ContentRoot { get; init; }
        public required string CleanupBoundary { get; init; }
    }

    private static NativeSuitProject CloneProjectForPackagePreparation(NativeSuitProject project) =>
        JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))
        ?? throw new InvalidOperationException("Could not snapshot the suit for isolated package preparation.");

    /// <summary>
    /// Captures one immutable, certified authoring stage into a unique disposable work tree.
    /// The rebuild gate covers validation plus the complete copy so a concurrent rebuild cannot
    /// produce a mixed-generation snapshot.
    /// </summary>
    private async Task<PackagePreparationStage> CreatePackagePreparationStageAsync(
        NativeSuitProject project,
        string projectRoot)
    {
        await RebuildGate.WaitAsync();
        try
        {
            var sourceContentRoot = CurrentPackageContentRoot(project);
            if (IsIncompleteDeclarativeGraftStage(project, sourceContentRoot))
            {
                throw new InvalidOperationException(
                    "The certified authoring stage is incomplete. Rebuild the suit's declarative stage before packaging.");
            }
            if (!Directory.Exists(sourceContentRoot))
            {
                throw new DirectoryNotFoundException(
                    "The certified authoring Content root does not exist: " + sourceContentRoot);
            }

            var projectOutput = new SuitProjectService(projectRoot).ProjectOutputDirectory(project);
            var preparationParent = Path.GetFullPath(Path.Combine(projectOutput, "PackagePreparation"));
            var preparationRoot = Path.GetFullPath(Path.Combine(
                preparationParent,
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")));
            EnsurePackagePreparationPath(preparationRoot, preparationParent);
            var contentRoot = Path.Combine(preparationRoot, "LEGOBatmanLotDK", "Content");

            try
            {
                await RunWithFileLockRetryAsync(
                    () =>
                    {
                        Directory.CreateDirectory(contentRoot);
                        CopyDirectoryContents(sourceContentRoot, contentRoot, overwrite: true);
                        return true;
                    },
                    "snapshot the certified authoring stage for package preparation");
            }
            catch
            {
                TryDeletePackagePreparationTree(preparationRoot, preparationParent);
                throw;
            }

            return new PackagePreparationStage
            {
                RootDirectory = preparationRoot,
                ContentRoot = contentRoot,
                CleanupBoundary = preparationParent,
            };
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    private async Task CleanupPackagePreparationStageAsync(PackagePreparationStage stage)
    {
        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    EnsurePackagePreparationPath(stage.RootDirectory, stage.CleanupBoundary);
                    if (Directory.Exists(stage.RootDirectory))
                    {
                        Directory.Delete(stage.RootDirectory, recursive: true);
                    }
                    return true;
                },
                "clean disposable package-preparation stage");
        }
        catch (Exception ex)
        {
            // A retained work copy has no completion marker and is never selected by authoring or
            // packaging root resolution, so cleanup failure is safe and recoverable.
            AppendLog(
                $"  warning: disposable package-preparation files stayed locked and were retained at {stage.RootDirectory}: {ex.Message}");
        }
    }

    private static void TryDeletePackagePreparationTree(string preparationRoot, string preparationParent)
    {
        try
        {
            EnsurePackagePreparationPath(preparationRoot, preparationParent);
            if (Directory.Exists(preparationRoot))
            {
                Directory.Delete(preparationRoot, recursive: true);
            }
        }
        catch
        {
            // The caller reports the original preparation failure. A partial work copy is inert:
            // it has no completion marker and no root selector points at PackagePreparation.
        }
    }

    private static void EnsurePackagePreparationPath(string preparationRoot, string preparationParent)
    {
        var parent = Path.GetFullPath(preparationParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(preparationRoot);
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to modify a package-preparation directory outside its generated project boundary.");
        }
    }
}
