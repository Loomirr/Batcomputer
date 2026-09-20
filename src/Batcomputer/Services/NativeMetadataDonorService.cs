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
        => TryRead(dcmdTemplate, playableTemplate, cutsceneTemplate, out _);

    public static Donor? TryRead(TemplateRecord? dcmdTemplate, TemplateRecord? playableTemplate,
        TemplateRecord? cutsceneTemplate, out string error)
    {
        error = "";
        if (dcmdTemplate is null || string.IsNullOrWhiteSpace(dcmdTemplate.PackagePath))
        {
            error = "No native metadata donor package was selected.";
            return null;
        }

        // Projects keep the donor package path, but a game refresh can replace the
        // extracted directory that originally supplied the file. Always resolve from
        // the active dump so old and new game builds cannot be mixed.
        var dcmdUasset = ResolveTemplateUasset(dcmdTemplate);
        if (string.IsNullOrWhiteSpace(dcmdUasset) || !File.Exists(dcmdUasset))
        {
            error = $"Missing donor {dcmdTemplate.PackagePath} in the active extraction: {AppSettings.Current.EffectiveExtractedContentRoot()}";
            return null;
        }

        try
        {
            var dcmd = Load(dcmdUasset);
            var uimdPackage = FindPackage(dcmd, "DA_UIMD_");
            if (string.IsNullOrWhiteSpace(uimdPackage))
            {
                error = $"Cannot resolve the UI metadata referenced by {dcmdTemplate.PackagePath} in the active extraction.";
                return null;
            }

            var uimdUasset = PackageToUasset(uimdPackage);
            var icons = File.Exists(uimdUasset) ? ReadIcons(Load(uimdUasset)) : Icons.Empty;
            // The DCMD is authoritative for its actor pair. Several shipped families do not use
            // predictable sibling names (for example RobinDickGrayson playables point at
            // BP_Robin_* or BP_DickGrayson_* cinematic actors). Using the picker-derived template
            // path here meant the generated DCMD could retain the donor CinematicsActor and the
            // game would silently fall back to the native/default suit in a cold cutscene.
            var serializedPlayable = ReadActorPackage(dcmd, "Pawn");
            var serializedCutscene = ReadActorPackage(dcmd, "CinematicsActor");
            return new Donor(
                UnrealPathUtil.NormalizePackagePath(dcmdTemplate.PackagePath),
                dcmdUasset,
                PreferSerializedActorPackage(serializedPlayable, playableTemplate?.PackagePath),
                PreferSerializedActorPackage(serializedCutscene, cutsceneTemplate?.PackagePath),
                uimdPackage,
                uimdUasset,
                ReadGameplayTag(dcmd, "PawnTag"),
                CanonicalProgressTag(ReadGameplayTag(dcmd, "ProgressTag")),
                icons);
        }
        catch (Exception ex)
        {
            error = $"Could not read {dcmdTemplate.PackagePath} ({dcmdUasset}) using mappings {AppSettings.Current.EffectiveUsmapPath()}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    internal static string CanonicalProgressTag(string? tag)
    {
        // DA_DCMD_Batman_Batman_Playable is a retired donor with the same localized
        // title/icons as TheBatman2025. PROG_Characters and the Batman_Unlock rule
        // define only the latter progression entry. Do not infer aliases by title.
        if (string.Equals(tag, "GameProgress.Definitions.Characters.Batman.Batman", StringComparison.OrdinalIgnoreCase))
            return "GameProgress.Definitions.Characters.Batman.TheBatman2025";
        // Build 1344350 retains Joker/Harley Default DCMDs with obsolete gates.
        // PROG_DLC_VMCharacters defines HTV/BTAS instead, matching the native
        // groups' DefaultCharacterVariant and those variants' own DCMDs. A missing
        // progress definition causes the roster to skip the suit, not just lock it.
        // Exact aliases only: do not rewrite other variants or custom unlocks.
        if (string.Equals(tag, "GameProgress.Definitions.Characters.Joker", StringComparison.OrdinalIgnoreCase))
            return "GameProgress.Definitions.Characters.Joker.HTV";
        if (string.Equals(tag, "GameProgress.Definitions.Characters.Harley.Default", StringComparison.OrdinalIgnoreCase))
            return "GameProgress.Definitions.Characters.Harley.BTAS";
        return tag ?? "";
    }

    /// <summary>
    /// Reads the exact cinematic Blueprint package authored on a native DCMD. This is used by the
    /// base picker before its filename heuristic because some character families deliberately use
    /// different playable and cutscene stems.
    /// </summary>
    internal static string TryReadCinematicsActorPackage(string? dcmdUasset)
    {
        if (string.IsNullOrWhiteSpace(dcmdUasset) || !File.Exists(dcmdUasset))
        {
            return "";
        }

        try
        {
            return ReadActorPackage(Load(dcmdUasset), "CinematicsActor");
        }
        catch
        {
            return "";
        }
    }

    internal static string PreferSerializedActorPackage(string? serializedPackage, string? templatePackage)
    {
        var serialized = UnrealPathUtil.NormalizePackagePath(serializedPackage ?? "");
        return !string.IsNullOrWhiteSpace(serialized)
            ? serialized
            : UnrealPathUtil.NormalizePackagePath(templatePackage ?? "");
    }

    private static string ResolveTemplateUasset(TemplateRecord template)
    {
        // Never mix an old saved donor with the new extraction's UI metadata/mappings.
        // A configured active extraction is authoritative, including missing packages.
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.EffectiveExtractedContentRoot()))
            return PackageToUasset(template.PackagePath);
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

    private static string ReadActorPackage(UAsset asset, string propertyName)
    {
        var reference = NativeAssetTextPatch.GetSoftReference(asset, propertyName);
        return UnrealPathUtil.NormalizePackagePath(reference?.PackageName ?? "");
    }

    private static string PackageToUasset(string packagePath)
    {
        return ExtractedPackagePathService.ResolvePackageUasset(
                   AppSettings.Current.EffectiveExtractedContentRoot(),
                   packagePath)
               ?? "";
    }
}
