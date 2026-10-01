using System.Text.Json;

namespace Batcomputer;

internal static class AnimationSpawnedItemRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var edit = new AnimationSpawnedItemEdit { AnimationPackage = "/Game/Animation/Test/A_Dive", ExportIndex = 1, OriginalMeshPackage = "/Game/Meshes/SM_Claws", SourceFingerprint = new('A', 64), HeldItemId = "claws" };
        var profile = new AbilityLoadoutProfile { HeldItems = [new() { Id = "claws" }], AnimationSpawnedItems = [edit] };
        yield return (AnimationSpawnedItemService.Validate(profile).Count == 0, "animation items target exact export identities and an existing extra prop");
        var copy = AbilityExplorerForm.CloneProfile(profile); var before = AbilityLoadoutService.ConfigurationFingerprint(profile);
        copy.AnimationSpawnedItems[0].Hide = true;
        yield return (!edit.Hide && AbilityLoadoutService.ConfigurationFingerprint(copy) != before, "animation item edits deep-clone and invalidate staged builds");
        var roundtrip = JsonSerializer.Deserialize<AbilityLoadoutProfile>(JsonSerializer.Serialize(profile))!;
        yield return (roundtrip.AnimationSpawnedItems.Single().Key == edit.Key && AbilityLoadoutService.ConfigurationFingerprint(roundtrip) == before, "animation item replacements survive saved project round trips");
        copy.AnimationSpawnedItems.Add(copy.AnimationSpawnedItems[0].Clone());
        yield return (AnimationSpawnedItemService.Validate(copy).Count > 0, "duplicate animation spawn edits are rejected");
        copy = AbilityExplorerForm.CloneProfile(profile); copy.HeldItems = [];
        yield return (AnimationSpawnedItemService.Validate(copy).Count > 0, "removing a referenced prop cannot silently restore a different animation mesh");
        copy.AnimationSpawnedItems[0].Hide = true;
        yield return (AnimationSpawnedItemService.Validate(copy).Count == 0, "hiding an animation item does not require a replacement prop");
        copy.AnimationSpawnedItems[0].AnimationPackage = "/Game/../Native";
        yield return (AnimationSpawnedItemService.Validate(copy).Count > 0, "animation item edits reject escaping source paths");
        var edges = new Dictionary<string,string[]> { ["Root"] = ["Controller", "Other"], ["Controller"] = ["Montage", "Loop"], ["Montage"] = ["Strike"], ["Other"] = ["Unrelated"], ["Loop"] = ["Controller"] };
        yield return (AnimationSpawnedItemService.Impacted(["Strike"], edges).SetEquals(["Strike", "Montage", "Controller", "Root", "Loop"]), "animation item closure handles cycles and does not clone unrelated siblings");
        var owner = CustomCharacterProjectService.CreateRecipe(null, "Test", "Test", "Test"); owner.AbilityLoadout = profile; owner.VoiceProfileId = "private-voice"; owner.FaceAnimationBlueprintPackage = FaceAnimationService.BruceWayne;
        var suit = CustomCharacterProjectService.CreateRecipe(owner, "Alternate", "Test", "Alternate", owner.SlotId);
        yield return (suit.AbilityLoadout!.AnimationSpawnedItems.Single().HeldItemId == "claws" && suit.VoiceProfileId == owner.VoiceProfileId && suit.FaceAnimationBlueprintPackage == owner.FaceAnimationBlueprintPackage && !ReferenceEquals(suit.AbilityLoadout, owner.AbilityLoadout), "new character suits inherit voice, face and animation items as independent recipes");
        CharacterRebaseService.CaptureInitial(owner, suit); owner.AbilityLoadout.AnimationSpawnedItems[0].Hide = true;
        var rebased = CharacterRebaseService.Compose(owner, suit, owner, new HashSet<string> { "abilities" });
        yield return (rebased.AbilityLoadout!.AnimationSpawnedItems.Single().Hide && !suit.AbilityLoadout.AnimationSpawnedItems.Single().Hide, "character rebases propagate animation-item changes without modifying the existing suit");
    }
}
