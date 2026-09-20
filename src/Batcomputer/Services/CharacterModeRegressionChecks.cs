using System.Text.Json;

namespace Batcomputer;

internal static class CharacterModeRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var result = new List<(bool, string)>();
        void Check(bool ok, string name) => result.Add((ok, "character modes: " + name));
        var legacy = JsonSerializer.Deserialize<CustomCharacterIdentity>("{\"CharacterId\":\"Legacy\"}")!;
        Check(legacy.ModeAvailability == CharacterModeAvailability.Normal, "old projects retain normal mode");
        var owner = CustomCharacterProjectService.CreateRecipe(null, "Test", "ModeTest", "ModeTest");
        owner.CustomCharacter!.ModeAvailability = CharacterModeAvailability.Both;
        var child = CustomCharacterProjectService.CreateRecipe(owner, "Other", "ModeTest", "Other", owner.SlotId);
        Check(child.CustomCharacter!.ModeAvailability == CharacterModeAvailability.Both, "new suits copy the owner setting");
        child.CustomCharacter.ModeAvailability = CharacterModeAvailability.Normal;
        var config = CharacterModeAccessService.Render([owner, child]);
        Check(config.Contains("Pawns.Playable.ModeTest=both") && !config.Contains("Other=") && !config.Contains("=normal"), "build reads the definition, not stale child copies");
        Check(CharacterModeAccessService.RequiresModeRuntime([owner, child]), "Both requires integrated runtime capability");
        owner.CustomCharacter.ModeAvailability = CharacterModeAvailability.Normal;
        Check(!CharacterModeAccessService.RequiresModeRuntime([owner, child]), "legacy normal-only mods do not require a new runtime");
        var originalTag = owner.PawnTag; var originalSlot = owner.SlotId;
        owner.CustomCharacter.ModeAvailability = CharacterModeAvailability.Mayhem;
        CustomCharacterProjectService.ApplyIdentity(owner);
        Check(owner.PawnTag == originalTag && owner.SlotId == originalSlot, "changing modes preserves IDs and pawn tags");
        var roundTrip = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(owner))!;
        Check(roundTrip.CustomCharacter!.ModeAvailability == CharacterModeAvailability.Mayhem, "mode survives save/load");
        owner.CustomCharacter.ModeAvailability = (CharacterModeAvailability)999;
        Check(CustomCharacterProjectService.IdentityError(owner) is not null, "invalid enum blocked before building");
        owner.CustomCharacter.ModeAvailability = CharacterModeAvailability.Both;
        var conflict = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(owner))!;
        conflict.CustomCharacter!.ModeAvailability = CharacterModeAvailability.Normal;
        bool rejected = false; try { _ = CharacterModeAccessService.Render([owner, conflict]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "conflicting definitions cannot overwrite one another");
        Exception? uiError = null;
        var thread = new Thread(() => {
            try {
            using var dialog = new CharacterIdentityDialog("Test", "ID", "Test", "ModeTest", modeAvailability: CharacterModeAvailability.Both);
            Check(dialog.ModeAvailability == CharacterModeAvailability.Both, "identity menu selects saved mode");
            using var childDialog = new CharacterIdentityDialog("Suit", "ID", "Other", "Other", ownerId: "ModeTest", modeAvailability: CharacterModeAvailability.Mayhem);
            var combo = (ComboBox)typeof(CharacterIdentityDialog).GetField("_modes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(childDialog)!;
            Check(!combo.Enabled, "child menu shows inherited availability, not an independent override");
            } catch (Exception ex) { uiError = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Check(uiError is null, "mode dialog creates without an exception");
        var scratch = Path.Combine(Path.GetTempPath(), "BatcomputerModeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var module = Path.Combine(scratch, "LOTDKExpanded");
            Directory.CreateDirectory(Path.Combine(module, "dlls"));
            File.WriteAllText(Path.Combine(module,"dlls","main.dll"),"fixture");
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(module,"dlls","main.dll"))));
            File.WriteAllText(Path.Combine(module,"capabilities.json"),JsonSerializer.Serialize(new CharacterModeAccessService.RuntimeCapability(1,hash)));
            CharacterModeAccessService.ValidateInstalledRuntime(module);
            Check(true,"integrated runtime capability and DLL hash match");
            File.AppendAllText(Path.Combine(module,"dlls","main.dll"),"changed");
            bool rejectedRuntime=false;
            try { CharacterModeAccessService.ValidateInstalledRuntime(module); } catch(InvalidDataException) { rejectedRuntime=true; }
            Check(rejectedRuntime,"mismatched integrated runtime is rejected");
            var plugin=Path.Combine(scratch,"RegistryPlugins","Test");
            CharacterModeAccessService.Stage(scratch,plugin,[owner,child]);
            Check(File.ReadAllText(Path.Combine(plugin,"Config",CharacterModeAccessService.PolicyName)).Contains("ModeTest=both"),"release carries character mode policy");
            Check(CharacterModeAccessService.DependencyFiles(scratch).Count==0,"character release includes no standalone runtime DLL");
            File.WriteAllText(Path.Combine(scratch,"mode-access-dependency.json"),"{}");
            bool oldRejected=false;
            try { CharacterModeAccessService.DependencyFiles(scratch); } catch(InvalidDataException) { oldRejected=true; }
            Check(oldRejected,"old standalone-helper releases require rebuilding");
        }
        finally { Directory.Delete(scratch, recursive: true); }
        return result;
    }
}
