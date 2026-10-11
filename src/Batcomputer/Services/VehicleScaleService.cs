using UAssetAPI;

namespace Batcomputer;

/// <summary>Reject unproven runtime scaling; bake vehicle dimensions into the imported mesh.</summary>
internal static class VehicleScaleService
{
    internal static void RequireUnitScale(VehicleProject project) => VehicleAssetService.Require(
        project.SizeMultiplier == 1,
        "Whole-vehicle runtime resizing is unavailable: it can disagree with the driving model or crash. " +
        "Set Whole vehicle size to 100%, resize the source model, and reimport it. Saved placements are not changed automatically.");

    internal static void Apply(UAsset asset, VehicleProject project) => RequireUnitScale(project);
    internal static void ApplySpawnScale(UAsset asset, VehicleProject project) => RequireUnitScale(project);
    internal static void VerifySpawnScale(UAsset asset, VehicleProject project) => RequireUnitScale(project);
}
