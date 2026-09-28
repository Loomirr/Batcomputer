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
        var repairFixture = Path.Combine(Path.GetTempPath(), "BatcomputerVehicleRepair-" + Guid.NewGuid().ToString("N"));
        try
        {
            var projects = new VehicleProjectService(repairFixture);
            var legacy = p.Clone();
            legacy.Model = new SkinnedMeshImport { SourceRelativePath = "ImportedSkinnedMeshes/old/source.fbx",
                CacheRelativePath = "ImportedSkinnedMeshes/old", SourceSha256 = new string('A', 64) };
            var projectFile = projects.Save(legacy);
            var cache = SkinnedMeshCookService.SafePath(projects.DirectoryFor(legacy), legacy.Model.CacheRelativePath);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "validated.json"), JsonSerializer.Serialize(new SkinnedMeshCookService.CookManifest(
                legacy.Model.SourceSha256, "", "", "", "", [], [], 0)));
            Check(VehicleLegacyMeshRepairService.Pending(projects.DirectoryFor(legacy), legacy).SequenceEqual(["body"]),
                "old body cooks are identified before preview");
            var before = File.ReadAllBytes(projectFile);
            Check(Throws(() => VehicleLegacyMeshRepairService.RepairAsync(projects, legacy, _ => { }).GetAwaiter().GetResult()) &&
                File.ReadAllBytes(projectFile).SequenceEqual(before),
                "a missing saved FBX fails without modifying the vehicle project");
            File.WriteAllText(Path.Combine(cache, "validated.json"), JsonSerializer.Serialize(new SkinnedMeshCookService.CookManifest(
                legacy.Model.SourceSha256, "", "", "", "", [], [], SkinnedMeshCookService.RigValidationVersion)));
            Check(VehicleLegacyMeshRepairService.Pending(projects.DirectoryFor(legacy), legacy).Count == 0,
                "current validated cooks do not prompt a rebuild");
        }
        finally
        {
            if (Directory.Exists(repairFixture) && FileSystemPathUtil.IsWithinDirectory(repairFixture, Path.GetTempPath()))
                Directory.Delete(repairFixture, true);
        }
        Check(VehicleSoundService.Styles.Count == 19 && VehicleSoundService.Styles.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == 19,
            "nineteen distinct native vehicle sound styles are available");
        Check(VehicleSoundPreviewService.EventId("Play_Veh_Engine_Player_Tumbler_Lp") == 0x6062E56F &&
            VehicleSoundPreviewService.EventId("Play_Veh_Lawnmower_Engine_Lp") == 0x6B017336 &&
            VehicleSoundPreviewService.EventId("PLAY_VEH_LAWNMOWER_ENGINE_LP") == VehicleSoundPreviewService.EventId("play_veh_lawnmower_engine_lp"),
            "Wwise event hashing matches native vehicle banks regardless of case");
        Check(VehicleSoundPreviewService.Wav(null!, "../escape", Path.GetTempPath()).Wav is null,
            "sound preview refuses path-like event names before accessing its cache");
        if (Directory.Exists(AppSettings.Current.EffectiveGamePaksRoot()) && File.Exists(AppSettings.Current.EffectiveUsmapPath()))
        {
            using var audioProvider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
            Check(VehicleSoundPreviewService.Sources(audioProvider, "Play_Veh_Engine_Player_Tumbler_Lp").Count > 0,
                "sound preview locates the Tumbler engine media in native Wwise banks");
        }
        var soundRecipe = p.Clone(); soundRecipe.SoundStyle = "batmobile2005";
        VehicleProjectService.ValidateIdentity(soundRecipe);
        Check(VehicleSoundService.Applies(soundRecipe) && !VehicleSoundService.Applies(p) && p.SoundStyle == "",
            "sound swaps are opt-in and old projects keep donor audio");
        var boostRecipe = p.Clone(); boostRecipe.BoostStyle = "bvs-twin"; boostRecipe.TwinExhausts = true;
        VehicleProjectService.ValidateIdentity(boostRecipe);
        Check(VehicleExhaustService.Styles.Count == 10 && VehicleExhaustService.PrivateBoost(boostRecipe) && !VehicleExhaustService.PrivateBoost(p) &&
            boostRecipe.Clone().TwinExhausts && boostRecipe.Clone().BoostStyle == "bvs-twin",
            "ten native boost styles and twin-outlet settings are opt-in and roundtrip");
        Check(VehicleWorkshopService.Boost(boostRecipe) is { Twin: true, Colour: "#ff8a2a" } preview &&
            preview.Outlets.SequenceEqual(["VFX_Exhaust_01", "VFX_Exhaust_02"]),
            "workshop boost preview follows the selected style and both editable outlets");
        boostRecipe.TwinExhausts = false;
        Check(Throws(() => VehicleProjectService.ValidateIdentity(boostRecipe)), "a twin boost style cannot lose its second outlet");
        boostRecipe.TwinExhausts = true; boostRecipe.BoostColor = new() { R = 40, G = 80, B = 255 };
        Check(Throws(() => VehicleProjectService.ValidateIdentity(boostRecipe)), "separate boost-style and recolor chains cannot collide");
        boostRecipe.BoostColor = null; boostRecipe.BoostStyle = "unknown";
        Check(Throws(() => VehicleProjectService.ValidateIdentity(boostRecipe)), "unknown boost styles fail before staging");
        boostRecipe.BoostStyle = "bvs-twin"; boostRecipe.BoostTopSpeedMph = 145; boostRecipe.BoostAccelerationGs = 8;
        VehicleProjectService.ValidateIdentity(boostRecipe);
        Check(boostRecipe.Clone().BoostTopSpeedMph == 145 && boostRecipe.Clone().BoostAccelerationGs == 8,
            "boost speed and acceleration settings survive recipe serialization");
        boostRecipe.BoostTopSpeedMph = float.NaN;
        Check(Throws(() => VehicleProjectService.ValidateIdentity(boostRecipe)), "non-finite boost speed fails before staging");
        boostRecipe.BoostTopSpeedMph = 145;
        soundRecipe.SoundStyle = "not-a-sound-style";
        Check(Throws(() => VehicleProjectService.ValidateIdentity(soundRecipe)), "unknown sound styles fail before staging");
        Check(VehicleLightService.IsLampMesh("H_LED_02_Mesh_GEN_VARIABLE") &&
            VehicleLightService.IsLampMesh("B_Glow_01_Mesh_GEN_VARIABLE") &&
            !VehicleLightService.IsLampMesh("H_Light_01_Light_GEN_VARIABLE") &&
            !VehicleLightService.IsLampMesh("H_LED_02_Mesh_GEN_VARIABLE_Extra"),
            "only decorative lamp mesh components get the new default");
        var newWithoutExtraction = VehicleProjectService.CreateNew(Path.Combine(Path.GetTempPath(), "MissingVehicleExtraction-" + Guid.NewGuid().ToString("N")));
        Check(newWithoutExtraction.DisabledParts.Count == 0 && p.DisabledParts.Count == 0,
            "missing donor extraction does not block creation or change existing vehicle recipes");
        var tagFixture = Path.Combine(Path.GetTempPath(), "BatcomputerBaseTag-" + Guid.NewGuid().ToString("N") + ".fbx");
        try
        {
            File.WriteAllText(tagFixture, "; FBX 7.4.0 project file\nP: \"BatcomputerBase\", \"KString\", \"\", \"\", \"batmobile1995\"\n");
            Check(FbxBaseTag.Read(tagFixture) == "batmobile1995", "ASCII FBX driving-base tag is read without Unreal");
            File.WriteAllText(tagFixture, "; FBX 7.4.0 project file\nP: \"OtherProperty\", \"KString\", \"\", \"\", \"batmobile1995\"\n");
            Check(FbxBaseTag.Read(tagFixture) is null, "untagged FBX preserves the existing import path");
            File.WriteAllText(tagFixture, "; FBX 7.4.0 project file\nP: \"BatcomputerBase\", \"KString\", \"\", \"\", \"batmobile1995\"\nP: \"BatcomputerBase\", \"KString\", \"\", \"\", \"batmobile1989\"\n");
            Check(Throws(() => FbxBaseTag.Read(tagFixture)), "conflicting FBX driving-base tags are rejected");
        }
        finally { File.Delete(tagFixture); }
        var activeVehicleContent = AppSettings.Current.EffectiveExtractedContentRoot();
        if (VehicleDonorService.PackageComplete(activeVehicleContent, VehicleAssetService.NativeBlueprint))
        {
            var defaultLamps = VehicleLightService.LampMeshes(VehicleAssetService.Read(activeVehicleContent, VehicleAssetService.NativeBlueprint));
            var freshVehicle = VehicleProjectService.CreateNew(activeVehicleContent);
            Check(defaultLamps.Count > 0 && freshVehicle.DisabledParts.ToHashSet(StringComparer.Ordinal).SetEquals(defaultLamps) &&
                VehicleLightService.HasOnlyDefaultDisabledLamps(freshVehicle, activeVehicleContent),
                "new vehicles hide exactly the lamp meshes found in their native blueprint");
            soundRecipe.SoundStyle = "batmobile2005";
            var soundBlueprint = VehicleAssetService.Read(activeVehicleContent, VehicleAssetService.NativeBlueprint);
            VehicleSoundService.Apply(soundBlueprint, soundRecipe);
            VehicleSoundService.Verify(soundBlueprint, soundRecipe);
            Check(VehicleSoundService.DonorSlots(soundBlueprint).Any(slot => slot.Label == "Engine" && slot.Event == VehicleSoundService.FindStyle("batmobile2005")!.Engine),
                "Tumbler engine and matching audio slots apply to the Forever blueprint");
            foreach (var donorId in new[] { "batmobile1989", "batmobile2005" })
            {
                var another = p.Clone(); another.DonorId = donorId; another.TwinExhausts = true;
                var baseCar = VehicleDonorService.Get(another);
                if (!VehicleDonorService.PackageComplete(activeVehicleContent, baseCar.Mesh) || !VehicleDonorService.PackageComplete(activeVehicleContent, baseCar.Skeleton)) continue;
                var mesh = VehicleAssetService.Read(activeVehicleContent, baseCar.Mesh);
                VehicleSocketService.Apply(mesh, VehicleAssetService.Read(activeVehicleContent, baseCar.Skeleton), another);
                Check(VehicleSocketService.Socket(mesh, VehicleExhaustService.SecondSocket) is not null,
                    donorId + " gets a second outlet without assuming a Grapple socket");
            }
            var donor = VehicleDonorService.Get(boostRecipe);
            var requiredBoost = donor.BoostPackages.Append(VehicleExhaustService.FindStyle(boostRecipe.BoostStyle)!.BoostBlueprint).Concat(VehicleBoostSpeedService.Packages);
            if (requiredBoost.All(package => VehicleDonorService.PackageComplete(activeVehicleContent, package)))
            {
                var socketMesh = VehicleAssetService.Read(activeVehicleContent, donor.Mesh);
                var socketSkeleton = VehicleAssetService.Read(activeVehicleContent, donor.Skeleton);
                VehicleSocketService.Apply(socketMesh, socketSkeleton, boostRecipe);
                Check(VehicleSocketService.Socket(socketMesh, VehicleExhaustService.SecondSocket) is not null,
                    "the second outlet is created on a private mesh without changing the donor skeleton");
                var baseTwin = p.Clone(); baseTwin.TwinExhausts = true;
                var speedOnly = p.Clone(); speedOnly.BoostTopSpeedMph = 135;
                var styleRecipes = VehicleExhaustService.Styles.Where(style => VehicleDonorService.PackageComplete(activeVehicleContent, style.BoostBlueprint))
                    .Select(style => { var recipe = p.Clone(); recipe.BoostStyle = style.Id; recipe.TwinExhausts = style.Twin; return recipe; });
                foreach (var recipe in styleRecipes.Concat([boostRecipe, baseTwin, speedOnly]))
                {
                    var temp = Path.Combine(Path.GetTempPath(), "BatcomputerBoostStage-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    try
                    {
                        void Save(UAssetAPI.UAsset asset, string package)
                        {
                            var file = SkinnedMeshCookService.SafePath(temp, package[6..] + ".uasset");
                            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                            asset.FolderName = new UAssetAPI.UnrealTypes.FString(package);
                            asset.Write(file);
                        }
                        VehicleExhaustService.Stage(recipe, donor, activeVehicleContent, Save, _ => { });
                        var bp = VehicleAssetService.Read(activeVehicleContent, donor.Blueprint);
                        CustomEquipmentService.Rename(bp, VehicleExhaustService.Redirects(recipe, donor));
                        VehicleExhaustService.ApplyEngineEffects(bp, recipe);
                        Save(bp, VehicleProjectService.Blueprint(recipe));
                        VehicleExhaustService.Verify(recipe, donor, temp, VehicleAssetService.Read(temp, VehicleProjectService.Blueprint(recipe)));
                        Check(true, "Forever " + (recipe == baseTwin ? "native twin" : recipe == speedOnly ? "speed-only" : recipe.BoostStyle) +
                            " boost survives staged package roundtrips");
                    }
                    finally { Directory.Delete(temp, true); }
                }
            }
            foreach (var (donorId, styleId) in new[] { ("batmobile1989", "charger"), ("batmobile2005", "forever") })
            {
                var alternate = p.Clone(); alternate.DonorId = donorId; alternate.BoostStyle = styleId; alternate.TwinExhausts = true;
                var driving = VehicleDonorService.Get(alternate);
                var style = VehicleExhaustService.FindStyle(styleId)!;
                if (!driving.BoostPackages.Append(style.BoostBlueprint).All(package => VehicleDonorService.PackageComplete(activeVehicleContent, package))) continue;
                var stage = Path.Combine(Path.GetTempPath(), "BatcomputerBoostDonor-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    void Save(UAssetAPI.UAsset asset, string package)
                    {
                        var file = SkinnedMeshCookService.SafePath(stage, package[6..] + ".uasset");
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        asset.FolderName = new UAssetAPI.UnrealTypes.FString(package);
                        asset.Write(file);
                    }
                    VehicleExhaustService.Stage(alternate, driving, activeVehicleContent, Save, _ => { });
                    var gameplay = VehicleAssetService.Read(activeVehicleContent, driving.Blueprint);
                    CustomEquipmentService.Rename(gameplay, VehicleExhaustService.Redirects(alternate, driving));
                    VehicleExhaustService.ApplyEngineEffects(gameplay, alternate);
                    Save(gameplay, VehicleProjectService.Blueprint(alternate));
                    VehicleExhaustService.Verify(alternate, driving, stage, VehicleAssetService.Read(stage, VehicleProjectService.Blueprint(alternate)));
                    Check(true, donorId + " uses " + driving.ExhaustSocket + " with " + styleId + " effects in staged assets");
                }
                finally { Directory.Delete(stage, true); }
            }
        }
        var source = JsonSerializer.Serialize(p); var copy = p.Clone(); copy.DisplayName = "Renamed";
        Check(copy.LightSurfaces.Count == 0, "old vehicle recipes keep ordinary body surfaces by default");
        Check(copy.BoostColor is null, "old vehicle recipes keep the native boost flame by default");
        copy.BoostColor = new() { R = 38, G = 130, B = 255 };
        Check(copy.Clone().BoostColor?.B == 255 && p.BoostColor is null, "boost color roundtrips without changing the source vehicle");
        copy.BoostColor.R = -1; Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "invalid boost RGB values fail closed");
        copy.BoostColor.R = 38; copy.DonorId = "batmobile1997";
        Check(Throws(() => VehicleProjectService.ValidateIdentity(copy)), "unverified driving bases cannot silently request a boost recolor");
        copy = p.Clone();
        Check(VehicleBoostColorService.Private(copy, VehicleBoostColorService.NativePawnData).StartsWith(VehicleProjectService.ContentRoot(copy) + "/", StringComparison.Ordinal) &&
            VehicleBoostColorService.Packages.All(package => VehicleBoostColorService.Private(copy, package).StartsWith(VehicleProjectService.ContentRoot(copy) + "/", StringComparison.Ordinal)),
            "boost-color assets are private to the vehicle, never native package overrides");
        Check(VehicleSocketService.GadgetSockets.Length == 5 && VehicleSocketService.GadgetSockets.All(s => VehicleSocketService.IsEditable("socket:" + s)), "the five verified launcher / grapple references are editable");
        Check(VehicleSocketService.EffectSockets.SequenceEqual(new[] { "VFX_Exhaust_01", "VFX_ExhaustBoost_01", "VFX_Exhaust_02" }) && VehicleSocketService.IsEditable("socket:VFX_Exhaust_01") && !VehicleSocketService.IsEditable("socket:VFX_Streak_01"), "boost exposes native and second exhaust outlets, not unrelated effect points");
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
        var lampPart = new VehicleWorkshopService.Part("H_LED_02_Mesh_GEN_VARIABLE", "Headlight LED", "Lights", "", "", "Body", true,
            VehicleWorkshopService.Identity, new() { Component = "H_LED_02_Mesh_GEN_VARIABLE" }, new() { Component = "H_LED_02_Mesh_GEN_VARIABLE" }, [], "") { CanDisable = true };
        var assignedLampScene = lensScene with { Parts = [lampPart] };
        var assignedLampMessage = JsonSerializer.Serialize(new { type = "vehicleWorkshopSave", vehicleId = lens.Id, session = "lens-session",
            transforms = Array.Empty<VehicleComponentTransform>(), disabledParts = new[] { lampPart.Id }, lightSurfaces = new[] { new { slot = 0, role = "Headlight_L" } } });
        Check(!VehicleWorkshopForm.ReadPlacementMessage(lens, assignedLampScene, assignedLampMessage).DisabledParts.Contains(lampPart.Id),
            "assigning a lens role restores its required native LED mesh");
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
