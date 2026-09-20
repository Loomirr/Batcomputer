using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class DlcUpdateRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string detail) => results.Add((ok, detail));
        static bool Rejects(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var asset = new UAsset { Imports = [], Exports = [] };
        asset.ClearNameIndexList();
        Check(!VehicleCustomizationService.Seats(asset).Any(), "a non-rideable asset genuinely has no seats");
        asset.Exports.Add(new RawExport([], asset, []) { ObjectName = new FName(asset, "RideableComponent") });
        Check(Rejects(() => VehicleCustomizationService.Seats(asset).ToArray()), "unparsed vehicle seats reject outdated mappings rather than disappearing");
        asset.Exports.Clear();
        var rideable = new NormalExport(asset, []) { ObjectName = new FName(asset, "RideableComponent"), Data = [] };
        asset.Exports.Add(rideable);
        Check(Rejects(() => VehicleCustomizationService.Seats(asset).ToArray()), "missing vehicle seat fields produce an actionable update error");
        rideable.Data.Add(new ArrayPropertyData(new FName(asset, "Seats")) { Value = [] });
        Check(!VehicleCustomizationService.Seats(asset).Any(), "an explicitly empty seat array remains valid");
        Check(HeldItemService.ValidPackage("/Game/Models/Vehicles/BP_VEH-Batbike2022_theBatman") &&
            new[] { "/Game/../Test", "/Game//Test", "/Game/Test.Test", "/Game/A\\B", "/Script/Engine/Test" }.All(p => !HeldItemService.ValidPackage(p)),
            "native hyphenated packages are allowed without allowing traversal or object paths");
        Check(ComponentRemoveService.PreserveConstructionNode("Face") && !ComponentRemoveService.PreserveConstructionNode("Face_2") &&
            !ComponentRemoveService.PreserveConstructionNode("Head"), "only the original facial-animation component is preserved on removal");
        var nativePoint = new System.Numerics.Vector3(23, 48, 71);
        var gltfPoint = SkinnedGlbExportService.ToGltf(new("Test", -1, nativePoint,
            System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One)).Translation;
        var workshopPoint = System.Numerics.Vector3.Transform(gltfPoint,
            System.Numerics.Matrix4x4.CreateScale(1, 1, -1) * System.Numerics.Matrix4x4.CreateRotationX(MathF.PI / 2));
        Check(System.Numerics.Vector3.Distance(workshopPoint * 100, nativePoint) < .001f,
            "current corrected GLB basis requires the workshop reflection to align asymmetric markers");
        var monster = VehicleDonorService.All.Single(d => d.Id == "batmobilemonstertruck");
        Check(monster.Mesh.Contains("/DLC_Shared/") && monster.Menu.Contains("/DLC_Shared/") &&
            monster.SummonMesh.Contains("/DLC_Shared/") && monster.Metadata.StartsWith("/DLC_PartyPack/"),
            "relocated Batmobeast visual assets retain their Party Pack metadata mount");

        var equipment = new UAsset { Exports = [], Imports = [] }; equipment.ClearNameIndexList();
        var equipmentCdo = new NormalExport(equipment, []) { ObjectName = new FName(equipment, "Default__Equipment_C"),
            Data = [new FloatPropertyData(new FName(equipment, "MaxRadius")) { Value = 1 },
                new FloatPropertyData(new FName(equipment, "Lifetime")) { Value = 2 }] };
        equipment.Exports.Add(equipmentCdo);
        var writtenEquipment = new UAsset { Exports = [], Imports = [] }; writtenEquipment.ClearNameIndexList();
        var writtenCdo = new NormalExport(writtenEquipment, []) { ObjectName = new FName(writtenEquipment, "Default__Equipment_C"),
            Data = [new FloatPropertyData(new FName(writtenEquipment, "Lifetime")) { Value = 2 },
                new FloatPropertyData(new FName(writtenEquipment, "MaxRadius")) { Value = 1 }] };
        writtenEquipment.Exports.Add(writtenCdo);
        Check(!Rejects(() => CustomEquipmentService.RequirePropertyLayoutRoundtrip(equipment, writtenEquipment, "/Game/Test")),
            "equipment layout check accepts serialization reordering with complete fields");
        writtenCdo.Data.RemoveAt(0);
        Check(Rejects(() => CustomEquipmentService.RequirePropertyLayoutRoundtrip(equipment, writtenEquipment, "/Game/Test")),
            "equipment layout check rejects a silently truncated unedited CDO despite matching export counts");
        writtenCdo.Data.Add(new IntPropertyData(new FName(writtenEquipment, "Lifetime")) { Value = 2 });
        Check(Rejects(() => CustomEquipmentService.RequirePropertyLayoutRoundtrip(equipment, writtenEquipment, "/Game/Test")),
            "equipment layout check rejects a changed property type with matching names and counts");
        writtenCdo.Data.RemoveAt(1);
        writtenCdo.Data.Add(new FloatPropertyData(new FName(writtenEquipment, "Lifetime")) { Value = 2 });
        var guidField = new UAssetAPI.PropertyTypes.Structs.GuidPropertyData(new FName(equipment, "ExpressionGUID")) { Value = Guid.Empty };
        equipmentCdo.Data.Add(new UAssetAPI.PropertyTypes.Structs.StructPropertyData(new FName(equipment, "ExpressionGUID")) { Value = [guidField] });
        writtenCdo.Data.Add(new UAssetAPI.PropertyTypes.Structs.StructPropertyData(new FName(writtenEquipment, "ExpressionGUID")) { Value = [] });
        Check(!Rejects(() => CustomEquipmentService.RequirePropertyLayoutRoundtrip(equipment, writtenEquipment, "/Game/Test")),
            "declared equipment field layout permits cooked zero-mask omission of inner struct values");
        writtenCdo.Data.RemoveAt(2);
        Check(Rejects(() => CustomEquipmentService.RequirePropertyLayoutRoundtrip(equipment, writtenEquipment, "/Game/Test")),
            "equipment layout still rejects losing the entire outer struct field");
        const string partyMove = "Global/Conversations/Blueprints/GA_ConversationAbility_PartyMoveTo";
        var dependencyMessage = AnimArchetypeGraftService.MissingGameplayDependencyMessage("/Game/AS_CharacterCoreAbilitySet", "/Game/" + partyMove, "fixture-content");
        Check(dependencyMessage.Contains(partyMove) && dependencyMessage.Contains("Full character extraction") &&
            dependencyMessage.Contains("rebuild") && dependencyMessage.Contains("fixture-content"),
            "unresolved ability errors retain the exact asset and give extraction and rebuild recovery steps");

        var saved = AppSettings.Current;
        var fixture = Path.Combine(Path.GetTempPath(), "Batcomputer-dlc-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = Path.Combine(fixture, "Game", "Content");
            string FileFor(string relative, string text = "fixture")
            {
                var path = Path.Combine(content, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text); return path;
            }
            var helmet = FileFor("Characters/Attachments/HAT/Talia/SK_HAT_Talia.uasset");
            Check(GameAssetRefreshService.MissingCharacterDependencyFiles(content).Contains(partyMove + ".uasset"),
                "full refresh requires the core PartyMoveTo conversation ability");
            FileFor(partyMove + ".uasset"); FileFor(partyMove + ".uexp", "");
            Check(GameAssetRefreshService.MissingCharacterDependencyFiles(content).Contains(partyMove + ".uexp") &&
                !GameAssetRefreshService.MissingCharacterDependencyFiles(content).Contains(partyMove + ".uasset"),
                "ability extraction sentinels reject empty cooked sidecars, not just missing headers");
            FileFor(partyMove + ".uexp");
            Check(!GameAssetRefreshService.MissingCharacterDependencyFiles(content).Any(p => p.StartsWith(partyMove)),
                "complete conversation ability pairs satisfy the refresh sentinel");
            var stagedChild = FileFor("Mods/Test/BP_Child.uasset");
            var stagedParent = FileFor("Mods/Test/BP_Parent.uasset"); FileFor("Mods/Test/BP_Parent.uexp");
            Check(NativeBlueprintSchemaService.ResolveParentFile(stagedChild, "/Game/Mods/Test/BP_Parent", Path.Combine(fixture, "MissingNative")) == Path.GetFullPath(stagedParent),
                "custom parent schemas resolve from the current stage without requiring a native extraction copy");
            Check(NativeBlueprintSchemaService.ResolveParentFile(Path.Combine(fixture, "Other", "Content", "Mods", "Test", "BP_Child.uasset"),
                "/Game/Mods/Test/BP_Parent", content) is null,
                "missing staged parents never fall back to another stage or the extracted tree");
            Check(NativeBlueprintSchemaService.ResolveParentFile(stagedChild, "/Game/Characters/Attachments/HAT/Talia/SK_HAT_Talia", content) == Path.GetFullPath(helmet),
                "native parent lookups continue using the active extraction");
            var removedFace = new ComponentRemoveService(fixture).RemoveFromContentRoot(content, "test", "/Game/Missing/Playable", "", "Face", true, false);
            Check(removedFace.Files.Single().Error?.Contains("Staged asset not found") == true,
                "face removal uses the safe visual-hide path and reports a missing staged asset");
            FileFor("AdditionalContent/VillainMode/Characters/Attachments/HAT/Hood/SM_HAT_Hood.uasset");
            var joker = FileFor("AdditionalContent/VillainMode/Characters/Playables/Joker/BP_Joker_Default_Playable.uasset");
            var meshPaths = ExtractedAttachmentMeshCatalogService.Discover(content).Select(a => a.Path).ToArray();
            Check(meshPaths.Length == 2 && meshPaths.Any(p => p.EndsWith("SK_HAT_Talia")) && meshPaths.Any(p => p.EndsWith("SM_HAT_Hood")),
                "attachment meshes overlay both base and Villain Mode character roots");
            Check(BaseCharacterPicker.EnumerateExtractedVisualPackages(content, true).Any(p => p.EndsWith("BP_Joker_Default_Playable")),
                "the playable picker discovers the Villain Mode Playables layout");
            var map = FileFor("current.usmap");
            AppSettings.Current = new AppSettings { ExtractedContentRoot = content, UsmapPath = map };
            var indexService = new PartIndexService(fixture);
            Directory.CreateDirectory(Path.GetDirectoryName(indexService.PartIndexPath)!);
            var cachedIndex = new NativeSuitPartIndex { SourceContentRoot = content, MappingsPath = map,
                MappingsRevision = PartIndexService.CaptureMappingsRevision(map) };
            File.WriteAllText(indexService.PartIndexPath, System.Text.Json.JsonSerializer.Serialize(cachedIndex));
            Check(indexService.LoadPartIndex() is not null, "unchanged parts index and mappings are reusable");
            var oldMapTime = File.GetLastWriteTimeUtc(map);
            File.SetLastWriteTimeUtc(map, oldMapTime.AddSeconds(1));
            Check(indexService.LoadPartIndex() is null, "same-path mappings replacement invalidates cached part recipes");
            File.SetLastWriteTimeUtc(map, oldMapTime);
            var alternateMap = FileFor("alternate.usmap");
            File.SetLastWriteTimeUtc(alternateMap, oldMapTime);
            AppSettings.Current.UsmapPath = alternateMap;
            Check(indexService.LoadPartIndex() is null, "switching mappings rejects the previous index even with identical file size and timestamp");
            AppSettings.Current.UsmapPath = map;
            cachedIndex.MappingsRevision = null;
            File.WriteAllText(indexService.PartIndexPath, System.Text.Json.JsonSerializer.Serialize(cachedIndex));
            Check(indexService.LoadPartIndex() is null, "parts caches without mappings provenance must rebuild");
            var project = new NativeSuitProject { PlayableTemplate = new TemplateRecord
                { PackagePath = "/Game/AdditionalContent/VillainMode/Characters/Playables/Joker/BP_Joker_Default_Playable", Uasset = joker } };
            var original = UAssetPatchService.BuildStageIdentityForTest(project);
            File.AppendAllText(map, "changed mappings");
            var newMap = UAssetPatchService.BuildStageIdentityForTest(project);
            FileFor("AdditionalContent/VillainMode/Characters/Playables/Joker/BP_Joker_Default_Playable.uexp", "new game build");
            Check(original != newMap && newMap != UAssetPatchService.BuildStageIdentityForTest(project),
                "stage identity changes when mappings or donor sidecars change");
            var missing = NativeMetadataDonorService.TryRead(new TemplateRecord { PackagePath = "/Game/Missing/DA_DCMD_Test", Uasset = helmet }, null, null, out var error);
            Check(missing is null && error.Contains("Missing donor"), "missing active metadata never falls back to an old saved donor");
        }
        finally
        {
            AppSettings.Current = saved;
            ExtractedAttachmentMeshCatalogService.Invalidate();
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
        return results;
    }
}
