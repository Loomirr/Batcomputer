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

public sealed class StaticMeshMorphService
{
    private const string DonorPackagePath = "/Game/Characters/Attachments/Misc/HeadPatch/SM_RHeadPatch";
    private const string DonorAssetName = "SM_RHeadPatch";
    private const int PositionMetadataOffset = 0x115;
    private const int PositionBulkHeaderOffset = 0x11D;
    private const int PositionDataOffset = 0x125;
    private const int PositionStride = 12;
    private const int ExpectedVertexCount = 18;
    private const int TangentBulkHeaderOffset = 0x20F;
    private const int TangentDataOffset = 0x217;
    private const int TangentStride = 8;
    private const int UvBulkHeaderOffset = 0x2A7;
    private const int UvDataOffset = 0x2AF;
    private const int UvStride = 4;
    private const int UvElementCount = ExpectedVertexCount * 2;
    private const int IndexDataBytes = 120;
    private const int IndexCount = IndexDataBytes / sizeof(ushort);
    // This later inline record is the RenderData.Bounds block exposed by FModel.
    private const int RenderDataBoundsOffset = 0x5D9;
    private const int RenderDataBoundsBytes = 7 * sizeof(double);
    private static readonly int[] IndexDataOffsets = [0x355, 0x3ED];
    private static readonly ushort[] CubeTriangleIndices =
    [
        0, 2, 1, 0, 3, 2,
        4, 5, 6, 4, 6, 7,
        0, 4, 7, 0, 7, 3,
        1, 2, 6, 1, 6, 5,
        0, 1, 5, 0, 5, 4,
        3, 7, 6, 3, 6, 2
    ];
    private static readonly ushort[] DoubleSidedCubeTriangleIndices =
    [
        .. CubeTriangleIndices,
        0, 1, 2, 0, 2, 3,
        4, 6, 5, 4, 7, 6,
        0, 7, 4, 0, 3, 7,
        1, 6, 2, 1, 5, 6
    ];
    private static readonly ushort[] SideShellTriangleIndices =
    [
        0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3,
        4, 5, 6, 4, 6, 7, 4, 6, 5, 4, 7, 6,
        8, 9, 10, 8, 10, 11, 8, 10, 9, 8, 11, 10,
        12, 13, 14, 12, 14, 15, 12, 14, 13, 12, 15, 14,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

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
        public float CubeScale { get; set; } = 1f;
        public bool CenterAtAttachmentOrigin { get; set; }
        public bool RewriteCubeRenderData { get; set; }
        public bool UseFourSidedHardEdgeShell { get; set; }
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
        public string GeometryMode { get; set; } = "";
        public int VertexCount { get; set; }
        public long StaticMeshSerialOffset { get; set; }
        public long StaticMeshSerialSize { get; set; }
        public int UexpSerialOffset { get; set; }
        public int PositionDataOffsetInSerial { get; set; } = PositionDataOffset;
        public List<int> IndexDataOffsetsInSerial { get; set; } = [];
        public bool ExtendedBoundsUpdated { get; set; }
        public bool RenderBoundsUpdated { get; set; }
        public bool CenteredAtAttachmentOrigin { get; set; }
        public bool TangentsUpdated { get; set; }
        public bool UvsUpdated { get; set; }
        public bool DoubleSidedFacesWritten { get; set; }
        public bool HardEdgeSideShellWritten { get; set; }
        public float CubeHalfExtent { get; set; }
        public string UexpSha256Before { get; set; } = "";
        public string UexpSha256After { get; set; } = "";
        public List<string> Log { get; set; } = [];
    }

    private readonly record struct Vertex(float X, float Y, float Z);

    public sealed class BoundsInfo
    {
        public string PackagePath { get; set; } = "";
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float OriginZ { get; set; }
        public float ExtentX { get; set; }
        public float ExtentY { get; set; }
        public float ExtentZ { get; set; }
    }

    public BoundsInfo ReadExtendedBounds(string extractedContentRoot, string usmapPath, string packagePath)
    {
        var contentRoot = AppSettings.NormalizeContentRoot(extractedContentRoot);
        var normalizedPackage = UnrealPathUtil.NormalizePackagePath(packagePath);
        var uassetPath = PackagePathToBasePath(contentRoot, normalizedPackage) + ".uasset";
        if (!File.Exists(uassetPath))
        {
            throw new FileNotFoundException("Static mesh package was not found in extracted Content.", uassetPath);
        }
        if (!File.Exists(usmapPath))
        {
            throw new FileNotFoundException("Mappings file was not found.", usmapPath);
        }

        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, MappingsCache.Load(usmapPath), NameMapOnlyPatchFlags);
        var staticMesh = FindStaticMeshExport(asset)
            ?? throw new InvalidOperationException("The package does not contain a StaticMesh export.");
        var bounds = staticMesh.Data.OfType<StructPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
        var origin = FindBoundsVector(bounds, "Origin")
            ?? throw new InvalidOperationException("The static mesh has no ExtendedBounds origin.");
        var extent = FindBoundsVector(bounds, "BoxExtent")
            ?? throw new InvalidOperationException("The static mesh has no ExtendedBounds box extent.");
        return new BoundsInfo
        {
            PackagePath = normalizedPackage,
            OriginX = (float)origin.Value.X,
            OriginY = (float)origin.Value.Y,
            OriginZ = (float)origin.Value.Z,
            ExtentX = (float)extent.Value.X,
            ExtentY = (float)extent.Value.Y,
            ExtentZ = (float)extent.Value.Z
        };
    }

    public Result CreateCubeMorphProbe(Request request)
    {
        var result = new Result
        {
            OutputPackagePath = UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath),
            GeometryMode = "donor-shell true-cube positions and fixed indices"
        };

        try
        {
            if (request.CubeScale is < 0.25f or > 8f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.CubeScale), "Cube scale must be between 0.25 and 8.");
            }
            if (!result.OutputPackagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Probe output must be under /Game/Mods/ so it cannot replace a base-game mesh.");
            }

            var contentRoot = AppSettings.NormalizeContentRoot(request.ExtractedContentRoot);
            var sourceBase = PackagePathToBasePath(contentRoot, DonorPackagePath);
            var outputBase = PackagePathToBasePath(request.OutputContentRoot, result.OutputPackagePath);
            var sourceUasset = sourceBase + ".uasset";
            var sourceUexp = sourceBase + ".uexp";
            var sourceUbulk = sourceBase + ".ubulk";

            if (!File.Exists(sourceUasset) || !File.Exists(sourceUexp) || !File.Exists(sourceUbulk))
            {
                throw new FileNotFoundException("The static-mesh donor needs its .uasset, .uexp, and .ubulk files in extracted Content.", sourceUasset);
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
            var staticMesh = FindStaticMeshExport(asset)
                ?? throw new InvalidOperationException("The cloned donor no longer contains a StaticMesh export.");
            result.StaticMeshSerialOffset = staticMesh.SerialOffset;
            result.StaticMeshSerialSize = staticMesh.SerialSize;
            result.UexpSerialOffset = checked((int)(staticMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));

            var sourceUexpBytes = File.ReadAllBytes(result.OutputUexp);
            var cubeBounds = ReadCubeBounds(sourceUexpBytes, result.UexpSerialOffset, request.CubeScale, request.CenterAtAttachmentOrigin);
            result.CenteredAtAttachmentOrigin = request.CenterAtAttachmentOrigin;
            PatchExtendedBounds(staticMesh, cubeBounds, result);
            asset.Write(result.OutputUasset);

            var rewrittenAsset = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var rewrittenMesh = FindStaticMeshExport(rewrittenAsset)
                ?? throw new InvalidOperationException("The rewritten static-mesh shell no longer contains a StaticMesh export.");
            result.StaticMeshSerialOffset = rewrittenMesh.SerialOffset;
            result.StaticMeshSerialSize = rewrittenMesh.SerialSize;
            result.UexpSerialOffset = checked((int)(rewrittenMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));

            var uexp = File.ReadAllBytes(result.OutputUexp);
            result.UexpSha256Before = Hash(uexp);
            PatchRenderDataBounds(uexp, result.UexpSerialOffset, cubeBounds, result);
            PatchPositions(uexp, result.UexpSerialOffset, cubeBounds, request.UseFourSidedHardEdgeShell, result);
            if (request.RewriteCubeRenderData)
            {
                PatchTangentsAndUvs(uexp, result.UexpSerialOffset, cubeBounds, request.UseFourSidedHardEdgeShell, result);
            }
            PatchIndices(uexp, result.UexpSerialOffset, request.RewriteCubeRenderData, request.UseFourSidedHardEdgeShell, result);
            result.UexpSha256After = Hash(uexp);
            File.WriteAllBytes(result.OutputUexp, uexp);

            var validation = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var validationMesh = FindStaticMeshExport(validation)
                ?? throw new InvalidOperationException("UAssetAPI could not reopen the generated static mesh shell.");
            if (validation.FolderName.ToString() != result.OutputPackagePath)
            {
                throw new InvalidOperationException("Generated static mesh package name did not persist.");
            }
            if (!validationMesh.ObjectName.ToString().Equals(UnrealPathUtil.AssetName(result.OutputPackagePath), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated static mesh object name did not persist.");
            }
            result.Log.Add("UAssetAPI reopened the generated package with the requested package and object names.");

            result.Status = "created";
            result.Log.Add("Copied the native donor shell and preserved its uasset, section, material, collision, and bulk layouts.");
            result.Log.Add(request.UseFourSidedHardEdgeShell
                ? "Replaced the 18 LOD0 vertices with a four-sided hard-edge shell centered on the attachment origin."
                : "Replaced the 18 LOD0 position vectors with a true cube centered on the donor bounds.");
            result.Log.Add(request.UseFourSidedHardEdgeShell
                ? "Replaced the primary and mirrored 16-bit index buffers with four double-sided side faces while retaining the donor's 20-triangle section size."
                : "Replaced the primary and mirrored 16-bit index buffers with cube triangles while retaining the donor's 20-triangle section size.");
            result.Log.Add(request.UseFourSidedHardEdgeShell
                ? "This proof uses independent vertices, tangent frames, and UVs for every visible side face; the top and bottom are intentionally open."
                : request.RewriteCubeRenderData
                ? "This proof also rebuilt the packed tangent frames, both UV channels, and double-sided face coverage."
                : "This is a fixed-topology cube proof. The property and render bounds match the generated cube; collision, tangents, and UVs remain donor data.");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }
        finally
        {
            WriteReport(result);
        }

        return result;
    }

    private static void RewriteIdentity(string uassetPath, string outputPackagePath, Usmap mappings, Result result)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        asset.FolderName = new FString(outputPackagePath);

        var replacements = new Dictionary<string, string>
        {
            [DonorPackagePath] = outputPackagePath,
            [DonorAssetName] = outputName
        };
        var nameMap = asset.GetNameMapIndexList();
        var replacementsApplied = 0;
        for (var i = 0; i < nameMap.Count; i++)
        {
            var original = nameMap[i].ToString();
            var patched = original;
            foreach (var replacement in replacements)
            {
                patched = patched.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            }
            if (patched == original)
            {
                continue;
            }
            asset.SetNameReference(i, new FString(patched));
            replacementsApplied++;
        }

        asset.Write(uassetPath);
        result.Log.Add($"Patched package identity and {replacementsApplied} matching name-map entries.");
    }

    private static CubeBounds ReadCubeBounds(byte[] uexp, int serialOffset, float cubeScale, bool centerAtAttachmentOrigin)
    {
        if (serialOffset < 0 || serialOffset + PositionDataOffset + ExpectedVertexCount * PositionStride > uexp.Length)
        {
            throw new InvalidOperationException("StaticMesh serial data does not contain the expected inline LOD0 position buffer.");
        }

        var metadataOffset = serialOffset + PositionMetadataOffset;
        var bulkHeaderOffset = serialOffset + PositionBulkHeaderOffset;
        if (ReadInt32(uexp, metadataOffset) != PositionStride ||
            ReadInt32(uexp, metadataOffset + sizeof(int)) != ExpectedVertexCount ||
            ReadInt32(uexp, bulkHeaderOffset) != PositionStride ||
            ReadInt32(uexp, bulkHeaderOffset + sizeof(int)) != ExpectedVertexCount)
        {
            throw new InvalidOperationException("The donor position-buffer layout changed. No geometry bytes were written.");
        }

        var positionOffset = serialOffset + PositionDataOffset;
        var original = new Vertex[ExpectedVertexCount];
        for (var i = 0; i < ExpectedVertexCount; i++)
        {
            original[i] = ReadVertex(uexp, positionOffset + i * PositionStride);
        }

        var min = new Vertex(original.Min(vertex => vertex.X), original.Min(vertex => vertex.Y), original.Min(vertex => vertex.Z));
        var max = new Vertex(original.Max(vertex => vertex.X), original.Max(vertex => vertex.Y), original.Max(vertex => vertex.Z));
        return CubeBounds.FromDonorBounds(min, max, cubeScale, centerAtAttachmentOrigin);
    }

    private static void PatchPositions(byte[] uexp, int serialOffset, CubeBounds cubeBounds, bool useFourSidedHardEdgeShell, Result result)
    {
        if (serialOffset < 0 || serialOffset + PositionDataOffset + ExpectedVertexCount * PositionStride > uexp.Length)
        {
            throw new InvalidOperationException("StaticMesh serial data does not contain the expected inline LOD0 position buffer.");
        }

        var metadataOffset = serialOffset + PositionMetadataOffset;
        var bulkHeaderOffset = serialOffset + PositionBulkHeaderOffset;
        if (ReadInt32(uexp, metadataOffset) != PositionStride ||
            ReadInt32(uexp, metadataOffset + sizeof(int)) != ExpectedVertexCount ||
            ReadInt32(uexp, bulkHeaderOffset) != PositionStride ||
            ReadInt32(uexp, bulkHeaderOffset + sizeof(int)) != ExpectedVertexCount)
        {
            throw new InvalidOperationException("The donor position-buffer layout changed. No geometry bytes were written.");
        }

        var positionOffset = serialOffset + PositionDataOffset;
        var corners = CubeCorners(cubeBounds);
        var shell = useFourSidedHardEdgeShell ? FourSidedShellVertices(cubeBounds) : [];
        for (var i = 0; i < ExpectedVertexCount; i++)
        {
            var position = useFourSidedHardEdgeShell
                ? shell[i % shell.Length].Position
                : corners[i % corners.Length];
            WriteVertex(uexp, positionOffset + i * PositionStride, position);
        }

        result.VertexCount = ExpectedVertexCount;
        result.Log.Add($"Position buffer guard passed: stride={PositionStride}, vertices={ExpectedVertexCount}, relative offset=0x{PositionDataOffset:X}.");
        result.Log.Add($"Used a uniform half-extent of {cubeBounds.HalfExtent:F2}.");
    }

    private static void PatchTangentsAndUvs(byte[] uexp, int serialOffset, CubeBounds cubeBounds, bool useFourSidedHardEdgeShell, Result result)
    {
        var tangentOffset = serialOffset + TangentDataOffset;
        if (tangentOffset < 0 || tangentOffset + ExpectedVertexCount * TangentStride > uexp.Length ||
            ReadInt32(uexp, serialOffset + TangentBulkHeaderOffset) != TangentStride ||
            ReadInt32(uexp, serialOffset + TangentBulkHeaderOffset + sizeof(int)) != ExpectedVertexCount)
        {
            throw new InvalidOperationException("The donor tangent-buffer layout changed. No tangent bytes were written.");
        }

        var uvOffset = serialOffset + UvDataOffset;
        if (uvOffset < 0 || uvOffset + UvElementCount * UvStride > uexp.Length ||
            ReadInt32(uexp, serialOffset + UvBulkHeaderOffset) != UvStride ||
            ReadInt32(uexp, serialOffset + UvBulkHeaderOffset + sizeof(int)) != UvElementCount)
        {
            throw new InvalidOperationException("The donor UV-buffer layout changed. No UV bytes were written.");
        }

        var corners = CubeCorners(cubeBounds);
        var shell = useFourSidedHardEdgeShell ? FourSidedShellVertices(cubeBounds) : [];
        for (var vertexIndex = 0; vertexIndex < ExpectedVertexCount; vertexIndex++)
        {
            var cornerIndex = vertexIndex % corners.Length;
            var entry = useFourSidedHardEdgeShell ? shell[vertexIndex % shell.Length] : default;
            var normal = useFourSidedHardEdgeShell ? entry.Normal : Normalize(Subtract(corners[cornerIndex], cubeBounds.Center));
            var tangent = useFourSidedHardEdgeShell ? entry.Tangent : Perpendicular(normal);
            WritePackedNormal(uexp, tangentOffset + vertexIndex * TangentStride, tangent, 127);
            WritePackedNormal(uexp, tangentOffset + vertexIndex * TangentStride + 4, normal, 127);

            var uv = useFourSidedHardEdgeShell ? entry.Uv : CubeUvs[cornerIndex];
            WriteUv(uexp, uvOffset + (vertexIndex * 2) * UvStride, uv);
            WriteUv(uexp, uvOffset + (vertexIndex * 2 + 1) * UvStride, uv);
        }

        result.TangentsUpdated = true;
        result.UvsUpdated = true;
        result.Log.Add($"Tangent and UV guards passed: {ExpectedVertexCount} packed tangent frames and {UvElementCount} half-float UV entries.");
    }

    private static void PatchIndices(byte[] uexp, int serialOffset, bool writeDoubleSidedFaces, bool useFourSidedHardEdgeShell, Result result)
    {
        var indices = useFourSidedHardEdgeShell
            ? SideShellTriangleIndices
            : writeDoubleSidedFaces ? DoubleSidedCubeTriangleIndices : CubeTriangleIndices;
        foreach (var relativeOffset in IndexDataOffsets)
        {
            var dataOffset = serialOffset + relativeOffset;
            if (dataOffset < 0 || dataOffset + IndexDataBytes > uexp.Length ||
                ReadInt32(uexp, dataOffset - sizeof(int)) != IndexDataBytes)
            {
                throw new InvalidOperationException("The donor index-buffer layout changed. No index bytes were written.");
            }

            for (var i = 0; i < IndexCount; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    uexp.AsSpan(dataOffset + i * sizeof(ushort), sizeof(ushort)),
                    indices[i % indices.Length]);
            }
            result.IndexDataOffsetsInSerial.Add(relativeOffset);
        }

        result.Log.Add($"Index-buffer guards passed: {IndexDataOffsets.Length} buffers x {IndexCount} 16-bit indices.");
        result.DoubleSidedFacesWritten = writeDoubleSidedFaces || useFourSidedHardEdgeShell;
        result.HardEdgeSideShellWritten = useFourSidedHardEdgeShell;
    }

    private static void PatchExtendedBounds(NormalExport staticMesh, CubeBounds cubeBounds, Result result)
    {
        var bounds = staticMesh.Data.OfType<StructPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
        var origin = FindBoundsVector(bounds, "Origin");
        var extent = FindBoundsVector(bounds, "BoxExtent");
        var radius = bounds?.Value.OfType<DoublePropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("SphereRadius", StringComparison.OrdinalIgnoreCase));
        if (bounds is null || origin is null || extent is null || radius is null)
        {
            throw new InvalidOperationException("The donor ExtendedBounds property did not match the verified BoxSphereBounds layout.");
        }

        if (!NearlyEqual((float)origin.Value.X, cubeBounds.SourceCenter.X) ||
            !NearlyEqual((float)origin.Value.Y, cubeBounds.SourceCenter.Y) ||
            !NearlyEqual((float)origin.Value.Z, cubeBounds.SourceCenter.Z))
        {
            throw new InvalidOperationException("The donor ExtendedBounds origin did not match its position-buffer center.");
        }

        origin.Value = new FVector(cubeBounds.Center.X, cubeBounds.Center.Y, cubeBounds.Center.Z);
        extent.Value = new FVector(cubeBounds.HalfExtent, cubeBounds.HalfExtent, cubeBounds.HalfExtent);
        radius.Value = Math.Sqrt(3d * cubeBounds.HalfExtent * cubeBounds.HalfExtent);
        result.ExtendedBoundsUpdated = true;
        result.CubeHalfExtent = cubeBounds.HalfExtent;
        result.Log.Add("Updated the verified ExtendedBounds property to match the generated cube.");
    }

    private static void PatchRenderDataBounds(byte[] uexp, int serialOffset, CubeBounds cubeBounds, Result result)
    {
        var boundsOffset = serialOffset + RenderDataBoundsOffset;
        if (boundsOffset < 0 || boundsOffset + RenderDataBoundsBytes > uexp.Length)
        {
            throw new InvalidOperationException("StaticMesh serial data does not contain the verified inline render-bounds record.");
        }

        var origin = new Vertex(
            (float)ReadDouble(uexp, boundsOffset),
            (float)ReadDouble(uexp, boundsOffset + sizeof(double)),
            (float)ReadDouble(uexp, boundsOffset + 2 * sizeof(double)));
        var extent = new Vertex(
            (float)ReadDouble(uexp, boundsOffset + 3 * sizeof(double)),
            (float)ReadDouble(uexp, boundsOffset + 4 * sizeof(double)),
            (float)ReadDouble(uexp, boundsOffset + 5 * sizeof(double)));
        var radius = ReadDouble(uexp, boundsOffset + 6 * sizeof(double));

        if (!NearlyEqual(origin.X, cubeBounds.SourceCenter.X) ||
            !NearlyEqual(origin.Y, cubeBounds.SourceCenter.Y) ||
            !NearlyEqual(origin.Z, cubeBounds.SourceCenter.Z) ||
            extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0 || radius <= 0)
        {
            throw new InvalidOperationException("The donor render-bounds record did not match its verified center and positive extent layout.");
        }

        WriteDouble(uexp, boundsOffset, cubeBounds.Center.X);
        WriteDouble(uexp, boundsOffset + sizeof(double), cubeBounds.Center.Y);
        WriteDouble(uexp, boundsOffset + 2 * sizeof(double), cubeBounds.Center.Z);
        WriteDouble(uexp, boundsOffset + 3 * sizeof(double), cubeBounds.HalfExtent);
        WriteDouble(uexp, boundsOffset + 4 * sizeof(double), cubeBounds.HalfExtent);
        WriteDouble(uexp, boundsOffset + 5 * sizeof(double), cubeBounds.HalfExtent);
        WriteDouble(uexp, boundsOffset + 6 * sizeof(double), MathF.Sqrt(3f * cubeBounds.HalfExtent * cubeBounds.HalfExtent));

        result.RenderBoundsUpdated = true;
        result.Log.Add($"Render-bounds guard passed at relative offset 0x{RenderDataBoundsOffset:X}; updated extents and radius only.");
    }

    private static VectorPropertyData? FindBoundsVector(StructPropertyData? bounds, string name) => bounds?.Value
        .OfType<StructPropertyData>()
        .FirstOrDefault(property => property.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))?
        .Value.OfType<VectorPropertyData>()
        .FirstOrDefault();

    private static Vertex[] CubeCorners(CubeBounds cubeBounds)
    {
        var low = new Vertex(
            cubeBounds.Center.X - cubeBounds.HalfExtent,
            cubeBounds.Center.Y - cubeBounds.HalfExtent,
            cubeBounds.Center.Z - cubeBounds.HalfExtent);
        var high = new Vertex(
            cubeBounds.Center.X + cubeBounds.HalfExtent,
            cubeBounds.Center.Y + cubeBounds.HalfExtent,
            cubeBounds.Center.Z + cubeBounds.HalfExtent);
        return
        [
            new(low.X, low.Y, low.Z), new(high.X, low.Y, low.Z),
            new(high.X, high.Y, low.Z), new(low.X, high.Y, low.Z),
            new(low.X, low.Y, high.Z), new(high.X, low.Y, high.Z),
            new(high.X, high.Y, high.Z), new(low.X, high.Y, high.Z)
        ];
    }

    private static readonly Uv[] CubeUvs =
    [
        new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f),
        new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)
    ];

    private static ShellVertex[] FourSidedShellVertices(CubeBounds cubeBounds)
    {
        var low = new Vertex(
            cubeBounds.Center.X - cubeBounds.HalfExtent,
            cubeBounds.Center.Y - cubeBounds.HalfExtent,
            cubeBounds.Center.Z - cubeBounds.HalfExtent);
        var high = new Vertex(
            cubeBounds.Center.X + cubeBounds.HalfExtent,
            cubeBounds.Center.Y + cubeBounds.HalfExtent,
            cubeBounds.Center.Z + cubeBounds.HalfExtent);
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
            new(new(high.X, high.Y, low.Z), new(0, 1, 0), new(1, 0, 0), new(0, 1))
        ];
    }

    private readonly record struct CubeBounds(Vertex SourceCenter, Vertex Center, float HalfExtent)
    {
        public static CubeBounds FromDonorBounds(Vertex min, Vertex max, float cubeScale, bool centerAtAttachmentOrigin)
        {
            var sourceCenter = new Vertex(
                (min.X + max.X) / 2f,
                (min.Y + max.Y) / 2f,
                (min.Z + max.Z) / 2f);
            var halfExtent = Math.Max(
                Math.Abs(max.X - min.X),
                Math.Max(Math.Abs(max.Y - min.Y), Math.Abs(max.Z - min.Z))) / 2f;
            var center = centerAtAttachmentOrigin ? new Vertex(0, 0, 0) : sourceCenter;
            return new CubeBounds(sourceCenter, center, halfExtent * cubeScale);
        }
    }

    private readonly record struct Uv(float U, float V);
    private readonly record struct ShellVertex(Vertex Position, Vertex Tangent, Vertex Normal, Uv Uv);

    private static NormalExport? FindStaticMeshExport(UAsset asset) => asset.Exports
        .OfType<NormalExport>()
        .FirstOrDefault(export => export.GetExportClassType().Value?.ToString()
            .Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) == true);

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        const string gamePrefix = "/Game/";
        var normalizedContentRoot = AppSettings.NormalizeContentRoot(contentRoot);
        if (!packagePath.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected a /Game package path.", nameof(packagePath));
        }
        return Path.Combine(normalizedContentRoot, packagePath[gamePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static Vertex ReadVertex(byte[] data, int offset) =>
        new(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));

    private static void WriteVertex(byte[] data, int offset, Vertex vertex)
    {
        WriteSingle(data, offset, vertex.X);
        WriteSingle(data, offset + 4, vertex.Y);
        WriteSingle(data, offset + 8, vertex.Z);
    }

    private static Vertex Subtract(Vertex left, Vertex right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static Vertex Normalize(Vertex value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length <= 0.0001f ? new Vertex(0, 0, 1) : new Vertex(value.X / length, value.Y / length, value.Z / length);
    }

    private static Vertex Perpendicular(Vertex normal)
    {
        var reference = Math.Abs(normal.Z) < 0.95f ? new Vertex(0, 0, 1) : new Vertex(0, 1, 0);
        return Normalize(new Vertex(
            reference.Y * normal.Z - reference.Z * normal.Y,
            reference.Z * normal.X - reference.X * normal.Z,
            reference.X * normal.Y - reference.Y * normal.X));
    }

    private static void WritePackedNormal(byte[] data, int offset, Vertex value, byte handedness)
    {
        data[offset] = PackNormal(value.X);
        data[offset + 1] = PackNormal(value.Y);
        data[offset + 2] = PackNormal(value.Z);
        data[offset + 3] = handedness;
    }

    private static byte PackNormal(float value) => unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(value * 127f), -127, 127));

    private static void WriteUv(byte[] data, int offset, Uv uv)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)), BitConverter.HalfToUInt16Bits((Half)uv.U));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + sizeof(ushort), sizeof(ushort)), BitConverter.HalfToUInt16Bits((Half)uv.V));
    }

    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));

    private static float ReadSingle(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(data, offset));

    private static double ReadDouble(byte[] data, int offset) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(double))));

    private static void WriteSingle(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), BitConverter.SingleToInt32Bits(value));

    private static void WriteDouble(byte[] data, int offset, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, sizeof(double)), BitConverter.DoubleToInt64Bits(value));

    private static bool NearlyEqual(float left, float right) => Math.Abs(left - right) <= 0.001f;

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void WriteReport(Result result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputUasset))
        {
            return;
        }

        result.ReportPath = Path.ChangeExtension(result.OutputUasset, ".static-mesh-probe-report.json");
        File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
    }
}
