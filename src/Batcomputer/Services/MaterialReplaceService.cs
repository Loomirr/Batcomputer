using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

/// <summary>
/// Replace-mode: set OverrideMaterials[slot] on an EXISTING component (e.g.
/// CharacterMesh0, Head) in the already-staged playable/cutscene assets, pointing
/// it at a generated MI. Preserves other material slots (pads with mesh-default
/// nulls). Edits the current packageable stage in place, so the existing packager
/// picks it up.
/// </summary>
public sealed class MaterialReplaceService
{
    public string ProjectRoot { get; }

    public MaterialReplaceService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public sealed class Assignment
    {
        public string Component { get; set; } = "CharacterMesh0";
        public int Slot { get; set; }
        public string MiPackagePath { get; set; } = "";
        public bool ApplyToPlayable { get; set; } = true;
        public bool ApplyToCutscene { get; set; } = true;
    }

    public sealed class FileResult
    {
        public string Role { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Success { get; set; }
        public bool TransientFileLock { get; set; }
        public bool ComponentFound { get; set; }
        public bool CreatedOverrideArray { get; set; }
        public string? Error { get; set; }
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public bool TransientFileLock { get; set; }
        public string StageContentRoot { get; set; } = "";
        public List<FileResult> Files { get; set; } = new();
    }

    public Result Apply(string slotId, string playablePackagePath, string cutscenePackagePath, Assignment assignment) =>
        ApplyCore(slotId, playablePackagePath, cutscenePackagePath, assignment, stageContentRootOverride: null);

    /// <summary>
    /// Applies an assignment to an explicit disposable package-preparation Content root.
    /// This bypasses authoring-stage discovery so release preparation can never rewrite a
    /// certified GraftedPartStage/PatchedNameMapStage in place.
    /// </summary>
    public Result ApplyToContentRoot(
        string stageContentRoot,
        string slotId,
        string playablePackagePath,
        string cutscenePackagePath,
        Assignment assignment) =>
        ApplyCore(slotId, playablePackagePath, cutscenePackagePath, assignment, stageContentRoot);

    /// <summary>
    /// Reads the material actually assigned to an existing Blueprint component slot. This is used
    /// to certify paired-cape visual overlays; scanning arbitrary imports is not sufficient when a
    /// character Blueprint references several costume material variants.
    /// </summary>
    internal static string? TryReadComponentMaterialPackage(
        string uassetPath,
        string componentName,
        int slot,
        Usmap? mappings)
    {
        if (!File.Exists(uassetPath) || slot < 0)
        {
            return null;
        }
        try
        {
            var asset = new UAsset(
                uassetPath,
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var component = FindComponentExport(asset, componentName);
            var overrides = component?.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => property.Name.ToString().Equals(
                    "OverrideMaterials",
                    StringComparison.OrdinalIgnoreCase));
            if (overrides?.Value is null || slot >= overrides.Value.Length ||
                overrides.Value[slot] is not ObjectPropertyData material ||
                material.Value.IsNull() || !material.Value.IsImport())
            {
                return null;
            }

            var importIndex = -material.Value.Index - 1;
            if (importIndex < 0 || importIndex >= asset.Imports.Count)
            {
                return null;
            }
            var current = asset.Imports[importIndex];
            for (var depth = 0; depth < asset.Imports.Count + 1; depth++)
            {
                var name = current.ObjectName.ToString();
                if (name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                {
                    return UnrealPathUtil.NormalizePackagePath(name);
                }
                if (!current.OuterIndex.IsImport())
                {
                    return null;
                }
                var outerIndex = -current.OuterIndex.Index - 1;
                if (outerIndex < 0 || outerIndex >= asset.Imports.Count)
                {
                    return null;
                }
                current = asset.Imports[outerIndex];
            }
        }
        catch
        {
            // Declaration configuration reports a complete, actionable failure if the fallback
            // cannot establish a material either. This probe remains non-destructive.
        }
        return null;
    }

    private Result ApplyCore(
        string slotId,
        string playablePackagePath,
        string cutscenePackagePath,
        Assignment assignment,
        string? stageContentRootOverride)
    {
        var result = new Result();
        try
        {
            assignment.MiPackagePath = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath);
            playablePackagePath = UnrealPathUtil.NormalizePackagePath(playablePackagePath);
            cutscenePackagePath = UnrealPathUtil.NormalizePackagePath(cutscenePackagePath);

            if (string.IsNullOrWhiteSpace(assignment.MiPackagePath) ||
                !assignment.MiPackagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "bad-mi-path";
                result.Error = $"MI package path must start with /Game/. Got: {assignment.MiPackagePath}";
                return result;
            }

            var stageRoot = string.IsNullOrWhiteSpace(stageContentRootOverride)
                ? ResolveStageContentRoot(slotId)
                : Path.GetFullPath(stageContentRootOverride);
            if (stageRoot is null || !Directory.Exists(stageRoot))
            {
                result.Status = "no-stage";
                result.Error = string.IsNullOrWhiteSpace(stageContentRootOverride)
                    ? "No staged content found (run graft / name-map patch first)."
                    : $"The explicit package-preparation Content root does not exist: {stageRoot}";
                return result;
            }
            result.StageContentRoot = stageRoot;

            var mappings = LoadMappings();

            if (assignment.ApplyToPlayable && !string.IsNullOrWhiteSpace(playablePackagePath))
            {
                result.Files.Add(ApplyToAsset("playable", stageRoot, playablePackagePath, assignment, mappings));
            }
            if (assignment.ApplyToCutscene && !string.IsNullOrWhiteSpace(cutscenePackagePath))
            {
                result.Files.Add(ApplyToAsset("cutscene", stageRoot, cutscenePackagePath, assignment, mappings));
            }

            result.TransientFileLock = result.Files.Any(file => file.TransientFileLock);
            result.Status = result.Files.Any(f => f.Success) ? "applied" : "no-change";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            result.TransientFileLock = FileLockUtil.IsTransient(ex);
            return result;
        }
    }

    /// <summary>Lists the components and their override materials for a staged playable/cutscene asset.</summary>
    public List<string> DescribeStage(string slotId, string role, string? packagePath = null)
    {
        var lines = new List<string>();
        var stageRoot = ResolveStageContentRoot(slotId);
        if (stageRoot is null)
        {
            lines.Add("No staged content found (set a base suit first).");
            return lines;
        }

        var uassetPath = ResolveStageAssetPath(stageRoot, role, packagePath);
        if (uassetPath is null)
        {
            lines.Add($"No {role} asset found under {stageRoot}.");
            return lines;
        }

        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);
        lines.Add(Path.GetFileName(uassetPath));

        var inactiveScsTemplates = InactiveScsTemplateExportIndices(asset);
        for (var exportIndex = 1; exportIndex <= asset.Exports.Count; exportIndex++)
        {
            if (asset.Exports[exportIndex - 1] is not NormalExport export ||
                inactiveScsTemplates.Contains(exportIndex))
            {
                continue;
            }

            var cls = export.GetExportClassType().Value?.ToString() ?? "";
            if (!cls.Contains("MeshComponent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = export.ObjectName.ToString();
            var overrides = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name.ToString().Equals("OverrideMaterials", StringComparison.OrdinalIgnoreCase));
            if (overrides?.Value is null || overrides.Value.Length == 0)
            {
                lines.Add($"{name}: (mesh default materials)");
                continue;
            }

            for (var i = 0; i < overrides.Value.Length; i++)
            {
                var mat = "(mesh default)";
                if (overrides.Value[i] is ObjectPropertyData op && !op.Value.IsNull() && op.Value.IsImport())
                {
                    mat = asset.Imports[-op.Value.Index - 1].ObjectName.ToString();
                }
                lines.Add($"{name}  slot {i} = {mat}");
            }
        }

        return lines;
    }

    public sealed class InspectorSlot
    {
        public int Slot { get; set; }
        public string Material { get; set; } = "";
        public bool IsDefault { get; set; }
    }

    public sealed class InspectorComponent
    {
        public string Name { get; set; } = "";
        public string Class { get; set; } = "";
        public string Mesh { get; set; } = "";
        public bool IsScsCreated { get; set; }
        public List<InspectorSlot> Slots { get; } = new();
    }

    public sealed class InspectorReport
    {
        public string Role { get; set; } = "";
        public string AssetFile { get; set; } = "";
        public bool Found { get; set; }
        public string? Message { get; set; }
        public List<InspectorComponent> Components { get; } = new();
    }

    /// <summary>Structured component + material breakdown for the inspector view.</summary>
    public InspectorReport DescribeStageComponents(string slotId, string role, string? packagePath = null)
    {
        var report = new InspectorReport { Role = role };
        var stageRoot = ResolveStageContentRoot(slotId);
        if (stageRoot is null)
        {
            report.Message = "No staged content found (set a base suit first).";
            return report;
        }

        var uassetPath = ResolveStageAssetPath(stageRoot, role, packagePath);
        if (uassetPath is null)
        {
            report.Message = $"No {role} asset found under the stage.";
            return report;
        }

        report.AssetFile = Path.GetFileName(uassetPath);
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);

        var inactiveScsTemplates = InactiveScsTemplateExportIndices(asset);
        var activeScsTemplates = ActiveScsTemplateExportIndices(asset);
        for (var exportIndex = 1; exportIndex <= asset.Exports.Count; exportIndex++)
        {
            if (asset.Exports[exportIndex - 1] is not NormalExport export ||
                inactiveScsTemplates.Contains(exportIndex))
            {
                continue;
            }

            var cls = export.GetExportClassType().Value?.ToString() ?? "";
            if (!cls.Contains("MeshComponent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new InspectorComponent
            {
                Name = export.ObjectName.ToString().Replace("_GEN_VARIABLE", ""),
                Class = cls,
                IsScsCreated = activeScsTemplates.Contains(exportIndex),
            };

            // Surface the component's mesh (StaticMesh / SkeletalMesh / SkinnedAsset).
            foreach (var meshProp in new[] { "SkeletalMesh", "StaticMesh", "SkinnedAsset" })
            {
                var mp = export.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals(meshProp, StringComparison.OrdinalIgnoreCase));
                if (mp is not null && !mp.Value.IsNull() && mp.Value.IsImport())
                {
                    info.Mesh = asset.Imports[-mp.Value.Index - 1].ObjectName.ToString();
                    break;
                }
            }

            var overrides = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name.ToString().Equals("OverrideMaterials", StringComparison.OrdinalIgnoreCase));
            if (overrides?.Value is not null)
            {
                for (var i = 0; i < overrides.Value.Length; i++)
                {
                    var slot = new InspectorSlot { Slot = i, IsDefault = true, Material = "(mesh default)" };
                    if (overrides.Value[i] is ObjectPropertyData op && !op.Value.IsNull() && op.Value.IsImport())
                    {
                        slot.Material = asset.Imports[-op.Value.Index - 1].ObjectName.ToString();
                        slot.IsDefault = false;
                    }
                    info.Slots.Add(slot);
                }
            }

            report.Components.Add(info);
        }

        report.Found = true;
        return report;
    }

    private static string? ResolveStageAssetPath(string stageRoot, string role, string? packagePath)
    {
        if (!string.IsNullOrWhiteSpace(packagePath))
        {
            var exact = PackagePathToBasePath(stageRoot, packagePath) + ".uasset";
            if (File.Exists(exact))
            {
                return exact;
            }
        }

        var suffix = role.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
            ? "_Cutscene.uasset"
            : "_Playable.uasset";

        // Avoid accidentally treating DA_DCMD_*_Playable as the playable BP.
        return Directory
            .EnumerateFiles(stageRoot, "*" + suffix, SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !name.StartsWith("DA_DCMD_", StringComparison.OrdinalIgnoreCase) &&
                       !name.StartsWith("DA_UIMD_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(path => Path.GetFileName(path).StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private FileResult ApplyToAsset(string role, string stageRoot, string packagePath, Assignment assignment, Usmap? mappings)
    {
        var fileResult = new FileResult { Role = role };
        try
        {
            var uassetPath = PackagePathToBasePath(stageRoot, packagePath) + ".uasset";
            fileResult.Path = uassetPath;
            if (!File.Exists(uassetPath))
            {
                fileResult.Error = $"Staged asset not found: {uassetPath}";
                return fileResult;
            }

            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);

            var component = FindComponentExport(asset, assignment.Component);
            if (component is null)
            {
                fileResult.Error = $"Component '{assignment.Component}' not found in {role}. Mesh-like components present: {ListMeshComponents(asset)}";
                return fileResult;
            }
            fileResult.ComponentFound = true;

            var (miPackage, miObject) = SplitObjectPath(assignment.MiPackagePath);
            var miImport = EnsureObjectImport(asset, miPackage, miObject, "/Script/Engine", "MaterialInstanceConstant");
            if (miImport.IsNull())
            {
                fileResult.Error = "Failed to import the MI.";
                return fileResult;
            }

            fileResult.CreatedOverrideArray = SetOverrideMaterialSlot(asset, component, assignment.Slot, miImport);

            // The .usmap lacks a schema for the cutscene parent class, so UAssetAPI
            // can't write unversioned headers for it. Inject a minimal stub (same
            // workaround the graft service uses) so the write succeeds.
            EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");

            asset.Write(uassetPath);
            fileResult.Success = true;
            return fileResult;
        }
        catch (Exception ex)
        {
            fileResult.Error = ex.ToString();
            fileResult.TransientFileLock = FileLockUtil.IsTransient(ex);
            return fileResult;
        }
    }

    /// <summary>Returns true if it had to create the OverrideMaterials array.</summary>
    private static bool SetOverrideMaterialSlot(UAsset asset, NormalExport component, int slot, FPackageIndex miImport)
    {
        var created = false;
        var prop = component.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name.ToString().Equals("OverrideMaterials", StringComparison.OrdinalIgnoreCase));

        if (prop is null)
        {
            prop = new ArrayPropertyData(MakeName(asset, "OverrideMaterials"))
            {
                ArrayType = MakeName(asset, "ObjectProperty"),
                Value = Array.Empty<PropertyData>()
            };
            component.Data.Add(prop);
            created = true;
        }

        var entries = (prop.Value ?? Array.Empty<PropertyData>()).ToList();
        while (entries.Count <= slot)
        {
            entries.Add(new ObjectPropertyData(MakeName(asset, entries.Count.ToString()))
            {
                Value = FPackageIndex.FromRawIndex(0) // null override => uses mesh default
            });
        }

        entries[slot] = new ObjectPropertyData(MakeName(asset, slot.ToString()))
        {
            Value = miImport
        };

        prop.Value = entries.ToArray();
        return created;
    }

    internal static NormalExport? FindComponentExport(UAsset asset, string componentName)
    {
        // The character body mesh is "CharacterMesh0" in the playable BP but the
        // inherited ACharacter mesh "Mesh" in the cutscene BP; accept either.
        var inactiveScsTemplates = InactiveScsTemplateExportIndices(asset);
        foreach (var candidate in ComponentAliases(componentName))
        {
            var hit = FindComponentExportExact(asset, candidate, inactiveScsTemplates);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static IEnumerable<string> ComponentAliases(string componentName)
    {
        yield return componentName;
        if (componentName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Mesh (CharacterMesh0)";
            yield return "Mesh";
        }
        else if (componentName.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
        {
            yield return "CharacterMesh0";
        }
    }

    private static NormalExport? FindComponentExportExact(UAsset asset, string componentName, HashSet<int> inactiveScsTemplates)
    {
        // 1) An SCS node whose InternalVariableName matches -> its ComponentTemplate.
        for (var exportIndex = 1; exportIndex <= asset.Exports.Count; exportIndex++)
        {
            if (asset.Exports[exportIndex - 1] is not NormalExport export)
            {
                continue;
            }

            if (!export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var internalName = export.Data.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase));
            if (internalName?.Value.ToString().Equals(componentName, StringComparison.OrdinalIgnoreCase) == true)
            {
                var templateProp = export.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals("ComponentTemplate", StringComparison.OrdinalIgnoreCase));
                var idx = templateProp?.Value?.Index ?? 0;
                if (idx > 0 &&
                    idx <= asset.Exports.Count &&
                    !inactiveScsTemplates.Contains(idx) &&
                    asset.Exports[idx - 1] is NormalExport tmpl)
                {
                    return tmpl;
                }
            }
        }

        // 2) Export named exactly (e.g. CharacterMesh0) or the GEN_VARIABLE template.
        for (var exportIndex = 1; exportIndex <= asset.Exports.Count; exportIndex++)
        {
            if (inactiveScsTemplates.Contains(exportIndex) ||
                asset.Exports[exportIndex - 1] is not NormalExport export)
            {
                continue;
            }

            var name = export.ObjectName.ToString();
            if (name.Equals(componentName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(componentName + "_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase))
            {
                return export;
            }
        }

        return null;
    }

    private static HashSet<int> InactiveScsTemplateExportIndices(UAsset asset)
    {
        var referencedNodeIndexes = ReferencedScsNodeExportIndices(asset);
        var allTemplates = new HashSet<int>();
        var activeTemplates = new HashSet<int>();

        for (var exportIndex = 1; exportIndex <= asset.Exports.Count; exportIndex++)
        {
            if (asset.Exports[exportIndex - 1] is not NormalExport export ||
                !export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var templateIndex = export.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name.ToString().Equals("ComponentTemplate", StringComparison.OrdinalIgnoreCase))
                ?.Value.Index ?? 0;

            if (templateIndex <= 0 || templateIndex > asset.Exports.Count)
            {
                continue;
            }

            allTemplates.Add(templateIndex);
            if (referencedNodeIndexes.Contains(exportIndex))
            {
                activeTemplates.Add(templateIndex);
            }
        }

        allTemplates.ExceptWith(activeTemplates);
        return allTemplates;
    }

    private static HashSet<int> ActiveScsTemplateExportIndices(UAsset asset)
    {
        var referencedNodeIndexes = ReferencedScsNodeExportIndices(asset);
        var activeTemplates = new HashSet<int>();
        foreach (var nodeIndex in referencedNodeIndexes)
        {
            if (nodeIndex <= 0 ||
                nodeIndex > asset.Exports.Count ||
                asset.Exports[nodeIndex - 1] is not NormalExport node ||
                !node.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var templateIndex = node.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => property.Name.ToString().Equals(
                    "ComponentTemplate",
                    StringComparison.OrdinalIgnoreCase))
                ?.Value.Index ?? 0;
            if (templateIndex > 0 && templateIndex <= asset.Exports.Count)
            {
                activeTemplates.Add(templateIndex);
            }
        }

        return activeTemplates;
    }

    private static HashSet<int> ReferencedScsNodeExportIndices(UAsset asset)
    {
        var output = new HashSet<int>();
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            foreach (var property in export.Data.OfType<ArrayPropertyData>())
            {
                var name = property.Name.ToString();
                if (!name.Equals("RootNodes", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("AllNodes", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("ChildNodes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in property.Value ?? Array.Empty<PropertyData>())
                {
                    if (value is ObjectPropertyData objectProperty &&
                        objectProperty.Value.Index > 0 &&
                        objectProperty.Value.Index <= asset.Exports.Count)
                    {
                        output.Add(objectProperty.Value.Index);
                    }
                }
            }
        }

        return output;
    }

    // Diagnostic: list mesh-component-ish exports so a "not found" error is actionable.
    private static string ListMeshComponents(UAsset asset)
    {
        var names = new List<string>();
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var cls = export.GetExportClassType().Value?.ToString() ?? "";
            var name = export.ObjectName.ToString();
            if (cls.Contains("MeshComponent", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                if (name.StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
                {
                    var internalName = export.Data.OfType<NamePropertyData>()
                        .FirstOrDefault(p => p.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase));
                    if (internalName is not null) names.Add(internalName.Value.ToString() + " (SCS)");
                }
                else
                {
                    names.Add(name + " [" + cls + "]");
                }
            }
        }
        return names.Count == 0 ? "(none)" : string.Join(", ", names.Distinct());
    }

    // ---- stage resolution ---------------------------------------------------

    private string? ResolveStageContentRoot(string slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return null;
        }

        // A project opened from an older portable workspace can retain a nested
        // Generated path. The active tool root remains the reliable fallback.
        var projectRoots = new[] { ProjectRoot, AppSettings.Current.EffectiveProjectRoot() }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var projectRoot in projectRoots)
        {
            var baseDir = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId);
            var candidates = new[]
            {
                Path.Combine(baseDir, "GraftedPartStage", "LEGOBatmanLotDK", "Content"),
                Path.Combine(baseDir, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content"),
                Path.Combine(baseDir, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content")
            };
            var found = candidates.FirstOrDefault(Directory.Exists);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ---- UAssetAPI helpers (self-contained) ---------------------------------

    private static FPackageIndex EnsureObjectImport(UAsset asset, string packagePath, string objectName, string classPackage, string className)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(objectName))
        {
            return FPackageIndex.FromRawIndex(0);
        }

        var packageImport = EnsurePackageImport(asset, packagePath);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(objectName, StringComparison.Ordinal) &&
                import.OuterIndex.Index == packageImport.Index &&
                import.ClassPackage.ToString().Equals(classPackage, StringComparison.Ordinal) &&
                import.ClassName.ToString().Equals(className, StringComparison.Ordinal))
            {
                return FromImportNumber(i + 1);
            }
        }

        AddNames(asset, objectName, classPackage, className);
        return asset.AddImport(new Import(classPackage, className, packageImport, objectName, false, asset));
    }

    private static FPackageIndex EnsurePackageImport(UAsset asset, string packagePath)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(packagePath, StringComparison.Ordinal) &&
                import.OuterIndex.IsNull() &&
                import.ClassName.ToString().Equals("Package", StringComparison.Ordinal))
            {
                return FromImportNumber(i + 1);
            }
        }

        AddNames(asset, packagePath, "/Script/CoreUObject", "Package");
        return asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), packagePath, false, asset));
    }

    private static void AddNames(UAsset asset, params string?[] names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && !asset.ContainsNameReference(new FString(name)))
            {
                asset.AddNameReference(new FString(name), false, false);
            }
        }
    }

    private static FName MakeName(UAsset asset, string value)
    {
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }
        return new FName(asset, value);
    }

    private static void EnsureMinimalSchema(UAsset asset, string schemaName, string modulePath)
    {
        var mappings = asset.Mappings;
        if (mappings is null || mappings.Schemas.ContainsKey(schemaName))
        {
            return;
        }

        mappings.Schemas[schemaName] = new UsmapSchema(
            name: schemaName,
            superType: "",
            propCount: 0,
            props: new ConcurrentDictionary<int, UsmapProperty>(),
            isCaseInsensitive: mappings.AreFNamesCaseInsensitive,
            superTypeModulePath: "",
            fromAsset: true)
        {
            ModulePath = modulePath
        };
    }

    private static FPackageIndex FromImportNumber(int importNumber)
    {
        return importNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromImport(importNumber - 1);
    }

    private Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }

    private static (string package, string obj) SplitObjectPath(string path)
    {
        var packagePath = UnrealPathUtil.NormalizePackagePath(path);
        return (packagePath, UnrealPathUtil.AssetName(packagePath));
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only /Game package paths are supported. Got: {packagePath}");
        }
        return Path.Combine(contentRoot, packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
