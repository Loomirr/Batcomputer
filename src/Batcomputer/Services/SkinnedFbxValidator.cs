using System.IO.Compression;
using System.Text;

namespace Batcomputer;

/// <summary>Read-only preflight. Unreal must not silently repair missing skin weights on import.</summary>
internal static class SkinnedFbxValidator
{
    internal sealed record Audit(int Vertices, int Bones, IReadOnlyList<string> Materials);
    private sealed record Node(string Name, object[] Values, List<Node> Children);
    private const int Limit = 128 * 1024 * 1024;

    internal static Audit Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > Limit) throw new InvalidDataException("FBX must be smaller than 128 MB.");
        using var r = new BinaryReader(stream, Encoding.UTF8);
        if (!r.ReadBytes(23).SequenceEqual(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")))
            throw new InvalidDataException("Use a binary FBX exported from Blender or another 3D editor.");
        var version = r.ReadUInt32();
        if (version is < 7400 or > 7700) throw new InvalidDataException("Supported FBX versions: 7.4–7.7.");
        int count = 0; long decoded = 0;
        var nodes = new List<Node>();
        while (ReadNode(0) is { } node) nodes.Add(node);
        var objects = nodes.SingleOrDefault(n => n.Name == "Objects")?.Children
            ?? throw new InvalidDataException("FBX contains no objects.");
        var meshes = objects.Where(n => n.Name == "Geometry" && Text(n, 2) == "Mesh").ToArray();
        if (meshes.Length != 1) throw new InvalidDataException("Export one joined skinned mesh with its armature. Multiple material slots are supported.");
        var mesh = meshes[0];
        var positions = mesh.Children.SingleOrDefault(n => n.Name == "Vertices")?.Values.FirstOrDefault() as double[];
        if (positions is null || positions.Length == 0 || positions.Length % 3 != 0 || positions.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("FBX has invalid vertex positions.");
        var bones = objects.Where(n => n.Name == "Model" && Text(n, 2) == "LimbNode").ToArray();
        if (bones.Length == 0) throw new InvalidDataException("This model has no rig. Rig and weight it in Blender first.");
        var edges = nodes.SingleOrDefault(n => n.Name == "Connections")?.Children
            .Where(n => n.Values.Length >= 3 && Text(n, 0) == "OO")
            .Select(n => (Child: Convert.ToInt64(n.Values[1]), Parent: Convert.ToInt64(n.Values[2]))).ToArray() ?? [];
        var skins = objects.Where(n => n.Name == "Deformer" && Text(n, 2) == "Skin" &&
            edges.Contains((Convert.ToInt64(n.Values[0]), Convert.ToInt64(mesh.Values[0])))).ToArray();
        if (skins.Length != 1) throw new InvalidDataException("The mesh must have exactly one skin connected to its armature.");
        var clusters = objects.Where(n => n.Name == "Deformer" && Text(n, 2) == "Cluster" &&
            edges.Contains((Convert.ToInt64(n.Values[0]), Convert.ToInt64(skins[0].Values[0])))).ToArray();
        var sums = new double[positions.Length / 3]; var influences = new int[sums.Length];
        foreach (var cluster in clusters)
        {
            var indices = cluster.Children.SingleOrDefault(n => n.Name == "Indexes")?.Values.FirstOrDefault() as int[] ?? [];
            var weights = cluster.Children.SingleOrDefault(n => n.Name == "Weights")?.Values.FirstOrDefault() as double[] ?? [];
            if (indices.Length != weights.Length) throw new InvalidDataException("FBX skin indices and weights do not match.");
            if (indices.Length > 0 && !bones.Any(b => edges.Contains((Convert.ToInt64(b.Values[0]), Convert.ToInt64(cluster.Values[0])))))
                throw new InvalidDataException("A skin cluster has no matching bone.");
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] < 0 || indices[i] >= sums.Length || !double.IsFinite(weights[i]) || weights[i] < 0)
                    throw new InvalidDataException("FBX contains invalid skin weights.");
                if (weights[i] > 0.000001) { sums[indices[i]] += weights[i]; influences[indices[i]]++; }
            }
        }
        var unweighted = sums.Count(s => s < 0.000001);
        var overLimit = influences.Count(n => n > 8);
        var unnormalized = sums.Count(s => Math.Abs(s - 1) > 0.001);
        if (unweighted > 0 || overLimit > 0 || unnormalized > 0)
            throw new InvalidDataException($"Fix weights in Blender before importing: {unweighted} unweighted vertices, {overLimit} with more than 8 influences, {unnormalized} not normalized. Use Normalize All / Limit Total, then inspect deformation. No automatic repairs were applied.");
        return new Audit(sums.Length, bones.Length, objects.Where(n => n.Name == "Material").Select(n => Text(n, 1).Split('\0')[0]).ToArray());

        Node? ReadNode(int depth)
        {
            if (depth > 64 || ++count > 200000) throw new InvalidDataException("FBX node limit exceeded.");
            long end = version >= 7500 ? checked((long)r.ReadUInt64()) : r.ReadUInt32();
            long properties = version >= 7500 ? checked((long)r.ReadUInt64()) : r.ReadUInt32();
            long length = version >= 7500 ? checked((long)r.ReadUInt64()) : r.ReadUInt32();
            int nameLength = r.ReadByte();
            if (end == 0) return null;
            if (end > stream.Length || end <= stream.Position || properties > 100000 || length > Limit)
                throw new InvalidDataException("Invalid FBX node bounds.");
            var name = Encoding.UTF8.GetString(r.ReadBytes(nameLength));
            long start = stream.Position;
            var values = Enumerable.Range(0, checked((int)properties)).Select(_ => ReadValue()).ToArray();
            if (stream.Position != start + length) throw new InvalidDataException("Invalid FBX property length.");
            var children = new List<Node>();
            while (stream.Position < end && ReadNode(depth + 1) is { } child) children.Add(child);
            if (stream.Position != end) throw new InvalidDataException("Invalid FBX child bounds.");
            return new Node(name, values, children);
        }
        object ReadValue()
        {
            char type = (char)r.ReadByte();
            switch (type)
            {
                case 'Y': return r.ReadInt16(); case 'C': return r.ReadByte(); case 'I': return r.ReadInt32();
                case 'F': return r.ReadSingle(); case 'D': return r.ReadDouble(); case 'L': return r.ReadInt64();
                case 'S': case 'R':
                    int length = r.ReadInt32();
                    if (length < 0 || length > Limit || stream.Position + length > stream.Length) throw new InvalidDataException("Invalid FBX string length.");
                    var bytes = r.ReadBytes(length); return type == 'S' ? Encoding.UTF8.GetString(bytes) : bytes;
                case 'd': case 'f': case 'i': case 'l': case 'b': case 'c':
                    int size = type is 'd' or 'l' ? 8 : type is 'f' or 'i' ? 4 : 1;
                    int elements = r.ReadInt32(), encoding = r.ReadInt32(), compressed = r.ReadInt32();
                    long bytesNeeded = (long)elements * size;
                    if (elements < 0 || compressed < 0 || compressed > Limit || (decoded += bytesNeeded) > Limit || encoding is < 0 or > 1)
                        throw new InvalidDataException("FBX array limit exceeded.");
                    var payload = r.ReadBytes(compressed);
                    using (var memory = new MemoryStream(payload))
                    using (Stream data = encoding == 1 ? new ZLibStream(memory, CompressionMode.Decompress) : memory)
                    using (var reader = new BinaryReader(data))
                    {
                        object result = type switch
                        {
                            'd' => Enumerable.Range(0, elements).Select(_ => reader.ReadDouble()).ToArray(),
                            'i' => Enumerable.Range(0, elements).Select(_ => reader.ReadInt32()).ToArray(),
                            _ => ReadExact(reader, checked((int)bytesNeeded))
                        };
                        if (data.ReadByte() != -1) throw new InvalidDataException("FBX array length mismatch.");
                        return result;
                    }
                default: throw new InvalidDataException($"Unsupported FBX property '{type}'.");
            }
        }
    }
    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var data = reader.ReadBytes(length);
        return data.Length == length ? data : throw new EndOfStreamException("Truncated FBX array.");
    }
    private static string Text(Node node, int index) => node.Values.ElementAtOrDefault(index)?.ToString() ?? "";
}
