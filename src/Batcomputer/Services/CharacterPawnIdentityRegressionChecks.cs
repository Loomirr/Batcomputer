namespace Batcomputer;

internal static class CharacterPawnIdentityRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var checks = new List<(bool, string)>();
        var prior = AppSettings.Current;
        var root = Path.Combine(Path.GetTempPath(), "BatcomputerIdentity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = Path.Combine(root, "Content");
            var groups = Path.Combine(content, "Characters", "MetaData", "Groups");
            Directory.CreateDirectory(groups);
            File.WriteAllText(Path.Combine(groups, "DA_Character_Group_PoisonIvy.uasset"), "filename fixture");
            AppSettings.Current = new() { ProjectRoot = root, ExtractedContentRoot = content };
            checks.Add((CustomCharacterProjectService.IsNativeOwner("PoisonIvy"), "native identity validation includes alternate Character_Group naming"));
            var service = new SuitProjectService(root);
            var owner = CustomCharacterProjectService.CreateRecipe(null, "Poison Ivy", "PoisonIvy", "PoisonIvy");
            var child = CustomCharacterProjectService.CreateRecipe(owner, "Winter Ivy", "PoisonIvy", "Winter", owner.SlotId);
            service.SaveProject(owner); service.SaveProject(child);
            var original = File.ReadAllText(service.ProjectPathForSlot(owner.SlotId));
            var updated = CharacterPawnIdentityService.SaveFamily(service, owner, "MyPlayableIvy", out var backup);
            var updatedChild = service.LoadProject(service.ProjectPathForSlot(child.SlotId))!;
            checks.Add((updated.SlotId == owner.SlotId && updated.CustomCharacter!.CharacterId == "PoisonIvy" && updated.DisplayName == "Poison Ivy" &&
                updated.TargetPackages.Dcmd == owner.TargetPackages.Dcmd && updated.ProgressTag == owner.ProgressTag &&
                updated.PawnTag == "Pawns.Playable.MyPlayableIvy.PoisonIvy" && updatedChild.PawnTag == "Pawns.Playable.MyPlayableIvy.Winter",
                "pawn-family migration updates dependent suits while preserving project, asset, progress and display identities"));
            checks.Add((File.ReadAllText(Path.Combine(backup, Path.GetFileName(service.ProjectPathForSlot(owner.SlotId)))) == original,
                "pawn-family migration preserves the original recipe in its recovery backup"));
            checks.Add((CustomCharacterRegistrationService.RosterConfig([updated, updatedChild]).Contains("Pawns.Playable.MyPlayableIvy") &&
                !CustomCharacterRegistrationService.RosterConfig([updated, updatedChild]).Contains("Pawns.Playable.PoisonIvy"), "rebuilt roster uses the new runtime family"));
            var extra = CustomCharacterProjectService.CreateRecipe(updated, "New suit", "PoisonIvy", "New", owner.SlotId);
            checks.Add((extra.PawnTag == "Pawns.Playable.MyPlayableIvy.New", "future child suits inherit the edited runtime family"));
            var saved = File.ReadAllText(service.ProjectPathForSlot(owner.SlotId));
            var rejected = false;
            try { CharacterPawnIdentityService.SaveFamily(service, updated, "PoisonIvy", out _); } catch (InvalidDataException) { rejected = true; }
            checks.Add((rejected && File.ReadAllText(service.ProjectPathForSlot(owner.SlotId)) == saved, "native pawn-family collision is rejected without mutating saved recipes"));
            var other = CustomCharacterProjectService.CreateRecipe(null, "Other", "AnotherTestFamily", "AnotherTestFamily");
            service.SaveProject(other);
            rejected = false;
            try { CharacterPawnIdentityService.SaveFamily(service, updated, "AnotherTestFamily", out _); } catch (InvalidDataException) { rejected = true; }
            checks.Add((rejected && File.ReadAllText(service.ProjectPathForSlot(owner.SlotId)) == saved, "pawn migration rejects another saved family without partially updating the definition"));
            var settings = new AppSettings { ProjectRoot = root };
            checks.Add((SettingsPathStatusService.Check("AssetExtractRoot", "", settings).Pending, "default not-yet-created extraction destination is on demand, not missing input"));
            checks.Add((!SettingsPathStatusService.Check("UnrealEngineRoot", root, settings).Valid, "an arbitrary existing folder is not reported as a usable Unreal installation"));
        }
        finally { AppSettings.Current = prior; Directory.Delete(root, true); }
        return checks;
    }
}
