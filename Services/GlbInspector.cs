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

    /// <summary>Full 3D bounds of a .glb's geometry from the POSITION accessors' min/max.</summary>
    public static (Vector3 Min, Vector3 Max)? Bounds3(string glbPath)
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

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
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
                    min = Vector3.Min(min, new Vector3((float)mn[0].GetDouble(), (float)mn[1].GetDouble(), (float)mn[2].GetDouble()));
                    max = Vector3.Max(max, new Vector3((float)mx[0].GetDouble(), (float)mx[1].GetDouble(), (float)mx[2].GetDouble()));
                }
            }
        }
        return min.X <= max.X ? (min, max) : null;
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

    /// <summary>
    /// Splits the LEGO face mesh's single merged section into base / mouth / hidden triangle runs.
    ///
    /// The cooked SK_LEGOface has ONE render section; the game's layered M_LEGOface shader routes the
    /// feature textures (mouth, eyes...) itself. For the preview the features are separated
    /// geometrically, using bands measured from the aligned community Blender scene (face-local,
    /// character faces +X, scale exactly 0.01 of that scene):
    ///   - base shell front sits at x = 0.204; feature shells ride proud at x &gt; 0.205
    ///   - the mouth (and its backing strip) occupies y 0.094..0.115, |z| &lt; 0.07
    /// Triangles are REORDERED in the index buffer to [base][mouth][hidden] so the viewer can bind
    /// three material groups over contiguous ranges. Returns triangle counts, or null on failure.
    /// </summary>
    /// <summary>
    /// Alternate feature bands found by the last <see cref="TryGroupFaceFeatures"/> call, in the
    /// order they were appended to the hidden run: (ExtraUV0 band id, triangle count). These are the
    /// expression system's sprite slots.
    /// </summary>
    public static List<(int Band, int Tris)> FaceBandLayout { get; private set; } = new();

    public static int[]? TryGroupFaceFeatures(string glbPath)
    {
        try
        {
            var data = File.ReadAllBytes(glbPath);
            var jsonLen = (int)BitConverter.ToUInt32(data, 12);
            var json = System.Text.Encoding.UTF8.GetString(data, 20, jsonLen);
            var binOffset = 20 + jsonLen + 8;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            var prim = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
            if (!prim.TryGetProperty("indices", out var idxEl))
            {
                return null;
            }

            // Index buffer.
            var iAcc = accessors[idxEl.GetInt32()];
            var iCount = iAcc.GetProperty("count").GetInt32();
            var iType = iAcc.GetProperty("componentType").GetInt32(); // 5123 u16 / 5125 u32
            var iView = views[iAcc.GetProperty("bufferView").GetInt32()];
            var iStart = binOffset + (iView.TryGetProperty("byteOffset", out var ibo) ? ibo.GetInt32() : 0)
                         + (iAcc.TryGetProperty("byteOffset", out var iao) ? iao.GetInt32() : 0);
            var indices = new uint[iCount];
            for (var i = 0; i < iCount; i++)
            {
                indices[i] = iType == 5125
                    ? BitConverter.ToUInt32(data, iStart + i * 4)
                    : BitConverter.ToUInt16(data, iStart + i * 2);
            }

            // Positions (interleaved - walk by stride).
            var pAcc = accessors[prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
            var pCount = pAcc.GetProperty("count").GetInt32();
            var pView = views[pAcc.GetProperty("bufferView").GetInt32()];
            var pStart = binOffset + (pView.TryGetProperty("byteOffset", out var pbo) ? pbo.GetInt32() : 0)
                         + (pAcc.TryGetProperty("byteOffset", out var pao) ? pao.GetInt32() : 0);
            var pStride = pView.TryGetProperty("byteStride", out var ps) ? ps.GetInt32() : 12;
            var pos = new Vector3[pCount];
            for (var i = 0; i < pCount; i++)
            {
                var o = pStart + i * pStride;
                pos[i] = new Vector3(BitConverter.ToSingle(data, o),
                                     BitConverter.ToSingle(data, o + 4),
                                     BitConverter.ToSingle(data, o + 8));
            }

            // The feature shells are separate CONNECTED COMPONENTS of the merged mesh (verified: 14
            // components - two large face shells, the mouth blob front-center, and symmetric eye/brow
            // pairs at +-z). Weld verts by position first, since normals split the vertex stream.
            var weld = new Dictionary<(float, float, float), int>();
            var remap = new int[pCount];
            for (var i = 0; i < pCount; i++)
            {
                var k = (MathF.Round(pos[i].X, 5), MathF.Round(pos[i].Y, 5), MathF.Round(pos[i].Z, 5));
                if (!weld.TryGetValue(k, out var id))
                {
                    id = weld.Count;
                    weld[k] = id;
                }
                remap[i] = id;
            }
            var parent = new int[weld.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
            for (var t = 0; t + 2 < iCount; t += 3)
            {
                Union(remap[indices[t]], remap[indices[t + 1]]);
                Union(remap[indices[t + 1]], remap[indices[t + 2]]);
            }

            // Component stats.
            var stats = new Dictionary<int, (int Tris, Vector3 Sum)>();
            var triComp = new int[iCount / 3];
            for (var t = 0; t + 2 < iCount; t += 3)
            {
                var comp = Find(remap[indices[t]]);
                triComp[t / 3] = comp;
                var centre = (pos[indices[t]] + pos[indices[t + 1]] + pos[indices[t + 2]]) / 3f;
                stats[comp] = stats.TryGetValue(comp, out var s) ? (s.Tris + 1, s.Sum + centre) : (1, centre);
            }

            // FEATURE ID CHANNEL: the LEGO face mesh identifies each feature shell by the INTEGER
            // BAND of TEXCOORD_1 (ExtraUV0) - base face u in [8,9], mouth shells u in [13,14], other
            // features in their own bands. This is the game's own feature selector (confirmed against
            // the community Blender scene, where the same objects carry exactly these UV ranges), and
            // it is far more reliable than geometry heuristics. The mouth's SHAPE is sculpted
            // geometry - the texture only colours it.
            var uv1Band = new int[pCount];
            var haveBands = false;
            if (prim.GetProperty("attributes").TryGetProperty("TEXCOORD_1", out var uv1El))
            {
                var uAcc = accessors[uv1El.GetInt32()];
                var uView = views[uAcc.GetProperty("bufferView").GetInt32()];
                var uStart = binOffset + (uView.TryGetProperty("byteOffset", out var ubo) ? ubo.GetInt32() : 0)
                             + (uAcc.TryGetProperty("byteOffset", out var uao) ? uao.GetInt32() : 0);
                var uStride = uView.TryGetProperty("byteStride", out var us) ? us.GetInt32() : 8;
                for (var i = 0; i < pCount && i < uAcc.GetProperty("count").GetInt32(); i++)
                {
                    uv1Band[i] = (int)MathF.Floor(BitConverter.ToSingle(data, uStart + i * uStride));
                }
                haveBands = uv1Band.Any(b => b != 0);
            }

            // Small components that are NOT the mouth are unused feature shells (eyes/eyelids/brows,
            // all bound to T_Dummy_Alpha_Off on cowled Batman faces) - hide them. Large components
            // are the face base.
            var kind = new Dictionary<int, int>(); // 0 base, 2 hidden
            foreach (var (comp, s) in stats)
            {
                kind[comp] = s.Tris < 600 ? 2 : 0;
            }

            // The mouth band is selected by POSITION, not connectivity: in the cooked mesh it is
            // welded to the face base shell (the community Blender scene has it as separate objects
            // SK_LEGOface.015/.030, measuring a WIDE ~6:1 band - 0.021 tall x 0.123 wide in glTF
            // units - seated at local y 0.089..0.115 on the front surface). Connectivity alone finds
            // only the small square inner-mouth cavity below it, which sampled the whole mouth sheet
            // and rendered as a round "O".
            var baseT = new List<uint>();
            var mouthT = new List<uint>();
            var hiddenT = new List<uint>();
            const int MouthBand = 13;
            const int FaceBand = 8;

            // Every OTHER band is an alternate feature shell - the expression system's sprite slots
            // (anim notifies "SpriteIndex00"/"SpriteIndex01" carry the index to show). Keep them in
            // band order inside the hidden run so the viewer can address each one individually.
            var otherBands = new SortedDictionary<int, List<uint>>();
            for (var t = 0; t + 2 < iCount; t += 3)
            {
                List<uint> bucket;
                if (haveBands)
                {
                    var band = uv1Band[indices[t]];
                    if (band == MouthBand) bucket = mouthT;
                    else if (band == FaceBand) bucket = baseT;
                    else
                    {
                        if (!otherBands.TryGetValue(band, out bucket!))
                        {
                            bucket = new List<uint>();
                            otherBands[band] = bucket;
                        }
                    }
                }
                else
                {
                    bucket = kind[triComp[t / 3]] == 2 ? hiddenT : baseT;
                }
                bucket.Add(indices[t]); bucket.Add(indices[t + 1]); bucket.Add(indices[t + 2]);
            }
            FaceBandLayout = otherBands.Select(kv => (kv.Key, kv.Value.Count / 3)).ToList();
            foreach (var kv in otherBands)
            {
                hiddenT.AddRange(kv.Value);
            }

            // Rewrite the index buffer in [base][mouth][hidden] order, in place.
            var reordered = baseT.Concat(mouthT).Concat(hiddenT).ToArray();
            for (var i = 0; i < reordered.Length; i++)
            {
                if (iType == 5125)
                {
                    BitConverter.GetBytes(reordered[i]).CopyTo(data, iStart + i * 4);
                }
                else
                {
                    BitConverter.GetBytes((ushort)reordered[i]).CopyTo(data, iStart + i * 2);
                }
            }
            File.WriteAllBytes(glbPath, data);
            return new[] { baseT.Count / 3, mouthT.Count / 3, hiddenT.Count / 3 };
        }
        catch
        {
            return null;
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
