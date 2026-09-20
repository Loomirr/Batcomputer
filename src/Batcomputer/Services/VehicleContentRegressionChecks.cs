using System.Buffers.Binary;
using System.Text.Json;

namespace Batcomputer;

internal static class VehicleContentRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool value, string description) => results.Add((value, description));
        static bool Rejects(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        Check(ModelPreviewControl.IsVehicleWorkshopMessage("vehicleWorkshopSurface") && ModelPreviewControl.IsVehicleWorkshopMessage("vehicleWorkshopToybox") && !ModelPreviewControl.IsVehicleWorkshopMessage("unknown"), "surface and toybox commands reach the desktop workshop host");
        Check(VehicleMaterialService.SlotName(new() { DisplayName = "70s Batmobile" }, 0) == "MI_Slot0_70sBatmobile", "vehicle material copies use readable car and slot names");
        Check(!VehicleMaterialService.Owned(new(), "/Game/Characters/MI_Black") && !VehicleMaterialService.Owned(new(), "/Game/Mods/Other/Materials/MI_Test"), "vehicle material editing cannot overwrite a native or another vehicle's package");
        Check(Rejects(() => SkinnedCookWorkspace.ValidateLength(new string('x', 180), "/Game/Mods/Test/SK_Test")), "overlong temporary cook filenames fail before launching Unreal");
        var failure = SkinnedMeshCookService.FailureSummary("LogTextureFormatASTC: Display: Loaded\nLogCook: Error: Couldn't save package, filename is too long (269 >= 260)\nFile: bad.uasset");
        Check(failure.Contains("269 >= 260") && !failure.Contains("ASTC"), "skinned import errors show the actual cook failure instead of startup texture messages");
        var lightDefault = VehicleLightService.ReadDefaults("H_Light_01_Light_GEN_VARIABLE", Newtonsoft.Json.Linq.JObject.Parse("{\"Intensity\":160,\"OuterConeAngle\":50,\"LightColor\":{\"R\":114,\"G\":243,\"B\":252}}"));
        Check(lightDefault.Intensity == 160 && lightDefault.OuterCone == 50 && lightDefault.R == 114, "headlight preview uses donor-specific color and brightness");
        foreach (var donor in VehicleDonorService.All)
        {
            var vehicle = new VehicleProject { DonorId = donor.Id, SizeMultiplier = 1.4f };
            VehicleProjectService.ValidateIdentity(vehicle);
            vehicle.Lights.Add(VehicleLightService.Defaults("H_Light_01_Light_GEN_VARIABLE"));
            Check(Rejects(() => VehicleProjectService.ValidateIdentity(vehicle)) == !donor.HeadlightEditing, "headlight controls follow verified donor support: " + donor.Id);
            Check(vehicle.Clone().DonorId == donor.Id && vehicle.Clone().SizeMultiplier == 1.4f, "vehicle donor and size survive save/reopen: " + donor.Label);
        }
        Check(new[] { float.NaN, float.PositiveInfinity, .49f, 2.01f }.All(scale => Rejects(() => VehicleProjectService.ValidateIdentity(new() { SizeMultiplier = scale }))), "unsafe whole-vehicle sizes are rejected");
        Check(VehicleDonorService.All.Select(d => d.Id).Distinct().Count() == VehicleDonorService.All.Length && VehicleDonorService.All.Length == 6, "six driving bases have distinct stable identities");
        Check(VehicleDonorService.ExtractionFilter("/DLC_PartyPack/Vehicles/DA_Test") == "Plugins/GameFeatures/DLC_PartyPack/Content/Vehicles/DA_Test", "DLC donor extraction uses the plugin mount");
        Check(VehicleDonorService.All.Single(d => d.Id == "batmobilemonstertruck").RequiredDlc == "Party Pack DLC", "Batmobeast declares its DLC requirement");
        Check(VehicleDonorService.All.Where(d => d.Id is "batmobile1995" or "batmobile1989" or "batmobile2005").All(d => d.Animations.Count >= 3), "native mechanics clips are catalogued for three driving bases");
        var transform = VehicleAnimationPreviewService.Transform(new(100,200,300), new(.5f,.5f,.5f,.5f), new(1,2,3));
        Check(transform.SequenceEqual(new float[] {1,3,2,-.5f,-.5f,-.5f,.5f,1,3,2}), "motion preview uses corrected glTF handedness and metres");
        Check(EmbeddedAssets.ReadBytes("preview/VehicleMotion.js") is { Length: > 1000 }, "vehicle motion controller ships in the offline viewer");
        var placeholder = CustomStaticMeshImportService.DefaultMaterialPackagePath;
        Check(VehiclePaintService.ImportSlotMaterial("lego_solid", placeholder, true) == VehiclePaintService.Material("Solid"), "vehicle reimport upgrades placeholder LEGO slots case-insensitively");
        Check(VehiclePaintService.ImportSlotMaterial("LEGO_Solid", "/Game/Mods/Test/MI_Custom", true) == "/Game/Mods/Test/MI_Custom" && VehiclePaintService.ImportSlotMaterial("LEGO_Solid", placeholder, false) == placeholder, "material defaults preserve user choices and non-vehicle imports");
        Check(new VehicleProject().MatchBuildUpToBody && !new VehicleProject { MatchBuildUpToBody = false }.Clone().MatchBuildUpToBody, "assembly material matching defaults on and preserves an explicit opt-out");
        var retryCount = 0;
        var retryValue = SkinnedGlbExportService.WithExportFileRetry(() => { if (retryCount++ == 0) throw new IOException("locked", unchecked((int)0x80070020)); return 7; });
        Check(retryCount == 2 && retryValue == 7, "reference GLB export retries transient sharing violations");
        var deniedCount = 0; bool denied = false;
        try { SkinnedGlbExportService.WithExportFileRetry<int>(() => { deniedCount++; throw new UnauthorizedAccessException(); }); }
        catch (UnauthorizedAccessException) { denied = true; }
        Check(denied && deniedCount == 1, "reference GLB export does not retry permissions failures");
        var unsupportedLights = new VehicleProject { DonorId = "batmobile1997", AccentColor = new() { R = 255 } };
        Check(Rejects(() => VehicleProjectService.ValidateIdentity(unsupportedLights)), "new donor rigs cannot silently use Forever-specific lamp bytecode edits");
        var part = new VehicleToyboxPart { Added = true, Component = "BC_Part_123456abcdef_GEN_VARIABLE", MeshPackage = "/Game/Models/Vehicles/Test/SM_Test" };
        var parts = new VehicleProject { ToyboxParts = [part] }; VehicleProjectService.ValidateIdentity(parts);
        Check(parts.Clone().ToyboxParts.Single().MeshPackage == part.MeshPackage, "toybox definitions survive project cloning");
        var removedParts = parts.Clone(); removedParts.DisabledParts = [part.Component, "H_Glow_01_Mesh_GEN_VARIABLE"];
        removedParts.Transforms = [new() { Component = part.Component, Z = -699.817f }, new() { Component = "seat:SeatDriver", Z = -13.478f }];
        removedParts.MaterialOverrides = [new() { Component = part.Component, MaterialPath = "/Game/Mods/Test/MI_Removed" }];
        var buildParts = VehicleToyboxService.BuildRecipe(removedParts);
        Check(buildParts.ToyboxParts.Count == 0 && buildParts.MaterialOverrides.Count == 0 && buildParts.Transforms.Single().Component == "seat:SeatDriver" && buildParts.DisabledParts.SequenceEqual(new[] { "H_Glow_01_Mesh_GEN_VARIABLE" }), "removed added parts have no build component, transform or material dependency; native removals and seat edits survive");
        Check(removedParts.ToyboxParts.Count == 1 && removedParts.MaterialOverrides.Count == 1 && removedParts.Transforms.Count == 2, "build filtering preserves the editable vehicle recipe");
        Check(VehicleToyboxService.BuildRecipe(parts).ToyboxParts.Count == 1, "active added parts remain in the build recipe");
        Check(new[] { "SM_COL_Batmobile_ArkhamKnight", "SM_Collision_Car", "SM_Car_Collision", "SM_UCX_Car" }.All(n => !VehicleToyboxService.Allowed("/Game/Models/Vehicles/Test/" + n)) && VehicleToyboxService.Allowed("/Game/Models/Vehicles/Test/SM_Headlight"), "toybox filters internal collision meshes without hiding decorative lights");
        Check(!VehicleToyboxService.Allowed("/Game/Mods/Test/SM_Test") && !VehicleToyboxService.Allowed("/Game/Models/Vehicles/../SM_Test") && !VehicleToyboxService.Allowed("/Game/Models/Vehicles/Test/SK_Test"), "toybox rejects traversal, authored packages and skeletal meshes");
        parts.ToyboxParts.Add(part); Check(Rejects(() => VehicleProjectService.ValidateIdentity(parts)), "duplicate toybox component identities are rejected");

        var legacy = JsonSerializer.Deserialize<VehiclePaletteColor>("{\"Slot\":0,\"R\":0.3,\"G\":0.2,\"B\":0.1}")!;
        Check(legacy.Finish == "Flat", "existing vehicle color recipes retain their original shader until explicitly converted");
        foreach (var finish in new[] { "Solid", "Metallic", "Transparent" })
        {
            var color = new VehiclePaletteColor { Slot = 0, R = .05f, G = .4f, B = .9f, Finish = finish };
            var loaded = JsonSerializer.Deserialize<VehiclePaletteColor>(JsonSerializer.Serialize(color))!;
            Check(loaded.Finish == finish && VehiclePaintService.Material(finish).StartsWith(VehiclePaintService.MaterialRoot),
                "native " + finish + " paint keeps its finish through save/reopen");
        }
        Check(VehiclePaintService.Material("Solid").EndsWith("_RedBrick") && !VehiclePaintService.ValidFinish("Material") &&
            Rejects(() => VehiclePaintService.Material("unknown")), "solid paint selects the native Red Brick permutation; invalid finishes are rejected");
        Check(VehiclePaintService.ExtractionFilters.All(f => GameAssetRefreshService.AllCharacterFilters.Contains(f)) &&
            GameAssetRefreshService.AllCharacterFilters.Any(f => f.Contains("T_UI_IconVeh_Batmobile1995_BatmanForever_BCA")),
            "full refresh includes the native vehicle thumbnail, paint shaders and palette");
        Check(VehiclePaintService.DefaultSlotMaterial("LEGO_Solid", "fallback") == VehiclePaintService.Material("Solid") &&
            VehiclePaintService.DefaultSlotMaterial("LEGO_Metallic", "fallback") == VehiclePaintService.Material("Metallic") &&
            VehiclePaintService.DefaultSlotMaterial("LEGO_Transparent", "fallback") == VehiclePaintService.Material("Transparent") &&
            VehiclePaintService.DefaultSlotMaterial("SomeOtherSlot", "fallback") == "fallback", "named LEGO material slots select only their matching vehicle shader");

        var data = Enumerable.Repeat((byte)0x5a, 2198).ToArray();
        VehiclePaintService.WriteUniformPalette(data, new() { R = .05f, G = .4f, B = .9f });
        Check(data.Take(0x7e).All(v => v == 0x5a) && data.Skip(0x7e + 2048).All(v => v == 0x5a), "vehicle swatch patch preserves the native texture header and trailing data");
        var expected = new[] { .05f, .4f, .9f, 1f };
        Check(Enumerable.Range(0, 256 * 4).All(i => Math.Abs((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x7e + i * 2, 2))) - expected[i % 4]) < .001f),
            "every vehicle swatch entry contains the requested linear color and opaque alpha");
        Check(Rejects(() => VehiclePaintService.WriteUniformPalette(new byte[1], new())) &&
            Rejects(() => VehiclePaintService.WriteUniformPalette(data, new() { R = float.NaN })) &&
            Rejects(() => VehiclePaintService.WriteUniformPalette(data, new() { G = -1 })) &&
            Rejects(() => VehiclePaintService.WriteUniformPalette(data, new() { B = 2 })), "invalid swatch sizes and colors fail before modifying texture data");

        var fixture = Path.Combine(Path.GetTempPath(), "Batcomputer-VehicleContent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vehicle = new VehicleProject { Id = "IconCheck", DisplayName = "Icon check" };
            var cache = Path.Combine(fixture, "ImportedIcons", "revision"); Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "source.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(cache, "icon.uasset"), [4, 5]);
            File.WriteAllBytes(Path.Combine(cache, "icon.uexp"), [6, 7]);
            vehicle.MenuIcon = new() { PackagePath = VehicleIconService.Package(vehicle), CacheRelativePath = "ImportedIcons/revision",
                SourceSha256 = SkinnedMeshCookService.Hash(Path.Combine(cache, "source.png")),
                Files = new() { [".uasset"] = SkinnedMeshCookService.Hash(Path.Combine(cache, "icon.uasset")), [".uexp"] = SkinnedMeshCookService.Hash(Path.Combine(cache, "icon.uexp")) } };
            VehicleIconService.Validate(vehicle, fixture);
            var saved = vehicle.Clone(); VehicleIconService.Validate(saved, fixture);
            Check(saved.MenuIcon?.PackagePath == vehicle.MenuIcon.PackagePath, "custom vehicle icon recipe survives save/reopen and cache validation");
            var setup = saved.Clone(); setup.Model = new() { Materials = [new() { Slot = 0 }] };
            setup.Palette = [new() { Slot = 0, Finish = "Metallic", R = .3f }];
            var scene = new VehicleWorkshopService.Scene(setup.Id, "icon-paint-session", setup.DisplayName, [], [], [], []);
            var message = JsonSerializer.Serialize(new { type = "vehicleWorkshopSave", session = scene.Session, vehicleId = setup.Id, transforms = Array.Empty<VehicleComponentTransform>() });
            var fromWorkshop = VehicleWorkshopForm.ReadPlacementMessage(setup, scene, message);
            Check(fromWorkshop.MenuIcon?.PackagePath == VehicleIconService.Package(saved) && fromWorkshop.Palette.Single().Finish == "Metallic" && fromWorkshop.Palette.Single().R == .3f,
                "returning from the 3D workshop preserves the setup thumbnail and native paint finish");
            var content = Path.Combine(fixture, "Content"); VehicleIconService.Stage(saved, fixture, content);
            Check(File.ReadAllBytes(Path.Combine(content, saved.MenuIcon!.PackagePath[6..] + ".uexp")).SequenceEqual(new byte[] { 6, 7 }), "vehicle icon stages under its private vehicle package");
            var renamed = saved.Clone(); renamed.Id = "OtherCar";
            Check(Rejects(() => VehicleIconService.Validate(renamed, fixture)), "copying an icon recipe to a different vehicle identity requires reimport");
            var bad = saved.Clone(); bad.MenuIcon!.CacheRelativePath = "../escape";
            Check(Rejects(() => VehicleIconService.Validate(bad, fixture)), "vehicle icon cache cannot escape its project directory");
            bad = saved.Clone(); bad.MenuIcon!.Files.Remove(".uexp");
            Check(Rejects(() => VehicleIconService.Validate(bad, fixture)), "incomplete vehicle icon sidecar declarations are rejected");
            File.WriteAllBytes(Path.Combine(cache, "source.png"), [9]);
            Check(Rejects(() => VehicleIconService.Validate(saved, fixture)), "changed vehicle icon source is detected before build");
            File.WriteAllBytes(Path.Combine(cache, "source.png"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(cache, "icon.uexp"), [9]);
            Check(Rejects(() => VehicleIconService.Validate(saved, fixture)), "changed cooked vehicle icon is detected before build");
            saved.MenuIcon = null; VehicleIconService.Validate(saved, fixture);
            Check(true, "restoring the donor icon does not require a custom icon cache");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
        return results;
    }
}
