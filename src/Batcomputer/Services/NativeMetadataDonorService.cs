using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Reads the selected donor's native metadata.</summary>
public static class NativeMetadataDonorService
{
    public sealed record Icons(string Menu, string Suit, string Left, string Right)
    {
        public static readonly Icons Empty = new("", "", "", "");
    }

    public sealed record Donor(
        string DcmdPackagePath,
        string DcmdUassetPath,
        string PlayablePackagePath,
        string CutscenePackagePath,
        string UimdPackagePath,
        string UimdUassetPath,
        string PawnTag,
        string ProgressTag,
        Icons IconPaths);

    public static Donor? TryRead(
        TemplateRecord? dcmdTemplate,
        TemplateRecord? playableTemplate = null,
        TemplateRecord? cutsceneTemplate = null)
    {
        if (dcmdTemplate is null || string.IsNullOrWhiteSpace(dcmdTemplate.PackagePath))
        {
            return null;
        }

        // Projects keep the donor package path, but a game refresh can replace the
        // extracted directory that originally supplied the file. Prefer the saved
        // path when it remains valid and otherwise resolve the same package from
        // the active extracted dump.
        var dcmdUasset = ResolveTemplateUasset(dcmdTemplate);
        if (string.IsNullOrWhiteSpace(dcmdUasset) || !File.Exists(dcmdUasset))
        {
            return null;
        }

        try
        {
            var dcmd = Load(dcmdUasset);
            var uimdPackage = FindPackage(dcmd, "DA_UIMD_");
            if (string.IsNullOrWhiteSpace(uimdPackage))
            {
                return null;
            }

            var uimdUasset = PackageToUasset(uimdPackage);
            var icons = File.Exists(uimdUasset) ? ReadIcons(Load(uimdUasset)) : Icons.Empty;
            return new Donor(
                UnrealPathUtil.NormalizePackagePath(dcmdTemplate.PackagePath),
                dcmdUasset,
                UnrealPathUtil.NormalizePackagePath(playableTemplate?.PackagePath ?? ""),
                UnrealPathUtil.NormalizePackagePath(cutsceneTemplate?.PackagePath ?? ""),
                uimdPackage,
                uimdUasset,
                ReadGameplayTag(dcmd, "PawnTag"),
                ReadGameplayTag(dcmd, "ProgressTag"),
                icons);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveTemplateUasset(TemplateRecord template)
    {
        if (!string.IsNullOrWhiteSpace(template.Uasset) && File.Exists(template.Uasset))
        {
            return template.Uasset;
        }

        return PackageToUasset(template.PackagePath);
    }

    private static UAsset Load(string path)
    {
        var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
        var mappings = !string.IsNullOrWhiteSpace(mappingsPath) && File.Exists(mappingsPath)
            ? MappingsCache.Load(mappingsPath)
            : null;
        return new UAsset(path, EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
    }

    private static string FindPackage(UAsset asset, string assetPrefix)
    {
        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        return asset.GetNameMapIndexList()
            .Select(name => UnrealPathUtil.NormalizePackagePath(name.ToString()))
            .FirstOrDefault(package =>
                UnrealPathUtil.AssetName(package).StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) &&
                ExtractedPackagePathService.ResolvePackageUasset(contentRoot, package) is { } path &&
                File.Exists(path))
            ?? "";
    }

    private static Icons ReadIcons(UAsset asset)
    {
        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var paths = asset.GetNameMapIndexList()
            .Select(name => UnrealPathUtil.NormalizePackagePath(name.ToString()))
            .Where(path => path.Contains("T_UI_Icon", StringComparison.OrdinalIgnoreCase) &&
                           ExtractedPackagePathService.ResolvePackageUasset(contentRoot, path) is { } uasset &&
                           File.Exists(uasset))
            .ToList();

        string Pick(params string[] terms) => paths.FirstOrDefault(path =>
            terms.All(term => path.Contains(term, StringComparison.OrdinalIgnoreCase))) ?? "";

        return new Icons(
            Pick("IconChar", "Menu"),
            Pick("IconSuit"),
            Pick("IconChar", "Left"),
            Pick("IconChar", "Right"));
    }

    private static string ReadGameplayTag(UAsset asset, string propertyName)
    {
        var export = asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(candidate => candidate.Data.Any(property => property.Name.ToString() == propertyName));
        var property = export?.Data.OfType<StructPropertyData>()
            .FirstOrDefault(candidate => candidate.Name.ToString() == propertyName);
        return property?.Value.OfType<NamePropertyData>()
            .FirstOrDefault(candidate => candidate.Name.ToString() == "TagName")?.Value.ToString() ?? "";
    }

    private static string PackageToUasset(string packagePath)
    {
        return ExtractedPackagePathService.ResolvePackageUasset(
                   AppSettings.Current.EffectiveExtractedContentRoot(),
                   packagePath)
               ?? "";
    }
}
