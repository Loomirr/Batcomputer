using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Read-only browser for the extracted character library. It deliberately does not participate in
/// suit generation or packaging: the service only enumerates files and lazily parses the asset the
/// user selected in the Research browser.
/// </summary>
public sealed class CharacterResearchService
{
    private static readonly string[] InterestingWords =
    {
        "colormask", "colourmask", "pawntag", "progresstag", "uimd", "menuactor", "dprd",
        "archetype", "equipment", "ability", "skeletalmesh", "staticmesh", "headstud", "torso2",
        "hair", "cape", "face", "cowl", "costume", "component", "parent"
    };

    private string? _indexedRoot;
    private List<ResearchAssetRecord> _assets = new();

    public IReadOnlyList<ResearchAssetRecord> GetAssets(string contentRoot, string? type, string? search)
    {
        var root = NormalizeRoot(contentRoot);
        if (!string.Equals(_indexedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _indexedRoot = root;
            _assets = EnumerateAssets(root);
        }

        IEnumerable<ResearchAssetRecord> query = _assets;
        if (type is not null && !type.Equals("Character assets", StringComparison.OrdinalIgnoreCase))
        {
            if (type.Equals("Playable / cutscene", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(IsPlayableOrCutscene);
            }
            else if (type.Equals("Materials / ColorMask", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(IsMaterialOrColorMask);
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(asset =>
                asset.AssetName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                asset.PackagePath.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                asset.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }

    public ResearchAssetInspection Inspect(ResearchAssetRecord record)
    {
        try
        {
            var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
            Usmap? mappings = !string.IsNullOrWhiteSpace(mappingsPath) && File.Exists(mappingsPath)
                ? MappingsCache.Load(mappingsPath)
                : null;

            var asset = new UAsset(
                record.UassetPath,
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            using var document = JsonDocument.Parse(asset.SerializeJson(false));
            var references = new List<string>();
            CollectInterestingValues(document.RootElement, references);
            var distinctReferences = references
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();

            var file = new FileInfo(record.UassetPath);
            var summary = new List<string>
            {
                $"Asset: {record.AssetName}",
                $"Package: {record.PackagePath}",
                $"Class/export count: {asset.Exports.Count}",
                $"Import count: {asset.Imports.Count}",
                $"UAsset: {FormatBytes(file.Length)}",
                $"UEXP: {(record.HasUexp ? FormatBytes(new FileInfo(record.UexpPath!).Length) : "missing")}",
                $"Mappings: {(mappings is null ? "not loaded" : "loaded")}",
            };

            var exports = asset.Exports
                .Select((export, index) =>
                {
                    var className = export.GetExportClassType().Value?.ToString() ?? "unknown class";
                    return $"{index + 1}. {export.ObjectName}  [{className}]";
                })
                .Take(120)
                .ToList();

            var imports = asset.Imports
                .Select((import, index) => $"{index + 1}. {import.ObjectName}")
                .Take(120)
                .ToList();

            return new ResearchAssetInspection
            {
                Record = record,
                Succeeded = true,
                SummaryLines = summary,
                ExportLines = exports,
                ImportLines = imports,
                InterestingReferences = distinctReferences,
                Note = distinctReferences.Count == 0
                    ? "No tagged references were found in the serialized properties."
                    : "Tagged references are a heuristic research aid, not an edit instruction."
            };
        }
        catch (Exception ex)
        {
            return new ResearchAssetInspection
            {
                Record = record,
                Succeeded = false,
                SummaryLines = new[]
                {
                    $"Asset: {record.AssetName}",
                    $"Package: {record.PackagePath}",
                    "UAssetAPI could not parse this asset with the current mappings."
                },
                Note = ex.GetType().Name + ": " + ex.Message
            };
        }
    }

    private static List<ResearchAssetRecord> EnumerateAssets(string contentRoot)
    {
        if (!Directory.Exists(contentRoot))
        {
            return new List<ResearchAssetRecord>();
        }

        // DLC character packages live below AdditionalContent/.../Characters,
        // whereas the base game uses Content/Characters. Scan each logical
        // Characters root so the research browser mirrors the base picker and
        // part index without turning into a full-game asset browser.
        return CharacterContentRootService.Enumerate(contentRoot)
            .SelectMany(charactersRoot => Directory.EnumerateFiles(
                charactersRoot,
                "*.uasset",
                SearchOption.AllDirectories))
            .Select(path => CreateRecord(contentRoot, path))
            .OrderBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ResearchAssetRecord CreateRecord(string contentRoot, string uassetPath)
    {
        var relative = Path.GetRelativePath(contentRoot, uassetPath).Replace('\\', '/');
        var withoutExtension = relative.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            ? relative[..^6]
            : relative;
        var package = "/Game/" + withoutExtension.TrimStart('/');
        var uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
        var ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
        return new ResearchAssetRecord
        {
            AssetName = Path.GetFileNameWithoutExtension(uassetPath),
            PackagePath = package,
            RelativePath = relative,
            UassetPath = uassetPath,
            UexpPath = File.Exists(uexpPath) ? uexpPath : null,
            UbulkPath = File.Exists(ubulkPath) ? ubulkPath : null,
        };
    }

    private static bool IsPlayableOrCutscene(ResearchAssetRecord asset)
    {
        var name = asset.AssetName;
        return name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Playable", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Cutscene", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("DA_DCMD", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("DA_UIMD", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("DPRD", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Archetype", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMaterialOrColorMask(ResearchAssetRecord asset)
    {
        var name = asset.AssetName;
        return name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("M_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ColorMask", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ColourMask", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectInterestingValues(JsonElement element, List<string> results, string propertyName = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectInterestingValues(property.Value, results, property.Name);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectInterestingValues(child, results, propertyName);
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var combined = propertyName + " " + value;
                if (InterestingWords.Any(word => combined.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(string.IsNullOrWhiteSpace(propertyName)
                        ? value
                        : propertyName + ": " + value);
                }
                break;
        }
    }

    private static string NormalizeRoot(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return root.TrimEnd(Path.DirectorySeparatorChar); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }

    public sealed class ResearchAssetRecord
    {
        public string AssetName { get; init; } = "";
        public string PackagePath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string UassetPath { get; init; } = "";
        public string? UexpPath { get; init; }
        public string? UbulkPath { get; init; }
        public bool HasUexp => !string.IsNullOrWhiteSpace(UexpPath);
    }

    public sealed class ResearchAssetInspection
    {
        public ResearchAssetRecord Record { get; init; } = new();
        public bool Succeeded { get; init; }
        public IReadOnlyList<string> SummaryLines { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExportLines { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ImportLines { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InterestingReferences { get; init; } = Array.Empty<string>();
        public string Note { get; init; } = "";
    }
}
