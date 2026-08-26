using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Workspace-wide catalog of materials authored by the tool. A material keeps its original
/// /Game/Mods package identity, but any suit can reference it and the packager copies that exact
/// cooked package into the consuming mod. Existing suit-local records are migrated on discovery.
/// </summary>
public sealed class ToolMaterialLibraryService
{
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

    public string ProjectRoot { get; }
    public string CatalogRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitMaterials");
    public string CatalogPath => Path.Combine(CatalogRoot, "material-library.json");
    public string ContentRoot => Path.Combine(CatalogRoot, "Content");

    public ToolMaterialLibraryService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public IReadOnlyList<GeneratedMaterialEntry> LoadAvailable()
    {
        var entries = LoadCatalog().Materials;
        var changed = MergeSavedSuitMaterials(entries);
        foreach (var entry in entries)
        {
            // Older projects kept their authored MIs only in the suit's persisted build stage.
            // Adopt those cooked files into the workspace library before the stage is rebuilt or
            // the source suit is removed, so "All tool materials" is genuinely project-wide.
            ArchiveMaterialClosure(entry.PackagePath);
        }
        var available = entries
            .Where(entry => HasCookedPackage(entry.PackagePath))
            .GroupBy(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => Clone(group.Last()))
            .OrderBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed)
        {
            Save(entries);
        }
        return available;
    }

    public void Register(IEnumerable<GeneratedMaterialEntry> materials)
    {
        var entries = LoadCatalog().Materials;
        var changed = false;
        foreach (var material in materials)
        {
            changed |= Upsert(entries, material);
            // Register is also called after an in-place edit. Refresh the archived bytes from the
            // newly cooked export instead of keeping an older complete library copy.
            ArchiveMaterialClosure(material.PackagePath, refreshFromSource: true);
        }
        if (changed)
        {
            Save(entries);
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

        var entries = LoadCatalog().Materials;
        entries.RemoveAll(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
            .Equals(oldPackage, StringComparison.OrdinalIgnoreCase));
        Upsert(entries, replacement);
        ArchiveMaterialClosure(replacement.PackagePath, refreshFromSource: true);
        DeleteArchivedPackageFiles(oldPackage);
        Save(entries);
    }

    public void Remove(string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var entries = LoadCatalog().Materials;
        if (entries.RemoveAll(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save(entries);
        }
        DeleteArchivedPackageFiles(package);
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
                UnrealPathUtil.NormalizePackagePath(mesh.MaterialPath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
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
        var packageBase = ResolvePackageBase(packagePath);
        return HasCompletePackageBase(packageBase);
    }

    public string? ResolvePackageUasset(string packagePath)
    {
        ArchivePackage(packagePath);
        var packageBase = ResolvePackageBase(packagePath);
        return HasCompletePackageBase(packageBase) ? packageBase + ".uasset" : null;
    }

    public IReadOnlyList<string> CopyPackageToContentRoot(string packagePath, string contentRoot)
    {
        var copied = new List<string>();
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return copied;
        }
        ArchivePackage(package);
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
        var copied = new List<string>();
        var closure = ResolveMaterialDependencyClosure(materialPackagePath, preferLiveSource: false);
        foreach (var package in closure)
        {
            var sourceBase = ResolvePackageBase(package);
            ValidateClosurePackageBase(sourceBase, package, "workspace material library");
            copied.AddRange(CopyPackageToContentRoot(package, contentRoot));

            var destinationBase = PackageBaseUnder(contentRoot, package);
            ValidateClosurePackageBase(destinationBase, package, "fresh packaging stage");
        }
        return copied;
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

    private void Save(List<GeneratedMaterialEntry> entries)
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

    private bool ArchiveMaterialClosure(string materialPackagePath, bool refreshFromSource = false)
    {
        // Registration and legacy migration remain best-effort so a temporarily locked texture
        // does not make the Materials tab unusable. Release staging resolves the same graph in
        // strict mode and refuses to package until every dependency is complete.
        var archivedRoot = ArchivePackage(materialPackagePath, refreshFromSource);
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
                complete &= ArchivePackage(dependency, refreshFromSource);
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
            return asset.Imports
                .Select(import => UnrealPathUtil.NormalizePackagePath(import.ObjectName.ToString()))
                .Where(dependency => dependency.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read the mod-local dependency graph for '{packagePath}'. " +
                "Re-cook or recreate this tool material before packaging.",
                ex);
        }
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

    private bool ArchivePackage(string packagePath, bool refreshFromSource = false)
    {
        try
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

            Directory.CreateDirectory(Path.GetDirectoryName(archiveBase)!);
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var source = sourceBase + extension;
                if (File.Exists(source))
                {
                    File.Copy(source, archiveBase + extension, overwrite: true);
                }
            }
            return HasCompletePackageBase(archiveBase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only archive must not make the Materials tab unusable. The entry
            // remains available from its original cooked source and can be adopted on a later load.
            return HasCookedPackage(packagePath);
        }
    }

    private string? ResolvePackageBase(string packagePath, bool includeArchive = true)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsSafeGamePackagePath(package))
        {
            return null;
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

        var projects = new SuitProjectService(ProjectRoot);
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

    private void DeleteArchivedPackageFiles(string packagePath)
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
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var path = packageBase + extension;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
