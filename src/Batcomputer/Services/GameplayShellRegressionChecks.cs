namespace Batcomputer;

/// <summary>
/// Portable checks for the body-profile regression where visual cleanup removed non-visual
/// character machinery, then every clean rebuild faithfully replayed the damage.
/// </summary>
internal static class GameplayShellRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);

    internal static IReadOnlyList<Result> Run()
    {
        var results = new List<Result>();

        results.Add(new Result(
            GameplayShellComponentPolicy.IsRequired("WubDialogueVoiceActor:0") &&
            GameplayShellComponentPolicy.IsRequired("WubDialogueVoiceActor_2_GEN_VARIABLE") &&
            GameplayShellComponentPolicy.IsRequired("TtCharacterAssetMinion:0") &&
            !GameplayShellComponentPolicy.IsRequired("Head:0") &&
            !GameplayShellComponentPolicy.IsRequired("Cape:0"),
            "dialogue and character-presentation SCS nodes are protected while visual parts remain removable"));

        var migrationFixture = FixtureProject();
        var migrated = GameplayShellComponentPolicy.RemoveLegacyAutomaticRemovals(migrationFixture);
        results.Add(new Result(
            migrated.SequenceEqual(
                new[] { "TtCharacterAssetMinion", "WubDialogueVoiceActor" },
                StringComparer.OrdinalIgnoreCase) &&
            migrationFixture.Requirements.Count == 2 &&
            migrationFixture.Requirements.Any(requirement =>
                requirement.TargetComponent.Equals("Cape:0", StringComparison.OrdinalIgnoreCase)) &&
            migrationFixture.Requirements.Any(requirement =>
                requirement.TargetComponent.Equals("WubDialogueVoiceActor:0", StringComparison.OrdinalIgnoreCase) &&
                requirement.Notes.StartsWith("Manual", StringComparison.OrdinalIgnoreCase)),
            "legacy tool-authored gameplay-node removals migrate without erasing cosmetic or explicit manual declarations"));

        var saveRoot = Path.Combine(
            Path.GetTempPath(),
            "BatcomputerGameplayShellRegression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saveFixture = FixtureProject();
            var service = new SuitProjectService(saveRoot);
            var snapshot = service.CaptureProjectSave(saveFixture);
            var frozen = service.MaterializeProjectSaveSnapshot(snapshot);
            results.Add(new Result(
                frozen.Requirements.Count == 2 &&
                !frozen.Requirements.Any(GameplayShellComponentPolicy.IsLegacyAutomaticRemoval) &&
                frozen.Requirements.Any(requirement =>
                    requirement.TargetComponent.Equals("WubDialogueVoiceActor:0", StringComparison.OrdinalIgnoreCase) &&
                    requirement.Notes.StartsWith("Manual", StringComparison.OrdinalIgnoreCase)),
                "every project-save capture persists the precise legacy gameplay-shell migration"));
        }
        catch
        {
            results.Add(new Result(false,
                "every project-save capture persists the precise legacy gameplay-shell migration"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(saveRoot))
                {
                    Directory.Delete(saveRoot, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup must not hide the assertion result.
            }
        }

        var missing = StageValidationService.MissingRequiredGameplayShellComponentsForTest(
            new[] { "Head", "WubDialogueVoiceActor", "TtCharacterAssetMinion" },
            new[] { "Head", "WubDialogueVoiceActor_2_GEN_VARIABLE" });
        results.Add(new Result(
            missing.SequenceEqual(new[] { "TtCharacterAssetMinion" }, StringComparer.OrdinalIgnoreCase),
            "stage validation detects a missing donor gameplay node and ignores cosmetic-node differences"));

        results.Add(new Result(
            NativeBodyProfileService.ProtectedGameplayContractMatchesForTest(
                "BP_CAT_Archetype_Batman_C",
                "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman",
                new[] { "WubDialogueVoiceActor", "TtCharacterAssetMinion", "Head" },
                "bp_cat_archetype_batman_c",
                "/game/characters/minifig/batman/bp_cat_archetype_batman",
                new[] { "TtCharacterAssetMinion:0", "WubDialogueVoiceActor_GEN_VARIABLE", "Cape" }) &&
            !NativeBodyProfileService.ProtectedGameplayContractMatchesForTest(
                "BP_CAT_Archetype_Batman_C",
                "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman",
                new[] { "WubDialogueVoiceActor", "TtCharacterAssetMinion" },
                "BP_CAT_Archetype_Batman_C",
                "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman",
                new[] { "WubDialogueVoiceActor" }) &&
            !NativeBodyProfileService.ProtectedGameplayContractMatchesForTest(
                "BP_CAT_Archetype_Batman_C",
                "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman",
                new[] { "WubDialogueVoiceActor", "TtCharacterAssetMinion" },
                "BP_CAT_Archetype_Robin_C",
                "/Game/Characters/Minifig/Robin/BP_CAT_Archetype_Robin",
                new[] { "WubDialogueVoiceActor", "TtCharacterAssetMinion" }),
            "native body mutation preserves the donor parent plus speech and character/suit runtime nodes"));

        return results;
    }

    private static NativeSuitProject FixtureProject() => new()
    {
        SlotId = "gameplay-shell-regression",
        Requirements =
        [
            new NativeSuitRequirement
            {
                Kind = "remove-component",
                TargetComponent = "WubDialogueVoiceActor:0",
                Notes = "Auto-hidden on visual-base select: donor has no 'other' part."
            },
            new NativeSuitRequirement
            {
                Kind = "remove-component",
                TargetComponent = "TtCharacterAssetMinion:0",
                Notes = "Auto-hidden on base select: donor has no 'other' part."
            },
            new NativeSuitRequirement
            {
                Kind = "remove-component",
                TargetComponent = "Cape:0",
                Notes = "Auto-hidden on visual-base select: visual has no 'cape' part."
            },
            new NativeSuitRequirement
            {
                Kind = "remove-component",
                TargetComponent = "WubDialogueVoiceActor:0",
                Notes = "Manual fixture declaration"
            },
        ]
    };
}
