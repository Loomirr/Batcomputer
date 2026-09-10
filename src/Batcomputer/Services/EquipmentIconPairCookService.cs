namespace Batcomputer;

/// <summary>One source import, two independently recoverable native texture recipes.</summary>
internal static class EquipmentIconPairCookService
{
    internal static void CheckCollisions(IEnumerable<string> packages, IEnumerable<string> names,
        IEnumerable<string> occupiedPackages, IEnumerable<string> occupiedNames)
    {
        var existingPackages = occupiedPackages.Select(UnrealPathUtil.NormalizePackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNames = occupiedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (packages.Select(UnrealPathUtil.NormalizePackagePath).Any(existingPackages.Contains) || names.Any(existingNames.Contains))
            throw new InvalidOperationException("An icon with this name or its BCA/SDF output path already exists. Choose a different name, or reimport the existing textures.");
    }

    internal static List<GeneratedTextureEntry> Cook(string projectRoot, string sourceImage, string basePackage,
        string name, string outputParent, IEnumerable<string> occupiedPackages, IEnumerable<string> occupiedNames)
    {
        basePackage = UnrealPathUtil.NormalizePackagePath(basePackage);
        if (!HeldItemService.ValidPackage(basePackage)) throw new InvalidOperationException("Invalid equipment icon package path.");
        var specs = new[]
        {
            (Suffix: "BCA", Kind: EquipmentUiTextureCatalog.ColorKind, Profile: "ui-equipment-bca-256-bgra8",
                Folder: TextureCookTemplateService.EquipmentColorTemplateFolder, Size: 256),
            (Suffix: "SDF", Kind: EquipmentUiTextureCatalog.AlphaKind, Profile: "ui-equipment-alpha-to-sdf-64-bgra8",
                Folder: TextureCookTemplateService.EquipmentAlphaTemplateFolder, Size: 64)
        };
        CheckCollisions(specs.Select(s => basePackage + "_" + s.Suffix), specs.Select(s => $"{name} ({s.Suffix})"),
            occupiedPackages, occupiedNames);
        foreach (var spec in specs)
            if (!TextureCookTemplateService.IsTemplateReady(TextureCookTemplateService.TemplateJsonPath(projectRoot, spec.Folder)))
                throw new InvalidOperationException($"The {spec.Suffix} equipment icon template is missing. Run Full refresh to extract the native gadget icons, then retry.");
        if (!FileSystemPathUtil.IsWithinDirectory(Path.GetFullPath(outputParent),
                Path.GetFullPath(Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureImports"))))
            throw new InvalidOperationException("Equipment icon output must be inside this workspace's TextureImports folder.");
        if (Directory.Exists(outputParent)) throw new InvalidOperationException("Icon output folder already exists. Retry with a new output folder.");

        // Snapshot once so an external edit cannot give the two cooks different input bytes.
        var sourceBytes = File.ReadAllBytes(sourceImage);
        var entries = new List<GeneratedTextureEntry>();
        foreach (var spec in specs)
        {
            var root = Path.Combine(outputParent, spec.Suffix);
            var source = Path.Combine(root, "Source", Path.GetFileName(sourceImage));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, sourceBytes);
            var template = TextureCookTemplateService.TemplateJsonPath(projectRoot, spec.Folder);
            var package = basePackage + "_" + spec.Suffix;
            var result = new TextureCookService(projectRoot).Cook(new()
            {
                SourceImagePath = source, TemplateJsonPath = template,
                OutputContentRoot = Path.Combine(root, "Cooked", "LEGOBatmanLotDK", "Content"),
                OutputPackagePath = package, BleedTransparentRgb = false, WriteInlineMips = true
            });
            if (!result.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{spec.Suffix} cook failed: {result.Error ?? result.Status}");
            entries.Add(new GeneratedTextureEntry
            {
                DisplayName = $"{name} ({spec.Suffix})", Kind = spec.Kind, CookProfile = spec.Profile,
                CookWidth = spec.Size, CookHeight = spec.Size, CookPixelFormat = "PF_B8G8R8A8",
                SourcePng = source, PackagePath = package, ObjectPath = package + "." + UnrealPathUtil.AssetName(package),
                TemplateJson = template, OutputRoot = root, IoStoreRoot = Path.Combine(root, "IoStore"),
                PackageBaseName = "Texture_" + UnrealPathUtil.AssetName(package) + "_P", CreatedUtc = DateTime.UtcNow.ToString("O")
            });
        }
        // Nothing is registered with a project until both cooks have succeeded.
        return entries;
    }
}
