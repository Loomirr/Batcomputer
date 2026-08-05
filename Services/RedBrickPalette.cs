using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal sealed record BaseGameRedBrickDefinition(
    string Id,
    string DisplayName,
    string PrimaryColourRow,
    string SecondaryColourRow,
    string TertiaryColourRow);

internal sealed class BaseGameRedBrickCatalog
{
    public IReadOnlyList<BaseGameRedBrickDefinition> Definitions { get; init; } = [];
    public IReadOnlyList<string> ColourRows { get; init; } = [];
    public string Error { get; init; } = "";
    public bool IsAvailable => Definitions.Count > 0;
}

/// <summary>Reads the shipped Red Brick tint combinations from the extracted game asset.</summary>
internal static class BaseGameRedBrickCatalogService
{
    private const string PayloadPackage = "/Game/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickData_Main";
    private static readonly object Gate = new();
    private static string _cacheKey = "";
    private static BaseGameRedBrickCatalog? _cached;

    public static BaseGameRedBrickCatalog LoadCurrent() => Load(
        AppSettings.Current.EffectiveExtractedContentRoot(),
        AppSettings.Current.EffectiveUsmapPath());

    public static BaseGameRedBrickCatalog Load(string? extractedContentRoot, string? usmapPath)
    {
        var contentRoot = AppSettings.NormalizeContentRoot(extractedContentRoot ?? "");
        var assetPath = PackageToBase(contentRoot, PayloadPackage) + ".uasset";
        var key = assetPath + "|" + (usmapPath ?? "") + "|" +
            (File.Exists(assetPath) ? File.GetLastWriteTimeUtc(assetPath).Ticks : 0) + "|" +
            (!string.IsNullOrWhiteSpace(usmapPath) && File.Exists(usmapPath) ? File.GetLastWriteTimeUtc(usmapPath).Ticks : 0);
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

    private static BaseGameRedBrickCatalog Read(string assetPath, string? usmapPath)
    {
        if (!File.Exists(assetPath))
        {
            return new BaseGameRedBrickCatalog
            {
                Error = "The native Red Brick data has not been extracted yet.",
            };
        }
        if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
        {
            return new BaseGameRedBrickCatalog
            {
                Error = "A valid .usmap is needed to read the native Red Brick data.",
            };
        }

        try
        {
            var mappings = MappingsCache.Load(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var metadata = asset.Exports.OfType<NormalExport>()
                .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
                .FirstOrDefault(property => property.Name.ToString() == "MetaData")
                ?.Value.OfType<StructPropertyData>()
                .ToArray() ?? [];

            var definitions = metadata
                .Select(ReadDefinition)
                .Where(definition => definition is not null)
                .Cast<BaseGameRedBrickDefinition>()
                .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rows = definitions
                .SelectMany(definition => new[]
                {
                    definition.PrimaryColourRow,
                    definition.SecondaryColourRow,
                    definition.TertiaryColourRow,
                })
                .Where(row => !string.IsNullOrWhiteSpace(row))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(row => row, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new BaseGameRedBrickCatalog { Definitions = definitions, ColourRows = rows };
        }
        catch (Exception ex)
        {
            return new BaseGameRedBrickCatalog
            {
                Error = "Could not read the native Red Brick data: " + ex.Message.Split('\n')[0],
            };
        }
    }

    private static BaseGameRedBrickDefinition? ReadDefinition(StructPropertyData entry)
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
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(secondary) || string.IsNullOrWhiteSpace(tertiary))
        {
            return null;
        }

        var id = effectTag.Split('.').LastOrDefault()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new BaseGameRedBrickDefinition(id, DisplayNameFromId(id), primary, secondary, tertiary);
    }

    private static string ReadNestedName(StructPropertyData parent, string propertyName, string nestedName) =>
        parent.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == propertyName)?
            .Value.OfType<NamePropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == nestedName)?
            .Value.ToString() ?? "";

    private static string DisplayNameFromId(string id)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(id, "([a-z])([A-Z])", "$1 $2");
        return words.Replace("90s", "90s", StringComparison.Ordinal);
    }

    private static string PackageToBase(string contentRoot, string packagePath)
    {
        var relative = packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? packagePath[6..]
            : packagePath.TrimStart('/');
        return Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}

internal static class RedBrickPalette
{
    private static readonly string[] FallbackRows =
    [
        "Black", "White", "BrightRed", "BrightYellow", "BrightBlue", "DarkStoneGrey",
    ];

    public static IReadOnlyList<string> CurrentRows
    {
        get
        {
            var catalog = BaseGameRedBrickCatalogService.LoadCurrent();
            return catalog.ColourRows.Count > 0 ? catalog.ColourRows : FallbackRows;
        }
    }

    public static bool Contains(string? row) => CurrentRows.Contains(row ?? "", StringComparer.OrdinalIgnoreCase);

    public static string PreviewHex(string? row) => (row ?? "") switch
    {
        "BrightRed" => "#C91A09",
        "BrightYellow" or "FireYellow" => "#F2CD37",
        "BrightBlue" or "TinyBlue" => "#0055BF",
        "MediumBlue" or "LightRoyalBlue" or "LightBlue" => "#5A93DB",
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
        "MediumReddishViolet" or "BrightRed.Violet" => "#8E2F7A",
        _ => "#FFFFFF",
    };
}
