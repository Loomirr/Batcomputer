using System.Text.Json;

namespace Batcomputer;

internal static class VehicleRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var checks = new List<(bool, string)>();
        void Check(bool value, string text) => checks.Add((value, "vehicle: " + text));
        bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        var p = new VehicleProject { Id = "ExampleCar", DisplayName = "Example car" };
        var source = JsonSerializer.Serialize(p); var copy = p.Clone(); copy.DisplayName = "Renamed";
        Check(copy.LightSurfaces.Count == 0, "old vehicle recipes keep ordinary body surfaces by default");
        Check(VehicleSocketService.GadgetSockets.Length == 5 && VehicleSocketService.GadgetSockets.All(s => VehicleSocketService.IsEditable("socket:" + s)), "the five verified launcher / grapple references are editable");
        Check(VehicleSocketService.EffectSockets.SequenceEqual(new[] { "VFX_Exhaust_01" }) && VehicleSocketService.IsEditable("socket:VFX_Exhaust_01") && !VehicleSocketService.IsEditable("socket:VFX_Streak_01"), "boost exposes the verified shared exhaust outlet, not unverified effect points");
        copy.Transforms = [new() { Component = "socket:VFX_Exhaust_01", Z = 25, Pitch = 10 }]; VehicleProjectService.ValidateIdentity(copy);
        Check(copy.Clone().Transforms.Single().Pitch == 10, "boost outlet position and rotation survive recipe serialization");
        var exhaustMarker = VehicleSocketService.MarkerDirection("VFX_Exhaust_01");
        var nativeBodyRotation = new System.Numerics.Quaternion(0, .70710677f, 0, -.70710677f);
        var exhaustWorldDirection = System.Numerics.Vector3.Transform(new(exhaustMarker[0], exhaustMarker[1], exhaustMarker[2]), nativeBodyRotation * VehicleWorkshopService.Rotation(180, 0, -180));
        Check(exhaustWorldDirection.X < -.999f && Math.Abs(exhaustWorldDirection.Z) < .001f && VehicleSocketService.MarkerDirection("LauncherGadget_01").SequenceEqual(new float[] { 1, 0, 0 }), "exhaust marker points rearward in the native rig without changing launcher axes or saved socket rotations");
        copy.Transforms = [new() { Component = "socket:SeatDriver" }]; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "arbitrary skeleton socket edits are rejected");
        copy.Transforms = [new() { Component = "socket:Grapple_01", ScaleX = 2 }]; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "gadget sockets cannot rescale their animated launchers");
        Check(VehicleLightSurfaceService.Suggest("BC_Headlight_L") == "Headlight_L" && VehicleLightSurfaceService.Suggest("LHeadlight") == "Headlight_L" && VehicleLightSurfaceService.Suggest("RBrakelight") == "BrakeLight_R" && VehicleLightSurfaceService.Suggest("Accent_01") == "Accent_01" && VehicleLightSurfaceService.Suggest("Black") is null, "named lens slots have explicit suggestions, ordinary material names do not");
        var lens = p.Clone(); lens.Model = new() { Materials = [new() { Slot = 0, SourceMaterialName = "Headlight_L" }, new() { Slot = 1, SourceMaterialName = "BrakeLight_L" }] };
        lens.LightSurfaces = [new() { Slot = 0, Role = "Headlight_L" }];
        VehicleProjectService.ValidateIdentity(lens);
        Check(lens.Clone().LightSurfaces.Single().Role == "Headlight_L", "light assignments roundtrip as independent recipe data");
        lens.LightSurfaces.Add(new() { Slot = 1, Role = "Headlight_L" }); Check(Throws(() => VehicleProjectService.ValidateIdentity(lens)), "two body sections cannot silently compete for one lamp controller");
        lens.LightSurfaces[1].Role = "BrakeLight_L"; lens.LightSurfaces[1].Slot = 0; Check(Throws(() => VehicleProjectService.ValidateIdentity(lens)), "one body section cannot have conflicting light roles");
        lens.LightSurfaces.RemoveAt(1); lens.LightSurfaces[0].Role = "UnknownLight"; Check(Throws(() => VehicleProjectService.ValidateIdentity(lens)), "unimplemented behavior names fail closed");
        lens.LightSurfaces[0].Role = "Headlight_L"; lens.DisabledParts = ["H_LED_02_Mesh_GEN_VARIABLE"]; Check(Throws(() => VehicleProjectService.ValidateIdentity(lens)), "an assigned controller cannot also be removed"); lens.DisabledParts.Clear();
        lens.MaterialOverrides = [new() { Component = "H_LED_02_Mesh_GEN_VARIABLE", Slot = 0, MaterialPath = VehicleAssetService.PaletteTemplate }]; Check(Throws(() => VehicleProjectService.ValidateIdentity(lens)), "lamp shader overrides cannot silently disable an assigned behavior"); lens.MaterialOverrides.Clear();
        var lensScene = new VehicleWorkshopService.Scene(lens.Id, "lens-session", lens.DisplayName, [], [], [], []) { LightSurfaceChoices = [new(0, "Headlight_L", true, "", [0,0,0], "Headlight_L"), new(1, "BrakeLight_L", false, "moving bone", [0,0,0], "BrakeLight_L")] };
        string LensMessage(int slot) => JsonSerializer.Serialize(new { type = "vehicleWorkshopSave", vehicleId = lens.Id, session = "lens-session", transforms = Array.Empty<VehicleComponentTransform>(), lightSurfaces = new[] { new { slot, role = "Headlight_L" } } });
        Check(VehicleWorkshopForm.ReadPlacementMessage(lens, lensScene, LensMessage(0)).LightSurfaces.Single().Slot == 0, "bridge accepts a verified rigid lens section");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(lens, lensScene, LensMessage(1))) && Throws(() => VehicleWorkshopForm.ReadPlacementMessage(lens, lensScene, LensMessage(55))), "bridge rejects moving-bone and fabricated lens sections");
        copy = p.Clone(); copy.DisplayName = "Renamed";
        Check(JsonSerializer.Serialize(p) == source && copy.Id == p.Id && VehicleProjectService.PawnTag(copy) == VehicleProjectService.PawnTag(p), "editing a copy preserves source data and stable selection identity");
        Check(VehicleProjectService.Metadata(p).StartsWith("/Game/Mods/Vehicle_ExampleCar/", StringComparison.Ordinal) && !VehicleProjectService.Blueprint(p).Contains("Batman"), "generated packages use the vehicle identity, not donor names");
        Check(new[] { "", "../Bad", "A/B", "9Car", "Car.Variant", new string('X', 65) }.All(id => Throws(() => VehicleProjectService.RequireId(id))), "unsafe IDs are rejected before path construction");
        copy.OwnerTag = "Pawns.Playable.Batman.Suit"; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "suit-level owner tags are rejected");
        copy = p.Clone(); copy.DonorId = "invented"; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "unverified driving rigs are not silently accepted");
        copy = p.Clone(); copy.Transforms.Add(new() { Component = "Light", X = float.NaN }); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "non-finite component coordinates are rejected");
        copy = p.Clone(); copy.Transforms.Add(new() { Component = "Light", ScaleX = 0 }); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "zero component scales are rejected");
        copy = p.Clone(); copy.Transforms.AddRange([new() { Component = "Light" }, new() { Component = "Light" }]); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "duplicate component edits are rejected");
        Check(VehicleAssetService.ExtractionFilters.All(GameAssetRefreshService.AllCharacterFilters.Contains) && VehicleAssetService.ExtractionFilters.All(GameAssetRefreshService.DeveloperResearchFilters.Contains), "full and developer extraction include the verified vehicle/progression donors");
        Check(RegistryPluginService.ValidateRows([new(VehicleProjectService.Metadata(p), "PawnMetaData", "/Script/DinnerPawnMetaData.DinnerVehiclePawnMetaData", GameplayBundleAssets: [VehicleAssetService.ObjectPath(VehicleProjectService.Blueprint(p), true)]), new(VehicleProjectService.Progress(p), RegistryPluginService.ProgressDefinitionPrimaryAssetType, RegistryPluginService.ProgressDefinitionClass)]).Count == 0, "vehicle and progress registry rows use supported private roots");
        var light = new VehicleComponentTransform { Component = "B_Glow_01_Mesh_GEN_VARIABLE" };
        var readOnly = new VehicleComponentTransform { Component = "UnknownFromOlderVersion", Z = 12 };
        var scene = new VehicleWorkshopService.Scene(p.Id, "session", p.DisplayName,
            [new(light.Component, "Brake glow", "Brake lights", "mesh.glb", "B_Glow_01", "Body", true, VehicleWorkshopService.Identity, light, light, [], "")], [], [], []);
        string Message(object edits, string session = "session", string? id = null) => JsonSerializer.Serialize(new { type = "vehicleWorkshopSave", vehicleId = id ?? p.Id, session, transforms = edits });
        var changedLight = new VehicleComponentTransform { Component = light.Component, X = 20, Pitch = 25, ScaleY = .5f };
        var placed = VehicleWorkshopForm.ReadPlacementMessage(p, scene, Message(new[] { changedLight }));
        Check(placed.Transforms.Single().X == 20 && p.Transforms.Count == 0, "3D placement returns a validated copy without mutating the open vehicle");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, scene, Message(new[] { changedLight }, "stale"))) && Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, scene, Message(new[] { changedLight }, id: "AnotherCar"))), "3D placement rejects stale sessions and other vehicle IDs");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, scene, Message(new[] { new VehicleComponentTransform { Component = "body", Z = 5 } }))), "3D messages cannot reposition a read-only driving body");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, scene, Message(new[] { new VehicleComponentTransform { Component = light.Component, ScaleX = -1 } }))), "3D messages reject invalid transform limits");
        var sourceWithOld = p.Clone(); sourceWithOld.Transforms.Add(readOnly);
        Check(VehicleWorkshopForm.ReadPlacementMessage(sourceWithOld, scene, Message(new[] { readOnly })).Transforms.Single().Z == 12 && Throws(() => VehicleWorkshopForm.ReadPlacementMessage(sourceWithOld, scene, Message(Array.Empty<VehicleComponentTransform>()))), "unavailable older placements survive a workshop session and cannot be silently deleted");
        var q = VehicleWorkshopService.Rotation(0, 90, 0);
        var composed = VehicleWorkshopService.Compose(new([100, 0, 0], [q.X, q.Y, q.Z, q.W], [1, 1, 1]), new([10, 0, 0], [0, 0, 0, 1], [1, 1, 1]));
        Check(Math.Abs(composed.Position[0] - 100) < .001f && Math.Abs(composed.Position[1] - 10) < .001f, "native socket transforms compose through bone rotation in centimetres");
        var pitched = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, VehicleWorkshopService.Rotation(90, 0, 0));
        Check(pitched.Z > .999f, "Unreal positive pitch rotates forward toward positive Z");
        copy = JsonSerializer.Deserialize<VehicleProject>("{\"Id\":\"LegacyCar\",\"DisplayName\":\"Legacy car\"}")!;
        Check(copy.MaterialOverrides.Count == 0 && copy.DisabledParts.Count == 0, "existing vehicles default to native materials and enabled parts");
        copy = p.Clone(); copy.Transforms.Add(new() { Component = "seat:SeatDriver", Z = 5 }); VehicleProjectService.ValidateIdentity(copy);
        Check(copy.Clone().Transforms.Single().Z == 5, "seat offsets roundtrip without changing a shared skeleton");
        copy.Transforms[0].ScaleX = 2; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "seat scaling is rejected");
        copy = p.Clone(); copy.DisabledParts.Add("body"); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "the driving body cannot be removed");
        copy = p.Clone(); copy.MaterialOverrides.Add(new() { Component = "body", MaterialPath = "/Game/Mods/../Bad" }); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "material paths cannot escape a content package");
        var paintingScene = scene with { Parts = [scene.Parts[0] with { CanDisable = true, Materials = [new(0, VehicleAssetService.PaletteTemplate, "Surface", null, [])] }], MaterialCatalog = [new(VehicleAssetService.PaletteTemplate, "Black", "Base game", "Surface")] };
        string Painting(string type, string id = "B_Glow_01_Mesh_GEN_VARIABLE", int slot = 0, string material = VehicleAssetService.PaletteTemplate) => JsonSerializer.Serialize(new { type, vehicleId = p.Id, session = "session", transforms = Array.Empty<VehicleComponentTransform>(), materialOverrides = new[] { new VehicleMaterialOverride { Component = id, Slot = slot, MaterialPath = material } }, disabledParts = new[] { id } });
        var painted = VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopSave"));
        Check(painted.MaterialOverrides.Count == 1 && painted.DisabledParts.Count == 1 && p.MaterialOverrides.Count == 0 && p.DisabledParts.Count == 0, "material and removal edits return an isolated draft");
        Check(VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopSettings")).MaterialOverrides.Count == 1, "opening setup preserves the material and removal draft");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopSave", slot: 9))) && Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopSave", material: "/Game/Unknown/MI_Invented"))), "messages cannot assign nonexistent slots or unlisted materials");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopSave", id: "body"))), "messages cannot remove the driving body");
        Check(VehicleMaterialCatalogService.PackageFromMountedFile("LEGOBatmanLotDK/Content/Models/Vehicles/Car/MI_Paint.uasset") == "/Game/Models/Vehicles/Car/MI_Paint" && VehicleMaterialCatalogService.PackageFromMountedFile("LEGOBatmanLotDK/Plugins/TtLEGOMaterials/Content/MaterialInstances/MI_Black.uasset") == "/TtLEGOMaterials/MaterialInstances/MI_Black", "material catalog resolves game and shared plugin mounts");
        Check(VehicleMaterialCatalogService.PackageFromMountedFile("LEGOBatmanLotDK/Content/Models/Car.uexp") == "" && VehicleMaterialCatalogService.IsVehicle("/Game/Models/Vehicles/Car/MI_Paint") && VehicleMaterialCatalogService.IsVehicle("/Game/AdditionalContent/DLC/Models/Vehicles/Car/MI_Paint") && VehicleMaterialCatalogService.IsPart("/TtLEGOMaterials/MaterialInstances/MI_LEGO_Base_Transp"), "standard/DLC surfaces and shared LEGO parts have independent filters");
        Check(copy.Lights.Count == 0 && p.AccentColor is null, "old recipes retain native beams and accents until explicitly edited");
        copy = p.Clone(); copy.Lights.Add(new() { Component = "body" }); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "mesh components cannot masquerade as editable beam sources");
        copy = p.Clone(); copy.Lights.Add(new() { Component = "R_Light_01_Light_GEN_VARIABLE", Intensity = float.NaN }); Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "non-finite beam values are rejected");
        copy.Lights[0].Intensity = 128; copy.Lights[0].R = 256; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "beam colors are bounded sRGB bytes");
        copy = p.Clone(); copy.AccentColor = new() { G = -1 }; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "accent colors cannot underflow");
        var beam = VehicleLightService.Defaults("R_Light_01_Light_GEN_VARIABLE");
        var beamScene = scene with { Parts = [new(beam.Component, "Rear beam", "Rear lights", "", "R_Light_01", "Body", true, VehicleWorkshopService.Identity, new() { Component = beam.Component }, new() { Component = beam.Component }, [], "") { Light = beam }] };
        var beamMessage = JsonSerializer.Serialize(new { type = "vehicleWorkshopSave", vehicleId = p.Id, session = "session", transforms = Array.Empty<VehicleComponentTransform>(), lights = new[] { beam }, accentColor = new VehicleRgbColor { R = 40, B = 255 } });
        var beamDraft = VehicleWorkshopForm.ReadPlacementMessage(p, beamScene, beamMessage);
        Check(beamDraft.Lights.Count == 1 && beamDraft.AccentColor?.B == 255 && p.Lights.Count == 0 && p.AccentColor is null, "light and shared accent edits survive the validated bridge without changing the source");
        Check(Throws(() => VehicleWorkshopForm.ReadPlacementMessage(p, scene, beamMessage)), "the bridge rejects light sources absent from the current preview");
        Check(VehicleWorkshopForm.ReadPlacementMessage(p, paintingScene, Painting("vehicleWorkshopCopyMaterial")).MaterialOverrides.Count == 1, "material copying carries the complete isolated vehicle draft");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BatcomputerVehicleChecks-" + Guid.NewGuid().ToString("N")));
        try
        {
            var service = new VehicleProjectService(root); var mods = new ModProjectService(root);
            var path = service.Save(p); p.DisplayName = "Renamed car"; service.Save(p);
            Check(service.List().Count == 1 && service.Load(path).DisplayName == p.DisplayName && service.Load(path).Id == "ExampleCar", "renaming saves the same project without duplicating it");
            var mod = new NativeSuitModProject { ModId = "VehicleChecks", DisplayName = "Checks", Vehicles = [service.Entry(p)] };
            var modPath = mods.SaveMod(mod); mod = mods.LoadMod(modPath)!;
            Check(mod.Suits.Count == 0 && service.Enabled(mod).Single().Id == p.Id, "vehicle-only mods roundtrip without masquerading as suits");
            mod.Vehicles.Add(new() { VehicleId = "Missing", VehicleProjectPath = "missing.json", Enabled = false });
            Check(service.Enabled(mod).Count == 1, "disabled missing vehicles do not block builds");
            mod.Vehicles[1].Enabled = true; Check(Throws(() => service.Enabled(mod)), "enabled missing vehicles fail closed"); mod.Vehicles.RemoveAt(1);
            mod.Vehicles.Add(service.Entry(p)); Check(Throws(() => service.Enabled(mod)), "duplicate vehicle members are rejected"); mod.Vehicles.RemoveAt(1);
            mod.Vehicles[0].VehicleId = "Other"; Check(Throws(() => service.Enabled(mod)), "stale vehicle IDs cannot redirect a mod entry");
            var old = JsonSerializer.Deserialize<NativeSuitModProject>("{\"ModId\":\"Old\",\"Suits\":[]}")!;
            Check(old.Vehicles.Count == 0, "old suit-only project JSON retains an empty vehicle collection");
            var receipt = Path.Combine(root, "Receipt"); Directory.CreateDirectory(receipt);
            File.WriteAllText(Path.Combine(receipt, "mod.json"), "{\"suits\":[],\"vehicles\":[{}]}");
            Check(!VehicleProjectService.RuntimeSuitManifestRequired(receipt), "vehicle-only releases do not install an invalid empty-suit runtime manifest");
            File.WriteAllText(Path.Combine(receipt, "mod.json"), "{\"suits\":[{}],\"vehicles\":[{}]}");
            Check(VehicleProjectService.RuntimeSuitManifestRequired(receipt), "mixed releases retain the suit runtime manifest");
            var installedRoot = Path.Combine(root, "Installed"); var installed = Path.Combine(installedRoot, "OtherMod"); Directory.CreateDirectory(installed);
            foreach (var name in new[] { "mod.json", "vehicle-content.json" })
            {
                var file = Path.Combine(installed, name);
                File.WriteAllText(file, JsonSerializer.Serialize(new { mod_id = "OtherMod", vehicles = new[] { new { vehicle_id = p.Id, pawn_tag = VehicleProjectService.PawnTag(p), metadata = VehicleProjectService.Metadata(p) } } }));
                var result = new ModReleaseValidationService.Result(); VehicleProjectService.ValidateInstalledCollisions(mod, [p], installedRoot, result);
                Check(!result.Passed, "duplicate installed vehicle is blocked in " + name);
                mod.PreviousModIds.Add("OtherMod"); result = new(); VehicleProjectService.ValidateInstalledCollisions(mod, [p], installedRoot, result);
                Check(result.Passed, "the same vehicle can migrate from a previous mod ID in " + name); mod.PreviousModIds.Clear();
                File.Delete(file);
            }
            File.WriteAllText(Path.Combine(installed, "vehicle-content.json"), JsonSerializer.Serialize(new { mod_id = mod.ModId, vehicles = new[] { new { vehicle_id = p.Id } } }));
            var own = new ModReleaseValidationService.Result(); VehicleProjectService.ValidateInstalledCollisions(mod, [p], installedRoot, own);
            Check(own.Passed, "rebuilding the owning release does not collide with itself");
        }
        finally { if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("BatcomputerVehicleChecks-", StringComparison.Ordinal) && FileSystemPathUtil.IsWithinDirectory(root, Path.GetTempPath())) Directory.Delete(root, true); }
        return checks;
    }
}
