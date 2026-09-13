using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Per-mesh sockets. The shared driving skeleton and its animations are never edited.</summary>
internal static class VehicleSocketService
{
    internal static readonly string[] GadgetSockets = ["LauncherGadget_01", "LauncherGadget_02", "Grapple_01", "VFX_Shoot_01", "VFX_Shoot_02"];
    internal static readonly string[] EffectSockets = ["VFX_Exhaust_01"];
    internal static IEnumerable<string> EditableSockets => GadgetSockets.Concat(EffectSockets);
    internal static bool IsEditable(string id) => id.StartsWith("socket:", StringComparison.Ordinal) && EditableSockets.Contains(id[7..], StringComparer.Ordinal);
    // Forever's outlet is socket-local +Z. This is preview geometry only; changing
    // the saved socket rotation to fix an arrow would rotate the working game VFX.
    internal static float[] MarkerDirection(string socket) => socket == "VFX_Exhaust_01" ? [0, 0, 1] : [1, 0, 0];
    internal static string Label(string socket) => socket switch {
        "LauncherGadget_01" => "Rocket launcher 1", "LauncherGadget_02" => "Rocket launcher 2",
        "Grapple_01" => "Grapple launcher", "VFX_Shoot_01" => "Firing reference 1", "VFX_Shoot_02" => "Firing reference 2",
        "VFX_Exhaust_01" => "Boost / exhaust outlet", _ => socket
    };
    internal static void Validate(VehicleProject project)
    {
        foreach (var t in project.Transforms.Where(t => t.Component.StartsWith("socket:", StringComparison.Ordinal)))
            VehicleAssetService.Require(IsEditable(t.Component) && t.ScaleX == 1 && t.ScaleY == 1 && t.ScaleZ == 1, "Only supported vehicle launcher / exhaust sockets can move. Socket scale stays at 1.");
    }
    internal static NormalExport Socket(UAsset skeleton, string name) => skeleton.Exports.OfType<NormalExport>().Single(e =>
        e.GetExportClassType()?.ToString() == "SkeletalMeshSocket" && e.Data.OfType<NamePropertyData>().Any(p => p.Name.ToString() == "SocketName" && p.Value.ToString() == name));
    internal static VehicleComponentTransform Transform(NormalExport socket, string id)
    {
        var p = socket.Data.OfType<StructPropertyData>().SingleOrDefault(p => p.Name.ToString() == "RelativeLocation")?.Value.OfType<VectorPropertyData>().SingleOrDefault()?.Value;
        var r = socket.Data.OfType<StructPropertyData>().SingleOrDefault(p => p.Name.ToString() == "RelativeRotation")?.Value.OfType<RotatorPropertyData>().SingleOrDefault()?.Value;
        return new() { Component = id, X = (float)(p?.X ?? 0), Y = (float)(p?.Y ?? 0), Z = (float)(p?.Z ?? 0), Pitch = (float)(r?.Pitch ?? 0), Yaw = (float)(r?.Yaw ?? 0), Roll = (float)(r?.Roll ?? 0) };
    }
    internal static void Set(UAsset mesh, UAsset skeleton, string name, string bone, VehicleComponentTransform transform)
    {
        var body = mesh.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "SkeletalMesh");
        var list = body.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "Sockets");
        if (list is null) { list = new(new FName(mesh, "Sockets")) { ArrayType = new FName(mesh, "ObjectProperty"), Value = [] }; body.Data.Add(list); }
        var existing = list.Value.OfType<ObjectPropertyData>().Where(p => p.Value.IsExport()).Select(p => p.Value.ToExport(mesh)).OfType<NormalExport>()
            .SingleOrDefault(e => e.Data.OfType<NamePropertyData>().Any(p => p.Name.ToString() == "SocketName" && p.Value.ToString() == name));
        if (existing is null)
        {
            var source = Socket(skeleton, "Grapple_01");
            existing = new NormalExport { Asset = mesh, ObjectName = new FName(mesh, "BC_Socket_" + name),
                ClassIndex = AttachmentClearanceService.RebaseImport(mesh, skeleton, source.ClassIndex),
                TemplateIndex = AttachmentClearanceService.RebaseImport(mesh, skeleton, source.TemplateIndex),
                OuterIndex = FPackageIndex.FromExport(mesh.Exports.IndexOf(body)), SuperIndex = FPackageIndex.FromRawIndex(0),
                ObjectFlags = source.ObjectFlags, Data = [], Extras = [],
                CreateBeforeSerializationDependencies = [], SerializationBeforeSerializationDependencies = [],
                SerializationBeforeCreateDependencies = [], CreateBeforeCreateDependencies = [] };
            existing.CreateBeforeCreateDependencies.Add(existing.OuterIndex);
            existing.SerializationBeforeCreateDependencies.Add(existing.ClassIndex);
            if (!existing.TemplateIndex.IsNull()) existing.SerializationBeforeCreateDependencies.Add(existing.TemplateIndex);
            mesh.Exports.Add(existing);
            var index = FPackageIndex.FromExport(mesh.Exports.Count - 1);
            list.Value = [.. list.Value, new ObjectPropertyData(new FName(mesh, list.Value.Length.ToString())) { Value = index }];
            body.CreateBeforeSerializationDependencies.Add(index);
        }
        existing.Data = [
            new NamePropertyData(new FName(mesh, "SocketName")) { Value = new FName(mesh, name) },
            new NamePropertyData(new FName(mesh, "BoneName")) { Value = new FName(mesh, bone) },
            new StructPropertyData(new FName(mesh, "RelativeLocation")) { StructType = new FName(mesh, "Vector"), Value = [new VectorPropertyData(new FName(mesh, "RelativeLocation")) { Value = new FVector(transform.X, transform.Y, transform.Z) }] },
            new StructPropertyData(new FName(mesh, "RelativeRotation")) { StructType = new FName(mesh, "Rotator"), Value = [new RotatorPropertyData(new FName(mesh, "RelativeRotation")) { Value = new FRotator(transform.Pitch, transform.Yaw, transform.Roll) }] }
        ];
        list.IsZero = false;
        if (!mesh.HasUnversionedProperties)
        {
            // Unreal-cooked imports have UE5 complete type tags, unlike the native unversioned
            // donor. New properties need these tags even though their values use the same schema.
            FPropertyTypeName Type(params (string Name, int Children)[] nodes) => new(nodes.Select(n => new FPropertyTypeNameNode { Name = new FName(mesh, n.Name), InnerCount = n.Children }).ToList(), true);
            list.PropertyTypeName = Type(("ArrayProperty", 1), ("ObjectProperty", 0));
            foreach (var property in existing.Data)
                property.PropertyTypeName = property is StructPropertyData s
                    ? Type(("StructProperty", 1), (s.StructType.ToString(), 1), ("/Script/CoreUObject", 0))
                    : Type((property.PropertyType.ToString(), 0));
        }
    }
    internal static void Apply(UAsset mesh, UAsset skeleton, VehicleProject project)
    {
        Validate(project);
        foreach (var t in project.Transforms.Where(t => IsEditable(t.Component)))
        {
            var source = Socket(skeleton, t.Component[7..]);
            var bone = source.Data.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "BoneName").Value.ToString();
            Set(mesh, skeleton, t.Component[7..], bone, t);
        }
    }
    internal static void Verify(UAsset mesh, VehicleProject project)
    {
        foreach (var t in project.Transforms.Where(t => IsEditable(t.Component)))
        {
            var actual = Transform(Socket(mesh, t.Component[7..]), t.Component);
            VehicleAssetService.Require(new[] { actual.X - t.X, actual.Y - t.Y, actual.Z - t.Z, actual.Pitch - t.Pitch, actual.Yaw - t.Yaw, actual.Roll - t.Roll }.All(v => Math.Abs(v) < .001f), "Vehicle socket did not survive staging: " + t.Component);
        }
    }
}
