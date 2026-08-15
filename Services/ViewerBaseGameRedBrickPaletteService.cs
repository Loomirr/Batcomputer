using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// One read-only tint preset from the game's native Red Brick payload. This service exists only for
/// the 3D viewer; it does not create, register, package, unlock, or otherwise mutate Red Bricks.
/// </summary>
internal sealed record ViewerBaseGameRedBrickDefinition(
    string Id,
    string DisplayName,
    string PrimaryColourRow,
    string SecondaryColourRow,
    string TertiaryColourRow);

internal sealed class ViewerBaseGameRedBrickCatalog
{
    public IReadOnlyList<ViewerBaseGameRedBrickDefinition> Definitions { get; init; } = [];
    public string Error { get; init; } = "";
    public bool IsAvailable => Definitions.Count > 0;
}

/// <summary>
/// Reads the shipped Red Brick colour combinations from an extracted base-game asset for local,
/// read-only character preview. Keeping this separate from mod authoring prevents the retired Red
/// Brick packaging/registry flow from returning accidentally.
/// </summary>
internal static class ViewerBaseGameRedBrickPaletteService
{
    private const string PayloadPackage = "/Game/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickData_Main";
    public const string RetocFilter = "Content/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickData_Main";
    private static readonly object Gate = new();
    private static string _cacheKey = "";
    private static ViewerBaseGameRedBrickCatalog? _cached;

    public static IReadOnlyCollection<ModelPreviewService.PreviewRedBrickTint> LoadPreviewTints()
    {
        var catalog = Load(
            AppSettings.Current.EffectiveExtractedContentRoot(),
            AppSettings.Current.EffectiveUsmapPath());
        return catalog.Definitions
            .Select(definition => new ModelPreviewService.PreviewRedBrickTint(
                definition.DisplayName,
                PreviewHex(definition.PrimaryColourRow),
                PreviewHex(definition.SecondaryColourRow),
                PreviewHex(definition.TertiaryColourRow)))
            .ToArray();
    }

    private static ViewerBaseGameRedBrickCatalog Load(string? extractedContentRoot, string? usmapPath)
    {
        var contentRoot = AppSettings.NormalizeContentRoot(extractedContentRoot ?? "");
        var assetPath = PackageToBase(contentRoot, PayloadPackage) + ".uasset";
        var key = assetPath + "|" + (usmapPath ?? "") + "|" +
                  (File.Exists(assetPath) ? File.GetLastWriteTimeUtc(assetPath).Ticks : 0) + "|" +
                  (!string.IsNullOrWhiteSpace(usmapPath) && File.Exists(usmapPath)
                      ? File.GetLastWriteTimeUtc(usmapPath).Ticks
                      : 0);
        lock (Gate)
        {
            if (_cached is not null && string.Equals(_cacheKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return _cached;
            }

            _cacheKey = key;
            _cached = Read(assetPath, usmapPath);
            return _cached;
        }
    }

    private static ViewerBaseGameRedBrickCatalog Read(string assetPath, string? usmapPath)
    {
        if (!File.Exists(assetPath))
        {
            return new ViewerBaseGameRedBrickCatalog
            {
                Error = "The native Red Brick palette has not been extracted.",
            };
        }
        if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
        {
            return new ViewerBaseGameRedBrickCatalog
            {
                Error = "A valid .usmap is needed to read the native Red Brick palette.",
            };
        }

        try
        {
            var asset = new UAsset(
                assetPath,
                EngineVersion.VER_UE5_6,
                MappingsCache.Load(usmapPath),
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var metadata = asset.Exports.OfType<NormalExport>()
                .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
                .FirstOrDefault(property => property.Name.ToString() == "MetaData")
                ?.Value.OfType<StructPropertyData>()
                .ToArray() ?? [];

            var definitions = metadata
                .Select(ReadDefinition)
                .Where(definition => definition is not null)
                .Cast<ViewerBaseGameRedBrickDefinition>()
                .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ViewerBaseGameRedBrickCatalog { Definitions = definitions };
        }
        catch (Exception ex)
        {
            return new ViewerBaseGameRedBrickCatalog
            {
                Error = "Could not read the native Red Brick palette: " + ex.Message.Split('\n')[0],
            };
        }
    }

    private static ViewerBaseGameRedBrickDefinition? ReadDefinition(StructPropertyData entry)
    {
        var effectTag = ReadNestedName(entry, "RedBrickEffectTag", "TagName");
        var tint = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == "CharacterTintData");
        if (string.IsNullOrWhiteSpace(effectTag) || tint is null)
        {
            return null;
        }

        var primary = ReadNestedName(tint, "PrimaryColourRowHandle", "RowName");
        var secondary = ReadNestedName(tint, "SecondaryColourRowHandle", "RowName");
        var tertiary = ReadNestedName(tint, "TertiaryColourRowHandle", "RowName");
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(secondary) ||
            string.IsNullOrWhiteSpace(tertiary))
        {
            return null;
        }

        var id = effectTag.Split('.').LastOrDefault()?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(id)
            ? null
            : new ViewerBaseGameRedBrickDefinition(id, DisplayNameFromId(id), primary, secondary, tertiary);
    }

    private static string ReadNestedName(StructPropertyData parent, string propertyName, string nestedName) =>
        parent.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == propertyName)?
            .Value.OfType<NamePropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == nestedName)?
            .Value.ToString() ?? "";

    private static string DisplayNameFromId(string id) =>
        System.Text.RegularExpressions.Regex.Replace(id, "([a-z])([A-Z])", "$1 $2");

    private static string PackageToBase(string contentRoot, string packagePath)
    {
        var relative = packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? packagePath[6..]
            : packagePath.TrimStart('/');
        return Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string PreviewHex(string? row) => (row ?? "") switch
    {
        "BrightRed" => "#C91A09",
        "BrightYellow" or "FireYellow" => "#F2CD37",
        "BrightBlue" or "TinyBlue" => "#0055BF",
        "MediumBlue" or "LightRoyalBlue" or "LightBlue" => "#5A93DB",
        "MediumRoyalBlue" => "#4C61DB",
        "DoveBlue" => "#7A9DB8",
        "EarthBlue" or "DarkRoyalBlue" => "#17336B",
        "NeonGreen" or "Spr.YellowishGreen" => "#C8E000",
        "BrightYel.Green" => "#B7D933",
        "EarthGreen" or "DarkGreen" => "#184632",
        "LightFadedGreen" => "#A4C639",
        "BrightOrange" or "FlameYel.Orange" => "#FE8A18",
        "NewDarkRed" => "#720E0F",
        "White" => "#F4F4F4",
        "Black" => "#111111",
        "DarkGrey" or "DarkStoneGrey" => "#595D60",
        "MedStoneGrey" or "LightGrey" => "#9BA19D",
        "Curry" or "WarmGold" => "#C78A26",
        "MediumLilac" or "Lilac" or "Lavender" => "#AC78BA",
        "LightPink" => "#F7B6C2",
        "LightYellow" => "#FBE696",
        "MediumReddishViolet" or "BrightRed.Violet" => "#8E2F7A",
        _ => "#FFFFFF",
    };
}
