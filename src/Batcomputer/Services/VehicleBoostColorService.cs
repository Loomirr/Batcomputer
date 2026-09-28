using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Recolors the Forever Batmobile's four native boost Niagara systems. These
/// systems are referenced by its boost data set, not by the vehicle Blueprint.
/// The original extraction is read-only; modified packages are staged into
/// the mod content root at their native paths.
/// </summary>
internal static class VehicleBoostColorService
{
    internal const string SupportedDonor = "batmobile1995";
    internal const string NativePawnData = VehicleAssetService.NativeRoot + "/DA_DPRD_Batmobile1995_BatmanForever_PawnData";
    internal const string NativeBoostSet = "/Game/Vehicles/Abilities/Boost/DataSets/AS_VehicleBoostData_BatmanForever1995";
    internal const string NativeBoostData = "/Game/Vehicles/Abilities/Boost/DataSets/BP_VehicleBoostData_BatmanForever1995";
    private const string Root = "/Game/VFX/Vehicles/BoostGen/Emitters/";
    internal static readonly string[] WiringPackages = [NativePawnData, NativeBoostSet, NativeBoostData];
    internal static readonly string[] Packages =
    [
        Root + "NS_Veh_Boost_Start_Burst_BatmanForever_01",
        Root + "NS_Veh_Boost_BatmanForever_01",
        Root + "NS_Veh_Boost_Manual_Cancel_BatmanForever_01",
        Root + "NS_Veh_Boost_Auto_Cancel_BatmanForever_01"
    ];

    internal static void Validate(VehicleProject project, string? nativeContent = null)
    {
        var color = project.BoostColor;
        if (color is null) return;
        VehicleAssetService.Require(project.DonorId == SupportedDonor,
            "Boost color is currently verified only for the 1995 Batman Forever Batmobile base.");
        VehicleAssetService.Require(new[] { color.R, color.G, color.B }.All(v => v is >= 0 and <= 255),
            "Boost color must use RGB values from 0 to 255.");
        VehicleAssetService.Require(color.R + color.G + color.B > 0,
            "Choose a visible boost color rather than black.");
        if (nativeContent is not null)
            VehicleAssetService.Require(Packages.Concat(WiringPackages).All(p => VehicleDonorService.PackageComplete(nativeContent, p)),
                "The Forever boost assets are missing. Run Refresh game assets → Add vehicle driving bases (quick).");
    }

    internal static string Private(VehicleProject project, string source) => source == NativePawnData
        ? VehicleProjectService.PawnData(project)
        : VehicleProjectService.ContentRoot(project) + "/Boost/" + UnrealPathUtil.AssetName(source);

    internal static void Stage(VehicleProject project, string nativeContent, string content, Action<UAsset, string> save, Action<string> log)
    {
        if (project.BoostColor is null) return;
        Validate(project, nativeContent);
        var redirects = Packages.Concat(WiringPackages).ToDictionary(source => source, source => Private(project, source), StringComparer.Ordinal);
        foreach (var package in Packages)
        {
            var asset = VehicleAssetService.Read(nativeContent, package);
            var count = Recolor(asset, project.BoostColor);
            VehicleAssetService.Require(count > 0, "The boost effect has no cooked color curves: " + package);
            CustomEquipmentService.Rename(asset, redirects);
            SkinnedMeshStageService.RedirectImports(asset, redirects);
            save(asset, redirects[package]);
            var roundtrip = VehicleAssetService.Read(content, redirects[package]);
            VehicleAssetService.Require(roundtrip.Exports.Count == asset.Exports.Count &&
                roundtrip.Exports.OfType<NormalExport>().Count(e => e.GetExportClassType()?.ToString() == "NiagaraDataInterfaceColorCurve") == count &&
                CurveSamples(roundtrip).SequenceEqual(CurveSamples(asset)),
                "Boost effect did not survive package staging: " + package);
        }
        // Keep this chain private to the custom car: pawn data grants its own
        // boost AbilitySet, which resolves a private data Blueprint, which in
        // turn selects the four private Niagara systems.
        foreach (var package in new[] { NativeBoostData, NativeBoostSet, NativePawnData })
        {
            var asset = VehicleAssetService.Read(nativeContent, package);
            CustomEquipmentService.Rename(asset, redirects);
            SkinnedMeshStageService.RedirectImports(asset, redirects);
            save(asset, redirects[package]);
            var staged = VehicleAssetService.Read(content, redirects[package]);
            VehicleAssetService.Require(staged.Exports.Count == asset.Exports.Count,
                "Private boost data did not survive staging: " + package);
        }
        RequireRedirect(content, redirects[NativeBoostData], redirects[Packages[1]]);
        RequireRedirect(content, redirects[NativeBoostSet], redirects[NativeBoostData]);
        RequireRedirect(content, redirects[NativePawnData], redirects[NativeBoostSet]);
        log("Forever Batmobile boost color staged on private pawn data, boost data and Niagara effects; the stock Forever car is untouched. Verify the custom car in game.");
    }

    internal static void RequireRedirect(string content, string from, string target)
    {
        var asset = VehicleAssetService.Read(content, from);
        var imports = asset.Imports.Select(i => i.ObjectName.ToString()).ToHashSet(StringComparer.Ordinal);
        VehicleAssetService.Require(imports.Contains(target), $"Private boost reference missing: {from} → {target}");
    }

    internal static int Recolor(UAsset asset, VehicleRgbColor color)
    {
        // Niagara's cooked ShaderLUT stores interleaved linear RGBA samples.
        // Retain the native HDR intensity and alpha envelope, replacing only
        // chromatic direction. The brightest channel remains unchanged.
        static float Linear(int value)
        {
            var srgb = value / 255f;
            return srgb <= .04045f ? srgb / 12.92f : MathF.Pow((srgb + .055f) / 1.055f, 2.4f);
        }
        var rgb = new[] { Linear(color.R), Linear(color.G), Linear(color.B) };
        var peak = rgb.Max();
        VehicleAssetService.Require(peak > 0, "Choose a visible boost color.");
        for (var channel = 0; channel < 3; channel++) rgb[channel] /= peak;
        var changed = 0;
        foreach (var curve in asset.Exports.OfType<NormalExport>()
                     .Where(e => e.GetExportClassType()?.ToString() == "NiagaraDataInterfaceColorCurve"))
        {
            var lut = curve.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "ShaderLUT");
            VehicleAssetService.Require(lut is not null && lut.Value.Length > 0 && lut.Value.Length % 4 == 0 &&
                lut.Value.All(v => v is FloatPropertyData), "Unexpected cooked boost color-curve layout.");
            for (var i = 0; i < lut!.Value.Length; i += 4)
            {
                var floats = lut.Value.Skip(i).Take(4).Cast<FloatPropertyData>().ToArray();
                var brightness = MathF.Max(0, MathF.Max(floats[0].Value, MathF.Max(floats[1].Value, floats[2].Value)));
                for (var channel = 0; channel < 3; channel++) floats[channel].Value = brightness * rgb[channel];
            }
            changed++;
        }
        return changed;
    }

    private static IEnumerable<float> CurveSamples(UAsset asset) => asset.Exports.OfType<NormalExport>()
        .Where(e => e.GetExportClassType()?.ToString() == "NiagaraDataInterfaceColorCurve")
        .SelectMany(e => e.Data.OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "ShaderLUT")
            .SelectMany(p => p.Value.Cast<FloatPropertyData>().Select(v => v.Value)));
}
