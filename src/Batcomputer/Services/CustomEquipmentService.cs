using System.Security.Cryptography;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Packages isolated equipment derivatives; never rewrites shipped packages or runtime DLLs.</summary>
public static class CustomEquipmentService
{
    public static string Namespace(NativeSuitProject project)
    {
        var parts = project.TargetPackages.Playable.Split('/');
        if (parts.Length < 5 || parts[1] != "Game" || parts[2] != "Mods" || !UnrealPathUtil.IsValidIdentifier(parts[3]))
            throw new InvalidDataException("Custom equipment needs a valid suit-local /Game/Mods namespace.");
        return parts[3];
    }
    public static string Root(NativeSuitProject project, CustomEquipmentRecipe recipe)
    {
        if (!UnrealPathUtil.IsValidIdentifier(recipe.Id)) throw new InvalidDataException("Invalid custom equipment ID.");
        return $"/Game/Mods/{Namespace(project)}/Equipment/{recipe.Id}";
    }
    public static string EtaPackage(NativeSuitProject project, CustomEquipmentRecipe recipe) =>
        $"/Game/Characters/Equipment/Mods/{Namespace(project)}/DA_ETA_{Namespace(project)}_{recipe.Id}";
    public static string EquipmentTag(NativeSuitProject project, CustomEquipmentRecipe recipe) =>
        $"Equipment.Batcomputer.{Namespace(project)}.{recipe.Id}";
    public static string DefinitionPackage(NativeSuitProject project, CustomEquipmentRecipe recipe) => Root(project, recipe) + $"/BP_ED_{Namespace(project)}_{recipe.Id}";
    internal static string EffectiveEtaPackage(NativeSuitProject project, EquipmentSlotChange slot, string nativeEta) =>
        slot.Custom is null ? nativeEta : EtaPackage(project, slot.Custom);
    public static IEnumerable<PawnTagConfigService.TagRow> TagRows(NativeSuitProject project) =>
        project.EquipmentSlots.Where(s => s.Custom is not null).Select(s => new PawnTagConfigService.TagRow(
            EquipmentTag(project, s.Custom!), "Custom equipment: " + s.Custom!.Name));

    public static void Generate(NativeSuitProject project, string contentRoot, Action<string> log)
    {
        var custom = project.EquipmentSlots.Where(s => s.Custom is not null).ToArray();
        if (custom.Length == 0) return;
        if (custom.Select(s => s.Custom!.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != custom.Length)
            throw new InvalidDataException("Custom equipment IDs must be unique within a suit.");
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        var mappingPath = AppSettings.Current.EffectiveUsmapPath() ?? throw new InvalidDataException("Mappings are required.");
        var mappings = MappingsCache.Load(mappingPath);
        var mod = Namespace(project);
        foreach (var slot in custom)
        {
            var recipe = slot.Custom!;
            var native = GameDataService.Instance.FindEquipment(slot.Gadget) ?? throw new InvalidDataException("Equipment donor is not cataloged: " + slot.Gadget);
            if (!native.EtaPackage.Equals(recipe.DonorEtaPackage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Custom equipment donor changed; reopen its workshop before rebuilding.");
            var profile = EquipmentAssetService.Inspect(extracted, mappingPath, recipe.DonorEtaPackage);
            EquipmentWorkshopPolicy.RequireEditable(native, profile, GameDataService.Instance.Db);
            var root = Root(project, recipe);
            var redirects = profile.OwnedGraph.ToDictionary(p => p, p => root + "/" +
                UnrealPathUtil.AssetName(p) + "_" + mod + "_" + recipe.Id, StringComparer.OrdinalIgnoreCase);
            redirects[profile.EtaPackage] = EtaPackage(project, recipe);
            redirects[profile.DefinitionPackage] = DefinitionPackage(project, recipe);
            // Different source objects with the same FName cannot be safely rewritten globally.
            if (redirects.Keys.Select(UnrealPathUtil.AssetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != redirects.Count)
                throw new InvalidDataException("This equipment graph has ambiguous object names and needs a specialized adapter.");
            if (recipe.Parts.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != recipe.Parts.Count)
                throw new InvalidDataException("The same equipment component is customized twice.");
            foreach (var model in recipe.Parts.Where(p => p.Model is not null))
                if (recipe.Parts.Any(p => p.OwnerPackage == model.OwnerPackage && p.ExportName == model.ExportName &&
                    p.PropertyPath.StartsWith("OverrideMaterials", StringComparison.Ordinal)))
                    throw new InvalidDataException("Set custom model materials inside the 3D editor; component material overrides conflict with its slots.");
            var cloneAssets = new Dictionary<string, UAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in profile.OwnedGraph)
            {
                var asset = EquipmentAssetService.Read(extracted, source, mappings);
                Rename(asset, redirects);
                asset.FolderName = new FString(redirects[source]);
                cloneAssets[source] = asset;
            }
            foreach (var edit in recipe.Parts)
            {
                var part = profile.Parts.SingleOrDefault(p => p.Key == edit.Key) ??
                    throw new InvalidDataException("An edited equipment component is no longer present: " + edit.Key);
                if (!part.CanEdit || !part.Package.Equals(edit.OriginalPackage, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An equipment donor reference changed or is unsupported: " + edit.Key);
                var asset = cloneAssets[part.OwnerPackage];
                var export = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == RenamedObject(part.ExportName, redirects));
                var property = EquipmentAssetService.Properties(export.Data).Single(p => p.Path == edit.PropertyPath).Property;
                var replacement = edit.ReplacementPackage;
                if (edit.Model is not null)
                {
                    if (part.AssetClass != "StaticMesh") throw new InvalidDataException("OBJ recipes can only replace StaticMesh components.");
                    if (part.PropertyPath != "StaticMesh" || !export.GetExportClassType()!.ToString().Contains("StaticMeshComponent", StringComparison.Ordinal))
                        throw new InvalidDataException("This mesh reference needs a specialized adapter; only StaticMeshComponent model baking is supported.");
                    replacement = root + "/SM_Custom_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(edit.Key)))[..12];
                    WeaponModelService.Bake(edit.Model, extracted, mappingPath, contentRoot, replacement);
                    // A component's native material overrides otherwise win over the baked
                    // mesh slots (notably projectile heat/trail materials). Match the workshop.
                    var materialOverrides = export.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
                    if (materialOverrides is null)
                    {
                        materialOverrides = new ArrayPropertyData(new FName(asset, "OverrideMaterials")) { ArrayType = new FName(asset, "ObjectProperty") };
                        export.Data.Add(materialOverrides);
                    }
                    materialOverrides.Value = edit.Model.Materials.Select((material, index) =>
                    {
                        var materialPackage = UnrealPathUtil.NormalizePackagePath(material.MaterialPath);
                        var materialFile = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, materialPackage);
                        var materialAsset = EquipmentAssetService.Read(materialFile is not null && File.Exists(materialFile) ? contentRoot : extracted, materialPackage, mappings);
                        var materialType = materialAsset.Exports.First(e => e.GetExportClassType()?.ToString() is "Material" or "MaterialInstanceConstant").GetExportClassType()!.ToString();
                        var reference = SwordCombatService.Obj(asset, materialPackage, UnrealPathUtil.AssetName(materialPackage), "/Script/Engine", materialType);
                        if (!export.CreateBeforeSerializationDependencies.Contains(reference)) export.CreateBeforeSerializationDependencies.Add(reference);
                        return (PropertyData)new ObjectPropertyData(new FName(asset, index.ToString())) { Value = reference };
                    }).ToArray();
                }
                if (!HeldItemService.ValidPackage(replacement)) throw new InvalidDataException("Choose a cooked replacement package for " + edit.PropertyPath);
                UAsset replacementAsset;
                var stagedReplacement = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, replacement);
                replacementAsset = EquipmentAssetService.Read(stagedReplacement is not null && File.Exists(stagedReplacement) ? contentRoot : extracted, replacement, mappings);
                if (!replacementAsset.Exports.Any(e => e.GetExportClassType()?.ToString() == part.AssetClass))
                    throw new InvalidDataException($"Replacement for {part.PropertyPath} must be {part.AssetClass}.");
                ReplaceReference(asset, export, property, replacement, part.AssetClass);
            }
            // Retain the native behavior tags (draw/fire/animation/upgrades). Only the identity
            // linking the character lookup to its equipment definition becomes custom.
            SetTag(cloneAssets[profile.EtaPackage], "EquipmentTag", EquipmentTag(project, recipe));
            SetTag(cloneAssets[profile.DefinitionPackage], "EquipmentIdentifierTag", EquipmentTag(project, recipe));
            foreach (var (source, asset) in cloneAssets)
            {
                var package = redirects[source];
                var file = StagedPath(contentRoot, package);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                asset.Write(file);
                // Parse the emitted asset immediately. Broken cooked exports stop the build.
                var reread = EquipmentAssetService.Read(contentRoot, package, mappings);
                if (reread.Exports.Count != asset.Exports.Count) throw new InvalidDataException("Equipment clone export count changed: " + package);
                foreach (var edit in recipe.Parts.Where(p => p.OwnerPackage == source))
                {
                    var expectedExport = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == RenamedObject(edit.ExportName, redirects));
                    var actualExport = reread.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == expectedExport.ObjectName.ToString());
                    var expected = EquipmentAssetService.Reference(asset, EquipmentAssetService.Properties(expectedExport.Data).Single(p => p.Path == edit.PropertyPath).Property);
                    var actual = EquipmentAssetService.Reference(reread, EquipmentAssetService.Properties(actualExport.Data).Single(p => p.Path == edit.PropertyPath).Property);
                    if (expected != actual) throw new InvalidDataException("Custom equipment binding failed cooked roundtrip: " + edit.Key);
                }
            }
            var dcmd = StagedPath(contentRoot, project.TargetPackages.Dcmd);
            var change = new DcmdGenService(AppSettings.Current.ProjectRoot!).ReplaceEquipment(dcmd,
                [new(slot.Slot, slot.Gadget, EtaPackage(project, recipe), string.IsNullOrEmpty(native.UpgradePackage) ? null : native.UpgradePackage)]);
            if (change.Status != "ok") throw new InvalidDataException(change.Error ?? "Custom DCMD equipment replacement failed.");
            var dprd = StagedPath(contentRoot, $"/Game/Mods/{mod}/Characters/DA_DPRD_{mod}");
            var graft = new AnimGraftService().SetEquipmentSlot(dprd, slot.Slot, DefinitionPackage(project, recipe));
            if (graft.Status != "ok") throw new InvalidDataException(graft.Error ?? "Custom equipment needs a generated DPRD.");
            var equipment = new AbilityAssetMutationService().InspectDprdEquipment(dprd);
            if (!equipment.Success || equipment.Equipment.ElementAtOrDefault(slot.Slot)?.PackagePath != DefinitionPackage(project, recipe))
                throw new InvalidDataException("Custom equipment did not survive the DPRD roundtrip.");
            log($"Custom equipment '{recipe.Name}': {profile.OwnedGraph.Count} isolated assets, {recipe.Parts.Count} customized bindings; native upgrades retained.");
        }
    }

    private static string StagedPath(string root, string package)
    {
        if (!HeldItemService.ValidPackage(package) || !package.StartsWith("/Game/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid equipment output path.");
        return Path.Combine(root, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
    }
    private static void SetTag(UAsset asset, string name, string tag)
    {
        var property = asset.Exports.OfType<NormalExport>().SelectMany(e => e.Data).OfType<StructPropertyData>().Single(p => p.Name.ToString() == name);
        property.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "TagName").Value = new FName(asset, tag);
    }
    internal static void ReplaceReference(UAsset asset, NormalExport export, PropertyData property, string package, string kind)
    {
        if (property is SoftObjectPropertyData soft)
            soft.Value = new FSoftObjectPath(new FTopLevelAssetPath(new FName(asset, package), new FName(asset, UnrealPathUtil.AssetName(package))), new FString(""));
        else if (property is ObjectPropertyData obj)
        {
            var script = kind == "NiagaraSystem" ? "/Script/Niagara" : "/Script/Engine";
            var index = SwordCombatService.Obj(asset, package, UnrealPathUtil.AssetName(package), script, kind);
            obj.Value = index;
            if (!export.CreateBeforeSerializationDependencies.Contains(index)) export.CreateBeforeSerializationDependencies.Add(index);
        }
        else throw new InvalidDataException("Unsupported equipment property type.");
    }
    internal static string RenamedObject(string name, IReadOnlyDictionary<string, string> redirects)
    {
        foreach (var (source, target) in redirects)
        {
            var oldName = UnrealPathUtil.AssetName(source); var newName = UnrealPathUtil.AssetName(target);
            if (name == oldName) return newName;
            if (name == oldName + "_C") return newName + "_C";
            if (name == "Default__" + oldName + "_C") return "Default__" + newName + "_C";
        }
        return name;
    }
    private static void Rename(UAsset asset, IReadOnlyDictionary<string, string> redirects)
    {
        var names = asset.GetNameMapIndexList();
        for (int i = 0; i < names.Count; i++)
        {
            var before = names[i].ToString();
            var after = redirects.TryGetValue(before, out var path) ? path : RenamedObject(before, redirects);
            if (after != before) asset.SetNameReference(i, new FString(after));
        }
        foreach (var export in asset.Exports) export.GeneratePublicHash = true;
    }
}
