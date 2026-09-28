using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>
/// Twin exhausts with the base game's own effects. Boost effects come from the pawn data's boost ability set
/// (<c>BP_VehicleBoostData_*.BoostVFXs</c>, one entry per socket), the idle/start/stop flames from the gameplay BP's
/// <c>VehicleEngineEffectsComponent</c> maps (socket → Niagara system). Twin mode gives the vehicle private copies of
/// the pawn data, boost ability set and boost data with a second <c>VFX_Exhaust_02</c> entry, and adds the second
/// socket to the engine effect maps. Native twin-exhaust boost sets use the same two socket names.
/// </summary>
internal static class VehicleExhaustService
{
    internal const string FirstSocket = "VFX_Exhaust_01";
    internal const string BoostOutletSocket = "VFX_ExhaustBoost_01";
    internal const string SecondSocket = "VFX_Exhaust_02";
    private const string BoostList = "BoostVFXs";
    private const string BoostSocket = "BoostVFXSocketName";

    /// <summary>A native boost data set. Twin = its BoostVFXs has two outlets (checked against the game data).</summary>
    /// <param name="Engine">The engine effect set (start-up, idle, shut-down / gear-change flames) its vehicles use; empty = none.</param>
    internal sealed record Style(string Id, string Label, string Dataset, bool Twin, string PreviewColour, string Engine)
    {
        internal string BoostData => "/Game/Vehicles/Abilities/Boost/DataSets/AS_VehicleBoostData_" + Dataset;
        internal string BoostBlueprint => "/Game/Vehicles/Abilities/Boost/DataSets/BP_VehicleBoostData_" + Dataset;
        public override string ToString() => Label + (Twin ? " · twin exhausts" : "");
    }

    // Duplicates are merged: the 1966, 1989 and police-cruiser sets use the generic effects (the police set is twin).
    // Preview colours are approximations: most boosts use preset 0 of T_VEH_RB_Boost_Gradient_01 (orange); Batman
    // Beyond sets User.Boost_Base_Colour to red.
    // Engine sets, read from every playable vehicle BP (2026-09-14): Forever on the 1995; Charger on the 2022 The Batman; Generic on
    // the 1966, 1989, 1993 Animated (Generic boost), Tumbler and Arkham Knight. Every vehicle using the bike, Batpod or twin-exhaust
    // boosts (BvS, Beyond, 1939, 1997, bikes, Batpod…) keeps BP_PlayableVehicleBase's empty engine effects: boost only.
    internal static readonly IReadOnlyList<Style> Styles =
    [
        new("generic", "Generic", "Generic", false, "#ff8a2a", "Generic"),
        new("forever", "Batman Forever", "BatmanForever1995", false, "#ff8a2a", "Forever"),
        new("charger", "Batmobile Charger", "BatmobileCharger", false, "#ff9a3c", "Charger"),
        new("tumbler", "Tumbler (with broken-boost smoke)", "Tumbler", false, "#ff8a2a", "Generic"),
        new("bike", "Bike", "Bike_Generic", false, "#ff8a2a", ""),
        new("generic-twin", "Generic", "TwinExhausts_Generic", true, "#ff8a2a", ""),
        new("bvs-twin", "Batman v Superman", "TwinExhausts_BvS", true, "#ff8a2a", ""),
        new("beyond-twin", "Batman Beyond", "TwinExhausts_BatmanBeyond", true, "#ff3030", ""),
        new("batpod-twin", "Batpod", "BatPod", true, "#ff8a2a", ""),
        new("bike-twin", "Bike", "Bike_TwinExhausts_Generic", true, "#ff8a2a", ""),
    ];
    internal static readonly IReadOnlyList<string> EngineSets = ["Forever", "Charger", "Generic"];
    private static readonly string[] EnginePhases = ["StartUp", "Constant", "ShutDown"];
    internal static string EngineSystem(string set, string phase) => "/Game/VFX/Vehicles/EngineEffects/NS_VEH_Engine_" + set + "_" + phase;
    internal static Style? FindStyle(string? id) => Styles.FirstOrDefault(s => s.Id == id);
    internal static IEnumerable<string> StylePackages => Styles.Select(s => s.BoostBlueprint);
    /// <summary>The vehicle gets private boost data when it changes the boost style, adds a second outlet or changes boost speed.</summary>
    internal static bool PrivateBoost(VehicleProject p) => p.TwinExhausts || !string.IsNullOrEmpty(p.BoostStyle) || VehicleBoostSpeedService.Applies(p);

    internal static string PawnData(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/DA_DPRD_" + p.Id;
    internal static string BoostData(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/AS_BoostData_" + p.Id;
    internal static string BoostBlueprint(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/BP_BoostData_" + p.Id;

    internal static string? Unavailable(VehicleDonorService.Donor donor) =>
        donor.BoostData is null ? donor.Label + " has no boost, so boost styles and twin exhausts aren't available on it." : null;

    internal static void ValidateSelection(VehicleProject p, VehicleDonorService.Donor donor)
    {
        if (!PrivateBoost(p)) return;
        VehicleAssetService.Require(p.BoostColor is null, "Choose either a boost style / twin exhausts or a custom boost color for now; they use separate private boost chains.");
        VehicleAssetService.Require(Unavailable(donor) is null, Unavailable(donor) ?? "");
        var style = string.IsNullOrEmpty(p.BoostStyle) ? null : FindStyle(p.BoostStyle)
            ?? throw new InvalidDataException("Unknown boost style: " + p.BoostStyle);
        VehicleAssetService.Require(style is null || !style.Twin || p.TwinExhausts, style?.Label + " is a twin-exhaust boost; turn on twin exhausts.");
        VehicleAssetService.Require(p.TwinExhausts || !p.Transforms.Any(t => t.Component == "socket:" + SecondSocket),
            "Remove the second outlet transform or enable twin exhausts.");
    }

    internal static void Validate(VehicleProject p, VehicleDonorService.Donor donor, string nativeContent)
    {
        if (!PrivateBoost(p)) return;
        ValidateSelection(p, donor);
        var style = FindStyle(p.BoostStyle);
        var missing = donor.BoostPackages.Concat(style is null ? [] : [style.BoostBlueprint]).Concat(VehicleBoostSpeedService.Missing(p, nativeContent))
            .Where(package => ExtractedPackagePathService.ResolvePackageUasset(nativeContent, package) is not { } file || !File.Exists(file)).ToArray();
        VehicleAssetService.Require(missing.Length == 0, "Twin exhausts need the driving base's boost data. Use ☰ → Refresh game assets → Add vehicle driving bases (quick).\n" + string.Join("\n", missing));
    }

    /// <summary>Redirects for the cloned gameplay BP: it names the pawn data, and its engine effects follow the boost style's vehicle
    /// (otherwise a Charger boost still spits Forever flames on gear changes).</summary>
    internal static IReadOnlyDictionary<string, string> Redirects(VehicleProject p, VehicleDonorService.Donor donor)
    {
        var redirects = new Dictionary<string, string>();
        if (!PrivateBoost(p) || donor.PawnData is null) return redirects;
        redirects[donor.PawnData] = PawnData(p);
        if (FindStyle(p.BoostStyle) is { Engine.Length: > 0 } style)
            foreach (var set in EngineSets.Where(s => s != style.Engine))
                foreach (var phase in EnginePhases) redirects[EngineSystem(set, phase)] = EngineSystem(style.Engine, phase);
        return redirects;
    }

    private static string SocketOf(StructPropertyData entry) =>
        entry.Value.OfType<NamePropertyData>().FirstOrDefault(n => n.Name.ToString() == BoostSocket)?.Value.ToString() ?? "";

    /// <summary>Clones the boost chain with a second socket entry. <paramref name="save"/> writes a package into the mod.</summary>
    internal static void Stage(VehicleProject p, VehicleDonorService.Donor donor, string nativeContent, Action<UAsset, string> save, Action<string> log)
    {
        if (!PrivateBoost(p)) return;
        Validate(p, donor, nativeContent);
        var style = FindStyle(p.BoostStyle);
        var sourceBlueprint = style?.BoostBlueprint ?? donor.BoostBlueprint!;
        var redirects = new Dictionary<string, string>
        {
            [donor.PawnData!] = PawnData(p), [donor.BoostData!] = BoostData(p), [donor.BoostBlueprint!] = BoostBlueprint(p),
        };
        redirects[sourceBlueprint] = BoostBlueprint(p);
        foreach (var (from, to) in VehicleBoostSpeedService.Redirects(p)) redirects[from] = to;

        var boost = VehicleAssetService.Read(nativeContent, sourceBlueprint);
        CustomEquipmentService.Rename(boost, redirects);
        var defaults = boost.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal));
        var list = defaults.Data.OfType<ArrayPropertyData>().SingleOrDefault(a => a.Name.ToString() == BoostList)
            ?? throw new InvalidDataException("The boost data has no BoostVFXs list.");
        var entries = list.Value.OfType<StructPropertyData>().ToList();
        VehicleAssetService.Require(entries.Count > 0, "The boost data has no boost effects.");
        // Outlets follow this vehicle, not the style's own socket names: the first entry fires from the base's outlet,
        // the second (a twin style's own, or a copy of the first) from VFX_Exhaust_02. Extra entries are dropped.
        StructPropertyData Outlet(StructPropertyData entry, string socketName, int index)
        {
            var copy = (StructPropertyData)entry.Clone();
            copy.Value.OfType<NamePropertyData>().Single(n => n.Name.ToString() == BoostSocket).Value = new FName(boost, socketName);
            copy.Name = new FName(boost, index.ToString());
            return copy;
        }
        var outlets = new List<PropertyData> { Outlet(entries[0], donor.ExhaustSocket, 0) };
        if (p.TwinExhausts) outlets.Add(Outlet(entries.Count > 1 ? entries[1] : entries[0], SecondSocket, 1));
        list.Value = [.. outlets];
        save(boost, BoostBlueprint(p));

        var set = VehicleAssetService.Read(nativeContent, donor.BoostData!);
        CustomEquipmentService.Rename(set, redirects);
        save(set, BoostData(p));

        var pawn = VehicleAssetService.Read(nativeContent, donor.PawnData!);
        CustomEquipmentService.Rename(pawn, redirects);
        save(pawn, PawnData(p));
        VehicleBoostSpeedService.Stage(p, nativeContent, save, log);
        log("Boost: " + (style is null ? "driving base's own" : style.ToString()) + ", from " + donor.ExhaustSocket + (p.TwinExhausts ? " and " + SecondSocket : "") + " (private pawn data and boost set).");
    }

    /// <summary>Empties the engine effect maps for a style whose vehicles have none; otherwise adds the second socket to every map
    /// that has the first (start-up, idle, shut-down, gear change).</summary>
    internal static void ApplyEngineEffects(UAsset gameplay, VehicleProject p)
    {
        if (FindStyle(p.BoostStyle) is { Engine.Length: 0 })
        {
            foreach (var map in EngineMaps(gameplay)) map.Value.Clear();
            return;
        }
        if (!p.TwinExhausts) return;
        var outlet = VehicleDonorService.Get(p).ExhaustSocket;
        foreach (var map in EngineMaps(gameplay))
        {
            var first = map.Value.Keys.OfType<NamePropertyData>().FirstOrDefault(k => k.Value.ToString() == outlet);
            if (first is null || map.Value.Keys.OfType<NamePropertyData>().Any(k => k.Value.ToString() == SecondSocket)) continue;
            var key = (NamePropertyData)first.Clone();
            key.Value = new FName(gameplay, SecondSocket);
            map.Value.Add(key, (PropertyData)map.Value[first].Clone());
        }
    }

    private static IEnumerable<MapPropertyData> EngineMaps(UAsset gameplay) =>
        gameplay.Exports.OfType<NormalExport>().Where(e => e.GetExportClassType()?.ToString() == "VehicleEngineEffectsComponent")
            .SelectMany(e => e.Data.OfType<StructPropertyData>()).SelectMany(s => s.Value.OfType<MapPropertyData>());

    internal static void Verify(VehicleProject p, VehicleDonorService.Donor donor, string content, UAsset stagedGameplay)
    {
        if (!PrivateBoost(p)) return;
        VehicleBoostSpeedService.Verify(p, content);
        
        var names = stagedGameplay.GetNameMapIndexList().Select(n => n.ToString()).ToHashSet(StringComparer.Ordinal);
        VehicleAssetService.Require(names.Contains(PawnData(p)) && !names.Contains(donor.PawnData!), "The vehicle doesn't use its twin-exhaust pawn data.");
        var pawnAsset = VehicleAssetService.Read(content, PawnData(p));
        
        var pawn = pawnAsset.GetNameMapIndexList().Select(n => n.ToString()).ToHashSet(StringComparer.Ordinal);
        VehicleAssetService.Require(pawn.Contains(BoostData(p)) && !pawn.Contains(donor.BoostData!), "The twin-exhaust pawn data doesn't grant the private boost set.");
        var boost = VehicleAssetService.Read(content, BoostBlueprint(p));
        var sockets = boost.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ArrayPropertyData>()).Where(a => a.Name.ToString() == BoostList)
            .SelectMany(a => a.Value.OfType<StructPropertyData>()).Select(SocketOf).ToHashSet(StringComparer.Ordinal);
        VehicleAssetService.Require(sockets.SetEquals(p.TwinExhausts ? [donor.ExhaustSocket, SecondSocket] : [donor.ExhaustSocket]), "The vehicle's boost data doesn't match its exhaust outlets.");
        if (FindStyle(p.BoostStyle) is { } chosen)
            VehicleAssetService.Require(!boost.GetNameMapIndexList().Any(n => n.ToString() == chosen.BoostBlueprint), "The boost style copy still names the native boost data.");
        if (FindStyle(p.BoostStyle) is { Engine.Length: 0 })
            VehicleAssetService.Require(EngineMaps(stagedGameplay).All(m => m.Value.Count == 0), "The boost style has no engine flames, but the vehicle still has some.");
        else if (FindStyle(p.BoostStyle) is { } engine)
            VehicleAssetService.Require(!EngineSets.Where(s => s != engine.Engine).Any(s => names.Any(n => n.StartsWith("NS_VEH_Engine_" + s + "_", StringComparison.Ordinal) || n.StartsWith(EngineSystem(s, ""), StringComparison.Ordinal))),
                "The vehicle's engine effects still use another vehicle's flames.");
        foreach (var map in p.TwinExhausts && FindStyle(p.BoostStyle) is not { Engine.Length: 0 } ? EngineMaps(stagedGameplay) : [])
            if (map.Value.Keys.OfType<NamePropertyData>().Any(k => k.Value.ToString() == donor.ExhaustSocket))
                VehicleAssetService.Require(map.Value.Keys.OfType<NamePropertyData>().Any(k => k.Value.ToString() == SecondSocket), "An engine effect doesn't cover the second exhaust.");
    }
}
