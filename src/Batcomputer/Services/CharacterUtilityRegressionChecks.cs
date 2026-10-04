using System.Text.Json;

namespace Batcomputer;

internal static class CharacterUtilityRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        void Check(bool condition, string name) { output.WriteLine((condition ? "PASS " : "FAIL ") + name); if (!condition) failures.Add(name); }
        Check(!CharacterUtilityService.Enabled(null) && !CharacterUtilityService.Enabled(new()), "character utilities are opt-in for old and default projects");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -.1f, 0, 10.1f })
            Check(CharacterUtilityService.ValidationError(new() { Healing = true, HealingPercentPerSecond = invalid }) is not null, "invalid healing rate rejected: " + invalid);
        Check(CharacterUtilityService.ValidationError(new() { Healing = true, HealingPercentPerSecond = .1f }) is null &&
              CharacterUtilityService.ValidationError(new() { Healing = true, HealingPercentPerSecond = 10 }) is null, "bounded healing rates accepted");
        var profile = new AbilityLoadoutProfile { Utilities = new() { Tracking = true, Healing = true, HealingPercentPerSecond = 2 } };
        var p = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/UtilityTest/Characters/BP_Test" }, AbilityLoadout = profile };
        var copy = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(p))!;
        Check(copy.AbilityLoadout?.Utilities is { Tracking: true, Healing: true, HealingPercentPerSecond: 2 } &&
              !ReferenceEquals(profile.Utilities, copy.AbilityLoadout.Utilities), "utility settings survive independent project save/clone");
        var editorCopy = AbilityExplorerForm.CloneProfile(profile);
        Check(editorCopy.Utilities is { Tracking: true, Healing: true, HealingPercentPerSecond: 2 } &&
              !ReferenceEquals(profile.Utilities, editorCopy.Utilities), "ability workshop clones and saves independent utility settings without dropping them");
        editorCopy.Utilities!.Tracking = false;
        Check(profile.Utilities.Tracking && editorCopy.Utilities.Healing, "removing tracking on a workshop copy preserves healing and the original project");
        Check(AbilityLoadoutService.HasCustomizations(p) && CharacterUtilityService.NeedsSet(p), "utility-only projects request the custom archetype pipeline");
        var before = AbilityLoadoutService.ConfigurationFingerprint(profile);
        profile.Utilities.HealingPercentPerSecond = 3;
        Check(before != AbilityLoadoutService.ConfigurationFingerprint(profile), "healing rate invalidates build configuration fingerprint");
        profile.Utilities.Tracking = false;
        var healingPlan = AbilityDependencyService.Build(p, "", []);
        Check(healingPlan.RequiredAbilitySets.Contains(CharacterUtilityService.SetPackage(p)) &&
              !healingPlan.RequiredMontageAnimSets.Contains(CharacterUtilityService.TrackingMas), "healing alone does not install tracking or unrelated animations");
        profile.Utilities.Tracking = true;
        var plan = AbilityDependencyService.Build(p, "", []);
        Check(plan.RequiredAbilitySets.Contains(CharacterUtilityService.SetPackage(p)) && plan.RequiredMontageAnimSets.Contains(CharacterUtilityService.TrackingMas) &&
              plan.RequiredLayerAnimSets.Contains(CharacterUtilityService.TrackingLas) && !plan.RequiredAbilitySets.Any(s => s.EndsWith("/AS_Batman")), "tracking includes both animation closures without Batman gadgets");
        profile.Utilities.Healing = false;
        profile.AbilitySets.Add(new() { PackagePath = CharacterUtilityService.TrackingSet });
        Check(!CharacterUtilityService.NeedsSet(p), "existing native tracking grant is not duplicated");
        profile.Utilities.Tracking = false;
        Check(!CharacterUtilityService.NeedsSet(p), "disabling utilities stops generation");
    }
}
