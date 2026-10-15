using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class CharacterAssetReuseRegressionChecks
{
    internal static void VerifyCooked(string content, string mappingPath, bool vehicleOnly = false)
    {
        var mappings = MappingsCache.Load(mappingPath);
        var fixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories))
        {
            var asset = new UAssetAPI.UAsset(file, UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_6, mappings,
                UAssetAPI.CustomSerializationFlags.SkipParsingExports | UAssetAPI.CustomSerializationFlags.SkipPreloadDependencyLoading);
            var type = asset.Exports.FirstOrDefault(e => e.ObjectName.ToString() == Path.GetFileNameWithoutExtension(file))?.GetExportClassType()?.ToString();
            if (type is not null && CharacterAssetReuseService.SupportsSharedPayload(type)) fixtures.TryAdd(type, file);
            var ownerType = type is "DinnerVehiclePawnMetaData" or "TtGameProgressDefinitionSet" or "DinnerPawnRuntimeData" or "TtPawnUIMetaData" ? type :
                asset.Exports.Select(e => e.GetExportClassType()?.ToString()).FirstOrDefault(t => t is "BlueprintGeneratedClass" or "AnimBlueprintGeneratedClass");
            if (ownerType is not null) owners.TryAdd(ownerType, file);
        }
        if (fixtures.Count == 0) throw new InvalidDataException("No eligible cooked sharing fixtures found.");
        var root = Directory.CreateTempSubdirectory("Batcomputer-cooked-sharing-").FullName;
        try
        {
            var staged = Path.Combine(root, "Content");
            var projects = new List<NativeSuitProject>();
            var vehicles = new List<VehicleProject>();
            var prefix = vehicleOnly ? "/Game/Mods/Vehicle_Sharing" : "/Game/Mods/Sharing";
            foreach (var member in new[] { "Base", "VariantA", "VariantB" })
            {
                if (vehicleOnly) vehicles.Add(new() { Id = "Sharing" + member, DisplayName = member });
                else projects.Add(new() { SlotId = member, CustomCharacter = new() { IsDefinition = member == "Base", DefinitionSlotId = "Base" },
                    TargetPackages = new() { Playable = prefix + member + "/BP_Test" } });
                foreach (var (type, file) in fixtures.Concat(owners))
                {
                    var old = "/Game/" + Path.GetRelativePath(content, file)[..^7].Replace('\\', '/');
                    var package = prefix + member + "/" + type + "/" + Path.GetFileNameWithoutExtension(file);
                    var target = Path.Combine(staged, package[6..] + ".uasset");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var asset = new UAssetAPI.UAsset(file, UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_6, mappings,
                        UAssetAPI.CustomSerializationFlags.SkipPreloadDependencyLoading);
                    CharacterAssetReuseService.Rewrite(asset, new Dictionary<string, string> { [old] = package });
                    asset.FolderName = new UAssetAPI.UnrealTypes.FString(package);
                    // Preserve a unique opaque name-table difference in the definition.
                    if (member == "Base") asset.AddNameReference(new UAssetAPI.UnrealTypes.FString("UniqueDefinitionFixture"));
                    asset.Write(target);
                    foreach (var ext in new[] { ".ubulk", ".uptnl" })
                        if (File.Exists(Path.ChangeExtension(file, ext))) File.Copy(Path.ChangeExtension(file, ext), Path.ChangeExtension(target, ext));
                }
            }
            var saved = System.Text.Json.JsonSerializer.Serialize(projects);
            var savedVehicles = System.Text.Json.JsonSerializer.Serialize(vehicles);
            var originalObjects = projects.ToArray();
            // A same-named asset belonging to an unlisted vehicle must stay out of the sharing scope.
            var sample = fixtures.First();
            var samplePackage = prefix + "VariantB/" + sample.Key + "/" + Path.GetFileNameWithoutExtension(sample.Value);
            var unlistedPackage = "/Game/Mods/Vehicle_Unlisted/" + Path.GetFileNameWithoutExtension(sample.Value);
            var unlisted = Path.Combine(staged, unlistedPackage[6..] + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(unlisted)!);
            var sampleFile = Path.Combine(staged, samplePackage[6..] + ".uasset");
            var unlistedAsset = new UAssetAPI.UAsset(sampleFile, UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_6, mappings,
                UAssetAPI.CustomSerializationFlags.SkipPreloadDependencyLoading);
            CharacterAssetReuseService.Rewrite(unlistedAsset, new Dictionary<string, string> { [samplePackage] = unlistedPackage });
            unlistedAsset.FolderName = new UAssetAPI.UnrealTypes.FString(unlistedPackage);
            unlistedAsset.Write(unlisted);
            foreach (var ext in new[] { ".ubulk", ".uptnl" })
                if (File.Exists(Path.ChangeExtension(sampleFile, ext))) File.Copy(Path.ChangeExtension(sampleFile, ext), Path.ChangeExtension(unlisted, ext));
            var unlistedHash = SkinnedMeshCookService.Hash(unlisted);
            var result = CharacterAssetReuseService.Apply(staged, projects, mappings, Path.Combine(root, "Work"), root, Console.WriteLine, vehicles);
            if (result.Packages != fixtures.Count || result.Aliases.Any(p => !p.Key.StartsWith(prefix + "VariantB/") || !p.Value.StartsWith(prefix + "VariantA/")))
                throw new InvalidDataException("Cooked alternate-variant sharing failed.");
            if (saved != System.Text.Json.JsonSerializer.Serialize(originalObjects)) throw new InvalidDataException("Sharing mutated source authoring objects.");
            if (savedVehicles != System.Text.Json.JsonSerializer.Serialize(vehicles) || SkinnedMeshCookService.Hash(unlisted) != unlistedHash)
                throw new InvalidDataException("Sharing changed vehicle recipes or an unlisted vehicle.");
            foreach (var pair in result.Aliases)
            {
                if (File.Exists(Path.Combine(staged, pair.Key[6..] + ".uasset")) || !File.Exists(Path.Combine(staged, pair.Value[6..] + ".uasset")))
                    throw new InvalidDataException("Cooked alias did not resolve to a retained package.");
            }
            foreach (var member in new[] { "Base", "VariantA", "VariantB" })
                foreach (var (type, file) in owners)
                    if (!File.Exists(Path.Combine(staged, (prefix + member)[6..], type, Path.GetFileName(file))))
                        throw new InvalidDataException("Runtime owner package was shared: " + type);
            Console.WriteLine($"Cooked {(vehicleOnly ? "vehicle-only" : "character-family")} alternate-variant sharing: PASS (" + string.Join(", ", fixtures.Keys.Order()) + "). Unique definitions and authoring objects preserved.");
            Console.WriteLine($"Runtime identity isolation: PASS ({owners.Count} owner classes, three independent owners each).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        yield return (VerifySharedLight(), "vehicle light verification follows proven mesh aliases but still rejects incorrect socket wiring");
        yield return (new[] { "BlendSpace", "BlendSpace1D", "TTAnimSet", "TTLayerSet" }.All(CharacterAssetReuseService.SupportsSharedPayload) &&
            new[] { "BlueprintGeneratedClass", "AnimBlueprintGeneratedClass", "TtAbilitySet", "DinnerCharacterMetaData", "DinnerVehiclePawnMetaData", "TtGameProgressDefinitionSet", "TtPawnUIMetaData", "UnknownNewClass" }
            .All(t => !CharacterAssetReuseService.SupportsSharedPayload(t)), "shared payload policy covers animation data but never guesses runtime-owner or unknown-class equivalence");
        var variants = CharacterAssetReuseService.Refine([["base", "suitA", "suitB", "unique"]],
            (p, _, _) => p == "base" ? "base" : p == "unique" ? "unique" : "alternate");
        yield return (variants.Count == 1 && variants["suitB"] == "suitA",
            "asset sharing finds identical alternate suit variants even when the base differs");
        var dependent = CharacterAssetReuseService.Refine([["texBase", "texA", "texB"], ["matBase", "matA", "matB"]],
            (p, _, proposed) => p.StartsWith("tex") ? (p == "texBase" ? "base pixels" : "alternate pixels") :
                CharacterAssetReuseService.Resolve("tex" + p[3..], proposed));
        yield return (dependent.Count == 2 && dependent["texB"] == "texA" && dependent["matB"] == "matA" && !dependent.ContainsKey("matA"),
            "sharing refinement propagates different texture bindings to materials while retaining matching alternate variants");
        var cyclic = CharacterAssetReuseService.Refine([["a1", "a2"], ["b1", "b2"]],
            (p, _, proposed) => CharacterAssetReuseService.Resolve((p[0] == 'a' ? "b" : "a") + p[1..], proposed));
        yield return (cyclic.Count == 2 && cyclic["a2"] == "a1" && cyclic["b2"] == "b1",
            "equivalent dependency cycles retain flat noncyclic package aliases");
        var aliases = new Dictionary<string, string> { ["/Game/Mods/ExampleSuit/Meshes/SM_Claws"] = "/Game/Mods/ExampleHero/Meshes/SM_Claws" };
        var vehicle = new VehicleProject { Id = "Example", TwinExhausts = false, BoostTopSpeedMph = 123,
            OwnerTag = "Pawns.Playable.Example", Transforms = [new() { Component = "SeatDriver", X = 3, Yaw = 45 }],
            MaterialOverrides = [new() { Component = "body", Slot = 0, MaterialPath = "/Game/Mods/Vehicle_B/Materials/MI_Color_0" }] };
        var vehicleJson = System.Text.Json.JsonSerializer.Serialize(vehicle);
        var redirected = CharacterAssetReuseService.RedirectRecipe(vehicle, new Dictionary<string, string> {
            ["/Game/Mods/Vehicle_B/Materials/MI_Color_0"] = "/Game/Mods/Vehicle_A/Materials/MI_Color_0" });
        yield return (vehicleJson == System.Text.Json.JsonSerializer.Serialize(vehicle) && redirected.Id == vehicle.Id && redirected.OwnerTag == vehicle.OwnerTag &&
            !redirected.TwinExhausts && redirected.BoostTopSpeedMph == 123 && redirected.Transforms[0].X == 3 && redirected.Transforms[0].Yaw == 45 &&
            redirected.MaterialOverrides[0].MaterialPath == "/Game/Mods/Vehicle_A/Materials/MI_Color_0",
            "vehicle sharing redirects only disposable verification recipes and preserves saved seats, boost, exhaust and owner choices");
        string Redirect(string text) => CharacterAssetReuseService.Redirect(text, aliases);
        var declared = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/ExampleHero/Characters/BP_Playable" },
            CustomStaticMeshes = [new() { MeshPackagePath = "/Game/Mods/ExampleGeometry/Meshes/SM_Claws" }] };
        declared.ReleaseAssetAliases = aliases;
        yield return (!System.Text.Json.JsonSerializer.Serialize(declared).Contains("ReleaseAssetAliases", StringComparison.Ordinal) &&
            CharacterAssetReuseService.Resolve("/Game/Mods/ExampleSuit/Meshes/SM_Claws", aliases) == "/Game/Mods/ExampleHero/Meshes/SM_Claws",
            "shared-asset validation context resolves proven aliases without adding build state to saved authoring recipes");
        yield return (CharacterAssetReuseService.DeclaredRoots(declared).SequenceEqual(new[] { "/Game/Mods/ExampleGeometry", "/Game/Mods/ExampleHero" }),
            "shared-asset discovery includes explicitly declared geometry roots as well as the character root");
        yield return (Redirect("/Game/Mods/ExampleSuit/Meshes/SM_Claws.SM_Claws") == "/Game/Mods/ExampleHero/Meshes/SM_Claws.SM_Claws" &&
            Redirect("StaticMesh'/Game/Mods/ExampleSuit/Meshes/SM_Claws.SM_Claws'") == "StaticMesh'/Game/Mods/ExampleHero/Meshes/SM_Claws.SM_Claws'" &&
            Redirect("/Game/Mods/ExampleSuit/Meshes/SM_ClawsExtra") == "/Game/Mods/ExampleSuit/Meshes/SM_ClawsExtra" &&
            Redirect("Status.ExampleSuit.Claws") == "Status.ExampleSuit.Claws",
            "shared-asset redirects change exact package/object references, never similar names or owner-local gameplay tags");
        var folder = Directory.CreateTempSubdirectory("BatcomputerReuseChecks-").FullName;
        try
        {
            var files = new[] { "Child", "Parent", "NormalizedChild", "NormalizedParent" }.Select(n => Path.Combine(folder, n + ".uasset")).ToArray();
            foreach (var file in files) { File.WriteAllBytes(file, [1,2,3]); File.WriteAllBytes(Path.ChangeExtension(file,".uexp"), [4,5,6]); }
            bool Same() => CharacterAssetReuseService.EquivalentFamily(files[0], files[1], files[2], files[3]);
            bool matched = Same();
            File.WriteAllBytes(Path.ChangeExtension(files[0], ".ubulk"), [7]); bool missing = !Same();
            File.WriteAllBytes(Path.ChangeExtension(files[1], ".ubulk"), [8]); bool bulkChanged = !Same();
            File.WriteAllBytes(Path.ChangeExtension(files[1], ".ubulk"), [7]); bool bulkSame = Same();
            File.WriteAllBytes(Path.ChangeExtension(files[2], ".uexp"), [4,5,9]); bool payloadChanged = !Same();
            yield return (matched && missing && bulkChanged && bulkSame && payloadChanged,
                "asset sharing requires identical normalized headers, export data and every bulk sidecar; changed or missing payloads stay separate");
        }
        finally { if (FileSystemPathUtil.IsWithinDirectory(folder, Path.GetTempPath())) Directory.Delete(folder, true); }
    }

    private static bool VerifySharedLight()
    {
        var project = new VehicleProject { Id = "LightB", LightSurfaces = [new() { Role = "Headlight_L", Slot = 0 }] };
        var binding = project.LightSurfaces[0];
        var role = VehicleLightSurfaceService.Roles.Single(r => r.Id == binding.Role);
        var shared = "/Game/Mods/Vehicle_LightA/Lights/SM_Headlight_L";
        var aliases = new Dictionary<string, string> { [VehicleLightSurfaceService.MeshPackage(project, binding)] = shared };
        var asset = new UAsset { Imports = [], Exports = [] }; asset.ClearNameIndexList();
        ObjectPropertyData Ref(string name, string package, string type) => new(new FName(asset, name))
            { Value = SwordCombatService.Obj(asset, package, UnrealPathUtil.AssetName(package), "/Script/Engine", type) };
        ArrayPropertyData Materials(string package, string type) => new(new FName(asset, "OverrideMaterials")) { Value = [Ref("0", package, type)] };
        var socket = new NamePropertyData(new FName(asset, "0")) { Value = new FName(asset, VehicleLightSurfaceService.SocketName(binding)) };
        asset.Exports.Add(new NormalExport(asset, []) { ObjectName = new FName(asset, role.Component), Data = [
            Ref("StaticMesh", shared, "StaticMesh"), Materials(VehicleLightSurfaceService.BulbMaterial, "MaterialInstanceConstant"),
            new ArrayPropertyData(new FName(asset, "ComponentTags")) { Value = [socket] }] });
        asset.Exports.Add(new NormalExport(asset, []) { ObjectName = new FName(asset, "SkeletalMeshComponent"),
            Data = [Materials(VehicleLightSurfaceService.InvisibleMaterial, "Material")] });
        asset.Exports.Add(new NormalExport(asset, []) { ObjectName = new FName(asset, role.Glow),
            Data = [new ObjectPropertyData(new FName(asset, "StaticMesh")) { Value = FPackageIndex.FromRawIndex(0) }] });
        bool Rejects(Action check) { try { check(); return false; } catch (InvalidDataException) { return true; } }
        var requiresAlias = Rejects(() => VehicleLightSurfaceService.Verify(asset, project));
        VehicleLightSurfaceService.Verify(asset, project, aliases);
        socket.Value = new FName(asset, "WrongSocket");
        return requiresAlias && Rejects(() => VehicleLightSurfaceService.Verify(asset, project, aliases));
    }
}
