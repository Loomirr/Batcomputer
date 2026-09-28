using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>
/// What a vehicle sounds like. Every playable vehicle keeps its audio on one <c>DinnerAudioPlayableVehicleComp</c>
/// component in its gameplay blueprint, as plain object references to <c>WubAudioEvent</c> assets (the engine loop,
/// ignition, shut-down, gear change, boost and spawn) plus the <c>WubGameParameter</c> RTPCs that drive engine
/// speed and load. A style copies one native vehicle's set onto the cloned blueprint; the events themselves are
/// base-game packages, so nothing is cooked or extracted for this.
///
/// The engine loop, its two RTPCs, the ignition, the shut-down and the gear change always travel together: an
/// engine loop reading another vehicle's RPM parameter does not rev, and a Tumbler start-up in front of a Batpod
/// engine is the kind of mismatch this is meant to avoid. Boost and spawn sounds are only written when the style
/// has them, so a vehicle keeps its driving base's boost audio (and its chosen boost style's flames) otherwise.
/// </summary>
internal static class VehicleSoundService
{
    private const string Events = "/Game/Wub/AudioEvents/";
    private const string Parameters = "/Game/Wub/GameParameters/";
    internal const string Component = "Sfx Vehicle Component_GEN_VARIABLE";
    private const string DefaultSpeed = "GP_Veh_Engine_Speed_Player";
    private const string DefaultLoad = "GP_Vehicle_Engine_Load";

    /// <summary>One native vehicle's audio set. Empty slots are left as they are, except Init/Off/UpShift, which
    /// are cleared so no part of the base's own engine survives under a different one.</summary>
    internal sealed record Style(string Id, string Label, string Engine, string Init = "", string Off = "", string UpShift = "",
        string Boost = "", string BoostFail = "", string Summon = "", string Speed = DefaultSpeed, string Load = DefaultLoad)
    {
        public override string ToString() => Label;
    }

    // Read from every playable vehicle blueprint in the shipped game (2026-09-21, probe --probe-vehicle-audio).
    internal static readonly IReadOnlyList<Style> Styles =
    [
        new("batmobile1939", "1939 Batmobile", "Play_Veh_Batmobile1939_Engine_Full", "Play_Veh_Batmobile1939_Engine_On", "Play_Veh_Batmobile1939_Engine_Off", "Play_Veh_Gear_Up_Batmobile1966", "Play_Veh_Batmobile1939_Boost", "", "Play_Veh_Spawn_Batmobile1939"),
        new("batmobile1966", "1966 Batmobile", "Play_Veh_Engine_Player_Batmobile1966_Lp", "Play_Veh_Engine_Startup_Batmobile1966", "", "Play_Veh_Gear_Up_Batmobile1966", "Play_Veh_Batmobile66_Boost", "Play_Veh_BatmanForever1995_Boost_Fail", "Play_VEH_Spawn_Batmobile66"),
        new("batmobile1989", "1989 Batmobile", "Play_Veh_Batmobile89_Engine_Full", "Play_Veh_Batmobile89_Start", "Play_Veh_Batmobile89_SwitchOff", "Play_Veh_Gear_UP_Batmobile89", "Play_Veh_Batmobile89_Boost_lp", "", "Play_VEH_Spawn_Batmobile89"),
        new("batmobile1993", "1993 Animated Batmobile", "Play_Veh_BatmobileAnimated_Engine_Full", "Play_Veh_BatmobileAnimated_Engine_StartUp", "Play_Veh_BatmobileAnimated_Engine_SwitchOff", "Play_Veh_BatmobileAnimated_Gear_Change", "Play_Veh_BatmobileAnimated_Boost_lp", "Play_Veh_BatmanForever1995_Boost_Fail", "Play_VEH_Spawn_BatmobileAnimatedSeries"),
        new("batmobile1995", "1995 Batman Forever Batmobile", "Play_Veh_Engine_Player_BatmanForever1995_Lp", "", "", "Play_Veh_Gear_Up_BatmanForever1995", "Play_Veh_BatmanForever1995_Boost_Lp", "Play_Veh_BatmanForever1995_Boost_Fail", "Play_VEH_Spawn_BatmobileForever", "GP_Veh_Engine_Speed_BatmanForever", "GP_Vehicle_Engine_Load_BatmanForever"),
        new("batmobile1997", "1997 Batman & Robin Batmobile", "Play_Veh_BatmanRobin_Batmobile_Engine_Full", "Play_Veh_BatmanRobin_Batmobile_Startup", "Play_Veh_BatmanRobin_Batmobile_SwitchOff", "", "Play_Veh_BatmanRobin_Batmobile_Boost_lp", "Play_Veh_BatmanRobin_Batmobile_Boost_Fail", "Play_VEH_Spawn_BatmobileBatRob"),
        new("batmobile2005", "2005 Tumbler", "Play_Veh_Engine_Player_Tumbler_Lp", "Play_Veh_Tumbler_Engine_Start", "Play_Veh_Tumbler_Engine_Stop", "Play_Veh_Gear_Up_Tumbler", "Play_Veh_Boost_Tumbler_02_Lp", "Play_Veh_Batmobile_Boost_Fail_01", "Play_Veh_Spawn_BuildIn_Tumbler"),
        new("batmobile2016", "2016 Batman v Superman Batmobile", "Play_Veh_Engine_Player_BatVsSuper2016_Lp", "Play_Veh_Startup_BatVsSuper2016", "Play_Veh_Engine_SwitchOff_BatVsSuper2016", "", "Play_Veh_BatVsSuper2016_Boost_Lp", "Play_Veh_Batmobile_Boost_Fail_01", "Play_VEH_Spawn_BatmobileBatVSup", DefaultSpeed, "GP_Vehicle_Engine_Load_BatVsSuper2016"),
        new("charger2022", "2022 Batmobile (Charger)", "Play_Veh_Engine_Player_Charger2022_Lp", "Play_Veh_Spawn_Player_Charger2022", "Play_Veh_Charger2022_SwitchOFF", "Play_Veh_Gear_Up_Charger2022", "Play_Veh_Batmobile_Boost_01", "Play_Veh_Batmobile_Boost_Fail_01", "Play_Veh_Spawn_BuildIn_Charger"),
        new("batpod", "Batpod", "Play_Veh_BatPod_Engine_Full", "Play_Veh_BatPod_Engine_Start", "Play_Veh_BatPod_Engine_Stop", "Play_Veh_Gear_Up_BatPod", "Play_Veh_Batpod_Boost_Lp", "", "Play_VEH_Spawn_BatPod"),
        new("gordonbike", "Police bike", "Play_Veh_GordonPoliceBike_Engine_Full", "Play_Veh_GordonPoliceBike_Startup", "Play_Veh_GordonPoliceBike_Engine_SwitchOff", "Play_Veh_Gear_UP_GPB", "Play_Veh_GordonBike_Boost_lp", "Play_Veh_PoliceCruiser2000_Boost_Fail", "Play_Veh_Spawn_GordonBike"),
        new("nightbird", "Nightbird", "Play_Veh_NightBird_Engine_Lp", "Play_Veh_NightBird_Engine_StartUp", "Play_Veh_NightBird_Engine_SwitchOff", "", "Play_Veh_NightBird_Boost_Lp", "", "Play_VEH_Spawn_NightBird"),
        new("robinmobile", "Robinmobile", "Play_Veh_RobinMobile_Engine_Full", "Play_Veh_RobinMobile_Start", "Play_Veh_RobinMobile_SwitchOff", "Play_Veh_BatmanRobin_Batmobile_BlowOff", "Play_Veh_RobinMobile_Boost_lp", "Play_Veh_Batmobile_Boost_Fail_01", "Play_Veh_RobinMobile_Spawn"),
        new("kittycar", "Kitty car", "Play_Veh_KittyCar_Modern_Engine_Full", "Play_Veh_KittyCar_Modern_Startup", "Play_Veh_KittyCar_Modern_Engine_SwitchOff", "Play_Veh_Gear_Up_Batmobile1966", "Play_Veh_KittyCarModern_Boost_lp", "", "Play_Veh_Spawn_KittyCarModern"),
        new("batgirlvan", "Batgirl's van", "Play_Veh_BatGirl_Van_Lp", "Play_Veh_BatGirl_Van_StartUp", "Play_Veh_BatGirl_Van_Engine_Stop", "Play_Veh_Gear_Up_Batmobile1966", "Play_Veh_BatGirl_Van_Boost_Lp", "Play_Veh_Batmobile_Boost_Fail_01", "Play_Veh_BatGirlVan_Spawn", "GP_Veh_Engine_Speed_BatGirlVan"),
        new("policecruiser", "Police cruiser", "Play_Veh_Engine_Player_PoliceCruiser2000_Lp", "Play_Veh_PoliceCruiser_SwitchON", "Play_Veh_PoliceCruiser_SwitchOFF", "Play_Veh_Gear_Up_PoliceCruiser2000", "Play_Veh_PoliceCruiser2000_Boost_Start", "", "Play_Veh_Spawn_BuildIn_PoliceCruiser", DefaultSpeed, "GP_Vehicle_Engine_Load_PoliceCruiser"),
        new("swatvan", "SWAT van", "Play_Veh_Engine_Player_SwatVan_Lp", "Play_Veh_StartUp_PoliceSwatVan", "Play_Veh_SwitchOff_PoliceSwatVan", "Play_Veh_Gear_Up_SwatVan", "Play_Veh_SwatVan_Boost_Lp", "", "Play_Veh_Spawn_BuildIn_SwatVan", DefaultSpeed, "GP_Vehicle_Engine_Load_SwatVan"),
        new("forklift", "Forklift", "Play_Veh_Engine_Player_Forklift_Lp", "Play_Veh_Engine_Player_Forklift_Ignition", "Play_Veh_Engine_Player_Forklift_Off", "", "", "", "", DefaultSpeed, "GP_Veh_Engine_Load_ForkLift"),
        new("lawnmower", "Lawnmower", "Play_Veh_Lawnmower_Engine_Lp", "Play_Veh_Lawnmower_Engine_Startup", "Play_Veh_Lawnmower_Engine_Off"),
    ];

    /// <summary>A sound a preview can play: the label shown to the user and the audio event behind it.</summary>
    internal sealed record Slot(string Label, string Event) { public override string ToString() => Label; }
    private static readonly (string Label, string Property)[] SlotProperties =
        [("Engine", "EngineSfx"), ("Ignition", "EngineInitSfx"), ("Shut-down", "EngineTurnOffSfx"),
         ("Gear change", "UpShift"), ("Boost", "BoostSfx"), ("Boost fail", "BoostFailSfx"), ("Spawn", "SummonSfx")];

    internal static IReadOnlyList<Slot> Slots(Style style) =>
        new[] { ("Engine", style.Engine), ("Ignition", style.Init), ("Shut-down", style.Off), ("Gear change", style.UpShift),
                ("Boost", style.Boost), ("Boost fail", style.BoostFail), ("Spawn", style.Summon) }
            .Where(x => x.Item2.Length > 0).Select(x => new Slot(x.Item1, x.Item2)).ToArray();

    /// <summary>The sounds a driving base plays today, for previewing when no style is chosen.</summary>
    internal static IReadOnlyList<Slot> DonorSlots(UAsset blueprint)
    {
        var component = blueprint.Exports.OfType<NormalExport>().SingleOrDefault(e => e.GetExportClassType()?.ToString() == "DinnerAudioPlayableVehicleComp");
        if (component is null) return [];
        var slots = new List<Slot>();
        foreach (var (label, property) in SlotProperties)
        {
            var reference = property == "UpShift" ? UpShiftEvent(component)
                : component.Data.OfType<ObjectPropertyData>().FirstOrDefault(v => v.Name.ToString() == property);
            if (reference is null || reference.Value.IsNull()) continue;
            if (EquipmentAssetService.Reference(blueprint, reference)?.Package is { } package && package.Length > 0)
                slots.Add(new(label, UnrealPathUtil.AssetName(package)));
        }
        return slots;
    }

    internal static Style? FindStyle(string? id) => Styles.FirstOrDefault(s => s.Id == id);
    internal static bool Applies(VehicleProject p) => !string.IsNullOrEmpty(p.SoundStyle);

    internal static void Validate(VehicleProject p)
    {
        if (!Applies(p)) return;
        if (FindStyle(p.SoundStyle) is null) throw new InvalidDataException("Unknown vehicle sound: " + p.SoundStyle);
    }

    private static NormalExport Audio(UAsset asset)
    {
        var component = asset.Exports.OfType<NormalExport>().SingleOrDefault(e => e.GetExportClassType()?.ToString() == "DinnerAudioPlayableVehicleComp");
        VehicleAssetService.Require(component is not null, "This driving base has no vehicle audio component, so its sounds cannot be changed.");
        return component!;
    }
    private static void Set(UAsset asset, NormalExport component, string property, string package, string className)
    {
        var value = package.Length == 0 ? FPackageIndex.FromRawIndex(0)
            : SwordCombatService.Obj(asset, package, UnrealPathUtil.AssetName(package), "/Script/Wub", className);
        if (component.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == property) is { } existing) existing.Value = value;
        else component.Data.Add(new ObjectPropertyData(new FName(asset, property)) { Value = value });
        if (!value.IsNull() && !component.CreateBeforeSerializationDependencies.Contains(value)) component.CreateBeforeSerializationDependencies.Add(value);
    }
    private static void Event(UAsset asset, NormalExport component, string property, string name) =>
        Set(asset, component, property, name.Length == 0 ? "" : Events + name, "WubAudioEvent");
    private static void Parameter(UAsset asset, NormalExport component, string property, string name) =>
        Set(asset, component, property, Parameters + name, "WubGameParameter");

    /// <summary>The gear-change event lives one struct deep, and only on bases that shift gears audibly.</summary>
    private static ObjectPropertyData? UpShiftEvent(NormalExport component) =>
        component.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "EngineOverrides")
            ?.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "UpShift")
            ?.Value.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Event");

    internal static void Apply(UAsset asset, VehicleProject p)
    {
        if (!Applies(p)) return;
        Validate(p);
        var style = FindStyle(p.SoundStyle)!; var component = Audio(asset);
        Event(asset, component, "EngineSfx", style.Engine);
        Event(asset, component, "EngineInitSfx", style.Init);
        Event(asset, component, "EngineTurnOffSfx", style.Off);
        Parameter(asset, component, "Rtpc_EngineSpeed", style.Speed);
        Parameter(asset, component, "Rtpc_EngineLoad", style.Load);
        if (style.Boost.Length > 0) Event(asset, component, "BoostSfx", style.Boost);
        if (style.BoostFail.Length > 0) Event(asset, component, "BoostFailSfx", style.BoostFail);
        if (style.Summon.Length > 0) Event(asset, component, "SummonSfx", style.Summon);
        if (UpShiftEvent(component) is { } upShift)
        {
            upShift.Value = style.UpShift.Length == 0 ? FPackageIndex.FromRawIndex(0)
                : SwordCombatService.Obj(asset, Events + style.UpShift, style.UpShift, "/Script/Wub", "WubAudioEvent");
            if (!upShift.Value.IsNull() && !component.CreateBeforeSerializationDependencies.Contains(upShift.Value)) component.CreateBeforeSerializationDependencies.Add(upShift.Value);
        }
    }

    internal static void Verify(UAsset asset, VehicleProject p)
    {
        if (!Applies(p)) return;
        var style = FindStyle(p.SoundStyle)!; var component = Audio(asset);
        void Same(string property, string package)
        {
            var reference = component.Data.OfType<ObjectPropertyData>().SingleOrDefault(v => v.Name.ToString() == property);
            VehicleAssetService.Require(reference is not null && (package.Length == 0 ? reference.Value.IsNull() : EquipmentAssetService.Reference(asset, reference)?.Package == package),
                "Vehicle sound did not survive staging: " + property);
        }
        Same("EngineSfx", Events + style.Engine);
        Same("EngineInitSfx", style.Init.Length == 0 ? "" : Events + style.Init);
        Same("EngineTurnOffSfx", style.Off.Length == 0 ? "" : Events + style.Off);
        Same("Rtpc_EngineSpeed", Parameters + style.Speed);
        Same("Rtpc_EngineLoad", Parameters + style.Load);
        if (style.Boost.Length > 0) Same("BoostSfx", Events + style.Boost);
        if (style.Summon.Length > 0) Same("SummonSfx", Events + style.Summon);
        if (UpShiftEvent(component) is { } upShift)
            VehicleAssetService.Require(style.UpShift.Length == 0 ? upShift.Value.IsNull() : EquipmentAssetService.Reference(asset, upShift)?.Package == Events + style.UpShift,
                "Vehicle gear-change sound did not survive staging.");
    }
}
