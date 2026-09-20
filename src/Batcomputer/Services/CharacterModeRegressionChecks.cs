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
        Check(CharacterModeAccessService.RequiresHelper([owner, child]), "Both requires the helper");
        owner.CustomCharacter.ModeAvailability = CharacterModeAvailability.Normal;
        Check(!CharacterModeAccessService.RequiresHelper([owner, child]), "legacy normal-only mods do not require a new runtime");
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
            var incomplete = Path.Combine(scratch, "IncompleteBundle");
            Directory.CreateDirectory(incomplete);
            foreach (var name in new[] { "main.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "README.md" })
                File.WriteAllText(Path.Combine(incomplete, name), "fixture");
            CharacterModeAccessService.ValidateBundledHelper(incomplete);
            File.WriteAllText(Path.Combine(incomplete, "README.md"), "");
            bool emptyGuideRejected = false;
            try { CharacterModeAccessService.ValidateBundledHelper(incomplete); } catch (FileNotFoundException) { emptyGuideRejected = true; }
            Check(emptyGuideRejected, "preflight rejects an empty bundled guide before packaging");
            File.WriteAllText(Path.Combine(incomplete, "README.md"), "fixture");
            File.Delete(Path.Combine(incomplete, "THIRD-PARTY-NOTICES.txt"));
            bool noticesRejected = false;
            try { CharacterModeAccessService.ValidateBundledHelper(incomplete); } catch (FileNotFoundException) { noticesRejected = true; }
            Check(noticesRejected, "preflight rejects missing redistribution notices even when the DLL exists");
            var plugin = Path.Combine(scratch, "RegistryPlugins", "Test");
            if (File.Exists(CharacterModeAccessService.BundledHelper))
            {
                CharacterModeAccessService.Stage(scratch, plugin, [owner, child]);
                var dependencies = CharacterModeAccessService.DependencyFiles(scratch);
                Check(dependencies.Count == 5 && dependencies.All(d => File.Exists(d.Source)) &&
                    dependencies.All(d => d.Relative.StartsWith("Binaries/Win64/ue4ss/Mods/LOTDKModeAccess/")), "release includes the helper and enable marker");
                Check(File.ReadAllText(Path.Combine(plugin,"Config",CharacterModeAccessService.PolicyName)).Contains("ModeTest=both"), "release carries the character policy");
                File.AppendAllText(dependencies[0].Source, "tampered");
                bool tamperRejected = false;
                try { CharacterModeAccessService.DependencyFiles(scratch); } catch (InvalidDataException) { tamperRejected = true; }
                Check(tamperRejected, "changed helper is rejected before install/export");
                File.Delete(Path.Combine(scratch, CharacterModeAccessService.MarkerName));
                bool receiptRejected = false;
                try { CharacterModeAccessService.DependencyFiles(scratch); } catch (InvalidDataException) { receiptRejected = true; }
                Check(receiptRejected, "missing dependency receipt is rejected");
            }
            else Check(false, "bundled helper exists for integration checks");
            var missing = Path.Combine(scratch, "MissingHelper", "Config");Directory.CreateDirectory(missing);
            File.WriteAllText(Path.Combine(missing, CharacterModeAccessService.PolicyName), config);
            bool missingRejected = false;
            try { CharacterModeAccessService.DependencyFiles(Path.GetDirectoryName(missing)!); } catch (InvalidDataException) { missingRejected = true; }
            Check(missingRejected, "Mayhem/Both policy cannot be exported without its helper");
        }
        finally { Directory.Delete(scratch, recursive: true); }
        return result;
    }
}
