using System.Numerics;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class VehicleCustomizationService
{
    internal sealed record MaterialChoice(string Path, string Name, string Source, string Family)
    {
        public bool Vehicle => VehicleMaterialCatalogService.IsVehicle(Path);
        public bool Part => VehicleMaterialCatalogService.IsPart(Path);
    }
    internal static string Family(string path) => path.Contains("/UI/", StringComparison.OrdinalIgnoreCase) ? "UI shader · not recommended" : path.Contains("VehicleLight", StringComparison.OrdinalIgnoreCase) ? "Vehicle glow" : path.Contains("Rubber", StringComparison.OrdinalIgnoreCase) ? "Rubber" : path.Contains("Metal", StringComparison.OrdinalIgnoreCase) ? "Metal" : path.Contains("Transp", StringComparison.OrdinalIgnoreCase) || path.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? "Glass / transparent" : path.Contains("/Vehicles/", StringComparison.OrdinalIgnoreCase) ? "Vehicle surface" : "Surface";
    internal static IReadOnlyList<MaterialChoice> Catalog(string projectRoot) => new ToolMaterialLibraryService(projectRoot).LoadMetadataSnapshot()
        .Select(m => new MaterialChoice(m.PackagePath, m.DisplayName.Length > 0 ? m.DisplayName : UnrealPathUtil.AssetName(m.PackagePath), "Generated", Family(m.PackagePath)))
        .Concat(GameDataService.Instance.AssetsOfClass("MaterialInstanceConstant").Select(m => new MaterialChoice(m.Path, UnrealPathUtil.AssetName(m.Path), "Base game", Family(m.Path))))
        .DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IEnumerable<StructPropertyData> Seats(UAsset asset) => asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "RideableComponent")
        .Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Seats").Value.OfType<StructPropertyData>();
    private static string SeatName(StructPropertyData seat) => seat.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "SeatSocketName").Value.ToString();
    internal static bool IsSeat(string id) => id is "seat:SeatDriver" or "seat:SeatPassenger";
    internal static IReadOnlyList<VehicleAssetService.Component> SeatComponents(UAsset asset) => Seats(asset).Select(seat =>
    {
        var name = SeatName(seat); var offset = seat.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "SeatOffset");
        var position = offset.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "Translation").Value.OfType<VectorPropertyData>().FirstOrDefault()?.Value;
        var rawQ = offset.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "Rotation").Value.OfType<QuatPropertyData>().FirstOrDefault()?.Value;
        var q = rawQ is { } value ? new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W) : Quaternion.Identity;
        q = Quaternion.Normalize(q);
        var sinPitch = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
        var pitch = -MathF.Asin(sinPitch) * 180 / MathF.PI;
        // At vertical pitch, yaw and roll are coupled. Keep the equivalent rotation,
        // rather than evaluating two near-zero atan2 pairs independently.
        var yaw = (Math.Abs(sinPitch) < .9999999f ? MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)) : MathF.Atan2(2 * (q.W * q.Z - q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z))) * 180 / MathF.PI;
        var roll = Math.Abs(sinPitch) < .9999999f ? -MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)) * 180 / MathF.PI : 0;
        return new VehicleAssetService.Component("seat:" + name, "Seat position", name, new() { Component = "seat:" + name, X = (float)(position?.X ?? 0), Y = (float)(position?.Y ?? 0), Z = (float)(position?.Z ?? 0), Pitch = pitch, Yaw = yaw, Roll = roll });
    }).ToArray();
    internal static void ApplySeat(UAsset asset, VehicleComponentTransform edit)
    {
        var seat = Seats(asset).Single(s => "seat:" + SeatName(s) == edit.Component);
        var offset = seat.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "SeatOffset");
        var q = VehicleWorkshopService.Rotation(edit.Pitch, edit.Yaw, edit.Roll);
        foreach (var field in offset.Value.OfType<StructPropertyData>())
        {
            if (field.Name.ToString() == "Translation") { field.IsZero = false; field.Value = [new VectorPropertyData(new FName(asset, "Translation")) { Value = new FVector(edit.X, edit.Y, edit.Z) }]; }
            if (field.Name.ToString() == "Rotation") { field.IsZero = false; field.Value = [new QuatPropertyData(new FName(asset, "Rotation")) { Value = new FQuat(q.X, q.Y, q.Z, q.W) }]; }
        }
    }
    internal static void ValidateShape(VehicleProject project)
    {
        VehicleLightService.Validate(project.Lights);
        VehicleLightService.ValidateAccent(project.AccentColor);
        VehicleSocketService.Validate(project);
        if (project.MaterialOverrides is null || project.DisabledParts is null) throw new InvalidDataException("Incomplete vehicle customization settings.");
        if (project.MaterialOverrides.Any(m => m is null || string.IsNullOrWhiteSpace(m.Component) || m.Slot < 0 || m.Slot > 255 || !ExtractedPackagePathService.IsContentPackagePath(m.MaterialPath) || m.MaterialPath.Split('/').Skip(1).Any(s => !UnrealPathUtil.IsValidIdentifier(s))) ||
            project.MaterialOverrides.Select(m => (m.Component, m.Slot)).Distinct().Count() != project.MaterialOverrides.Count) throw new InvalidDataException("Invalid or duplicate vehicle material slots.");
        if (project.DisabledParts.Any(string.IsNullOrWhiteSpace) || project.DisabledParts.Distinct(StringComparer.Ordinal).Count() != project.DisabledParts.Count || project.DisabledParts.Any(id => id == "body" || IsSeat(id))) throw new InvalidDataException("Only decorative parts can be disabled.");
        if (project.Transforms.Where(t => t.Component.StartsWith("seat:", StringComparison.Ordinal)).Any(t => !IsSeat(t.Component) || t.ScaleX != 1 || t.ScaleY != 1 || t.ScaleZ != 1)) throw new InvalidDataException("Seat offsets cannot resize characters or add seats.");
        VehicleLightSurfaceService.Validate(project);
    }
    internal static (float Pitch, float Yaw, float Roll) FromQuaternion(Quaternion value)
    {
        var q = Quaternion.Normalize(value); var sin = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
        var regular = Math.Abs(sin) < .9999999f;
        return (-MathF.Asin(sin) * 180 / MathF.PI,
            (regular ? MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)) : MathF.Atan2(2 * (q.W * q.Z - q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z))) * 180 / MathF.PI,
            regular ? -MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)) * 180 / MathF.PI : 0);
    }
    internal static void Verify(UAsset asset, VehicleProject project)
    {
        foreach (var edit in project.MaterialOverrides)
        {
            if (edit.Component == "body" && project.LightSurfaces.Any(s => s.Slot == edit.Slot)) continue;
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == (edit.Component == "body" ? BodyComponent(asset) : edit.Component));
            var array = component.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "OverrideMaterials");
            var reference = ((ObjectPropertyData)array.Value[edit.Slot]).Value;
            var imported = reference.IsImport() ? reference.ToImport(asset) : null;
            VehicleAssetService.Require(imported is not null && imported.ObjectName.ToString() == UnrealPathUtil.AssetName(edit.MaterialPath) && imported.OuterIndex.IsImport() && imported.OuterIndex.ToImport(asset).ObjectName.ToString() == edit.MaterialPath, "Vehicle material did not survive staging: " + edit.Component);
        }
        foreach (var id in project.DisabledParts)
        {
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == id);
            VehicleAssetService.Require(component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value.IsNull(), "Vehicle part removal did not survive staging: " + id);
        }
    }
    internal static void Apply(UAsset asset, VehicleProject project)
    {
        foreach (var edit in project.MaterialOverrides)
        {
            var name = edit.Component == "body" ? BodyComponent(asset) : edit.Component;
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == name);
            var array = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
            if (array is null) { array = new(new FName(asset, "OverrideMaterials")) { ArrayType = new FName(asset, "ObjectProperty"), Value = [] }; component.Data.Add(array); }
            var entries = array.Value.ToList();
            while (entries.Count <= edit.Slot) entries.Add(new ObjectPropertyData(new FName(asset, entries.Count.ToString())) { Value = FPackageIndex.FromRawIndex(0) });
            var reference = SwordCombatService.Obj(asset, edit.MaterialPath, UnrealPathUtil.AssetName(edit.MaterialPath), "/Script/Engine", "MaterialInstanceConstant");
            entries[edit.Slot] = new ObjectPropertyData(new FName(asset, edit.Slot.ToString())) { Value = reference }; array.Value = entries.ToArray();
            if (!component.CreateBeforeSerializationDependencies.Contains(reference)) component.CreateBeforeSerializationDependencies.Add(reference);
        }
        foreach (var id in project.DisabledParts)
        {
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == id);
            component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value = FPackageIndex.FromRawIndex(0);
        }
    }
    private static string BodyComponent(UAsset asset) => asset.Exports.OfType<NormalExport>().Any(e => e.ObjectName.ToString() == "SkeletalMeshComponent") ? "SkeletalMeshComponent" : "SkeletalMesh1_GEN_VARIABLE";
}
