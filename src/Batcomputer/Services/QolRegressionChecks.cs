using System.Reflection;
using System.Text.Json;

namespace Batcomputer;

internal static class QolRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
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
