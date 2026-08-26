using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

/// <summary>
/// Workspace-wide catalog of materials authored by the tool. A material keeps its original
/// /Game/Mods package identity, but any suit can reference it and the packager copies that exact
/// cooked package into the consuming mod. Existing suit-local records are migrated on discovery.
/// </summary>
public sealed class ToolMaterialLibraryService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepairGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CookedPackageExtensions = [".uasset", ".uexp", ".ubulk"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CatalogFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GeneratedMaterialEntry> Materials { get; set; } = new();
    }

    internal sealed record RepairSnapshot(
        string Root,
        string CatalogCopy,
        bool CatalogExisted,
        IReadOnlyList<string> Packages);

    /// <summary>
    /// Owns one workspace-library repair until the consuming suit has also been rebuilt and
    /// saved. Disposing an uncommitted transaction restores both the catalog and every affected
    /// archived package, so a later suit-stage failure cannot leave a half-updated shared library.
    /// </summary>
    public sealed class MaterialLibraryRepairTransaction : IDisposable
    {
        private readonly ToolMaterialLibraryService _owner;
        private readonly SemaphoreSlim _gate;
        private readonly RepairSnapshot _snapshot;
        private readonly Dictionary<string, GeneratedMaterialEntry> _materialEntries;
        private bool _finished;

        internal MaterialLibraryRepairTransaction(
            ToolMaterialLibraryService owner,
            SemaphoreSlim gate,
            RepairSnapshot snapshot,
            IEnumerable<string> rootMaterialPackages,
            IEnumerable<string> closurePackages,
            IEnumerable<GeneratedMaterialEntry> materialEntries)
        {
            _owner = owner;
            _gate = gate;
            _snapshot = snapshot;
            RootMaterialPackages = rootMaterialPackages
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ClosurePackages = closurePackages
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(package => RootMaterialPackages.Contains(package, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(package => package, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _materialEntries = materialEntries
                .GroupBy(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Clone(group.Last()),
                    StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<string> RootMaterialPackages { get; }
        public IReadOnlyList<string> ClosurePackages { get; }

        /// <summary>
        /// Attaches the exact repaired catalog entry without running LoadAvailable. The latter is
        /// intentionally avoided while this transaction owns the library gate because it can
        /// discover and archive unrelated projects as a side effect.
        /// </summary>
        public bool ImportIntoProject(NativeSuitProject project, string packagePath)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            ArgumentNullException.ThrowIfNull(project);

            var package = UnrealPathUtil.NormalizePackagePath(packagePath);
            if (!_materialEntries.TryGetValue(package, out var entry) ||
                !RootMaterialPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            project.GeneratedMaterials ??= new List<GeneratedMaterialEntry>();
            project.GeneratedMaterials.RemoveAll(candidate =>
                UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
            project.GeneratedMaterials.Add(Clone(entry));
            return true;
        }

        /// <summary>
        /// Keeps the repaired archive and catalog. Call only after the consuming suit project and
        /// all of its generated stages have committed successfully.
        /// </summary>
        public void Commit()
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _finished = true;
            try
            {
                DeleteDirectoryBestEffort(_snapshot.Root);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                _owner.RestoreRepairSnapshotCore(_snapshot);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public string ProjectRoot { get; }
    public string CatalogRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitMaterials");
    public string CatalogPath => Path.Combine(CatalogRoot, "material-library.json");
    public string ContentRoot => Path.Combine(CatalogRoot, "Content");

    private SemaphoreSlim RepairGate => RepairGates.GetOrAdd(
        Path.GetFullPath(CatalogRoot),
        _ => new SemaphoreSlim(1, 1));

    public ToolMaterialLibraryService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public IReadOnlyList<GeneratedMaterialEntry> LoadAvailable()
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            return LoadAvailableCore();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads only the already-recorded material metadata. Browsers use this lightweight snapshot
    /// for compatibility labels; migration, package adoption, dependency repair, and availability
    /// checks remain explicit work performed by <see cref="LoadAvailable"/> and build flows.
    /// </summary>
    public IReadOnlyList<GeneratedMaterialEntry> LoadMetadataSnapshot()
    {
        var gate = RepairGate;
        // This is a best-effort browser hint, not a build prerequisite. Never freeze the UI behind
        // a material registration/repair transaction; the next view refresh can read its metadata.
        if (!gate.Wait(0))
        {
            return Array.Empty<GeneratedMaterialEntry>();
        }
        try
        {
            var entries = LoadCatalog().Materials;
            // Keep browser labels independent of navigation order. A disposable catalog can be
            // missing or stale, so merge the authoritative saved-suit metadata in memory without
            // archiving packages, checking dependency files, or writing a repaired catalog.
            MergeSavedSuitMaterials(entries);
            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(
                    UnrealPathUtil.NormalizePackagePath(entry.PackagePath)))
                .GroupBy(
                    entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => Clone(group.Last()))
                .OrderBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<GeneratedMaterialEntry> LoadAvailableCore()
    {
        var entries = LoadCatalog().Materials;
        var changed = MergeSavedSuitMaterials(entries);
        foreach (var entry in entries)
        {
            // Older projects kept their authored MIs only in the suit's persisted build stage.
            // Adopt those cooked files into the workspace library before the stage is rebuilt or
            // the source suit is removed, so "All tool materials" is genuinely project-wide.
            ArchiveMaterialClosureCore(entry.PackagePath);
        }
        var available = entries
            .Where(entry => HasCookedPackageCore(entry.PackagePath))
            .GroupBy(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => Clone(group.Last()))
            .OrderBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed)
        {
            SaveCore(entries);
        }
        return available;
    }

    public void Register(IEnumerable<GeneratedMaterialEntry> materials)
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            var entries = LoadCatalog().Materials;
            var changed = false;
            foreach (var material in materials)
            {
                changed |= Upsert(entries, material);
                // Register is also called after an in-place edit. Refresh the archived bytes from the
                // newly cooked export instead of keeping an older complete library copy.
                ArchiveMaterialClosureCore(material.PackagePath, refreshFromSource: true);
            }
            if (changed)
            {
                SaveCore(entries);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public bool ImportIntoProject(NativeSuitProject project, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var entry = LoadAvailable().FirstOrDefault(candidate =>
            UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        project.GeneratedMaterials ??= new List<GeneratedMaterialEntry>();
        project.GeneratedMaterials.RemoveAll(candidate =>
            UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        project.GeneratedMaterials.Add(Clone(entry));
        return true;
    }

    public void Rename(string oldPackagePath, GeneratedMaterialEntry replacement)
    {
        var oldPackage = UnrealPathUtil.NormalizePackagePath(oldPackagePath);
        var replacementPackage = UnrealPathUtil.NormalizePackagePath(replacement.PackagePath);
        if (!IsSafeGamePackagePath(replacementPackage))
        {
            throw new InvalidOperationException(
                $"Material package path must be a safe /Game/ path. Current value: '{replacement.PackagePath}'.");
        }

        var gate = RepairGate;
        gate.Wait();
        try
        {
            var entries = LoadCatalog().Materials;
            entries.RemoveAll(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(oldPackage, StringComparison.OrdinalIgnoreCase));
            Upsert(entries, replacement);
            ArchiveMaterialClosureCore(replacement.PackagePath, refreshFromSource: true);
            DeleteArchivedPackageFilesCore(oldPackage);
            SaveCore(entries);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Remove(string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var gate = RepairGate;
        gate.Wait();
        try
        {
            var entries = LoadCatalog().Materials;
            if (entries.RemoveAll(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveCore(entries);
            }
            DeleteArchivedPackageFilesCore(package);
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<string> FindReferencingSuits(string packagePath, string? exceptSlotId = null)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(package))
        {
            return Array.Empty<string>();
        }

        var references = new List<string>();
        var projects = new SuitProjectService(ProjectRoot);
        foreach (var summary in projects.ListProjectFiles())
        {
            NativeSuitProject? project;
            try { project = projects.LoadProject(summary.Path); }
            catch { continue; }
            if (project is null ||
                (!string.IsNullOrWhiteSpace(exceptSlotId) &&
                 project.SlotId.Equals(exceptSlotId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ownsRecord = (project.GeneratedMaterials ?? new List<GeneratedMaterialEntry>()).Any(material =>
                UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
            var hasAssignment = (project.MaterialAssignments ?? new List<SavedMaterialAssignment>()).Any(assignment =>
                UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
            var hasCustomMeshMaterial = (project.CustomStaticMeshes ?? new List<CustomStaticMeshImport>()).Any(mesh =>
                StaticMeshObjProbeService.EffectiveMaterialSlots(mesh)
                .Select(slot => slot.MaterialPath)
                .Any(materialPath => UnrealPathUtil.NormalizePackagePath(materialPath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase)));
            if (ownsRecord || hasAssignment || hasCustomMeshMaterial)
            {
                references.Add(string.IsNullOrWhiteSpace(project.DisplayName)
                    ? project.SlotId
                    : project.DisplayName);
            }
        }
        return references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool HasCookedPackage(string packagePath)
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            return HasCookedPackageCore(packagePath);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool HasCookedPackageCore(string packagePath) =>
        HasCompletePackageBase(ResolvePackageBase(packagePath));

    public string? ResolvePackageUasset(string packagePath)
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            ArchivePackageBestEffortCore(packagePath);
            var packageBase = ResolvePackageBase(packagePath);
            return HasCompletePackageBase(packageBase) ? packageBase + ".uasset" : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<string> CopyPackageToContentRoot(string packagePath, string contentRoot)
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            return CopyPackageToContentRootCore(packagePath, contentRoot);
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<string> CopyPackageToContentRootCore(string packagePath, string contentRoot)
    {
        var copied = new List<string>();
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return copied;
        }
        ArchivePackageBestEffortCore(package);
        var sourceBase = ResolvePackageBase(package);
        var destinationBase = PackageBaseUnder(contentRoot, package);
        if (sourceBase is null || destinationBase is null)
        {
            return copied;
        }

        // The packaging root is built from a freshly certified declarative stage. If that stage
        // already owns any member of this package, keep the entire package together and leave it
        // untouched rather than mixing archived sidecars with newly generated bytes.
        var extensions = new[] { ".uasset", ".uexp", ".ubulk" };
        if (extensions.Any(extension => File.Exists(destinationBase + extension)))
        {
            return copied;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationBase)!);
        foreach (var extension in extensions)
        {
            var source = sourceBase + extension;
            if (!File.Exists(source))
            {
                continue;
            }
            var destination = destinationBase + extension;
            File.Copy(source, destination, overwrite: false);
            copied.Add(destination);
        }
        return copied;
    }

    /// <summary>
    /// Stages one tool-created material plus every mod-local package it imports. This is the
    /// durable cross-suit path: base-game dependencies remain supplied by the game, while custom
    /// texture packages and mod-local material parents travel with the shared MI.
    /// </summary>
    public IReadOnlyList<string> CopyMaterialClosureToContentRoot(
        string materialPackagePath,
        string contentRoot)
    {
        var gate = RepairGate;
        gate.Wait();
        try
        {
            return CopyMaterialClosureToContentRootCore(materialPackagePath, contentRoot);
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<string> CopyMaterialClosureToContentRootCore(
        string materialPackagePath,
        string contentRoot)
    {
        var copied = new List<string>();
        var closure = ResolveMaterialDependencyClosure(materialPackagePath, preferLiveSource: false);
        foreach (var package in closure)
        {
            var sourceBase = ResolvePackageBase(package);
            ValidateClosurePackageBase(sourceBase, package, "workspace material library");
            copied.AddRange(CopyPackageToContentRootCore(package, contentRoot));

            var destinationBase = PackageBaseUnder(contentRoot, package);
            ValidateClosurePackageBase(destinationBase, package, "fresh packaging stage");
        }
        return copied;
    }

    /// <summary>
    /// Starts a workspace-library repair that remains reversible until the consuming suit's own
    /// project and generated stages have committed. The transaction owns the shared-library gate;
    /// attach repaired entries through its ImportIntoProject method rather than LoadAvailable.
    /// </summary>
    public MaterialLibraryRepairTransaction BeginRepairMaterialClosures(
        IEnumerable<string> materialPackagePaths)
    {
        ArgumentNullException.ThrowIfNull(materialPackagePaths);
        var roots = materialPackagePaths
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("Material repair requires at least one material package.");
        }
        foreach (var root in roots)
        {
            if (!IsSafeModPackagePath(root))
            {
                throw new InvalidOperationException(
                    $"Tool-created material repair roots must be safe /Game/Mods packages. Current value: '{root}'.");
            }
        }

        var gate = RepairGate;
        gate.Wait();
        RepairSnapshot? snapshot = null;
        try
        {
            var closure = roots
                .SelectMany(root => ResolveMaterialDependencyClosure(root, preferLiveSource: true))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(package => roots.Contains(package, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(package => package, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var catalog = LoadCatalog();
            MergeSavedSuitMaterials(catalog.Materials);
            var repairEntries = new List<GeneratedMaterialEntry>();
            foreach (var root in roots)
            {
                var entry = catalog.Materials.FirstOrDefault(candidate =>
                    UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                        .Equals(root, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    entry = new GeneratedMaterialEntry
                    {
                        DisplayName = UnrealPathUtil.AssetName(root),
                        Kind = "Material",
                        PackagePath = root,
                    };
                    Upsert(catalog.Materials, entry);
                }
                repairEntries.Add(Clone(entry));
            }

            snapshot = CreateRepairSnapshotCore(closure);
            foreach (var package in closure)
            {
                if (!ArchivePackageCore(package, refreshFromSource: true))
                {
                    throw new InvalidOperationException(
                        $"Material repair could not recover '{package}' from the current suit or workspace library.");
                }

                var archiveBase = PackageBaseUnder(ContentRoot, package);
                ValidateClosurePackageBase(archiveBase, package, "repaired workspace material library");
            }

            // Save catalog discovery and synthesized legacy entries inside the same snapshot.
            // A later suit-stage failure restores the original catalog byte-for-byte.
            SaveCore(catalog.Materials);
            return new MaterialLibraryRepairTransaction(
                this,
                gate,
                snapshot,
                roots,
                closure,
                repairEntries);
        }
        catch (Exception repairFailure)
        {
            Exception? rollbackFailure = null;
            if (snapshot is not null)
            {
                try
                {
                    RestoreRepairSnapshotCore(snapshot);
                }
                catch (Exception ex)
                {
                    rollbackFailure = ex;
                }
            }
            gate.Release();

            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Material repair failed and its workspace-library snapshot could not be fully restored. " +
                    $"The recovery snapshot was kept at '{snapshot!.Root}'.",
                    repairFailure,
                    rollbackFailure);
            }
            throw;
        }
    }

    /// <summary>
    /// Immediate compatibility wrapper. Workflows that rebuild a suit after repair should use
    /// BeginRepairMaterialClosures and commit only after the suit transaction succeeds.
    /// </summary>
    public IReadOnlyList<string> RepairMaterialClosure(string materialPackagePath)
    {
        using var repair = BeginRepairMaterialClosures([materialPackagePath]);
        var closure = repair.ClosurePackages.ToList();
        repair.Commit();
        return closure;
    }

    /// <summary>
    /// Deterministic, cycle-safe graph walk shared by the real cooked-package resolver and focused
    /// regressions. Only /Game/Mods dependencies belong in a mod package; unsafe paths fail closed
    /// instead of being normalized into an arbitrary filesystem destination.
    /// </summary>
    internal static IReadOnlyList<string> WalkModLocalMaterialDependencyClosure(
        string rootMaterialPackage,
        Func<string, IEnumerable<string>> directDependencies)
    {
        var root = UnrealPathUtil.NormalizePackagePath(rootMaterialPackage);
        if (!IsSafeModPackagePath(root))
        {
            throw new InvalidOperationException(
                $"Tool-created material dependency roots must be safe /Game/Mods packages. Current value: '{rootMaterialPackage}'.");
        }

        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var package = pending.Dequeue();
            if (!visited.Add(package))
            {
                continue;
            }

            foreach (var rawDependency in directDependencies(package) ?? Enumerable.Empty<string>())
            {
                var dependency = UnrealPathUtil.NormalizePackagePath(rawDependency);
                if (!dependency.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsSafeModPackagePath(dependency))
                {
                    throw new InvalidOperationException(
                        $"Material '{package}' contains an unsafe mod-local dependency: '{rawDependency}'.");
                }
                if (!visited.Contains(dependency))
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        return visited
            .OrderBy(package => package.Equals(root, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool ClosurePackageFilesAreCompleteForTest(string packageBase)
    {
        try
        {
            ValidateClosurePackageBase(packageBase, "/Game/Mods/Test/Fixture", "regression fixture");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Focused regression hook: snapshots an already prepared catalog/archive without requiring a
    /// parseable UAsset dependency graph. The caller may replace the listed archive packages and
    /// catalog, then Dispose to assert rollback or Commit to assert promotion persistence.
    /// </summary>
    internal MaterialLibraryRepairTransaction BeginRepairSnapshotForTest(
        IEnumerable<string> materialPackagePaths)
    {
        var packages = materialPackagePaths
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (packages.Count == 0 || packages.Any(package => !IsSafeModPackagePath(package)))
        {
            throw new InvalidOperationException(
                "The material repair regression snapshot requires safe /Game/Mods packages.");
        }

        var gate = RepairGate;
        gate.Wait();
        RepairSnapshot? snapshot = null;
        try
        {
            snapshot = CreateRepairSnapshotCore(packages);
            var entries = packages.Select(package => new GeneratedMaterialEntry
            {
                DisplayName = UnrealPathUtil.AssetName(package),
                Kind = "Material",
                PackagePath = package,
            }).ToList();
            return new MaterialLibraryRepairTransaction(
                this,
                gate,
                snapshot,
                packages,
                packages,
                entries);
        }
        catch
        {
            if (snapshot is not null)
            {
                DeleteDirectoryBestEffort(snapshot.Root);
            }
            gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Focused regression hook for the same per-package promotion used by real archive refreshes.
    /// Call while a BeginRepairSnapshotForTest transaction owns the fixture library.
    /// </summary>
    internal static void ReplacePackageFilesAtomicallyForTest(
        string sourceBase,
        string destinationBase) =>
        ReplacePackageFilesAtomically(sourceBase, destinationBase, requireCompleteSource: true);

    private CatalogFile LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return new CatalogFile();
        }
        try
        {
            return JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(CatalogPath), JsonOptions)
                   ?? new CatalogFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The authoritative cooked files and suit project records remain available for
            // migration; a damaged disposable catalog must not hide the author's materials.
            return new CatalogFile();
        }
    }

    private bool MergeSavedSuitMaterials(List<GeneratedMaterialEntry> entries)
    {
        var changed = false;
        var projects = new SuitProjectService(ProjectRoot);
        foreach (var summary in projects.ListProjectFiles())
        {
            NativeSuitProject? project;
            try { project = projects.LoadProject(summary.Path); }
            catch { continue; }
            foreach (var material in project?.GeneratedMaterials ?? Enumerable.Empty<GeneratedMaterialEntry>())
            {
                var package = UnrealPathUtil.NormalizePackagePath(material.PackagePath);
                var existing = entries.FirstOrDefault(entry =>
                    UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                        .Equals(package, StringComparison.OrdinalIgnoreCase));
                if (existing is null || PreferMigratedEntry(material, existing))
                {
                    changed |= Upsert(entries, material);
                }
            }

            // Material Forge projects from before the shared catalog stored the package only as
            // an assignment. Recover those records too; their cooked package is validated before
            // the entry is shown, so a normal base-game or missing MI cannot become a false tile.
            foreach (var assignment in project?.MaterialAssignments ?? Enumerable.Empty<SavedMaterialAssignment>())
            {
                var package = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath);
                if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) ||
                    entries.Any(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                        .Equals(package, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                changed |= Upsert(entries, new GeneratedMaterialEntry
                {
                    DisplayName = UnrealPathUtil.AssetName(package),
                    Kind = assignment.Component.Equals("Face", StringComparison.OrdinalIgnoreCase) ||
                           package.Contains("/Face", StringComparison.OrdinalIgnoreCase) ||
                           UnrealPathUtil.AssetName(package).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase)
                        ? "Face"
                        : "Material",
                    PackagePath = package,
                });
            }

            // Older custom-mesh projects could keep their material only on the import recipe.
            // Recover every authored OBJ section as well, including sections added after the
            // original single-material format, so All tool materials remains a workspace-wide
            // view even when the generated-material catalog needs rebuilding.
            foreach (var package in (project?.CustomStaticMeshes ?? Enumerable.Empty<CustomStaticMeshImport>())
                         .SelectMany(mesh => StaticMeshObjProbeService.EffectiveMaterialSlots(mesh)
                             .Select(slot => slot.MaterialPath))
                         .Select(UnrealPathUtil.NormalizePackagePath)
                         .Where(package => package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (entries.Any(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                changed |= Upsert(entries, new GeneratedMaterialEntry
                {
                    DisplayName = UnrealPathUtil.AssetName(package),
                    Kind = "Material",
                    PackagePath = package,
                });
            }
        }
        return changed;
    }

    internal static bool PreferMigratedEntry(GeneratedMaterialEntry candidate, GeneratedMaterialEntry existing)
    {
        var candidateHasTime = DateTimeOffset.TryParse(candidate.CreatedUtc, out var candidateTime);
        var existingHasTime = DateTimeOffset.TryParse(existing.CreatedUtc, out var existingTime);
        if (candidateHasTime && (!existingHasTime || candidateTime > existingTime))
        {
            return true;
        }
        if (candidateHasTime != existingHasTime || (candidateHasTime && candidateTime != existingTime))
        {
            return false;
        }

        static int MetadataScore(GeneratedMaterialEntry entry) =>
            (string.IsNullOrWhiteSpace(entry.DisplayName) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(entry.Kind) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(entry.SourceMaterialPackagePath) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(entry.ParentMaterialPath) ? 0 : 1) +
            (entry.CompatibleFaceMeshPackagePaths?.Count ?? 0) +
            (string.IsNullOrWhiteSpace(entry.TemplateRecipeId) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(entry.TemplateOutputRole) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(entry.TemplateGroupId) ? 0 : 1);
        return MetadataScore(candidate) > MetadataScore(existing);
    }

    private static bool Upsert(List<GeneratedMaterialEntry> entries, GeneratedMaterialEntry material)
    {
        var package = UnrealPathUtil.NormalizePackagePath(material.PackagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return false;
        }
        var clone = Clone(material);
        clone.PackagePath = package;
        clone.DisplayName = string.IsNullOrWhiteSpace(clone.DisplayName)
            ? UnrealPathUtil.AssetName(package)
            : clone.DisplayName;
        var index = entries.FindIndex(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
            .Equals(package, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var before = JsonSerializer.Serialize(entries[index], JsonOptions);
            var after = JsonSerializer.Serialize(clone, JsonOptions);
            if (before.Equals(after, StringComparison.Ordinal))
            {
                return false;
            }
            entries[index] = clone;
            return true;
        }
        entries.Add(clone);
        return true;
    }

    private void SaveCore(List<GeneratedMaterialEntry> entries)
    {
        Directory.CreateDirectory(CatalogRoot);
        var catalog = new CatalogFile
        {
            Materials = entries
                .GroupBy(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => Clone(group.Last()))
                .OrderBy(entry => entry.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        AtomicFileUtil.WriteAllText(CatalogPath, JsonSerializer.Serialize(catalog, JsonOptions));
    }

    private RepairSnapshot CreateRepairSnapshotCore(IReadOnlyList<string> packages)
    {
        var attemptsRoot = Path.Combine(
            AppSettings.GeneratedRootFor(ProjectRoot),
            "NativeSuitMaterialRepairAttempts");
        var snapshotRoot = Path.Combine(attemptsRoot, Guid.NewGuid().ToString("N"));
        var snapshotContentRoot = Path.Combine(snapshotRoot, "Content");
        var catalogCopy = Path.Combine(snapshotRoot, "material-library.original.json");
        try
        {
            Directory.CreateDirectory(snapshotRoot);
            var catalogExisted = File.Exists(CatalogPath);
            if (catalogExisted)
            {
                CopyFileDurably(CatalogPath, catalogCopy);
            }

            foreach (var package in packages)
            {
                var archiveBase = PackageBaseUnder(ContentRoot, package)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve archived material package '{package}'.");
                var snapshotBase = PackageBaseUnder(snapshotContentRoot, package)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve repair snapshot path for '{package}'.");
                foreach (var extension in CookedPackageExtensions)
                {
                    var source = archiveBase + extension;
                    if (!File.Exists(source))
                    {
                        continue;
                    }
                    CopyFileDurably(source, snapshotBase + extension);
                }
            }

            return new RepairSnapshot(
                snapshotRoot,
                catalogCopy,
                catalogExisted,
                packages.ToList());
        }
        catch
        {
            DeleteDirectoryBestEffort(snapshotRoot);
            throw;
        }
    }

    private void RestoreRepairSnapshotCore(RepairSnapshot snapshot)
    {
        var failures = new List<Exception>();
        var snapshotContentRoot = Path.Combine(snapshot.Root, "Content");
        foreach (var package in snapshot.Packages)
        {
            try
            {
                var snapshotBase = PackageBaseUnder(snapshotContentRoot, package)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve repair snapshot path for '{package}'.");
                var archiveBase = PackageBaseUnder(ContentRoot, package)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve archived material package '{package}'.");
                ReplacePackageFilesAtomically(
                    snapshotBase,
                    archiveBase,
                    requireCompleteSource: false);
            }
            catch (Exception ex)
            {
                failures.Add(new IOException(
                    $"Could not restore archived package '{package}' from the material-repair snapshot.",
                    ex));
            }
        }

        try
        {
            if (snapshot.CatalogExisted)
            {
                if (!File.Exists(snapshot.CatalogCopy))
                {
                    throw new FileNotFoundException(
                        "The material catalog snapshot is missing.",
                        snapshot.CatalogCopy);
                }
                AtomicReplaceFileFrom(snapshot.CatalogCopy, CatalogPath);
            }
            else if (File.Exists(CatalogPath))
            {
                File.Delete(CatalogPath);
            }
        }
        catch (Exception ex)
        {
            failures.Add(new IOException("Could not restore the workspace material catalog.", ex));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "The material-library repair could not be fully rolled back. " +
                $"The recovery snapshot was kept at '{snapshot.Root}'.",
                failures);
        }

        DeleteDirectoryBestEffort(snapshot.Root);
    }

    private static GeneratedMaterialEntry Clone(GeneratedMaterialEntry entry) => new()
    {
        DisplayName = entry.DisplayName,
        Kind = entry.Kind,
        PackagePath = entry.PackagePath,
        SourceMaterialPackagePath = entry.SourceMaterialPackagePath,
        ParentMaterialPath = entry.ParentMaterialPath,
        CompatibleFaceMeshPackagePaths = entry.CompatibleFaceMeshPackagePaths?.ToList() ?? new List<string>(),
        TemplateRecipeId = entry.TemplateRecipeId,
        TemplateOutputRole = entry.TemplateOutputRole,
        TemplateGroupId = entry.TemplateGroupId,
        CreatedUtc = entry.CreatedUtc,
    };

    private bool ArchiveMaterialClosureCore(string materialPackagePath, bool refreshFromSource = false)
    {
        // Registration and legacy migration remain best-effort so a temporarily locked texture
        // does not make the Materials tab unusable. Release staging resolves the same graph in
        // strict mode and refuses to package until every dependency is complete.
        var archivedRoot = ArchivePackageBestEffortCore(materialPackagePath, refreshFromSource);
        if (!archivedRoot)
        {
            return false;
        }
        try
        {
            var closure = ResolveMaterialDependencyClosure(materialPackagePath, refreshFromSource);
            var complete = true;
            foreach (var dependency in closure)
            {
                complete &= ArchivePackageBestEffortCore(dependency, refreshFromSource);
            }
            return complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return archivedRoot;
        }
    }

    private IReadOnlyList<string> ResolveMaterialDependencyClosure(
        string materialPackagePath,
        bool preferLiveSource)
    {
        return WalkModLocalMaterialDependencyClosure(
            materialPackagePath,
            package => DirectModLocalPackageDependencies(package, preferLiveSource));
    }

    private IReadOnlyList<string> DirectModLocalPackageDependencies(
        string packagePath,
        bool preferLiveSource)
    {
        var sourceBase = PreferredClosurePackageBase(packagePath, preferLiveSource);
        ValidateClosurePackageBase(sourceBase, packagePath, "workspace material source");
        try
        {
            var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
            var mappings = !string.IsNullOrWhiteSpace(mappingsPath) && File.Exists(mappingsPath)
                ? MappingsCache.Load(mappingsPath)
                : null;
            var asset = new UAsset(
                sourceBase + ".uasset",
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            return LiveModLocalPackageDependencies(asset);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read the mod-local dependency graph for '{packagePath}'. " +
                "Re-cook or recreate this tool material before packaging.",
                ex);
        }
    }

    /// <summary>
    /// UAsset import maps are append-only in normal Material Forge edits. Retargeting a texture
    /// parameter therefore leaves the old package/object imports behind even though no serialized
    /// property references them anymore. Packaging must follow the live export properties rather
    /// than treating every historical import-table row as a required dependency.
    /// </summary>
    private static IReadOnlyList<string> LiveModLocalPackageDependencies(UAsset asset)
    {
        var referencedImports = new HashSet<int>();
        var directPackagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            CollectLivePropertyReferences(export.Data, referencedImports, directPackagePaths);
            CollectImportReference(export.ClassIndex, referencedImports);
            CollectImportReference(export.SuperIndex, referencedImports);
            CollectImportReference(export.TemplateIndex, referencedImports);
            CollectImportReference(export.OuterIndex, referencedImports);

            // Preload/dependency arrays are intentionally excluded. Like the import table, they
            // can retain historical package indices after an in-place material parameter edit.
        }

        foreach (var importIndex in referencedImports)
        {
            var package = ImportedPackagePath(asset, importIndex);
            if (!string.IsNullOrWhiteSpace(package))
            {
                directPackagePaths.Add(package);
            }
        }

        return directPackagePaths
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectLivePropertyReferences(
        IEnumerable<PropertyData> properties,
        ISet<int> referencedImports,
        ISet<string> directPackagePaths)
    {
        foreach (var property in properties)
        {
            switch (property)
            {
                case ObjectPropertyData objectReference:
                    CollectImportReference(objectReference.Value, referencedImports);
                    break;

                case SoftObjectPropertyData softReference:
                    var softPackage = softReference.Value.AssetPath.PackageName.ToString();
                    if (!string.IsNullOrWhiteSpace(softPackage))
                    {
                        directPackagePaths.Add(softPackage);
                    }
                    break;

                // SoftObjectPath, SoftClassPath, legacy SoftAssetPath and string asset/class
                // references use the same serialized path shape but are not SoftObjectProperty.
                case SoftObjectPathPropertyData softPathReference:
                    var softPathPackage = softPathReference.Value.AssetPath.PackageName.ToString();
                    if (!string.IsNullOrWhiteSpace(softPathPackage))
                    {
                        directPackagePaths.Add(softPathPackage);
                    }
                    break;

                case DelegatePropertyData delegateReference:
                    if (delegateReference.Value is not null)
                    {
                        CollectImportReference(delegateReference.Value.Object, referencedImports);
                    }
                    break;

                case MulticastDelegatePropertyData multicastReference:
                    foreach (var item in multicastReference.Value ?? [])
                    {
                        if (item is not null)
                        {
                            CollectImportReference(item.Object, referencedImports);
                        }
                    }
                    break;

                case AssetObjectPropertyData assetReference:
                    var assetPackage = UnrealPathUtil.NormalizePackagePath(assetReference.Value?.ToString());
                    if (!string.IsNullOrWhiteSpace(assetPackage))
                    {
                        directPackagePaths.Add(assetPackage);
                    }
                    break;

                case StructPropertyData structure:
                    CollectLivePropertyReferences(structure.Value, referencedImports, directPackagePaths);
                    break;

                case MapPropertyData map:
                    CollectLivePropertyReferences(map.Value.Keys, referencedImports, directPackagePaths);
                    CollectLivePropertyReferences(map.Value.Values, referencedImports, directPackagePaths);
                    break;

                case ArrayPropertyData array:
                    CollectLivePropertyReferences(array.Value ?? Array.Empty<PropertyData>(), referencedImports, directPackagePaths);
                    break;
            }
        }
    }

    private static void CollectImportReference(FPackageIndex? packageIndex, ISet<int> referencedImports)
    {
        if (packageIndex is not null && packageIndex.IsImport())
        {
            referencedImports.Add(-packageIndex.Index - 1);
        }
    }

    private static string? ImportedPackagePath(UAsset asset, int importIndex)
    {
        var visited = new HashSet<int>();
        while (importIndex >= 0 && importIndex < asset.Imports.Count && visited.Add(importIndex))
        {
            var import = asset.Imports[importIndex];
            var objectName = import.ObjectName.ToString();
            if (objectName.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                return UnrealPathUtil.NormalizePackagePath(objectName);
            }

            if (!import.OuterIndex.IsImport())
            {
                return null;
            }
            importIndex = -import.OuterIndex.Index - 1;
        }
        return null;
    }

    internal static IReadOnlyList<string> ReachableImportPackagesForTest(
        IReadOnlyList<(string ObjectName, int OuterImportIndex)> imports,
        IEnumerable<int> referencedImportIndices)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var referencedIndex in referencedImportIndices)
        {
            var index = referencedIndex;
            var visited = new HashSet<int>();
            while (index >= 0 && index < imports.Count && visited.Add(index))
            {
                var import = imports[index];
                if (import.ObjectName.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                {
                    var package = UnrealPathUtil.NormalizePackagePath(import.ObjectName);
                    if (package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        packages.Add(package);
                    }
                    break;
                }
                index = import.OuterImportIndex;
            }
        }
        return packages.OrderBy(package => package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string? PreferredClosurePackageBase(string packagePath, bool preferLiveSource)
    {
        if (preferLiveSource)
        {
            var live = ResolvePackageBase(packagePath, includeArchive: false);
            if (HasCompletePackageBase(live))
            {
                return live;
            }
        }
        return ResolvePackageBase(packagePath);
    }

    private static void ValidateClosurePackageBase(
        string? packageBase,
        string packagePath,
        string sourceDescription)
    {
        if (string.IsNullOrWhiteSpace(packageBase))
        {
            throw new InvalidOperationException(
                $"Material dependency '{packagePath}' is missing from the {sourceDescription}.");
        }

        var missing = new List<string>();
        foreach (var extension in new[] { ".uasset", ".uexp" })
        {
            var path = packageBase + extension;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                missing.Add(extension);
            }
        }
        var bulkPath = packageBase + ".ubulk";
        if (File.Exists(bulkPath) && new FileInfo(bulkPath).Length == 0)
        {
            missing.Add(".ubulk (empty)");
        }
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Material dependency '{packagePath}' is incomplete in the {sourceDescription}: " +
                string.Join(", ", missing));
        }
    }

    private bool ArchivePackageBestEffortCore(string packagePath, bool refreshFromSource = false)
    {
        try
        {
            return ArchivePackageCore(packagePath, refreshFromSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only archive must not make the Materials tab unusable. The entry
            // remains available from its original cooked source and can be adopted on a later load.
            return HasCookedPackageCore(packagePath);
        }
    }

    private bool ArchivePackageCore(string packagePath, bool refreshFromSource = false)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return false;
        }

        var archiveBase = PackageBaseUnder(ContentRoot, package);
        if (archiveBase is null)
        {
            return false;
        }
        if (!refreshFromSource && HasCompletePackageBase(archiveBase))
        {
            return true;
        }

        var sourceBase = ResolvePackageBase(package, includeArchive: false);
        if (!HasCompletePackageBase(sourceBase))
        {
            return HasCompletePackageBase(archiveBase);
        }

        ValidateClosurePackageBase(sourceBase, package, "current workspace material source");
        ReplacePackageFilesAtomically(
            sourceBase!,
            archiveBase,
            requireCompleteSource: true);
        return HasCompletePackageBase(archiveBase);
    }

    /// <summary>
    /// Promotes all members of one cooked package as a unit. Every candidate is staged beside the
    /// destination first, and the complete previous trio is retained until validation succeeds.
    /// If any member cannot be promoted, the exact previous file set is restored before returning.
    /// </summary>
    private static void ReplacePackageFilesAtomically(
        string sourceBase,
        string destinationBase,
        bool requireCompleteSource)
    {
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationBase))
            ?? throw new InvalidOperationException(
                $"Could not resolve the archive directory for '{destinationBase}'.");
        Directory.CreateDirectory(destinationDirectory);

        var attemptRoot = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationBase)}.promote-{Guid.NewGuid():N}");
        var stagedBase = Path.Combine(attemptRoot, "next", "package");
        var previousBase = Path.Combine(attemptRoot, "previous", "package");
        Directory.CreateDirectory(attemptRoot);
        var keepAttemptForRecovery = false;
        try
        {
            foreach (var extension in CookedPackageExtensions)
            {
                var source = sourceBase + extension;
                if (File.Exists(source))
                {
                    CopyFileDurably(source, stagedBase + extension);
                }

                var previous = destinationBase + extension;
                if (File.Exists(previous))
                {
                    CopyFileDurably(previous, previousBase + extension);
                }
            }

            if (requireCompleteSource)
            {
                ValidateClosurePackageBase(
                    stagedBase,
                    "/Game/Mods/MaterialRepair/StagedPackage",
                    "atomic archive candidate");
            }

            try
            {
                foreach (var extension in CookedPackageExtensions)
                {
                    var staged = stagedBase + extension;
                    var destination = destinationBase + extension;
                    if (File.Exists(staged))
                    {
                        // The staging folder is a sibling of the destination package, keeping the
                        // final move on one volume so each individual member replacement is atomic.
                        File.Move(staged, destination, overwrite: true);
                    }
                    else if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }

                if (requireCompleteSource)
                {
                    ValidateClosurePackageBase(
                        destinationBase,
                        "/Game/Mods/MaterialRepair/ArchivedPackage",
                        "promoted workspace material library");
                }
            }
            catch (Exception promotionFailure)
            {
                try
                {
                    RestorePackageMembers(previousBase, destinationBase);
                }
                catch (Exception rollbackFailure)
                {
                    keepAttemptForRecovery = true;
                    throw new AggregateException(
                        $"Could not promote cooked package '{destinationBase}' or restore its previous files. " +
                        $"The package recovery files were kept at '{attemptRoot}'.",
                        promotionFailure,
                        rollbackFailure);
                }
                throw;
            }
        }
        finally
        {
            if (!keepAttemptForRecovery)
            {
                DeleteDirectoryBestEffort(attemptRoot);
            }
        }
    }

    private static void RestorePackageMembers(string snapshotBase, string destinationBase)
    {
        foreach (var extension in CookedPackageExtensions)
        {
            var snapshot = snapshotBase + extension;
            var destination = destinationBase + extension;
            if (File.Exists(snapshot))
            {
                AtomicReplaceFileFrom(snapshot, destination);
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static void AtomicReplaceFileFrom(string source, string destination)
    {
        var destinationPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Could not resolve the parent folder for '{destination}'.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            CopyFileDurably(source, temporary);
            File.Move(temporary, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { /* best-effort orphan cleanup */ }
            }
        }
    }

    private static void CopyFileDurably(string source, string destination)
    {
        var destinationPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Could not resolve the parent folder for '{destination}'.");
        Directory.CreateDirectory(directory);
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try { Directory.Delete(path, recursive: true); }
        catch { /* keep an orphaned recovery/attempt directory instead of masking the result */ }
    }

    private string? ResolvePackageBase(string packagePath, bool includeArchive = true)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return null;
        }

        var projects = new SuitProjectService(ProjectRoot);
        // Generated recipes are the authoritative current source for their texture packages.
        // Prefer them to the durable archive and ExportContent so a recook takes effect on the
        // very next material refresh/build instead of leaving stale bytes in the closure.
        var generatedTextureBase = ResolveGeneratedTexturePackageBase(package, projects);
        if (generatedTextureBase is not null)
        {
            return generatedTextureBase;
        }

        if (includeArchive)
        {
            var archiveBase = PackageBaseUnder(ContentRoot, package);
            if (HasCompletePackageBase(archiveBase))
            {
                return archiveBase;
            }
        }

        var exportBase = PackageBaseUnder(AppSettings.Current.EffectiveExportContentRoot(), package);
        if (HasCompletePackageBase(exportBase))
        {
            return exportBase;
        }

        foreach (var summary in projects.ListProjectFiles())
        {
            NativeSuitProject? project;
            try { project = projects.LoadProject(summary.Path); }
            catch { continue; }
            if (project is null)
            {
                continue;
            }

            foreach (var contentRoot in PersistedContentRoots(projects, project))
            {
                var candidate = PackageBaseUnder(contentRoot, package);
                if (HasCompletePackageBase(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private string? ResolveGeneratedTexturePackageBase(
        string package,
        SuitProjectService projects)
    {
        var candidates = new List<(string PackageBase, string Owner)>();
        var declaredOwners = new List<(string Owner, bool HasCompleteCook, string Reason)>();
        foreach (var summary in projects.ListProjectFiles()
                     .OrderBy(project => project.Path, StringComparer.OrdinalIgnoreCase))
        {
            NativeSuitProject? project;
            try { project = projects.LoadProject(summary.Path); }
            catch { continue; }
            if (project is null)
            {
                continue;
            }

            foreach (var texture in project.GeneratedTextures ?? Enumerable.Empty<GeneratedTextureEntry>())
            {
                if (!UnrealPathUtil.NormalizePackagePath(texture.PackagePath)
                        .Equals(package, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var owner = $"{summary.DisplayName} ({summary.Path})";
                var ownerHasCompleteCook = false;
                var rejectedCandidates = new List<string>();
                foreach (var contentRoot in GeneratedTextureContentRoots(texture))
                {
                    var candidate = PackageBaseUnder(contentRoot, package);
                    var validationReason = "the cooked package path is invalid";
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        MainForm.ValidateGeneratedTextureCook(texture, candidate, out validationReason))
                    {
                        ownerHasCompleteCook = true;
                        candidates.Add((Path.GetFullPath(candidate!), owner));
                    }
                    else if (!string.IsNullOrWhiteSpace(validationReason))
                    {
                        rejectedCandidates.Add(validationReason);
                    }
                }
                declaredOwners.Add((
                    owner,
                    ownerHasCompleteCook,
                    rejectedCandidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault() ??
                    "no trusted current cooked output was found"));
            }
        }

        var incompleteOwners = declaredOwners
            .Where(owner => !owner.HasCompleteCook)
            .Select(owner => $"{owner.Owner}: {owner.Reason}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(owner => owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (incompleteOwners.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generated texture package '{package}' has a saved recipe, but its current cooked output is incomplete. " +
                "Reimport that texture before packaging. Owners: " + string.Join("; ", incompleteOwners));
        }

        var distinct = candidates
            .GroupBy(candidate => candidate.PackageBase, StringComparer.OrdinalIgnoreCase)
            .Select(group => (PackageBase: group.Key,
                Owners: group.Select(candidate => candidate.Owner)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(owner => owner, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(candidate => candidate.PackageBase, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (declaredOwners.Count == 0)
        {
            return null;
        }
        if (distinct.Count == 0)
        {
            throw new InvalidOperationException(
                $"Generated texture package '{package}' has a saved recipe, but no complete current cook was found.");
        }

        var expectedFingerprint = PackageFingerprint(distinct[0].PackageBase);
        if (distinct.Skip(1).Any(candidate => !PackageFingerprint(candidate.PackageBase)
                .Equals(expectedFingerprint, StringComparison.Ordinal)))
        {
            var owners = distinct.SelectMany(candidate => candidate.Owners.Select(owner =>
                $"{owner}: {candidate.PackageBase}"));
            throw new InvalidOperationException(
                $"Generated texture package '{package}' is owned by multiple saved recipes with different cooked bytes. " +
                "Rename or remove the duplicate texture before packaging. Owners: " +
                string.Join("; ", owners));
        }

        return distinct[0].PackageBase;
    }

    private IEnumerable<string> GeneratedTextureContentRoots(GeneratedTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            yield break;
        }

        var importsRoot = Path.GetFullPath(Path.Combine(
            AppSettings.GeneratedRootFor(ProjectRoot),
            "TextureImports"));
        string? savedOutputRoot = null;
        try
        {
            var resolvedSavedRoot = Path.GetFullPath(Path.IsPathRooted(texture.OutputRoot)
                ? texture.OutputRoot
                : Path.Combine(ProjectRoot, texture.OutputRoot));
            // Saved project JSON is data, not authority to load cooked packages from arbitrary
            // directories. Only the current workspace's TextureImports tree is accepted directly.
            if (FileSystemPathUtil.IsWithinDirectory(resolvedSavedRoot, importsRoot))
            {
                savedOutputRoot = resolvedSavedRoot;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep looking for a portable rebased copy below. A malformed legacy path must not
            // prevent another saved suit from supplying the same generated package.
        }

        if (!string.IsNullOrWhiteSpace(savedOutputRoot))
        {
            yield return Path.Combine(savedOutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        }

        // OutputRoot was historically stored as an absolute cache path. When a workspace moves,
        // retain the portion below TextureImports and try it under this workspace's Generated
        // root. The package path is still checked exactly, and PackageBaseUnder keeps the final
        // candidate inside the computed content root.
        var portableOutputRoot = RebaseGeneratedTextureOutputRoot(texture.OutputRoot);
        if (!string.IsNullOrWhiteSpace(portableOutputRoot) &&
            !portableOutputRoot.Equals(savedOutputRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(portableOutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        }
    }

    private string? RebaseGeneratedTextureOutputRoot(string savedOutputRoot)
    {
        var normalized = savedOutputRoot.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = Path.DirectorySeparatorChar + "TextureImports" + Path.DirectorySeparatorChar;
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var relative = normalized[(markerIndex + marker.Length)..];
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            var importsRoot = Path.GetFullPath(Path.Combine(
                AppSettings.GeneratedRootFor(ProjectRoot),
                "TextureImports"));
            var rebased = Path.GetFullPath(Path.Combine(importsRoot, relative));
            return FileSystemPathUtil.IsWithinDirectory(rebased, importsRoot) ? rebased : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string PackageFingerprint(string packageBase)
    {
        var members = new List<string>();
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var path = packageBase + extension;
            if (!File.Exists(path))
            {
                members.Add(extension + ":missing");
                continue;
            }

            using var stream = File.OpenRead(path);
            members.Add($"{extension}:{stream.Length}:{Convert.ToHexString(SHA256.HashData(stream))}");
        }
        return string.Join("|", members);
    }

    private static IEnumerable<string> PersistedContentRoots(
        SuitProjectService projects,
        NativeSuitProject project)
    {
        var projectOutput = projects.ProjectOutputDirectory(project);
        yield return Path.Combine(projectOutput, "IoStore", "Stage", "LEGOBatmanLotDK", "Content");
        foreach (var stage in new[] { "GraftedPartStage", "GraftedTorso2Stage", "PatchedNameMapStage" })
        {
            yield return Path.Combine(projectOutput, stage, "LEGOBatmanLotDK", "Content");
        }
    }

    private static string? PackageBaseUnder(string contentRoot, string packagePath)
    {
        if (!IsSafeGamePackagePath(packagePath))
        {
            return null;
        }

        var root = Path.GetFullPath(contentRoot);
        var packageBase = Path.GetFullPath(Path.Combine(
            root,
            packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar)));
        return FileSystemPathUtil.IsWithinDirectory(packageBase, root) ? packageBase : null;
    }

    private static bool IsSafeGamePackagePath(string packagePath) =>
        packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) &&
        packagePath.Length > "/Game/".Length &&
        packagePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not ".." &&
                            segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static bool IsSafeModPackagePath(string packagePath) =>
        packagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) &&
        IsSafeGamePackagePath(packagePath);

    private static bool HasCompletePackageBase(string? packageBase) =>
        !string.IsNullOrWhiteSpace(packageBase) &&
        File.Exists(packageBase + ".uasset") && new FileInfo(packageBase + ".uasset").Length > 0 &&
        File.Exists(packageBase + ".uexp") && new FileInfo(packageBase + ".uexp").Length > 0;

    private void DeleteArchivedPackageFilesCore(string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return;
        }

        var packageBase = PackageBaseUnder(ContentRoot, package);
        if (packageBase is null)
        {
            return;
        }
        foreach (var extension in CookedPackageExtensions)
        {
            var path = packageBase + extension;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
