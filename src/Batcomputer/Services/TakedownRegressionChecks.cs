using System.Text.Json;

namespace Batcomputer;

internal static class TakedownRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool condition, string label) => results.Add((condition, label));
        const string melee = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Robin_DickGrayson";
        const string unrelated = "/Game/GA_CustomAttack";
        var source = new AbilitySetCatalogEntry { PackagePath = melee, GameplayAbilities = [
            new() { PackagePath = TakedownProfileService.MainAbility(TakedownProfileService.Preset.Smallfig), AbilityLevel = 3, InputTag = "Input.Test" },
            new() { PackagePath = TakedownProfileService.End(TakedownProfileService.Preset.Smallfig) },
            new() { PackagePath = unrelated }] };
        var catalog = new AbilityEditorCatalog { InheritedAbilitySets = [source], GameplayAbilities =
            Enum.GetValues<TakedownProfileService.Preset>().Where(p => p != TakedownProfileService.Preset.Native)
                .SelectMany(p => new[] { TakedownProfileService.MainAbility(p), TakedownProfileService.End(p) })
                .Select(p => new GameplayAbilityCatalogEntry { PackagePath = p }).ToList() };
        AbilityLoadoutProfile Fixture() => new() { FightingStyleId = "robin-dual-sticks", AbilitySets = [new() { PackagePath = melee,
            AddedGameplayAbilities = [new() { PackagePath = unrelated + "Added" }], RemovedGameplayAbilities = [unrelated] }] };
        var profile = Fixture();
        var untouchedSource = JsonSerializer.Serialize(catalog);
        TakedownProfileService.Apply(profile, catalog, TakedownProfileService.Preset.Minifig);
        var set = profile.AbilitySets.Single();
        Check(profile.FightingStyleId == "robin-dual-sticks" && set.RemovedGameplayAbilities.Contains(unrelated) &&
            set.AddedGameplayAbilities.Any(g => g.PackagePath == unrelated + "Added") && JsonSerializer.Serialize(catalog) == untouchedSource,
            "takedown presets preserve combat style, unrelated edits and the source catalog");
        Check(set.RemovedGameplayAbilities.Contains(TakedownProfileService.MainAbility(TakedownProfileService.Preset.Smallfig)) &&
            set.RemovedGameplayAbilities.Contains(TakedownProfileService.End(TakedownProfileService.Preset.Smallfig)) &&
            set.AddedGameplayAbilities.Single(g => g.PackagePath == TakedownProfileService.MainAbility(TakedownProfileService.Preset.Minifig)) is { AbilityLevel: 3, InputTag: "Input.Test" },
            "Minifig preset replaces both Robin takedown grants while retaining level and input metadata");
        var first = JsonSerializer.Serialize(profile);
        TakedownProfileService.Apply(profile, catalog, TakedownProfileService.Preset.Minifig);
        Check(first == JsonSerializer.Serialize(profile), "reapplying a takedown preset does not duplicate grants");
        TakedownProfileService.Apply(profile, catalog, TakedownProfileService.Preset.Smallfig);
        Check(set.AddedGameplayAbilities.Count(g => g.PackagePath == TakedownProfileService.MainAbility(TakedownProfileService.Preset.Smallfig)) == 1 &&
            !set.AddedGameplayAbilities.Any(g => g.PackagePath == TakedownProfileService.MainAbility(TakedownProfileService.Preset.Minifig)),
            "switching takedown size replaces the previous preset instead of stacking abilities");
        TakedownProfileService.Apply(profile, catalog, TakedownProfileService.Preset.Native);
        Check(JsonSerializer.Serialize(profile) == JsonSerializer.Serialize(Fixture()), "restoring native takedowns preserves other manual ability edits exactly");

        void RejectUnchanged(AbilityLoadoutProfile value, AbilityEditorCatalog data, string label)
        {
            var before = JsonSerializer.Serialize(value); bool rejected = false;
            try { TakedownProfileService.Apply(value, data, TakedownProfileService.Preset.Minifig); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && JsonSerializer.Serialize(value) == before, label);
        }
        var duplicate = Fixture(); duplicate.AbilitySets.Add(new() { PackagePath = melee });
        RejectUnchanged(duplicate, catalog, "ambiguous melee sets reject takedown edits without mutation");
        var disabled = Fixture(); disabled.AbilitySets[0].Enabled = false;
        RejectUnchanged(disabled, catalog, "disabled melee sets are not silently enabled by takedown presets");
        var duplicateGrant = Fixture(); duplicateGrant.AbilitySets[0].AddedGameplayAbilities.Add(new() { PackagePath = TakedownProfileService.MainAbility(TakedownProfileService.Preset.Smallfig) });
        RejectUnchanged(duplicateGrant, catalog, "duplicate active takedown grants require explicit repair");
        catalog.SavedLoadoutNeedsRemap = true;
        RejectUnchanged(Fixture(), catalog, "stale donor loadouts reject takedown presets"); catalog.SavedLoadoutNeedsRemap = false;
        var noEnd = Fixture(); noEnd.AbilitySets[0].RemovedGameplayAbilities.Add(TakedownProfileService.End(TakedownProfileService.Preset.Smallfig));
        TakedownProfileService.Apply(noEnd, catalog, TakedownProfileService.Preset.Minifig);
        Check(!noEnd.AbilitySets[0].AddedGameplayAbilities.Any(g => g.PackagePath == TakedownProfileService.End(TakedownProfileService.Preset.Minifig)) &&
            noEnd.AbilitySets[0].RemovedGameplayAbilities.Contains(TakedownProfileService.End(TakedownProfileService.Preset.Smallfig)),
            "an intentionally removed end-of-encounter ability stays removed");
        catalog.GameplayAbilities.RemoveAll(g => g.PackagePath == TakedownProfileService.End(TakedownProfileService.Preset.Minifig));
        RejectUnchanged(Fixture(), catalog, "a missing replacement dependency rejects the entire takedown edit");
        return results;
    }
}
