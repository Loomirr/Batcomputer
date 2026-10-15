using System.Text.Json;

namespace Batcomputer;

internal static class CharacterRebaseRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var visualOwner = CustomCharacterProjectService.CreateRecipe(null, "Visual owner", "Visual", "Visual");
        visualOwner.AbilityLoadout = new() { HeldItems = [new() { Id = "prop", MaterialPackage = "/Game/Owner/Material", PreferOverAnimationEmptyHands = true }], Utilities = new() { Healing = true } };
        var visualSuit = CustomCharacterProjectService.CreateRecipe(visualOwner, "Visual suit", "Visual", "Suit", visualOwner.SlotId);
        visualSuit.AbilityLoadout!.HeldItems![0].MaterialPackage = "/Game/Suit/Material";
        visualSuit.AbilityLoadout.HeldItems[0].CustomModel = new() { SourceName = "Suit mesh", Scale = 0.8f };
        visualSuit.AbilityLoadout.HeldItems[0].PreferOverAnimationEmptyHands = false;
        visualSuit.MaterialAssignments = [new() { Component = "Body", MiPackagePath = "/Game/Suit/Body" }];
        visualSuit.PartGrafts = [new()]; visualSuit.CustomStaticMeshes = [new()]; visualSuit.SkinnedMeshes = [new()];
        visualSuit.PreviewPartPlacements = [new()]; visualSuit.GliderMaterial = "/Game/Suit/Cape"; visualSuit.IconMenu = "/Game/Suit/Icon";
        visualSuit.GeneratedTextures = [new() { PackagePath = "/Game/Suit/Texture" }];
        var visualBefore = JsonSerializer.Serialize(visualSuit);
        var safeSections = CharacterRebaseService.Sections().Where(s => CharacterRebaseService.IsGameplayRefreshSection(s.Id)).Select(s => s.Id).ToHashSet();
        var visualResult = CharacterRebaseService.Compose(visualOwner, visualSuit, visualOwner, safeSections);
        yield return (visualResult.MaterialAssignments[0].MiPackagePath == "/Game/Suit/Body" && visualResult.PartGrafts.Count == 1 &&
            visualResult.CustomStaticMeshes.Count == 1 && visualResult.SkinnedMeshes.Count == 1 && visualResult.PreviewPartPlacements.Count == 1 &&
            visualResult.GeneratedTextures[0].PackagePath == "/Game/Suit/Texture" && visualResult.IconMenu == "/Game/Suit/Icon" && visualResult.GliderMaterial == "/Game/Suit/Cape",
            "gameplay refresh preserves suit materials, parts, models, placements, textures, cape and icons");
        yield return (visualResult.AbilityLoadout!.HeldItems![0].MaterialPackage == "/Game/Suit/Material" &&
            visualResult.AbilityLoadout.HeldItems[0].CustomModel?.Scale == 0.8f &&
            visualResult.AbilityLoadout.HeldItems[0].PreferOverAnimationEmptyHands && visualResult.AbilityLoadout.Utilities!.Healing,
            "ability refresh keeps matched held-item visuals while adopting updated behavior");
        visualResult.AbilityLoadout.HeldItems[0].CustomModel!.Scale = 2;
        yield return (JsonSerializer.Serialize(visualSuit) == visualBefore, "preserved held-item visuals are deep copies, never mutations of the saved suit");
        var resetVisuals = CharacterRebaseService.Compose(visualOwner, visualSuit, visualOwner, new HashSet<string> { "abilities", "appearance" });
        yield return (resetVisuals.AbilityLoadout!.HeldItems![0].MaterialPackage == "/Game/Owner/Material", "explicit appearance reset still adopts the parent held-item visuals");
        yield return (resetVisuals.GeneratedTextures.Single().PackagePath == "/Game/Suit/Texture" &&
            !ReferenceEquals(resetVisuals.GeneratedTextures[0], visualSuit.GeneratedTextures[0]),
            "appearance rebase retains independent local texture recipes even when shared parent recipes are empty");
        visualOwner.GeneratedTextures = [new() { PackagePath = "/Game/Mods/CC_Visual_Visual/Textures/T_Base" }];
        var textureCopy = CustomCharacterProjectService.CreateRecipe(visualOwner, "Test", "Visual", "Copy", visualOwner.SlotId);
        yield return (textureCopy.GeneratedTextures.Count == 0 && visualOwner.GeneratedTextures.Count == 1,
            "new suit shares base textures without duplicating or mutating parent recipes");
        textureCopy.GeneratedTextures = [new() { PackagePath = "/Game/Mods/CC_Visual_Copy/Textures/T_Base" },
            new() { PackagePath = "/Game/Mods/CC_Visual_Copy/Textures/T_Local" }];
        var retained = CharacterTextureReferenceService.Preserve(visualOwner, textureCopy,
            "/Game/Mods/CC_Visual_Visual", "/Game/Mods/CC_Visual_Copy");
        yield return (retained.GeneratedTextures.Single().PackagePath.EndsWith("/T_Local") && textureCopy.GeneratedTextures.Count == 2,
            "texture sharing removes only inherited recipes and preserves local imports without mutating input");
        var textureRebase = CharacterRebaseService.Compose(visualOwner, resetVisuals, visualOwner, new HashSet<string> { "appearance" });
        var again = CharacterRebaseService.Compose(visualOwner, textureRebase, visualOwner, new HashSet<string> { "appearance" });
        yield return (again.GeneratedTextures.Count == 2 && again.GeneratedTextures.Select(t => t.PackagePath).Distinct().Count() == 2,
            "repeated appearance rebases retain imports without duplicate recipes");
        var owner = CustomCharacterProjectService.CreateRecipe(null, "Example character", "Example", "Example");
        owner.MaterialAssignments = [new() { Component = "Body", MiPackagePath = "/Game/Example/Original" }];
        owner.AnimationSlotOverrides = [new() { ReplacementPackage = "/Game/Mods/CC_Example_Example/Animations/AM_Test" }];
        var suit = CustomCharacterProjectService.CreateRecipe(owner, "Alternate suit", "Example", "Alternate", owner.SlotId);
        yield return (suit.AnimationSlotOverrides[0].ReplacementPackage == owner.AnimationSlotOverrides[0].ReplacementPackage && !ReferenceEquals(suit.AnimationSlotOverrides[0], owner.AnimationSlotOverrides[0]),
            "character copies preserve animation-library package identities with independent recipe records");
        yield return (CharacterRebaseService.Preview(owner, suit).All(r => !r.HasBaseline && !r.TakeCharacterByDefault), "older character suits require an explicit inheritance review");
        CharacterRebaseService.CaptureInitial(owner, suit);
        yield return (CharacterRebaseService.GameplayUpdateWarning(owner, suit) is null,
            "fresh inherited gameplay does not produce an update warning");
        yield return (CharacterRebaseService.Preview(owner, suit).All(r => r.HasBaseline && !r.SuitEdited && !r.CharacterEdited), "new character suits track their independent inherited recipe baseline");
        suit.MaterialAssignments[0].MiPackagePath = "/Game/Example/SuitOnly";
        owner.MaterialAssignments[0].MiPackagePath = "/Game/Example/Updated";
        yield return (CharacterRebaseService.GameplayUpdateWarning(owner, suit) is null,
            "parent appearance edits alone do not report stale inherited gameplay");
        owner.AbilityLoadout = new() { HeldItems = [new() { MaterialPackage = "/Game/Example/Held" }] };
        owner.UseCustomArchetype = true; owner.IconMenu = "/Game/Example/Icon";
        var beforeOwner = JsonSerializer.Serialize(owner); var beforeSuit = JsonSerializer.Serialize(suit);
        var rows = CharacterRebaseService.Preview(owner, suit);
        yield return (CharacterRebaseService.GameplayUpdateWarning(owner, suit)?.Contains("Abilities, fighting style and held items") == true,
            "parent gameplay changes produce an actionable rebase warning");
        yield return (rows.Single(r => r.Section.Id == "appearance") is { SuitEdited: true, CharacterEdited: true } &&
            rows.Single(r => r.Section.Id == "abilities") is { SuitEdited: false, CharacterEdited: true }, "rebase distinguishes local suit edits from changed character settings");
        var all = CharacterRebaseService.Sections().Select(s => s.Id).ToHashSet();
        var selected = all.Where(id => id != "appearance").ToHashSet();
        var merged = CharacterRebaseService.Compose(owner, suit, owner, selected);
        yield return (CharacterRebaseService.GameplayUpdateWarning(owner, merged) is null,
            "reviewed gameplay no longer warns while suit appearance remains independent");
        var keptGameplay = CharacterRebaseService.Compose(owner, suit, owner, new HashSet<string>());
        yield return (CharacterRebaseService.GameplayUpdateWarning(owner, keptGameplay) is null,
            "explicitly keeping suit gameplay acknowledges the current parent revision");
        var freshOwner = CustomCharacterProjectService.CreateRecipe(null, "Current", "Current", "Current");
        freshOwner.AbilityLoadout = new() { HeldItemToggle = new() { Version = 4, Enabled = true, PreserveNativeTakedown = true } };
        var freshSuit = CustomCharacterProjectService.CreateRecipe(freshOwner, "New suit", "Current", "New", freshOwner.SlotId);
        yield return (freshSuit.AbilityLoadout?.HeldItemToggle is { Version: 4, PreserveNativeTakedown: true } &&
            !ReferenceEquals(freshOwner.AbilityLoadout.HeldItemToggle, freshSuit.AbilityLoadout.HeldItemToggle),
            "new suits copy the current native-takedown-preserving input handler without sharing mutable records");
        yield return (JsonSerializer.Serialize(owner)==beforeOwner, "composing an inherited suit never changes its character definition");
        yield return (merged.MaterialAssignments[0].MiPackagePath == "/Game/Example/SuitOnly" && merged.AbilityLoadout?.HeldItems?.Count == 1 && merged.UseCustomArchetype && merged.IconMenu == owner.IconMenu,
            "rebase keeps selected suit-specific sections while inheriting abilities, items, animations and icons");
        yield return (merged.DisplayName == suit.DisplayName && merged.SlotId == suit.SlotId && merged.PawnTag == suit.PawnTag && merged.ProgressTag == suit.ProgressTag && merged.TargetPackages.Playable == suit.TargetPackages.Playable && !merged.CustomCharacter!.IsDefinition,
            "character rebase preserves suit identity, roster tags and output packages");
        yield return (CharacterRebaseService.Preview(owner, merged).Single(r => r.Section.Id == "appearance").SuitEdited,
            "kept suit sections remain explicit overrides on subsequent rebases");
        var reset = CharacterRebaseService.Compose(owner, merged, owner, all);
        yield return (reset.MaterialAssignments[0].MiPackagePath == owner.MaterialAssignments[0].MiPackagePath && reset.CustomCharacter!.Inheritance!.KeptSections.Count == 0,
            "inherit everything resets all section overrides to the current character recipe");
        owner.AbilityLoadout = null; owner.MaterialAssignments.Clear(); owner.UseCustomArchetype = false;
        var cleared = CharacterRebaseService.Compose(owner, reset, owner, all);
        yield return (cleared.AbilityLoadout is null && cleared.MaterialAssignments.Count == 0 && !cleared.UseCustomArchetype,
            "rebase propagates character removals, cleared profiles and disabled flags");
        yield return (JsonSerializer.Serialize(suit) == beforeSuit && beforeOwner != JsonSerializer.Serialize(owner), "rebase never edits the source suit while constructing a candidate");
        var restored = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(merged))!;
        yield return (restored.CustomCharacter!.Inheritance!.KeptSections.SequenceEqual(merged.CustomCharacter!.Inheritance!.KeptSections), "inheritance provenance survives saved-project round trips");
        var wrong = CustomCharacterProjectService.CreateRecipe(null, "Other", "Other", "Other");
        yield return (Reject(() => CharacterRebaseService.Preview(wrong, suit)) && Reject(() => CharacterRebaseService.Compose(owner, suit, owner, new HashSet<string> { "invalid" })),
            "rebase rejects foreign parents and unrecognized sections before writing");
        var folder = Directory.CreateTempSubdirectory("BatcomputerRebaseChecks-").FullName;
        var service = new SuitProjectService(folder);
        owner.CustomStaticMeshes = [new() { Id = "test", SourceObjRelativePath = "Models/test.obj" }];
        var sourceFile = Path.Combine(service.ProjectOutputDirectory(owner), "Models/test.obj");
        var oldFile = Path.Combine(service.ProjectOutputDirectory(suit), "Models/test.obj");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!); Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        File.WriteAllText(sourceFile, "parent source"); File.WriteAllText(oldFile, "suit edits");
        var copy = CustomCharacterProjectService.CreateRecipe(owner, suit.DisplayName, "Example", "Alternate", owner.SlotId);
        CustomCharacterProjectService.CopyAuthoringSources(owner, copy, service, "CharacterRebases/test", "/Game/Mods/Example/Test");
        yield return (File.ReadAllText(oldFile) == "suit edits" && File.ReadAllText(sourceFile) == "parent source" &&
            copy.CustomStaticMeshes[0].SourceObjRelativePath == "CharacterRebases/test/Models/test.obj" &&
            File.ReadAllText(Path.Combine(service.ProjectOutputDirectory(copy), copy.CustomStaticMeshes[0].SourceObjRelativePath)) == "parent source",
            "rebase copies editable sources to a fresh revision without overwriting parent or suit files");
        var prepared = CharacterRebaseService.Prepare(owner, suit, all, service, _ => { });
        var repeated = CharacterRebaseService.Prepare(owner, prepared, all, service, _ => { });
        yield return (prepared.CustomStaticMeshes[0].SourceObjRelativePath != repeated.CustomStaticMeshes[0].SourceObjRelativePath &&
            File.Exists(Path.Combine(service.ProjectOutputDirectory(suit), prepared.CustomStaticMeshes[0].SourceObjRelativePath)) &&
            File.Exists(Path.Combine(service.ProjectOutputDirectory(suit), repeated.CustomStaticMeshes[0].SourceObjRelativePath)) &&
            prepared.AnimationSlotOverrides[0].ReplacementPackage == owner.AnimationSlotOverrides[0].ReplacementPackage &&
            File.ReadAllText(oldFile) == "suit edits" && prepared.TargetPackages.Playable == suit.TargetPackages.Playable,
            "full and repeated rebase preparation preserve previous sources, animation identities and target outputs");
        owner.CustomStaticMeshes[0].SourceObjRelativePath = "missing.obj";
        var animationOnly = CharacterRebaseService.Prepare(owner, suit, new HashSet<string> { "animations" }, service, _ => { });
        yield return (animationOnly.AnimationSlotOverrides[0].ReplacementPackage == owner.AnimationSlotOverrides[0].ReplacementPackage,
            "animation-only rebases do not recook visual assets or require unrelated missing model sources");
        owner.GameplayAnimationGraphs = [new() { Id = new string('a',32), Name = "Example movement", OwnerDprdPackage = "/Game/Mods/CC_Example_Example/Characters/DA_DPRD_CC_Example_Example",
            Abilities = [new() { OriginalPackage = "/Game/Characters/GA_Jump", ReplacementPackage = "/Game/Mods/ExampleMovement/GA_CustomMovement" }] }];
        var refreshed = CharacterRebaseService.Prepare(owner, suit, new HashSet<string> { "animations" }, service, _ => { });
        yield return (refreshed.GameplayAnimationGraphs[0].Id == owner.GameplayAnimationGraphs[0].Id &&
            refreshed.GameplayAnimationGraphs[0].Abilities[0].ReplacementPackage == owner.GameplayAnimationGraphs[0].Abilities[0].ReplacementPackage &&
            refreshed.GameplayAnimationGraphs[0].OwnerDprdPackage == "/Game/Mods/CC_Example_Alternate/Characters/DA_DPRD_CC_Example_Alternate",
            "animation-only rebase shares immutable cached assets while wiring the receiving suit's own DPRD");
    }
    private static bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
}
