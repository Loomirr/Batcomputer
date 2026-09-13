using System.Globalization;
using System.Numerics;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse_Conversion.Meshes;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Rigid lens sections reuse existing LED components, including their native callbacks.</summary>
internal static class VehicleLightSurfaceService
{
    internal const string BulbMaterial = "/Game/Art/Lighting/Lego_Lights/LightGlows/Materials/MI_LEGO_VehicleLight_Bulb_01";
    internal const string InvisibleMaterial = "/Game/Characters/Materials/M_Masters/M_Invisible";
    internal sealed record Role(string Id, string Label, string Component, string Glow);
    internal static readonly Role[] Roles = [
        new("Headlight_L", "Headlight · left", "H_LED_02_Mesh_GEN_VARIABLE", "H_Glow_02_Mesh_GEN_VARIABLE"),
        new("Headlight_R", "Headlight · right", "H_LED_01_Mesh_GEN_VARIABLE", "H_Glow_01_Mesh_GEN_VARIABLE"),
        new("BrakeLight_L", "Brake light · left", "B_LED_01_Mesh_GEN_VARIABLE", "B_Glow_01_Mesh_GEN_VARIABLE"),
        new("BrakeLight_R", "Brake light · right", "B_LED_02_Mesh_GEN_VARIABLE", "B_Glow_02_Mesh_GEN_VARIABLE"),
        new("RearLight_L", "Rear light · left", "R_LED_01_Mesh_GEN_VARIABLE", "R_Glow_01_Mesh_GEN_VARIABLE"),
        new("RearLight_R", "Rear light · right", "R_LED_02_Mesh_GEN_VARIABLE", "R_Glow_02_Mesh_GEN_VARIABLE"),
        new("Accent_01", "Accent · 1", "C_LED_01_Mesh_GEN_VARIABLE", "C_Glow_01_Mesh_GEN_VARIABLE"),
        new("Accent_02", "Accent · 2", "C_LED_02_Mesh_GEN_VARIABLE", "C_Glow_02_Mesh_GEN_VARIABLE"),
        new("Accent_03", "Accent · 3", "C_LED_03_Mesh_GEN_VARIABLE", "C_Glow_03_Mesh_GEN_VARIABLE"),
        new("Accent_04", "Accent · 4", "C_LED_04_Mesh_GEN_VARIABLE", "C_Glow_04_Mesh_GEN_VARIABLE")
    ];
    internal sealed record Surface(int Slot, string Name, bool CanAssign, string Reason, float[] Center, string? SuggestedRole);
    internal static string? Suggest(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (key.StartsWith("bc")) key = key[2..];
        foreach (var role in Roles)
        {
            var match = new string(role.Id.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (key == match || role.Id.EndsWith("_L") && key == "l" + match[..^1] || role.Id.EndsWith("_R") && key == "r" + match[..^1]) return role.Id;
        }
        return null;
    }
    internal static void Validate(VehicleProject project)
    {
        if (project.LightSurfaces is null) throw new InvalidDataException("Missing light surface assignments.");
        VehicleAssetService.Require(project.LightSurfaces.All(s => s is not null && s.Slot >= 0 && project.Model is not null && s.Slot < project.Model.Materials.Count && Roles.Any(r => r.Id == s.Role)), "Light surfaces require a valid imported-body slot and a supported role.");
        VehicleAssetService.Require(project.LightSurfaces.Select(s => s.Slot).Distinct().Count() == project.LightSurfaces.Count && project.LightSurfaces.Select(s => s.Role).Distinct().Count() == project.LightSurfaces.Count, "Each light role and body slot can be assigned once. Combine matching lenses into one material slot in Blender.");
        foreach (var binding in project.LightSurfaces)
        {
            var role = Roles.Single(r => r.Id == binding.Role);
            VehicleAssetService.Require(!project.DisabledParts.Contains(role.Component), "Restore the lamp component before assigning its light role: " + role.Label);
            VehicleAssetService.Require(!project.MaterialOverrides.Any(m => m.Component == role.Component), "Reset the lamp's material override before assigning its light role: " + role.Label);
        }
    }
    internal static VehicleWorkshopService.Pose BodyPose(USkeletalMesh mesh)
    {
        var poses = new List<VehicleWorkshopService.Pose>();
        for (var i = 0; i < mesh.ReferenceSkeleton.FinalRefBoneInfo.Length; i++)
        {
            var bone = mesh.ReferenceSkeleton.FinalRefBoneInfo[i]; var p = mesh.ReferenceSkeleton.FinalRefBonePose[i];
            var local = new VehicleWorkshopService.Pose([p.Translation.X, p.Translation.Y, p.Translation.Z], [p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W], [p.Scale3D.X, p.Scale3D.Y, p.Scale3D.Z]);
            poses.Add(bone.ParentIndex < 0 ? local : VehicleWorkshopService.Compose(poses[bone.ParentIndex], local));
            if (bone.Name.Text == "Body") return poses[^1];
        }
        throw new InvalidDataException("Vehicle has no Body bone.");
    }
    internal static IReadOnlyList<Surface> Analyze(USkeletalMesh mesh, VehicleProject project)
    {
        if (project.Model is null) return [];
        VehicleAssetService.Require(mesh.TryConvert(out var converted) && converted?.LODs.Count > 0, "Vehicle light geometry could not be decoded.");
        var lod = converted!.LODs[0]; var body = Array.FindIndex(mesh.ReferenceSkeleton.FinalRefBoneInfo, b => b.Name.Text == "Body");
        var sections = lod.Sections?.Value ?? throw new InvalidDataException("Missing mesh sections.");
        var allIndices = lod.Indices?.Value ?? throw new InvalidDataException("Missing mesh indices.");
        var allVertices = lod.Verts ?? throw new InvalidDataException("Missing mesh vertices.");
        var results = new List<Surface>();
        foreach (var slot in project.Model.Materials)
        {
            var indices = sections.Where(s => s.MaterialIndex == slot.Slot).SelectMany(s => allIndices.Skip(s.FirstIndex).Take(s.NumFaces * 3)).Distinct().ToArray();
            var vertices = indices.Select(i => allVertices[i]).ToArray();
            var rigid = body >= 0 && vertices.Length > 0 && vertices.All(v => v.Influences.Any(i => i.Weight > .0001f) && v.Influences.Where(i => i.Weight > .0001f).All(i => i.Bone == body));
            var center = vertices.Length == 0 ? new float[3] : new[] { (vertices.Min(v => v.Position.X) + vertices.Max(v => v.Position.X)) / 2, (vertices.Min(v => v.Position.Y) + vertices.Max(v => v.Position.Y)) / 2, (vertices.Min(v => v.Position.Z) + vertices.Max(v => v.Position.Z)) / 2 };
            var reason = !rigid ? "Use a lens-only slot weighted entirely to Body. Wheel / moving-bone lights are not supported yet." : vertices.Length >= 65535 ? "Keep a light section below 65,535 vertices." : "";
            results.Add(new(slot.Slot, slot.SourceMaterialName, reason.Length == 0, reason, center, Suggest(slot.SourceMaterialName)));
        }
        return results;
    }
    internal static IReadOnlyList<Surface> StageGeometry(VehicleProject project, string nativeContent, string content, UAsset meshAsset, UAsset skeleton, Action<string> log)
    {
        if (project.LightSurfaces.Count == 0) return [];
        Validate(project);
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!, [content]);
        var mesh = provider.LoadPackageObject<USkeletalMesh>(project.Model!.MeshPackage);
        var surfaces = Analyze(mesh, project); mesh.TryConvert(out var converted); var lod = converted!.LODs[0];
        var sections = lod.Sections!.Value!; var allIndices = lod.Indices!.Value!; var vertices = lod.Verts!;
        var pose = BodyPose(mesh); var bodyQ = new Quaternion(pose.Quaternion[0], pose.Quaternion[1], pose.Quaternion[2], pose.Quaternion[3]);
        var bodyPosition = new Vector3(pose.Position[0], pose.Position[1], pose.Position[2]);
        var work = Path.Combine(Path.GetDirectoryName(content)!, "VehicleLightWork", project.Id); Directory.CreateDirectory(work);
        foreach (var binding in project.LightSurfaces)
        {
            var surface = surfaces.Single(s => s.Slot == binding.Slot);
            VehicleAssetService.Require(surface.CanAssign, surface.Name + ": " + surface.Reason);
            var indices = sections.Where(s => s.MaterialIndex == binding.Slot).SelectMany(s => allIndices.Skip(s.FirstIndex).Take(s.NumFaces * 3)).ToArray();
            var unique = indices.Distinct().ToArray(); var remap = unique.Select((v, i) => (v, i)).ToDictionary(v => v.v, v => v.i + 1);
            var obj = new StringBuilder("# Batcomputer rigid vehicle lens\nusemtl Lens\n");
            foreach (var index in unique)
            {
                var v = vertices[index]; var p = v.Position; var n = v.Normal;
                obj.AppendLine(FormattableString.Invariant($"v {p.X:R} {p.Z:R} {-p.Y:R}"));
                obj.AppendLine(FormattableString.Invariant($"vt {v.UV.U:R} {1 - v.UV.V:R}"));
                obj.AppendLine(FormattableString.Invariant($"vn {n.X:R} {n.Z:R} {-n.Y:R}"));
            }
            for (var i = 0; i < indices.Length; i += 3) { var a = remap[indices[i]]; var b = remap[indices[i + 1]]; var c = remap[indices[i + 2]]; obj.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}"); }
            var path = Path.Combine(work, binding.Role + ".obj"); File.WriteAllText(path, obj.ToString());
            var result = new StaticMeshObjProbeService().CreateObjHeadProbe(new() { ExtractedContentRoot = nativeContent, UsmapPath = AppSettings.Current.EffectiveUsmapPath()!, OutputContentRoot = content, OutputPackagePath = MeshPackage(project, binding), ObjPath = path, Scale = 1, LegacyMaterialPath = BulbMaterial });
            VehicleAssetService.Require(result.Error is null && File.Exists(result.OutputUasset), "Light section could not be cooked: " + result.Error);
            var position = Vector3.Transform(new Vector3(surface.Center[0], surface.Center[1], surface.Center[2]) - bodyPosition, Quaternion.Inverse(bodyQ));
            var inverse = VehicleCustomizationService.FromQuaternion(Quaternion.Inverse(bodyQ));
            VehicleSocketService.Set(meshAsset, skeleton, SocketName(binding), "Body", new() { X = position.X, Y = position.Y, Z = position.Z, Pitch = inverse.Pitch, Yaw = inverse.Yaw, Roll = inverse.Roll });
            log("Light surface: " + surface.Name + " → " + binding.Role);
        }
        return surfaces;
    }
    internal static string SocketName(VehicleLightSurface binding) => "BC_Light_" + binding.Role;
    internal static string MeshPackage(VehicleProject project, VehicleLightSurface binding) => VehicleProjectService.ContentRoot(project) + "/Lights/SM_" + binding.Role;
    private static NormalExport Component(UAsset asset, string name) => asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == name);
    internal static void Apply(UAsset asset, VehicleProject project)
    {
        foreach (var binding in project.LightSurfaces)
        {
            var role = Roles.Single(r => r.Id == binding.Role); var lamp = Component(asset, role.Component);
            VehicleAssetService.ApplyTransforms(asset, [project.Transforms.FirstOrDefault(t => t.Component == role.Component) ?? new() { Component = role.Component }]);
            var mesh = lamp.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh");
            mesh.Value = SwordCombatService.Obj(asset, MeshPackage(project, binding), UnrealPathUtil.AssetName(MeshPackage(project, binding)), "/Script/Engine", "StaticMesh");
            lamp.CreateBeforeSerializationDependencies.Add(mesh.Value);
            var tags = lamp.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ComponentTags");
            VehicleAssetService.Require(tags.Value.Length > 1 && tags.Value.OfType<NamePropertyData>().Any(t => t.Value.ToString() == "AutoGenSocketData"), "Native light attachment tags changed.");
            ((NamePropertyData)tags.Value[1]).Value = new FName(asset, SocketName(binding)); tags.IsZero = false;
            SetMaterial(asset, lamp, 0, BulbMaterial, "MaterialInstanceConstant");
            // The original shape remains in the skinned body but is invisible. Its collision is
            // still the donor vehicle's physics; the new visual lens uses the native lamp host.
            var body = asset.Exports.OfType<NormalExport>().First(e => e.ObjectName.ToString() is "SkeletalMeshComponent" or "SkeletalMesh1_GEN_VARIABLE");
            SetMaterial(asset, body, binding.Slot, InvisibleMaterial, "Material");
            Component(asset, role.Glow).Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value = FPackageIndex.FromRawIndex(0);
        }
    }
    private static void SetMaterial(UAsset asset, NormalExport component, int slot, string path, string type)
    {
        var materials = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
        if (materials is null) { materials = new(new FName(asset, "OverrideMaterials")) { ArrayType = new FName(asset, "ObjectProperty"), Value = [] }; component.Data.Add(materials); }
        var values = materials.Value.ToList(); while (values.Count <= slot) values.Add(new ObjectPropertyData(new FName(asset, values.Count.ToString())) { Value = FPackageIndex.FromRawIndex(0) });
        var reference = SwordCombatService.Obj(asset, path, UnrealPathUtil.AssetName(path), "/Script/Engine", type);
        values[slot] = new ObjectPropertyData(new FName(asset, slot.ToString())) { Value = reference }; materials.Value = values.ToArray(); materials.IsZero = false;
        component.CreateBeforeSerializationDependencies.Add(reference);
    }
    internal static void Verify(UAsset asset, VehicleProject project)
    {
        foreach (var binding in project.LightSurfaces)
        {
            var role = Roles.Single(r => r.Id == binding.Role); var lamp = Component(asset, role.Component);
            var reference = lamp.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh");
            VehicleAssetService.Require(EquipmentAssetService.Reference(asset, reference)?.Package == MeshPackage(project, binding), "Light mesh binding did not survive staging.");
            VehicleAssetService.Require(lamp.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ComponentTags").Value.OfType<NamePropertyData>().Any(t => t.Value.ToString() == SocketName(binding)), "Light socket binding did not survive staging.");
            string? Material(NormalExport component, int slot) => EquipmentAssetService.Reference(asset,
                component.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "OverrideMaterials").Value.OfType<ObjectPropertyData>().ElementAt(slot))?.Package;
            var body = asset.Exports.OfType<NormalExport>().First(e => e.ObjectName.ToString() is "SkeletalMeshComponent" or "SkeletalMesh1_GEN_VARIABLE");
            VehicleAssetService.Require(Material(lamp, 0) == BulbMaterial && Material(body, binding.Slot) == InvisibleMaterial, "Light surface materials did not survive staging.");
            VehicleAssetService.Require(Component(asset, role.Glow).Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value.IsNull(), "Original light glow was not suppressed.");
        }
    }
}
