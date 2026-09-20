namespace Batcomputer;

internal static class VehicleIconService
{
    internal const int Width = 512, Height = 340;
    internal static string Package(VehicleProject vehicle) => VehicleProjectService.ContentRoot(vehicle) + "/UI/T_Icon_" + vehicle.Id;
    internal static VehicleIconImport Import(string projectRoot, string directory, VehicleProject vehicle, string png)
    {
        using (var source = Image.FromFile(png))
            if (source.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid || source.Width != Width || source.Height != Height)
                throw new InvalidDataException("Use a 512 × 340 PNG. Keep a transparent background and leave space around the vehicle.");
        var template = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.VehicleIconTemplateFolder);
        if (!TextureCookTemplateService.IsTemplateReady(template))
            TextureCookTemplateService.PrepareFromContentRoot(projectRoot, AppSettings.Current.EffectiveExtractedContentRoot());
        if (!TextureCookTemplateService.IsTemplateReady(template))
            throw new InvalidDataException("The native vehicle icon template is missing or changed. Run Full refresh, then import the icon again.");
        // A new immutable revision keeps the previous icon usable if cooking fails.
        var relative = Path.Combine("ImportedIcons", Guid.NewGuid().ToString("N"));
        var cache = SkinnedMeshCookService.SafePath(directory, relative); Directory.CreateDirectory(cache);
        var sourcePath = Path.Combine(cache, "source.png"); File.Copy(png, sourcePath);
        var content = Path.Combine(cache, "Cooked");
        var result = new TextureCookService(projectRoot).Cook(new() { SourceImagePath = sourcePath, TemplateJsonPath = template,
            OutputContentRoot = content, OutputPackagePath = Package(vehicle), WriteInlineMips = true, BleedTransparentRgb = true });
        if (result.Status != "created") throw new InvalidDataException("Vehicle icon cook failed: " + result.Error);
        var recipe = new VehicleIconImport { PackagePath = Package(vehicle), CacheRelativePath = relative, SourceSha256 = SkinnedMeshCookService.Hash(sourcePath) };
        foreach (var ext in new[] { ".uasset", ".uexp" })
        {
            var file = Path.Combine(content, Package(vehicle)[6..].Replace('/', Path.DirectorySeparatorChar) + ext);
            File.Copy(file, Path.Combine(cache, "icon" + ext));
            recipe.Files.Add(ext, SkinnedMeshCookService.Hash(file));
        }
        var check = vehicle.Clone(); check.MenuIcon = recipe; Validate(check, directory);
        return recipe;
    }
    internal static void Validate(VehicleProject vehicle, string directory)
    {
        if (vehicle.MenuIcon is not { } icon) return;
        if (icon.PackagePath != Package(vehicle)) throw new InvalidDataException("Vehicle icon identity changed. Reimport its PNG for this vehicle.");
        var cache = SkinnedMeshCookService.SafePath(directory, icon.CacheRelativePath);
        if (icon.Files is null || icon.Files.Count != 2 || !icon.Files.ContainsKey(".uasset") || !icon.Files.ContainsKey(".uexp"))
            throw new InvalidDataException("Vehicle icon recipe is incomplete. Reimport its PNG.");
        if (!File.Exists(Path.Combine(cache, "source.png")) || SkinnedMeshCookService.Hash(Path.Combine(cache, "source.png")) != icon.SourceSha256)
            throw new InvalidDataException("The saved vehicle icon source changed. Reimport it before building.");
        foreach (var (ext, hash) in icon.Files)
        {
            var file = Path.Combine(cache, "icon" + ext);
            if (!File.Exists(file) || new FileInfo(file).Length == 0 || SkinnedMeshCookService.Hash(file) != hash)
                throw new InvalidDataException("The cooked vehicle icon changed or is missing. Reimport its PNG.");
        }
    }
    internal static string? PreviewPath(VehicleProject vehicle, string directory) => vehicle.MenuIcon is { } icon
        ? Path.Combine(SkinnedMeshCookService.SafePath(directory, icon.CacheRelativePath), "source.png") : null;
    internal static void Stage(VehicleProject vehicle, string directory, string content)
    {
        if (vehicle.MenuIcon is not { } icon) return;
        Validate(vehicle, directory);
        var cache = SkinnedMeshCookService.SafePath(directory, icon.CacheRelativePath);
        foreach (var ext in icon.Files.Keys)
        {
            var file = SkinnedMeshCookService.SafePath(content, Package(vehicle)[6..] + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(Path.Combine(cache, "icon" + ext), file, overwrite: false);
        }
    }
}
