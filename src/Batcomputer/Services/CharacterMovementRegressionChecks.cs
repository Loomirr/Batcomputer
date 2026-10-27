using System.Text.Json;

namespace Batcomputer;

internal static class CharacterMovementRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var legacy = JsonSerializer.Deserialize<NativeSuitProject>("{}")!;
        yield return (!CharacterMovementService.Enabled(legacy.MovementAttributes) &&
            !CharacterMovementService.Enabled(new CharacterMovementSettings()), "movement overrides are opt-in for old and new projects");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0, -1 })
        {
            var inputs = new CharacterMovementSettings[] { new() { RunSpeedMultiplier = invalid }, new() { SprintSpeed = invalid },
                new() { JumpHeightMultiplier = invalid }, new() { GlideSpeedMultiplier = invalid }, new() { GlideDescentMultiplier = invalid } };
            yield return (inputs.All(settings => CharacterMovementService.ValidationError(settings) is not null), "nonfinite and nonpositive movement values rejected: " + invalid);
        }
        var settings = new CharacterMovementSettings { RunSpeedMultiplier = 1.12f, SprintSpeed = 1200,
            JumpHeightMultiplier = 2, GlideSpeedMultiplier = 1.5f, GlideDescentMultiplier = .5f };
        var p = new NativeSuitProject { MovementAttributes = settings, TargetPackages = new() { Playable = "/Game/Mods/MovementTest/Characters/BP_Test" } };
        yield return (AnimArchetypeGraftService.RequiresCustomArchetype(p) && AnimArchetypeGraftService.RequiresGeneratedDprd(p, "") &&
            StageValidationService.RequiresGeneratedDprdCertificate(p, "", [], []), "movement-only suits request generation and validation of a local DPRD");
        var owner = CustomCharacterProjectService.CreateRecipe(p, "Movement owner", "MovementOwner", "MovementOwner");
        var child = CustomCharacterProjectService.CreateRecipe(owner, "Movement child", "MovementOwner", "Child", owner.SlotId);
        yield return (child.MovementAttributes?.RunSpeedMultiplier == 1.12f && !ReferenceEquals(child.MovementAttributes, owner.MovementAttributes),
            "new suits inherit independent movement settings from their custom character");
        child.MovementAttributes!.RunSpeedMultiplier = 1.8f;
        var keep = CharacterRebaseService.Compose(owner, child, owner, new HashSet<string>());
        var take = CharacterRebaseService.Compose(owner, child, owner, new HashSet<string> { "abilities" });
        yield return (keep.MovementAttributes?.RunSpeedMultiplier == 1.8f && take.MovementAttributes?.RunSpeedMultiplier == 1.12f &&
            owner.MovementAttributes?.RunSpeedMultiplier == 1.12f, "rebase can preserve local movement or accept the parent without mutating either recipe");
        var ordinary = new NativeSuitProject { TargetPackages = p.TargetPackages };
        yield return (CharacterMovementService.ProjectError(ordinary) is null &&
            JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(p))!.MovementAttributes?.SprintSpeed == 1200,
            "ordinary recipes inherit defaults and configured movement survives JSON roundtrip");
        var replacer = new NativeSuitProject { ReplacementTarget = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Playable", MovementAttributes = settings };
        yield return (CharacterMovementService.ProjectError(replacer) is not null, "visual replacers reject gameplay movement overrides");
        var custom = new NativeSuitProject { MovementAttributes = settings, GameplayAnimationGraphs = [new() { Abilities = [new() {
            OriginalPackage = CharacterMovementService.SprintAbility, ReplacementPackage = "/Game/Mods/Example/GA_CustomSprint" }] }] };
        yield return (CharacterMovementService.ProjectError(custom) is not null, "custom sprint controllers cannot silently bypass a configured sprint override");
        custom.MovementAttributes = new() { RunSpeedMultiplier = 1.2f };
        yield return (CharacterMovementService.ProjectError(custom) is null, "other movement edits preserve an existing custom sprint controller");
    }
}
