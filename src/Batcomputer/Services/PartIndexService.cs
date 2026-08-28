using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

public sealed class PartIndexService
{
    public const int CurrentIndexSchemaVersion = 4;

    private static readonly string[] CharacterRigFolders = { "Minifig", "Smallfig" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> IgnoredScsSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "DefaultSceneRoot",
        "TtCharacterAssetMinion",
        "WubDialogueVoiceActor"
    };

    private static readonly HashSet<string> KnownVisualSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Face",
        "Head",
        "Cape",
        "Torso",
        "Torso1",
        "Torso2",
        "Hip",
        // Hair/hat/head-topper attachments are ordinary static-mesh SCS
        // components (SM_HAIR_*/SM_HAT_* from Content/Characters/Attachments).
        // Treating them as visual slots lets the existing part graft swap them.
        "HAIR",
        "Hair",
        "HAT",
        "Hat",
        "HeadAttachment",
        "Hat_Hair"
    };

    public string ProjectRoot { get; }
    public string PartIndexOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitParts");
    public string PartIndexPath => Path.Combine(PartIndexOutputRoot, "part-index.json");

    public PartIndexService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public NativeSuitPartIndex BuildPartIndex(string? sourceContentRoot = null)
    {
        var contentRoot = string.IsNullOrWhiteSpace(sourceContentRoot)
            ? FindDefaultExtractedContentRoot()
            : AppSettings.NormalizeContentRoot(sourceContentRoot.Trim());
        // Preserve the legacy diagnostic field for existing part-index readers while the scan
        // itself now covers both supported character rigs.
        var legacyMinifigRoot = Path.Combine(contentRoot, "Characters", "Minifig");
        var characterRoots = EnumerateCharacterRigRoots(contentRoot);

        var index = new NativeSuitPartIndex
        {
            CreatedUtc = DateTime.UtcNow,
            SourceContentRoot = contentRoot,
            SourceMinifigRoot = legacyMinifigRoot,
            MappingsPath = FindDefaultMappingsPath()
        };

        Directory.CreateDirectory(PartIndexOutputRoot);

        if (characterRoots.Count == 0)
        {
            index.Status = "missing-source-root";
            index.Errors.Add(new NativeSuitPartScanError
            {
                Uasset = Path.Combine(contentRoot, "Characters"),
                Error = "No extracted Minifig or Smallfig character root was found, including under AdditionalContent."
            });
            SavePartIndex(index);
            return index;
        }

        Usmap? mappings = null;
        if (!string.IsNullOrWhiteSpace(index.MappingsPath))
        {
            try
            {
                mappings = MappingsCache.Load(index.MappingsPath);
            }
            catch (Exception ex)
            {
                index.Errors.Add(new NativeSuitPartScanError
                {
                    Uasset = index.MappingsPath,
                    Error = "Failed to load mappings: " + ex.Message
                });
            }
        }

        var assets = EnumerateCharacterBlueprints(contentRoot);

        index.AssetsFound = assets.Count;

        foreach (var assetPath in assets)
        {
            try
            {
                var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                using var doc = JsonDocument.Parse(asset.SerializeJson(false));
                var parts = ExtractParts(doc.RootElement, assetPath, contentRoot);
                index.AssetsParsed++;
                if (parts.Count > 0)
                {
                    index.AssetsWithParts++;
                    index.Parts.AddRange(parts);
                }
            }
            catch (Exception ex)
            {
                index.Errors.Add(new NativeSuitPartScanError
                {
                    Uasset = assetPath,
                    Error = ex.Message
                });
            }
        }

        index.Status = index.Errors.Count == 0 ? "created" : "created-with-errors";
        index.Parts = index.Parts
            .OrderBy(part => part.Context, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SavePartIndex(index);
        return index;
    }

    public NativeSuitPartIndex? LoadPartIndex()
    {
        if (!File.Exists(PartIndexPath))
        {
            return null;
        }

        try
        {
            var index = JsonSerializer.Deserialize<NativeSuitPartIndex>(
                File.ReadAllText(PartIndexPath),
                JsonOptions);
            // Version 2 scanned Minifig only. Treat it as a stale cache so selecting an extracted
            // Smallfig visual automatically builds the multi-rig index instead of silently omitting
            // its cape, head, and face recipes. A same-schema index from a different extract is
            // stale too: its donor paths and recipes must never be replayed against the active dump.
            return IsCurrentIndex(index, FindDefaultExtractedContentRoot()) ? index : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // This file is a disposable cache. A partial write or an older malformed copy must not
            // prevent startup; callers that require the index will rebuild it from the extract.
            return null;
        }
    }

    internal static bool IsCurrentIndexForTest(NativeSuitPartIndex? index) => IsCurrentIndex(index);

    private static bool IsCurrentIndex(NativeSuitPartIndex? index) =>
        index is not null && index.SchemaVersion >= CurrentIndexSchemaVersion;

    private static bool IsCurrentIndex(NativeSuitPartIndex? index, string activeContentRoot)
    {
        if (!IsCurrentIndex(index) || string.IsNullOrWhiteSpace(activeContentRoot))
        {
            return false;
        }

        var sourceContentRoot = index!.SourceContentRoot;
        if (string.IsNullOrWhiteSpace(sourceContentRoot))
        {
            return false;
        }

        try
        {
            var indexedContentRoot = AppSettings.NormalizeContentRoot(sourceContentRoot);
            var normalizedActiveRoot = AppSettings.NormalizeContentRoot(activeContentRoot);
            return indexedContentRoot.Equals(normalizedActiveRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // SourceContentRoot is persisted cache metadata. Invalid path data makes the cache stale;
            // callers can rebuild it from the currently configured extract.
            return false;
        }
    }

    private void SavePartIndex(NativeSuitPartIndex index)
    {
        AtomicFileUtil.WriteAllText(PartIndexPath, JsonSerializer.Serialize(index, JsonOptions));
    }

    internal static IReadOnlyList<string> EnumerateCharacterBlueprintsForTest(string contentRoot) =>
        EnumerateCharacterBlueprints(AppSettings.NormalizeContentRoot(contentRoot));

    private static List<string> EnumerateCharacterBlueprints(string contentRoot) =>
        EnumerateCharacterRigRoots(contentRoot)
            .SelectMany(root => Directory.EnumerateFiles(root, "BP_*.uasset", SearchOption.AllDirectories))
            .Where(path => !Path.GetFileNameWithoutExtension(path)
                .StartsWith("BP_CAT_Archetype", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> EnumerateCharacterRigRoots(string contentRoot)
    {
        if (!Directory.Exists(contentRoot))
        {
            return new List<string>();
        }

        return CharacterContentRootService.Enumerate(contentRoot)
            .SelectMany(charactersRoot => CharacterRigFolders.Select(rig => Path.Combine(charactersRoot, rig)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<NativeSuitPartRecord> ExtractParts(JsonElement root, string assetPath, string contentRoot)
    {
        var output = new List<NativeSuitPartRecord>();
        if (!root.TryGetProperty("Exports", out var exportsElement) ||
            !root.TryGetProperty("Imports", out var importsElement))
        {
            return output;
        }

        var exports = exportsElement.EnumerateArray().ToList();
        var imports = importsElement.EnumerateArray().ToList();
        var classChildSlots = ExtractClassChildSlots(exports);
        var sourcePackagePath = GetString(root, "FolderName");
        if (string.IsNullOrWhiteSpace(sourcePackagePath))
        {
            sourcePackagePath = PackagePathFromContentPath(assetPath, contentRoot);
        }

        var contentRelative = Path.GetRelativePath(contentRoot, assetPath);
        var stem = Path.GetFileNameWithoutExtension(assetPath);
        var characterFolder = GetCharacterFolder(assetPath, contentRoot);
        var context = DetermineContext(stem);

        for (var exportIndex = 0; exportIndex < exports.Count; exportIndex++)
        {
            var export = exports[exportIndex];
            if (!GetString(export, "ObjectName").StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = GetArray(export, "Data");
            var slot = GetPropertyValueString(data, "InternalVariableName");
            if (string.IsNullOrWhiteSpace(slot) || IgnoredScsSlots.Contains(slot))
            {
                continue;
            }

            var componentTemplateIndex = GetPropertyObjectIndex(data, "ComponentTemplate");
            if (componentTemplateIndex <= 0 || componentTemplateIndex > exports.Count)
            {
                continue;
            }

            var componentTemplate = exports[componentTemplateIndex - 1];
            var componentData = GetArray(componentTemplate, "Data");
            var componentClassRef = ResolveObjectRef(imports, exports, GetPropertyObjectIndex(data, "ComponentClass"));
            var componentClass = !string.IsNullOrWhiteSpace(componentClassRef.ObjectName)
                ? componentClassRef.ObjectName
                : ResolveObjectRef(imports, exports, GetInt(componentTemplate, "ClassIndex")).ObjectName;

            var meshRef = ResolveFirstMesh(imports, exports, componentData, out var meshKind);
            var animClassRef = ResolveObjectRef(imports, exports, GetPropertyObjectIndex(componentData, "AnimClass"));
            var materialRefs = GetPropertyObjectArray(componentData, "OverrideMaterials")
                .Select(index => ResolveObjectRef(imports, exports, index))
                .Where(reference => !string.IsNullOrWhiteSpace(reference.ObjectName) || !string.IsNullOrWhiteSpace(reference.PackagePath))
                .ToList();
            var componentTags = GetPropertyStringArray(componentData, "ComponentTags");
            var knownSlot = KnownVisualSlots.Contains(slot);
            var hasMesh = !string.IsNullOrWhiteSpace(meshRef.ObjectName) || !string.IsNullOrWhiteSpace(meshRef.PackagePath);
            var semanticKind = PartRecipeService.SemanticKind(slot, meshRef.ObjectPath, meshRef.ObjectName, componentTags);

            if (!knownSlot && !hasMesh && materialRefs.Count == 0)
            {
                continue;
            }

            output.Add(new NativeSuitPartRecord
            {
                SourcePackagePath = sourcePackagePath,
                SourceUasset = assetPath,
                ContentRelativePath = contentRelative,
                CharacterFolder = characterFolder,
                Stem = stem,
                Context = context,
                Slot = slot,
                ComponentClass = componentClass,
                ComponentTemplateExport = GetString(componentTemplate, "ObjectName"),
                ComponentTemplateExportIndex = componentTemplateIndex,
                ScsNodeExport = GetString(export, "ObjectName"),
                ScsNodeExportIndex = exportIndex + 1,
                ParentComponentOrVariableName = GetPropertyValueString(data, "ParentComponentOrVariableName"),
                AttachSocket = GetPropertyValueString(data, "AttachToName"),
                MeshKind = meshKind,
                MeshObjectName = meshRef.ObjectName,
                MeshPackagePath = meshRef.PackagePath,
                MeshObjectPath = meshRef.ObjectPath,
                AnimClassObjectName = animClassRef.ObjectName,
                AnimClassPackagePath = animClassRef.PackagePath,
                AnimClassObjectPath = animClassRef.ObjectPath,
                Materials = materialRefs,
                ComponentTags = componentTags,
                HasClassChildProperty = classChildSlots.Contains(slot),
                IsKnownVisualSlot = knownSlot,
                // Keep indexing uncommon authored attachments too (Batpack, Collar,
                // Spine, SM_* props, etc.). Only the root/body SCS nodes are excluded;
                // every other mesh-bearing component gets a recipe the user can inspect
                // and attempt to graft.
                IsLikelyGraftCandidate = hasMesh && !IsNonVisualRootSlot(slot),
                SemanticKind = semanticKind,
                TemplatePackagePath = sourcePackagePath,
                TemplateUasset = assetPath,
                TemplateSlot = slot,
                TemplateComponentClass = componentClass,
                RecipeKey = "",
                Notes = BuildNotes(context, slot, componentClass)
            });
        }

        foreach (var part in output)
        {
            part.RecipeKey = PartRecipeService.BuildRecipeKey(part);
        }

        return output;
    }

    private static bool IsNonVisualRootSlot(string slot) =>
        slot.Equals("DefaultSceneRoot", StringComparison.OrdinalIgnoreCase) ||
        slot.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
        slot.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
        slot.Contains("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
        slot.Equals("TtCharacterAssetMinion", StringComparison.OrdinalIgnoreCase) ||
        slot.Equals("WubDialogueVoiceActor", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ExtractClassChildSlots(List<JsonElement> exports)
    {
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var export in exports)
        {
            if (!GetString(export, "$type").Contains("ClassExport", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!export.TryGetProperty("LoadedProperties", out var loadedProperties) ||
                loadedProperties.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var property in loadedProperties.EnumerateArray())
            {
                var name = GetString(property, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    slots.Add(name);
                }
            }
        }

        return slots;
    }

    private static NativeSuitObjectRef ResolveFirstMesh(List<JsonElement> imports, List<JsonElement> exports, List<JsonElement> componentData, out string meshKind)
    {
        foreach (var candidate in new[] { "StaticMesh", "SkeletalMesh", "SkinnedAsset" })
        {
            var objectIndex = GetPropertyObjectIndex(componentData, candidate);
            if (objectIndex == 0)
            {
                continue;
            }

            meshKind = candidate;
            return ResolveObjectRef(imports, exports, objectIndex);
        }

        meshKind = "";
        return new NativeSuitObjectRef();
    }

    private static NativeSuitObjectRef ResolveObjectRef(List<JsonElement> imports, List<JsonElement> exports, int objectIndex)
    {
        if (objectIndex == 0)
        {
            return new NativeSuitObjectRef();
        }

        if (objectIndex > 0)
        {
            if (objectIndex > exports.Count)
            {
                return new NativeSuitObjectRef { ObjectName = $"<invalid export {objectIndex}>" };
            }

            var export = exports[objectIndex - 1];
            var objectName = GetString(export, "ObjectName");
            return new NativeSuitObjectRef
            {
                ObjectName = objectName,
                ObjectPath = objectName,
                ClassName = ResolveObjectRef(imports, exports, GetInt(export, "ClassIndex")).ObjectName
            };
        }

        var importIndex = -objectIndex;
        if (importIndex <= 0 || importIndex > imports.Count)
        {
            return new NativeSuitObjectRef { ObjectName = $"<invalid import {importIndex}>" };
        }

        var import = imports[importIndex - 1];
        var name = GetString(import, "ObjectName");
        var packagePath = ResolveImportPackagePath(imports, GetInt(import, "OuterIndex"));
        return new NativeSuitObjectRef
        {
            ObjectName = name,
            PackagePath = packagePath,
            ObjectPath = BuildObjectPath(packagePath, name),
            ClassName = GetString(import, "ClassName")
        };
    }

    private static string ResolveImportPackagePath(List<JsonElement> imports, int outerIndex)
    {
        if (outerIndex >= 0)
        {
            return "";
        }

        var importIndex = -outerIndex;
        if (importIndex <= 0 || importIndex > imports.Count)
        {
            return "";
        }

        var import = imports[importIndex - 1];
        var name = GetString(import, "ObjectName");
        if (name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return ResolveImportPackagePath(imports, GetInt(import, "OuterIndex"));
    }

    private static string BuildObjectPath(string packagePath, string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return packagePath;
        }

        if (objectName.StartsWith("/", StringComparison.Ordinal))
        {
            return objectName;
        }

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return objectName;
        }

        return packagePath + "." + objectName;
    }

    private static string BuildNotes(string context, string slot, string componentClass)
    {
        if (slot.Equals("Head", StringComparison.OrdinalIgnoreCase) &&
            componentClass.Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase))
        {
            return "Static head attachment; this covers hair pieces like Thomas slickback.";
        }

        if (slot.Equals("Torso2", StringComparison.OrdinalIgnoreCase))
        {
            return "Extra torso/chest attachment; good first graft target.";
        }

        if (context.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
        {
            return "Cutscene component shape; do not blindly copy to playable.";
        }

        return "";
    }

    private static string DetermineContext(string stem)
    {
        var lower = stem.ToLowerInvariant();
        if (lower.Contains("cutscene", StringComparison.Ordinal) ||
            lower.EndsWith("_cut", StringComparison.Ordinal) ||
            lower.Contains("_cut_", StringComparison.Ordinal))
        {
            return "cutscene";
        }

        if (lower.Contains("batcave", StringComparison.Ordinal))
        {
            return "batcave";
        }

        // Quest characters are appearance donors, not gameplay donors. Their concrete BP still
        // supplies the playable-side visual recipe; the visual-base flow deliberately reuses that
        // recipe for cutscene when no dedicated counterpart exists.
        if (lower.EndsWith("_quest", StringComparison.Ordinal))
        {
            return "playable";
        }

        return "playable";
    }

    private static string GetCharacterFolder(string assetPath, string contentRoot)
    {
        var directory = Path.GetDirectoryName(assetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "";
        }

        var segments = Path.GetRelativePath(contentRoot, directory)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        var charactersIndex = Array.FindIndex(segments, segment =>
            segment.Equals("Characters", StringComparison.OrdinalIgnoreCase));
        return charactersIndex >= 0 &&
               charactersIndex + 2 < segments.Length &&
               CharacterRigFolders.Contains(segments[charactersIndex + 1], StringComparer.OrdinalIgnoreCase)
            ? segments[charactersIndex + 2]
            : segments.LastOrDefault() ?? "";
    }

    private static string PackagePathFromContentPath(string assetPath, string contentRoot)
    {
        var relative = Path.GetRelativePath(contentRoot, assetPath);
        var noExtension = Path.ChangeExtension(relative, null);
        return "/Game/" + noExtension.Replace('\\', '/');
    }

    private static List<JsonElement> GetArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<JsonElement>();
        }

        return value.EnumerateArray().ToList();
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt32(out var number) ? number : 0;
    }

    private static JsonElement? FindProperty(List<JsonElement> properties, string propertyName)
    {
        foreach (var property in properties)
        {
            if (GetString(property, "Name").Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static string GetPropertyValueString(List<JsonElement> properties, string propertyName)
    {
        var property = FindProperty(properties, propertyName);
        if (property is null || !property.Value.TryGetProperty("Value", out var value))
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static int GetPropertyObjectIndex(List<JsonElement> properties, string propertyName)
    {
        var property = FindProperty(properties, propertyName);
        if (property is null || !property.Value.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt32(out var index) ? index : 0;
    }

    private static List<int> GetPropertyObjectArray(List<JsonElement> properties, string propertyName)
    {
        var property = FindProperty(properties, propertyName);
        if (property is null || !property.Value.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<int>();
        }

        var output = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.TryGetProperty("Value", out var itemValue) &&
                itemValue.ValueKind == JsonValueKind.Number &&
                itemValue.TryGetInt32(out var objectIndex))
            {
                output.Add(objectIndex);
            }
        }

        return output;
    }

    private static List<string> GetPropertyStringArray(List<JsonElement> properties, string propertyName)
    {
        var property = FindProperty(properties, propertyName);
        if (property is null || !property.Value.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var output = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.TryGetProperty("Value", out var itemValue))
            {
                var text = itemValue.ValueKind == JsonValueKind.String ? itemValue.GetString() : itemValue.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    output.Add(text!);
                }
            }
        }

        return output;
    }

    private static string FindDefaultExtractedContentRoot()
    {
        return AppSettings.Current.EffectiveExtractedContentRoot();
    }

    private string? FindDefaultMappingsPath()
    {
        return AppSettings.Current.EffectiveUsmapPath();
    }
}
