using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

/// <summary>
/// Removes a Blueprint-created character part from the staged playable/cutscene
/// assets by removing its SCS node from the construction-script node arrays.
///
/// Important: this deliberately does not physically delete exports from the
/// package. Deleting exports shifts package indices and is much easier to
/// corrupt in cooked assets. An orphaned component template is okay; if its SCS
/// node is no longer referenced by RootNodes/AllNodes/ChildNodes, the component
/// should not be constructed.
/// </summary>
public sealed class ComponentRemoveService
{
    public string ProjectRoot { get; }

    public ComponentRemoveService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    /// <summary>
    /// Lists the InternalVariableName of every SCS component in the staged asset
    /// whose name starts with <paramref name="prefix"/> (case-insensitive). Used to
    /// deterministically find all "Cape"/"Cape_2"/… duplicates for cleanup.
    /// </summary>
    public List<string> ListScsComponentNames(string slotId, string packagePath, string prefix)
    {
        var names = new List<string>();
        try
        {
            var stageRoot = ResolveStageContentRoot(slotId);
            if (stageRoot is null || string.IsNullOrWhiteSpace(packagePath))
            {
                return names;
            }
            var uassetPath = PackagePathToBasePath(stageRoot, packagePath) + ".uasset";
            if (!File.Exists(uassetPath))
            {
                return names;
            }
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);
            for (var i = 0; i < asset.Exports.Count; i++)
            {
                var exp = asset.Exports[i];
                if (exp is not NormalExport normal ||
                    !normal.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!AnyArrayContainsObjectIndexLive(asset, "RootNodes", FromExportNumber(i + 1)) &&
                    !AnyArrayContainsObjectIndexLive(asset, "AllNodes", FromExportNumber(i + 1)) &&
                    !AnyArrayContainsObjectIndexLive(asset, "ChildNodes", FromExportNumber(i + 1)))
                {
                    continue;
                }
                var iv = FindPropertyLive<NamePropertyData>(normal.Data, "InternalVariableName")?.Value.ToString();
                if (!string.IsNullOrEmpty(iv) && iv.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(iv);
                }
            }
        }
        catch
        {
            // best effort - return whatever we found
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Sets AttachToName = <paramref name="socket"/> on every SCS node whose
    /// InternalVariableName starts with <paramref name="prefix"/>, in the staged
    /// playable + cutscene. Returns how many nodes were changed. Used to correct the
    /// wingsuit "Cape" attach (Chest_Socket → Root) in place without re-grafting.
    /// </summary>
    public int SetScsNodeAttachSocketForPrefix(string slotId, string playablePackagePath, string cutscenePackagePath, string prefix, string socket)
    {
        var changed = 0;
        var stageRoot = ResolveStageContentRoot(slotId);
        if (stageRoot is null)
        {
            return 0;
        }
        var mappings = LoadMappings();
        foreach (var pkg in new[] { playablePackagePath, cutscenePackagePath })
        {
            if (string.IsNullOrWhiteSpace(pkg))
            {
                continue;
            }
            try
            {
                var uassetPath = PackagePathToBasePath(stageRoot, pkg) + ".uasset";
                if (!File.Exists(uassetPath))
                {
                    continue;
                }
                var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                var dirty = false;
                foreach (var exp in asset.Exports.OfType<NormalExport>())
                {
                    if (!exp.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var iv = FindPropertyLive<NamePropertyData>(exp.Data, "InternalVariableName")?.Value.ToString();
                    if (string.IsNullOrEmpty(iv) || !iv.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var attach = FindPropertyLive<NamePropertyData>(exp.Data, "AttachToName");
                    if (attach is not null)
                    {
                        attach.Value = FName.FromString(asset, socket);
                        changed++;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    // Cooked cutscene BPs need the parent-class schema stubbed before
                    // an unversioned write (usmap lacks BP_CutsceneMinifigCharacter_C).
                    EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
                    asset.Write(uassetPath);
                }
            }
            catch
            {
                // best effort
            }
        }
        return changed;
    }

    public ComponentRemoveResult Remove(
        string slotId,
        string playablePackagePath,
        string cutscenePackagePath,
        string componentName,
        bool applyToPlayable = true,
        bool applyToCutscene = true)
    {
        var result = new ComponentRemoveResult
        {
            SlotId = slotId,
            Component = componentName
        };

        try
        {
            var stageRoot = ResolveStageContentRoot(slotId);
            if (stageRoot is null)
            {
                result.Status = "no-stage";
                result.Error = "No staged content found. Set a base suit / create a stage first.";
                return result;
            }

            result.StageContentRoot = stageRoot;
            var mappings = LoadMappings();

            if (applyToPlayable && !string.IsNullOrWhiteSpace(playablePackagePath))
            {
                result.Files.Add(RemoveFromAsset("playable", stageRoot, playablePackagePath, componentName, mappings));
            }

            if (applyToCutscene && !string.IsNullOrWhiteSpace(cutscenePackagePath))
            {
                result.Files.Add(RemoveFromAsset("cutscene", stageRoot, cutscenePackagePath, componentName, mappings));
            }

            result.Status = result.Files.Any(file => file.Success)
                ? "removed"
                : "no-change";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>
    /// Restores a previously removed SCS node back into the cooked Blueprint
    /// construction arrays. Removal intentionally leaves exports behind and only
    /// drops RootNodes/AllNodes/ChildNodes references; without this, a later glider
    /// repoint can edit an orphaned component export that the game never constructs.
    /// </summary>
    public ComponentRemoveResult RestoreScsReferences(
        string slotId,
        string playablePackagePath,
        string cutscenePackagePath,
        string componentName,
        bool applyToPlayable = true,
        bool applyToCutscene = true)
    {
        var result = new ComponentRemoveResult
        {
            SlotId = slotId,
            Component = componentName
        };

        try
        {
            var stageRoot = ResolveStageContentRoot(slotId);
            if (stageRoot is null)
            {
                result.Status = "no-stage";
                result.Error = "No staged content found. Set a base suit / create a stage first.";
                return result;
            }

            result.StageContentRoot = stageRoot;
            var mappings = LoadMappings();

            if (applyToPlayable && !string.IsNullOrWhiteSpace(playablePackagePath))
            {
                result.Files.Add(RestoreScsReferencesInAsset("playable", stageRoot, playablePackagePath, componentName, mappings));
            }

            if (applyToCutscene && !string.IsNullOrWhiteSpace(cutscenePackagePath))
            {
                result.Files.Add(RestoreScsReferencesInAsset("cutscene", stageRoot, cutscenePackagePath, componentName, mappings));
            }

            result.Status = result.Files.Any(file => file.Success && file.RestoredNodeReferences > 0)
                ? "restored"
                : result.Files.Any(file => file.ComponentFound)
                    ? "already-present"
                    : "no-change";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    private ComponentRemoveFileResult RemoveFromAsset(
        string role,
        string stageRoot,
        string packagePath,
        string componentName,
        Usmap? mappings)
    {
        var fileResult = new ComponentRemoveFileResult
        {
            Role = role,
            TargetPackagePath = packagePath
        };

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
            var nodeIndex = FindScsNodeBySlotLive(asset, componentName);
            if (nodeIndex == 0)
            {
                var directComponent = FindComponentExport(asset, componentName);
                if (directComponent is not null)
                {
                    fileResult.ComponentFound = true;
                    fileResult.Error =
                        $"Component '{componentName}' exists in {role}, but it is not an SCS-created part. " +
                        "Actual removal is only supported for Blueprint/SCS parts right now; inherited core meshes should be hidden/replaced instead.";
                    return fileResult;
                }

                fileResult.Error = $"Component/SCS slot '{componentName}' was not found in {role}.";
                return fileResult;
            }

            fileResult.ComponentFound = true;
            fileResult.ScsNodeExportIndex = nodeIndex;

            var node = asset.Exports[nodeIndex - 1] as NormalExport
                ?? throw new InvalidOperationException($"SCS node export {nodeIndex} was not a NormalExport.");

            var templateIndex = GetObjectPropertyValueLive(node.Data, "ComponentTemplate").Index;
            fileResult.ComponentTemplateExportIndex = templateIndex;

            var removedReferences = RemoveObjectIndexFromScsArraysLive(asset, FromExportNumber(nodeIndex));
            fileResult.RemovedNodeReferences = removedReferences;

            if (removedReferences <= 0)
            {
                fileResult.Error = $"SCS node '{componentName}' was found, but it was not referenced by RootNodes/AllNodes/ChildNodes.";
                return fileResult;
            }

            if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
            {
                EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
            }

            UpdateRootCountsLive(asset);
            asset.Write(uassetPath);

            fileResult.Success = true;
            return fileResult;
        }
        catch (Exception ex)
        {
            fileResult.Error = ex.ToString();
            return fileResult;
        }
    }

    private ComponentRemoveFileResult RestoreScsReferencesInAsset(
        string role,
        string stageRoot,
        string packagePath,
        string componentName,
        Usmap? mappings)
    {
        var fileResult = new ComponentRemoveFileResult
        {
            Role = role,
            TargetPackagePath = packagePath
        };

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
            var nodeIndex = FindScsNodeBySlotLive(asset, componentName);
            if (nodeIndex == 0)
            {
                fileResult.Error = $"Component/SCS slot '{componentName}' was not found in {role}.";
                return fileResult;
            }

            fileResult.ComponentFound = true;
            fileResult.ScsNodeExportIndex = nodeIndex;

            var node = asset.Exports[nodeIndex - 1] as NormalExport
                ?? throw new InvalidOperationException($"SCS node export {nodeIndex} was not a NormalExport.");

            fileResult.ComponentTemplateExportIndex = GetObjectPropertyValueLive(node.Data, "ComponentTemplate").Index;

            var nodeRef = FromExportNumber(nodeIndex);
            var restored = 0;

            restored += EnsureObjectIndexInFirstArrayLive(asset, "AllNodes", nodeRef);

            // If removal orphaned the node completely, put it back as a root
            // construction node. If it is already referenced by RootNodes or
            // ChildNodes, leave the existing hierarchy alone.
            if (!AnyArrayContainsObjectIndexLive(asset, "RootNodes", nodeRef) &&
                !AnyArrayContainsObjectIndexLive(asset, "ChildNodes", nodeRef))
            {
                restored += EnsureObjectIndexInFirstArrayLive(asset, "RootNodes", nodeRef);
            }

            fileResult.RestoredNodeReferences = restored;
            fileResult.Success = true;

            if (restored > 0)
            {
                if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
                }

                UpdateRootCountsLive(asset);
                asset.Write(uassetPath);
            }

            return fileResult;
        }
        catch (Exception ex)
        {
            fileResult.Error = ex.ToString();
            return fileResult;
        }
    }

    private static int FindScsNodeBySlotLive(UAsset asset, string slot)
    {
        foreach (var candidate in ComponentAliases(slot))
        {
            for (var i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is not NormalExport normal ||
                    !normal.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var internalVariableName = FindPropertyLive<NamePropertyData>(normal.Data, "InternalVariableName");
                if (internalVariableName?.Value.ToString().Equals(candidate, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return i + 1;
                }
            }
        }

        return 0;
    }

    private static NormalExport? FindComponentExport(UAsset asset, string componentName)
    {
        foreach (var candidate in ComponentAliases(componentName))
        {
            var exact = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
                export.ObjectName.ToString().Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                export.ObjectName.ToString().Equals(candidate + "_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
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

    private static int RemoveObjectIndexFromScsArraysLive(UAsset asset, FPackageIndex objectIndex)
    {
        var removed = 0;
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            foreach (var property in export.Data.OfType<ArrayPropertyData>())
            {
                var propertyName = property.Name.ToString();
                if (!propertyName.Equals("RootNodes", StringComparison.OrdinalIgnoreCase) &&
                    !propertyName.Equals("AllNodes", StringComparison.OrdinalIgnoreCase) &&
                    !propertyName.Equals("ChildNodes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var oldValues = property.Value ?? Array.Empty<PropertyData>();
                var newValues = new List<PropertyData>();
                var changed = false;
                foreach (var value in oldValues)
                {
                    if (value is ObjectPropertyData objectProperty &&
                        objectProperty.Value.Index == objectIndex.Index)
                    {
                        removed++;
                        changed = true;
                        continue;
                    }

                    newValues.Add(value);
                }

                if (!changed)
                {
                    continue;
                }

                property.Value = newValues
                    .Select((value, i) =>
                    {
                        if (value is ObjectPropertyData objectProperty)
                        {
                            return (PropertyData)new ObjectPropertyData(MakeName(asset, i.ToString()))
                            {
                                Value = objectProperty.Value
                            };
                        }

                        return value;
                    })
                    .ToArray();
            }
        }

        return removed;
    }

    private static bool AnyArrayContainsObjectIndexLive(UAsset asset, string arrayPropertyName, FPackageIndex objectIndex)
    {
        return asset.Exports.OfType<NormalExport>()
            .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
            .Where(property => property.Name.ToString().Equals(arrayPropertyName, StringComparison.OrdinalIgnoreCase))
            .Any(property => (property.Value ?? Array.Empty<PropertyData>())
                .OfType<ObjectPropertyData>()
                .Any(value => value.Value.Index == objectIndex.Index));
    }

    private static int EnsureObjectIndexInFirstArrayLive(UAsset asset, string arrayPropertyName, FPackageIndex objectIndex)
    {
        foreach (var property in asset.Exports.OfType<NormalExport>()
                     .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
                     .Where(property => property.Name.ToString().Equals(arrayPropertyName, StringComparison.OrdinalIgnoreCase)))
        {
            var oldValues = property.Value ?? Array.Empty<PropertyData>();
            if (oldValues.OfType<ObjectPropertyData>().Any(value => value.Value.Index == objectIndex.Index))
            {
                return 0;
            }

            var newValues = oldValues.ToList();
            newValues.Add(new ObjectPropertyData(MakeName(asset, newValues.Count.ToString()))
            {
                Value = objectIndex
            });
            property.Value = newValues.ToArray();
            return 1;
        }

        return 0;
    }

    private static FPackageIndex FromExportNumber(int exportNumber)
    {
        return exportNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromExport(exportNumber - 1);
    }

    private static FPackageIndex GetObjectPropertyValueLive(List<PropertyData> properties, string propertyName)
    {
        var property = FindPropertyLive<ObjectPropertyData>(properties, propertyName);
        return property?.Value ?? FPackageIndex.FromRawIndex(0);
    }

    private static T? FindPropertyLive<T>(List<PropertyData> properties, string propertyName)
        where T : PropertyData
    {
        return properties
            .OfType<T>()
            .FirstOrDefault(property => property.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static void UpdateRootCountsLive(UAsset asset)
    {
        foreach (var generation in asset.Generations)
        {
            generation.ExportCount = asset.Exports.Count;
            generation.NameCount = asset.GetNameMapIndexList().Count;
        }
    }

    private static void EnsureMinimalSchema(UAsset asset, string schemaName, string modulePath)
    {
        var mappings = asset.Mappings;
        if (mappings is null || mappings.Schemas.ContainsKey(schemaName))
        {
            return;
        }

        var schema = new UsmapSchema(
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

        mappings.Schemas[schemaName] = schema;
    }

    private static FName MakeName(UAsset asset, string value)
    {
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }

        return new FName(asset, value, 0);
    }

    private string? ResolveStageContentRoot(string slotId)
    {
        var baseDir = Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitGuiProjects", slotId);
        var candidates = new[]
        {
            Path.Combine(baseDir, "GraftedPartStage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(baseDir, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(baseDir, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private Usmap? LoadMappings()
    {
        var path = FindDefaultMappingsPath();
        return string.IsNullOrWhiteSpace(path) ? null : MappingsCache.Load(path);
    }

    private string? FindDefaultMappingsPath()
    {
        var configured = AppSettings.Current.UsmapPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            AppSettings.BundledUsmapPath() ?? "",
            Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "PartGraphProbe", "input", "Dinner.usmap"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UAssetGUI", "Mappings", "Dinner-5.6.1-1283556+++Dinner+mainline-7f7cc36f.usmap"),
        };

        return candidates.FirstOrDefault(File.Exists);
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

public sealed class ComponentRemoveResult
{
    public string Status { get; set; } = "";
    public string SlotId { get; set; } = "";
    public string Component { get; set; } = "";
    public string StageContentRoot { get; set; } = "";
    public string? Error { get; set; }
    public List<ComponentRemoveFileResult> Files { get; set; } = new();
}

public sealed class ComponentRemoveFileResult
{
    public string Role { get; set; } = "";
    public string TargetPackagePath { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Success { get; set; }
    public bool ComponentFound { get; set; }
    public int ScsNodeExportIndex { get; set; }
    public int ComponentTemplateExportIndex { get; set; }
    public int RemovedNodeReferences { get; set; }
    public int RestoredNodeReferences { get; set; }
    public string? Error { get; set; }
}
