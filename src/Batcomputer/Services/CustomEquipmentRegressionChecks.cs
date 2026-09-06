using System.Text.Json;

namespace Batcomputer;

internal static class CustomEquipmentRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);
    internal static IReadOnlyList<Result> Run()
    {
        var checks = new List<Result>();
        var db = new GameDataDb { Families = [new() { Name = "Batman" }, new() { Name = "Gordon", PlayableCount = 1 }, new() { Name = "Boss" }],
            Assets = [new() { Path = "/Game/Characters/Minifig/Batman/BP_Batman_Default_Playable", Class = "BlueprintGeneratedClass" },
                new() { Path = "/Game/Characters/Minifig/Boss/BP_Boss_Archetype", Class = "BlueprintGeneratedClass" }] };
        var allowed = new GameDataEquipment { Name = "Batarang", EtaPackage = "/Game/Equipment/DA_ETA_Test", EdPackage = "/Game/Equipment/BP_Test_ED", NativeFamilies = ["Batman"] };
        var staticProfile = new EquipmentAssetProfile { EtaPackage = allowed.EtaPackage, DefinitionPackage = allowed.EdPackage };
        staticProfile.Parts.Add(new() { AssetClass = "StaticMesh", ExportName = "WeaponMesh", PropertyPath = "StaticMesh" });
        checks.Add(new(EquipmentWorkshopPolicy.Evaluate(allowed, staticProfile, db).CanCustomize,
            "workshop accepts a static-bodied gadget owned by an actual playable blueprint even in older catalogs without counts"));
        var boss = new GameDataEquipment { Name = "Boss weapon", EtaPackage = allowed.EtaPackage, EdPackage = allowed.EdPackage, NativeFamilies = ["Boss"] };
        checks.Add(new(!EquipmentWorkshopPolicy.Evaluate(boss, staticProfile, db).CanCustomize &&
            !EquipmentWorkshopPolicy.Evaluate(new GameDataEquipment(), staticProfile, db).CanCustomize &&
            !EquipmentWorkshopPolicy.Evaluate(null, staticProfile, db).CanCustomize,
            "boss archetypes, NPC/unowned gear and uncataloged variants stay view-only despite static meshes"));
        var skeletalProfile = new EquipmentAssetProfile { EtaPackage = allowed.EtaPackage, DefinitionPackage = allowed.EdPackage };
        skeletalProfile.Parts.Add(new() { AssetClass = "SkeletalMesh", ExportName = "WeaponMesh" });
        for (int i = 0; i < 5; i++) skeletalProfile.Parts.Add(new() { AssetClass = "StaticMesh", ExportName = "Bullet" + i });
        checks.Add(new(!EquipmentWorkshopPolicy.Evaluate(allowed, skeletalProfile, db).CanCustomize,
            "a skeletal gun remains entirely view-only even when its static projectile references outnumber its body"));
        bool oldRecipeRejected = false;
        try { EquipmentWorkshopPolicy.RequireEditable(allowed, skeletalProfile, db); } catch (InvalidDataException) { oldRecipeRejected = true; }
        checks.Add(new(oldRecipeRejected, "the shared builder guard rejects previously saved recipes for now-view-only equipment"));
        staticProfile.Parts.Add(new() { AssetClass = "SkeletalMesh", ExportName = "Projectile Mesh" });
        checks.Add(new(EquipmentWorkshopPolicy.Evaluate(allowed, staticProfile, db).CanCustomize && !staticProfile.Parts[1].CanEdit,
            "a static main body can be customized while its secondary skeletal projectile stays individually locked"));
        checks.Add(new(!EquipmentWorkshopPolicy.Evaluate(allowed, null, db).CanCustomize &&
            !EquipmentWorkshopPolicy.Evaluate(allowed, new EquipmentAssetProfile { EtaPackage = allowed.EtaPackage, DefinitionPackage = "/Game/Other" }, db).CanCustomize,
            "missing inspection data and mismatched definitions cannot enable customization"));
        var claw = new EquipmentAssetPart { OwnerPackage = "/Game/BP_BatClaw_Proj", AssetClass = "StaticMesh" };
        checks.Add(new(EquipmentWorkshopForm.Group(claw) == "Projectile models" &&
            EquipmentWorkshopForm.PartTitle(staticProfile.Parts[0]) == "Held equipment model",
            "workshop labels prioritize readable roles and recognize abbreviated projectile actor names"));
        checks.Add(new(EquipmentAssetService.ExtractionFilters.All(GameAssetRefreshService.AllCharacterFilters.Contains) &&
            EquipmentAssetService.ExtractionFilters.Contains("Content/UI/Icons/Gadgets/") &&
            EquipmentAssetService.ExtractionFilters.Contains("Content/UI/Icons/GadgetUpgrades/") &&
            EquipmentAssetService.ExtractionFilters.All(p => p != "Content/Models/Props/" && p != "Content/Levels/"),
            "first-time/full extraction includes equipment HUD and narrow model dependencies without extracting all props or levels"));
        var recipe = new CustomEquipmentRecipe { Id = "slot_1", Name = "Banana", DonorEtaPackage = "/Game/Characters/Equipment/Batarang/DA_ETA_Batarang",
            Parts = [new() { OwnerPackage = "/Game/Characters/Equipment/Batarang/BP_Batarang_Weapon", ExportName = "Mesh", PropertyPath = "StaticMesh",
                OriginalPackage = "/Game/Models/Gadgets/GA_Batarang/SM_GA_Batarang", Model = new() { SourceName = "banana.obj", ObjText = "v 1 2 3", Scale = .4f,
                    Materials = [new() { Slot = 0, SourceMaterialName = "Yellow", MaterialPath = "/Game/Characters/Materials/MI_Yellow" }] } }] };
        var copy = recipe.Clone(); copy.Parts[0].Model!.Materials[0].MaterialPath = "/Game/Characters/Materials/MI_Black";
        checks.Add(new(recipe.Parts[0].Model!.Materials[0].MaterialPath.EndsWith("MI_Yellow") && copy.Parts[0].Model!.ObjText == "v 1 2 3",
            "equipment workshop edits are private and embed OBJ/material recipes without changing the saved donor"));
        var project = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/TestSuit/Characters/BP_Playable" },
            EquipmentSlots = [new() { Slot = 0, Gadget = "Batarang", Custom = recipe }] };
        var reload = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))!;
        checks.Add(new(reload.EquipmentSlots[0].Custom!.Parts[0].Model!.Scale == .4f &&
            reload.EquipmentSlots[0].Custom!.Parts[0].Model!.Materials[0].SourceMaterialName == "Yellow",
            "suit saves retain custom equipment identities, independent component bindings and bake transforms"));
        checks.Add(new(CustomEquipmentService.EtaPackage(project, recipe) == "/Game/Characters/Equipment/Mods/TestSuit/DA_ETA_TestSuit_slot_1" &&
            CustomEquipmentService.DefinitionPackage(project, recipe).StartsWith("/Game/Mods/TestSuit/Equipment/slot_1/") &&
            CustomEquipmentService.TagRows(project).Single().PawnTag == "Equipment.Batcomputer.TestSuit.slot_1",
            "equipment lookup, definition and gameplay-tag identities use separate suit-owned namespaces"));
        checks.Add(new(AnimArchetypeGraftService.RequiresCustomArchetype(project),
            "customizing a native-at-slot gadget still requires an owned archetype/DPRD"));
        checks.Add(new(CustomEquipmentService.EffectiveEtaPackage(project, project.EquipmentSlots[0], recipe.DonorEtaPackage) == CustomEquipmentService.EtaPackage(project, recipe) &&
            CustomEquipmentService.EffectiveEtaPackage(project, new EquipmentSlotChange(), recipe.DonorEtaPackage) == recipe.DonorEtaPackage,
            "legacy equipment replacement rules retain the custom lookup while ordinary slots retain native lookups"));
        var rename = new Dictionary<string, string> { ["/Game/Source/BP_Thing"] = "/Game/Mods/Test/BP_Thing_Test" };
        checks.Add(new(CustomEquipmentService.RenamedObject("Default__BP_Thing_C", rename) == "Default__BP_Thing_Test_C" &&
            CustomEquipmentService.RenamedObject("BP_Thing_C", rename) == "BP_Thing_Test_C" &&
            CustomEquipmentService.RenamedObject("Equipment.Batarang", rename) == "Equipment.Batarang",
            "equipment class/CDO renaming does not retag native behavior and upgrade contracts"));
        var invalid = recipe.Clone(); invalid.Id = "../../Stock"; bool rejected = false;
        try { CustomEquipmentService.Root(project, invalid); } catch (InvalidDataException) { rejected = true; }
        checks.Add(new(rejected, "equipment output refuses unsafe identity paths"));
        checks.Add(new(new EquipmentAssetPart { AssetClass = "StaticMesh" }.CanEdit &&
            !new EquipmentAssetPart { AssetClass = "SkeletalMesh" }.CanEdit &&
            !new EquipmentAssetPart { AssetClass = "NiagaraSystem" }.CanEdit,
            "equipment workshop separates editable static assets from skinned models and parked extra effects"));
        return checks;
    }
}
