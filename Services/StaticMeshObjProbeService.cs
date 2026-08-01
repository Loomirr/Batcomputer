using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
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
    private const int DonorVertexCount = 486;
    private const int PositionDataOffset = 0x11D;
    private const int PositionStride = 12;
    private const int PositionCountOffset0 = 0x111;
    private const int PositionCountOffset1 = 0x119;
    private const int TangentDataOffset = 0x17FF;
    private const int TangentStride = 8;
    private const int TangentCountOffset0 = 0x17EB;
    private const int TangentCountOffset1 = 0x17FB;
    private const int UvDataOffset = 0x2737;
    private const int UvStride = 4;
    private const int UvCountOffset = 0x2733;
    private const int Index0DataOffset = 0x2EE5;
    private const int Index0SizeOffset = 0x2EE1;
    private const int Index1DataOffset = 0x38DD;
    private const int Index1SizeOffset = 0x38D9;
    private const int DonorIndexBytes = 2520;
    private const int SectionTriangleCountOffset = 0xA3;
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
        public string ObjPath { get; set; } = "";
        public float Scale { get; set; } = 150f;
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
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
        public string ObjSha256 { get; set; } = "";
        public string UexpSha256Before { get; set; } = "";
        public string UexpSha256After { get; set; } = "";
        public List<string> Log { get; set; } = [];
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
        public Bounds Bounds { get; set; }
    }

    public Result CreateObjHeadProbe(Request request)
    {
        var result = new Result
        {
            SourceObjPath = request.ObjPath,
            OutputPackagePath = UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath)
        };

        try
        {
            if (request.Scale is < 1f or > 1000f)
            {
                throw new ArgumentOutOfRangeException(nameof(request.Scale), "OBJ scale must be between 1 and 1000.");
            }
            if (!result.OutputPackagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Probe output must be under /Game/Mods/.");
            }
            if (!File.Exists(request.ObjPath))
            {
                throw new FileNotFoundException("The OBJ file was not found.", request.ObjPath);
            }

            var mesh = ParseObj(request.ObjPath, request.Scale);
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
            asset.Write(result.OutputUasset);

            var afterBounds = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            var afterBoundsMesh = FindStaticMeshExport(afterBounds)
                ?? throw new InvalidOperationException("The donor no longer contains a StaticMesh export after its bounds update.");
            serialOffset = checked((int)(afterBoundsMesh.SerialOffset - new FileInfo(result.OutputUasset).Length));
            var uexp = File.ReadAllBytes(result.OutputUexp);
            result.UexpSha256Before = Hash(uexp);
            var payload = BuildPayload(uexp.AsSpan(serialOffset, originalSerialSize).ToArray(), mesh, result);
            result.StaticMeshBytesBefore = originalSerialSize;
            result.StaticMeshBytesAfter = payload.Length;

            PatchExportSerialSize(result.OutputUasset, originalSerialSize, afterBoundsMesh.SerialOffset, payload.Length);

            var finalUexp = File.ReadAllBytes(result.OutputUexp);
            var prefix = finalUexp.AsSpan(0, serialOffset).ToArray();
            var tail = finalUexp.AsSpan(serialOffset + originalSerialSize).ToArray();
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

            result.Status = "created";
            result.Log.Add($"Parsed {result.VertexCount} flattened vertices and {result.TriangleCount} double-sided triangles from the OBJ.");
            result.Log.Add("Expanded the final StaticMesh export's inline position, tangent, UV, and active index buffers.");
            result.Log.Add("Kept the donor's one material section, collision shell, and package identity structure.");
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
                result.ReportPath = Path.ChangeExtension(result.OutputUasset, ".obj-probe-report.json");
                File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
            }
        }

        return result;
    }

    private static byte[] BuildPayload(byte[] source, ImportedMesh mesh, Result result)
    {
        var oldPositionEnd = PositionDataOffset + DonorVertexCount * PositionStride;
        var oldTangentEnd = TangentDataOffset + DonorVertexCount * TangentStride;
        var oldUvEnd = UvDataOffset + DonorVertexCount * UvStride;
        var oldIndex0End = Index0DataOffset + DonorIndexBytes;
        var oldIndex1End = Index1DataOffset + DonorIndexBytes;
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

        using var output = new MemoryStream(source.Length + vertexDelta + 2 * indexDelta);
        output.Write(source, 0, PositionDataOffset);
        WritePositions(output, mesh.Vertices);
        output.Write(source, oldPositionEnd, TangentDataOffset - oldPositionEnd);
        WriteTangents(output, mesh.Vertices);
        output.Write(source, oldTangentEnd, UvDataOffset - oldTangentEnd);
        WriteUvs(output, mesh.Vertices);
        output.Write(source, oldUvEnd, Index0DataOffset - oldUvEnd);
        WriteIndices(output, mesh.Indices);
        output.Write(source, oldIndex0End, Index1DataOffset - oldIndex0End);
        WriteIndices(output, mesh.Indices);
        output.Write(source, oldIndex1End, source.Length - oldIndex1End);

        var payload = output.ToArray();
        WriteInt32(payload, PositionCountOffset0, vertexCount);
        WriteInt32(payload, PositionCountOffset1, vertexCount);
        WriteInt32(payload, TangentCountOffset0 + positionDelta, vertexCount);
        WriteInt32(payload, TangentCountOffset1 + positionDelta, vertexCount);
        WriteInt32(payload, UvCountOffset + positionDelta + tangentDelta, vertexCount);
        WriteInt32(payload, Index0SizeOffset + vertexDelta, indexBytes);
        WriteInt32(payload, Index1SizeOffset + vertexDelta + indexDelta, indexBytes);
        WriteInt32(payload, SectionTriangleCountOffset, mesh.Indices.Count / 3);

        var boundsShift = vertexDelta + 2 * indexDelta;
        WriteRenderBounds(payload, RenderBoundsOffsets[0], mesh.Bounds);
        WriteRenderBounds(payload, RenderBoundsOffsets[1], mesh.Bounds);
        WriteRenderBounds(payload, RenderBoundsOffsets[2] + boundsShift, mesh.Bounds);
        return payload;
    }

    private static ImportedMesh ParseObj(string objPath, float scale)
    {
        var positions = new List<Vector3> { default };
        var uvs = new List<Vector2> { default };
        var normals = new List<Vector3> { default };
        var mesh = new ImportedMesh();
        var vertexMap = new Dictionary<ObjKey, ushort>();

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
                case "f" when tokens.Length >= 4:
                {
                    var face = tokens.Skip(1).Select(token => ParseObjKey(token, positions.Count - 1, uvs.Count - 1, normals.Count - 1)).ToArray();
                    for (var i = 1; i < face.Length - 1; i++)
                    {
                        var first = AddVertex(face[0], positions, uvs, normals, vertexMap, mesh, scale);
                        var second = AddVertex(face[i], positions, uvs, normals, vertexMap, mesh, scale);
                        var third = AddVertex(face[i + 1], positions, uvs, normals, vertexMap, mesh, scale);
                        mesh.Indices.Add(first);
                        mesh.Indices.Add(second);
                        mesh.Indices.Add(third);
                        mesh.Indices.Add(first);
                        mesh.Indices.Add(third);
                        mesh.Indices.Add(second);
                    }
                    break;
                }
            }
        }

        if (mesh.Vertices.Count == 0)
        {
            throw new InvalidOperationException("The OBJ contains no usable faces.");
        }
        CenterAndBuildFrames(mesh);
        return mesh;
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

    private static void CenterAndBuildFrames(ImportedMesh mesh)
    {
        var min = new Vector3(mesh.Vertices.Min(vertex => vertex.Position.X), mesh.Vertices.Min(vertex => vertex.Position.Y), mesh.Vertices.Min(vertex => vertex.Position.Z));
        var max = new Vector3(mesh.Vertices.Max(vertex => vertex.Position.X), mesh.Vertices.Max(vertex => vertex.Position.Y), mesh.Vertices.Max(vertex => vertex.Position.Z));
        var center = (min + max) * 0.5f;
        foreach (var vertex in mesh.Vertices)
        {
            vertex.Position -= center;
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

        var extent = new Vector3(
            MathF.Max(MathF.Abs(min.X - center.X), MathF.Abs(max.X - center.X)),
            MathF.Max(MathF.Abs(min.Y - center.Y), MathF.Abs(max.Y - center.Y)),
            MathF.Max(MathF.Abs(min.Z - center.Z), MathF.Abs(max.Z - center.Z)));
        mesh.Bounds = new Bounds(default, extent, MathF.Sqrt(Dot(extent, extent)));
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
        if (serialOffset < 0 || serialSize <= Index1DataOffset + DonorIndexBytes || serialOffset + serialSize > uexp.Length ||
            ReadInt32(uexp, serialOffset + PositionCountOffset0) != DonorVertexCount ||
            ReadInt32(uexp, serialOffset + PositionCountOffset1) != DonorVertexCount ||
            ReadInt32(uexp, serialOffset + TangentCountOffset0) != DonorVertexCount ||
            ReadInt32(uexp, serialOffset + TangentCountOffset1) != DonorVertexCount ||
            ReadInt32(uexp, serialOffset + UvCountOffset) != DonorVertexCount ||
            ReadInt32(uexp, serialOffset + Index0SizeOffset) != DonorIndexBytes ||
            ReadInt32(uexp, serialOffset + Index1SizeOffset) != DonorIndexBytes ||
            ReadInt32(uexp, serialOffset + SectionTriangleCountOffset) != 420)
        {
            throw new InvalidOperationException("The Nightwing donor's verified LOD0 layout changed.");
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

    private static void PatchExportSerialSize(string uassetPath, int originalSize, long serialOffset, int newSize)
    {
        var data = File.ReadAllBytes(uassetPath);
        var matches = new List<int>();
        for (var offset = 0; offset <= data.Length - 2 * sizeof(long); offset++)
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(long))) == originalSize &&
                BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + sizeof(long), sizeof(long))) == serialOffset)
            {
                matches.Add(offset);
            }
        }
        if (matches.Count != 1)
        {
            throw new InvalidOperationException("The cloned package does not have one verified StaticMesh serial-size entry.");
        }
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(matches[0], sizeof(long)), newSize);
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
