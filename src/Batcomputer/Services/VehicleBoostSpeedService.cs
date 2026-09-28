using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>
/// Boost top speed and acceleration. Every vehicle's pawn data grants the shared <c>AS_VehicleBoost</c>, whose
/// <c>GA_VehicleBoost</c> applies <c>GE_VehicleBoostSpeed</c> while boosting. That effect overrides the movement attributes
/// TopSpeedMph and AccelerationGs with <c>CT_VehicleAbilities</c> rows Boost.MaxVehicleSpeed (90) and Boost.VehicleAcceleration
/// (6) for every vehicle, so a car tuned above 90 mph slows down when it boosts. A vehicle with boost values gets private
/// copies of the set, ability and effect (the effect with fixed magnitudes instead of the curve rows), granted by its private
/// pawn data.
/// </summary>
internal static class VehicleBoostSpeedService
{
    internal const string AbilitySet = "/Game/Vehicles/Abilities/Boost/AS_VehicleBoost";
    internal const string Ability = "/Game/Vehicles/Abilities/Boost/GA_VehicleBoost";
    internal const string Effect = "/Game/Vehicles/Abilities/Boost/GE_VehicleBoostSpeed";
    internal static IEnumerable<string> Packages => [AbilitySet, Ability, Effect];

    // Handling field → the movement attribute the boost effect overrides, and the curve value every native vehicle gets (read 2026-09-14).
    internal static readonly IReadOnlyDictionary<string, (string Attribute, float Native)> Fields = new Dictionary<string, (string, float)>
    {
        ["BoostTopSpeedMph"] = ("TopSpeedMph", 90), ["BoostAccelerationGs"] = ("AccelerationGs", 6),
    };

    internal static IEnumerable<(string Property, float Value)> Values(VehicleProject p)
    {
        if (p.BoostTopSpeedMph is { } speed) yield return ("BoostTopSpeedMph", speed);
        if (p.BoostAccelerationGs is { } acceleration) yield return ("BoostAccelerationGs", acceleration);
    }
    internal static bool Applies(VehicleProject p) => p.BoostTopSpeedMph is not null || p.BoostAccelerationGs is not null;
    internal static void Validate(VehicleProject p)
    {
        if (p.BoostTopSpeedMph is { } speed && (!float.IsFinite(speed) || speed is < 10 or > 500)) throw new InvalidDataException("Boost top speed must be between 10 and 500 mph.");
        if (p.BoostAccelerationGs is { } acceleration && (!float.IsFinite(acceleration) || acceleration is < 0.1f or > 50)) throw new InvalidDataException("Boost acceleration must be between 0.1 and 50 Gs.");
    }
    internal static string PrivateSet(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/AS_Boost_" + p.Id;
    internal static string PrivateAbility(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/GA_Boost_" + p.Id;
    internal static string PrivateEffect(VehicleProject p) => VehicleProjectService.ContentRoot(p) + "/Boost/GE_BoostSpeed_" + p.Id;

    internal static Dictionary<string, string> Redirects(VehicleProject p) =>
        Applies(p) ? new() { [AbilitySet] = PrivateSet(p), [Ability] = PrivateAbility(p), [Effect] = PrivateEffect(p) } : [];

    internal static IEnumerable<string> Missing(VehicleProject p, string nativeContent) =>
        Applies(p) ? Packages.Where(package => ExtractedPackagePathService.ResolvePackageUasset(nativeContent, package) is not { } file || !File.Exists(file)) : [];

    private static IEnumerable<StructPropertyData> Modifiers(UAsset effect) =>
        effect.Exports.OfType<NormalExport>().Where(e => e.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal))
            .SelectMany(e => e.Data.OfType<ArrayPropertyData>()).Where(a => a.Name.ToString() == "Modifiers").SelectMany(a => a.Value.OfType<StructPropertyData>());
    private static StructPropertyData? Child(StructPropertyData s, string name) => s.Value.OfType<StructPropertyData>().FirstOrDefault(c => c.Name.ToString() == name);
    private static string AttributeOf(StructPropertyData modifier) =>
        Child(modifier, "Attribute")?.Value.OfType<StrPropertyData>().FirstOrDefault(n => n.Name.ToString() == "AttributeName")?.Value?.Value ?? "";
    private static StructPropertyData? Magnitude(StructPropertyData modifier) => Child(modifier, "ModifierMagnitude") is { } m ? Child(m, "ScalableFloatMagnitude") : null;

    /// <summary>Clones the effect (fixed values), ability and ability set. The pawn data clone picks up <see cref="Redirects"/>.</summary>
    internal static void Stage(VehicleProject p, string nativeContent, Action<UAsset, string> save, Action<string> log)
    {
        if (!Applies(p)) return;
        var missing = Missing(p, nativeContent).ToArray();
        VehicleAssetService.Require(missing.Length == 0, "Boost speed needs the boost ability. Use Refresh game assets → Add vehicle driving bases (quick).\n" + string.Join("\n", missing));
        var redirects = Redirects(p);

        var effect = VehicleAssetService.Read(nativeContent, Effect);
        CustomEquipmentService.Rename(effect, redirects);
        foreach (var value in Values(p))
        {
            var attribute = Fields[value.Property].Attribute;
            var modifier = Modifiers(effect).SingleOrDefault(m => AttributeOf(m) == attribute) ?? throw new InvalidDataException("The game's boost effect no longer sets " + attribute + ".");
            var magnitude = Magnitude(modifier) ?? throw new InvalidDataException("The game's boost effect changed how it sets " + attribute + ".");
            var scale = magnitude.Value.OfType<FloatPropertyData>().SingleOrDefault(f => f.Name.ToString() == "Value");
            if (scale is null) magnitude.Value.Add(scale = new FloatPropertyData(new FName(effect, "Value")));
            scale.Value = value.Value;
            // Without a curve row the ScalableFloat is just Value.
            if (Child(magnitude, "Curve")?.Value.OfType<ObjectPropertyData>().FirstOrDefault(o => o.Name.ToString() == "CurveTable") is { } table) table.Value = new FPackageIndex(0);
        }
        save(effect, PrivateEffect(p));

        var ability = VehicleAssetService.Read(nativeContent, Ability);
        CustomEquipmentService.Rename(ability, redirects);
        save(ability, PrivateAbility(p));

        var set = VehicleAssetService.Read(nativeContent, AbilitySet);
        CustomEquipmentService.Rename(set, redirects);
        save(set, PrivateSet(p));
        log("Boost speed: " + string.Join(", ", Values(p).Select(h => Fields[h.Property].Attribute + " " + h.Value.ToString("0.##"))) + " while boosting (private boost ability).");
    }

    internal static void Verify(VehicleProject p, string content)
    {
        if (!Applies(p)) return;
        HashSet<string> Names(string package) => VehicleAssetService.Read(content, package).GetNameMapIndexList().Select(n => n.ToString()).ToHashSet(StringComparer.Ordinal);
        var pawn = Names(VehicleExhaustService.PawnData(p));
        VehicleAssetService.Require(pawn.Contains(PrivateSet(p)) && !pawn.Contains(AbilitySet), "The vehicle's pawn data doesn't grant its own boost ability.");
        var set = Names(PrivateSet(p));
        VehicleAssetService.Require(set.Contains(PrivateAbility(p)) && !set.Contains(Ability), "The vehicle's boost set doesn't use its own boost ability.");
        var ability = Names(PrivateAbility(p));
        VehicleAssetService.Require(ability.Contains(PrivateEffect(p)) && !ability.Contains(Effect), "The vehicle's boost ability doesn't apply its own boost speed.");
        var effect = VehicleAssetService.Read(content, PrivateEffect(p));
        foreach (var value in Values(p))
        {
            var magnitude = Modifiers(effect).Where(m => AttributeOf(m) == Fields[value.Property].Attribute).Select(Magnitude).SingleOrDefault();
            var staged = magnitude?.Value.OfType<FloatPropertyData>().SingleOrDefault(f => f.Name.ToString() == "Value")?.Value;
            var curve = magnitude is null ? null : Child(magnitude, "Curve")?.Value.OfType<ObjectPropertyData>().FirstOrDefault(o => o.Name.ToString() == "CurveTable");
            VehicleAssetService.Require(staged is { } v && MathF.Abs(v - value.Value) < 1e-4f && (curve is null || curve.Value.Index == 0), "Boost speed did not survive staging: " + value.Property);
        }
    }
}
