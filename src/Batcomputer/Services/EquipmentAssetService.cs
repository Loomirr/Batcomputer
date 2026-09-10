using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Inspects serialized equipment references. Never infers mesh support from a gadget name.</summary>
public static class EquipmentAssetService
{
    // Serialized visual dependencies outside Characters/Models/Gadgets. Keep this
    // scoped to equipment, not all Props, UI, VFX or story levels.
    public static IReadOnlyList<string> ExtractionFilters { get; } = new[] {
        "VFX/Mechanic/Gadgets/Batarang/Emitters/NS_Batarang_SuccessfulHit",
        "UI/Icons/Gadgets/", "UI/Icons/GadgetUpgrades/", "UI/Global/M_UI_SDFIconGadget",
        "UI/Hud/Art/Reticles/", "UI/Hud/Art/T_UI_RetRubberBullet",
        "Models/Bolts/SM_Ducktank_Rocket", "Models/Bolts/SM_RocketLauncher_Bolt",
        "Models/GangWeapons/Redhood/SM_RedhoodRocketLauncher",
        "Models/Props/Materials/Mi_LEGO_RevolverBake", "Models/Props/SK_Penguin_Umbrella",
        "Models/Props/SM_BaseBall", "Models/Props/SM_BruiserBruteHammer", "Models/Props/SM_BruiserBulwarkRiotShield",
        "Models/Props/SM_EMP_Grenade", "Models/Props/SM_Katana", "Models/Props/SM_Penguin",
        "Models/Props/SM_Pistol", "Models/Props/SM_Revolver", "Models/Props/SM_RocketLauncher",
        "Models/Props/SM_Shield", "Models/Props/SM_Shotgun", "Models/Props/SM_SmokeBomb",
        "Models/Props/SM_SniperRifle_02", "Models/Props/SM_StunBaton", "Models/Props/SM_UmbrellaClosed",
        "PlaceHolder/Gadgets/Blowpipe/Meshes/Darts/SM_Blowpipe_Dart_Sleep",
        "VFX/Collectables/RedBricks/Vehicles/Meshes/SM_RedBrick_Police_RubberBullet",
        "VFX/Common/Meshes/SM_Sphere", "VFX/Character/Combat/Penguin/Materials/M_FishRocket_Pulse",
        "VFX/Character/Combat/Penguin/Materials/MI_FishRocket_Pulse_Gradient",
        "VFX/Character/Combat/PistolGoon/Materials/M_Projectile_Heat",
        "VFX/Character/Combat/PistolGoon/Materials/MI_RubberBulletPistol_Projectile_Heat",
        "VFX/Mechanic/Gadgets/Batarang/Materials/M_Batarang_Upgrades",
        "VFX/Mechanic/Gadgets/FoamGun/Materials/MI_FoamGun_AmmoLiquid",
        "VFX/Mechanic/Gadgets/NinjaTeleport/Materials/M_NinjaTeleport_Marker",
        "Art/Lighting/Lego_Lights/LightGlows/LEGO/Sm_Light_P58176",
        "Art/Lighting/Lego_Lights/LightGlows/LEGO/Sm_Light_P98138",
        "Art/Lighting/Lego_Lights/LightGlows/Materials/MI_LEGO_LightGlow_01_CPD_TD",
        "Art/Materials/BlockOut/MI_Emissive_Purple",
        "Global/Materials/LEGO_Material_Library/Project/Tech_Design/Material_Instances/Mi_LEGO_TD_Transp_CPD",
        "LEGOGameplay/GenericProps/BatCave/BatcaveCrates/BatcaveCrateLarge/Setup/SM_BatcaveCrateLarge",
        "Levels/Story/Chapter_04/M06_Observatory/MrFreeze/Blueprints/Assets/SM_Ice_Puck",
        "Levels/Story/Chapter_06/M01_StreetsOfLeague/Talia/Setup/Talia_EMP/SM_Talia_Emp_Bomb"
    }.Select(path => "Content/" + path).ToArray();

    public static IReadOnlyList<string> Discover(string contentRoot) => ExtractedPackagePathService.EnumerateMounts(contentRoot)
        .SelectMany(mount => Directory.EnumerateFiles(mount.ContentRoot, "DA_ETA_*.uasset", SearchOption.AllDirectories)
            .Select(file => mount.PackageRoot + "/" + Path.GetRelativePath(mount.ContentRoot, file)[..^7].Replace('\\', '/')))
        .Where(path => !path.Contains("/Mods/", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static EquipmentAssetProfile Inspect(string contentRoot, string mappingPath, string etaPackage)
    {
        var mappings = MappingsCache.Load(mappingPath);
        var result = new EquipmentAssetProfile { EtaPackage = etaPackage };
        var pending = new Queue<string>(); pending.Enqueue(etaPackage);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var package))
        {
            if (!seen.Add(package)) continue;
            if (seen.Count > 128) throw new InvalidDataException("Equipment graph exceeds the safe inspection limit (128 packages).");
            var asset = Read(contentRoot, package, mappings);
            result.OwnedGraph.Add(package);
            foreach (var export in asset.Exports.OfType<NormalExport>())
            foreach (var (path, property) in Properties(export.Data))
            {
                var reference = Reference(asset, property);
                if (reference is not { } r || !ExtractedPackagePathService.IsContentPackagePath(r.Package)) continue;
                var stem = UnrealPathUtil.AssetName(r.Package);
                if (package.Equals(etaPackage, StringComparison.OrdinalIgnoreCase) && path == "Equipment")
                    result.DefinitionPackage = r.Package;
                // Clone data/actor edges only. Ability sets, animation graphs, gameplay effects,
                // audio and Niagara stay native; blindly cloning those breaks their shared contracts.
                if (stem.StartsWith("BP_", StringComparison.Ordinal) ||
                    (stem.StartsWith("DA_", StringComparison.Ordinal) && path.Contains("Projectile", StringComparison.OrdinalIgnoreCase)))
                {
                    if (r.Class is "BlueprintGeneratedClass" or "soft" || path.Contains("Projectile", StringComparison.OrdinalIgnoreCase))
                        pending.Enqueue(r.Package);
                    continue;
                }
                var kind = r.Class;
                if (kind == "soft" && (path.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("Reticle", StringComparison.OrdinalIgnoreCase)))
                {
                    try { kind = Read(contentRoot, r.Package, mappings).Exports.First(e => e.GetExportClassType()?.ToString() is "Texture2D" or "MaterialInstanceConstant" or "Material").GetExportClassType()!.ToString(); }
                    catch { result.Warnings.Add("Icon dependency not extracted/readable: " + r.Package); }
                }
                if (kind is not ("StaticMesh" or "SkeletalMesh" or "MaterialInstanceConstant" or "Material" or "Texture2D" or "NiagaraSystem" or "ParticleSystem" or "WubAudioEvent")) continue;
                // HUD materials wrap the actual icon texture. Clone that small MI too,
                // exposing its texture parameter instead of leaving PNG replacement disconnected.
                if (kind == "MaterialInstanceConstant" && path.Contains("Icon", StringComparison.OrdinalIgnoreCase)) pending.Enqueue(r.Package);
                var category = kind switch {
                    "NiagaraSystem" or "ParticleSystem" => "Effects (native, read-only)",
                    "WubAudioEvent" => "Audio (native, read-only)",
                    "SkeletalMesh" => "Skinned model (native rig)",
                    "Texture2D" => "Icons / textures",
                    "Material" or "MaterialInstanceConstant" => "Materials / icon materials",
                    _ => package.Contains("Projectile", StringComparison.OrdinalIgnoreCase) ? "Projectile models" : "Held / attached models"
                };
                result.Parts.Add(new() { OwnerPackage = package, ExportName = export.ObjectName.ToString(),
                    PropertyPath = path, Package = r.Package, AssetClass = kind, Category = category });
            }
        }
        if (string.IsNullOrWhiteSpace(result.DefinitionPackage)) throw new InvalidDataException("Equipment lookup has no readable Equipment definition.");
        if (result.Parts.Any(p => p.AssetClass == "SkeletalMesh")) result.Warnings.Add("This gadget contains skinned components; static OBJ edits cannot replace those components.");
        result.Warnings.Add("Native abilities, effects, sockets, damage and upgrades remain linked. OBJ baking uses the existing static-mesh donor collision shell, not fitted collision. Projectile behavior is not retuned.");
        return result;
    }

    internal static UAsset Read(string root, string package, Usmap mappings) => ReadRaw(root, package, mappings, CustomSerializationFlags.None);

    internal static UAsset ReadRaw(string root, string package, Usmap mappings, CustomSerializationFlags flags)
    {
        if (!HeldItemService.ValidPackage(package)) throw new InvalidDataException("Invalid equipment package: " + package);
        var file = ExtractedPackagePathService.ResolvePackageUasset(root, package);
        if (file is null || !File.Exists(file)) throw new FileNotFoundException("Equipment dependency is missing; run Full refresh: " + package);
        using var bytes = new MemoryStream();
        using (var source = File.OpenRead(file)) source.CopyTo(bytes);
        var split = File.Exists(Path.ChangeExtension(file, ".uexp"));
        if (split) { using var source = File.OpenRead(Path.ChangeExtension(file, ".uexp")); source.CopyTo(bytes); }
        bytes.Position = 0;
        using var reader = new AssetBinaryReader(bytes);
        return new UAsset(reader, EngineVersion.VER_UE5_6, mappings, split, flags);
    }

    internal static IEnumerable<(string Path, PropertyData Property)> Properties(IEnumerable<PropertyData> properties, string prefix = "")
    {
        foreach (var p in properties)
        {
            var path = prefix + p.Name;
            yield return (path, p);
            if (p is StructPropertyData s) foreach (var child in Properties(s.Value, path + ".")) yield return child;
            if (p is ArrayPropertyData a) for (int i = 0; i < a.Value.Length; i++)
                foreach (var child in Properties([a.Value[i]], path + $"[{i}].")) yield return child;
            if (p is MapPropertyData m)
            {
                int i = 0;
                foreach (var pair in m.Value)
                {
                    foreach (var child in Properties([pair.Key], path + $"[{i}].Key.")) yield return child;
                    foreach (var child in Properties([pair.Value], path + $"[{i++}].Value.")) yield return child;
                }
            }
        }
    }

    internal static (string Package, string Object, string Class)? Reference(UAsset asset, PropertyData property)
    {
        if (property is SoftObjectPropertyData soft)
            return (soft.Value.AssetPath.PackageName.ToString(), soft.Value.AssetPath.AssetName.ToString(), "soft");
        if (property is not ObjectPropertyData obj || !obj.Value.IsImport()) return null;
        var import = obj.Value.ToImport(asset); var current = import;
        var seen = new HashSet<int>();
        while (current.OuterIndex.IsImport() && seen.Add(current.OuterIndex.Index)) current = current.OuterIndex.ToImport(asset);
        return (current.ObjectName.ToString(), import.ObjectName.ToString(), import.ClassName.ToString());
    }
}
