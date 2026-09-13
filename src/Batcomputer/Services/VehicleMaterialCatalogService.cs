using CUE4Parse.FileProvider;

namespace Batcomputer;

internal static class VehicleMaterialCatalogService
{
    internal static string PackageFromMountedFile(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return "";
        const string game = "LEGOBatmanLotDK/Content/";
        if (path.StartsWith(game, StringComparison.OrdinalIgnoreCase)) return "/Game/" + path[game.Length..^7];
        var content = path.LastIndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (content < 0) return "";
        var mount = path[..content].Split('/').Last();
        return "/" + mount + "/" + path[(content + 9)..^7];
    }
    internal static bool IsVehicle(string path) => path.Contains("/Vehicles/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/UI/", StringComparison.OrdinalIgnoreCase);
    internal static bool IsPart(string path) => path.Contains("LEGO_Material_Library/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/TtLEGOMaterials/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Lighting/Lego_Lights/", StringComparison.OrdinalIgnoreCase) || path.Equals(VehicleAssetService.PaletteTemplate, StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<VehicleCustomizationService.MaterialChoice> Read(string projectRoot, DefaultFileProvider provider)
    {
        // The shipped character catalog is not a complete game registry. Mounts include standard
        // vehicles, DLC and the shared LEGO material plugin even without a full disk extraction.
        var installed = provider.Files.Keys.Select(PackageFromMountedFile).Where(p => p.Length > 0 && UnrealPathUtil.AssetName(p).StartsWith("MI_", StringComparison.OrdinalIgnoreCase) && (IsVehicle(p) || IsPart(p)))
            .Append(VehicleAssetService.PaletteTemplate).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new VehicleCustomizationService.MaterialChoice(p, p == VehicleAssetService.PaletteTemplate ? "LEGO Black · recolorable copy" : UnrealPathUtil.AssetName(p), "Base game", VehicleCustomizationService.Family(p)));
        return VehicleCustomizationService.Catalog(projectRoot).Concat(installed)
            .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static async Task<string> PrepareCopySource(string projectRoot, string package, string scratch, CancellationToken cancellation)
    {
        var settings = AppSettings.Current;
        var existing = package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) ? new ToolMaterialLibraryService(projectRoot).ResolvePackageUasset(package) : ExtractedPackagePathService.ResolvePackageUasset(settings.EffectiveExtractedContentRoot(), package);
        if (existing is not null && File.Exists(existing)) return existing;
        using var provider = ModelPreviewService.MakeProvider(settings.EffectiveGamePaksRoot(), settings.EffectiveUsmapPath()!);
        var mounted = provider.Files.Keys.SingleOrDefault(p => PackageFromMountedFile(p).Equals(package, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("Material is not installed: " + package);
        var extraction = await GameAssetRefreshService.RunRetocAsync(settings.EffectiveRetocExePath(), settings.EffectiveGamePaksRoot(), scratch, mounted, cancellation);
        var path = Path.Combine(scratch, mounted.Replace('/', Path.DirectorySeparatorChar));
        if (extraction.ExitCode != 0 || !File.Exists(path)) throw new InvalidDataException("This material could not be prepared for copying. For a DLC material, run Full refresh first.\n" + string.Join("\n", extraction.ErrorLines.TakeLast(3)));
        return path;
    }
}
