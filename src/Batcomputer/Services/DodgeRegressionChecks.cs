using System.Text.Json;

namespace Batcomputer;

internal static class DodgeRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var robin = DodgeProfileService.Ability(DodgeProfileService.Preset.Smallfig);
        var batman = DodgeProfileService.Ability(DodgeProfileService.Preset.Minifig);
        const string setPath = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Robin_DickGrayson";
        var catalog = new AbilityEditorCatalog { InheritedAbilitySets = [new() { PackagePath = setPath,
            GameplayAbilities = [new() { PackagePath = robin, AbilityLevel = 3, InputTag = "Input.Dodge" }, new() { PackagePath = "/Game/Other" }] }],
            GameplayAbilities = [new() { PackagePath = robin }, new() { PackagePath = batman }] };
        var profile = new AbilityLoadoutProfile { FightingStyleId = "preserved", AbilitySets = [new() { PackagePath = setPath,
            AddedGameplayAbilities = [new() { PackagePath = "/Game/KeepMe" }], RemovedGameplayAbilities = ["/Game/Other"] }] };
        var project = new NativeSuitProject { AbilityLoadout = profile, BodyProfile = NativeBodyProfileService.Find("minifig-standard") };
        results.Add((DodgeProfileService.BodyMismatchWarning(project) is not null, "tall body with Robin dodge gets actionable guidance"));
        var originalCatalog = JsonSerializer.Serialize(catalog);
        DodgeProfileService.Apply(profile, catalog, DodgeProfileService.Preset.Minifig);
        var set = profile.AbilitySets[0];
        results.Add((set.AddedGameplayAbilities.Single(g => g.PackagePath == batman) is { AbilityLevel: 3, InputTag: "Input.Dodge" } && set.RemovedGameplayAbilities.Contains(robin),
            "body-matched dodge retains native input and ability level"));
        results.Add((DodgeProfileService.BodyMismatchWarning(project) is null && profile.FightingStyleId == "preserved" &&
            set.AddedGameplayAbilities.Any(g => g.PackagePath == "/Game/KeepMe") && set.RemovedGameplayAbilities.Contains("/Game/Other") && originalCatalog == JsonSerializer.Serialize(catalog),
            "matched dodge clears warning without changing unrelated grants or native catalog"));
        var once = JsonSerializer.Serialize(profile);
        DodgeProfileService.Apply(profile, catalog, DodgeProfileService.Preset.Minifig);
        results.Add((once == JsonSerializer.Serialize(profile), "reapplying a dodge preset is idempotent"));
        project.BodyProfile = NativeBodyProfileService.Find("smallfig-standard");
        results.Add((DodgeProfileService.BodyMismatchWarning(project) is not null, "short body with Batman dodge gets actionable guidance"));
        DodgeProfileService.Apply(profile, catalog, DodgeProfileService.Preset.Smallfig);
        results.Add((DodgeProfileService.BodyMismatchWarning(project) is null && !set.AddedGameplayAbilities.Any(g => g.PackagePath == batman), "switching body size replaces rather than duplicates dodge grants"));
        DodgeProfileService.Apply(profile, catalog, DodgeProfileService.Preset.Native);
        results.Add((!set.RemovedGameplayAbilities.Contains(robin) && set.AddedGameplayAbilities.Count == 1, "restore-native removes only dodge edits"));
        catalog.GameplayAbilities.Clear();
        var before = JsonSerializer.Serialize(profile);
        try { DodgeProfileService.Apply(profile, catalog, DodgeProfileService.Preset.Minifig); results.Add((false, "missing dodge target must fail")); }
        catch (InvalidDataException) { results.Add((before == JsonSerializer.Serialize(profile), "missing target fails without partial mutation")); }
        return results;
    }
}
