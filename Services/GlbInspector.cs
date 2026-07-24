using System.Numerics;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Minimal glTF-binary reader: walks the node hierarchy of an exported .glb to find where a named
/// node (a skeleton bone) ends up in the file's own coordinate space.
///
/// This exists so attachment placement is computed in C# - where it can be printed and verified -
/// rather than in the viewer's JavaScript, where a silently-failing bone lookup is invisible.
/// </summary>
internal static class GlbInspector
{
    /// <summary>
    /// Vertical bounds of a .glb's geometry, read from the POSITION accessors' min/max. This is the
    /// authoritative "where does this actually render" measurement - unlike the bone nodes, which at
    /// bind pose are not required to line up with the mesh.
    /// </summary>
    public static (float Min, float Max)? VerticalBounds(string glbPath)
    {
        var json = ReadJsonChunk(glbPath);
        if (json is null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("meshes", out var meshes) || !root.TryGetProperty("accessors", out var accessors))
        {
            return null;
        }

        float min = float.MaxValue, max = float.MinValue;
        foreach (var mesh in meshes.EnumerateArray())
        {
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (!prim.TryGetProperty("attributes", out var attrs) ||
                    !attrs.TryGetProperty("POSITION", out var posIdx))
                {
                    continue;
                }
                var acc = accessors[posIdx.GetInt32()];
                if (acc.TryGetProperty("min", out var mn) && acc.TryGetProperty("max", out var mx))
                {
                    min = Math.Min(min, (float)mn[1].GetDouble());
                    max = Math.Max(max, (float)mx[1].GetDouble());
                }
            }
        }
        return min <= max ? (min, max) : null;
    }

    /// <summary>
    /// Extracts each non-empty TEXCOORD_N of the first mesh primitive to a raw little-endian
    /// float32 file (vec2 per vertex) named <paramref name="baseName"/>_uvN.f32, and returns the
    /// channel indices written. Lets the viewer bind UV sets three.js's glTF loader drops (only 0/1
    /// survive import), so every channel can be tested live.
    /// </summary>
    public static List<int> ExtractUvChannels(string glbPath, string outDir, string baseName)
    {
        var written = new List<int>();
        try
        {
            var data = File.ReadAllBytes(glbPath);
            var jsonLen = (int)BitConverter.ToUInt32(data, 12);
            var json = System.Text.Encoding.UTF8.GetString(data, 20, jsonLen);
            var binOffset = 20 + jsonLen + 8; // skip JSON chunk + BIN chunk header

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            var attrs = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");

            for (var ch = 0; ch < 8; ch++)
            {
                if (!attrs.TryGetProperty($"TEXCOORD_{ch}", out var accIdxEl))
                {
                    continue;
                }
                var acc = accessors[accIdxEl.GetInt32()];
                var count = acc.GetProperty("count").GetInt32();
                var view = views[acc.GetProperty("bufferView").GetInt32()];
                var start = binOffset + (view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0)
                            + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);

                // CUE4Parse interleaves the whole vertex into one buffer with a byteStride, so a UV set
                // is NOT contiguous - each vec2 sits `stride` bytes apart. Reading contiguously grabbed
                // interleaved position/normal garbage (identical for every channel). Walk by stride.
                var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 8;

                var packed = new byte[count * 8];
                var anyNonZero = false;
                var ok = true;
                for (var i = 0; i < count; i++)
                {
                    var src = start + i * stride;
                    if (src + 8 > data.Length) { ok = false; break; }
                    Array.Copy(data, src, packed, i * 8, 8);
                    if (!anyNonZero)
                    {
                        for (var b = 0; b < 8; b++) if (packed[i * 8 + b] != 0) { anyNonZero = true; break; }
                    }
                }
                if (!ok || !anyNonZero)
                {
                    continue; // out of range, or an empty padding channel
                }

                var outPath = Path.Combine(outDir, $"{baseName}_uv{ch}.f32");
                File.WriteAllBytes(outPath, packed);
                written.Add(ch);
            }
        }
        catch
        {
            // Best effort - the switcher just won't offer channels we couldn't extract.
        }
        return written;
    }

    /// <summary>
    /// Rewrites a .glb so TEXCOORD_0 points at the accessor currently used by
    /// TEXCOORD_&lt;<paramref name="sourceChannel"/>&gt;.
    ///
    /// These meshes carry up to 8 UV sets, but three.js r128's glTF loader only reads TEXCOORD_0 and
    /// TEXCOORD_1 - any higher channel is dropped before we can select it. The printed decals are laid
    /// out against a higher channel, so we promote it into slot 0 at export time.
    /// </summary>
    public static bool TryPromoteUvChannel(string glbPath, int sourceChannel)
    {
        try
        {
            var data = File.ReadAllBytes(glbPath);
            var jsonLen = (int)BitConverter.ToUInt32(data, 12);
            var json = System.Text.Encoding.UTF8.GetString(data, 20, jsonLen);

            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var meshes = node?["meshes"]?.AsArray();
            if (meshes is null)
            {
                return false;
            }

            var changed = false;
            var wanted = $"TEXCOORD_{sourceChannel}";
            foreach (var mesh in meshes)
            {
                foreach (var prim in mesh?["primitives"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                {
                    var attrs = prim?["attributes"]?.AsObject();
                    if (attrs is null || !attrs.ContainsKey(wanted))
                    {
                        continue;
                    }
                    var idx = attrs[wanted]!.GetValue<int>();
                    attrs["TEXCOORD_0"] = idx;
                    changed = true;
                }
            }
            if (!changed)
            {
                return false;
            }

            // Rebuild the container: the JSON chunk must stay 4-byte aligned (padded with spaces).
            var newJson = node!.ToJsonString();
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(newJson);
            var pad = (4 - jsonBytes.Length % 4) % 4;
            var paddedLen = jsonBytes.Length + pad;

            var binOffset = 20 + jsonLen;
            var binLen = data.Length - binOffset;

            using var outStream = new MemoryStream();
            using var w = new BinaryWriter(outStream);
            w.Write(0x46546C67u);                       // "glTF"
            w.Write(2u);                                // version
            w.Write((uint)(12 + 8 + paddedLen + binLen));
            w.Write((uint)paddedLen);
            w.Write(0x4E4F534Au);                       // "JSON"
            w.Write(jsonBytes);
            for (var i = 0; i < pad; i++) w.Write((byte)0x20);
            w.Write(data, binOffset, binLen);

            File.WriteAllBytes(glbPath, outStream.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>World-space position of the node named <paramref name="nodeName"/>, or null.</summary>
    public static Vector3? NodeWorldPosition(string glbPath, string nodeName)
    {
        var json = ReadJsonChunk(glbPath);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("nodes", out var nodesEl))
        {
            return null;
        }

        var nodes = nodesEl.EnumerateArray().ToList();
        var target = -1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].TryGetProperty("name", out var n) && n.GetString() == nodeName)
            {
                target = i;
                break;
            }
        }
        if (target < 0)
        {
            return null;
        }

        // Parent map, so we can walk root -> target.
        var parent = new Dictionary<int, int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].TryGetProperty("children", out var kids))
            {
                foreach (var k in kids.EnumerateArray())
                {
                    parent[k.GetInt32()] = i;
                }
            }
        }

        var chain = new List<int>();
        for (var cur = target; ; )
        {
            chain.Add(cur);
            if (!parent.TryGetValue(cur, out var p)) break;
            cur = p;
        }
        chain.Reverse();

        // Row-vector convention: world = local * parentWorld.
        var world = Matrix4x4.Identity;
        foreach (var idx in chain)
        {
            world = LocalMatrix(nodes[idx]) * world;
        }
        return world.Translation;
    }

    private static Matrix4x4 LocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m))
        {
            var v = m.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            // glTF stores column-major; those 16 floats map directly onto Matrix4x4's field order.
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                                 v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        var t = Vector3.Zero;
        var r = Quaternion.Identity;
        var s = Vector3.One;
        if (node.TryGetProperty("translation", out var te))
        {
            var a = te.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            t = new Vector3(a[0], a[1], a[2]);
        }
        if (node.TryGetProperty("rotation", out var re))
        {
            var a = re.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            r = new Quaternion(a[0], a[1], a[2], a[3]);
        }
        if (node.TryGetProperty("scale", out var se))
        {
            var a = se.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            s = new Vector3(a[0], a[1], a[2]);
        }
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>Returns the JSON chunk of a .glb (header is magic/version/length, then chunks).</summary>
    private static string? ReadJsonChunk(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20)
            {
                return null;
            }
            var chunkLen = BitConverter.ToUInt32(data, 12);
            return System.Text.Encoding.UTF8.GetString(data, 20, (int)chunkLen);
        }
        catch
        {
            return null;
        }
    }
}
