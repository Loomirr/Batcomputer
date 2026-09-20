using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>The first verified driving rig. Summon assembly and socket authoring are separate contracts.</summary>
internal static class VehicleAssetService
{
    internal const string DonorId = "batmobile1995";
    internal const string NativeRoot = "/Game/Models/Vehicles/VEH_Batmobile1995_BatmanForever";
    internal const string NativeMesh = NativeRoot + "/SK_VEH_Batmobile1995_BatmanForever";
    internal const string NativeBlueprint = NativeRoot + "/BP_VEH_Batmobile1995_BatmanForever";
    internal const string NativePhysics = NativeRoot + "/PHYS_VEH_Batmobile1995_BatmanForever";
    internal const string NativeSkeleton = NativeRoot + "/SKEL_VEH_Batmobile1995_BatmanForever";
    internal const string NativeMetadata = "/Game/Vehicles/DA_Vehicle_Batmobile1995_BatmanForever";
    internal const string NativeUi = "/Game/Vehicles/DA_UI_Batmobile1995_BatmanForever";
    internal const string NativeMenu = "/Game/Vehicles/MenuActors/BP_MenuActor_Batmobile1995_BatmanForever";
    internal const string NativePlinth = "/Game/LEGOGameplay/Mechanics/Batcave/VehiclePurchase/VehiclePlinthActors/Batman/BP_VehiclePlinth_Batmobile_BatmanForever";
    internal const string NativeProgress = "/Game/GameProgress/PROG_Vehicles";
    internal const string PaletteTemplate = "/Game/Characters/Attachments/Hair/MI_Black";
    internal const string Warning = "Experimental vehicle editor. Seat offsets need in-game testing; collision and handling stay native.";
    internal static readonly string[] RequiredPackages = [NativeBlueprint, NativeMesh, NativePhysics, NativeSkeleton, NativeMetadata, NativeUi, NativeMenu, NativePlinth, NativeProgress];
    internal static IEnumerable<string> ExtractionFilters => VehicleDonorService.ExtractionFilters.Concat(VehiclePaintService.ExtractionFilters);
    internal static readonly string[] NativeOwners = ["Pawns.Playable.Batman", "Pawns.Playable.BatGirl", "Pawns.Playable.CatWoman", "Pawns.Playable.Gordon", "Pawns.Playable.Nightwing", "Pawns.Playable.RobinDickGrayson", "Pawns.Playable.TaliaAlGhul", "Pawns.Playable.PoisonIvy"];
    internal sealed record Component(string Name, string Kind, string Attachment, VehicleComponentTransform Transform)
    { public override string ToString() => Name.Replace("_GEN_VARIABLE", ""); }
    internal static Usmap Maps => VehicleLightService.Maps;
    internal static UAsset Read(string root, string package) => EquipmentAssetService.ReadRaw(root, package, Maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static string ObjectPath(string package, bool generated = false) => package + "." + UnrealPathUtil.AssetName(package) + (generated ? "_C" : "");
    internal static void Validate(VehicleProject p, string directory, string nativeContent)
    {
        VehicleProjectService.ValidateIdentity(p);
        var donor = VehicleDonorService.Get(p);
        var NativeMesh = donor.Mesh; var NativeSkeleton = donor.Skeleton; var NativeBlueprint = donor.Blueprint;
        VehicleIconService.Validate(p, directory);
        var missing = donor.MissingPackages(nativeContent);
        Require(missing.Length == 0, donor.UnavailableMessage + "\n" + string.Join("\n", missing));
        if (p.Model is { } model)
        {
            Require(model.DonorMeshPackage == NativeMesh && model.SkeletonPackage == NativeSkeleton && model.Component == "SkeletalMeshComponent" && model.MeshPackage == VehicleProjectService.Mesh(p), "Vehicle model identity or donor rig changed. Reimport using the vehicle workshop.");
            SkinnedMeshStageService.ValidateRecipe(model); SkinnedMeshStageService.ReadManifest(directory, model);
            Require(model.HiddenComponents.Count == 0, "Vehicle body imports cannot hide driving components.");
        }
        ValidateSummon(p, directory, nativeContent);
        var components = Components(nativeContent, p).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var t in p.Transforms) Require(components.Contains(t.Component) || VehicleSocketService.IsEditable(t.Component) || p.ToyboxParts.Any(a => a.Added && a.Component == t.Component), "Not an editable vehicle component: " + t.Component);
        var staticParts = Read(nativeContent, NativeBlueprint).Exports.OfType<NormalExport>().Where(e => e.GetExportClassType()?.ToString() == "StaticMeshComponent").Select(e => e.ObjectName.ToString()).ToHashSet(StringComparer.Ordinal);
        VehicleToyboxService.ValidateAssets(p, staticParts);
        staticParts.UnionWith(p.ToyboxParts.Where(t => t.Added).Select(t => t.Component));
        foreach (var id in p.DisabledParts) Require(staticParts.Contains(id), "Only decorative static-mesh parts can be disabled: " + id);
        var bodySlots = p.Model?.Materials.Count ?? (p.MaterialOverrides.Any(m => m.Component == "body") ? NativeMaterialCount(NativeMesh) : 0);
        foreach (var edit in p.MaterialOverrides) Require(edit.Component == "body" ? edit.Slot < bodySlots : staticParts.Contains(edit.Component) && (edit.Slot == 0 || p.ToyboxParts.Any(t => t.Component == edit.Component)), "Invalid vehicle material target: " + edit.Component);
    }
    private static int NativeMaterialCount(string mesh)
    {
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        return ModelPreviewService.MeshSlotMaterials(provider.LoadPackageObject<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(mesh)).Count;
    }
    internal static IReadOnlyList<Component> Components(string nativeContent, VehicleProject? project = null)
    {
        var bp = Read(nativeContent, VehicleDonorService.Get(project ?? new()).Blueprint);
        return bp.Exports.OfType<NormalExport>().Where(IsDecorative).Select(e => new Component(e.ObjectName.ToString(), e.GetExportClassType()!.ToString(),
            e.Data.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "AttachSocketName")?.Value.ToString() ?? "parent component", ReadTransform(e))).Concat(VehicleCustomizationService.SeatComponents(bp)).OrderBy(c => c.Name).ToArray();
    }
    private static bool IsDecorative(NormalExport e) => e.GetExportClassType()?.ToString() is "StaticMeshComponent" or "ChildActorComponent" or "PointLightComponent" or "SpotLightComponent" || VehicleLightService.IsSource(e.ObjectName.ToString()) && VehicleLightService.IsClass(e.GetExportClassType()?.ToString());
    private static VehicleComponentTransform ReadTransform(NormalExport e)
    {
        var loc = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeLocation")?.Value.OfType<VectorPropertyData>().SingleOrDefault()?.Value;
        var rot = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeRotation")?.Value.OfType<RotatorPropertyData>().SingleOrDefault()?.Value;
        var scale = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeScale3D")?.Value.OfType<VectorPropertyData>().SingleOrDefault()?.Value;
        return new() { Component = e.ObjectName.ToString(), X = (float)(loc?.X ?? 0), Y = (float)(loc?.Y ?? 0), Z = (float)(loc?.Z ?? 0),
            Pitch = (float)(rot?.Pitch ?? 0), Yaw = (float)(rot?.Yaw ?? 0), Roll = (float)(rot?.Roll ?? 0), ScaleX = (float)(scale?.X ?? 1), ScaleY = (float)(scale?.Y ?? 1), ScaleZ = (float)(scale?.Z ?? 1) };
    }
    private static StructPropertyData Struct(UAsset a, string name, string type, PropertyData value) => new(new FName(a, name)) { StructType = new FName(a, type), Value = [value] };
    internal static void ApplyTransforms(UAsset a, IEnumerable<VehicleComponentTransform> transforms)
    {
        foreach (var t in transforms)
        {
            if (VehicleSocketService.IsEditable(t.Component)) continue; // Authored on this vehicle's mesh, not a BP component.
            if (VehicleCustomizationService.IsSeat(t.Component)) { VehicleCustomizationService.ApplySeat(a, t); continue; }
            var e = a.Exports.OfType<NormalExport>().SingleOrDefault(e => e.ObjectName.ToString() == t.Component && IsDecorative(e));
            Require(e is not null, "Vehicle attachment disappeared: " + t.Component);
            e!.Data.RemoveAll(p => p.Name.ToString() is "RelativeLocation" or "RelativeRotation" or "RelativeScale3D");
            e.Data.Add(Struct(a, "RelativeLocation", "Vector", new VectorPropertyData(new FName(a, "RelativeLocation")) { Value = new FVector(t.X, t.Y, t.Z) }));
            e.Data.Add(Struct(a, "RelativeRotation", "Rotator", new RotatorPropertyData(new FName(a, "RelativeRotation")) { Value = new FRotator(t.Pitch, t.Yaw, t.Roll) }));
            e.Data.Add(Struct(a, "RelativeScale3D", "Vector", new VectorPropertyData(new FName(a, "RelativeScale3D")) { Value = new FVector(t.ScaleX, t.ScaleY, t.ScaleZ) }));
        }
    }
    internal static IReadOnlyList<PawnTagConfigService.TagRow> Tags(VehicleProject p) => [new(VehicleProjectService.PawnTag(p), p.DisplayName), new(VehicleProjectService.ProgressTag(p), "Vehicle acquired by default")];
    internal static Dictionary<string, string> Text(VehicleProject p) => new() { ["Vehicle." + p.Id + ".Name"] = p.DisplayName, ["Vehicle." + p.Id + ".Owner"] = p.OwnerTag.Split('.').Last(), ["Vehicle." + p.Id + ".Description"] = p.DisplayName, ["Vehicle." + p.Id + ".Locked"] = "Available by default" };
    internal static IReadOnlyList<RegistryPluginService.RegistryRow> Stage(VehicleProject p, string directory, string nativeContent, string content, string modId, string projectRoot, Action<string> log)
    {
        Validate(p, directory, nativeContent);
        p = VehicleToyboxService.BuildRecipe(p);
        var donor = VehicleDonorService.Get(p);
        var NativeMesh = donor.Mesh; var NativeSkeleton = donor.Skeleton; var NativeBlueprint = donor.Blueprint; var NativePhysics = donor.Physics;
        var NativeMetadata = donor.Metadata; var NativeUi = donor.Ui; var NativeMenu = donor.Menu; var NativePlinth = donor.Plinth;
        VehicleIconService.Stage(p, directory, content);
        var root = VehicleProjectService.ContentRoot(p);
        var ui = root + "/UI/DA_UI_" + p.Id; var menu = root + "/UI/BP_Menu_" + p.Id; var plinth = root + "/UI/BP_Plinth_" + p.Id;
        void Save(UAsset a, string package)
        {
            Require(package.StartsWith(root + "/", StringComparison.Ordinal) || package == VehicleProjectService.Progress(p), "Vehicle stage cannot replace native assets.");
            var file = SkinnedMeshCookService.SafePath(content, package[6..] + ".uasset"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Require(!File.Exists(file), "Another mod member already owns this vehicle package: " + package);
            a.FolderName = new FString(package); a.Write(file);
            Require(Read(content, package).Exports.Count == a.Exports.Count, "Vehicle export roundtrip failed: " + package);
        }
        var model = p.Model?.Clone();
        if (model is not null)
        {
            foreach (var color in p.Palette)
            {
                if (color.Finish != "Flat")
                {
                    VehiclePaintService.Stage(nativeContent, p, color, Save);
                    model.Materials[color.Slot].MaterialPath = root + "/Materials/MI_Color_" + color.Slot;
                    continue;
                }
                var material = Read(nativeContent, PaletteTemplate);
                var values = material.Exports.OfType<NormalExport>().SelectMany(e => EquipmentAssetService.Properties(e.Data))
                    .Where(p => p.Path.Contains("VectorParameterValues", StringComparison.Ordinal)).Select(p => p.Property).OfType<LinearColorPropertyData>().ToArray();
                Require(values.Length == 1, "The simple vehicle color template changed.");
                values[0].Value = new FLinearColor(color.R, color.G, color.B, 1);
                var package = root + "/Materials/MI_Color_" + color.Slot;
                CustomEquipmentService.Rename(material, new Dictionary<string, string> { [PaletteTemplate] = package }); Save(material, package);
                model.Materials[color.Slot].MaterialPath = package;
            }
            // A surface chosen from the workshop library must also reach the summon mesh.
            // Apply it to the staged recipe before matching the assembly's slot names below.
            foreach (var edit in p.MaterialOverrides.Where(m => m.Component == "body"))
                model.Materials[edit.Slot].MaterialPath = edit.MaterialPath;
            var library = new ToolMaterialLibraryService(projectRoot);
            foreach (var material in model.Materials.Select(m => m.MaterialPath).Distinct(StringComparer.OrdinalIgnoreCase))
                if (material.Contains("/Mods/", StringComparison.OrdinalIgnoreCase) && !File.Exists(SkinnedMeshCookService.SafePath(content, material[6..] + ".uasset")))
                    library.CopyMaterialClosureToContentRoot(material, content);
            Require(!File.Exists(SkinnedMeshCookService.SafePath(content, model.MeshPackage[6..] + ".uasset")), "Another mod member already owns this vehicle mesh: " + model.MeshPackage);
            SkinnedMeshStageService.BakeMesh(content, directory, model);
        }
        var summon = p.SummonModel?.Clone();
        if (summon is not null)
        {
            // Match by original material name, never by slot order (assembly meshes can reorder slots).
            if (model is not null && p.MatchBuildUpToBody)
                foreach (var slot in summon.Materials)
                {
                    var bodySlot = model.Materials.SingleOrDefault(m => m.SourceMaterialName == slot.SourceMaterialName);
                    if (bodySlot is not null) slot.MaterialPath = bodySlot.MaterialPath;
                }
            var library = new ToolMaterialLibraryService(projectRoot);
            foreach (var material in summon.Materials.Select(m => m.MaterialPath).Distinct(StringComparer.OrdinalIgnoreCase))
                if (material.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) && !File.Exists(SkinnedMeshCookService.SafePath(content, material[6..] + ".uasset")))
                    library.CopyMaterialClosureToContentRoot(material, content);
            SkinnedMeshStageService.BakeMesh(content, directory, summon);
        }
        var meshPackage = model?.MeshPackage;
        if (p.Transforms.Any(t => VehicleSocketService.IsEditable(t.Component)) || p.LightSurfaces.Count > 0)
        {
            meshPackage ??= VehicleProjectService.Mesh(p);
            var mesh = Read(model is null ? nativeContent : content, model is null ? NativeMesh : meshPackage);
            if (model is null) CustomEquipmentService.Rename(mesh, new Dictionary<string, string> { [NativeMesh] = meshPackage });
            var skeleton = Read(nativeContent, NativeSkeleton);
            VehicleSocketService.Apply(mesh, skeleton, p);
            VehicleLightSurfaceService.StageGeometry(p, nativeContent, content, mesh, skeleton, log);
            var file = SkinnedMeshCookService.SafePath(content, meshPackage[6..] + ".uasset"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (model is null)
            {
                Require(!File.Exists(file), "Another mod member already owns this vehicle mesh: " + meshPackage);
                var bulk = Path.ChangeExtension(ExtractedPackagePathService.ResolvePackageUasset(nativeContent, NativeMesh)!, ".ubulk");
                if (File.Exists(bulk)) File.Copy(bulk, Path.ChangeExtension(file, ".ubulk"), false);
            }
            mesh.FolderName = new FString(meshPackage); mesh.Write(file);
            VehicleSocketService.Verify(Read(content, meshPackage), p);
            log("Vehicle launcher / light / exhaust sockets staged on a private mesh.");
        }
        var redirects = new Dictionary<string, string> { [NativeBlueprint] = VehicleProjectService.Blueprint(p), [NativeMetadata] = VehicleProjectService.Metadata(p), [NativeUi] = ui, [NativeMenu] = menu, [NativePlinth] = plinth, [NativeProgress] = VehicleProjectService.Progress(p) };
        if (meshPackage is not null) redirects[NativeMesh] = meshPackage;
        if (summon is not null) redirects[summon.DonorMeshPackage] = summon.MeshPackage;
        foreach (var source in new[] { NativeBlueprint, NativeMetadata, NativeUi, NativeMenu, NativePlinth })
        {
            var a = Read(nativeContent, source); CustomEquipmentService.Rename(a, redirects);
            SkinnedMeshStageService.RedirectImports(a, redirects);
            if (model is not null || summon is not null)
                foreach (var component in a.Exports.OfType<NormalExport>())
                {
                    var reference = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(d => d.Name.ToString() == "SkeletalMesh");
                    var package = reference is null ? "" : EquipmentAssetService.Reference(a, reference)?.Package;
                    if (package == model?.MeshPackage || package == summon?.MeshPackage)
                        component.Data.RemoveAll(d => d.Name.ToString() == "OverrideMaterials");
                }
            if (source == NativeMetadata)
            {
                Require(NativeAssetTextPatch.SetGameplayTag(a, "PawnTag", VehicleProjectService.PawnTag(p)), "Missing vehicle tag");
                Require(NativeAssetTextPatch.SetGameplayTag(a, "ProgressTag", VehicleProjectService.ProgressTag(p)), "Missing vehicle progression");
                Require(NativeAssetTextPatch.SetGameplayTag(a, "OwningCharacter", p.OwnerTag), "Missing vehicle owner");
                Require(NativeAssetTextPatch.SetStringTableText(a, "DisplayName", StringTableGenService.ObjectPathFor(modId), "Vehicle." + p.Id + ".Owner"), "Missing owner text");
                Require(NativeAssetTextPatch.SetStringTableText(a, "DisplaySubtitle", StringTableGenService.ObjectPathFor(modId), "Vehicle." + p.Id + ".Name"), "Missing vehicle name");
            }
            if (source == NativeUi)
            {
                if (p.MenuIcon is not null) Require(NativeAssetTextPatch.SetSoftObject(a, "MenuIcon", VehicleIconService.Package(p)), "Missing vehicle menu icon binding");
                Require(NativeAssetTextPatch.SetGameplayTag(a, "PawnTag", VehicleProjectService.PawnTag(p)), "Missing UI tag");
                Require(NativeAssetTextPatch.SetStringTableText(a, "Description", StringTableGenService.ObjectPathFor(modId), "Vehicle." + p.Id + ".Description"), "Missing vehicle description");
                Require(NativeAssetTextPatch.SetStringTableText(a, "LockedDescription", StringTableGenService.ObjectPathFor(modId), "Vehicle." + p.Id + ".Locked"), "Missing vehicle lock text");
            }
            if (source == NativeBlueprint)
            {
                VehicleToyboxService.Apply(a, p);
                if (model is not null)
                {
                    var body = a.Exports.OfType<NormalExport>().SingleOrDefault(e => e.ObjectName.ToString() == VehicleCustomizationService.BodyComponent(a));
                    Require(body is not null, "Missing vehicle driving body.");
                    body!.Data.RemoveAll(d => d.Name.ToString() == "PhysicsAssetOverride");
                    var reference = SwordCombatService.Obj(a, NativePhysics, UnrealPathUtil.AssetName(NativePhysics), "/Script/Engine", "PhysicsAsset");
                    body.Data.Add(new ObjectPropertyData(new FName(a, "PhysicsAssetOverride")) { Value = reference }); body.CreateBeforeSerializationDependencies.Add(reference);
                }
                ApplyTransforms(a, p.Transforms);
                VehicleCustomizationService.Apply(a, p);
                VehicleLightService.Apply(a, p.Lights);
                VehicleLightService.ApplyAccent(a, p.AccentColor, gameplay: true);
                VehicleLightSurfaceService.Apply(a, p);
            }
            if (source == NativeMenu)
            {
                VehicleToyboxService.Apply(a, p);
                // The display actor has decorative meshes, but no gameplay light sources.
                ApplyTransforms(a, p.Transforms.Where(t => !VehicleCustomizationService.IsSeat(t.Component) && !VehicleLightService.IsSource(t.Component) && a.Exports.Any(e => e.ObjectName.ToString() == t.Component)));
                VehicleCustomizationService.Apply(a, VehicleCustomizationService.DisplayEdits(a, p));
                VehicleLightService.ApplyAccent(a, p.AccentColor, gameplay: false);
                VehicleLightSurfaceService.Apply(a, p);
            }
            if (source == NativePlinth)
            {
                VehicleToyboxService.Apply(a, p);
                var display = VehicleCustomizationService.DisplayEdits(a, p);
                // A plinth has a different native component graph; only added-part offsets apply.
                ApplyTransforms(a, p.Transforms.Where(t => p.ToyboxParts.Any(part => part.Added && part.Component == t.Component)));
                VehicleCustomizationService.Apply(a, display);
            }
            if (source == NativeBlueprint || source == NativeMenu || source == NativePlinth) VehicleScaleService.Apply(a, p);
            Save(a, redirects[source]);
        }
        var prog = Read(nativeContent, NativeProgress); var raw = prog.Exports.OfType<RawExport>().Single();
        raw.Data = CreateProgress(prog, raw.Data, VehicleProjectService.ProgressTag(p)); CustomEquipmentService.Rename(prog, redirects); Save(prog, VehicleProjectService.Progress(p));
        var staged = Read(content, VehicleProjectService.Metadata(p));
        Require(NativeAssetTextPatch.GetGameplayTag(staged, "PawnTag") == VehicleProjectService.PawnTag(p) && NativeAssetTextPatch.GetGameplayTag(staged, "OwningCharacter") == p.OwnerTag, "Vehicle identity did not survive staging.");
        var bp = Read(content, VehicleProjectService.Blueprint(p));
        VehicleToyboxService.Verify(bp, p);
        VehicleToyboxService.Verify(Read(content, menu), p);
        VehicleToyboxService.Verify(Read(content, plinth), p);
        VehicleCustomizationService.Verify(bp, p);
        VehicleLightService.Verify(bp, p.Lights);
        VehicleLightService.VerifyAccent(bp, p.AccentColor, gameplay: true);
        VehicleLightService.VerifyAccent(Read(content, menu), p.AccentColor, gameplay: false);
        var displayAsset = Read(content, menu);
        VehicleCustomizationService.Verify(displayAsset, VehicleCustomizationService.DisplayEdits(displayAsset, p));
        VehicleLightSurfaceService.Verify(bp, p);
        VehicleLightSurfaceService.Verify(Read(content, menu), p);
        var materialLibrary = new ToolMaterialLibraryService(projectRoot);
        foreach (var material in p.MaterialOverrides.Select(m => m.MaterialPath).Where(m => m.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            materialLibrary.CopyMaterialClosureToContentRoot(material, content);
        foreach (var transform in p.Transforms)
        {
            if (VehicleSocketService.IsEditable(transform.Component)) continue;
            if (VehicleCustomizationService.IsSeat(transform.Component))
            {
                var seat = VehicleCustomizationService.SeatComponents(bp).Single(c => c.Name == transform.Component).Transform;
                var actualQ = VehicleWorkshopService.Rotation(seat.Pitch, seat.Yaw, seat.Roll); var expectedQ = VehicleWorkshopService.Rotation(transform.Pitch, transform.Yaw, transform.Roll);
                Require(Math.Abs(seat.X - transform.X) < .001 && Math.Abs(seat.Y - transform.Y) < .001 && Math.Abs(seat.Z - transform.Z) < .001 && Math.Abs(System.Numerics.Quaternion.Dot(actualQ, expectedQ)) > .99999f, "Seat offset did not survive staging.");
                continue;
            }
            var actual = ReadTransform(bp.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == transform.Component));
            Require(System.Text.Json.JsonSerializer.Serialize(actual) == System.Text.Json.JsonSerializer.Serialize(transform), "Vehicle transform did not survive staging: " + transform.Component);
        }
        log($"Vehicle '{p.DisplayName}': separate entry, acquired by default; {p.Transforms.Count} attachment edits. " + Warning);
        if (p.SizeMultiplier != 1) log($"Experimental vehicle size: {p.SizeMultiplier:P0}. Check tire contact, suspension, seats, summon and collisions in game.");
        return [new(VehicleProjectService.Metadata(p), "PawnMetaData", "/Script/DinnerPawnMetaData.DinnerVehiclePawnMetaData", GameplayBundleAssets: [ObjectPath(VehicleProjectService.Blueprint(p), true)]),
            new(VehicleProjectService.Progress(p), RegistryPluginService.ProgressDefinitionPrimaryAssetType, RegistryPluginService.ProgressDefinitionClass)];
    }
    private static void ValidateSummon(VehicleProject project, string directory, string nativeContent)
    {
        if (project.SummonModel is not { } model) return;
        var donor = VehicleDonorService.Get(project);
        Require(model.DonorMeshPackage == donor.SummonMesh && model.SkeletonPackage == donor.SummonSkeleton && model.MeshPackage == VehicleProjectService.Mesh(project) + "_Summon", "The assembly model must use this driving base's summon rig.");
        Require(ExtractedPackagePathService.ResolvePackageUasset(nativeContent, donor.SummonMesh) is not null, "Extract this driving base's summon model before building.");
        SkinnedMeshStageService.ValidateRecipe(model); SkinnedMeshStageService.ReadManifest(directory, model);
        Require(model.HiddenComponents.Count == 0, "Assembly imports cannot hide driving components.");
    }
    internal static byte[] CreateProgress(UAsset a, byte[] source, string tag)
    {
        using var reader = new BinaryReader(new MemoryStream(source)); Require(reader.ReadUInt16() == 0x0301, "Vehicle progress schema changed.");
        var count = reader.ReadInt32(); Require(count > 0 && count < 1000, "Invalid vehicle progress count."); byte[]? entry = null; int typeIndex = 0;
        for (int i = 0; i < count; i++)
        {
            var type = reader.ReadInt32(); var size = reader.ReadInt32(); Require(size > 0 && size < 4096, "Invalid vehicle progress record size."); var data = reader.ReadBytes(size);
            Require(data.Length == size && type < 0 && FPackageIndex.FromRawIndex(type).ToImport(a).ObjectName.ToString() == "DinnerVehicleProgressDefinition", "Unknown vehicle progress record.");
            if (size == 24 && BitConverter.ToUInt16(data) == 0x0480 && BitConverter.ToUInt16(data, 2) == 0x0701 && a.GetNameReference(BitConverter.ToInt32(data, 12)).ToString() == "GameProgress.Definitions.Vehicles.Batman.Batpod")
            { Require(entry is null, "Ambiguous vehicle progress donor."); entry = data; typeIndex = type; }
        }
        Require(reader.BaseStream.Length - reader.BaseStream.Position == 4 && reader.ReadInt32() == 0, "Vehicle progress trailer changed.");
        Require(entry is not null && entry[4] == 1 && BitConverter.ToInt32(entry, 5) == 0 && entry[9] == 1 && BitConverter.ToUInt16(entry, 10) == 0x0300 && BitConverter.ToInt32(entry, 16) == 0 && BitConverter.ToInt32(entry, 20) == 0, "Vehicle progress layout changed.");
        entry![0] = 0; entry[4] = 2; // Explicit Complete; no story collection-count tags.
        BitConverter.GetBytes(a.AddNameReference(new FString(tag))).CopyTo(entry, 12);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x0301); writer.Write(1); writer.Write(typeIndex); writer.Write(entry.Length); writer.Write(entry); writer.Write(0); return output.ToArray();
    }
}
