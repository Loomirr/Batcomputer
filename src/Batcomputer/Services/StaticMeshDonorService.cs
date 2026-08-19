using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Read-only evidence collection for the experimental static-mesh writer.</summary>
public sealed class StaticMeshDonorService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record Candidate(string Id, string Purpose, string PackagePath);

    private static readonly Candidate[] Candidates =
    [
        new("head-patch", "Smallest known one-section payload candidate.",
            "/Game/Characters/Attachments/Misc/HeadPatch/SM_RHeadPatch"),
        new("key-lime-pie", "Known simple game prop used as a larger one-section comparison.",
            "/Game/Animation/LEGOfig/Robin_DickGrayson/SyncedAnims/Counter/Props/SM_KeyLimePie"),
        new("nightwing-hair", "Known character head attachment with a static-component shell.",
            "/Game/Characters/Attachments/HAIR/Nightwing08/SM_Hair_Nightwing08"),
        new("damaged-cowl", "Character attachment with two material slots for a later section proof.",
            "/Game/Characters/Attachments/HAT/BatmanCowl_MoldedEyes_Damaged/SM_HAT_BatmanCowl_Damaged")
    ];

    public sealed class Report
    {
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        public string ContentRoot { get; set; } = "";
        public string PaksDirectory { get; set; } = "";
        public string UsmapPath { get; set; } = "";
        public string? RuntimeProbeError { get; set; }
        public List<Donor> Donors { get; set; } = [];
    }

    public sealed class Donor
    {
        public string Id { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string PackagePath { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
        public string UassetPath { get; set; } = "";
        public long UassetBytes { get; set; }
        public long UexpBytes { get; set; }
        public long UbulkBytes { get; set; }
        public string UassetSha256 { get; set; } = "";
        public string UexpSha256 { get; set; } = "";
        public string UbulkSha256 { get; set; } = "";
        public bool UassetApiParsed { get; set; }
        public bool? UassetRoundTripByteEqual { get; set; }
        public string? UassetRoundTripError { get; set; }
        public List<string> ExportClasses { get; set; } = [];
        public int StaticMaterialSlots { get; set; }
        public bool HasExtendedBounds { get; set; }
        public bool HasBodySetupExport { get; set; }
        public bool HasNavCollisionExport { get; set; }
        public long StaticMeshSerialOffset { get; set; }
        public long StaticMeshSerialSize { get; set; }
        public int LodCount { get; set; }
        public int Lod0Vertices { get; set; }
        public int Lod0Sections { get; set; }
    }

    public Report CreateReport(string contentRoot, string paksDirectory, string usmapPath, string outputDirectory)
    {
        var report = new Report
        {
            ContentRoot = AppSettings.NormalizeContentRoot(contentRoot),
            PaksDirectory = paksDirectory,
            UsmapPath = usmapPath
        };

        Usmap? mappings = null;
        if (File.Exists(usmapPath))
        {
            mappings = MappingsCache.Load(usmapPath);
        }

        DefaultFileProvider? provider = null;
        try
        {
            if (!Directory.Exists(paksDirectory))
            {
                throw new DirectoryNotFoundException("Paks directory was not found.");
            }
            if (!File.Exists(usmapPath))
            {
                throw new FileNotFoundException("Mappings file was not found.", usmapPath);
            }

            provider = new DefaultFileProvider(
                paksDirectory,
                BaseGamePakSource.ShippedContainerSearchOption,
                versions: new VersionContainer(EGame.GAME_UE5_6),
                pathComparer: StringComparer.OrdinalIgnoreCase);
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey("0x0000000000000000000000000000000000000000000000000000000000000000"));
        }
        catch (Exception ex)
        {
            report.RuntimeProbeError = ex.Message;
        }

        foreach (var candidate in Candidates)
        {
            report.Donors.Add(InspectCandidate(candidate, report.ContentRoot, mappings, provider, outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "static-mesh-donor-report.json"),
            JsonSerializer.Serialize(report, JsonOptions));
        return report;
    }

    private static Donor InspectCandidate(
        Candidate candidate,
        string contentRoot,
        Usmap? mappings,
        DefaultFileProvider? provider,
        string outputDirectory)
    {
        var donor = new Donor
        {
            Id = candidate.Id,
            Purpose = candidate.Purpose,
            PackagePath = candidate.PackagePath
        };

        try
        {
            var assetBase = PackageToBasePath(contentRoot, candidate.PackagePath);
            donor.UassetPath = assetBase + ".uasset";
            ReadFile(assetBase + ".uasset", out var uassetBytes, out var uassetHash);
            ReadFile(assetBase + ".uexp", out var uexpBytes, out var uexpHash);
            ReadFile(assetBase + ".ubulk", out var ubulkBytes, out var ubulkHash);
            donor.UassetBytes = uassetBytes;
            donor.UassetSha256 = uassetHash;
            donor.UexpBytes = uexpBytes;
            donor.UexpSha256 = uexpHash;
            donor.UbulkBytes = ubulkBytes;
            donor.UbulkSha256 = ubulkHash;
            if (donor.UassetBytes == 0)
            {
                throw new FileNotFoundException("Extracted donor .uasset was not found.", donor.UassetPath);
            }

            var asset = new UAsset(donor.UassetPath, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            donor.UassetApiParsed = true;
            donor.ExportClasses = asset.Exports
                .Select(ExportClassName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var staticExport = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(export => ExportClassName(export).Contains("StaticMesh", StringComparison.OrdinalIgnoreCase));
            if (staticExport is not null)
            {
                donor.StaticMeshSerialOffset = staticExport.SerialOffset;
                donor.StaticMeshSerialSize = staticExport.SerialSize;
                var staticMaterials = staticExport.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(property => property.Name.ToString().Equals("StaticMaterials", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                donor.StaticMaterialSlots = staticMaterials?.Cast<object>().Count() ?? 0;
                donor.HasExtendedBounds = staticExport.Data.Any(property =>
                    property.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
            }
            donor.HasBodySetupExport = donor.ExportClasses.Any(name => name.Contains("BodySetup", StringComparison.OrdinalIgnoreCase));
            donor.HasNavCollisionExport = donor.ExportClasses.Any(name => name.Contains("NavCollision", StringComparison.OrdinalIgnoreCase));
            RoundTripUasset(donor, mappings, outputDirectory);

            if (provider?.LoadPackageObject(candidate.PackagePath) is UStaticMesh mesh && mesh.TryConvert(out var converted))
            {
                donor.LodCount = converted.LODs.Count;
                if (converted.LODs.Count > 0)
                {
                    var lod0 = converted.LODs[0];
                    donor.Lod0Vertices = lod0.NumVerts;
                    donor.Lod0Sections = lod0.Sections?.Value?.Length ?? 0;
                }
            }
            else if (provider is not null)
            {
                throw new InvalidOperationException("Mounted package did not resolve to a convertible UStaticMesh.");
            }

            donor.Status = "ok";
        }
        catch (Exception ex)
        {
            donor.Status = "error";
            donor.Error = ex.Message;
        }

        return donor;
    }

    private static void RoundTripUasset(Donor donor, Usmap? mappings, string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, ".static-mesh-roundtrip-" + Guid.NewGuid().ToString("N") + ".uasset");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var original = File.ReadAllBytes(donor.UassetPath);
            var asset = new UAsset(donor.UassetPath, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            asset.Write(path);
            donor.UassetRoundTripByteEqual = original.AsSpan().SequenceEqual(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            donor.UassetRoundTripByteEqual = null;
            donor.UassetRoundTripError = ex.Message;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string ExportClassName(Export export) =>
        export.GetExportClassType().Value?.ToString() ?? export.GetType().Name;

    private static string PackageToBasePath(string contentRoot, string packagePath)
    {
        const string gamePrefix = "/Game/";
        if (!packagePath.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected a /Game package path.", nameof(packagePath));
        }
        return Path.Combine(contentRoot, packagePath[gamePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static void ReadFile(string path, out long bytes, out string sha256)
    {
        if (!File.Exists(path))
        {
            bytes = 0;
            sha256 = "";
            return;
        }

        var data = File.ReadAllBytes(path);
        bytes = data.LongLength;
        sha256 = Convert.ToHexString(SHA256.HashData(data));
    }
}
