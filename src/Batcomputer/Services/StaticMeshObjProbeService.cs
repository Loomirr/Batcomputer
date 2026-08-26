using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

public sealed class StaticMeshObjProbeService
{
    private const string DonorPackagePath = "/Game/Characters/Attachments/HAIR/Nightwing08/SM_Hair_Nightwing08";
    private const string DonorAssetName = "SM_Hair_Nightwing08";
    // Verified UE 5.6 cooked FStaticMeshLODResources layout for the exact donor above. These
    // offsets are deliberately guarded in ValidateDonorPayload before any byte-range rewrite.
    private const int DonorStaticMeshSerialSize = 18132;
    private const int DonorVertexCount = 486;
    private const int DonorTriangleCount = 420;
    private const int DonorSerializedBuffersSize = 16704;
    private const int SectionsCountOffset = 0x97;
    private const int SectionDataOffset = SectionsCountOffset + sizeof(int);
    private const int SectionRecordSize = LodCookedOutOffset - SectionDataOffset;
    private const int SectionMaterialIndexRelativeOffset = 0x00;
    private const int SectionFirstIndexRelativeOffset = 0x04;
    private const int SectionTriangleCountRelativeOffset = 0x08;
    private const int SectionMinVertexIndexRelativeOffset = 0x0C;
    private const int SectionMaxVertexIndexRelativeOffset = 0x10;
    private const int SectionRenderBoundsRelativeOffset = 0x28;
    private const int MaxMaterialSlots = 64;
    // UAssetAPI separates reflected properties from NormalExport.Extras. For the verified donor,
    // the raw cooked LOD tail begins 0x6F bytes into the export and the section table therefore
    // starts at these stable Extras-relative offsets. StaticMaterials growth changes the reflected
    // prefix length, but it does not change offsets inside Extras.
    private const int CookedSectionsCountExtrasOffset = 0x28;
    private const int CookedSectionDataExtrasOffset = CookedSectionsCountExtrasOffset + sizeof(int);
    private const int DonorReflectedPropertyBytes = SectionsCountOffset - CookedSectionsCountExtrasOffset;
    private const int SectionTriangleCountOffset = 0xA3;
    private const int SectionMinVertexIndexOffset = 0xA7;
    private const int SectionMaxVertexIndexOffset = 0xAB;
    private const int LodCookedOutOffset = 0xFF;
    private const int LodBuffersInlinedOffset = 0x103;
    private const int LodHasRayTracingGeometryOffset = 0x107;
    private const int LodBufferGlobalStripFlagsOffset = 0x10B;
    private const int LodBufferClassStripFlagsOffset = 0x10C;
    private const byte DonorGlobalStripFlags = 0x05;
    private const byte DonorClassStripFlags = 0x08;
    private const int PositionStrideOffset = 0x10D;
    private const int PositionDataOffset = 0x11D;
    private const int PositionStride = 12;
    private const int PositionCountOffset0 = 0x111;
    private const int PositionElementSizeOffset = 0x115;
    private const int PositionCountOffset1 = 0x119;
    private const int StaticMeshVertexGlobalStripFlagsOffset = 0x17E5;
    private const int StaticMeshVertexClassStripFlagsOffset = 0x17E6;
    private const int NumTexCoordsOffset = 0x17E7;
    private const int TangentDataOffset = 0x17FF;
    private const int TangentStride = 8;
    private const int TangentCountOffset0 = 0x17EB;
    private const int TangentElementSizeOffset = 0x17F7;
    private const int TangentCountOffset1 = 0x17FB;
    private const int UvDataOffset = 0x2737;
    private const int UvStride = 4;
    private const int UvElementSizeOffset = 0x272F;
    private const int UvCountOffset = 0x2733;
    private const int ColorVertexGlobalStripFlagsOffset = 0x2ECF;
    private const int ColorVertexClassStripFlagsOffset = 0x2ED0;
    private const int ColorVertexStrideOffset = 0x2ED1;
    private const int ColorVertexCountOffset = 0x2ED5;
    private const int Index0Is32BitOffset = 0x2ED9;
    private const int Index0ElementSizeOffset = 0x2EDD;
    private const int Index0DataOffset = 0x2EE5;
    private const int Index0SizeOffset = 0x2EE1;
    private const int Index0ExpandTo32BitOffset = 0x38BD;
    private const int ReversedIndexIs32BitOffset = 0x38C1;
    private const int ReversedIndexElementSizeOffset = 0x38C5;
    private const int ReversedIndexSizeOffset = 0x38C9;
    private const int ReversedIndexExpandTo32BitOffset = 0x38CD;
    private const int Index1Is32BitOffset = 0x38D1;
    private const int Index1ElementSizeOffset = 0x38D5;
    private const int Index1DataOffset = 0x38DD;
    private const int Index1SizeOffset = 0x38D9;
    private const int Index1ExpandTo32BitOffset = 0x42B5;
    private const int ReversedDepthIndexIs32BitOffset = 0x42B9;
    private const int ReversedDepthIndexElementSizeOffset = 0x42BD;
    private const int ReversedDepthIndexSizeOffset = 0x42C1;
    private const int ReversedDepthIndexExpandTo32BitOffset = 0x42C5;
    private const int SectionSamplerProbabilityCountOffset = 0x42C9;
    private const int SectionSamplerAliasCountOffset = 0x42CD;
    private const int MeshSamplerProbabilityCountOffset = 0x42D5;
    private const int MeshSamplerAliasCountOffset = 0x42D9;
    private const int SerializedBuffersSizeOffset = 0x42E1;
    private const int DepthOnlyBufferSizeOffset = 0x42E5;
    private const int ReversedBuffersSizeOffset = 0x42E9;
    private const int DonorIndexBytes = 2520;
    private const int GlobalRenderBoundsOffset = 0x33;
    private const int FinalRenderBoundsOffset = 0x4629;
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
        public string ObjPath { get; set; } = "";
        public float Scale { get; set; } = 150f;
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotationPitch { get; set; }
        public float RotationYaw { get; set; }
        public float RotationRoll { get; set; }
        // Optional previously persisted slot map. Matching `usemtl` names keep their material
        // bindings even if the OBJ's first-use order changes on re-import.
        public List<CustomStaticMeshMaterialSlot> MaterialSlots { get; set; } = new();
        public string LegacyMaterialPath { get; set; } = "";
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public bool TransientFileLock { get; set; }
        public string SourcePackagePath { get; set; } = DonorPackagePath;
        public string SourceObjPath { get; set; } = "";
        public string OutputPackagePath { get; set; } = "";
        public string OutputUasset { get; set; } = "";
        public string OutputUexp { get; set; } = "";
        public string OutputUbulk { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public int IndexCount { get; set; }
        public int StaticMeshBytesBefore { get; set; }
        public int StaticMeshBytesAfter { get; set; }
        public float Scale { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotationPitch { get; set; }
        public float RotationYaw { get; set; }
        public float RotationRoll { get; set; }
        public string ObjSha256 { get; set; } = "";
        public string UexpSha256Before { get; set; } = "";
        public string UexpSha256After { get; set; } = "";
        public List<ObjMaterialSlot> MaterialSlots { get; set; } = new();
        public List<string> Log { get; set; } = [];
    }

    public sealed class ObjMaterialSlot
    {
        public int Slot { get; set; }
        public string SourceMaterialName { get; set; } = "";
        public string StableSlotName { get; set; } = "";
        public int FirstIndex { get; set; }
        public int IndexCount { get; set; }
        public int TriangleCount => IndexCount / 3;
        public int MinVertexIndex { get; set; }
        public int MaxVertexIndex { get; set; }
    }

    private readonly record struct Vector3(float X, float Y, float Z)
    {
        public static Vector3 operator +(Vector3 left, Vector3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Vector3 operator -(Vector3 left, Vector3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        public static Vector3 operator *(Vector3 value, float scalar) => new(value.X * scalar, value.Y * scalar, value.Z * scalar);
    }

    private readonly record struct Vector2(float U, float V);
    private readonly record struct ObjKey(int Position, int Uv, int Normal);
    private readonly record struct Bounds(Vector3 Center, Vector3 Extent, float Radius);

    private sealed class ImportedVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector3 Tangent { get; set; }
        public Vector2 Uv { get; set; }
        public Vector3 AccumulatedNormal { get; set; }
        public Vector3 AccumulatedTangent { get; set; }
    }

    private sealed class ImportedMesh
    {
        public List<ImportedVertex> Vertices { get; } = [];
        public List<ushort> Indices { get; } = [];
        public List<ImportedSection> Sections { get; } = [];
        public List<CustomStaticMeshMaterialSlot> DeclaredMaterialSlots { get; } = [];
        public Bounds Bounds { get; set; }
    }

    private sealed class ImportedSection
    {
        public int Slot { get; set; }
        public string SourceMaterialName { get; set; } = "";
        public string StableSlotName { get; set; } = "";
        public int FirstIndex { get; set; }
        public List<ushort> Indices { get; } = [];
        public Bounds Bounds { get; set; }
    }

    public Result CreateObjHeadProbe(Request request)
    {
        var result = new Result
        {
            SourceObjPath = request.ObjPath,
            OutputPackagePath = UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath),
            Scale = request.Scale,
            OffsetX = request.OffsetX,
            OffsetY = request.OffsetY,
            OffsetZ = request.OffsetZ,
            RotationPitch = request.RotationPitch,
            RotationYaw = request.RotationYaw,
            RotationRoll = request.RotationRoll,
        };

        try
        {
            if (request.Scale is < 1f or > 1000f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.Scale), "OBJ scale must be between 1 and 1000.");
            }
            if (!float.IsFinite(request.OffsetX) || !float.IsFinite(request.OffsetY) || !float.IsFinite(request.OffsetZ) ||
                MathF.Abs(request.OffsetX) > 100000f || MathF.Abs(request.OffsetY) > 100000f || MathF.Abs(request.OffsetZ) > 100000f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.OffsetX), "OBJ offsets must be finite values between -100000 and 100000.");
            }
            if (!float.IsFinite(request.RotationPitch) || !float.IsFinite(request.RotationYaw) || !float.IsFinite(request.RotationRoll) ||
                MathF.Abs(request.RotationPitch) > 360f || MathF.Abs(request.RotationYaw) > 360f || MathF.Abs(request.RotationRoll) > 360f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.RotationPitch), "OBJ rotations must be finite values between -360 and 360 degrees.");
            }
            if (!result.OutputPackagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Probe output must be under /Game/Mods/.");
            }
            if (!File.Exists(request.ObjPath))
            {
                throw new FileNotFoundException("The OBJ file was not found.", request.ObjPath);
            }

            var mesh = ParseObj(
                request.ObjPath,
                request.Scale,
                new Vector3(request.OffsetX, request.OffsetY, request.OffsetZ),
                request.RotationPitch,
                request.RotationYaw,
                request.RotationRoll,
                request.MaterialSlots,
                request.LegacyMaterialPath);
            if (mesh.Vertices.Count > ushort.MaxValue)
            {
                throw new InvalidOperationException("This first OBJ writer supports up to 65,535 flattened vertices.");
            }
            if (mesh.Indices.Count == 0 || mesh.Indices.Count % 3 != 0)
            {
                throw new InvalidOperationException("The OBJ did not produce triangle indices.");
            }

            result.VertexCount = mesh.Vertices.Count;
            result.IndexCount = mesh.Indices.Count;
            result.TriangleCount = mesh.Indices.Count / 3;
            result.MaterialSlots = DescribeMaterialSlots(mesh);
            result.ObjSha256 = Hash(File.ReadAllBytes(request.ObjPath));

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
            var staticMesh = FindStaticMeshExport(asset)
                ?? throw new InvalidOperationException("The cloned donor does not contain a StaticMesh export.");
            if (asset.Exports.IndexOf(staticMesh) != asset.Exports.Count - 1)
            {
                throw new InvalidOperationException("The selected donor is not package-terminal, so its render payload cannot be expanded safely.");
            }
            var originalSerialSize = checked((int)staticMesh.SerialSize);
            var serialOffset = checked((int)(staticMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));
            var initialUexp = File.ReadAllBytes(result.OutputUexp);
            ValidateDonorPayload(initialUexp, serialOffset, originalSerialSize);

            PatchExtendedBounds(staticMesh, mesh.Bounds);
            PatchStaticMaterialSlots(asset, staticMesh, mesh.DeclaredMaterialSlots);
            asset.Write(result.OutputUasset);

            var afterMetadata = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var afterMetadataMesh = FindStaticMeshExport(afterMetadata)
                ?? throw new InvalidOperationException("The donor no longer contains a StaticMesh export after its metadata update.");
            serialOffset = checked((int)(afterMetadataMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));
            var metadataSerialSize = checked((int)afterMetadataMesh.SerialSize);
            var sourceLayoutShift = checked(metadataSerialSize - originalSerialSize);
            if (sourceLayoutShift < 0)
            {
                throw new InvalidOperationException("The generated StaticMaterials metadata unexpectedly shrank the donor export.");
            }
            var uexp = File.ReadAllBytes(result.OutputUexp);
            result.UexpSha256Before = Hash(uexp);
            var metadataPayload = uexp.AsSpan(serialOffset, metadataSerialSize).ToArray();
            ValidateShiftedDonorPayload(metadataPayload, sourceLayoutShift);
            var payload = BuildPayload(metadataPayload, mesh, result, sourceLayoutShift);
            result.StaticMeshBytesBefore = metadataSerialSize;
            result.StaticMeshBytesAfter = payload.Length;

            PatchExportSerialSizeAndBulkDataStart(
                result.OutputUasset,
                metadataSerialSize,
                afterMetadataMesh.SerialOffset,
                payload.Length);

            var finalUexp = File.ReadAllBytes(result.OutputUexp);
            var prefix = finalUexp.AsSpan(0, serialOffset).ToArray();
            var tail = finalUexp.AsSpan(serialOffset + metadataSerialSize).ToArray();
            using var output = new MemoryStream(prefix.Length + payload.Length + tail.Length);
            output.Write(prefix);
            output.Write(payload);
            output.Write(tail);
            var outputBytes = output.ToArray();
            result.UexpSha256After = Hash(outputBytes);
            File.WriteAllBytes(result.OutputUexp, outputBytes);

            var validation = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var validationMesh = FindStaticMeshExport(validation)
                ?? throw new InvalidOperationException("UAssetAPI could not reopen the generated OBJ mesh.");
            if (validationMesh.SerialSize != payload.Length ||
                validation.FolderName.ToString() != result.OutputPackagePath ||
                !validationMesh.ObjectName.ToString().Equals(UnrealPathUtil.AssetName(result.OutputPackagePath), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The generated OBJ static-mesh metadata did not persist.");
            }
            ValidateStaticMaterialSlots(validationMesh, mesh.DeclaredMaterialSlots);
            ValidateCookedMaterialSections(validationMesh, mesh.DeclaredMaterialSlots);

            result.Status = "created";
            result.Log.Add($"Parsed {result.VertexCount} flattened vertices and {result.TriangleCount} double-sided triangles from the OBJ.");
            result.Log.Add($"Mapped {mesh.Sections.Count} active OBJ material section(s) onto {mesh.DeclaredMaterialSlots.Count} stable slot declaration(s).");
            result.Log.Add($"Applied mesh transform: scale={request.Scale:0.###}, offset=({request.OffsetX:0.###}, {request.OffsetY:0.###}, {request.OffsetZ:0.###}), rotation=({request.RotationPitch:0.###}, {request.RotationYaw:0.###}, {request.RotationRoll:0.###}).");
            result.Log.Add("Expanded the final StaticMesh export's inline position, tangent, UV, and active index buffers.");
            result.Log.Add("Preserved the donor collision shell and package identity while rebuilding its material sections.");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
            result.TransientFileLock = FileLockUtil.IsTransient(ex);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(result.OutputUasset))
            {
                result.ReportPath = Path.ChangeExtension(result.OutputUasset, ".obj-probe-report.json");
                File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
            }
        }

        return result;
    }

    /// <summary>Writes the cooked OBJ geometry in glTF meters for Batcomputer's local viewer.</summary>
    public static void WritePreviewGlb(
        string objPath,
        string outputPath,
        float scale,
        float offsetX,
        float offsetY,
        float offsetZ,
        float rotationPitch = 0f,
        float rotationYaw = 0f,
        float rotationRoll = 0f,
        IReadOnlyList<CustomStaticMeshMaterialSlot>? materialSlots = null)
    {
        if (!File.Exists(objPath))
        {
            throw new FileNotFoundException("The custom mesh OBJ was not found.", objPath);
        }
        if (!float.IsFinite(scale) || scale is < 0.001f or > 1000f)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Preview scale must be between 0.001 and 1000.");
        }

        // StaticMesh payloads use Unreal centimeters. CUE4Parse exports the character GLBs in
        // meters, so convert the authored import transform before the two meet in the viewer.
        const float unrealUnitsToMeters = 0.01f;
        var mesh = ParseObj(
            objPath,
            scale * unrealUnitsToMeters,
            new Vector3(offsetX, offsetY, offsetZ) * unrealUnitsToMeters,
            rotationPitch,
            rotationYaw,
            rotationRoll,
            materialSlots);
        // The cooked payload is UE space (X forward, Y right, Z up). CUE4Parse's
        // character GLBs use (X, Z, -Y), so use that same basis here rather than
        // handing the browser raw UE vertices.
        var positions = mesh.Vertices.Select(vertex => UeToGltf(vertex.Position)).ToArray();
        var min = new Vector3(positions.Min(value => value.X), positions.Min(value => value.Y), positions.Min(value => value.Z));
        var max = new Vector3(positions.Max(value => value.X), positions.Max(value => value.Y), positions.Max(value => value.Z));

        using var binary = new MemoryStream();
        var positionOffset = checked((int)binary.Length);
        foreach (var vertex in mesh.Vertices)
        {
            var position = UeToGltf(vertex.Position);
            WriteSingle(binary, position.X);
            WriteSingle(binary, position.Y);
            WriteSingle(binary, position.Z);
        }
        var positionLength = checked((int)binary.Length - positionOffset);
        Align4(binary);

        var normalOffset = checked((int)binary.Length);
        foreach (var vertex in mesh.Vertices)
        {
            var normal = UeToGltf(vertex.Normal);
            WriteSingle(binary, normal.X);
            WriteSingle(binary, normal.Y);
            WriteSingle(binary, normal.Z);
        }
        var normalLength = checked((int)binary.Length - normalOffset);
        Align4(binary);

        var uvOffset = checked((int)binary.Length);
        foreach (var vertex in mesh.Vertices)
        {
            WriteSingle(binary, vertex.Uv.U);
            WriteSingle(binary, 1f - vertex.Uv.V);
        }
        var uvLength = checked((int)binary.Length - uvOffset);
        Align4(binary);

        var bufferViews = new List<object>
        {
            new { buffer = 0, byteOffset = positionOffset, byteLength = positionLength, target = 34962 },
            new { buffer = 0, byteOffset = normalOffset, byteLength = normalLength, target = 34962 },
            new { buffer = 0, byteOffset = uvOffset, byteLength = uvLength, target = 34962 },
        };
        var accessors = new List<object>
        {
            new
            {
                bufferView = 0,
                componentType = 5126,
                count = mesh.Vertices.Count,
                type = "VEC3",
                min = new[] { min.X, min.Y, min.Z },
                max = new[] { max.X, max.Y, max.Z },
            },
            new { bufferView = 1, componentType = 5126, count = mesh.Vertices.Count, type = "VEC3" },
            new { bufferView = 2, componentType = 5126, count = mesh.Vertices.Count, type = "VEC2" },
        };
        var primitives = new List<object>();
        var materials = new List<object>();
        for (var sectionIndex = 0; sectionIndex < mesh.Sections.Count; sectionIndex++)
        {
            var section = mesh.Sections[sectionIndex];
            var indexOffset = checked((int)binary.Length);
            WriteIndices(binary, section.Indices);
            var indexLength = checked((int)binary.Length - indexOffset);
            Align4(binary);

            var bufferView = bufferViews.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = indexOffset, byteLength = indexLength, target = 34963 });
            var accessor = accessors.Count;
            accessors.Add(new { bufferView, componentType = 5123, count = section.Indices.Count, type = "SCALAR" });
            var material = materials.Count;
            materials.Add(new { name = section.StableSlotName });
            primitives.Add(new
            {
                attributes = new Dictionary<string, int>
                {
                    ["POSITION"] = 0,
                    ["NORMAL"] = 1,
                    ["TEXCOORD_0"] = 2,
                },
                indices = accessor,
                material,
                mode = 4,
            });
        }

        var json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0", generator = "Batcomputer OBJ preview" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[] { new { primitives } },
            materials,
            buffers = new[] { new { byteLength = binary.Length } },
            bufferViews,
            accessors,
        }, JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonLength = jsonBytes.Length;
        Array.Resize(ref jsonBytes, Align4(jsonLength));
        Array.Fill(jsonBytes, (byte)' ', jsonLength, jsonBytes.Length - jsonLength);
        var binaryBytes = binary.ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46546C67); // glTF
        writer.Write(2);
        writer.Write(checked(12 + 8 + jsonBytes.Length + 8 + binaryBytes.Length));
        writer.Write(jsonBytes.Length);
        writer.Write(0x4E4F534A); // JSON
        writer.Write(jsonBytes);
        writer.Write(binaryBytes.Length);
        writer.Write(0x004E4942); // BIN\0
        writer.Write(binaryBytes);
    }

    private static void Align4(Stream stream)
    {
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static int Align4(int length)
    {
        return (length + 3) & ~3;
    }

    private static Vector3 UeToGltf(Vector3 value) => new(value.X, value.Z, -value.Y);

    private static Vector3 RotateUnreal(Vector3 value, float pitch, float yaw, float roll)
    {
        const float degreesToRadians = MathF.PI / 180f;
        var yawRotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, yaw * degreesToRadians);
        var pitchRotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, pitch * degreesToRadians);
        var rollRotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, roll * degreesToRadians);
        var rotation = System.Numerics.Quaternion.Normalize(yawRotation * pitchRotation * rollRotation);
        var transformed = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(value.X, value.Y, value.Z), rotation);
        return new Vector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static byte[] BuildPayload(byte[] source, ImportedMesh mesh, Result result, int sourceLayoutShift = 0)
    {
        EnsureMaterialSections(mesh);
        var sectionDelta = checked((mesh.Sections.Count - 1) * SectionRecordSize);
        var shiftedSectionDataOffset = checked(SectionDataOffset + sourceLayoutShift);
        var shiftedLodCookedOutOffset = checked(LodCookedOutOffset + sourceLayoutShift);
        var shiftedPositionDataOffset = checked(PositionDataOffset + sourceLayoutShift);
        var shiftedTangentDataOffset = checked(TangentDataOffset + sourceLayoutShift);
        var shiftedUvDataOffset = checked(UvDataOffset + sourceLayoutShift);
        var shiftedIndex0DataOffset = checked(Index0DataOffset + sourceLayoutShift);
        var shiftedIndex1DataOffset = checked(Index1DataOffset + sourceLayoutShift);
        var oldPositionEnd = shiftedPositionDataOffset + DonorVertexCount * PositionStride;
        var oldTangentEnd = shiftedTangentDataOffset + DonorVertexCount * TangentStride;
        var oldUvEnd = shiftedUvDataOffset + DonorVertexCount * UvStride;
        var oldIndex0End = shiftedIndex0DataOffset + DonorIndexBytes;
        var oldIndex1End = shiftedIndex1DataOffset + DonorIndexBytes;
        if (oldIndex1End > source.Length)
        {
            throw new InvalidOperationException("The donor's LOD0 buffers do not match the verified Nightwing layout.");
        }

        var vertexCount = mesh.Vertices.Count;
        var indexBytes = checked(mesh.Indices.Count * sizeof(ushort));
        var positionDelta = checked((vertexCount - DonorVertexCount) * PositionStride);
        var tangentDelta = checked((vertexCount - DonorVertexCount) * TangentStride);
        var uvDelta = checked((vertexCount - DonorVertexCount) * UvStride);
        var vertexDelta = checked(positionDelta + tangentDelta + uvDelta);
        var indexDelta = indexBytes - DonorIndexBytes;
        var generatedShift = checked(sectionDelta + vertexDelta + 2 * indexDelta);
        var totalLayoutShift = checked(sourceLayoutShift + generatedShift);
        // FStaticMeshBuffersSize counts raw position/tangent/UV bytes plus every active index
        // buffer. This donor carries main and depth-only indices; both are replaced below.
        var serializedBuffersSize = checked(
            vertexCount * (PositionStride + TangentStride + UvStride) +
            2 * indexBytes);

        var minVertexIndex = mesh.Indices.Min(index => (int)index);
        var maxVertexIndex = mesh.Indices.Max(index => (int)index);
        if (minVertexIndex < 0 || maxVertexIndex >= vertexCount)
        {
            throw new InvalidOperationException(
                $"The OBJ index range {minVertexIndex}..{maxVertexIndex} is outside its {vertexCount} flattened vertices.");
        }

        using var output = new MemoryStream(checked(source.Length + generatedShift));
        output.Write(source, 0, shiftedSectionDataOffset);
        for (var i = 0; i < mesh.Sections.Count; i++)
        {
            output.Write(source, shiftedSectionDataOffset, SectionRecordSize);
        }
        output.Write(
            source,
            shiftedLodCookedOutOffset,
            shiftedPositionDataOffset - shiftedLodCookedOutOffset);
        WritePositions(output, mesh.Vertices);
        output.Write(source, oldPositionEnd, shiftedTangentDataOffset - oldPositionEnd);
        WriteTangents(output, mesh.Vertices);
        output.Write(source, oldTangentEnd, shiftedUvDataOffset - oldTangentEnd);
        WriteUvs(output, mesh.Vertices);
        output.Write(source, oldUvEnd, shiftedIndex0DataOffset - oldUvEnd);
        WriteIndices(output, mesh.Indices);
        output.Write(source, oldIndex0End, shiftedIndex1DataOffset - oldIndex0End);
        WriteIndices(output, mesh.Indices);
        output.Write(source, oldIndex1End, source.Length - oldIndex1End);

        var payload = output.ToArray();
        WriteInt32(payload, SectionsCountOffset + sourceLayoutShift, mesh.Sections.Count);
        for (var sectionIndex = 0; sectionIndex < mesh.Sections.Count; sectionIndex++)
        {
            var section = mesh.Sections[sectionIndex];
            var recordOffset = checked(SectionDataOffset + sourceLayoutShift + sectionIndex * SectionRecordSize);
            WriteInt32(payload, recordOffset + SectionMaterialIndexRelativeOffset, section.Slot);
            WriteInt32(payload, recordOffset + SectionFirstIndexRelativeOffset, section.FirstIndex);
            WriteInt32(payload, recordOffset + SectionTriangleCountRelativeOffset, section.Indices.Count / 3);
            WriteInt32(payload, recordOffset + SectionMinVertexIndexRelativeOffset, section.Indices.Min(index => (int)index));
            WriteInt32(payload, recordOffset + SectionMaxVertexIndexRelativeOffset, section.Indices.Max(index => (int)index));
            WriteRenderBounds(payload, recordOffset + SectionRenderBoundsRelativeOffset, section.Bounds);
        }

        var postSectionShift = checked(sourceLayoutShift + sectionDelta);
        WriteInt32(payload, PositionCountOffset0 + postSectionShift, vertexCount);
        WriteInt32(payload, PositionCountOffset1 + postSectionShift, vertexCount);
        WriteInt32(payload, TangentCountOffset0 + postSectionShift + positionDelta, vertexCount);
        WriteInt32(payload, TangentCountOffset1 + postSectionShift + positionDelta, vertexCount);
        WriteInt32(payload, UvCountOffset + postSectionShift + positionDelta + tangentDelta, vertexCount);
        WriteInt32(payload, Index0SizeOffset + postSectionShift + vertexDelta, indexBytes);
        WriteInt32(payload, Index1SizeOffset + postSectionShift + vertexDelta + indexDelta, indexBytes);
        WriteInt32(payload, SerializedBuffersSizeOffset + totalLayoutShift, serializedBuffersSize);
        WriteInt32(payload, DepthOnlyBufferSizeOffset + totalLayoutShift, indexBytes);
        WriteInt32(payload, ReversedBuffersSizeOffset + totalLayoutShift, 0);

        WriteRenderBounds(payload, GlobalRenderBoundsOffset + sourceLayoutShift, mesh.Bounds);
        WriteRenderBounds(payload, FinalRenderBoundsOffset + totalLayoutShift, mesh.Bounds);
        ValidateGeneratedPayload(
            payload,
            mesh,
            sourceLayoutShift,
            sectionDelta,
            positionDelta,
            tangentDelta,
            vertexDelta,
            indexDelta,
            totalLayoutShift,
            indexBytes,
            serializedBuffersSize);
        result.Log.Add(
            $"Validated {mesh.Sections.Count} LOD0 material section(s), vertices {minVertexIndex}..{maxVertexIndex}; " +
            $"serialized buffers {serializedBuffersSize:N0} bytes (depth-only {indexBytes:N0}).");
        return payload;
    }

    private static ImportedMesh ParseObj(
        string objPath,
        float scale,
        Vector3 offset,
        float rotationPitch = 0f,
        float rotationYaw = 0f,
        float rotationRoll = 0f,
        IReadOnlyList<CustomStaticMeshMaterialSlot>? preferredMaterialSlots = null,
        string legacyMaterialPath = "")
    {
        var positions = new List<Vector3> { default };
        var uvs = new List<Vector2> { default };
        var normals = new List<Vector3> { default };
        var mesh = new ImportedMesh();
        var vertexMap = new Dictionary<ObjKey, ushort>();
        var sectionsByMaterial = new Dictionary<string, ImportedSection>(StringComparer.Ordinal);
        var sourceSections = new List<ImportedSection>();
        var currentMaterial = "Default";

        ImportedSection CurrentSection()
        {
            if (sectionsByMaterial.TryGetValue(currentMaterial, out var existing))
            {
                return existing;
            }
            if (sourceSections.Count >= MaxMaterialSlots)
            {
                throw new InvalidOperationException($"Custom OBJ meshes support up to {MaxMaterialSlots} material slots.");
            }
            var created = new ImportedSection { SourceMaterialName = currentMaterial };
            sectionsByMaterial.Add(currentMaterial, created);
            sourceSections.Add(created);
            return created;
        }

        foreach (var rawLine in File.ReadLines(objPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }
            switch (tokens[0])
            {
                case "v" when tokens.Length >= 4:
                    positions.Add(new Vector3(ParseFloat(tokens[1]), ParseFloat(tokens[2]), ParseFloat(tokens[3])));
                    break;
                case "vt" when tokens.Length >= 3:
                    uvs.Add(new Vector2(ParseFloat(tokens[1]), ParseFloat(tokens[2])));
                    break;
                case "vn" when tokens.Length >= 4:
                    normals.Add(Normalize(new Vector3(ParseFloat(tokens[1]), ParseFloat(tokens[2]), ParseFloat(tokens[3]))));
                    break;
                case "usemtl":
                    currentMaterial = line[tokens[0].Length..].Trim();
                    if (string.IsNullOrWhiteSpace(currentMaterial))
                    {
                        currentMaterial = "Default";
                    }
                    break;
                case "f" when tokens.Length >= 4:
                {
                    var section = CurrentSection();
                    var face = tokens.Skip(1).Select(token => ParseObjKey(token, positions.Count - 1, uvs.Count - 1, normals.Count - 1)).ToArray();
                    for (var i = 1; i < face.Length - 1; i++)
                    {
                        var first = AddVertex(face[0], positions, uvs, normals, vertexMap, mesh, scale);
                        var second = AddVertex(face[i], positions, uvs, normals, vertexMap, mesh, scale);
                        var third = AddVertex(face[i + 1], positions, uvs, normals, vertexMap, mesh, scale);
                        section.Indices.Add(first);
                        section.Indices.Add(second);
                        section.Indices.Add(third);
                        section.Indices.Add(first);
                        section.Indices.Add(third);
                        section.Indices.Add(second);
                    }
                    break;
                }
            }
        }

        if (mesh.Vertices.Count == 0)
        {
            throw new InvalidOperationException("The OBJ contains no usable faces.");
        }
        var declarations = ReconcileMaterialSlots(
            preferredMaterialSlots,
            sourceSections.Select(section => section.SourceMaterialName),
            legacyMaterialPath);
        mesh.DeclaredMaterialSlots.AddRange(declarations);
        var activeSlots = declarations.ToDictionary(slot => slot.SourceMaterialName, StringComparer.Ordinal);
        foreach (var section in sourceSections)
        {
            if (!activeSlots.TryGetValue(section.SourceMaterialName, out var declaration))
            {
                throw new InvalidOperationException($"OBJ material '{section.SourceMaterialName}' did not receive a stable slot.");
            }
            section.Slot = declaration.Slot;
            section.StableSlotName = declaration.StableSlotName;
            mesh.Sections.Add(section);
        }
        mesh.Sections.Sort((left, right) => left.Slot.CompareTo(right.Slot));
        foreach (var section in mesh.Sections)
        {
            section.FirstIndex = mesh.Indices.Count;
            mesh.Indices.AddRange(section.Indices);
        }
        CenterAndBuildFrames(mesh, offset, rotationPitch, rotationYaw, rotationRoll);
        foreach (var section in mesh.Sections)
        {
            section.Bounds = ComputeBounds(mesh, section.Indices);
        }
        return mesh;
    }

    /// <summary>
    /// Reads the OBJ's active <c>usemtl</c> sections. Repeated declarations are consolidated in
    /// first-use order; faces before the first declaration use the stable <c>Default</c> slot.
    /// </summary>
    public static List<ObjMaterialSlot> InspectObjMaterialSlots(string objPath)
    {
        if (!File.Exists(objPath))
        {
            throw new FileNotFoundException("The custom mesh OBJ was not found.", objPath);
        }
        var mesh = ParseObj(objPath, 1f, default);
        return DescribeMaterialSlots(mesh);
    }

    /// <summary>
    /// Reconciles a freshly observed set of OBJ material names with saved declarations. Existing
    /// source names retain their relative order and material paths, new names append, and removed
    /// names disappear. Runtime slots are then compacted so every output stays contiguous.
    /// </summary>
    public static List<CustomStaticMeshMaterialSlot> ReconcileMaterialSlots(
        IReadOnlyList<CustomStaticMeshMaterialSlot>? existingSlots,
        IEnumerable<string> sourceMaterialNames,
        string legacyMaterialPath = "")
    {
        var priorBySource = (existingSlots ?? Array.Empty<CustomStaticMeshMaterialSlot>())
            .Where(slot => slot is not null && slot.Slot is >= 0 and < MaxMaterialSlots)
            .OrderBy(slot => slot.Slot)
            .GroupBy(slot => NormalizeSourceMaterialName(slot.SourceMaterialName), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(
                slot => NormalizeSourceMaterialName(slot.SourceMaterialName),
                slot => slot,
                StringComparer.Ordinal);
        var sourceOrder = sourceMaterialNames
            .Select(NormalizeSourceMaterialName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sourceOrder.Count > MaxMaterialSlots)
        {
            throw new InvalidOperationException($"Custom OBJ meshes support up to {MaxMaterialSlots} material slots.");
        }

        // Keep the relative order of surviving saved names so merely reordering usemtl blocks does
        // not swap slots. New names append in first-use order. Removed names are intentionally gone.
        var orderedNames = priorBySource.Values
            .OrderBy(slot => slot.Slot)
            .Select(slot => NormalizeSourceMaterialName(slot.SourceMaterialName))
            .Where(sourceOrder.Contains)
            .Distinct(StringComparer.Ordinal)
            .Concat(sourceOrder.Where(name => !priorBySource.ContainsKey(name)))
            .ToList();
        var reconciled = new List<CustomStaticMeshMaterialSlot>(orderedNames.Count);
        for (var slot = 0; slot < orderedNames.Count; slot++)
        {
            var sourceName = orderedNames[slot];
            priorBySource.TryGetValue(sourceName, out var prior);
            reconciled.Add(new CustomStaticMeshMaterialSlot
            {
                Slot = slot,
                SourceMaterialName = sourceName,
                StableSlotName = StableMaterialSlotName(slot, sourceName),
                MaterialPath = prior?.MaterialPath ?? (slot == 0 ? legacyMaterialPath ?? "" : ""),
            });
        }
        return reconciled;
    }

    /// <summary>Returns declared slots, or a synthetic slot zero for a legacy one-material import.</summary>
    public static IReadOnlyList<CustomStaticMeshMaterialSlot> EffectiveMaterialSlots(CustomStaticMeshImport import)
    {
        if (import.MaterialSlots is { Count: > 0 })
        {
            return import.MaterialSlots
                .Where(slot => slot.Slot is >= 0 and < MaxMaterialSlots)
                .OrderBy(slot => slot.Slot)
                .GroupBy(slot => NormalizeSourceMaterialName(slot.SourceMaterialName), StringComparer.Ordinal)
                .Select(group => group.First())
                .Select((slot, index) => new CustomStaticMeshMaterialSlot
                {
                    Slot = index,
                    SourceMaterialName = NormalizeSourceMaterialName(slot.SourceMaterialName),
                    StableSlotName = StableMaterialSlotName(index, slot.SourceMaterialName),
                    MaterialPath = slot.MaterialPath ?? "",
                })
                .ToList();
        }
        return
        [
            new CustomStaticMeshMaterialSlot
            {
                Slot = 0,
                SourceMaterialName = "Default",
                StableSlotName = StableMaterialSlotName(0, "Default"),
                MaterialPath = import.MaterialPath ?? "",
            },
        ];
    }

    private static List<ObjMaterialSlot> DescribeMaterialSlots(ImportedMesh mesh) => mesh.Sections
        .Select(section => new ObjMaterialSlot
        {
            Slot = section.Slot,
            SourceMaterialName = section.SourceMaterialName,
            StableSlotName = section.StableSlotName,
            FirstIndex = section.FirstIndex,
            IndexCount = section.Indices.Count,
            MinVertexIndex = section.Indices.Min(index => (int)index),
            MaxVertexIndex = section.Indices.Max(index => (int)index),
        })
        .ToList();

    private static string NormalizeSourceMaterialName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();

    private static string StableMaterialSlotName(int slot, string? sourceMaterialName)
    {
        var source = NormalizeSourceMaterialName(sourceMaterialName);
        var sanitized = new StringBuilder(source.Length);
        var lastWasSeparator = false;
        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                sanitized.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sanitized.Append('_');
                lastWasSeparator = true;
            }
            if (sanitized.Length >= 48)
            {
                break;
            }
        }
        var suffix = sanitized.ToString().Trim('_');
        return $"BC_SLOT_{slot:D3}_{(suffix.Length == 0 ? "Default" : suffix)}";
    }

    private static void EnsureMaterialSections(ImportedMesh mesh)
    {
        if (mesh.Sections.Count > 0)
        {
            var flattened = mesh.Sections.SelectMany(section => section.Indices).ToArray();
            if (!flattened.SequenceEqual(mesh.Indices))
            {
                throw new InvalidOperationException("The OBJ material sections no longer match its combined index buffer.");
            }
            return;
        }
        var section = new ImportedSection
        {
            Slot = 0,
            SourceMaterialName = "Default",
            StableSlotName = StableMaterialSlotName(0, "Default"),
            FirstIndex = 0,
            Bounds = mesh.Bounds,
        };
        section.Indices.AddRange(mesh.Indices);
        mesh.Sections.Add(section);
        if (mesh.DeclaredMaterialSlots.Count == 0)
        {
            mesh.DeclaredMaterialSlots.Add(new CustomStaticMeshMaterialSlot
            {
                Slot = 0,
                SourceMaterialName = "Default",
                StableSlotName = section.StableSlotName,
            });
        }
    }

    private static Bounds ComputeBounds(ImportedMesh mesh, IReadOnlyList<ushort> indices)
    {
        if (indices.Count == 0)
        {
            return mesh.Bounds;
        }
        var vertices = indices.Select(index => mesh.Vertices[index].Position).ToArray();
        var min = new Vector3(vertices.Min(value => value.X), vertices.Min(value => value.Y), vertices.Min(value => value.Z));
        var max = new Vector3(vertices.Max(value => value.X), vertices.Max(value => value.Y), vertices.Max(value => value.Z));
        var center = (min + max) * 0.5f;
        var extent = (max - min) * 0.5f;
        return new Bounds(center, extent, MathF.Sqrt(Dot(extent, extent)));
    }

    private static ushort AddVertex(
        ObjKey key,
        List<Vector3> positions,
        List<Vector2> uvs,
        List<Vector3> normals,
        Dictionary<ObjKey, ushort> vertexMap,
        ImportedMesh mesh,
        float scale)
    {
        if (vertexMap.TryGetValue(key, out var existing))
        {
            return existing;
        }
        if (mesh.Vertices.Count >= ushort.MaxValue)
        {
            throw new InvalidOperationException("This first OBJ writer supports up to 65,535 flattened vertices.");
        }
        if (key.Position <= 0 || key.Position >= positions.Count)
        {
            throw new InvalidOperationException("The OBJ references a position index outside its vertex list.");
        }

        var position = positions[key.Position];
        var convertedPosition = new Vector3(position.X * scale, -position.Z * scale, position.Y * scale);
        var normal = key.Normal > 0 && key.Normal < normals.Count
            ? ConvertDirection(normals[key.Normal])
            : default;
        var uv = key.Uv > 0 && key.Uv < uvs.Count ? uvs[key.Uv] : new Vector2(0.5f, 0.5f);
        var index = checked((ushort)mesh.Vertices.Count);
        mesh.Vertices.Add(new ImportedVertex { Position = convertedPosition, Normal = normal, Uv = uv });
        vertexMap.Add(key, index);
        return index;
    }

    private static void CenterAndBuildFrames(
        ImportedMesh mesh,
        Vector3 offset,
        float rotationPitch,
        float rotationYaw,
        float rotationRoll)
    {
        var min = new Vector3(mesh.Vertices.Min(vertex => vertex.Position.X), mesh.Vertices.Min(vertex => vertex.Position.Y), mesh.Vertices.Min(vertex => vertex.Position.Z));
        var max = new Vector3(mesh.Vertices.Max(vertex => vertex.Position.X), mesh.Vertices.Max(vertex => vertex.Position.Y), mesh.Vertices.Max(vertex => vertex.Position.Z));
        var center = (min + max) * 0.5f;
        foreach (var vertex in mesh.Vertices)
        {
            vertex.Position = RotateUnreal(vertex.Position - center, rotationPitch, rotationYaw, rotationRoll) + offset;
            vertex.Normal = RotateUnreal(vertex.Normal, rotationPitch, rotationYaw, rotationRoll);
        }

        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            var edge1 = b.Position - a.Position;
            var edge2 = c.Position - a.Position;
            var faceNormal = Cross(edge1, edge2);
            a.AccumulatedNormal += faceNormal;
            b.AccumulatedNormal += faceNormal;
            c.AccumulatedNormal += faceNormal;
            var du1 = b.Uv.U - a.Uv.U;
            var dv1 = b.Uv.V - a.Uv.V;
            var du2 = c.Uv.U - a.Uv.U;
            var dv2 = c.Uv.V - a.Uv.V;
            var determinant = du1 * dv2 - du2 * dv1;
            if (MathF.Abs(determinant) > 0.000001f)
            {
                var tangent = (edge1 * dv2 - edge2 * dv1) * (1f / determinant);
                a.AccumulatedTangent += tangent;
                b.AccumulatedTangent += tangent;
                c.AccumulatedTangent += tangent;
            }
        }

        foreach (var vertex in mesh.Vertices)
        {
            var normal = LengthSquared(vertex.Normal) > 0.000001f ? Normalize(vertex.Normal) : Normalize(vertex.AccumulatedNormal);
            var tangent = vertex.AccumulatedTangent - normal * Dot(normal, vertex.AccumulatedTangent);
            if (LengthSquared(tangent) <= 0.000001f)
            {
                tangent = MathF.Abs(normal.Z) < 0.9f ? Cross(new Vector3(0, 0, 1), normal) : Cross(new Vector3(0, 1, 0), normal);
            }
            vertex.Normal = normal;
            vertex.Tangent = Normalize(tangent);
        }

        var transformedMin = new Vector3(
            mesh.Vertices.Min(vertex => vertex.Position.X),
            mesh.Vertices.Min(vertex => vertex.Position.Y),
            mesh.Vertices.Min(vertex => vertex.Position.Z));
        var transformedMax = new Vector3(
            mesh.Vertices.Max(vertex => vertex.Position.X),
            mesh.Vertices.Max(vertex => vertex.Position.Y),
            mesh.Vertices.Max(vertex => vertex.Position.Z));
        var transformedCenter = (transformedMin + transformedMax) * 0.5f;
        var extent = (transformedMax - transformedMin) * 0.5f;
        mesh.Bounds = new Bounds(transformedCenter, extent, MathF.Sqrt(Dot(extent, extent)));
    }

    private static ObjKey ParseObjKey(string token, int positionCount, int uvCount, int normalCount)
    {
        var parts = token.Split('/');
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new InvalidOperationException("The OBJ contains a face corner with no position index.");
        }
        return new ObjKey(
            ResolveIndex(parts[0], positionCount),
            parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? ResolveIndex(parts[1], uvCount) : 0,
            parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? ResolveIndex(parts[2], normalCount) : 0);
    }

    private static int ResolveIndex(string value, int count)
    {
        var parsed = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return parsed < 0 ? count + parsed + 1 : parsed;
    }

    private static float ParseFloat(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static Vector3 ConvertDirection(Vector3 value) => Normalize(new Vector3(value.X, -value.Z, value.Y));
    private static float Dot(Vector3 left, Vector3 right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    private static Vector3 Cross(Vector3 left, Vector3 right) => new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    private static float LengthSquared(Vector3 value) => Dot(value, value);
    private static Vector3 Normalize(Vector3 value)
    {
        var lengthSquared = LengthSquared(value);
        return lengthSquared <= 0.00000001f ? new Vector3(0, 0, 1) : value * (1f / MathF.Sqrt(lengthSquared));
    }

    private static void WritePositions(Stream output, IReadOnlyList<ImportedVertex> vertices)
    {
        foreach (var vertex in vertices)
        {
            WriteSingle(output, vertex.Position.X);
            WriteSingle(output, vertex.Position.Y);
            WriteSingle(output, vertex.Position.Z);
        }
    }

    private static void WriteTangents(Stream output, IReadOnlyList<ImportedVertex> vertices)
    {
        foreach (var vertex in vertices)
        {
            WritePackedNormal(output, vertex.Tangent, 127);
            WritePackedNormal(output, vertex.Normal, 127);
        }
    }

    private static void WriteUvs(Stream output, IReadOnlyList<ImportedVertex> vertices)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        foreach (var vertex in vertices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, BitConverter.HalfToUInt16Bits((Half)vertex.Uv.U));
            output.Write(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, BitConverter.HalfToUInt16Bits((Half)(1f - vertex.Uv.V)));
            output.Write(bytes);
        }
    }

    private static void WriteIndices(Stream output, IReadOnlyList<ushort> indices)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        foreach (var index in indices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, index);
            output.Write(bytes);
        }
    }

    private static void WriteSingle(Stream output, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        output.Write(bytes);
    }

    private static void WritePackedNormal(Stream output, Vector3 value, byte handedness)
    {
        output.WriteByte(PackNormal(value.X));
        output.WriteByte(PackNormal(value.Y));
        output.WriteByte(PackNormal(value.Z));
        output.WriteByte(handedness);
    }

    private static byte PackNormal(float value) => unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(value * 127f), -127, 127));

    private static void ValidateDonorPayload(byte[] uexp, int serialOffset, int serialSize)
    {
        if (serialOffset < 0 || serialSize != DonorStaticMeshSerialSize ||
            (long)serialOffset + serialSize > uexp.Length)
        {
            throw new InvalidOperationException("The Nightwing donor's verified LOD0 layout changed.");
        }

        void RequireInt(int relativeOffset, int expected, string field) =>
            RequireInt32Value(uexp, serialOffset + relativeOffset, expected, $"Nightwing donor {field}");
        void RequireByte(int relativeOffset, byte expected, string field) =>
            RequireByteValue(uexp, serialOffset + relativeOffset, expected, $"Nightwing donor {field}");

        RequireInt(SectionsCountOffset, 1, "section count");
        RequireInt(SectionTriangleCountOffset, DonorTriangleCount, "section triangle count");
        RequireInt(SectionMinVertexIndexOffset, 0, "section minimum vertex");
        RequireInt(SectionMaxVertexIndexOffset, DonorVertexCount - 1, "section maximum vertex");
        RequireInt(LodCookedOutOffset, 0, "cooked-out flag");
        RequireInt(LodBuffersInlinedOffset, 1, "inline-buffer flag");
        RequireInt(LodHasRayTracingGeometryOffset, 0, "ray-tracing geometry flag");
        RequireByte(LodBufferGlobalStripFlagsOffset, DonorGlobalStripFlags, "LOD buffer global strip flags");
        RequireByte(LodBufferClassStripFlagsOffset, DonorClassStripFlags, "LOD buffer class strip flags");

        RequireInt(PositionStrideOffset, PositionStride, "position stride");
        RequireInt(PositionCountOffset0, DonorVertexCount, "position vertex count");
        RequireInt(PositionElementSizeOffset, PositionStride, "position element size");
        RequireInt(PositionCountOffset1, DonorVertexCount, "position bulk count");
        RequireByte(StaticMeshVertexGlobalStripFlagsOffset, DonorGlobalStripFlags, "static vertex global strip flags");
        RequireByte(StaticMeshVertexClassStripFlagsOffset, 0, "static vertex class strip flags");
        RequireInt(NumTexCoordsOffset, 1, "texture-coordinate channel count");
        RequireInt(TangentCountOffset0, DonorVertexCount, "tangent vertex count");
        RequireInt(TangentElementSizeOffset, TangentStride, "tangent element size");
        RequireInt(TangentCountOffset1, DonorVertexCount, "tangent bulk count");
        RequireInt(UvElementSizeOffset, UvStride, "UV element size");
        RequireInt(UvCountOffset, DonorVertexCount, "UV bulk count");
        RequireByte(ColorVertexGlobalStripFlagsOffset, DonorGlobalStripFlags, "colour vertex global strip flags");
        RequireByte(ColorVertexClassStripFlagsOffset, 0, "colour vertex class strip flags");
        RequireInt(ColorVertexStrideOffset, 0, "colour vertex stride");
        RequireInt(ColorVertexCountOffset, 0, "colour vertex count");

        RequireInt(Index0Is32BitOffset, 0, "main index width flag");
        RequireInt(Index0ElementSizeOffset, 1, "main index byte-array element size");
        RequireInt(Index0SizeOffset, DonorIndexBytes, "main index byte count");
        RequireInt(Index0ExpandTo32BitOffset, 0, "main index expansion flag");
        RequireInt(ReversedIndexIs32BitOffset, 0, "reversed index width flag");
        RequireInt(ReversedIndexElementSizeOffset, 1, "reversed index byte-array element size");
        RequireInt(ReversedIndexSizeOffset, 0, "reversed index byte count");
        RequireInt(ReversedIndexExpandTo32BitOffset, 0, "reversed index expansion flag");
        RequireInt(Index1Is32BitOffset, 0, "depth-only index width flag");
        RequireInt(Index1ElementSizeOffset, 1, "depth-only index byte-array element size");
        RequireInt(Index1SizeOffset, DonorIndexBytes, "depth-only index byte count");
        RequireInt(Index1ExpandTo32BitOffset, 0, "depth-only index expansion flag");
        RequireInt(ReversedDepthIndexIs32BitOffset, 0, "reversed-depth index width flag");
        RequireInt(ReversedDepthIndexElementSizeOffset, 1, "reversed-depth index byte-array element size");
        RequireInt(ReversedDepthIndexSizeOffset, 0, "reversed-depth index byte count");
        RequireInt(ReversedDepthIndexExpandTo32BitOffset, 0, "reversed-depth index expansion flag");

        RequireInt(SectionSamplerProbabilityCountOffset, 0, "section sampler probability count");
        RequireInt(SectionSamplerAliasCountOffset, 0, "section sampler alias count");
        RequireInt(MeshSamplerProbabilityCountOffset, 0, "mesh sampler probability count");
        RequireInt(MeshSamplerAliasCountOffset, 0, "mesh sampler alias count");
        RequireInt(SerializedBuffersSizeOffset, DonorSerializedBuffersSize, "serialized buffer-size summary");
        RequireInt(DepthOnlyBufferSizeOffset, DonorIndexBytes, "depth-only buffer-size summary");
        RequireInt(ReversedBuffersSizeOffset, 0, "reversed buffer-size summary");
    }

    private static void ValidateGeneratedPayload(
        byte[] payload,
        ImportedMesh mesh,
        int sourceLayoutShift,
        int sectionDelta,
        int positionDelta,
        int tangentDelta,
        int vertexDelta,
        int indexDelta,
        int totalLayoutShift,
        int indexBytes,
        int serializedBuffersSize)
    {
        var vertexCount = mesh.Vertices.Count;
        var postSectionShift = checked(sourceLayoutShift + sectionDelta);
        var uvHeaderShift = checked(postSectionShift + positionDelta + tangentDelta);
        var firstIndexTailShift = checked(postSectionShift + vertexDelta + indexDelta);

        if (payload.Length != checked(DonorStaticMeshSerialSize + totalLayoutShift))
        {
            throw new InvalidOperationException(
                $"Generated StaticMesh payload length {payload.Length:N0} did not match the expected " +
                $"{DonorStaticMeshSerialSize + totalLayoutShift:N0} bytes.");
        }

        void RequireInt(int offset, int expected, string field) =>
            RequireInt32Value(payload, offset, expected, $"generated {field}");
        void RequireByte(int offset, byte expected, string field) =>
            RequireByteValue(payload, offset, expected, $"generated {field}");

        RequireInt(SectionsCountOffset + sourceLayoutShift, mesh.Sections.Count, "section count");
        for (var sectionIndex = 0; sectionIndex < mesh.Sections.Count; sectionIndex++)
        {
            var section = mesh.Sections[sectionIndex];
            var recordOffset = checked(SectionDataOffset + sourceLayoutShift + sectionIndex * SectionRecordSize);
            RequireInt(recordOffset + SectionMaterialIndexRelativeOffset, section.Slot, $"section {sectionIndex} material slot");
            RequireInt(recordOffset + SectionFirstIndexRelativeOffset, section.FirstIndex, $"section {sectionIndex} first index");
            RequireInt(recordOffset + SectionTriangleCountRelativeOffset, section.Indices.Count / 3, $"section {sectionIndex} triangle count");
            RequireInt(recordOffset + SectionMinVertexIndexRelativeOffset, section.Indices.Min(index => (int)index), $"section {sectionIndex} minimum vertex");
            RequireInt(recordOffset + SectionMaxVertexIndexRelativeOffset, section.Indices.Max(index => (int)index), $"section {sectionIndex} maximum vertex");
        }
        RequireInt(LodCookedOutOffset + postSectionShift, 0, "cooked-out flag");
        RequireInt(LodBuffersInlinedOffset + postSectionShift, 1, "inline-buffer flag");
        RequireInt(LodHasRayTracingGeometryOffset + postSectionShift, 0, "ray-tracing geometry flag");
        RequireByte(LodBufferGlobalStripFlagsOffset + postSectionShift, DonorGlobalStripFlags, "LOD buffer global strip flags");
        RequireByte(LodBufferClassStripFlagsOffset + postSectionShift, DonorClassStripFlags, "LOD buffer class strip flags");

        RequireInt(PositionStrideOffset + postSectionShift, PositionStride, "position stride");
        RequireInt(PositionCountOffset0 + postSectionShift, vertexCount, "position vertex count");
        RequireInt(PositionElementSizeOffset + postSectionShift, PositionStride, "position element size");
        RequireInt(PositionCountOffset1 + postSectionShift, vertexCount, "position bulk count");
        RequireByte(StaticMeshVertexGlobalStripFlagsOffset + postSectionShift + positionDelta, DonorGlobalStripFlags, "static vertex global strip flags");
        RequireByte(StaticMeshVertexClassStripFlagsOffset + postSectionShift + positionDelta, 0, "static vertex class strip flags");
        RequireInt(NumTexCoordsOffset + postSectionShift + positionDelta, 1, "texture-coordinate channel count");
        RequireInt(TangentCountOffset0 + postSectionShift + positionDelta, vertexCount, "tangent vertex count");
        RequireInt(TangentElementSizeOffset + postSectionShift + positionDelta, TangentStride, "tangent element size");
        RequireInt(TangentCountOffset1 + postSectionShift + positionDelta, vertexCount, "tangent bulk count");
        RequireInt(UvElementSizeOffset + uvHeaderShift, UvStride, "UV element size");
        RequireInt(UvCountOffset + uvHeaderShift, vertexCount, "UV bulk count");
        RequireByte(ColorVertexGlobalStripFlagsOffset + postSectionShift + vertexDelta, DonorGlobalStripFlags, "colour vertex global strip flags");
        RequireByte(ColorVertexClassStripFlagsOffset + postSectionShift + vertexDelta, 0, "colour vertex class strip flags");
        RequireInt(ColorVertexStrideOffset + postSectionShift + vertexDelta, 0, "colour vertex stride");
        RequireInt(ColorVertexCountOffset + postSectionShift + vertexDelta, 0, "colour vertex count");

        RequireInt(Index0Is32BitOffset + postSectionShift + vertexDelta, 0, "main index width flag");
        RequireInt(Index0ElementSizeOffset + postSectionShift + vertexDelta, 1, "main index byte-array element size");
        RequireInt(Index0SizeOffset + postSectionShift + vertexDelta, indexBytes, "main index byte count");
        RequireInt(Index0ExpandTo32BitOffset + firstIndexTailShift, 0, "main index expansion flag");
        RequireInt(ReversedIndexIs32BitOffset + firstIndexTailShift, 0, "reversed index width flag");
        RequireInt(ReversedIndexElementSizeOffset + firstIndexTailShift, 1, "reversed index byte-array element size");
        RequireInt(ReversedIndexSizeOffset + firstIndexTailShift, 0, "reversed index byte count");
        RequireInt(ReversedIndexExpandTo32BitOffset + firstIndexTailShift, 0, "reversed index expansion flag");
        RequireInt(Index1Is32BitOffset + firstIndexTailShift, 0, "depth-only index width flag");
        RequireInt(Index1ElementSizeOffset + firstIndexTailShift, 1, "depth-only index byte-array element size");
        RequireInt(Index1SizeOffset + firstIndexTailShift, indexBytes, "depth-only index byte count");
        RequireInt(Index1ExpandTo32BitOffset + totalLayoutShift, 0, "depth-only index expansion flag");
        RequireInt(ReversedDepthIndexIs32BitOffset + totalLayoutShift, 0, "reversed-depth index width flag");
        RequireInt(ReversedDepthIndexElementSizeOffset + totalLayoutShift, 1, "reversed-depth index byte-array element size");
        RequireInt(ReversedDepthIndexSizeOffset + totalLayoutShift, 0, "reversed-depth index byte count");
        RequireInt(ReversedDepthIndexExpandTo32BitOffset + totalLayoutShift, 0, "reversed-depth index expansion flag");

        RequireInt(SectionSamplerProbabilityCountOffset + totalLayoutShift, 0, "section sampler probability count");
        RequireInt(SectionSamplerAliasCountOffset + totalLayoutShift, 0, "section sampler alias count");
        RequireInt(MeshSamplerProbabilityCountOffset + totalLayoutShift, 0, "mesh sampler probability count");
        RequireInt(MeshSamplerAliasCountOffset + totalLayoutShift, 0, "mesh sampler alias count");
        RequireInt(SerializedBuffersSizeOffset + totalLayoutShift, serializedBuffersSize, "serialized buffer-size summary");
        RequireInt(DepthOnlyBufferSizeOffset + totalLayoutShift, indexBytes, "depth-only buffer-size summary");
        RequireInt(ReversedBuffersSizeOffset + totalLayoutShift, 0, "reversed buffer-size summary");

        var mainIndexDataOffset = checked(Index0DataOffset + postSectionShift + vertexDelta);
        var depthIndexDataOffset = checked(Index1DataOffset + firstIndexTailShift);
        for (var i = 0; i < mesh.Indices.Count; i++)
        {
            var byteOffset = checked(i * sizeof(ushort));
            var expected = mesh.Indices[i];
            var mainIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.AsSpan(mainIndexDataOffset + byteOffset, sizeof(ushort)));
            var depthIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.AsSpan(depthIndexDataOffset + byteOffset, sizeof(ushort)));
            if (mainIndex != expected || depthIndex != expected)
            {
                throw new InvalidOperationException(
                    $"Generated StaticMesh index {i:N0} did not match in both main and depth-only buffers.");
            }
        }
    }

    internal static bool PayloadMetadataRegressionPasses()
    {
        try
        {
            var source = CreateVerifiedDonorPayloadFixture();
            ValidateDonorPayload(source, 0, source.Length);

            const int vertexCount = 600;
            const int indexCount = 2100;
            var mesh = new ImportedMesh
            {
                Bounds = new Bounds(new Vector3(0f, 0f, 0f), new Vector3(300f, 1f, 1f), 300.01f),
            };
            for (var i = 0; i < vertexCount; i++)
            {
                mesh.Vertices.Add(new ImportedVertex
                {
                    Position = new Vector3(i - vertexCount / 2f, i % 2, 0f),
                    Normal = new Vector3(0f, 0f, 1f),
                    Tangent = new Vector3(1f, 0f, 0f),
                    Uv = new Vector2((i % 32) / 31f, (i % 16) / 15f),
                });
            }
            for (var i = 0; i < indexCount; i++)
            {
                mesh.Indices.Add((ushort)(i % vertexCount));
            }

            _ = BuildPayload(source, mesh, new Result());
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool MultiMaterialObjRegressionPasses()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "batcomputer-obj-material-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var objPath = Path.Combine(tempRoot, "sections.obj");
            var glbPath = Path.Combine(tempRoot, "sections.glb");
            File.WriteAllLines(objPath,
            [
                "v 0 0 0",
                "v 1 0 0",
                "v 0 1 0",
                "v 1 1 0",
                "vt 0 0",
                "vt 1 0",
                "vt 0 1",
                "vt 1 1",
                "vn 0 0 1",
                "usemtl Black Plastic",
                "f 1/1/1 2/2/1 3/3/1",
                "usemtl Metal",
                "f 2/2/1 4/4/1 3/3/1",
                "usemtl Black Plastic",
                "f 1/1/1 3/3/1 4/4/1",
            ]);

            var prior = new List<CustomStaticMeshMaterialSlot>
            {
                new() { Slot = 0, SourceMaterialName = "Metal", MaterialPath = "/Game/Test/MI_Metal" },
                new() { Slot = 1, SourceMaterialName = "Black Plastic", MaterialPath = "/Game/Test/MI_Black" },
            };
            var reconciled = ReconcileMaterialSlots(prior, ["Black Plastic", "Accent", "Metal"]);
            var removed = ReconcileMaterialSlots(prior, ["Black Plastic"]);
            if (reconciled.Count != 3 ||
                reconciled[0].SourceMaterialName != "Metal" || reconciled[0].MaterialPath != "/Game/Test/MI_Metal" ||
                reconciled[1].SourceMaterialName != "Black Plastic" || reconciled[1].MaterialPath != "/Game/Test/MI_Black" ||
                reconciled[2].SourceMaterialName != "Accent" ||
                !reconciled.Select((slot, index) => slot.Slot == index).All(matches => matches) ||
                removed.Count != 1 || removed[0].Slot != 0 || removed[0].MaterialPath != "/Game/Test/MI_Black")
            {
                return false;
            }

            var mesh = ParseObj(objPath, 1f, default, preferredMaterialSlots: prior);
            if (mesh.Sections.Count != 2 ||
                mesh.Sections[0].SourceMaterialName != "Metal" || mesh.Sections[0].FirstIndex != 0 || mesh.Sections[0].Indices.Count != 6 ||
                mesh.Sections[1].SourceMaterialName != "Black Plastic" || mesh.Sections[1].FirstIndex != 6 || mesh.Sections[1].Indices.Count != 12 ||
                mesh.Indices.Count != 18)
            {
                return false;
            }

            const int simulatedStaticMaterialsGrowth = 31;
            const int staticMaterialInsertionOffset = 0x0A;
            var donor = CreateVerifiedDonorPayloadFixture();
            var expandedDonor = new byte[donor.Length + simulatedStaticMaterialsGrowth];
            donor.AsSpan(0, staticMaterialInsertionOffset).CopyTo(expandedDonor);
            donor.AsSpan(staticMaterialInsertionOffset)
                .CopyTo(expandedDonor.AsSpan(staticMaterialInsertionOffset + simulatedStaticMaterialsGrowth));
            ValidateShiftedDonorPayload(expandedDonor, simulatedStaticMaterialsGrowth);
            var payload = BuildPayload(expandedDonor, mesh, new Result(), simulatedStaticMaterialsGrowth);
            var sectionBase = SectionDataOffset + simulatedStaticMaterialsGrowth;
            if (ReadInt32(payload, SectionsCountOffset + simulatedStaticMaterialsGrowth) != 2 ||
                ReadInt32(payload, sectionBase + SectionMaterialIndexRelativeOffset) != 0 ||
                ReadInt32(payload, sectionBase + SectionFirstIndexRelativeOffset) != 0 ||
                ReadInt32(payload, sectionBase + SectionRecordSize + SectionMaterialIndexRelativeOffset) != 1 ||
                ReadInt32(payload, sectionBase + SectionRecordSize + SectionFirstIndexRelativeOffset) != 6)
            {
                return false;
            }

            WritePreviewGlb(objPath, glbPath, 1f, 0f, 0f, 0f, materialSlots: prior);
            var glb = File.ReadAllBytes(glbPath);
            if (glb.Length < 20 || BinaryPrimitives.ReadInt32LittleEndian(glb) != 0x46546C67)
            {
                return false;
            }
            var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12, sizeof(int)));
            using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
            var root = document.RootElement;
            var materials = root.GetProperty("materials");
            var primitives = root.GetProperty("meshes")[0].GetProperty("primitives");
            return materials.GetArrayLength() == 2 && primitives.GetArrayLength() == 2 &&
                   materials[0].GetProperty("name").GetString() == "BC_SLOT_000_Metal" &&
                   materials[1].GetProperty("name").GetString() == "BC_SLOT_001_Black_Plastic";
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Regression cleanup is best effort; the unique directory cannot affect later runs.
            }
        }
    }

    internal static bool MultiMaterialPackageValidationRegressionPasses()
    {
        try
        {
            var declarations = new List<CustomStaticMeshMaterialSlot>
            {
                new()
                {
                    Slot = 0,
                    SourceMaterialName = "Black Plastic",
                    StableSlotName = "BC_SLOT_000_Black_Plastic",
                },
                new()
                {
                    Slot = 1,
                    SourceMaterialName = "Metal",
                    StableSlotName = "BC_SLOT_001_Metal",
                },
            };
            var mesh = new ImportedMesh
            {
                Bounds = new Bounds(new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), 0.71f),
            };
            foreach (var position in new[]
                     {
                         new Vector3(0f, 0f, 0f),
                         new Vector3(1f, 0f, 0f),
                         new Vector3(0f, 1f, 0f),
                         new Vector3(1f, 1f, 0f),
                     })
            {
                mesh.Vertices.Add(new ImportedVertex
                {
                    Position = position,
                    Normal = new Vector3(0f, 0f, 1f),
                    Tangent = new Vector3(1f, 0f, 0f),
                    Uv = new Vector2(position.X, position.Y),
                });
            }
            var first = new ImportedSection
            {
                Slot = 0,
                SourceMaterialName = declarations[0].SourceMaterialName,
                StableSlotName = declarations[0].StableSlotName,
                FirstIndex = 0,
                Bounds = mesh.Bounds,
            };
            first.Indices.AddRange([0, 1, 2, 0, 2, 1]);
            var second = new ImportedSection
            {
                Slot = 1,
                SourceMaterialName = declarations[1].SourceMaterialName,
                StableSlotName = declarations[1].StableSlotName,
                FirstIndex = first.Indices.Count,
                Bounds = mesh.Bounds,
            };
            second.Indices.AddRange([1, 3, 2, 1, 2, 3]);
            mesh.Sections.AddRange([first, second]);
            mesh.Indices.AddRange(first.Indices);
            mesh.Indices.AddRange(second.Indices);
            mesh.DeclaredMaterialSlots.AddRange(declarations);

            const int staticMaterialsGrowth = 31;
            const int staticMaterialInsertionOffset = 0x0A;
            var donor = CreateVerifiedDonorPayloadFixture();
            var expandedDonor = new byte[donor.Length + staticMaterialsGrowth];
            donor.AsSpan(0, staticMaterialInsertionOffset).CopyTo(expandedDonor);
            donor.AsSpan(staticMaterialInsertionOffset)
                .CopyTo(expandedDonor.AsSpan(staticMaterialInsertionOffset + staticMaterialsGrowth));
            var payload = BuildPayload(expandedDonor, mesh, new Result(), staticMaterialsGrowth);
            var extras = payload.AsSpan(DonorReflectedPropertyBytes + staticMaterialsGrowth).ToArray();
            ValidateCookedMaterialSectionBytes(extras, declarations);

            var legacyExtras = donor.AsSpan(DonorReflectedPropertyBytes).ToArray();
            var legacyIndexDataOffset = Index0DataOffset - DonorReflectedPropertyBytes;
            for (var index = 0; index < DonorIndexBytes / sizeof(ushort); index++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    legacyExtras.AsSpan(
                        legacyIndexDataOffset + index * sizeof(ushort),
                        sizeof(ushort)),
                    (ushort)(index % DonorVertexCount));
            }
            ValidateCookedMaterialSectionBytes(
                legacyExtras,
                [new CustomStaticMeshMaterialSlot { Slot = 0, SourceMaterialName = "Default" }]);

            static bool Rejects(Action validation)
            {
                try
                {
                    validation();
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }

            var badMaterialIndex = extras.ToArray();
            WriteInt32(
                badMaterialIndex,
                CookedSectionDataExtrasOffset + SectionRecordSize + SectionMaterialIndexRelativeOffset,
                0);
            var badFirstIndex = extras.ToArray();
            WriteInt32(
                badFirstIndex,
                CookedSectionDataExtrasOffset + SectionRecordSize + SectionFirstIndexRelativeOffset,
                9);
            var badTriangleCount = extras.ToArray();
            WriteInt32(
                badTriangleCount,
                CookedSectionDataExtrasOffset + SectionTriangleCountRelativeOffset,
                999);
            var badStoredRange = extras.ToArray();
            WriteInt32(
                badStoredRange,
                CookedSectionDataExtrasOffset + SectionMaxVertexIndexRelativeOffset,
                3);

            var sectionDelta = SectionRecordSize;
            var vertexDelta = (mesh.Vertices.Count - DonorVertexCount) *
                              (PositionStride + TangentStride + UvStride);
            var indexDataOffset = Index0DataOffset - DonorReflectedPropertyBytes + sectionDelta + vertexDelta;
            var badVertexIndex = extras.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(
                badVertexIndex.AsSpan(indexDataOffset, sizeof(ushort)),
                (ushort)mesh.Vertices.Count);

            var validMetadata = new List<StaticMaterialSlotMetadata>
            {
                new(true, declarations[0].StableSlotName, declarations[0].StableSlotName),
                new(true, declarations[1].StableSlotName, declarations[1].StableSlotName),
            };
            ValidateStaticMaterialSlotMetadata(validMetadata, declarations);
            var nonStruct = validMetadata.ToArray();
            nonStruct[1] = new StaticMaterialSlotMetadata(false, null, null);
            var missingField = validMetadata.ToArray();
            missingField[0] = new StaticMaterialSlotMetadata(true, null, declarations[0].StableSlotName);
            var noneField = validMetadata.ToArray();
            noneField[0] = new StaticMaterialSlotMetadata(true, declarations[0].StableSlotName, "None");
            var wrongField = validMetadata.ToArray();
            wrongField[1] = new StaticMaterialSlotMetadata(
                true,
                declarations[0].StableSlotName,
                declarations[1].StableSlotName);

            return Rejects(() => ValidateCookedMaterialSectionBytes(badMaterialIndex, declarations)) &&
                   Rejects(() => ValidateCookedMaterialSectionBytes(badFirstIndex, declarations)) &&
                   Rejects(() => ValidateCookedMaterialSectionBytes(badTriangleCount, declarations)) &&
                   Rejects(() => ValidateCookedMaterialSectionBytes(badStoredRange, declarations)) &&
                   Rejects(() => ValidateCookedMaterialSectionBytes(badVertexIndex, declarations)) &&
                   Rejects(() => ValidateStaticMaterialSlotMetadata(nonStruct, declarations)) &&
                   Rejects(() => ValidateStaticMaterialSlotMetadata(missingField, declarations)) &&
                   Rejects(() => ValidateStaticMaterialSlotMetadata(noneField, declarations)) &&
                   Rejects(() => ValidateStaticMaterialSlotMetadata(wrongField, declarations));
        }
        catch
        {
            return false;
        }
    }

    private static byte[] CreateVerifiedDonorPayloadFixture()
    {
        var source = new byte[DonorStaticMeshSerialSize];
        WriteInt32(source, SectionsCountOffset, 1);
        WriteInt32(source, SectionTriangleCountOffset, DonorTriangleCount);
        WriteInt32(source, SectionMinVertexIndexOffset, 0);
        WriteInt32(source, SectionMaxVertexIndexOffset, DonorVertexCount - 1);
        WriteInt32(source, LodCookedOutOffset, 0);
        WriteInt32(source, LodBuffersInlinedOffset, 1);
        WriteInt32(source, LodHasRayTracingGeometryOffset, 0);
        source[LodBufferGlobalStripFlagsOffset] = DonorGlobalStripFlags;
        source[LodBufferClassStripFlagsOffset] = DonorClassStripFlags;

        WriteInt32(source, PositionStrideOffset, PositionStride);
        WriteInt32(source, PositionCountOffset0, DonorVertexCount);
        WriteInt32(source, PositionElementSizeOffset, PositionStride);
        WriteInt32(source, PositionCountOffset1, DonorVertexCount);
        source[StaticMeshVertexGlobalStripFlagsOffset] = DonorGlobalStripFlags;
        source[StaticMeshVertexClassStripFlagsOffset] = 0;
        WriteInt32(source, NumTexCoordsOffset, 1);
        WriteInt32(source, TangentCountOffset0, DonorVertexCount);
        WriteInt32(source, TangentElementSizeOffset, TangentStride);
        WriteInt32(source, TangentCountOffset1, DonorVertexCount);
        WriteInt32(source, UvElementSizeOffset, UvStride);
        WriteInt32(source, UvCountOffset, DonorVertexCount);
        source[ColorVertexGlobalStripFlagsOffset] = DonorGlobalStripFlags;
        source[ColorVertexClassStripFlagsOffset] = 0;
        WriteInt32(source, ColorVertexStrideOffset, 0);
        WriteInt32(source, ColorVertexCountOffset, 0);

        WriteInt32(source, Index0Is32BitOffset, 0);
        WriteInt32(source, Index0ElementSizeOffset, 1);
        WriteInt32(source, Index0SizeOffset, DonorIndexBytes);
        WriteInt32(source, Index0ExpandTo32BitOffset, 0);
        WriteInt32(source, ReversedIndexIs32BitOffset, 0);
        WriteInt32(source, ReversedIndexElementSizeOffset, 1);
        WriteInt32(source, ReversedIndexSizeOffset, 0);
        WriteInt32(source, ReversedIndexExpandTo32BitOffset, 0);
        WriteInt32(source, Index1Is32BitOffset, 0);
        WriteInt32(source, Index1ElementSizeOffset, 1);
        WriteInt32(source, Index1SizeOffset, DonorIndexBytes);
        WriteInt32(source, Index1ExpandTo32BitOffset, 0);
        WriteInt32(source, ReversedDepthIndexIs32BitOffset, 0);
        WriteInt32(source, ReversedDepthIndexElementSizeOffset, 1);
        WriteInt32(source, ReversedDepthIndexSizeOffset, 0);
        WriteInt32(source, ReversedDepthIndexExpandTo32BitOffset, 0);

        WriteInt32(source, SectionSamplerProbabilityCountOffset, 0);
        WriteInt32(source, SectionSamplerAliasCountOffset, 0);
        WriteInt32(source, MeshSamplerProbabilityCountOffset, 0);
        WriteInt32(source, MeshSamplerAliasCountOffset, 0);
        WriteInt32(source, SerializedBuffersSizeOffset, DonorSerializedBuffersSize);
        WriteInt32(source, DepthOnlyBufferSizeOffset, DonorIndexBytes);
        WriteInt32(source, ReversedBuffersSizeOffset, 0);
        return source;
    }

    private static void RequireInt32Value(byte[] data, int offset, int expected, string field)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
        {
            throw new InvalidOperationException($"The {field} lies outside the StaticMesh payload.");
        }
        var actual = ReadInt32(data, offset);
        if (actual != expected)
        {
            throw new InvalidOperationException($"The {field} was {actual}, expected {expected}.");
        }
    }

    private static void RequireByteValue(byte[] data, int offset, byte expected, string field)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new InvalidOperationException($"The {field} lies outside the StaticMesh payload.");
        }
        var actual = data[offset];
        if (actual != expected)
        {
            throw new InvalidOperationException($"The {field} was 0x{actual:X2}, expected 0x{expected:X2}.");
        }
    }

    private static void PatchExtendedBounds(NormalExport mesh, Bounds bounds)
    {
        var property = mesh.Data.OfType<StructPropertyData>().FirstOrDefault(item => item.Name.ToString().Equals("ExtendedBounds", StringComparison.OrdinalIgnoreCase));
        var origin = FindBoundsVector(property, "Origin");
        var extent = FindBoundsVector(property, "BoxExtent");
        var radius = property?.Value.OfType<DoublePropertyData>().FirstOrDefault(item => item.Name.ToString().Equals("SphereRadius", StringComparison.OrdinalIgnoreCase));
        if (origin is null || extent is null || radius is null)
        {
            throw new InvalidOperationException("The donor's ExtendedBounds layout changed.");
        }
        origin.Value = new FVector(bounds.Center.X, bounds.Center.Y, bounds.Center.Z);
        extent.Value = new FVector(bounds.Extent.X, bounds.Extent.Y, bounds.Extent.Z);
        radius.Value = bounds.Radius;
    }

    private static void PatchStaticMaterialSlots(
        UAsset asset,
        NormalExport mesh,
        IReadOnlyList<CustomStaticMeshMaterialSlot> declarations)
    {
        if (declarations.Count <= 1)
        {
            // Keep legacy single-material imports byte-for-byte compatible with the proven donor.
            return;
        }
        if (declarations.Count > MaxMaterialSlots ||
            declarations.Select((slot, index) => slot.Slot == index).Any(matches => !matches))
        {
            throw new InvalidOperationException("Custom OBJ material slots must be contiguous and start at slot zero.");
        }

        var staticMaterials = mesh.Data.OfType<ArrayPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("StaticMaterials", StringComparison.OrdinalIgnoreCase));
        var template = staticMaterials?.Value?.OfType<StructPropertyData>().FirstOrDefault();
        if (staticMaterials is null || template is null)
        {
            throw new InvalidOperationException("The verified StaticMesh donor no longer exposes a StaticMaterials template.");
        }

        var entries = new List<PropertyData>(declarations.Count);
        foreach (var declaration in declarations)
        {
            var entry = (StructPropertyData)template.Clone();
            entry.Name = MakeName(asset, declaration.Slot.ToString(CultureInfo.InvariantCulture));
            var materialSlotName = entry.Value.OfType<NamePropertyData>().FirstOrDefault(property =>
                property.Name.ToString().Equals("MaterialSlotName", StringComparison.OrdinalIgnoreCase));
            var importedSlotName = entry.Value.OfType<NamePropertyData>().FirstOrDefault(property =>
                property.Name.ToString().Equals("ImportedMaterialSlotName", StringComparison.OrdinalIgnoreCase));
            if (materialSlotName is null || importedSlotName is null)
            {
                throw new InvalidOperationException("The verified StaticMaterial structure no longer exposes its slot-name fields.");
            }
            materialSlotName.Value = MakeName(asset, declaration.StableSlotName);
            importedSlotName.Value = MakeName(asset, declaration.StableSlotName);
            entries.Add(entry);
        }
        staticMaterials.Value = entries.ToArray();
    }

    private readonly record struct StaticMaterialSlotMetadata(
        bool IsStruct,
        string? MaterialSlotName,
        string? ImportedMaterialSlotName);

    /// <summary>
    /// Verifies the reflected FStaticMaterial table written alongside the raw LOD sections. A
    /// legacy one-slot import intentionally retains the donor's authored name. Multi-slot imports
    /// must carry both exact stable-name fields on every real struct entry.
    /// </summary>
    internal static void ValidateStaticMaterialSlots(
        NormalExport mesh,
        IReadOnlyList<CustomStaticMeshMaterialSlot> declarations)
    {
        var staticMaterials = mesh.Data.OfType<ArrayPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("StaticMaterials", StringComparison.OrdinalIgnoreCase));
        var entries = staticMaterials?.Value;
        if (entries is null || entries.Length != declarations.Count)
        {
            throw new InvalidOperationException("The generated StaticMesh did not retain the expected material-slot count.");
        }
        if (declarations.Count <= 1)
        {
            return;
        }

        var metadata = entries
            .Select(entry =>
            {
                if (entry is not StructPropertyData structure)
                {
                    return new StaticMaterialSlotMetadata(false, null, null);
                }
                var materialNames = structure.Value.OfType<NamePropertyData>()
                    .Where(property => property.Name.ToString().Equals(
                        "MaterialSlotName",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(property => property.Value.ToString())
                    .ToList();
                var importedNames = structure.Value.OfType<NamePropertyData>()
                    .Where(property => property.Name.ToString().Equals(
                        "ImportedMaterialSlotName",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(property => property.Value.ToString())
                    .ToList();
                return new StaticMaterialSlotMetadata(
                    true,
                    materialNames.Count == 1 ? materialNames[0] : null,
                    importedNames.Count == 1 ? importedNames[0] : null);
            })
            .ToList();
        ValidateStaticMaterialSlotMetadata(metadata, declarations);
    }

    private static void ValidateStaticMaterialSlotMetadata(
        IReadOnlyList<StaticMaterialSlotMetadata> metadata,
        IReadOnlyList<CustomStaticMeshMaterialSlot> declarations)
    {
        if (metadata.Count != declarations.Count)
        {
            throw new InvalidOperationException("The generated StaticMesh did not retain the expected material-slot count.");
        }

        var expected = declarations.OrderBy(declaration => declaration.Slot).ToList();
        for (var index = 0; index < expected.Count; index++)
        {
            var entry = metadata[index];
            if (!entry.IsStruct)
            {
                throw new InvalidOperationException(
                    $"StaticMaterials slot {index} is not an FStaticMaterial struct entry.");
            }

            var expectedName = expected[index].StableSlotName ?? "";
            if (string.IsNullOrWhiteSpace(expectedName) ||
                expectedName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The saved stable material name for slot {index} is empty or None.");
            }

            foreach (var (field, actual) in new[]
                     {
                         ("MaterialSlotName", entry.MaterialSlotName),
                         ("ImportedMaterialSlotName", entry.ImportedMaterialSlotName),
                     })
            {
                if (string.IsNullOrWhiteSpace(actual) ||
                    actual.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"StaticMaterials slot {index} is missing a non-None {field} field.");
                }
                if (!actual.Equals(expectedName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"StaticMaterials slot {index} {field} is '{actual}', expected '{expectedName}'.");
                }
            }
        }
    }

    /// <summary>
    /// Validates the actual cooked LOD0 section table and main index buffer retained in
    /// <see cref="NormalExport.Extras"/>. Reflected StaticMaterials alone are not proof that the
    /// renderer will address the same slots and ranges.
    /// </summary>
    internal static void ValidateCookedMaterialSections(
        NormalExport mesh,
        IReadOnlyList<CustomStaticMeshMaterialSlot> declarations) =>
        ValidateCookedMaterialSectionBytes(mesh.Extras ?? [], declarations);

    private static void ValidateCookedMaterialSectionBytes(
        byte[] extras,
        IReadOnlyList<CustomStaticMeshMaterialSlot> declarations)
    {
        if (declarations.Count is < 1 or > MaxMaterialSlots)
        {
            throw new InvalidOperationException(
                $"The generated StaticMesh requires between 1 and {MaxMaterialSlots} material declarations.");
        }
        var orderedDeclarations = declarations.OrderBy(declaration => declaration.Slot).ToList();
        if (orderedDeclarations.Select((declaration, index) => declaration.Slot == index).Any(matches => !matches))
        {
            throw new InvalidOperationException(
                "The generated StaticMesh material declarations are not dense from slot zero.");
        }

        int ReadInt(int offset, string field)
        {
            if (offset < 0 || offset > extras.Length - sizeof(int))
            {
                throw new InvalidOperationException(
                    $"The generated StaticMesh {field} lies outside its cooked LOD0 payload.");
            }
            return BinaryPrimitives.ReadInt32LittleEndian(extras.AsSpan(offset, sizeof(int)));
        }

        var sectionCount = ReadInt(CookedSectionsCountExtrasOffset, "section count");
        if (sectionCount != orderedDeclarations.Count)
        {
            throw new InvalidOperationException(
                $"The cooked LOD0 contains {sectionCount} material section(s), expected {orderedDeclarations.Count}.");
        }

        var sectionDelta = checked((sectionCount - 1) * SectionRecordSize);
        var postSectionLodOffset = checked(LodCookedOutOffset - DonorReflectedPropertyBytes + sectionDelta);
        if (ReadInt(postSectionLodOffset, "cooked-out flag") != 0 ||
            ReadInt(LodBuffersInlinedOffset - DonorReflectedPropertyBytes + sectionDelta, "inline-buffer flag") != 1)
        {
            throw new InvalidOperationException(
                "The generated StaticMesh no longer has the verified inline LOD0 buffer layout.");
        }

        var positionCountOffset = checked(PositionCountOffset0 - DonorReflectedPropertyBytes + sectionDelta);
        var vertexCount = ReadInt(positionCountOffset, "position vertex count");
        if (vertexCount is < 1 or > ushort.MaxValue ||
            ReadInt(PositionStrideOffset - DonorReflectedPropertyBytes + sectionDelta, "position stride") != PositionStride ||
            ReadInt(PositionElementSizeOffset - DonorReflectedPropertyBytes + sectionDelta, "position element size") != PositionStride ||
            ReadInt(PositionCountOffset1 - DonorReflectedPropertyBytes + sectionDelta, "position bulk count") != vertexCount)
        {
            throw new InvalidOperationException(
                "The generated StaticMesh has inconsistent LOD0 position-buffer metadata.");
        }

        var positionDelta = checked((vertexCount - DonorVertexCount) * PositionStride);
        var tangentDelta = checked((vertexCount - DonorVertexCount) * TangentStride);
        var uvDelta = checked((vertexCount - DonorVertexCount) * UvStride);
        var vertexDelta = checked(positionDelta + tangentDelta + uvDelta);
        var indexHeaderShift = checked(sectionDelta + vertexDelta);
        if (ReadInt(Index0Is32BitOffset - DonorReflectedPropertyBytes + indexHeaderShift, "main index width flag") != 0 ||
            ReadInt(Index0ElementSizeOffset - DonorReflectedPropertyBytes + indexHeaderShift, "main index element size") != 1)
        {
            throw new InvalidOperationException(
                "The generated StaticMesh no longer has the verified 16-bit main index-buffer layout.");
        }

        var indexBytes = ReadInt(
            Index0SizeOffset - DonorReflectedPropertyBytes + indexHeaderShift,
            "main index byte count");
        if (indexBytes <= 0 || indexBytes % (3 * sizeof(ushort)) != 0)
        {
            throw new InvalidOperationException(
                $"The generated StaticMesh main index byte count {indexBytes} is not a positive triangle list.");
        }
        var totalIndexCount = indexBytes / sizeof(ushort);
        var indexDataOffset = checked(Index0DataOffset - DonorReflectedPropertyBytes + indexHeaderShift);
        if (indexDataOffset < 0 || (long)indexDataOffset + indexBytes > extras.Length)
        {
            throw new InvalidOperationException(
                "The generated StaticMesh main index buffer lies outside its cooked LOD0 payload.");
        }

        var nextFirstIndex = 0;
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var recordOffset = checked(CookedSectionDataExtrasOffset + sectionIndex * SectionRecordSize);
            var materialIndex = ReadInt(
                recordOffset + SectionMaterialIndexRelativeOffset,
                $"section {sectionIndex} material index");
            var firstIndex = ReadInt(
                recordOffset + SectionFirstIndexRelativeOffset,
                $"section {sectionIndex} first index");
            var triangleCount = ReadInt(
                recordOffset + SectionTriangleCountRelativeOffset,
                $"section {sectionIndex} triangle count");
            var minVertex = ReadInt(
                recordOffset + SectionMinVertexIndexRelativeOffset,
                $"section {sectionIndex} minimum vertex");
            var maxVertex = ReadInt(
                recordOffset + SectionMaxVertexIndexRelativeOffset,
                $"section {sectionIndex} maximum vertex");

            if (materialIndex != orderedDeclarations[sectionIndex].Slot)
            {
                throw new InvalidOperationException(
                    $"Cooked LOD0 section {sectionIndex} uses material index {materialIndex}, expected slot {orderedDeclarations[sectionIndex].Slot}.");
            }
            if (firstIndex != nextFirstIndex || triangleCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Cooked LOD0 section {sectionIndex} has an invalid FirstIndex/triangle range ({firstIndex}, {triangleCount}).");
            }

            var sectionIndexCount = checked(triangleCount * 3);
            var sectionEnd = checked(firstIndex + sectionIndexCount);
            if (sectionEnd > totalIndexCount || minVertex < 0 || minVertex > maxVertex || maxVertex >= vertexCount)
            {
                throw new InvalidOperationException(
                    $"Cooked LOD0 section {sectionIndex} range is outside {totalIndexCount} indices or {vertexCount} vertices.");
            }

            var observedMin = int.MaxValue;
            var observedMax = int.MinValue;
            for (var index = firstIndex; index < sectionEnd; index++)
            {
                var byteOffset = checked(indexDataOffset + index * sizeof(ushort));
                var vertex = BinaryPrimitives.ReadUInt16LittleEndian(extras.AsSpan(byteOffset, sizeof(ushort)));
                if (vertex >= vertexCount)
                {
                    throw new InvalidOperationException(
                        $"Cooked LOD0 section {sectionIndex} references vertex {vertex}, outside {vertexCount} vertices.");
                }
                observedMin = Math.Min(observedMin, vertex);
                observedMax = Math.Max(observedMax, vertex);
            }
            if (minVertex != observedMin || maxVertex != observedMax)
            {
                throw new InvalidOperationException(
                    $"Cooked LOD0 section {sectionIndex} stores vertex range {minVertex}..{maxVertex}, " +
                    $"but its indices address {observedMin}..{observedMax}.");
            }
            nextFirstIndex = sectionEnd;
        }

        if (nextFirstIndex != totalIndexCount)
        {
            throw new InvalidOperationException(
                $"Cooked LOD0 sections cover {nextFirstIndex} of {totalIndexCount} main indices.");
        }
    }

    private static void ValidateShiftedDonorPayload(byte[] source, int sourceLayoutShift)
    {
        if (sourceLayoutShift == 0)
        {
            ValidateDonorPayload(source, 0, source.Length);
            return;
        }
        const int staticMaterialInsertionOffset = 0x0A;
        if (sourceLayoutShift < 0 || source.Length != DonorStaticMeshSerialSize + sourceLayoutShift ||
            staticMaterialInsertionOffset + sourceLayoutShift > source.Length)
        {
            throw new InvalidOperationException("The expanded StaticMaterials layout did not match the verified donor export.");
        }
        var reconstructed = new byte[DonorStaticMeshSerialSize];
        source.AsSpan(0, staticMaterialInsertionOffset).CopyTo(reconstructed);
        source.AsSpan(staticMaterialInsertionOffset + sourceLayoutShift)
            .CopyTo(reconstructed.AsSpan(staticMaterialInsertionOffset));
        ValidateDonorPayload(reconstructed, 0, reconstructed.Length);
    }

    private static FName MakeName(UAsset asset, string value)
    {
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }
        return new FName(asset, value, 0);
    }

    private static void WriteRenderBounds(byte[] payload, int offset, Bounds bounds)
    {
        if (offset < 0 || offset + 7 * sizeof(double) > payload.Length)
        {
            throw new InvalidOperationException("The generated render-bounds record is outside the static mesh payload.");
        }
        WriteDouble(payload, offset, bounds.Center.X);
        WriteDouble(payload, offset + 8, bounds.Center.Y);
        WriteDouble(payload, offset + 16, bounds.Center.Z);
        WriteDouble(payload, offset + 24, bounds.Extent.X);
        WriteDouble(payload, offset + 32, bounds.Extent.Y);
        WriteDouble(payload, offset + 40, bounds.Extent.Z);
        WriteDouble(payload, offset + 48, bounds.Radius);
    }

    private static void RewriteIdentity(string uassetPath, string outputPackagePath, Usmap mappings, Result result)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
        asset.FolderName = new FString(outputPackagePath);
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        var names = asset.GetNameMapIndexList();
        var changed = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var original = names[i].ToString();
            var updated = original.Replace(DonorPackagePath, outputPackagePath, StringComparison.Ordinal)
                .Replace(DonorAssetName, outputName, StringComparison.Ordinal);
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

    private static void PatchExportSerialSizeAndBulkDataStart(
        string uassetPath,
        int originalSize,
        long serialOffset,
        int newSize)
    {
        var data = File.ReadAllBytes(uassetPath);
        var serialSizeMatches = new List<int>();
        for (var offset = 0; offset <= data.Length - 2 * sizeof(long); offset++)
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(long))) == originalSize &&
                BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + sizeof(long), sizeof(long))) == serialOffset)
            {
                serialSizeMatches.Add(offset);
            }
        }
        if (serialSizeMatches.Count != 1)
        {
            throw new InvalidOperationException("The cloned package does not have one verified StaticMesh serial-size entry.");
        }
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(serialSizeMatches[0], sizeof(long)), newSize);

        // This verified donor's StaticMesh export is package-terminal. Its end is therefore the
        // virtual start of the external .ubulk data. UAssetAPI updated this header when it wrote
        // StaticMaterials, but the raw payload resize happens afterward and must move it again.
        // Leaving the old value makes shrunken meshes unreadable and points larger meshes' bulk
        // references into the middle of their rewritten .uexp data.
        var oldBulkDataStart = checked(serialOffset + originalSize);
        var newBulkDataStart = checked(serialOffset + newSize);
        var bulkStartMatches = new List<int>();
        for (var offset = 0; offset <= data.Length - sizeof(long); offset++)
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(long))) == oldBulkDataStart)
            {
                bulkStartMatches.Add(offset);
            }
        }
        if (bulkStartMatches.Count != 1)
        {
            throw new InvalidOperationException("The cloned package does not have one verified bulk-data-start entry.");
        }
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(bulkStartMatches[0], sizeof(long)), newBulkDataStart);
        File.WriteAllBytes(uassetPath, data);
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
    private static void WriteInt32(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), value);
    private static void WriteDouble(byte[] data, int offset, double value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, sizeof(double)), BitConverter.DoubleToInt64Bits(value));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
