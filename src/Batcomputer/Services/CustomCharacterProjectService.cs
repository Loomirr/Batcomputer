using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Explicit character ownership; never infer a new owner from a foreign pawn tag.</summary>
public static class CustomCharacterProjectService
{
    public static bool IsCharacter(NativeSuitProject? project) => project?.CustomCharacter?.IsDefinition == true;
    public static bool IsIdentifier(string? id) => id is not null && Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9]{0,63}$");
    public static string Scope(CustomCharacterIdentity identity) => "Pawns.Playable." + identity.CharacterId;
    public static string PawnTag(CustomCharacterIdentity identity) => Scope(identity) + "." + identity.VariantId;
    public static string ProgressTag(CustomCharacterIdentity identity) =>
        $"GameProgress.Definitions.Characters.{identity.CharacterId}.{identity.VariantId}";

    public static string? IdentityError(NativeSuitProject project, string? donorPawnTag = null)
    {
        var identity = project.CustomCharacter;
        if (identity is null)
            return PawnTagConfigService.CharacterOwnerMismatchError(project.PawnTag, donorPawnTag);
        if (!IsIdentifier(identity.CharacterId) || !IsIdentifier(identity.VariantId) ||
            string.IsNullOrWhiteSpace(identity.DefinitionSlotId))
            return "The custom character identity is incomplete. Create it through Characters, or choose a saved character as the suit's base.";
        if (identity.IsDefinition && (identity.DefinitionSlotId != project.SlotId || identity.VariantId != identity.CharacterId))
            return "A character definition must own its default variant and its saved project ID.";
        if (!identity.IsDefinition && identity.DefinitionSlotId.Equals(project.SlotId, StringComparison.OrdinalIgnoreCase))
            return "A character suit cannot use itself as its character definition.";
        if (project.PawnTag != PawnTag(identity) || project.ProgressTag != ProgressTag(identity))
            return "The pawn/progress tags do not match the saved custom character identity. Reopen Character identity to review the owner; do not change these tags independently.";
        if (Scope(identity).Equals(PawnTagConfigService.CharacterScopeForPawnTag(donorPawnTag), StringComparison.OrdinalIgnoreCase))
            return "A new character cannot reuse its native gameplay donor's character group.";
        return null;
    }

    public static void ApplyIdentity(NativeSuitProject project)
    {
        if (project.CustomCharacter is not { } identity) return;
        project.PawnTag = PawnTag(identity);
        project.ProgressTag = ProgressTag(identity);
        project.LockedDescription = "";
    }

    public static bool IsNativeOwner(string id)
    {
        if (GameDataService.Instance.Db.Families.Any(family => family.Name.Equals(id, StringComparison.OrdinalIgnoreCase))) return true;
        var groups = Path.Combine(AppSettings.Current.EffectiveExtractedContentRoot(), "Characters", "MetaData", "Groups");
        return Directory.Exists(groups) && Directory.EnumerateFiles(groups, "DA_CharacterGroup_*.uasset", SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetFileNameWithoutExtension(path)["DA_CharacterGroup_".Length..].Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public static void RequireAvailableIdentity(NativeSuitProject candidate, SuitProjectService service)
    {
        var identity = candidate.CustomCharacter ?? throw new InvalidDataException("Missing character identity.");
        foreach (var path in CreationOutputDirectories(candidate, service))
            if (Directory.Exists(path)) throw new InvalidDataException("That identity already has generated assets. Choose another ID or recover the earlier project: " + path);
        if (IsNativeOwner(identity.CharacterId)) throw new InvalidDataException("That character ID belongs to a native character. Choose a new, unique ID.");
        foreach (var summary in service.ListProjectFiles())
        {
            var other = service.LoadProject(summary.Path);
            if (other is null) continue;
            if (candidate.PawnTag.Equals(other.PawnTag, StringComparison.OrdinalIgnoreCase) ||
                (identity.IsDefinition && PawnTagConfigService.CharacterScopeForPawnTag(other.PawnTag).Equals(Scope(identity), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"That character/variant identity is already used by '{other.DisplayName}'. Choose another ID.");
        }
    }

    private static string[] CreationOutputDirectories(NativeSuitProject project, SuitProjectService service) =>
    [
        Path.GetFullPath(service.ProjectOutputDirectory(project)),
        Below(Path.Combine(AppSettings.GeneratedRootFor(service.ProjectRoot), "TextureImports"), project.SlotId),
        Below(new ToolMaterialLibraryService(service.ProjectRoot).ContentRoot, OwnedRoot(project)[6..])
    ];

    public static string? ArchiveFailedCreation(NativeSuitProject project, SuitProjectService service)
    {
        // Called only after the caller established that all three destinations were absent.
        // A successfully saved recipe keeps its sources so the user can retry the normal stage.
        if (File.Exists(service.ProjectPathForSlot(project.SlotId))) return null;
        var generated = Path.GetFullPath(AppSettings.GeneratedRootFor(service.ProjectRoot));
        var paths = CreationOutputDirectories(project, service);
        foreach (var path in paths)
            if (!path.StartsWith(generated.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refused to archive a character output outside its generated workspace.");
        if (!paths.Any(Directory.Exists)) return null;
        var recovery = Below(generated, "FailedCharacterImports/" + project.SlotId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recovery);
        for (int i = 0; i < paths.Length; i++)
            if (Directory.Exists(paths[i])) Directory.Move(paths[i], Path.Combine(recovery, i.ToString("D2")));
        return recovery;
    }

    public static NativeSuitProject CreateRecipe(NativeSuitProject? source, string name, string characterId,
        string variantId, string definitionSlotId = "")
    {
        if (!IsIdentifier(characterId) || !IsIdentifier(variantId))
            throw new InvalidDataException("IDs must start with a letter and contain only letters and numbers (maximum 64 characters).");
        var project = source is null ? new NativeSuitProject() :
            JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(source))!;
        var definition = string.IsNullOrEmpty(definitionSlotId);
        if (definition) variantId = characterId;
        project.SlotId = $"character_{characterId.ToLowerInvariant()}_{variantId.ToLowerInvariant()}";
        project.DisplayName = name.Trim();
        project.CustomCharacter = new()
        {
            IsDefinition = definition, CharacterId = characterId, VariantId = variantId,
            DefinitionSlotId = definition ? project.SlotId : definitionSlotId,
            SymbolPackage = definition ? "" : source?.CustomCharacter?.SymbolPackage ?? ""
        };
        var stem = $"CC_{characterId}_{variantId}";
        var root = $"/Game/Mods/{stem}/Characters";
        project.TargetPackages = new()
        {
            Playable = $"{root}/BP_{stem}_Playable", Cutscene = $"{root}/BP_{stem}_Cutscene",
            Dcmd = $"{root}/DA_DCMD_{stem}_Playable"
        };
        project.PackageBaseName = stem + "_P";
        ApplyIdentity(project);
        if (source is not null && OwnedRoot(source) is { Length: > 0 } oldRoot)
        {
            var node = JsonSerializer.SerializeToNode(project)!;
            RewriteOwnedStrings(node, oldRoot, OwnedRoot(project));
            project = node.Deserialize<NativeSuitProject>()!;
        }
        return project;
    }

    private static string OwnedRoot(NativeSuitProject project) =>
        Regex.Match(project.TargetPackages.Playable, "^/Game/Mods/[^/]+").Value;

    private static void RewriteOwnedStrings(JsonNode node, string from, string to)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[pair.Key] = text.Replace(from + "/", to + "/", StringComparison.Ordinal);
                else if (pair.Value is not null) RewriteOwnedStrings(pair.Value, from, to);
            }
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[i] = text.Replace(from + "/", to + "/", StringComparison.Ordinal);
                else if (array[i] is { } child) RewriteOwnedStrings(child, from, to);
            }
    }

    /// <summary>Native donor templates and tool-library assets remain references. Copy project-owned
    /// model sources so editing/reimporting a new character never writes into the source suit folder.</summary>
    public static void CopyAuthoringSources(NativeSuitProject source, NativeSuitProject target, SuitProjectService service)
    {
        var from = Path.GetFullPath(service.ProjectOutputDirectory(source));
        var to = Path.GetFullPath(service.ProjectOutputDirectory(target));
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source and new project must differ.");
        foreach (var relative in source.CustomStaticMeshes.Select(mesh => mesh.SourceObjRelativePath)
                     .Concat(source.SkinnedMeshes.Concat(EquipmentSkinnedModelService.Models(source)).SelectMany(mesh => new[] { mesh.SourceRelativePath, mesh.CacheRelativePath }))
                     .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var input = Below(from, relative);
            var output = Below(to, relative);
            if (File.Exists(input)) CopyFile(input, output);
            else if (Directory.Exists(input))
                foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                    CopyFile(file, Below(output, Path.GetRelativePath(input, file)));
            else throw new FileNotFoundException("A saved model source/cache is missing. Repair it in the source project before copying.", input);
        }
        if (File.Exists(source.CoverImagePath))
        {
            target.CoverImagePath = Path.Combine(to, "cover" + Path.GetExtension(source.CoverImagePath));
            CopyFile(source.CoverImagePath, target.CoverImagePath);
        }
        // Custom OBJ geometry is regenerated from each project's alignment settings. Give it its
        // own output package while leaving shared material/texture library references untouched.
        foreach (var mesh in target.CustomStaticMeshes)
            mesh.MeshPackagePath = $"/Game/Mods/CC_{target.CustomCharacter!.CharacterId}_{target.CustomCharacter.VariantId}/Meshes/SM_Custom_{mesh.Id}";
    }

    public static void CopyVisualAssets(NativeSuitProject source, NativeSuitProject target, SuitProjectService service, Action<string> log)
    {
        var oldRoot = OwnedRoot(source); var newRoot = OwnedRoot(target);
        if (string.IsNullOrWhiteSpace(oldRoot) || oldRoot == newRoot)
            throw new InvalidDataException("The source needs a distinct mod-owned asset root.");
        var directory = service.ProjectOutputDirectory(target);
        var library = new ToolMaterialLibraryService(service.ProjectRoot);
        var scratch = Path.Combine(directory, "CopiedMaterialSources", "Content");
        var regenerated = new Dictionary<string, (GeneratedTextureEntry Texture, string PackageBase)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < target.GeneratedTextures.Count; i++)
        {
            var texture = target.GeneratedTextures[i];
            if (!File.Exists(texture.SourcePng)) throw new FileNotFoundException("Reimport this texture's source image in the original suit before copying it: " + texture.DisplayName, texture.SourcePng);
            var png = Below(directory, $"CopiedTextureSources/{i:D3}{Path.GetExtension(texture.SourcePng)}");
            CopyFile(texture.SourcePng, png); texture.SourcePng = png;
            texture.OutputRoot = Below(Path.Combine(AppSettings.GeneratedRootFor(service.ProjectRoot), "TextureImports"), $"{target.SlotId}/{i:D3}");
            texture.IoStoreRoot = "";
            var cook = new TextureCookService(service.ProjectRoot).Cook(new()
            {
                SourceImagePath = texture.SourcePng, TemplateJsonPath = texture.TemplateJson,
                OutputContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content"),
                OutputPackagePath = texture.PackagePath,
                NearestNeighborMips = MainForm.UseNearestNeighborMipsForTextureKind(texture.Kind, texture.CookProfile),
                BleedTransparentRgb = MainForm.IsUiTextureKind(texture.Kind), WriteInlineMips = true,
                Bc7InputLayout = "rgba", Bc7Quality = MainForm.IsNativeUimdIconCookProfile(texture.CookProfile) ? "best" : "balanced"
            });
            if (cook.Status != "created") throw new InvalidDataException("Texture copy could not be cooked: " + texture.DisplayName + ". " + cook.Error);
            var cookedBase = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content", texture.PackagePath[6..].Replace('/', Path.DirectorySeparatorChar));
            if (!MainForm.ValidateGeneratedTextureCook(texture, cookedBase, out var reason))
                throw new InvalidDataException($"Texture '{texture.DisplayName}' could not be certified after copying: {reason}");
            if (texture.PackagePath.StartsWith(newRoot + "/", StringComparison.Ordinal))
            {
                regenerated.Add(oldRoot + texture.PackagePath[newRoot.Length..], (texture, cookedBase));
                // The new recipe is saved only after the entire copy succeeds. Supply its fresh
                // certified bytes to material registration without publishing an incomplete recipe.
                var archivedBase = Path.Combine(library.ContentRoot, texture.PackagePath[6..].Replace('/', Path.DirectorySeparatorChar));
                foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
                    if (File.Exists(cookedBase + extension)) CopyFile(cookedBase + extension, archivedBase + extension);
            }
            log("Copied and cooked texture: " + texture.DisplayName);
        }
        library.CopyCharacterMaterialSources(VisualCopyRoots(source), scratch, regenerated);
        if (Directory.Exists(scratch))
        foreach (var file in Directory.EnumerateFiles(scratch, "*.uasset", SearchOption.AllDirectories))
        {
            var package = "/Game/" + Path.GetRelativePath(scratch, file)[..^7].Replace('\\', '/');
            if (!package.StartsWith(oldRoot + "/", StringComparison.Ordinal)) continue;
            var destinationPackage = newRoot + package[oldRoot.Length..];
            var destination = Path.Combine(library.ContentRoot, destinationPackage[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
                if (File.Exists(Path.ChangeExtension(file, ext))) File.Copy(Path.ChangeExtension(file, ext), Path.ChangeExtension(destination, ext), false);
            RepointCopiedPackage(destination, oldRoot, newRoot);
        }
        foreach (var mesh in target.SkinnedMeshes.Concat(EquipmentSkinnedModelService.Models(target)))
        {
            var cache = Below(directory, mesh.CacheRelativePath);
            var manifestFile = Path.Combine(cache, "validated.json");
            var manifest = JsonSerializer.Deserialize<SkinnedMeshCookService.CookManifest>(File.ReadAllText(manifestFile))
                ?? throw new InvalidDataException("Missing validated skinned cache.");
            RepointCopiedPackage(Path.Combine(cache, "mesh.uasset"), oldRoot, newRoot);
            var hashes = manifest.Files.Keys.ToDictionary(file => file, file => SkinnedMeshCookService.Hash(Path.Combine(cache, file)));
            manifest = manifest with { Package = mesh.MeshPackage, Files = hashes,
                TemporarySkeleton = manifest.TemporarySkeleton.Replace(oldRoot + "/", newRoot + "/", StringComparison.Ordinal) };
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest));
            SkinnedMeshStageService.ReadManifest(directory, mesh);
        }
        library.Register(target.GeneratedMaterials);
    }

    internal static IReadOnlyList<string> VisualCopyRoots(NativeSuitProject source)
    {
        var items = HeldItemService.Resolve(source.AbilityLoadout);
        var equipmentParts = source.EquipmentSlots.Where(slot => slot.Custom is not null)
            .SelectMany(slot => EquipmentHudIconService.ActiveCopyParts(slot.Custom!));
        // Weapon geometry is embedded in the JSON recipe. Its materials, including older recipes
        // which no longer list them in GeneratedMaterials, still need independent package closures.
        return source.GeneratedMaterials.Select(material => material.PackagePath)
            .Concat(source.MaterialAssignments.Select(material => material.MiPackagePath))
            .Concat(source.CustomStaticMeshes.SelectMany(mesh => mesh.MaterialSlots.Select(material => material.MaterialPath).Append(mesh.MaterialPath)))
            .Concat(source.SkinnedMeshes.Concat(EquipmentSkinnedModelService.Models(source)).SelectMany(mesh => mesh.Materials.Select(material => material.MaterialPath)))
            .Concat(items.SelectMany(item => (item.CustomModel?.Materials ?? []).Select(material => material.MaterialPath).Append(item.MaterialPackage)))
            .Concat(equipmentParts.SelectMany(part => (part.Model?.Materials ?? []).Select(material => material.MaterialPath).Append(part.ReplacementPackage)))
            .Concat(source.EquipmentSlots.Select(slot => slot.Custom?.HudIcon?.SdfPackage ?? ""))
            .Append(source.GliderMaterial)
            .Where(package => package.StartsWith(OwnedRoot(source) + "/", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void RepointCopiedPackage(string file, string oldRoot, string newRoot)
    {
        var asset = new UAsset(file, EngineVersion.VER_UE5_6, null,
            CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
        var names = asset.GetNameMapIndexList();
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i].ToString();
            if (name.StartsWith(oldRoot + "/", StringComparison.Ordinal))
                asset.SetNameReference(i, new FString(newRoot + name[oldRoot.Length..]));
        }
        if (asset.FolderName?.ToString() is { } folder && folder.StartsWith(oldRoot + "/", StringComparison.Ordinal))
            asset.FolderName = new FString(newRoot + folder[oldRoot.Length..]);
        asset.Write(file);
    }

    private static string Below(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A project-owned source path escapes its project folder.");
        return path;
    }
    private static void CopyFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: false);
    }

    /// <summary>Build children together with their default character; never rely on another installed pak.</summary>
    public static List<ModSuitEntry> ExpandMembers(IEnumerable<ModSuitEntry> entries, ModProjectService mods, SuitProjectService suits)
    {
        var expanded = entries.Where(entry => entry.Enabled).ToList();
        foreach (var entry in expanded.ToArray())
        {
            var project = suits.LoadProject(mods.ResolveSuitProjectPath(entry));
            if (project?.CustomCharacter is not { IsDefinition: false } identity) continue;
            var path = suits.ProjectPathForSlot(identity.DefinitionSlotId);
            var definition = suits.LoadProject(path);
            if (definition?.CustomCharacter is not { IsDefinition: true } owner || owner.CharacterId != identity.CharacterId ||
                IdentityError(definition) is not null)
                throw new InvalidDataException($"'{project.DisplayName}' needs its saved character definition '{identity.DefinitionSlotId}'. Restore that character or select a valid character base.");
            if (entries.Any(member => !member.Enabled && string.Equals(mods.ResolveSuitProjectPath(member), path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"'{project.DisplayName}' requires '{definition.DisplayName}', which is disabled in this mod. Enable the character or remove its child suit.");
            if (!expanded.Any(member => string.Equals(mods.ResolveSuitProjectPath(member), path, StringComparison.OrdinalIgnoreCase)))
                expanded.Add(new() { SuitId = definition.SlotId, SuitProjectPath = mods.MakeRelativeSuitProjectPath(path), Enabled = true, MenuOrder = 0 });
        }
        return expanded;
    }
}
