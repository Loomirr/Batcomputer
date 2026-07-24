using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Generates a mod-owned StringTable (<c>ST_&lt;ModId&gt;</c>) by cloning the shipped
/// <c>ST_TagNames</c> - a flat <c>KeysToEntries</c> table with an empty
/// <c>KeysToMetaData</c>, the exact shape we need. We keep only its STRUCTURE: the
/// namespace is replaced with the ModId (so two mods never collide in the shared "UI"
/// namespace) and every entry is replaced with the mod's own <c>Suit.&lt;id&gt;.*</c> keys.
///
/// One table per mod holds every suit's Name/Description/LockedDescription. DCMD/UIMD
/// text fields are then repointed at this table by DcmdGenService/UimdGenService.
///
/// UAssetAPI note: <c>StringTableExport.Table</c> is an <c>FStringTable</c>, which is a
/// <c>Dictionary&lt;FString,FString&gt;</c> with an added <c>TableNamespace</c> field -
/// so we mutate it directly and UAssetAPI reserializes (recomputing name hashes) on Write.
/// </summary>
public sealed class StringTableGenService
{
    // The shipped donor cloned for structure. Lives under the extracted content root.
    private const string DonorPackage = "Localization/StringTables/ST_TagNames";
    private const string DonorAsset = "ST_TagNames";
    private const string DonorPackagePath = "/Game/Localization/StringTables/ST_TagNames";

    public sealed class GenResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string OutputUasset { get; set; } = "";
        public string TableNamespace { get; set; } = "";
        public string PackagePath { get; set; } = "";
        public int EntryCount { get; set; }
    }

    public string ProjectRoot { get; }

    public StringTableGenService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    /// <summary>Resolves the donor ST_TagNames .uasset under the extracted content root.</summary>
    public static string ResolveDonorPath()
    {
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        return Path.Combine(root, DonorPackage.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    /// <summary>The generated table's object path, e.g. "/Game/Mods/&lt;ModId&gt;/Localization/ST_&lt;ModId&gt;.ST_&lt;ModId&gt;".</summary>
    public static string ObjectPathFor(string modId) =>
        $"/Game/Mods/{modId}/Localization/ST_{modId}.ST_{modId}";

    /// <summary>The generated table's package path (no object suffix).</summary>
    public static string PackagePathFor(string modId) =>
        $"/Game/Mods/{modId}/Localization/ST_{modId}";

    /// <summary>
    /// Writes ST_&lt;ModId&gt; to <paramref name="outputBasePath"/> (no extension) with the
    /// given namespace (usually the ModId) and key→value entries.
    /// </summary>
    public GenResult Generate(
        string outputBasePath,
        string modId,
        IReadOnlyDictionary<string, string> entries)
    {
        var result = new GenResult();
        try
        {
            var donor = ResolveDonorPath();
            if (!File.Exists(donor))
            {
                result.Status = "missing-donor";
                result.Error = $"Donor StringTable not found: {donor}. Run the game-asset refresh (it now extracts ST_TagNames).";
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputBasePath)!);
            var donorNoExt = Path.Combine(
                Path.GetDirectoryName(donor)!,
                Path.GetFileNameWithoutExtension(donor));
            CopyIfExists(donorNoExt + ".uasset", outputBasePath + ".uasset");
            CopyIfExists(donorNoExt + ".uexp", outputBasePath + ".uexp");

            var newAsset = $"ST_{modId}";
            var newPackagePath = PackagePathFor(modId);

            var asset = new UAsset(
                outputBasePath + ".uasset",
                EngineVersion.VER_UE5_6,
                LoadMappings(),
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            // Package identity: FolderName is the package path retoc's to-zen reads.
            asset.FolderName = new FString(newPackagePath);

            // Name-map identity: whole-entry replacement of the donor's asset name and
            // package path (mirrors DcmdGenService's approach - exact-match, no substring
            // overlap since these are standalone FName entries).
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DonorPackagePath] = newPackagePath,
                [DonorAsset] = newAsset,
            };
            var nameMap = asset.GetNameMapIndexList();
            for (var i = 0; i < nameMap.Count; i++)
            {
                if (map.TryGetValue(nameMap[i].ToString(), out var patched))
                {
                    asset.SetNameReference(i, new FString(patched));
                }
            }

            // Payload: namespace + entries.
            var ste = asset.Exports.OfType<StringTableExport>().FirstOrDefault()
                ?? throw new InvalidOperationException("Cloned asset has no StringTableExport.");
            ste.Table.TableNamespace = new FString(modId);
            ste.Table.Clear();
            foreach (var kv in entries)
            {
                ste.Table.Add(new FString(kv.Key), new FString(kv.Value ?? ""));
            }

            asset.Write(outputBasePath + ".uasset");

            result.OutputUasset = outputBasePath + ".uasset";
            result.TableNamespace = modId;
            result.PackagePath = newPackagePath;
            result.EntryCount = entries.Count;
            result.Status = "created";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    private static Usmap? LoadMappings()
    {
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap)
            ? MappingsCache.Load(usmap)
            : null;
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }
}
