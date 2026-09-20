using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Experimental component-space sizing. The donor's handling still needs a driving check.</summary>
internal static class VehicleScaleService
{
    internal static void Apply(UAsset asset, VehicleProject project)
    {
        if (project.SizeMultiplier == 1) return;
        var donor = VehicleDonorService.Get(project);
        var meshes = new[] { donor.Mesh, donor.SummonMesh, project.Model?.MeshPackage, project.SummonModel?.MeshPackage };
        var count = 0;
        foreach (var component in asset.Exports.OfType<NormalExport>())
        {
            // BP_PlayableVehicleBase.SCS_Node_17 attaches this inherited component to
            // SkeletalMeshComponent. It inherits that scale; applying it twice squares it.
            if (component.ObjectName.ToString() == "TtSummonAnimation_GEN_VARIABLE" && asset.Exports.Any(e => e.ObjectName.ToString() == "SkeletalMeshComponent")) continue;
            var mesh = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(d => d.Name.ToString() == "SkeletalMesh");
            if (mesh is null || !meshes.Contains(EquipmentAssetService.Reference(asset, mesh)?.Package)) continue;
            var current = component.Data.OfType<StructPropertyData>().SingleOrDefault(d => d.Name.ToString() == "RelativeScale3D")?.Value.OfType<VectorPropertyData>().SingleOrDefault()?.Value ?? new FVector(1, 1, 1);
            component.Data.RemoveAll(d => d.Name.ToString() == "RelativeScale3D");
            component.Data.Add(new StructPropertyData(new FName(asset, "RelativeScale3D")) { StructType = new FName(asset, "Vector"), Value = [new VectorPropertyData(new FName(asset, "RelativeScale3D")) { Value = new FVector(current.X * project.SizeMultiplier, current.Y * project.SizeMultiplier, current.Z * project.SizeMultiplier) }] });
            count++;
        }
        VehicleAssetService.Require(count > 0, "Vehicle size has no matching model component in this actor.");
        foreach (var movement in asset.Exports.OfType<NormalExport>().Where(e => e.GetExportClassType()?.ToString() == "TtWheeledVehicleComponent"))
        {
            foreach (var field in movement.Data.OfType<FloatPropertyData>().Where(d => d.Name.ToString() == "MaxWheelLower")) field.Value *= project.SizeMultiplier;
            foreach (var field in movement.Data.OfType<StructPropertyData>().Where(d => d.Name.ToString() == "ConvexWheelInfo").SelectMany(d => d.Value).OfType<FloatPropertyData>().Where(d => d.Name.ToString() is "FrontWheelWidth" or "RearWheelWidth")) field.Value *= project.SizeMultiplier;
        }
    }
}
