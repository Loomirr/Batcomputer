using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Native tracking and configurable periodic healing, rebuilt from saved suit settings.</summary>
internal static class CharacterUtilityService
{
    internal const string TrackingSet = "/Game/Characters/Abilities/SpectralVision/AS_SpectralVision";
    internal const string TrackingMas = "/Game/Animation/MontageAnimSets/Interaction/MAS_Interaction_SpectralVision";
    internal const string TrackingLas = "/Game/Animation/LayerAnimSets/Interaction/LAS_Interaction_SpectralVision";
    internal const string NativeHealing = "/Game/Characters/Abilities/Difficulty/GE_Normal_HealthRegen";
    private const string NativeExecution = "/Game/Characters/Abilities/Difficulty/BP_FlatAmount_HealExecution";
    private static float NativeMultiplier(SwordCombatService.Context context)
    {
        var execution = context.Read(NativeExecution);
        var cdo = execution.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
        var multiplier = cdo.Data.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "RegenMultiplier").Value;
        if (!float.IsFinite(multiplier) || multiplier <= 0) throw new InvalidDataException("The native regeneration multiplier is invalid.");
        return multiplier;
    }
    internal static bool Enabled(CharacterUtilitySettings? settings) => settings is { Tracking: true } or { Healing: true };
    internal static string? ValidationError(CharacterUtilitySettings settings) =>
        settings.Healing && (!float.IsFinite(settings.HealingPercentPerSecond) || settings.HealingPercentPerSecond < .1f || settings.HealingPercentPerSecond > 10)
            ? "Healing must be between 0.1% and 10% of maximum health per second." : null;
    private static string Mod(NativeSuitProject project)
    {
        var mod = project.TargetPackages.Playable.Split('/').ElementAtOrDefault(3) ?? "";
        if (!project.TargetPackages.Playable.StartsWith("/Game/Mods/", StringComparison.Ordinal) || mod.Length == 0 ||
            mod.Any(c => !char.IsLetterOrDigit(c) && c != '_')) throw new InvalidDataException("Character utilities require a suit-local package namespace.");
        return mod;
    }
    internal static string Root(NativeSuitProject project) => "/Game/Mods/" + Mod(project) + "/CharacterUtilities";
    internal static string SetPackage(NativeSuitProject project) => Root(project) + "/AS_CharacterUtilities";
    internal static bool HasNativeTracking(AbilityLoadoutProfile? profile) => profile is not null &&
        (profile.AbilitySets.Count > 0 ? profile.AbilitySets.Where(s => s.Enabled).Select(s => s.PackagePath) : profile.DonorAbilitySetPackages)
        .Any(p => p.Equals(TrackingSet, StringComparison.OrdinalIgnoreCase));
    internal static bool NeedsSet(NativeSuitProject project) => project.AbilityLoadout?.Utilities is { } settings &&
        (settings.Healing || settings.Tracking && !HasNativeTracking(project.AbilityLoadout));

    internal static void Generate(NativeSuitProject project, string contentRoot, Usmap mappings, Action<string> log)
    {
        if (!NeedsSet(project)) return;
        var settings = project.AbilityLoadout!.Utilities!;
        if (ValidationError(settings) is { } error) throw new InvalidDataException(error);
        var root = Root(project);
        using var context = new SwordCombatService.Context(AppSettings.Current.EffectiveExtractedContentRoot(), contentRoot, Mod(project), mappings, root);
        var set = context.Clone(TrackingSet, SetPackage(project));
        var mutation = new AbilityAssetMutationService();
        if (!settings.Tracking || HasNativeTracking(project.AbilityLoadout))
        {
            var inspection = mutation.InspectAbilitySet(context.PathFor(SetPackage(project)));
            if (!inspection.Success) throw new InvalidDataException(inspection.Error);
            var result = mutation.ApplyGameplayAbilityEdits(context.PathFor(SetPackage(project)), inspection.GameplayAbilities.Select(g =>
                new AbilityAssetMutationService.GameplayAbilityEdit { Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Remove, TargetPackagePath = g.PackagePath }).ToArray());
            if (!result.Success) throw new InvalidDataException(result.Error);
        }
        if (settings.Healing)
        {
            var healPackage = root + "/GE_PassiveHealing";
            var heal = context.Clone(NativeHealing, healPackage);
            ConfigureHealing(heal, settings.HealingPercentPerSecond, NativeMultiplier(context));
            context.Write(heal, healPackage);
            var effect = mutation.AddGameplayEffects(context.PathFor(SetPackage(project)), [new()
            {
                PackagePath = healPackage, EffectLevelOverride = 1,
                SourceAbilitySetUassetPath = ExtractedPackagePathService.ResolvePackageUasset(AppSettings.Current.EffectiveExtractedContentRoot(), "/Game/Characters/Minifig/Catwoman/AS_Catwoman") ?? "",
                SourceEffectPackagePath = "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_Agile"
            }]);
            if (!effect.Success) throw new InvalidDataException(effect.Error);
        }
        Verify(project, contentRoot, mappings, checkDprd: false);
        log($"Character utilities: tracking={(settings.Tracking ? "native clue interactions" : "off")}; healing={(settings.Healing ? settings.HealingPercentPerSecond + "% max health/second" : "off")}. Native assets unchanged; runtime check required.");
    }
    internal static void ConfigureHealing(UAsset asset, float percent, float nativeMultiplier)
    {
        var cdo = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal));
        var period = cdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "Period");
        period.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value").Value = 1;
        var executions = cdo.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Executions");
        var modifiers = executions.Value.Cast<StructPropertyData>().Single().Value.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "CalculationModifiers");
        var modifier = modifiers.Value.Cast<StructPropertyData>().Single();
        var capture = modifier.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "CapturedAttribute");
        var attribute = Walk(capture.Value).OfType<StrPropertyData>().Single(p => p.Name.ToString() == "AttributeName").Value;
        if (attribute?.ToString() != "MaxHealth") throw new InvalidDataException("Native healing calculation no longer captures MaxHealth.");
        var magnitude = modifier.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ModifierMagnitude");
        var scalable = magnitude.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ScalableFloatMagnitude");
        scalable.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value").Value = percent / 100 / nativeMultiplier;
        var duration = cdo.Data.OfType<EnumPropertyData>().FirstOrDefault(p => p.Name.ToString() == "DurationPolicy");
        if (duration is null)
        {
            duration = new EnumPropertyData(new FName(asset, "DurationPolicy")) { EnumType = new FName(asset, "EGameplayEffectDurationType") };
            cdo.Data.Add(duration);
        }
        duration.Value = new FName(asset, "Infinite");
        var initial = cdo.Data.OfType<BoolPropertyData>().FirstOrDefault(p => p.Name.ToString() == "bExecutePeriodicEffectOnApplication");
        if (initial is null) { initial = new BoolPropertyData(new FName(asset, "bExecutePeriodicEffectOnApplication")); cdo.Data.Add(initial); }
        initial.Value = false;
    }
    private static IEnumerable<PropertyData> Walk(IEnumerable<PropertyData> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            foreach (var child in Walk(row is StructPropertyData s ? s.Value : row is ArrayPropertyData a ? a.Value : [])) yield return child;
        }
    }
    internal static void Verify(NativeSuitProject project, string contentRoot, Usmap mappings, bool checkDprd = true)
    {
        if (!NeedsSet(project)) return;
        var root = Root(project); var settings = project.AbilityLoadout!.Utilities!;
        using var context = new SwordCombatService.Context(AppSettings.Current.EffectiveExtractedContentRoot(), contentRoot, Mod(project), mappings, root);
        var set = new AbilityAssetMutationService().InspectAbilitySet(context.PathFor(SetPackage(project)));
        var expected = settings.Tracking && !HasNativeTracking(project.AbilityLoadout) ? new[]
        {
            "/Game/Characters/Abilities/SpectralVision/GA_SpectralVisionAbility",
            "/Game/Characters/Abilities/SpectralVision/GA_UseSpectralVisionInteractSpot"
        } : [];
        if (!set.Success || !set.GameplayAbilities.Select(g => g.PackagePath).SequenceEqual(expected) ||
            !set.GameplayEffects.Select(g => g.PackagePath).SequenceEqual(settings.Healing ? [root + "/GE_PassiveHealing"] : []) ||
            set.Attributes.Count + set.GameplayData.Count + set.ActorGameplayCues.Count + set.StaticGameplayCues.Count != 0)
            throw new InvalidDataException("Character utility AbilitySet has missing or unrelated grants.");
        if (settings.Healing)
        {
            var heal = context.ReadStaged(root + "/GE_PassiveHealing");
            // Re-read all scalar/control fields rather than trusting settings or the producer.
            var cdo = heal.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
            var rows = Walk(cdo.Data).ToArray();
            var fraction = rows.OfType<StructPropertyData>().Where(p => p.Name.ToString() == "ScalableFloatMagnitude")
                .Single().Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value").Value;
            var period = cdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "Period").Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value").Value;
            if (Math.Abs(fraction * NativeMultiplier(context) - settings.HealingPercentPerSecond / 100) > .00001f || period != 1 ||
                cdo.Data.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bExecutePeriodicEffectOnApplication").Value ||
                cdo.Data.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "DurationPolicy").Value.ToString() != "Infinite")
                throw new InvalidDataException("Saved healing rate or periodic policy was not replayed.");
        }
        if (checkDprd)
        {
            var dprd = Path.Combine(contentRoot, "Mods", Mod(project), "Characters", "DA_DPRD_" + Mod(project) + ".uasset");
            var sets = new AbilityAssetMutationService().InspectDprdAbilitySets(dprd);
            if (!sets.Success || sets.AbilitySets.Count(s => s.PackagePath == SetPackage(project)) != 1)
                throw new InvalidDataException("Character utility set is missing or duplicated on the built character.");
        }
    }
}
