using System.Text.Json;

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

    public ToolMaterialLibraryService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public IReadOnlyList<GeneratedMaterialEntry> LoadAvailable()
    {
        var entries = LoadCatalog().Materials;
        var changed = MergeSavedSuitMaterials(entries);
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
        var entries = LoadCatalog().Materials;
        entries.RemoveAll(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
            .Equals(oldPackage, StringComparison.OrdinalIgnoreCase));
        Upsert(entries, replacement);
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
            if (ownsRecord || hasAssignment)
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
        var packageBase = ExportPackageBase(packagePath);
        return packageBase is not null &&
               File.Exists(packageBase + ".uasset") && new FileInfo(packageBase + ".uasset").Length > 0 &&
               File.Exists(packageBase + ".uexp") && new FileInfo(packageBase + ".uexp").Length > 0;
    }

    public IReadOnlyList<string> CopyPackageToContentRoot(string packagePath, string contentRoot)
    {
        var copied = new List<string>();
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var sourceBase = ExportPackageBase(package);
        if (sourceBase is null || !package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return copied;
        }
        var destinationBase = Path.Combine(
            contentRoot,
            package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationBase)!);
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var source = sourceBase + extension;
            if (!File.Exists(source))
            {
                continue;
            }
            var destination = destinationBase + extension;
            File.Copy(source, destination, overwrite: true);
            copied.Add(destination);
        }
        return copied;
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
        if (string.IsNullOrWhiteSpace(package))
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

    private static string? ExportPackageBase(string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.Combine(
            AppSettings.Current.EffectiveExportContentRoot(),
            package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
