using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>
/// Property-level patches for the native-suit localization fields on DCMD/UIMD assets.
/// Deliberately NOT name-map string replacement: several FText fields can
/// reference the same base StringTable, so a broad replace would corrupt unrelated text.
/// These edit the exact typed property (an FText StringTableEntry, or a GameplayTag's
/// inner TagName), and UAssetAPI reserializes on Write. Proven round-trip 2026-07-16.
/// </summary>
public static class NativeAssetTextPatch
{
    /// <summary>
    /// Repoints an FText property to a StringTable entry: sets HistoryType to
    /// StringTableEntry, TableId to the table's OBJECT path
    /// (e.g. "/Game/Mods/&lt;ModId&gt;/Localization/ST_&lt;ModId&gt;.ST_&lt;ModId&gt;")
    /// and the referenced key. Returns false if the property is absent.
    /// </summary>
    public static bool SetStringTableText(UAsset asset, string propName, string tableObjectPath, string key)
    {
        var ne = FindExportWithProp(asset, propName);
        if (ne is null)
        {
            return false;
        }
        var tp = ne.Data.OfType<TextPropertyData>().FirstOrDefault(p => p.Name.ToString() == propName);
        if (tp is null)
        {
            return false;
        }
        tp.HistoryType = TextHistoryType.StringTableEntry;
        tp.TableId = new FName(asset, tableObjectPath);
        tp.Value = new FString(key);
        return true;
    }

    /// <summary>
    /// Sets a GameplayTag struct property's inner TagName. Returns false if the property
    /// (or its TagName inner) is absent.
    /// </summary>
    public static bool SetGameplayTag(UAsset asset, string propName, string tag)
    {
        var ne = FindExportWithProp(asset, propName);
        if (ne is null)
        {
            return false;
        }
        var sp = ne.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == propName);
        var inner = sp?.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "TagName");
        if (inner is null)
        {
            return false;
        }
        inner.Value = new FName(asset, tag);
        return true;
    }

    /// <summary>Reads a GameplayTag struct property without changing the asset.</summary>
    public static string? GetGameplayTag(UAsset asset, string propName)
    {
        var ne = FindExportWithProp(asset, propName);
        var sp = ne?.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == propName);
        var inner = sp?.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "TagName");
        return inner?.Value.ToString();
    }

    /// <summary>Repoints a top-level soft object property to a cooked game asset.</summary>
    public static bool SetSoftObject(UAsset asset, string propName, string packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var export = FindExportWithProp(asset, propName);
        var property = export?.Data.OfType<SoftObjectPropertyData>()
            .FirstOrDefault(item => item.Name.ToString() == propName);
        if (property is null)
        {
            return false;
        }

        property.Value = new FSoftObjectPath(
            new FTopLevelAssetPath(
                FName.FromString(asset, normalized),
                FName.FromString(asset, UnrealPathUtil.AssetName(normalized))),
            new FString(string.Empty));
        return true;
    }

    /// <summary>
    /// Repoints a soft object property, adding the property when the cooked donor omitted it
    /// because it held the class default. The fallback export must contain one of the supplied
    /// sibling properties, which keeps the new value on the same data-asset CDO rather than an
    /// unrelated export.
    /// </summary>
    internal static bool SetOrAddSoftObject(
        UAsset asset,
        string propName,
        string packagePath,
        string objectName,
        params string[] siblingPropertyNames)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        var export = FindExportWithProp(asset, propName);
        var property = export?.Data.OfType<SoftObjectPropertyData>()
            .FirstOrDefault(item => item.Name.ToString() == propName);
        if (export is not null && property is null)
        {
            // A same-named property with another serialized type is a schema mismatch, not a
            // default-omitted soft reference. Never append a duplicate with a conflicting type.
            return false;
        }
        if (property is null)
        {
            var siblingNames = siblingPropertyNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);
            export ??= asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(candidate => candidate.Data.Any(item =>
                    siblingNames.Contains(item.Name.ToString())));
            if (export is null)
            {
                return false;
            }

            // These are unversioned cooked assets. UAssetAPI keys the added value by the class
            // schema, so appending a default-omitted property is safe as long as the donor class
            // really owns it (the sibling export selection above establishes that for UIMDs).
            property = new SoftObjectPropertyData(FName.FromString(asset, propName));
            export.Data.Add(property);
        }

        property.Value = new FSoftObjectPath(
            new FTopLevelAssetPath(
                FName.FromString(asset, normalized),
                FName.FromString(asset, objectName)),
            new FString(string.Empty));
        return true;
    }

    /// <summary>Reads a top-level soft reference without changing the asset.</summary>
    public static (string PackageName, string AssetName)? GetSoftReference(UAsset asset, string propName)
    {
        var export = FindExportWithProp(asset, propName);
        var property = export?.Data.OfType<SoftObjectPropertyData>()
            .FirstOrDefault(item => item.Name.ToString() == propName);
        return property is null
            ? null
            : (property.Value.AssetPath.PackageName.ToString(), property.Value.AssetPath.AssetName.ToString());
    }

    private static NormalExport? FindExportWithProp(UAsset asset, string propName) =>
        asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == propName));
}
