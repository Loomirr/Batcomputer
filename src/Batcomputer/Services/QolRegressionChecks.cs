using System.Reflection;
using System.Text.Json;

namespace Batcomputer;

internal static class QolRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var characterEntry = new CharacterCatalogService.Entry("Moon Knight", CharacterCatalogService.Source.CustomSuit,
            "", "character.json", true, "moonknight", "MoonKnight");
        var suitEntry = characterEntry with { IsCharacter = false, ProjectId = "moonknight_white" };
        results.Add((CharacterBrowserList.EntryLabel(characterEntry) == "CHARACTER · MoonKnight" &&
            CharacterBrowserList.EntryLabel(suitEntry) == "SUIT · moonknight_white",
            "viewer library distinguishes character definitions from suit variants with stable IDs"));
        var noise = new TextureDecodeService.Decoded([
            new(0, 255, 255, 255), new(255, 0, 255, 255),
            new(255, 0, 255, 255), new(0, 255, 255, 255)], 2, 2);
        var filteredNoise = TextureDecodeService.PrefilterNormalNoise(noise, 2, 2, 2);
        results.Add((filteredNoise.Width == 1 && filteredNoise.Height == 1 &&
            filteredNoise.Pixels[0].r == 127 && filteredNoise.Pixels[0].g == 127 &&
            noise.Width == 2 && noise.Pixels[0].r == 0,
            "micro-normal minification averages linear channels without changing the source texture"));
        results.Add((ReferenceEquals(noise, TextureDecodeService.PrefilterNormalNoise(noise, 64, 64, 1)),
            "micro-normal prefilter preserves full detail when the target has sufficient resolution"));
        var ui = new Thread(() =>
        {
            using var editor = new TextBox(); using var display = new TextBox();
            MainForm.ConnectDisplayNameEditor(editor, display);
            editor.Text = "  Renamed suit  ";
            results.Add((display.Text == "Renamed suit", "display-name editor changes the label without deriving a new project identity"));
        });
        ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
        var grey = new NativeSuitPartRecord { Context = "playable", Slot = "Cape", ComponentClass = "SkeletalMeshComponent",
            MeshKind = "SkeletalMesh", MeshPackagePath = "/Game/Models/SK_TwoHole", MeshObjectName = "SK_TwoHole", MeshObjectPath = "/Game/Models/SK_TwoHole.SK_TwoHole",
            SourcePackagePath = "/Game/Characters/BP_Grey_Playable", Materials = [new() { PackagePath = "/Game/MI_Grey", ObjectPath = "/Game/MI_Grey.MI_Grey" }] };
        var black = PartRecipeService.Clone(grey); black.Context = "cutscene"; black.SourcePackagePath = "/Game/Characters/BP_Batman_Cutscene";
        black.MeshPackagePath = "/Game/Models/SK_Spiked"; black.MeshObjectName = "SK_Spiked";
        results.Add((PartCounterpartService.Find(grey, [black], "cutscene") is null,
            "missing cape counterpart never substitutes an unrelated spiked cape"));
        grey.ComponentClass = "SkeletalMeshComponentBudgeted";
        var sameMesh = PartRecipeService.Clone(grey); sameMesh.Context = "cutscene"; sameMesh.ComponentClass = "SkeletalMeshComponent"; sameMesh.Materials[0].PackagePath = "/Game/MI_Black";
        sameMesh.Materials[0].ObjectPath = "/Game/MI_Black.MI_Black";
        var selected = PartCounterpartService.Find(grey, [black, sameMesh], "cutscene");
        results.Add((selected?.MeshPackagePath == grey.MeshPackagePath && selected.Materials[0].PackagePath == "/Game/MI_Grey" &&
            sameMesh.Materials[0].PackagePath == "/Game/MI_Black", "role counterpart uses the exact mesh and selected colour without mutating the index"));
        var saved = MainForm.PartToDonorForTest(selected, "cutscene")!;
        var replay = MainForm.ResolvePartForReplayForTest(saved, new NativeSuitPartIndex { Parts = [sameMesh] });
        results.Add((replay?.Materials[0].PackagePath == "/Game/MI_Grey", "declarative replay preserves saved counterpart materials instead of restoring its shell donor's colour"));
        var nativeCape = PartRecipeService.Clone(grey);
        nativeCape.MeshPackagePath = "/Game/Characters/Attachments/Cape/TwoHole/SK_CAPE_TwoHole";
        nativeCape.MeshObjectName = "SK_CAPE_TwoHole";
        var advancedCape = PartRecipeService.Clone(nativeCape); advancedCape.Context = "cutscene";
        advancedCape.MeshPackagePath += "_Advanced"; advancedCape.MeshObjectName += "_Advanced";
        results.Add((PartCounterpartService.Find(nativeCape, [advancedCape], "cutscene")?.MeshObjectName == "SK_CAPE_TwoHole_Advanced",
            "native cape pairing retains its matching Advanced cutscene rig"));
        var hair = new NativeSuitPartRecord { Context = "playable", Slot = "Head", SemanticKind = "Hair", ComponentClass = "StaticMeshComponent", TemplateComponentClass = "StaticMeshComponent",
            MeshKind = "StaticMesh", MeshPackagePath = "/Game/Characters/Attachments/Hair/PonyTail/SM_HAIR_PonyTail", MeshObjectName = "SM_HAIR_PonyTail",
            MeshObjectPath = "/Game/Characters/Attachments/Hair/PonyTail/SM_HAIR_PonyTail.SM_HAIR_PonyTail", SourcePackagePath = "/Game/Characters/BP_Quest",
            Materials = [new() { PackagePath = "/Game/MI_Ponytail", ObjectPath = "/Game/MI_Ponytail.MI_Ponytail" }] };
        var counterpart = PartCounterpartService.Find(hair, [hair], "cutscene");
        results.Add((counterpart?.MeshObjectPath == hair.MeshObjectPath && counterpart.Context == "playable" && !ReferenceEquals(counterpart, hair),
            "playable-only static hair uses its exact source recipe for the cutscene target"));
        var savedHair = MainForm.PartToDonorForTest(counterpart, "cutscene")!;
        var hairReplay = MainForm.ResolvePartForReplayForTest(savedHair, new NativeSuitPartIndex { Parts = [hair] });
        results.Add((savedHair.Context == "playable" && hairReplay?.MeshObjectPath == hair.MeshObjectPath && hairReplay.Materials[0].PackagePath == "/Game/MI_Ponytail",
            "shared hair roundtrip retains source context, mesh and material for the other target role"));
        var skeletalHair = PartRecipeService.Clone(hair); skeletalHair.MeshKind = "SkeletalMesh"; skeletalHair.ComponentClass = "SkeletalMeshComponent";
        results.Add((PartCounterpartService.Find(skeletalHair, [skeletalHair], "cutscene") is null && PartCounterpartService.Find(hair, [hair], "unknown") is null,
            "static head fallback does not manufacture skeletal or unknown-role counterparts"));
        results.Add((MainForm.HasEnabledModContent(new() { Vehicles = [new() { VehicleId = "OnlyCar", Enabled = true }] }),
            "vehicle-only mod is buildable without an open suit or suit entries"));
        results.Add((!MainForm.HasEnabledModContent(null) && !MainForm.HasEnabledModContent(new()) && !MainForm.HasEnabledModContent(new() { Vehicles = [new() { Enabled = false }] }),
            "empty or disabled-only mods are not marked buildable"));
        var recipe = new NativeSuitProject(); var before = MainForm.DeclarativeRecipeFingerprint(recipe);
        results.Add((before == MainForm.DeclarativeRecipeFingerprint(JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(recipe))!), "material stage fingerprint survives recipe serialization"));
        recipe.Changes.Add(new());
        results.Add((before == MainForm.DeclarativeRecipeFingerprint(recipe), "journal entries do not invalidate the material-only stage fast path"));
        recipe.MaterialAssignments.Add(new() { Component = "Cape", Slot = 0, MiPackagePath = "/Game/MI_Grey", Context = "both" });
        results.Add((before != MainForm.DeclarativeRecipeFingerprint(recipe), "changed material declaration invalidates reuse of an older stage fingerprint"));
        var skeletal = new EquipmentAssetPart { AssetClass = "SkeletalMesh", OwnerPackage = "/Game/Equipment/BP_Experimental", ExportName = "WeaponMesh", PropertyPath = "SkeletalMesh", Package = "/Game/Models/SK_Experimental" };
        results.Add((skeletal.CanEdit && !EquipmentSkinnedModelService.IsTested(skeletal), "unverified native skeletal components are editable but are not labelled tested"));
        results.Add((!new EquipmentAssetPart { AssetClass = "SkeletalMesh", PropertyPath = "SkeletalMesh", Package = "/Game/Mods/Other/SK_Mesh" }.CanEdit,
            "skeletal replacement cannot use another mod's mesh as a native rig donor"));
        results.Add((EquipmentUpgradeService.Options.Single(o => o.Id == "multi-throw").Label.Contains("Combo") &&
            EquipmentUpgradeService.Options.Single(o => o.Id == "scatterangs").Label.Contains("simultaneous"), "Combo capacity and simultaneous Scatterang throws have distinct labels without changing saved option IDs"));
        var rows = Enumerable.Range(0, 1000).Select(i => new RegistryPluginService.RegistryRow($"/Game/Mods/Large/DA_DCMD_Character{i}")).ToArray();
        var rowText = RegistryPluginService.SerializeAdditionalRows(rows);
        results.Add((rowText.Length > 16383 && rowText.Split(';').Length == 999 && rowText.Contains("DA_DCMD_Character999|"),
            "large registry row payload preserves every row in file format without command-line truncation"));
        var fixture = Path.Combine(Path.GetTempPath(), "Batcomputer-ScopedMaterial-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SuitProjectService(fixture);
            service.SaveProject(new NativeSuitProject { SlotId = "unrelated", DisplayName = "Unrelated",
                GeneratedMaterials = [new() { PackagePath = "/Game/Mods/Unrelated/MI_Missing", DisplayName = "Missing material" }] });
            var library = new ToolMaterialLibraryService(fixture);
            var copied = MainForm.StageReferencedToolMaterialsForRelease(new NativeSuitProject(), library, Path.Combine(fixture, "Stage"));
            results.Add((copied == 0 && !File.Exists(library.CatalogPath),
                "native-only release does not migrate or refresh unrelated saved material recipes"));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
        return results;
    }
}
