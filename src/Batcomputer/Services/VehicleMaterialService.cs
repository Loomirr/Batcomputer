using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Persistent, vehicle-owned instances. Builds consume these copies, not a shared base.</summary>
internal static class VehicleMaterialService
{
    private static readonly object PublishLock = new();
    internal static string SlotName(VehicleProject vehicle, int slot, string component = "body")
    {
        if (slot < 0 || slot > 63) throw new InvalidDataException("Invalid material slot.");
        var car = Regex.Replace(vehicle.DisplayName, "[^A-Za-z0-9_]", "");
        if (car.Length == 0) car = vehicle.Id;
        var part = component == "body" ? "" : Regex.Replace(component.Replace("_GEN_VARIABLE", ""), "[^A-Za-z0-9_]", "") + "_";
        return "MI_" + part + "Slot" + slot + "_" + car[..Math.Min(48, car.Length)];
    }
    internal static bool Owned(VehicleProject vehicle, string package) =>
        package.StartsWith(VehicleProjectService.ContentRoot(vehicle) + "/Materials/MI_", StringComparison.Ordinal) &&
        package.Split('/').Skip(1).All(UnrealPathUtil.IsValidIdentifier);

    internal static async Task EnsureSlots(string projectRoot, VehicleProject vehicle, string scratch, CancellationToken cancellation)
    {
        if (vehicle.Model is not { } model) return;
        // Work on the caller's cloned recipe. A cancelled import never replaces its saved model.
        foreach (var slot in model.Materials)
        {
            cancellation.ThrowIfCancellationRequested();
            var paint = vehicle.Palette.FirstOrDefault(p => p.Slot == slot.Slot);
            if (paint is null && slot.MaterialPath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) continue;
            var source = await VehicleMaterialCatalogService.PrepareCopySource(projectRoot, slot.MaterialPath, scratch, cancellation);
            slot.MaterialPath = Publish(projectRoot, vehicle, slot.Slot, "body", source, null, paint);
            vehicle.Palette.RemoveAll(p => p.Slot == slot.Slot);
        }
    }

    internal static string Publish(string projectRoot, VehicleProject vehicle, int slot, string component,
        string sourceFile, string? editPackage, VehiclePaletteColor? paint)
    {
        lock (PublishLock)
        {
            var library = new ToolMaterialLibraryService(projectRoot);
            var sourceInfo = new MaterialGenService(projectRoot).ReadTemplate(sourceFile);
            // A copy of authored paint gets its own swatch too; it must not change when the
            // original material's color is edited later.
            paint ??= library.LoadMetadataSnapshot().FirstOrDefault(m => m.PackagePath == sourceInfo.SourcePackagePath)?.VehiclePaint;
            var exportRoot = AppSettings.Current.EffectiveExportContentRoot();
            string FileAt(string root, string package) => SkinnedMeshCookService.SafePath(root, package[6..] + ".uasset");
            var package = editPackage ?? VehicleProjectService.ContentRoot(vehicle) + "/Materials/" + SlotName(vehicle, slot, component);
            if (!Owned(vehicle, package)) throw new InvalidDataException("Base-game and other vehicles' materials are read-only here. Create a copy first.");
            if (editPackage is not null && library.ResolvePackageUasset(package) is null) throw new InvalidDataException("The material to edit is missing.");
            if (editPackage is null)
            {
                var stem = package; int suffix = 2;
                while (File.Exists(FileAt(exportRoot, package)) || File.Exists(FileAt(library.ContentRoot, package))) package = stem + "_" + suffix++;
            }
            var info = sourceInfo;
            var prepared = Path.Combine(Path.GetTempPath(), "BatcomputerVehicleMaterial", Guid.NewGuid().ToString("N"));
            var packages = new List<string>();
            var backups = new Dictionary<string, byte[]?>();
            try
            {
                void Save(UAsset asset, string path)
                {
                    var file = FileAt(prepared, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    asset.FolderName = new FString(path); asset.Write(file); packages.Add(path);
                }
                if (paint is not null) VehiclePaintService.Create(AppSettings.Current.EffectiveExtractedContentRoot(), paint, package, Save);
                else
                {
                    var material = new UAsset(sourceFile, EngineVersion.VER_UE5_6, VehicleAssetService.Maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
                    var original = material.FolderName.ToString();
                    CustomEquipmentService.Rename(material, new Dictionary<string, string> { [original] = package }); Save(material, package);
                }
                // Validate every prepared package before publishing any bytes.
                foreach (var path in packages) _ = VehicleAssetService.Read(prepared, path);
                var targets = packages.SelectMany(p => new[] { exportRoot, library.ContentRoot }.SelectMany(r =>
                    new[] { ".uasset", ".uexp", ".ubulk" }.Select(ext => Path.ChangeExtension(FileAt(r, p), ext)))).Append(library.CatalogPath).Distinct().ToArray();
                foreach (var file in targets) backups[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
                foreach (var path in packages)
                foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
                {
                    var from = Path.ChangeExtension(FileAt(prepared, path), ext); var to = Path.ChangeExtension(FileAt(exportRoot, path), ext);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (File.Exists(from)) File.Copy(from, to, true);
                    else if (File.Exists(to)) File.Delete(to);
                }
                library.Register([new GeneratedMaterialEntry { PackagePath = package, DisplayName = UnrealPathUtil.AssetName(package),
                    SourceMaterialPackagePath = paint is null ? info.SourcePackagePath : paint.Finish == "Flat" ? VehicleAssetService.PaletteTemplate : VehiclePaintService.Material(paint.Finish),
                    ParentMaterialPath = new MaterialGenService(projectRoot).ReadTemplate(FileAt(prepared, package)).ParentMaterialPath,
                    VehiclePaint = paint, CreatedUtc = DateTime.UtcNow.ToString("O") }]);
                if (library.ResolvePackageUasset(package) is null) throw new InvalidDataException("The material copy could not be added to the library.");
                return package;
            }
            catch
            {
                foreach (var (file, bytes) in backups)
                    if (bytes is not null) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); }
                    else if (File.Exists(file)) File.Delete(file);
                throw;
            }
            finally
            {
                if (Directory.Exists(prepared) && FileSystemPathUtil.IsWithinDirectory(prepared, Path.Combine(Path.GetTempPath(), "BatcomputerVehicleMaterial")))
                    try { Directory.Delete(prepared, true); } catch (IOException) { }
            }
        }
    }
}
