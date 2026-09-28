using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class CustomCharacterRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool passed, string description) => results.Add((passed, description));
        Check(GameAssetRefreshService.AllCharacterFilters.Contains("Content/GameProgress/PROG_Characters") &&
            !GameAssetRefreshService.AllCharacterFilters.Except(GameAssetRefreshService.DeveloperResearchFilters, StringComparer.OrdinalIgnoreCase).Any(),
            "developer extraction is a superset of full-character extraction, including PROG_Characters");
        var source = new NativeSuitProject { DisplayName = "Moon Knight suit", PawnTag = "Pawns.Playable.Batman.MoonKnight",
            TargetPackages = new() { Playable = "/Game/Mods/MoonKnight/Characters/BP_Batman_MoonKnight_Playable" },
            GeneratedTextures = [new() { PackagePath = "/Game/Mods/MoonKnight/Textures/T_Body" }],
            MaterialAssignments = [new() { Component = "Body", MiPackagePath = "/Game/Mods/MoonKnight/Materials/MI_Body" }] };
        var before = JsonSerializer.Serialize(source);
        var character = CustomCharacterProjectService.CreateRecipe(source, "Moon Knight", "MoonKnight", "MoonKnight");
        var child = CustomCharacterProjectService.CreateRecipe(character, "Unhooded", "MoonKnight", "NoHood", character.SlotId);
        Check(character.CustomCharacter!.DefaultVehicleTag == "" && child.CustomCharacter!.DefaultVehicleTag == "" &&
            CharacterVehicleChoiceService.IsValidTag("None") && CharacterVehicleChoiceService.IsValidTag("Pawns.Vehicle.Batmobile1989") &&
            CharacterVehicleChoiceService.IsValidTag("Pawns.Vehicle.Batcomputer.TestCar"),
            "existing characters retain donor vehicle while no-vehicle and exact native/custom tags are valid");
        character.CustomCharacter.DefaultVehicleTag = "None";
        Check(JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(character))!.CustomCharacter!.DefaultVehicleTag == "None" &&
            CustomCharacterProjectService.IdentityError(character) is null,
            "no-default-vehicle choice survives project serialization and identity validation");
        character.CustomCharacter.DefaultVehicleTag = "Pawns.Vehicle.Bad/Path";
        Check(CustomCharacterProjectService.IdentityError(character) is not null, "invalid vehicle tags cannot enter a character build");
        character.CustomCharacter.DefaultVehicleTag = "";
        Check(CustomCharacterProjectService.IsCharacter(character) && !CustomCharacterProjectService.IsCharacter(child) &&
            !CustomCharacterProjectService.IsCharacter(source), "character definitions, child suits, and legacy suits remain distinct project kinds");
        Check(character.PawnTag == "Pawns.Playable.MoonKnight.MoonKnight" && child.PawnTag == "Pawns.Playable.MoonKnight.NoHood" &&
            child.CustomCharacter!.DefinitionSlotId == character.SlotId, "custom characters and child suits inherit independent owner and variant tags");
        Check(character.ProgressTag == "GameProgress.Definitions.Characters.MoonKnight.MoonKnight" && child.ProgressTag.EndsWith("MoonKnight.NoHood"),
            "character variants own their unlock/save progress tags");
        Check(JsonSerializer.Serialize(source) == before && character.TargetPackages.Dcmd != child.TargetPackages.Dcmd &&
            character.GeneratedTextures[0].PackagePath != child.GeneratedTextures[0].PackagePath &&
            character.MaterialAssignments[0].MiPackagePath.StartsWith("/Game/Mods/CC_MoonKnight_MoonKnight/", StringComparison.Ordinal),
            "copying a character design preserves the source and isolates editable asset package roots");
        Check(CustomCharacterProjectService.IdentityError(character, "Pawns.Playable.Batman.TheBatman2025") is null &&
            CustomCharacterProjectService.IdentityError(child, "Pawns.Playable.Batman.TheBatman2025") is null &&
            CustomCharacterProjectService.IdentityError(new() { PawnTag = child.PawnTag }, "Pawns.Playable.Batman.TheBatman2025") is not null,
            "explicit character mode permits a native gameplay donor without weakening legacy suit owner validation");
        child.PawnTag = "Pawns.Playable.Robin.NoHood";
        Check(CustomCharacterProjectService.IdentityError(child) is not null, "hand-edited mismatched custom owner tags are rejected");
        CustomCharacterProjectService.ApplyIdentity(child);
        Check(new[] { "../Batman", "Batman.Other", "Moon_Knight", "9Moon", "", new string('A', 65) }.All(id => !CustomCharacterProjectService.IsIdentifier(id)),
            "character IDs reject traversal, injected tags, invalid starts and oversized identifiers");
        var config = CustomCharacterRegistrationService.RosterConfig([character, character, child, source]);
        Check(config.Split("+SortedCharacterTags").Length == 2 && config.Contains("Pawns.Playable.MoonKnight") && !config.Contains("Batman") && !config.Contains("NoHood"),
            "roster config is additive and contains one owner entry, never suit variants or donor groups");
        var tagConfig = PawnTagConfigService.Render(new[] { character, child }.SelectMany(project =>
            CustomCharacterRegistrationService.AdditionalTags(project, "CharacterTest").Prepend(new PawnTagConfigService.TagRow(project.PawnTag, "variant"))));
        Check(tagConfig.Split("GameplayTagList=").Length == 6, "two character suits generate five unique loose tags without duplicating their owner tag");
        Check(GameAssetRefreshService.AllCharacterFilters.Contains("Content/GameProgress/PROG_Characters"), "full extraction includes native custom-character progression donor");
        source.AbilityLoadout = new() { HeldItems = [new() { CustomModel = new() { ObjText = "v 0 0 0", Materials =
            [new() { MaterialPath = "/Game/Mods/MoonKnight/Materials/MI_Held" }] } }] };
        source.EquipmentSlots = [new() { Custom = new() { Parts = [new() { Model = new() { ObjText = "v 1 0 0", Materials =
            [new() { MaterialPath = "/Game/Mods/MoonKnight/Materials/MI_Equipment" }] } }] } }];
        var armedCharacter = CustomCharacterProjectService.CreateRecipe(source, "Armed", "Armed", "Armed");
        var visualRoots = CustomCharacterProjectService.VisualCopyRoots(source);
        Check(visualRoots.Contains("/Game/Mods/MoonKnight/Materials/MI_Held") && visualRoots.Contains("/Game/Mods/MoonKnight/Materials/MI_Equipment") &&
            armedCharacter.AbilityLoadout!.HeldItems![0].CustomModel!.ObjText == "v 0 0 0" &&
            armedCharacter.EquipmentSlots[0].Custom!.Parts[0].Model!.Materials[0].MaterialPath.StartsWith("/Game/Mods/CC_Armed_Armed/", StringComparison.Ordinal),
            "character copies retain embedded held-item/equipment geometry and independently copy their material closures");

        var root = Path.Combine(Path.GetTempPath(), "BatcomputerCharacterChecks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var suits = new SuitProjectService(root); var mods = new ModProjectService(root);
            var meshSource = new NativeSuitProject { SlotId = "source_with_fbx", SkinnedMeshes = [new()
            {
                SourceRelativePath = "SkinnedMeshes/revision/source.fbx",
                CacheRelativePath = "SkinnedMeshes/revision"
            }] };
            var meshTarget = new NativeSuitProject { SlotId = "copied_with_fbx" };
            var cache = Path.Combine(suits.ProjectOutputDirectory(meshSource), "SkinnedMeshes", "revision");
            Directory.CreateDirectory(cache);
            var fbx = Path.Combine(cache, "source.fbx");
            File.WriteAllText(fbx, "original FBX");
            File.WriteAllText(Path.Combine(cache, "validated.json"), "original cache");
            CustomCharacterProjectService.CopyAuthoringSources(meshSource, meshTarget, suits);
            var copiedCache = Path.Combine(suits.ProjectOutputDirectory(meshTarget), "SkinnedMeshes", "revision");
            Check(File.ReadAllText(Path.Combine(copiedCache, "source.fbx")) == "original FBX" &&
                File.ReadAllText(Path.Combine(copiedCache, "validated.json")) == "original cache" &&
                Directory.EnumerateFiles(copiedCache).Count() == 2 && File.ReadAllText(fbx) == "original FBX",
                "character copy deduplicates an FBX nested in its cache without altering the source project");

            meshSource.SkinnedMeshes[0].SourceRelativePath = "SkinnedMeshes/revision/missing.fbx";
            var missingTarget = new NativeSuitProject { SlotId = "missing_fbx_copy" };
            Check(Throws<FileNotFoundException>(() => CustomCharacterProjectService.CopyAuthoringSources(meshSource, missingTarget, suits)) &&
                !Directory.Exists(suits.ProjectOutputDirectory(missingTarget)),
                "character copy rejects a missing declared FBX before writing target files");
            meshSource.SkinnedMeshes[0].SourceRelativePath = "SkinnedMeshes/revision/source.fbx";
            meshSource.SkinnedMeshes[0].CacheRelativePath = "SkinnedMeshes/missing_revision";
            var missingCacheTarget = new NativeSuitProject { SlotId = "missing_cache_copy" };
            Check(Throws<DirectoryNotFoundException>(() => CustomCharacterProjectService.CopyAuthoringSources(meshSource, missingCacheTarget, suits)) &&
                !Directory.Exists(suits.ProjectOutputDirectory(missingCacheTarget)),
                "character copy rejects a missing declared cache before writing target files");

            var collisionSource = new NativeSuitProject { SlotId = "source_with_collision",
                CustomStaticMeshes = [new() { SourceObjRelativePath = "cover.png" }] };
            var collisionFolder = suits.ProjectOutputDirectory(collisionSource);
            Directory.CreateDirectory(collisionFolder);
            File.WriteAllText(Path.Combine(collisionFolder, "cover.png"), "mesh source");
            collisionSource.CoverImagePath = Path.Combine(root, "other-cover.png");
            File.WriteAllText(collisionSource.CoverImagePath, "different cover source");
            var collisionTarget = new NativeSuitProject { SlotId = "collision_copy" };
            Check(Throws<InvalidDataException>(() => CustomCharacterProjectService.CopyAuthoringSources(collisionSource, collisionTarget, suits)) &&
                !Directory.Exists(suits.ProjectOutputDirectory(collisionTarget)),
                "character copy reports different sources targeting the same file before writing either");

            var copyContent = Path.Combine(root, "UnsafeCopyMustNotExist");
            Check(Throws(() => new ToolMaterialLibraryService(root).CopyCharacterMaterialSources([], copyContent,
                new Dictionary<string, (GeneratedTextureEntry, string)> { ["/Game/Mods/Old/T_Cape"] = (new(), Path.Combine(root, "missing")) })) &&
                !Directory.Exists(copyContent), "character material copy rejects uncertified texture substitutes before copying any files");
            suits.SaveProject(character); suits.SaveProject(child);
            Check(suits.ListProjects().Count(summary => summary.IsCharacter) == 1 &&
                suits.LoadProject(suits.ProjectPathForSlot(child.SlotId))?.CustomCharacter?.DefinitionSlotId == character.SlotId,
                "character kind and parent identity persist through save, discovery and reload");
            ModSuitEntry Entry(NativeSuitProject project, bool enabled = true) => new()
            { SuitId = project.SlotId, SuitProjectPath = mods.MakeRelativeSuitProjectPath(suits.ProjectPathForSlot(project.SlotId)), Enabled = enabled };
            var expanded = CustomCharacterProjectService.ExpandMembers([Entry(child)], mods, suits);
            Check(expanded.Count == 2 && expanded.Any(member => member.SuitId == character.SlotId), "building a child automatically includes its character's default project");
            Check(CustomCharacterProjectService.ExpandMembers([Entry(child), Entry(character)], mods, suits).Count == 2,
                "explicit and automatic character dependencies do not duplicate the default suit");
            Check(Throws(() => CustomCharacterProjectService.ExpandMembers([Entry(child), Entry(character, false)], mods, suits)),
                "a disabled required character produces an actionable dependency failure");
            child.CustomCharacter!.DefinitionSlotId = "missing_character"; suits.SaveProject(child);
            Check(Throws(() => CustomCharacterProjectService.ExpandMembers([Entry(child)], mods, suits)),
                "missing character definitions block builds instead of silently creating orphan suits");
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("BatcomputerCharacterChecks-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }

        var donorRoot = Path.Combine(Path.GetTempPath(), "BatcomputerCharacterDonors-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(CustomCharacterRegistrationService.MissingDonorFiles(donorRoot).Count == 4,
                "character preflight checks both complete donor asset/uexp pairs before staging");
            foreach (var package in new[] { CustomCharacterRegistrationService.GroupDonor, CustomCharacterRegistrationService.ProgressDonor })
            {
                var path = Path.Combine(donorRoot, package[6..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path + ".uasset", [1]);
                File.WriteAllBytes(path + ".uexp", package == CustomCharacterRegistrationService.ProgressDonor ? [] : [1]);
            }
            Check(CustomCharacterRegistrationService.MissingDonorFiles(donorRoot).SequenceEqual([CustomCharacterRegistrationService.ProgressDonor + ".uexp"]),
                "an empty progression sidecar cannot pass character extraction coverage");
            File.WriteAllBytes(Path.Combine(donorRoot, "GameProgress/PROG_Characters.uexp"), [1]);
            Check(CustomCharacterRegistrationService.MissingDonorFiles(donorRoot).Count == 0,
                "complete character donors pass the shared extraction/build presence check");
        }
        finally { if (Directory.Exists(donorRoot)) Directory.Delete(donorRoot, true); }

        var manyCharacters = Enumerable.Range(0, 128).Select(index =>
            CustomCharacterProjectService.CreateRecipe(null, "Example " + index, "Example" + index, "Example" + index)).ToArray();
        var roster = CustomCharacterRegistrationService.RosterConfig(manyCharacters);
        Check(roster.Split("+SortedCharacterTags=").Length - 1 == manyCharacters.Length &&
            !roster.Contains("!SortedCharacterTags") && !roster.Contains("-SortedCharacterTags"),
            "character roster generation appends 128 independent entries without clearing native entries (not an in-game capacity claim)");
        Check(CustomCharacterRegistrationService.ProgressPackage("ModA", "Example") != CustomCharacterRegistrationService.ProgressDonor &&
            CustomCharacterRegistrationService.GroupPackage("ModA", "Example") != CustomCharacterRegistrationService.GroupDonor &&
            CustomCharacterRegistrationService.GroupPackage("ModA", "Example") != CustomCharacterRegistrationService.GroupPackage("ModB", "Example"),
            "character group/progression outputs have mod-owned paths, never the native donor paths");
        var activeGroupContent = AppSettings.Current.EffectiveExtractedContentRoot();
        if (VehicleDonorService.PackageComplete(activeGroupContent, CustomCharacterRegistrationService.GroupDonor))
        {
            var nativeVehicles = CharacterVehicleChoiceService.Native(activeGroupContent);
            Check(nativeVehicles.Count >= 5 && nativeVehicles.Select(choice => choice.Tag).Distinct(StringComparer.Ordinal).Count() == nativeVehicles.Count,
                "native vehicle choices include extracted base-game and DLC metadata without duplicate pawn tags");
            var group = VehicleAssetService.Read(activeGroupContent, CustomCharacterRegistrationService.GroupDonor);
            var donorVehicle = NativeAssetTextPatch.GetGameplayTag(group, "DefaultVehicle");
            Check(donorVehicle?.StartsWith("Pawns.Vehicle.", StringComparison.Ordinal) == true &&
                NativeAssetTextPatch.SetGameplayTag(group, "DefaultVehicle", "None") &&
                NativeAssetTextPatch.GetGameplayTag(group, "DefaultVehicle") == "None" &&
                NativeAssetTextPatch.SetGameplayTag(group, "DefaultVehicle", "Pawns.Vehicle.Batmobile1989") &&
                NativeAssetTextPatch.GetGameplayTag(group, "DefaultVehicle") == "Pawns.Vehicle.Batmobile1989",
                "character group can clear or select its exact default vehicle without editing the native donor");
            if (VehicleDonorService.PackageComplete(activeGroupContent, CustomCharacterRegistrationService.ProgressDonor))
            {
                var noVehicle = CustomCharacterProjectService.CreateRecipe(null, "No vehicle test", "NoVehicleTest", "NoVehicleTest");
                noVehicle.CustomCharacter!.DefaultVehicleTag = "None";
                var tempStage = Path.Combine(Path.GetTempPath(), "BatcomputerCharacterVehicle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempStage);
                try
                {
                    CustomCharacterRegistrationService.Generate(tempStage, activeGroupContent, "VehicleChoiceTest", [noVehicle], VehicleLightService.Maps, []);
                    var staged = VehicleAssetService.Read(tempStage, CustomCharacterRegistrationService.GroupPackage("VehicleChoiceTest", noVehicle.CustomCharacter.CharacterId));
                    Check(NativeAssetTextPatch.GetGameplayTag(staged, "DefaultVehicle") == "None",
                        "no-default-vehicle choice survives character group staging and cooked reload");
                    var ownedVehicle = new VehicleProject { Id = "OwnedVehicleTest", OwnerTag = CustomCharacterProjectService.Scope(noVehicle.CustomCharacter) };
                    noVehicle.CustomCharacter.DefaultVehicleTag = VehicleProjectService.PawnTag(ownedVehicle);
                    var withVehicle = Path.Combine(tempStage, "WithVehicle");
                    CustomCharacterRegistrationService.Generate(withVehicle, activeGroupContent, "VehicleChoiceTest", [noVehicle], VehicleLightService.Maps, [ownedVehicle]);
                    var selected = VehicleAssetService.Read(withVehicle, CustomCharacterRegistrationService.GroupPackage("VehicleChoiceTest", noVehicle.CustomCharacter.CharacterId));
                    Check(NativeAssetTextPatch.GetGameplayTag(selected, "DefaultVehicle") == VehicleProjectService.PawnTag(ownedVehicle),
                        "owned custom vehicle choice survives character group staging and cooked reload");
                }
                finally { Directory.Delete(tempStage, true); }
            }
        }

        // Exercise the exact opaque FInstancedStruct codec with a tiny native-layout fixture.
        var asset = new UAsset();
        asset.ClearNameIndexList();
        asset.Imports = [new Import { ObjectName = FName.FromString(asset, "DinnerCharacterProgressDefinition") }];
        var nameIndex = asset.AddNameReference(new FString("GameProgress.Definitions.Characters.Batman.TheBatman2025"));
        var record = new byte[24];
        BitConverter.GetBytes((ushort)0x0480).CopyTo(record, 0); BitConverter.GetBytes((ushort)0x0701).CopyTo(record, 2);
        record[4] = 1; record[9] = 1; BitConverter.GetBytes((ushort)0x0300).CopyTo(record, 10); BitConverter.GetBytes(nameIndex).CopyTo(record, 12);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x0301); writer.Write(1); writer.Write(-1); writer.Write(24); writer.Write(record); writer.Write(0);
        var native = bytes.ToArray();
        var output = CustomCharacterRegistrationService.CreateUnlockedProgress(asset, native, [character.ProgressTag, "GameProgress.Definitions.Characters.MoonKnight.NoHood"]);
        Check(output.Length == 74 && BitConverter.ToInt32(output, 2) == 2 && output[14] == 0 && output[18] == 1 && output[23] == 1 &&
            BitConverter.ToInt32(output, output.Length - 4) == 0, "progress codec writes unlocked/saveable variants with exact native framing");
        Check(Throws(() => CustomCharacterRegistrationService.CreateUnlockedProgress(asset, native[..^1], [character.ProgressTag])) &&
            Throws(() => CustomCharacterRegistrationService.CreateUnlockedProgress(asset, native, [character.ProgressTag, character.ProgressTag])),
            "progress codec rejects truncated inputs and duplicate variant tags");
        native[0] = 0;
        Check(Throws(() => CustomCharacterRegistrationService.CreateUnlockedProgress(asset, native, [character.ProgressTag])),
            "unsupported native progress schemas fail closed before packaging");
        return results;
    }
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } catch (Exception) { return false; } }
}
