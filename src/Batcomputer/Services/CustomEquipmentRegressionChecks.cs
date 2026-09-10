using System.Text.Json;

namespace Batcomputer;

internal static class CustomEquipmentRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);
    private static void CheckUpgradeChoices(List<Result> checks)
    {
        var recipe = new CustomEquipmentRecipe { DonorEtaPackage = EquipmentUpgradeService.Eta };
        checks.Add(new(EquipmentUpgradeService.Disabled(recipe).Count == 0 && EquipmentUpgradeService.Options.Count == 8,
            "old equipment recipes retain all eight Batarang upgrades by default"));
        recipe.DisabledUpgrades = ["alarmarang", "concussive"];
        var clone = recipe.Clone(); clone.DisabledUpgrades.Add("bat-swarm");
        checks.Add(new(recipe.DisabledUpgrades.Count == 2 && EquipmentUpgradeService.Disabled(clone).Count == 3,
            "upgrade selections persist through JSON cloning without sharing mutable lists"));
        int rejected = 0;
        foreach (var invalid in new[] {
            new CustomEquipmentRecipe { DonorEtaPackage = EquipmentUpgradeService.Eta, DisabledUpgrades = ["not-an-upgrade"] },
            new CustomEquipmentRecipe { DonorEtaPackage = EquipmentUpgradeService.Eta, DisabledUpgrades = ["alarmarang", "alarmarang"] },
            new CustomEquipmentRecipe { DonorEtaPackage = "/Game/Other", DisabledUpgrades = ["alarmarang"] } })
            try { _ = EquipmentUpgradeService.Disabled(invalid); } catch (InvalidDataException) { rejected++; }
        checks.Add(new(rejected == 3, "unknown, duplicate and unsupported-donor upgrade choices fail closed"));
        rejected = 0;
        foreach (var count in new[] { 0, 2, 3 }) try { EquipmentUpgradeService.RequireSingleFamilyItem(count); } catch (InvalidDataException) { rejected++; }
        EquipmentUpgradeService.RequireSingleFamilyItem(1);
        checks.Add(new(rejected == 3, "filtered upgrades reject ambiguous shared Batarang attribute loadouts"));
        var icon = new EquipmentAssetPart { OwnerPackage = "/Game/BP_Item", ExportName = "Default", PropertyPath = "HudIcon", AssetClass = "Texture2D" };
        var material = new EquipmentAssetPart { OwnerPackage = icon.OwnerPackage, ExportName = icon.ExportName, PropertyPath = "HudIconMtl", AssetClass = "MaterialInstanceConstant", Package = "/Game/MI_Hud" };
        var wrapped = new EquipmentAssetPart { OwnerPackage = "/Game/MI_Hud", ExportName = "MI_Hud", PropertyPath = "TextureParameterValues[0].0.ParameterValue", AssetClass = "Texture2D" };
        var bca = new EquipmentAssetPart { OwnerPackage = icon.OwnerPackage, ExportName = icon.ExportName, PropertyPath = "Icon", AssetClass = "Texture2D" };
        var profile = new EquipmentAssetProfile(); profile.Parts.AddRange([icon, material, wrapped, bca]);
        var shared = new CustomEquipmentRecipe { HudIcon = new() { SdfPackage = "/Game/Mods/Test/SDF" }, Parts = profile.Parts.Select(p => new EquipmentPartEdit { OwnerPackage = p.OwnerPackage, ExportName = p.ExportName, PropertyPath = p.PropertyPath, ReplacementPackage = "/Game/Mods/Test/OldMissing" }).ToList() };
        checks.Add(new(EquipmentHudIconService.ActiveParts(shared, profile).Single().Key == bca.Key && shared.Parts.Count == 4,
            "visual copy excludes superseded direct/wrapped HUD edits but preserves BCA and inactive saved assignments"));
        shared.HudIcon = null;
        checks.Add(new(EquipmentHudIconService.ActiveParts(shared, profile).Count() == 4, "resetting shared HUD restores dependency ownership of individual edits"));
        var project = new NativeSuitProject { EquipmentSlots = [new() { Custom = new() { Parts = [new() { Model = new() { Materials = [new() { Slot = 0, MaterialPath = "/Game/Mods/Test/MI_EquipmentOnly" }] } }] } }] };
        checks.Add(new(MainForm.AssignedModMaterialPackagesForRelease(project).Contains("/Game/Mods/Test/MI_EquipmentOnly"),
            "equipment-only model materials participate in normal library staging and missing-dependency validation"));
        var thread = new Thread(() => {
            try {
                using var form = new EquipmentUpgradesForm(recipe);
                var multi = (CheckBox)form.Controls.Find("upgrade-multi-throw", true).Single(); multi.Checked = false;
                checks.Add(new(form.DisabledIds.Count == 3 && recipe.DisabledUpgrades.Count == 2 && form.Result is null,
                    "upgrade editor changes remain private until accepted; cancel cannot mutate equipment"));
                checks.Add(new(form.Controls.Find("upgrade-bat-swarm", true).Single().Text.Contains("focus"), "upgrade editor labels focus-only Bat Swarm"));
            } catch (Exception error) { checks.Add(new(false, "upgrade editor checks: " + error.GetBaseException().Message)); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    internal static IReadOnlyList<Result> Run()
    {
        var checks = new List<Result>();
        CheckUpgradeChoices(checks);
        var importAsset = new UAssetAPI.UAsset { Imports = [] }; importAsset.ClearNameIndexList();
        var originalImport = AnimGraftService.EnsureObjectImport(importAsset, "/Game/Equipment/Blowpipe/BP_ED", "BP_ED_C", "/Script/Engine", "BlueprintGeneratedClass");
        var repeatedImport = AnimGraftService.EnsureObjectImport(importAsset, "/Game/Equipment/BlowPipe/BP_ED", "BP_ED_C", "/Script/Engine", "BlueprintGeneratedClass");
        checks.Add(new(originalImport.Index == repeatedImport.Index && importAsset.Imports.Count == 2,
            "equipment grafts reuse case-equivalent native package/class imports instead of emitting duplicate cooked identities"));
        var cdo = AnimGraftService.EnsureObjectImport(importAsset, "/Game/Equipment/BlowPipe/BP_ED", "Default__BP_ED_C", "/Game/Equipment/BlowPipe/BP_ED", "BP_ED_C");
        var sameCdo = AnimGraftService.EnsureObjectImport(importAsset, "/Game/Equipment/Blowpipe/BP_ED", "default__bp_ed_c", "/Game/Equipment/Blowpipe/BP_ED", "BP_ED_C");
        var otherImport = AnimGraftService.EnsureObjectImport(importAsset, "/Game/Mods/Custom/BP_ED", "BP_ED_C", "/Script/Engine", "BlueprintGeneratedClass");
        checks.Add(new(cdo.Index == sameCdo.Index && otherImport.Index != originalImport.Index && importAsset.Imports.Count == 5,
            "case-insensitive CDO reuse still keeps same-named objects in distinct packages separate"));
        var catalog = GameDataService.Instance;
        checks.Add(new(catalog.FindEquipment("NinjaTeleport")?.EdPackage == "/Game/Characters/Equipment/NinjaTeleport_Blink/BP_NinjaTeleport_BlinkProto_ED" &&
            catalog.FindEquipment("TetherLauncher")?.NativeFamilies.Contains("Robin_DickGrayson") == true &&
            catalog.FindEquipment("ElectricTetherLauncher")?.NativeFamilies.Contains("Nightwing") == true &&
            catalog.FindEquipment("TetherLauncher")!.EdPackage != catalog.FindEquipment("ElectricTetherLauncher")!.EdPackage,
            "shipped equipment catalogue retains separate nested tether definitions and the cross-folder teleport definition"));
        CheckHudIconRecipe(checks);
        CheckHudIconEditor(checks);
        var directIcon = new EquipmentAssetPart { AssetClass = "Texture2D", Package = "/Game/UI/Icons/Gadgets/T_UI_IconBatarang_SDF", PropertyPath = "HudIcon" };
        var materialIcon = new EquipmentAssetPart { AssetClass = "Texture2D", Package = directIcon.Package, PropertyPath = "TextureParameterValues[0].0.ParameterValue" };
        var colorIcon = new EquipmentAssetPart { AssetClass = "Texture2D", Package = "/Game/UI/Icons/Gadgets/T_UI_IconBatarang_BCA", PropertyPath = "Icon" };
        var upgradeIcon = new EquipmentAssetPart { AssetClass = "Texture2D", Package = "/Game/UI/Icons/GadgetUpgrades/Batman/T_UI_IconBatarang_Alarmarang_SDF", PropertyPath = "HudIcon" };
        checks.Add(new(EquipmentWorkshopForm.PartTitle(directIcon).StartsWith("HUD SDF / direct") && EquipmentWorkshopForm.PartTitle(materialIcon).StartsWith("HUD SDF / material"), "icon labels distinguish direct HUD and material inputs before artwork names can be truncated"));
        checks.Add(new(EquipmentWorkshopForm.PartTitle(colorIcon).Contains("color / alpha (BCA)") && EquipmentWorkshopForm.PartTitle(upgradeIcon).StartsWith("Upgrade SDF"), "equipment icon labels separate BCA artwork from upgrade SDF inputs"));
        checks.Add(new(EquipmentIconPresentation.Help(materialIcon).Contains("same cooked SDF") && EquipmentIconPresentation.Help(colorIcon).Contains("does not replace an SDF"), "icon help explains paired SDF assignment and prevents BCA/HUD confusion"));
        var alpha = new byte[4096];
        for (var y = 16; y < 48; y++) for (var x = 16; x < 48; x++) alpha[y * 64 + x] = 255;
        var sdf = TextureCookService.EquipmentSdfAlphaForRegression(alpha);
        checks.Add(new(sdf[0] < 128 && sdf[(32 * 64 + 32) * 4] > 128 &&
            Enumerable.Range(0, 4096).All(i => sdf[i * 4 + 1] == 0 && sdf[i * 4 + 2] == 77 && sdf[i * 4 + 3] == 255),
            "transparent HUD silhouettes produce signed red distance with native packed channels"));
        var invalidRejected = 0;
        foreach (var invalidAlpha in new[] { new byte[4096], Enumerable.Repeat((byte)255, 4096).ToArray(), alpha.Select((a, i) => i == 0 ? (byte)255 : a).ToArray() })
            try { TextureCookService.EquipmentSdfAlphaForRegression(invalidAlpha); } catch (InvalidDataException) { invalidRejected++; }
        checks.Add(new(invalidRejected == 3, "HUD conversion rejects empty, fully opaque and edge-clipped silhouettes"));
        CheckAccentSdf(checks);
        checks.Add(new(new[] { EquipmentUiTextureCatalog.ImportKind, EquipmentUiTextureCatalog.AlphaKind, EquipmentUiTextureCatalog.ColorKind, EquipmentUiTextureCatalog.SdfKind, EquipmentUiTextureCatalog.AccentKind }
            .All(k => EquipmentUiTextureCatalog.IsEquipmentIcon(k) && !MainForm.IsUiTextureKind(k)),
            "all equipment icon input types remain separate from automatic character portraits"));
        checks.Add(new(EquipmentIconPresentation.SuggestedFormat(directIcon) == "SDF" &&
            EquipmentIconPresentation.SuggestedFormat(materialIcon) == "SDF" &&
            EquipmentIconPresentation.SuggestedFormat(upgradeIcon) == "SDF" &&
            EquipmentIconPresentation.SuggestedFormat(colorIcon) == "BCA" &&
            EquipmentIconPresentation.SuggestedFormat(new() { AssetClass = "Texture2D", Package = "/Game/T_Unknown" }) is null,
            "equipment icon suggestions follow native SDF/BCA bindings without guessing unknown slots"));
        checks.Add(new(EquipmentUiTextureCatalog.Format(EquipmentUiTextureCatalog.AlphaKind, "") == "SDF" &&
            EquipmentUiTextureCatalog.Format(EquipmentUiTextureCatalog.ColorKind, "") == "BCA" &&
            EquipmentUiTextureCatalog.Format(EquipmentUiTextureCatalog.SdfKind, "") == "SDF" &&
            EquipmentUiTextureCatalog.Format(EquipmentUiTextureCatalog.AccentKind, "") == "SDF" &&
            EquipmentUiTextureCatalog.Format("", EquipmentUiTextureCatalog.AccentProfile) == "SDF",
            "icon picker labels generated and legacy output recipes by BCA/SDF format"));
        var collisions = 0;
        try { EquipmentIconPairCookService.CheckCollisions(["/Game/Mods/Test/T_Icon_BCA", "/Game/Mods/Test/T_Icon_SDF"], ["Icon (BCA)", "Icon (SDF)"], ["/game/mods/test/t_icon_sdf"], []); }
        catch (InvalidOperationException) { collisions++; }
        try { EquipmentIconPairCookService.CheckCollisions(["/Game/Mods/Test/T_Icon_BCA"], ["Icon (BCA)"], [], ["icon (bca)"]); }
        catch (InvalidOperationException) { collisions++; }
        checks.Add(new(collisions == 2, "paired icon imports reject existing output paths and names before writing either recipe"));
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
        checks.Add(new(EquipmentWorkshopPolicy.Evaluate(allowed, skeletalProfile, db).CanCustomize && EquipmentWorkshopPolicy.Evaluate(allowed, skeletalProfile, db).Reason.Contains("EXPERIMENTAL"),
            "untested skeletal equipment is available with an explicit experimental warning"));
        bool oldRecipeRejected = false;
        try { EquipmentWorkshopPolicy.RequireEditable(allowed, skeletalProfile, db); } catch (InvalidDataException) { oldRecipeRejected = true; }
        checks.Add(new(!oldRecipeRejected, "the builder permits experimental skeletal donors while mesh imports remain independently validated"));
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
        CheckModelClipboard(checks);
        var uiProjects = new[] { new NativeSuitProject { DisplayName = "Current", GeneratedTextures = [
            new() { DisplayName = "HUD", Kind = EquipmentUiTextureCatalog.SdfKind, CookProfile = EquipmentUiTextureCatalog.SdfProfile, PackagePath = "/Game/Mods/A/T_Hud" },
            new() { DisplayName = "Body", Kind = "Character texture", PackagePath = "/Game/Mods/A/T_Body" }] },
            new NativeSuitProject { DisplayName = "Other suit", GeneratedTextures = [new() { DisplayName = "Portrait", Kind = "Character icon", PackagePath = "/Game/Mods/B/T_Icon" },
            new() { DisplayName = "Alias", Kind = "UI icon", PackagePath = "/Game/Mods/A/T_Hud" }] } };
        var uiEntries = EquipmentUiTextureCatalog.Entries(uiProjects);
        checks.Add(new(uiEntries.Count == 2 && uiEntries.Any(entry => entry.Owner == "Other suit") && uiEntries.Single(entry => entry.Package.EndsWith("T_Hud")).Owner == "Current",
            "equipment icon picker includes other projects' UI cooks, deduplicates package aliases and excludes body textures"));
        checks.Add(new(!MainForm.IsUiTextureKind(EquipmentUiTextureCatalog.SdfKind) &&
            TextureCookTemplateService.RetocFilters.Contains("Content/UI/Icons/Gadgets/T_UI_IconBatarang_SDF") &&
            TextureCookTemplateService.RequiredPackageExtensionsForRegression(TextureCookTemplateService.EquipmentSdfTemplateFolder).SequenceEqual(new[] { ".uasset", ".uexp" }),
            "equipment SDF profile preserves its inline donor layout and cannot be auto-assigned as a suit portrait"));
        return checks;
    }

    private static void CheckAccentSdf(List<Result> checks)
    {
        var white = new byte[64 * 64 * 4];
        for (var y = 10; y < 54; y++) for (var x = 10; x < 54; x++)
            for (var c = 0; c < 4; c++) white[(y * 64 + x) * 4 + c] = 255;
        var marked = white.ToArray();
        for (var y = 24; y < 40; y++) for (var x = 24; x < 40; x++)
        { marked[(y * 64 + x) * 4] = 0; marked[(y * 64 + x) * 4 + 2] = 0; }
        var plain = EquipmentAccentSdfService.Generate(white, 64, 64);
        var accent = EquipmentAccentSdfService.Generate(marked, 64, 64);
        checks.Add(new(Enumerable.Range(0, 4096).All(i => plain[i * 4] == accent[i * 4]) &&
            accent[(32 * 64 + 32) * 4 + 1] > 128 && accent[0 + 1] == 0 &&
            Enumerable.Range(0, 4096).All(i => accent[i * 4 + 2] == 77 && accent[i * 4 + 3] == 255),
            "green markers add an independent accent SDF without removing any of the red silhouette or changing native blue/alpha"));
        checks.Add(new(Enumerable.Range(0, 4096).All(i => plain[i * 4 + 1] == 0) &&
            Enumerable.Range(0, 4096).Min(i => plain[i * 4]) == 0 && Enumerable.Range(0, 4096).Max(i => plain[i * 4]) == 255,
            "unmarked artwork generates no accent and distance fields saturate across the full 0–255 range"));
        var hidden = white.ToArray(); hidden[1] = 255;
        checks.Add(new(EquipmentAccentSdfService.Generate(hidden, 64, 64).SequenceEqual(plain),
            "green RGB in fully transparent pixels cannot accidentally add an accent"));
        // Opaque and partially transparent green/grey blends from antialiased PNG edges.
        var blended = marked.ToArray(); var blendedWhite = white.ToArray(); var solidEdges = marked.ToArray();
        var edgeColours = new (byte R, byte G, byte B, byte A)[] {
            (0, 38, 0, 255), (0, 83, 0, 255), (49, 83, 49, 255),
            (21, 76, 21, 255), (0, 37, 0, 207), (23, 61, 23, 255), (0, 32, 0, 255), (0, 95, 0, 255)
        };
        for (var x = 24; x < 40; x++)
        {
            var i = (23 * 64 + x) * 4; var colour = edgeColours[(x - 24) % edgeColours.Length];
            blended[i] = colour.R; blended[i + 1] = colour.G; blended[i + 2] = colour.B; blended[i + 3] = colour.A;
            blendedWhite[i + 3] = colour.A;
            solidEdges[i] = 0; solidEdges[i + 1] = 255; solidEdges[i + 2] = 0; solidEdges[i + 3] = colour.A;
        }
        var blendedSdf = EquipmentAccentSdfService.Generate(blended, 64, 64);
        var blendedPlain = EquipmentAccentSdfService.Generate(blendedWhite, 64, 64);
        var solidSdf = EquipmentAccentSdfService.Generate(solidEdges, 64, 64);
        checks.Add(new(Enumerable.Range(0, 4096).All(i => blendedSdf[i * 4] == blendedPlain[i * 4]) && blendedSdf[(32 * 64 + 32) * 4 + 1] > 128,
            "dark-green and green/grey antialiased edges import without changing the alpha-defined silhouette"));
        checks.Add(new(Enumerable.Range(0, 4096).Any(i => blendedSdf[i * 4 + 1] != solidSdf[i * 4 + 1]) &&
            Enumerable.Range(0, 4096).All(i => blendedSdf[i * 4 + 2] == 77 && blendedSdf[i * 4 + 3] == 255),
            "dark-green edge coverage stays fractional rather than expanding into a solid accent"));
        var colourRejections = 0;
        foreach (var rgb in new (byte R, byte G, byte B)[] { (83, 0, 0), (0, 0, 83), (0, 83, 83), (83, 83, 0), (200, 0, 77) })
        {
            var invalidColour = blended.ToArray(); var i = (32 * 64 + 32) * 4;
            invalidColour[i] = rgb.R; invalidColour[i + 1] = rgb.G; invalidColour[i + 2] = rgb.B;
            try { EquipmentAccentSdfService.Generate(invalidColour, 64, 64); } catch (InvalidDataException) { colourRejections++; }
        }
        checks.Add(new(colourRejections == 5, "accepting dark-green edges still rejects red, blue, cyan, yellow and prepared-SDF artwork"));
        var wrongColour = white.ToArray(); wrongColour[(32 * 64 + 32) * 4] = 255;
        wrongColour[(32 * 64 + 32) * 4 + 1] = 0; wrongColour[(32 * 64 + 32) * 4 + 2] = 77;
        var edge = white.ToArray(); for (var y = 20; y < 44; y++) for (var x = 0; x < 10; x++)
            for (var c = 0; c < 4; c++) edge[(y * 64 + x) * 4 + c] = 255;
        var failures = 0;
        foreach (var invalid in new[] { new byte[white.Length], Enumerable.Repeat((byte)255, white.Length).ToArray(), wrongColour, edge })
            try { EquipmentAccentSdfService.Generate(invalid, 64, 64); } catch (InvalidDataException) { failures++; }
        checks.Add(new(failures == 4, "marked SDF import rejects empty, opaque-background, already-coloured SDF and clipped sources"));
        var tiny = new byte[256 * 256 * 4];
        for (var y = 32; y < 224; y++) for (var x = 32; x < 224; x++) for (var c = 0; c < 4; c++) tiny[(y * 256 + x) * 4 + c] = 255;
        tiny[(128 * 256 + 128) * 4] = 0; tiny[(128 * 256 + 128) * 4 + 2] = 0;
        var tinyRejected = false;
        try { EquipmentAccentSdfService.Generate(tiny, 256, 256); } catch (InvalidDataException e) { tinyRejected = e.Message.Contains("green accent"); }
        checks.Add(new(tinyRejected, "a painted accent that disappears at 64px produces an actionable error instead of silently losing it"));
        checks.Add(new(EquipmentUiTextureCatalog.NewImportKinds.SequenceEqual(new[] { EquipmentUiTextureCatalog.ColorKind, EquipmentUiTextureCatalog.AccentKind }) &&
            TextureCookTemplateService.RequiredPackageExtensionsForRegression(TextureCookTemplateService.EquipmentAccentTemplateFolder).SequenceEqual(new[] { ".uasset", ".uexp" }),
            "new imports offer only separate BCA and marked-SDF roles; the new SDF profile uses the seven-mip inline native donor"));
    }

    private static void CheckModelClipboard(List<Result> checks)
    {
        var source = new EquipmentAssetPart { OwnerPackage = "/Game/BP_Weapon", ExportName = "WeaponMesh", PropertyPath = "StaticMesh", Package = "/Game/SM_Held", AssetClass = "StaticMesh" };
        var target = new EquipmentAssetPart { OwnerPackage = "/Game/BP_Projectile", ExportName = "ProjectileMesh", PropertyPath = "StaticMesh", Package = "/Game/SM_Projectile", AssetClass = "StaticMesh" };
        var second = new EquipmentAssetPart { OwnerPackage = "/Game/BP_UpgradeProjectile", ExportName = "ProjectileMesh", PropertyPath = "StaticMesh", Package = "/Game/SM_Upgrade", AssetClass = "StaticMesh" };
        var original = new CustomEquipmentRecipe { DonorEtaPackage = "/Game/DA_ETA_Batarang" };
        EquipmentModelClipboard.StoreModel(original, source, new() { SourceName = "banana.obj", ObjText = "v 1 2 3", Scale = .4f,
            X = 1, Y = 2, Z = 3, Pitch = 4, Yaw = 5, Roll = 6, Materials = [new() { Slot = 0, SourceMaterialName = "Yellow",
                StableSlotName = "Yellow_0", MaterialPath = "/Game/MI_Yellow" }, new() { Slot = 1, SourceMaterialName = "Stem", StableSlotName = "Stem_1", MaterialPath = "/Game/MI_Green" }] });
        var originalJson = JsonSerializer.Serialize(original);
        var working = original.Clone();
        var clipboard = new EquipmentModelClipboard();
        checks.Add(new(!clipboard.CanPaste(working, target) && EquipmentModelClipboard.CanCopy(working, source) &&
            !EquipmentModelClipboard.CanCopy(working, target), "equipment model copy requires a custom OBJ and paste starts disabled"));
        clipboard.Copy(working, source);
        working.Parts[0].Model!.Scale = 9; working.Parts[0].Model!.Materials[0].MaterialPath = "/Game/MI_Black";
        working.Parts.Add(new() { OwnerPackage = target.OwnerPackage, ExportName = target.ExportName, PropertyPath = target.PropertyPath, ReplacementPackage = "/Game/SM_Other" });
        working.Parts.Add(new() { OwnerPackage = target.OwnerPackage, ExportName = target.ExportName, PropertyPath = "OverrideMaterials[0]", ReplacementPackage = "/Game/MI_Obsolete" });
        working.Parts.Add(new() { OwnerPackage = target.OwnerPackage, ExportName = "OtherMesh", PropertyPath = "OverrideMaterials[0]", ReplacementPackage = "/Game/MI_Keep" });
        clipboard.Paste(working, target);
        var pasted = working.Parts.Single(edit => edit.Key == target.Key);
        checks.Add(new(pasted.OwnerPackage == target.OwnerPackage && pasted.ExportName == target.ExportName && pasted.OriginalPackage == target.Package &&
            pasted.ReplacementPackage == "" && pasted.Model!.SourceName == "banana.obj" && pasted.Model.ObjText == "v 1 2 3" && pasted.Model.Scale == .4f &&
            pasted.Model.X == 1 && pasted.Model.Y == 2 && pasted.Model.Z == 3 && pasted.Model.Pitch == 4 && pasted.Model.Yaw == 5 && pasted.Model.Roll == 6 &&
            pasted.Model.Materials.Count == 2 && pasted.Model.Materials[0].MaterialPath == "/Game/MI_Yellow" && pasted.Model.Materials[1].StableSlotName == "Stem_1",
            "model paste preserves every copied setting while binding only to the destination equipment component"));
        checks.Add(new(!working.Parts.Any(edit => edit.OwnerPackage == target.OwnerPackage && edit.ExportName == target.ExportName && edit.PropertyPath.StartsWith("OverrideMaterials")) &&
            working.Parts.Any(edit => edit.ExportName == "OtherMesh" && edit.ReplacementPackage == "/Game/MI_Keep"),
            "model paste replaces conflicting destination materials without changing neighboring components"));
        pasted.Model!.Materials[0].MaterialPath = "/Game/MI_Red"; pasted.Model.X = 99;
        clipboard.Paste(working, second);
        checks.Add(new(working.Parts.Single(edit => edit.Key == second.Key).Model!.Materials[0].MaterialPath == "/Game/MI_Yellow" &&
            working.Parts.Single(edit => edit.Key == second.Key).Model!.X == 1 && working.Parts.Single(edit => edit.Key == source.Key).Model!.Scale == 9,
            "copied model snapshots and repeated pastes are independent of later source and destination edits"));
        clipboard.Paste(working, target); clipboard.Paste(working, target);
        checks.Add(new(working.Parts.Count(edit => edit.Key == target.Key) == 1 && JsonSerializer.Serialize(original) == originalJson &&
            working.Clone().Parts.Single(edit => edit.Key == second.Key).Model!.Materials[1].MaterialPath == "/Game/MI_Green",
            "repeated pastes stay idempotent, survive recipe reload and never mutate the saved parent recipe"));
        var blocked = new[] { new EquipmentAssetPart { AssetClass = "SkeletalMesh" }, new EquipmentAssetPart { AssetClass = "Texture2D" }, new EquipmentAssetPart { AssetClass = "NiagaraSystem" } };
        var before = JsonSerializer.Serialize(working); var rejected = 0;
        foreach (var part in blocked) try { clipboard.Paste(working, part); } catch (InvalidDataException) { rejected++; }
        checks.Add(new(rejected == blocked.Length && blocked.All(part => !clipboard.CanPaste(working, part)) && !clipboard.CanPaste(working.Clone(), target) &&
            before == JsonSerializer.Serialize(working), "equipment model clipboard refuses non-static parts and other workshop recipes without mutation"));
        var invalid = pasted.Model.Clone(); invalid.Scale = float.NaN; var failed = false;
        try { EquipmentModelClipboard.StoreModel(working, target, invalid); } catch (InvalidDataException) { failed = true; }
        checks.Add(new(failed && before == JsonSerializer.Serialize(working), "invalid pasted model settings cannot partially replace a working equipment component"));
        CheckModelClipboardUi(checks, original, source, target);
    }

    private static void CheckHudIconRecipe(List<Result> checks)
    {
        var recipe = new CustomEquipmentRecipe { HudIcon = new() { SdfPackage = "/Game/Mods/Test/T_Icon_SDF", GlowPower = 1.25f } };
        var clone = recipe.Clone(); clone.HudIcon!.GlowPower = 0;
        checks.Add(new(recipe.HudIcon.GlowPower == 1.25f && clone.HudIcon.SdfPackage == recipe.HudIcon.SdfPackage &&
            JsonSerializer.Deserialize<CustomEquipmentRecipe>("{\"Id\":\"legacy\",\"Parts\":[]}")!.HudIcon is null,
            "HUD material recipes save/clone independently and older equipment stays on individual assignments"));
        var invalid = 0;
        foreach (var icon in new EquipmentHudIconRecipe[] {
            new(), new() { SdfPackage = "/Game/Mods/Test/T_BCA" },
            new() { SdfPackage = recipe.HudIcon.SdfPackage, GlowPower = float.NaN },
            new() { SdfPackage = recipe.HudIcon.SdfPackage, GlowPower = -1 },
            new() { SdfPackage = recipe.HudIcon.SdfPackage, GlowPower = 5 } })
            try { EquipmentHudIconService.Validate(icon); } catch (InvalidDataException) { invalid++; }
        checks.Add(new(invalid == 5, "HUD material rejects absent/BCA inputs and invalid scalar ranges before packaging"));
        var legacyIcon = JsonSerializer.Deserialize<EquipmentHudIconRecipe>("{\"SdfPackage\":\"/Game/Mods/Test/T_Icon_SDF\",\"GlowPower\":1.25}")!;
        checks.Add(new(legacyIcon.FillColor == "#FFFFFF" && legacyIcon.FillOpacity == 1 && legacyIcon.OutlineColor == "#000000" &&
            legacyIcon.OutlineOpacity == .8f && legacyIcon.OutlineWidth == .2f && legacyIcon.Grain == .5f && legacyIcon.Sharpness == 8 && legacyIcon.GlowPower == 1.25f,
            "older HUD recipes inherit native appearance defaults without resetting their saved glow"));
        recipe.HudIcon.FillColor = "#80C040"; recipe.HudIcon.OutlineColor = "#204080"; recipe.HudIcon.OutlineWidth = .35f;
        recipe.HudIcon.FillOpacity = .65f; recipe.HudIcon.OutlineOpacity = .45f; recipe.HudIcon.Grain = .15f; recipe.HudIcon.Sharpness = 12;
        var styled = recipe.Clone(); styled.HudIcon!.FillColor = "#FFFFFF";
        checks.Add(new(recipe.HudIcon.FillColor == "#80C040" && styled.HudIcon.OutlineColor == "#204080" && styled.HudIcon.OutlineWidth == .35f &&
            styled.HudIcon.FillOpacity == .65f && styled.HudIcon.OutlineOpacity == .45f && styled.HudIcon.Grain == .15f && styled.HudIcon.Sharpness == 12,
            "all icon appearance settings persist and clone independently"));
        var rejectedAppearance = 0;
        Action<EquipmentHudIconRecipe>[] invalidAppearance = [
            r => r.FillColor = "#GG0000", r => r.OutlineColor = "white", r => r.FillColor = null!,
            r => r.FillOpacity = float.NaN, r => r.FillOpacity = 1.1f, r => r.OutlineOpacity = -1,
            r => r.OutlineWidth = 1.01f, r => r.Grain = float.PositiveInfinity, r => r.Sharpness = 0, r => r.Sharpness = 33
        ];
        foreach (var change in invalidAppearance)
        {
            var bad = recipe.Clone().HudIcon!; change(bad);
            try { EquipmentHudIconService.Validate(bad); } catch (InvalidDataException) { rejectedAppearance++; }
        }
        checks.Add(new(rejectedAppearance == invalidAppearance.Length, "icon appearance rejects invalid colours, nonfinite values and out-of-range overrides"));
        checks.Add(new(EquipmentHudIconService.ArtworkHelp.Contains("No painted outline needed") && EquipmentHudIconService.ReadColor("#80c040") == Color.FromArgb(128, 192, 64),
            "SDF guidance does not require an outline and colour hex parsing is case-insensitive"));
        var binding = new EquipmentAssetPart { AssetClass = "MaterialInstanceConstant", OwnerPackage = "/Game/BP_ED", Package = "/Game/MI_Hud", PropertyPath = "HudIconMtl" };
        var direct = new EquipmentAssetPart { AssetClass = "Texture2D", OwnerPackage = "/Game/BP_Instance", PropertyPath = "Mode.HudIcon" };
        var material = new EquipmentAssetPart { AssetClass = "Texture2D", OwnerPackage = "/Game/MI_Hud", PropertyPath = "TextureParameterValues[0].0.ParameterValue" };
        var reticle = new EquipmentAssetPart { AssetClass = "Texture2D", PropertyPath = "Reticle" };
        var otherMaterial = new EquipmentAssetPart { AssetClass = "Texture2D", OwnerPackage = "/Game/MI_Body", PropertyPath = material.PropertyPath };
        var profile = new EquipmentAssetProfile(); profile.Parts.AddRange([binding, direct, material, reticle, otherMaterial]);
        checks.Add(new(EquipmentHudIconService.HasBindings(profile) && new[] { binding, direct, material }.All(p => EquipmentHudIconService.Manages(profile, p)) &&
            !EquipmentHudIconService.Manages(profile, reticle) && !EquipmentHudIconService.Manages(profile, otherMaterial),
            "shared HUD material owns only HUD bindings; reticles, body materials and unrelated upgrade art remain independent"));
        checks.Add(new(EquipmentHudIconService.MaterialPackage("/Game/Mods/Test/Equipment/slot_1") == "/Game/Mods/Test/Equipment/slot_1/UI/MI_EquipmentIcon_HUD",
            "HUD material has a private slot-local package and an unambiguous non-numbered object name"));
        var project = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/Test/Characters/BP_Test_Playable" }, EquipmentSlots = [new() { Custom = recipe }] };
        checks.Add(new(CustomCharacterProjectService.VisualCopyRoots(project).Contains(recipe.HudIcon.SdfPackage),
            "copying a character includes a HUD-only SDF dependency even when no individual part references it"));
    }

    private static void CheckHudIconEditor(List<Result> checks)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var recipe = new EquipmentHudIconRecipe { SdfPackage = "/Game/Mods/Test/T_SDF", FillColor = "#80C040", OutlineColor = "#204080",
                    FillOpacity = .65f, OutlineOpacity = .45f, OutlineWidth = .35f, GlowPower = 1.25f, Grain = .15f, Sharpness = 12 };
                var before = JsonSerializer.Serialize(recipe);
                EquipmentUiTextureCatalog.Entry[] icons = [new("Dart", "Test character", recipe.SdfPackage, "SDF")];
                static IEnumerable<Control> Walk(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Walk(child)));
                static void Click(Form form, string text) => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(Walk(form).OfType<Button>().Single(b => b.Text == text), [EventArgs.Empty]);
                // Exercise handlers on private, hidden controls. No desktop interaction.
                using (var form = new EquipmentHudIconForm(recipe, icons, ""))
                {
                    Click(form, "Use material");
                    checks.Add(new(form.Result is not null && JsonSerializer.Serialize(form.Result) == before,
                        "HUD editor saves/reopens every appearance field without touching the source recipe"));
                }
                using (var form = new EquipmentHudIconForm(recipe, icons, ""))
                {
                    Click(form, "Native defaults"); Click(form, "Use material");
                    checks.Add(new(JsonSerializer.Serialize(form.Result) == JsonSerializer.Serialize(new EquipmentHudIconRecipe { SdfPackage = recipe.SdfPackage }),
                        "Native defaults keeps the selected SDF while restoring all appearance settings"));
                }
                using (var form = new EquipmentHudIconForm(recipe, icons, ""))
                {
                    Walk(form).OfType<NumericUpDown>().Single(c => c.AccessibleName == "Glow strength").Value = 4;
                    Click(form, "Cancel");
                    checks.Add(new(form.Result is null && JsonSerializer.Serialize(recipe) == before, "canceling HUD edits leaves the existing recipe unchanged"));
                }
                using (var form = new EquipmentHudIconForm(recipe, icons, ""))
                {
                    Click(form, "Remove material");
                    checks.Add(new(form.DialogResult == DialogResult.OK && form.Result is null && JsonSerializer.Serialize(recipe) == before,
                        "Remove material returns a private reset request without deleting the source recipe"));
                }
                using (var form = new EquipmentHudIconForm(null, icons, "/Game/Mods/Other/T_SDF"))
                {
                    Click(form, "Use material");
                    checks.Add(new(form.Result?.SdfPackage == "/Game/Mods/Other/T_SDF", "unknown cooked paths remain editable and save through manual mode"));
                }
                using (var form = new EquipmentHudIconForm(recipe, icons, ""))
                {
                    var controls = Walk(form).ToArray();
                    controls.OfType<CheckBox>().Single(c => c.Text == "Package path").Checked = true;
                    controls.OfType<TextBox>().Single(c => c.AccessibleName == "SDF package path").Text = "/Game/Mods/Other/T_SDF";
                    controls.OfType<CheckBox>().Single(c => c.Text == "Package path").Checked = false;
                    Click(form, "Use material");
                    checks.Add(new(form.Result?.SdfPackage == recipe.SdfPackage, "picker mode saves the visible selection, not a hidden manual path"));
                }
            }
            catch (Exception ex) { checks.Add(new(false, "HUD editor regression: " + ex.Message)); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }

    private static void CheckModelClipboardUi(List<Result> checks, CustomEquipmentRecipe original, EquipmentAssetPart source, EquipmentAssetPart target)
    {
        var thread = new Thread(() =>
        {
            try
            {
                // Hidden, code-built controls only; never show or drive the desktop.
                using var form = new EquipmentWorkshopForm(original);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                object Field(string name) => typeof(EquipmentWorkshopForm).GetField(name, flags)!.GetValue(form)!;
                void Set(string name, object value) => typeof(EquipmentWorkshopForm).GetField(name, flags)!.SetValue(form, value);
                void Invoke(string name) => typeof(EquipmentWorkshopForm).GetMethod(name, flags)!.Invoke(form, null);
                var profile = new EquipmentAssetProfile(); profile.Parts.Add(source); profile.Parts.Add(target);
                Set("_profile", profile); Set("_access", new EquipmentWorkshopPolicy.Access(true, "Test static equipment", ["Batman"]));
                form.ClientSize = new Size(1040, 780); _ = form.Handle;
                Invoke("RefreshParts");
                var parts = (ListBox)Field("_parts"); parts.SelectedItem = parts.Items.Cast<EquipmentAssetPart>().Single(part => part.Key == source.Key);
                var copyButton = (Button)Field("_copyModel"); var pasteButton = (Button)Field("_pasteModel");
                var copyInitiallyAvailable = copyButton.Enabled && !pasteButton.Enabled;
                Invoke("CopyModel"); parts.SelectedItem = parts.Items.Cast<EquipmentAssetPart>().Single(part => part.Key == target.Key);
                var pasteAvailable = !copyButton.Enabled && pasteButton.Enabled;
                Invoke("PasteModel");
                checks.Add(new(copyInitiallyAvailable && pasteAvailable && copyButton.Enabled && pasteButton.Enabled &&
                    ((CustomEquipmentRecipe)Field("_working")).Parts.Single(part => part.Key == target.Key).Model!.SourceName == "banana.obj",
                    "equipment workshop copy/paste controls follow selection and update the selected private recipe"));
                void Layout(Control control) { control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
                Layout(form);
                checks.Add(new(new[] { copyButton, pasteButton }.All(button => button.ClientSize.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width + 8 && button.Height >= 30),
                    "equipment model copy/paste labels fit at the workshop's minimum width"));
                var icon = new EquipmentAssetPart { AssetClass = "Texture2D", OwnerPackage = "/Game/MI_Hud", ExportName = "MI_Hud", PropertyPath = "TextureParameterValues[0]", Package = "/Game/T_Hud" };
                profile.Parts.Add(icon); parts.Items.Add(icon); parts.SelectedItem = icon;
                var picker = (ThemedDropDown)Field("_cookedIcons"); var path = (TextBox)Field("_replacement");
                picker.Items.Add(new EquipmentUiTextureCatalog.Entry("Test HUD", "Other suit", "/Game/Mods/Other/T_Hud", EquipmentUiTextureCatalog.SdfProfile)); picker.SelectedIndex = 1;
                var selectedPath = path.Text; path.Text = "/Game/Mods/Manual/T_Hud";
                Invoke("ApplyReplacement");
                checks.Add(new(picker.Enabled && !path.ReadOnly && selectedPath == "/Game/Mods/Other/T_Hud" &&
                    ((CustomEquipmentRecipe)Field("_working")).Parts.Single(part => part.Key == icon.Key).ReplacementPackage == "/Game/Mods/Manual/T_Hud",
                    $"equipment texture picker fills an editable path and manual paths apply to the selected texture binding (enabled={picker.Enabled}, readOnly={path.ReadOnly}, selected={selectedPath})"));
                profile.Parts.Add(new() { AssetClass = "MaterialInstanceConstant", PropertyPath = "HudIconMtl", Package = icon.OwnerPackage });
                var privateRecipe = (CustomEquipmentRecipe)Field("_working");
                privateRecipe.HudIcon = new() { SdfPackage = "/Game/Mods/Test/T_Shared_SDF" };
                Invoke("RefreshParts"); parts.SelectedItem = icon;
                var hudButton = (Button)Field("_hudIcon");
                checks.Add(new(hudButton.Enabled && !((Button)Field("_apply")).Enabled && !picker.Enabled && path.ReadOnly &&
                    privateRecipe.Parts.Any(p => p.Key == icon.Key), "shared HUD material locks its individual inputs without deleting older edits"));
                privateRecipe.HudIcon = null; Invoke("RefreshParts"); parts.SelectedItem = icon;
                checks.Add(new(((Button)Field("_apply")).Enabled && !path.ReadOnly, "resetting shared HUD material restores individual texture editing"));
                Set("_access", new EquipmentWorkshopPolicy.Access(false, "View only", [])); Invoke("ShowPart");
                Invoke("RefreshParts");
                checks.Add(new(!hudButton.Enabled, "view-only equipment cannot configure HUD materials"));
                checks.Add(new(!copyButton.Enabled && !pasteButton.Enabled, "view-only equipment cannot enable model clipboard actions"));
            }
            catch (Exception ex) { checks.Add(new(false, "equipment model clipboard UI checks: " + ex.GetBaseException().Message)); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
