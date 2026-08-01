using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

public sealed class StaticMeshClosedCubeProbeService
{
    private const string DonorPackagePath = "/Game/Characters/Attachments/HAIR/Nightwing08/SM_Hair_Nightwing08";
    private const string DonorAssetName = "SM_Hair_Nightwing08";
    private const int VertexCount = 486;
    private const int PositionMetadataOffset = 0x10D;
    private const int PositionBulkHeaderOffset = 0x115;
    private const int PositionDataOffset = 0x11D;
    private const int PositionStride = 12;
    private const int TangentBulkHeaderOffset = 0x17F7;
    private const int TangentDataOffset = 0x17FF;
    private const int TangentStride = 8;
    private const int UvBulkHeaderOffset = 0x272F;
    private const int UvDataOffset = 0x2737;
    private const int UvStride = 4;
    private const int IndexDataBytes = 2520;
    private const int IndexCount = IndexDataBytes / sizeof(ushort);
    private static readonly int[] IndexDataOffsets = [0x2EE5, 0x38DD];
    private static readonly int[] RenderBoundsOffsets = [0x33, 0xC3, 0x4629];
    private const CustomSerializationFlags NameMapOnlyPatchFlags =
        CustomSerializationFlags.SkipPreloadDependencyLoading;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed class Request
    {
        public string ExtractedContentRoot { get; set; } = "";
        public string UsmapPath { get; set; } = "";
        public string OutputContentRoot { get; set; } = "";
        public string OutputPackagePath { get; set; } = "";
        public float CubeScale { get; set; } = 2.5f;
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string SourcePackagePath { get; set; } = DonorPackagePath;
        public string OutputPackagePath { get; set; } = "";
        public string OutputUasset { get; set; } = "";
        public string OutputUexp { get; set; } = "";
        public string OutputUbulk { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public int VertexCount { get; set; } = StaticMeshClosedCubeProbeService.VertexCount;
        public int FaceVertexCount { get; set; }
        public int IndexCount { get; set; }
        public List<int> UpdatedIndexDataOffsets { get; set; } = [];
        public List<int> UpdatedRenderBoundsOffsets { get; set; } = [];
        public float CubeHalfExtent { get; set; }
        public string UexpSha256Before { get; set; } = "";
        public string UexpSha256After { get; set; } = "";
        public List<string> Log { get; set; } = [];
    }

    private readonly record struct Vertex(float X, float Y, float Z);
    private readonly record struct Uv(float U, float V);
    private readonly record struct FaceVertex(Vertex Position, Vertex Tangent, Vertex Normal, Uv Uv);
    private readonly record struct Bounds(Vertex SourceCenter, Vertex Center, float HalfExtent);

    public Result CreateClosedCubeProbe(Request request)
    {
        var result = new Result { OutputPackagePath = UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath) };
        try
        {
            if (request.CubeScale is < 0.5f or > 4f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.CubeScale), "Cube scale must be between 0.5 and 4.");
            }
            if (!result.OutputPackagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Probe output must be under /Game/Mods/.");
            }

            var contentRoot = AppSettings.NormalizeContentRoot(request.ExtractedContentRoot);
            var sourceBase = PackagePathToBasePath(contentRoot, DonorPackagePath);
            var outputBase = PackagePathToBasePath(request.OutputContentRoot, result.OutputPackagePath);
            var sourceUasset = sourceBase + ".uasset";
            var sourceUexp = sourceBase + ".uexp";
            var sourceUbulk = sourceBase + ".ubulk";
            if (!File.Exists(sourceUasset) || !File.Exists(sourceUexp) || !File.Exists(sourceUbulk))
            {
                throw new FileNotFoundException("The Nightwing static-mesh donor needs its .uasset, .uexp, and .ubulk files in extracted Content.", sourceUasset);
            }
            if (!File.Exists(request.UsmapPath))
            {
                throw new FileNotFoundException("Mappings file was not found.", request.UsmapPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
            result.OutputUasset = outputBase + ".uasset";
            result.OutputUexp = outputBase + ".uexp";
            result.OutputUbulk = outputBase + ".ubulk";
            File.Copy(sourceUasset, result.OutputUasset, overwrite: true);
            File.Copy(sourceUexp, result.OutputUexp, overwrite: true);
            File.Copy(sourceUbulk, result.OutputUbulk, overwrite: true);

            var mappings = MappingsCache.Load(request.UsmapPath);
            RewriteIdentity(result.OutputUasset, result.OutputPackagePath, mappings, result);

            var asset = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var mesh = FindStaticMeshExport(asset)
                ?? throw new InvalidOperationException("The cloned donor does not contain a StaticMesh export.");
            var serialOffset = checked((int)(mesh.SerialOffset - new FileInfo(result.OutputUasset).Length));
            var bounds = ReadBounds(mesh, request.CubeScale);
            PatchExtendedBounds(mesh, bounds, result);
            asset.Write(result.OutputUasset);

            var rewrittenAsset = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var rewrittenMesh = FindStaticMeshExport(rewrittenAsset)
                ?? throw new InvalidOperationException("The rewritten donor does not contain a StaticMesh export.");
            serialOffset = checked((int)(rewrittenMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));

            var uexp = File.ReadAllBytes(result.OutputUexp);
            result.UexpSha256Before = Hash(uexp);
            var faces = ClosedCubeFaceVertices(bounds);
            PatchRenderBounds(uexp, serialOffset, bounds, result);
            PatchPositions(uexp, serialOffset, faces);
            PatchTangents(uexp, serialOffset, faces);
            PatchUvs(uexp, serialOffset, faces);
            PatchIndices(uexp, serialOffset, result);
            result.UexpSha256After = Hash(uexp);
            File.WriteAllBytes(result.OutputUexp, uexp);

            var validation = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var validationMesh = FindStaticMeshExport(validation)
                ?? throw new InvalidOperationException("UAssetAPI could not reopen the closed-cube mesh.");
            if (validation.FolderName.ToString() != result.OutputPackagePath ||
                !validationMesh.ObjectName.ToString().Equals(UnrealPathUtil.AssetName(result.OutputPackagePath), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated mesh identity did not persist.");
            }

            result.FaceVertexCount = faces.Length;
            result.IndexCount = IndexCount;
            result.Status = "created";
            result.Log.Add("Cloned the one-section Nightwing static-mesh donor and rewrote only its verified LOD0 buffers.");
            result.Log.Add("Wrote 24 face-specific vertices with independent tangents, normals, and UVs for a fully closed cube.");
            result.Log.Add("Zeroed the remaining verified LOD0 triangles so unused donor geometry cannot render.");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(result.OutputUasset))
            {
                result.ReportPath = Path.ChangeExtension(result.OutputUasset, ".closed-cube-probe-report.json");
                File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
            }
        }

        return result;
    }

    private static void RewriteIdentity(string uassetPath, string outputPackagePath, Usmap mappings, Result result)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
        asset.FolderName = new FString(outputPackagePath);
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        var replacements = new Dictionary<string, string>
        {
            [DonorPackagePath] = outputPackagePath,
            [DonorAssetName] = outputName
        };
        var changed = 0;
        var names = asset.GetNameMapIndexList();
        for (var i = 0; i < names.Count; i++)
        {
            var original = names[i].ToString();
            var updated = original;
            foreach (var replacement in replacements)
            {
                updated = updated.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            }
            if (updated == original)
            {
                continue;
            }
            asset.SetNameReference(i, new FString(updated));
            changed++;
        }
        asset.Write(uassetPath);
        result.Log.Add($"Patched package identity and {changed} matching name-map entries.");
    }

    private static Bounds ReadBounds(NormalExport mesh, float scale)
    {
        var bounds = mesh.Data.OfType<StructPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
        var origin = FindBoundsVector(bounds, "Origin")
            ?? throw new InvalidOperationException("The donor has no ExtendedBounds origin.");
        var extent = FindBoundsVector(bounds, "BoxExtent")
            ?? throw new InvalidOperationException("The donor has no ExtendedBounds extent.");
        var sourceCenter = new Vertex((float)origin.Value.X, (float)origin.Value.Y, (float)origin.Value.Z);
        var halfExtent = Math.Max((float)extent.Value.X, Math.Max((float)extent.Value.Y, (float)extent.Value.Z)) * scale;
        return new Bounds(sourceCenter, new Vertex(0, 0, 0), halfExtent);
    }

    private static void PatchExtendedBounds(NormalExport mesh, Bounds bounds, Result result)
    {
        var property = mesh.Data.OfType<StructPropertyData>().FirstOrDefault(item =>
            item.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
        var origin = FindBoundsVector(property, "Origin");
        var extent = FindBoundsVector(property, "BoxExtent");
        var radius = property?.Value.OfType<DoublePropertyData>().FirstOrDefault(item =>
            item.Name.ToString().Equals("SphereRadius", StringComparison.OrdinalIgnoreCase));
        if (origin is null || extent is null || radius is null)
        {
            throw new InvalidOperationException("The donor ExtendedBounds layout changed.");
        }
        origin.Value = new FVector(bounds.Center.X, bounds.Center.Y, bounds.Center.Z);
        extent.Value = new FVector(bounds.HalfExtent, bounds.HalfExtent, bounds.HalfExtent);
        radius.Value = Math.Sqrt(3d * bounds.HalfExtent * bounds.HalfExtent);
        result.CubeHalfExtent = bounds.HalfExtent;
    }

    private static void PatchRenderBounds(byte[] uexp, int serialOffset, Bounds bounds, Result result)
    {
        foreach (var relativeOffset in RenderBoundsOffsets)
        {
            var offset = serialOffset + relativeOffset;
            if (offset < 0 || offset + 7 * sizeof(double) > uexp.Length)
            {
                throw new InvalidOperationException("The donor render-bounds layout changed.");
            }
            var matchesSourceCenter = NearlyEqual((float)ReadDouble(uexp, offset), bounds.SourceCenter.X) &&
                                      NearlyEqual((float)ReadDouble(uexp, offset + 8), bounds.SourceCenter.Y) &&
                                      NearlyEqual((float)ReadDouble(uexp, offset + 16), bounds.SourceCenter.Z);
            var matchesTargetCenter = NearlyEqual((float)ReadDouble(uexp, offset), bounds.Center.X) &&
                                      NearlyEqual((float)ReadDouble(uexp, offset + 8), bounds.Center.Y) &&
                                      NearlyEqual((float)ReadDouble(uexp, offset + 16), bounds.Center.Z);
            if (!matchesSourceCenter && !matchesTargetCenter)
            {
                throw new InvalidOperationException("The donor render-bounds layout changed.");
            }
            WriteDouble(uexp, offset, bounds.Center.X);
            WriteDouble(uexp, offset + 8, bounds.Center.Y);
            WriteDouble(uexp, offset + 16, bounds.Center.Z);
            WriteDouble(uexp, offset + 24, bounds.HalfExtent);
            WriteDouble(uexp, offset + 32, bounds.HalfExtent);
            WriteDouble(uexp, offset + 40, bounds.HalfExtent);
            WriteDouble(uexp, offset + 48, MathF.Sqrt(3f * bounds.HalfExtent * bounds.HalfExtent));
            result.UpdatedRenderBoundsOffsets.Add(relativeOffset);
        }
    }

    private static void PatchPositions(byte[] uexp, int serialOffset, FaceVertex[] faces)
    {
        ValidateBuffer(uexp, serialOffset + PositionMetadataOffset, PositionStride, VertexCount, "position metadata");
        ValidateBuffer(uexp, serialOffset + PositionBulkHeaderOffset, PositionStride, VertexCount, "position buffer");
        var dataOffset = serialOffset + PositionDataOffset;
        for (var i = 0; i < VertexCount; i++)
        {
            var position = i < faces.Length ? faces[i].Position : new Vertex(0, 0, 0);
            WriteVertex(uexp, dataOffset + i * PositionStride, position);
        }
    }

    private static void PatchTangents(byte[] uexp, int serialOffset, FaceVertex[] faces)
    {
        ValidateBuffer(uexp, serialOffset + TangentBulkHeaderOffset, TangentStride, VertexCount, "tangent buffer");
        var dataOffset = serialOffset + TangentDataOffset;
        for (var i = 0; i < VertexCount; i++)
        {
            var face = i < faces.Length ? faces[i] : new FaceVertex(default, new Vertex(1, 0, 0), new Vertex(0, 0, 1), new Uv(0.5f, 0.5f));
            WritePackedNormal(uexp, dataOffset + i * TangentStride, face.Tangent, 127);
            WritePackedNormal(uexp, dataOffset + i * TangentStride + 4, face.Normal, 127);
        }
    }

    private static void PatchUvs(byte[] uexp, int serialOffset, FaceVertex[] faces)
    {
        ValidateBuffer(uexp, serialOffset + UvBulkHeaderOffset, UvStride, VertexCount, "UV buffer");
        var dataOffset = serialOffset + UvDataOffset;
        for (var i = 0; i < VertexCount; i++)
        {
            var uv = i < faces.Length ? faces[i].Uv : new Uv(0.5f, 0.5f);
            BinaryPrimitives.WriteUInt16LittleEndian(uexp.AsSpan(dataOffset + i * UvStride, sizeof(ushort)), BitConverter.HalfToUInt16Bits((Half)uv.U));
            BinaryPrimitives.WriteUInt16LittleEndian(uexp.AsSpan(dataOffset + i * UvStride + sizeof(ushort), sizeof(ushort)), BitConverter.HalfToUInt16Bits((Half)uv.V));
        }
    }

    private static void PatchIndices(byte[] uexp, int serialOffset, Result result)
    {
        foreach (var relativeOffset in IndexDataOffsets)
        {
            var dataOffset = serialOffset + relativeOffset;
            if (dataOffset < 0 || dataOffset + IndexDataBytes > uexp.Length ||
                ReadInt32(uexp, dataOffset - sizeof(int)) != IndexDataBytes)
            {
                throw new InvalidOperationException("The donor LOD0 index-buffer layout changed.");
            }
            for (var i = 0; i < IndexCount; i++)
            {
                var value = i < ClosedCubeTriangleIndices.Length ? ClosedCubeTriangleIndices[i] : (ushort)0;
                BinaryPrimitives.WriteUInt16LittleEndian(uexp.AsSpan(dataOffset + i * sizeof(ushort), sizeof(ushort)), value);
            }
            result.UpdatedIndexDataOffsets.Add(relativeOffset);
        }
    }

    private static FaceVertex[] ClosedCubeFaceVertices(Bounds bounds)
    {
        var low = new Vertex(bounds.Center.X - bounds.HalfExtent, bounds.Center.Y - bounds.HalfExtent, bounds.Center.Z - bounds.HalfExtent);
        var high = new Vertex(bounds.Center.X + bounds.HalfExtent, bounds.Center.Y + bounds.HalfExtent, bounds.Center.Z + bounds.HalfExtent);
        return
        [
            new(new(low.X, low.Y, low.Z), new(1, 0, 0), new(0, 0, -1), new(0, 0)),
            new(new(high.X, low.Y, low.Z), new(1, 0, 0), new(0, 0, -1), new(1, 0)),
            new(new(high.X, high.Y, low.Z), new(1, 0, 0), new(0, 0, -1), new(1, 1)),
            new(new(low.X, high.Y, low.Z), new(1, 0, 0), new(0, 0, -1), new(0, 1)),
            new(new(low.X, low.Y, high.Z), new(-1, 0, 0), new(0, 0, 1), new(0, 0)),
            new(new(high.X, low.Y, high.Z), new(-1, 0, 0), new(0, 0, 1), new(1, 0)),
            new(new(high.X, high.Y, high.Z), new(-1, 0, 0), new(0, 0, 1), new(1, 1)),
            new(new(low.X, high.Y, high.Z), new(-1, 0, 0), new(0, 0, 1), new(0, 1)),
            new(new(low.X, low.Y, low.Z), new(0, 1, 0), new(-1, 0, 0), new(0, 0)),
            new(new(low.X, high.Y, low.Z), new(0, 1, 0), new(-1, 0, 0), new(1, 0)),
            new(new(low.X, high.Y, high.Z), new(0, 1, 0), new(-1, 0, 0), new(1, 1)),
            new(new(low.X, low.Y, high.Z), new(0, 1, 0), new(-1, 0, 0), new(0, 1)),
            new(new(high.X, low.Y, low.Z), new(0, 1, 0), new(1, 0, 0), new(0, 0)),
            new(new(high.X, low.Y, high.Z), new(0, 1, 0), new(1, 0, 0), new(1, 0)),
            new(new(high.X, high.Y, high.Z), new(0, 1, 0), new(1, 0, 0), new(1, 1)),
            new(new(high.X, high.Y, low.Z), new(0, 1, 0), new(1, 0, 0), new(0, 1)),
            new(new(low.X, low.Y, low.Z), new(1, 0, 0), new(0, -1, 0), new(0, 0)),
            new(new(high.X, low.Y, low.Z), new(1, 0, 0), new(0, -1, 0), new(1, 0)),
            new(new(high.X, low.Y, high.Z), new(1, 0, 0), new(0, -1, 0), new(1, 1)),
            new(new(low.X, low.Y, high.Z), new(1, 0, 0), new(0, -1, 0), new(0, 1)),
            new(new(low.X, high.Y, low.Z), new(1, 0, 0), new(0, 1, 0), new(0, 0)),
            new(new(low.X, high.Y, high.Z), new(1, 0, 0), new(0, 1, 0), new(1, 0)),
            new(new(high.X, high.Y, high.Z), new(1, 0, 0), new(0, 1, 0), new(1, 1)),
            new(new(high.X, high.Y, low.Z), new(1, 0, 0), new(0, 1, 0), new(0, 1))
        ];
    }

    private static readonly ushort[] ClosedCubeTriangleIndices =
    [
        0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3,
        4, 5, 6, 4, 6, 7, 4, 6, 5, 4, 7, 6,
        8, 9, 10, 8, 10, 11, 8, 10, 9, 8, 11, 10,
        12, 13, 14, 12, 14, 15, 12, 14, 13, 12, 15, 14,
        16, 18, 17, 16, 19, 18, 16, 17, 18, 16, 18, 19,
        20, 21, 22, 20, 22, 23, 20, 22, 21, 20, 23, 22
    ];

    private static void ValidateBuffer(byte[] data, int headerOffset, int stride, int count, string name)
    {
        if (headerOffset < 0 || headerOffset + 2 * sizeof(int) > data.Length ||
            ReadInt32(data, headerOffset) != stride || ReadInt32(data, headerOffset + sizeof(int)) != count)
        {
            throw new InvalidOperationException($"The donor {name} layout changed.");
        }
    }

    private static NormalExport? FindStaticMeshExport(UAsset asset) => asset.Exports.OfType<NormalExport>()
        .FirstOrDefault(export => export.GetExportClassType().Value?.ToString().Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) == true);

    private static VectorPropertyData? FindBoundsVector(StructPropertyData? bounds, string name) => bounds?.Value
        .OfType<StructPropertyData>()
        .FirstOrDefault(property => property.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))?
        .Value.OfType<VectorPropertyData>()
        .FirstOrDefault();

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        const string prefix = "/Game/";
        if (!packagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected a /Game package path.", nameof(packagePath));
        }
        return Path.Combine(AppSettings.NormalizeContentRoot(contentRoot), packagePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
    private static double ReadDouble(byte[] data, int offset) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(double))));
    private static void WriteDouble(byte[] data, int offset, double value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, sizeof(double)), BitConverter.DoubleToInt64Bits(value));
    private static void WriteVertex(byte[] data, int offset, Vertex value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), BitConverter.SingleToInt32Bits(value.X));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, sizeof(int)), BitConverter.SingleToInt32Bits(value.Y));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 8, sizeof(int)), BitConverter.SingleToInt32Bits(value.Z));
    }
    private static void WritePackedNormal(byte[] data, int offset, Vertex value, byte handedness)
    {
        data[offset] = PackNormal(value.X);
        data[offset + 1] = PackNormal(value.Y);
        data[offset + 2] = PackNormal(value.Z);
        data[offset + 3] = handedness;
    }
    private static byte PackNormal(float value) => unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(value * 127f), -127, 127));
    private static bool NearlyEqual(float left, float right) => Math.Abs(left - right) <= 0.01f;
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
