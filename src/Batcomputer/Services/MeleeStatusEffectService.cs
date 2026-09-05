using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Opt-in hit-target statuses for the verified melee adapters. Damage and native target filters remain intact.</summary>
internal static class MeleeStatusEffectService
{
    internal sealed record Preset(string Id, string Label, string Package);
    internal static readonly Preset[] Presets = [
        new("none", "None — native hits", ""),
        new("stun", "Stun interruption [experimental]", "/Game/Characters/Abilities/GameplayEffects/Interruptions/GE_StunnedInterruption"),
        new("smoke", "Smoke distraction [experimental]", "/Game/Characters/Abilities/GameplayEffects/Interruptions/GE_SmokedInterruption")
    ];
    internal static bool Enabled(MeleeStatusSettings? s) => s is not null && s.PresetId != "none";
    internal static IReadOnlyList<string> Validate(MeleeStatusSettings? s) => s is null || !Presets.Any(p => p.Id == s.PresetId) ||
        !float.IsFinite(s.DurationSeconds) || s.DurationSeconds < .25f || s.DurationSeconds > 10
        ? ["Choose a supported hit status and a duration between 0.25 and 10 seconds."] : [];
    internal static string StatusPackage(string mod, MeleeStatusSettings s) => SwordCombatService.Root(mod) + "/GE_HitStatus_" + s.PresetId;
    private static NormalExport Cdo(UAsset a) => a.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
    private static StructPropertyData Tags(UAsset a, string name, params string[] tags) => HeldItemEffectService.Struct(a, name, "GameplayTagContainer",
        new GameplayTagContainerPropertyData(new FName(a, name)) { Value = tags.Select(t => new FName(a, t)).ToArray() });
    internal static void Generate(SwordCombatService.Context c, string mod, MeleeStatusSettings s)
    {
        if (!Enabled(s)) return;
        var errors = Validate(s); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var path = StatusPackage(mod, s); var a = c.Clone(Presets.Single(p => p.Id == s.PresetId).Package, path); var cdo = Cdo(a);
        // Smoke normally lasts until the smoke volume removes it. On-hit smoke must expire independently.
        cdo.Data.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "DurationPolicy").Value = new FName(a, "HasDuration");
        Duration(cdo).Value = s.DurationSeconds;
        // Additional restriction, not replacement: native smoke exclusions / inherited effect checks remain.
        var requirements = HeldItemEffectService.Struct(a, "ApplicationTagRequirements", "GameplayTagRequirements",
            Tags(a, "RequireTags", "Pawns.NPC.Goon"), Tags(a, "IgnoreTags", "Pawns.Playable", "Pawns.NPC.Boss", "Status.Death.Dead", "Status.DamageImmune"));
        var component = HeldItemEffectService.AddExport(a, "PlayerHitTargetSafety", "/Script/GameplayAbilities", "TargetTagRequirementsGameplayEffectComponent",
            FPackageIndex.FromExport(a.Exports.IndexOf(cdo)), [requirements]);
        HeldItemEffectService.Append(a, cdo, "GEComponents", component);
        c.Write(a, path); VerifyStatus(c.ReadStaged(path), s);
    }
    private static FloatPropertyData Duration(NormalExport cdo) => cdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "DurationMagnitude").Value
        .OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ScalableFloatMagnitude").Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value");
    internal static void VerifyStatus(UAsset a, MeleeStatusSettings s)
    {
        if (Duration(Cdo(a)).Value != s.DurationSeconds || !Cdo(a).Data.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "DurationPolicy").Value.ToString().EndsWith("HasDuration"))
            throw new InvalidDataException("Hit status must have the configured bounded duration.");
        var safety = a.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "PlayerHitTargetSafety");
        var requirements = safety.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ApplicationTagRequirements");
        string[] Read(string name) => requirements.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == name).Value.OfType<GameplayTagContainerPropertyData>().Single().Value.Select(t => t.ToString()).ToArray();
        if (!Read("RequireTags").SequenceEqual(["Pawns.NPC.Goon"]) || !Read("IgnoreTags").SequenceEqual(["Pawns.Playable", "Pawns.NPC.Boss", "Status.Death.Dead", "Status.DamageImmune"])) throw new InvalidDataException("Hit status lost target safety checks.");
        VerifyComponentLinked(a,safety);
    }
    // The shipped mappings leave BP_DefaultHitFrame_Notify instances opaque to UAssetAPI.
    // CUE readback proves both damage fields use this exact import. Redirect its package/class
    // in place, keeping all raw property bytes and package indexes unchanged. Never guess offsets.
    internal const string NativeDamage = "/Game/Characters/Abilities/GameplayEffects/Damage/GenericDamage/GE_GenericDamage_Medium";
    private static string Wrapper(string mod, string original) => SwordCombatService.Root(mod) + "/GE_HitDamage_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original)))[..12];
    internal static void Apply(UAsset montage, SwordCombatService.Context c, string mod, MeleeStatusSettings s)
    {
        if (!Enabled(s)) return;
        if (!montage.Exports.Any(e=>e.GetExportClassType()?.ToString()=="BP_DefaultHitFrame_Notify_C")) throw new InvalidDataException("No supported native hit notify for status application.");
        var packageIndex = montage.Imports.FindIndex(i=>i.ClassName.ToString()=="Package" && i.ObjectName.ToString()==NativeDamage);
        if (packageIndex < 0) throw new InvalidDataException("On-hit statuses currently require the verified GenericDamage_Medium hit binding. This custom attack is not supported.");
        var importedClass = montage.Imports.Single(i=>i.OuterIndex.Index == FPackageIndex.FromImport(packageIndex).Index && i.ObjectName.ToString()=="GE_GenericDamage_Medium_C");
        {
            var path = Wrapper(mod, NativeDamage);
            if (!File.Exists(c.PathFor(path))) {
                var damage = c.Clone(NativeDamage, path); var cdo = Cdo(damage);
                // A status must never replace native damage, team checks or immunity checks.
                if (!cdo.Data.OfType<ArrayPropertyData>().Any(p => p.Name.ToString() == "Executions" && p.Value.Length > 0))
                    throw new InvalidDataException("Attack damage does not match the supported native execution layout.");
                var target = SwordCombatService.Obj(damage, StatusPackage(mod, s), UnrealPathUtil.AssetName(StatusPackage(mod, s)) + "_C", "/Script/Engine", "BlueprintGeneratedClass");
                var additional = HeldItemEffectService.AddExport(damage, "PlayerHitStatus", "/Script/GameplayAbilities", "AdditionalEffectsGameplayEffectComponent",
                    FPackageIndex.FromExport(damage.Exports.IndexOf(cdo)), [
                        new BoolPropertyData(new FName(damage, "bOnApplicationCopyDataFromOriginalSpec")) { Value = true },
                        new ArrayPropertyData(new FName(damage, "OnApplicationGameplayEffects")) { ArrayType = new FName(damage, "StructProperty"), Value = [
                            HeldItemEffectService.Struct(damage, "0", "ConditionalGameplayEffect", HeldItemEffectService.Object(damage, "EffectClass", target), Tags(damage, "RequiredSourceTags"))] }
                    ]);
                additional.ToExport(damage).CreateBeforeSerializationDependencies.Add(target);
                HeldItemEffectService.Append(damage, cdo, "GEComponents", additional);
                c.Write(damage, path);
            }
            montage.Imports[packageIndex].ObjectName = new FName(montage,path);
            importedClass.ObjectName = new FName(montage,UnrealPathUtil.AssetName(path)+"_C");
        }
    }
    internal static void Verify(UAsset montage, SwordCombatService.Context c, string mod, MeleeStatusSettings s)
    {
        if (!Enabled(s)) return;
        VerifyStatus(c.ReadStaged(StatusPackage(mod, s)), s);
        var path = Wrapper(mod,NativeDamage);
        if (montage.Imports.Any(i=>i.ClassName.ToString()=="Package"&&i.ObjectName.ToString()==NativeDamage) ||
            !montage.Imports.Any(i=>i.ClassName.ToString()=="BlueprintGeneratedClass"&&i.ObjectName.ToString()==UnrealPathUtil.AssetName(path)+"_C"&&SwordCombatService.Package(montage,i.OuterIndex)==path)) throw new InvalidDataException("Stale hit status damage binding.");
        {
            var damage = c.ReadStaged(path); var component = damage.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "PlayerHitStatus");
            VerifyComponentLinked(damage,component);
            var list = component.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "OnApplicationGameplayEffects");
            if (list.Value.Length != 1 || SwordCombatService.Package(damage, ((StructPropertyData)list.Value[0]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "EffectClass").Value) != StatusPackage(mod, s)) throw new InvalidDataException("Wrong hit-target status effect.");
        }
    }
    private static void VerifyComponentLinked(UAsset a, NormalExport component)
    {
        var index=FPackageIndex.FromExport(a.Exports.IndexOf(component));
        if (!Cdo(a).Data.OfType<ArrayPropertyData>().Single(p=>p.Name.ToString()=="GEComponents").Value.OfType<ObjectPropertyData>().Any(p=>p.Value.Index==index.Index))
            throw new InvalidDataException("Status component is not linked to its effect.");
    }
}
