using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using System.Security.Cryptography;
using System.Text;

namespace Batcomputer;

/// <summary>Bounded Batarang upgrade adapter. Native purchases and character attribute definitions stay shared.</summary>
internal static class EquipmentUpgradeService
{
    internal const string Eta = "/Game/Characters/Equipment/Batarang/DA_ETA_Batarang";
    internal const string Instance = "/Game/Characters/Equipment/Batarang/BP_Batarang_Instance";
    private const string Base = "/Game/Characters/Equipment/Batarang/Upgrades/";
    internal const string NativeRoot = Base + "DA_UF_BatarangUpgrades";
    internal sealed record Option(string Id, string Label, string Set, string? TargetedFunction = null);
    internal static IReadOnlyList<Option> Options { get; } = new[] {
        // Keep the persisted ID for compatibility; this is Combo capacity, not Scatterang.
        new Option("multi-throw", "Batarang Combo (capacity / rapid throws)", Base + "DA_Batarang_NumberofBatarangsUpgrade"),
        new Option("concussive", "Concussive", Base + "DA_Batarang_ConcussiveUpgrade", Base + "ConcussiveBatarang/BP_UF_ConcussiveBatarangEquipmentUpgrade"),
        new Option("stealthy-stun", "Stealthy stun", Base + "DA_Batarang_StealthyStunUpgrade"),
        new Option("speedy-stun", "Speedy stun", Base + "DA_Batarang_SpeedyStunUpgrade"),
        new Option("scatterangs", "Scatterang (simultaneous throws / three targets)", Base + "DA_Batarang_ScatterangsUpgrade"),
        new Option("alarmarang", "Alarmarang", Base + "DA_Batarang_AlarmarangUpgrade", Base + "Alarmarang/BP_UF_AlarmarangEquipmentUpgrade"),
        new Option("extra-damage", "Extra damage", Base + "DA_Batarang_ExtraDamageUpgrade"),
        new Option("bat-swarm", "Bat Swarm (focus throw)", Base + "DA_Batarang_BatSwarmUpgrade")
    };
    internal static bool Supported(CustomEquipmentRecipe recipe) => recipe.DonorEtaPackage == Eta;
    internal static HashSet<string> Disabled(CustomEquipmentRecipe recipe)
    {
        var values = recipe.DisabledUpgrades ?? [];
        if (values.Count > 0 && !Supported(recipe)) throw new InvalidDataException("Upgrade selection currently supports Batarang-based equipment only. Reset its upgrade choices before changing the donor.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count || values.Any(id => !Options.Any(option => option.Id == id)))
            throw new InvalidDataException("Equipment upgrade choices are duplicated or unknown. Reopen Upgrades and save the selection again.");
        return values.ToHashSet(StringComparer.Ordinal);
    }
    internal static void ValidateLoadout(NativeSuitProject project, string content, Usmap maps)
    {
        var custom = project.EquipmentSlots.Where(slot => slot.Custom is not null).Select(slot => slot.Custom!).ToArray();
        foreach (var recipe in custom) _ = Disabled(recipe);
        if (!custom.Any(recipe => Supported(recipe) && Disabled(recipe).Count > 0)) return;
        var dcmd = EquipmentAssetService.Read(content, project.TargetPackages.Dcmd, maps);
        var list = Array(dcmd, "EquipmentList");
        var familyEtas = project.EquipmentSlots.Where(slot => slot.Custom is { } recipe && Supported(recipe))
            .Select(slot => CustomEquipmentService.EtaPackage(project, slot.Custom!)).Append(Eta).ToHashSet(StringComparer.Ordinal);
        var count = list.Value.Count(property => EquipmentAssetService.Reference(dcmd, property) is { } reference && familyEtas.Contains(reference.Package));
        RequireSingleFamilyItem(count);
    }
    internal static void RequireSingleFamilyItem(int count)
    {
        if (count != 1) throw new InvalidDataException("Filtered upgrades require exactly one Batarang-family item in this loadout. Two items share upgrade attributes; remove the other Batarang or restore all upgrades.");
    }
    private static ArrayPropertyData Array(UAsset asset, string name) => asset.Exports.OfType<NormalExport>()
        .SelectMany(export => export.Data).OfType<ArrayPropertyData>().Single(property => property.Name.ToString() == name);
    private static string[] References(UAsset asset, ArrayPropertyData array) => array.Value
        .Select(property => EquipmentAssetService.Reference(asset, property)?.Package ?? throw new InvalidDataException("Unreadable upgrade reference.")).ToArray();
    private static string Target(UAsset asset)
    {
        var property = asset.Exports.OfType<NormalExport>().SelectMany(export => export.Data).Single(p => p.Name.ToString() == "EquipmentToModify");
        return EquipmentAssetService.Reference(asset, property)?.Package ?? throw new InvalidDataException("Unreadable upgrade equipment target.");
    }
    internal static string Destination(string equipmentRoot, string package)
    {
        if (!equipmentRoot.StartsWith("/Game/Mods/", StringComparison.Ordinal)) throw new InvalidDataException("Upgrade owner must be a custom equipment namespace.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(equipmentRoot)))[..12];
        // Primary asset IDs use object names, not directories. Different suits must
        // not all register UpgradeFunctionalityDataAsset:DA_UF_BatarangUpgrades.
        return "/Game/Characters/Equipment/Mods/" + equipmentRoot["/Game/Mods/".Length..] + "/Upgrades/" + UnrealPathUtil.AssetName(package) + "_" + identity;
    }
    internal static string? Generate(CustomEquipmentRecipe recipe, string nativeUpgrade, string equipmentRoot,
        IReadOnlyDictionary<string, string> equipmentRedirects, string extracted, string content, Usmap maps, Action<string> log)
    {
        var disabled = Disabled(recipe);
        if (!Supported(recipe)) return string.IsNullOrWhiteSpace(nativeUpgrade) ? null : nativeUpgrade;
        if (nativeUpgrade != NativeRoot || !equipmentRedirects.TryGetValue(Instance, out var customInstance))
            throw new InvalidDataException("The Batarang upgrade donor changed. Refresh game files before building.");
        var root = EquipmentAssetService.Read(extracted, NativeRoot, maps);
        var rootArray = Array(root, "UpgradeSets");
        if (!References(root, rootArray).SequenceEqual(Options.Select(option => option.Set)))
            throw new InvalidDataException("The native Batarang upgrade list changed. This build cannot safely apply its saved upgrade choices.");
        var selected = Options.Where(option => !disabled.Contains(option.Id)).ToArray();
        string Destination(string package) => EquipmentUpgradeService.Destination(equipmentRoot, package);
        var redirects = new Dictionary<string, string>(equipmentRedirects, StringComparer.Ordinal) { [NativeRoot] = Destination(NativeRoot) };
        var outputs = new Dictionary<string, UAsset>(StringComparer.Ordinal) { [NativeRoot] = root };
        foreach (var option in selected.Where(option => option.TargetedFunction is not null))
        {
            var set = EquipmentAssetService.Read(extracted, option.Set, maps);
            if (!References(set, Array(set, "UpgradeFunctionality")).Contains(option.TargetedFunction!, StringComparer.Ordinal))
                throw new InvalidDataException("Native upgrade functionality changed: " + option.Label);
            var function = EquipmentAssetService.Read(extracted, option.TargetedFunction!, maps);
            if (Target(function) != Instance) throw new InvalidDataException("Native upgrade target changed: " + option.Label);
            outputs.Add(option.Set, set); outputs.Add(option.TargetedFunction!, function);
            redirects.Add(option.Set, Destination(option.Set)); redirects.Add(option.TargetedFunction!, Destination(option.TargetedFunction!));
        }
        rootArray.Value = rootArray.Value.Where((_, index) => !disabled.Contains(Options[index].Id)).ToArray();
        foreach (var (source, asset) in outputs)
        {
            CustomEquipmentService.Rename(asset, redirects);
            asset.FolderName = new FString(redirects[source]);
            var file = Path.Combine(content, redirects[source][6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); asset.Write(file);
            var check = EquipmentAssetService.Read(content, redirects[source], maps);
            if (check.Exports.Count != asset.Exports.Count) throw new InvalidDataException("Upgrade export count changed during serialization.");
            if (source == NativeRoot && !References(check, Array(check, "UpgradeSets")).SequenceEqual(selected.Select(o => redirects.GetValueOrDefault(o.Set, o.Set))))
                throw new InvalidDataException("Upgrade selection failed its cooked roundtrip.");
            if (selected.Any(o => o.TargetedFunction == source) && Target(check) != customInstance)
                throw new InvalidDataException("Custom equipment upgrade target failed its cooked roundtrip.");
            if (selected.Any(o => o.Set == source) && !References(check, Array(check, "UpgradeFunctionality"))
                .Contains(redirects[selected.Single(o => o.Set == source).TargetedFunction!], StringComparer.Ordinal))
                throw new InvalidDataException("Custom upgrade functionality binding failed its cooked roundtrip.");
        }
        log($"Equipment upgrades: {selected.Length}/{Options.Count} allowed; unique upgrade identities and custom mode targets prepared for registry loading. Native purchases unchanged; gameplay acceptance required.");
        return redirects[NativeRoot];
    }
}
