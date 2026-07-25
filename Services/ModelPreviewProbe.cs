using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Animations;

namespace Batcomputer;

/// <summary>
/// Phase 0 spike for the 3D preview: prove CUE4Parse can open the game paks with our usmap and decode
/// a mesh into real geometry. Prints counts only - no rendering, no UI. Run via
/// <c>Batcomputer.exe --preview-probe "&lt;paksDir&gt;" "&lt;usmap&gt;" "&lt;objectPath&gt;"</c>.
/// </summary>
internal static class ModelPreviewProbe
{
    public static int Run(string paksDir, string usmapPath, string objectPath)
    {
        Console.WriteLine("Model preview probe (Phase 0)");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"paks:   {paksDir}");
        Console.WriteLine($"usmap:  {usmapPath}");
        Console.WriteLine($"object: {objectPath}");
        Console.WriteLine();

        if (!Directory.Exists(paksDir)) { Console.Error.WriteLine("paks dir not found"); return 2; }
        if (!File.Exists(usmapPath)) { Console.Error.WriteLine("usmap not found"); return 2; }

        var provider = new DefaultFileProvider(
            paksDir, SearchOption.AllDirectories, isCaseInsensitive: true, new VersionContainer(EGame.GAME_UE5_6));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        provider.Initialize();
        // Unencrypted paks: the zero key mounts them. Harmless if no key is needed.
        provider.SubmitKey(new FGuid(), new FAesKey("0x0000000000000000000000000000000000000000000000000000000000000000"));

        Console.WriteLine($"mounted files: {provider.Files.Count}");
        Console.WriteLine();

        // "search:<substring>" lists matching mounted files instead of loading one.
        if (objectPath.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
        {
            var needle = objectPath["search:".Length..];
            var hits = provider.Files.Keys
                .Where(k => k.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k).Take(40).ToList();
            Console.WriteLine($"files matching '{needle}': (showing {hits.Count})");
            foreach (var h in hits) Console.WriteLine("  " + h);
            return 0;
        }

        // "export:<objectPath>" writes the mesh to glTF (glb) so we can view it.
        if (objectPath.StartsWith("export:", StringComparison.OrdinalIgnoreCase))
        {
            var meshPath = objectPath["export:".Length..];
            var mesh = provider.LoadPackageObject(meshPath);
            var options = new ExporterOptions
            {
                MeshFormat = EMeshFormat.Gltf2,
                LodFormat = ELodFormat.FirstLod,
                ExportMorphTargets = false,
            };
            MeshExporter exporter = mesh switch
            {
                USkeletalMesh skm => new MeshExporter(skm, options),
                UStaticMesh stm => new MeshExporter(stm, options),
                _ => throw new InvalidOperationException($"{mesh.GetType().Name} is not an exportable mesh"),
            };
            var outDir = Path.Combine(Path.GetTempPath(), "bc_preview");
            Directory.CreateDirectory(outDir);
            if (exporter.TryWriteToDir(new DirectoryInfo(outDir), out var label, out var savedPath))
            {
                Console.WriteLine($"exported: {savedPath}");
            }
            else
            {
                Console.WriteLine($"export failed (label={label})");
            }
            return 0;
        }

        // "components:<bpPath>" dumps each skeletal-mesh component's mesh + material refs.
        if (objectPath.StartsWith("components:", StringComparison.OrdinalIgnoreCase))
        {
            var bpPath = objectPath["components:".Length..];
            var bp = provider.LoadPackage(bpPath);
            foreach (var exp in bp.GetExports())
            {
                if (!exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var meshRef = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMeshAsset")
                              ?? exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMesh");
                var mats = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials");
                var socket = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("AttachSocketName");
                var relLoc = exp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation");
                var relRot = exp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FRotator>("RelativeRotation");
                var relScale = exp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeScale3D");
                Console.WriteLine($"  {exp.Name}");
                Console.WriteLine($"      mesh:   {meshRef?.ResolvedObject?.GetPathName() ?? "(none / runtime)"}");
                Console.WriteLine($"      socket: {socket}   overrideMats: {mats?.Length ?? 0}");
                Console.WriteLine($"      loc: {relLoc}  rot: {relRot}  scale: {relScale}");
                if (mats is not null)
                {
                    foreach (var m in mats) Console.WriteLine($"        mat: {m?.ResolvedObject?.GetPathName()}");
                }
            }
            return 0;
        }

        // "sections:<meshPath>" prints LOD0's render sections and the mesh's own material slots -
        // which slot each section draws with, so cloth-sim proxy sections can be identified.
        if (objectPath.StartsWith("sections:", StringComparison.OrdinalIgnoreCase))
        {
            var m = provider.LoadPackageObject(objectPath["sections:".Length..]);
            if (m is USkeletalMesh sk3)
            {
                var mats = sk3.Materials ?? [];
                Console.WriteLine($"material slots: {mats.Length}");
                for (var i = 0; i < mats.Length; i++)
                {
                    Console.WriteLine($"  [{i}] {mats[i]?.Load()?.Name ?? "(none)"}");
                }
                if (sk3.TryConvert(out var conv2))
                {
                    for (var li = 0; li < conv2.LODs.Count; li++)
                    {
                        var secs = conv2.LODs[li].Sections.Value;
                        Console.WriteLine($"LOD{li}: {conv2.LODs[li].NumVerts} verts, {secs.Length} section(s)");
                        foreach (var s in secs)
                        {
                            Console.WriteLine($"    section: matIndex={s.MaterialIndex} firstFace={s.FirstIndex / 3} numFaces={s.NumFaces}");
                        }
                    }
                }
                // Raw render sections carry the cloth/disabled flags the converter drops.
                var lods = sk3.LODModels;
                for (var li = 0; lods is not null && li < lods.Length; li++)
                {
                    Console.WriteLine($"raw LOD{li}:");
                    foreach (var s in lods[li].Sections)
                    {
                        var t = s.GetType();
                        var line = $"    raw section matIndex={s.MaterialIndex} tris={s.NumTriangles}";
                        foreach (var fname in new[] { "bDisabled", "CorrespondClothAssetIndex", "ClothingData", "HasClothData", "ClothMappingDataLODs" })
                        {
                            var fi = t.GetField(fname) ?? null;
                            var pi = t.GetProperty(fname);
                            var v = fi?.GetValue(s) ?? pi?.GetValue(s);
                            if (v is not null) line += $" {fname}={(v is Array a ? $"[{a.Length}]" : v)}";
                        }
                        Console.WriteLine(line);
                    }
                }
            }
            return 0;
        }

        // "anim:<animPath>" loads a face expression animation and prints the pose it applies at
        // time 0 - the per-bone delta from the reference skeleton. The game drives faces this way
        // (SK_LEGOface has no morph targets; its PostProcessAnimBlueprint poses the facial rig).
        if (objectPath.StartsWith("anim:", StringComparison.OrdinalIgnoreCase))
        {
            var obj = provider.LoadPackageObject(objectPath["anim:".Length..]);
            Console.WriteLine($"[{obj.ExportType}] {obj.Name}");
            foreach (var prop in obj.Properties)
            {
                var val = prop.Tag?.GenericValue?.ToString() ?? "";
                if (val.Length > 70) val = val[..70] + "…";
                Console.WriteLine($"    {prop.Name.Text} = {val}");
            }
            if (obj is CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence anim)
            {
                Console.WriteLine($"  sequence: {anim.SequenceLength}s, {anim.NumFrames} frames");
                var skel = anim.Skeleton?.Load<CUE4Parse.UE4.Assets.Exports.Animation.USkeleton>();
                if (skel is not null)
                {
                    Console.WriteLine($"  skeleton: {skel.Name}");
                    var set = skel.ConvertAnims(anim);
                    foreach (var seq in set.Sequences)
                    {
                        Console.WriteLine($"  seq '{seq.Name}': {seq.Tracks.Count} tracks, {seq.NumFrames} frames");
                        for (var i = 0; i < seq.Tracks.Count && i < 40; i++)
                        {
                            var tr = seq.Tracks[i];
                            if (tr.KeyPos.Length == 0 && tr.KeyQuat.Length == 0) continue;
                            var refBones = skel.ReferenceSkeleton.FinalRefBoneInfo;
                            var bone = i < refBones.Length ? refBones[i].Name.Text : $"track{i}";
                            var p = tr.KeyPos.Length > 0 ? tr.KeyPos[0].ToString() : "-";
                            var q = tr.KeyQuat.Length > 0 ? tr.KeyQuat[0].ToString() : "-";
                            var s = tr.KeyScale.Length > 0 ? tr.KeyScale[0].ToString() : "-";
                            Console.WriteLine($"    {bone}: pos={p} quat={q} scale={s} (nPos={tr.KeyPos.Length} nQ={tr.KeyQuat.Length} nS={tr.KeyScale.Length})");
                        }
                    }
                }
            }
            return 0;
        }

        // "sockets:<meshPath>" lists the mesh's named attachment sockets and their transforms -
        // this is what components mean by AttachToName (e.g. HeadStud_Attach_Socket).
        if (objectPath.StartsWith("sockets:", StringComparison.OrdinalIgnoreCase))
        {
            if (provider.LoadPackageObject(objectPath["sockets:".Length..]) is USkeletalMesh skm2)
            {
                var socks = skm2.Sockets ?? [];
                Console.WriteLine($"sockets: {socks.Length}");
                foreach (var sref in socks)
                {
                    if (sref?.Load() is not { } so) continue;
                    var name = so.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("SocketName");
                    var bone = so.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("BoneName");
                    var loc = so.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation");
                    var rot = so.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FRotator>("RelativeRotation");
                    Console.WriteLine($"  {name} on bone {bone}  loc={loc} rot={rot}");
                }
            }
            return 0;
        }

        // "statics:<materialPath>" expands the STATIC parameter set (switches that enable or
        // disable whole material layers) and prints texture parameters untruncated.
        if (objectPath.StartsWith("statics:", StringComparison.OrdinalIgnoreCase))
        {
            var cur2 = provider.LoadPackageObject(objectPath["statics:".Length..]);
            var depth2 = 0;
            while (cur2 is not null && depth2++ < 6)
            {
                Console.WriteLine($"[{cur2.ExportType}] {cur2.Name}");
                var statics = cur2.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                if (statics is not null)
                {
                    foreach (var prop in statics.Properties)
                    {
                        Console.WriteLine($"    static.{prop.Name.Text} = {prop.Tag?.GenericValue}");
                        if (prop.Tag?.GenericValue is CUE4Parse.UE4.Assets.Objects.FStructFallback[] arr)
                        {
                            foreach (var e in arr)
                            {
                                var info = e.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo");
                                var nm = info?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "?";
                                var val = e.Properties.FirstOrDefault(x => x.Name.Text == "Value")?.Tag?.GenericValue;
                                Console.WriteLine($"        {nm} = {val}");
                            }
                        }
                    }
                }
                var texes = cur2.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues");
                if (texes is not null)
                {
                    foreach (var e in texes)
                    {
                        var nm = e.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                  ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "?";
                        var t = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParameterValue")?.ResolvedObject;
                        Console.WriteLine($"    tex {nm} = {t?.GetPathName()}");
                    }
                }
                cur2 = cur2.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Parent")?.ResolvedObject?.Load();
            }
            return 0;
        }

        // "statics:<materialPath>" expands the STATIC parameter set - the switches that turn whole
        // material layers on or off. A texture being bound proves nothing on its own; the switch is
        // what decides whether the game draws that layer.
        if (objectPath.StartsWith("statics:", StringComparison.OrdinalIgnoreCase))
        {
            var cur2 = provider.LoadPackageObject(objectPath["statics:".Length..]);
            var depth2 = 0;
            while (cur2 is not null && depth2++ < 6)
            {
                Console.WriteLine($"[{cur2.ExportType}] {cur2.Name}");
                var statics = cur2.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                if (statics is not null)
                {
                    foreach (var prop in statics.Properties)
                    {
                        var gv = prop.Tag?.GenericValue;
                        Console.WriteLine($"    [{prop.Name.Text}] {gv?.GetType().Name}");
                        if (gv is System.Collections.IEnumerable seq and not string)
                        {
                            foreach (var item in seq)
                            {
                                if (item is CUE4Parse.UE4.Assets.Objects.FStructFallback sf)
                                {
                                    var nm = sf.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                               ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "?";
                                    var parts = sf.Properties.Select(x => $"{x.Name.Text}={x.Tag?.GenericValue}");
                                    Console.WriteLine($"        SWITCH {nm}: {string.Join(", ", parts)}");
                                }
                                else
                                {
                                    Console.WriteLine($"        raw {item}");
                                }
                            }
                        }
                    }
                }
                cur2 = cur2.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Parent")?.ResolvedObject?.Load();
            }
            return 0;
        }

        // "bones:<meshPath>" prints the skeleton: each bone name, parent, and local position.
        if (objectPath.StartsWith("bones:", StringComparison.OrdinalIgnoreCase))
        {
            var m = provider.LoadPackageObject(objectPath["bones:".Length..]);
            if (m is USkeletalMesh sk && sk.TryConvert(out var conv))
            {
                Console.WriteLine($"bones: {conv.RefSkeleton.Count}");
                for (var i = 0; i < conv.RefSkeleton.Count; i++)
                {
                    var b = conv.RefSkeleton[i];
                    Console.WriteLine($"  [{i}] {b.Name} parent={b.ParentIndex} pos={b.Position} quat={b.Orientation}");
                }
            }
            return 0;
        }

        // "ueworld:<meshPath>" computes bone world positions in UE space from the reference skeleton
        // (accumulating position+orientation), which - unlike the exported glTF nodes - is spatially
        // real. Used to derive the UE -> glTF axis mapping for attachment placement.
        if (objectPath.StartsWith("ueworld:", StringComparison.OrdinalIgnoreCase))
        {
            var m = provider.LoadPackageObject(objectPath["ueworld:".Length..]);
            if (m is USkeletalMesh sk2 && sk2.TryConvert(out var cv))
            {
                var world = new System.Numerics.Matrix4x4[cv.RefSkeleton.Count];
                for (var i = 0; i < cv.RefSkeleton.Count; i++)
                {
                    var b = cv.RefSkeleton[i];
                    var local = System.Numerics.Matrix4x4.CreateFromQuaternion(
                                    new System.Numerics.Quaternion(b.Orientation.X, b.Orientation.Y, b.Orientation.Z, b.Orientation.W))
                                * System.Numerics.Matrix4x4.CreateTranslation(b.Position.X, b.Position.Y, b.Position.Z);
                    world[i] = b.ParentIndex >= 0 ? local * world[b.ParentIndex] : local;
                    var t = world[i].Translation;
                    if (b.Name.Text is "Root" or "Pelvis" or "Chest" or "Neck" or "Head" or "Head_Attach_01" or "AttachRoot")
                    {
                        Console.WriteLine($"  {b.Name}: UE world ({t.X:0.##}, {t.Y:0.##}, {t.Z:0.##})  -> /100 ({t.X / 100:0.####}, {t.Y / 100:0.####}, {t.Z / 100:0.####})");
                    }
                }
            }
            return 0;
        }

        // "tex:<texturePath>|<outFile>" decodes a texture to PNG so it can be inspected.
        if (objectPath.StartsWith("tex:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = objectPath["tex:".Length..].Split('|');
            if (provider.LoadPackageObject(spec[0]) is CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D t2)
            {
                Console.WriteLine($"format={t2.Format}");
                Console.WriteLine(TextureDecodeService.TryExportPng(t2, spec[1]) ? $"wrote {spec[1]}" : "decode failed");
            }
            else Console.WriteLine("not a Texture2D");
            return 0;
        }

        // "material:<path>" resolves a material instance: its texture/scalar/vector parameters and
        // the parent chain, which is how the LEGO colour model is actually configured.
        if (objectPath.StartsWith("material:", StringComparison.OrdinalIgnoreCase))
        {
            var cur = provider.LoadPackageObject(objectPath["material:".Length..]);
            var depth = 0;
            while (cur is not null && depth++ < 6)
            {
                Console.WriteLine($"[{cur.ExportType}] {cur.Name}");
                DumpParams(cur, "TextureParameterValues", "ParameterValue");
                DumpParams(cur, "VectorParameterValues", "ParameterValue");
                DumpParams(cur, "ScalarParameterValues", "ParameterValue");
                var parent = cur.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Parent");
                var next = parent?.ResolvedObject?.Load();
                if (next is null) break;
                Console.WriteLine($"  -> parent: {next.Name}");
                cur = next;
            }
            return 0;

            static void DumpParams(CUE4Parse.UE4.Assets.Exports.UObject obj, string arrayName, string valueKey)
            {
                var arr = obj.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>(arrayName);
                if (arr is null || arr.Length == 0) return;
                Console.WriteLine($"  {arrayName}:");
                foreach (var entry in arr)
                {
                    var info = entry.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo");
                    var name = info?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "?";
                    var val = entry.Properties.FirstOrDefault(p => p.Name.Text == valueKey)?.Tag?.GenericValue?.ToString() ?? "";
                    if (val.Length > 80) val = val[..80] + "…";
                    Console.WriteLine($"    {name} = {val}");
                }
            }
        }

        // "props:<pkgPath>" dumps every export's properties whose name or value mentions "mesh".
        if (objectPath.StartsWith("props:", StringComparison.OrdinalIgnoreCase))
        {
            var p = provider.LoadPackage(objectPath["props:".Length..]);
            foreach (var exp in p.GetExports())
            {
                Console.WriteLine($"[{exp.ExportType}] {exp.Name} ({exp.Properties.Count} props)");
                foreach (var prop in exp.Properties)
                {
                    var val = prop.Tag?.GenericValue?.ToString() ?? "";
                    if (val.Length > 90) val = val[..90] + "…";
                    Console.WriteLine($"    {prop.Name.Text} = {val}");
                }
            }
            return 0;
        }

        // Load the whole package and walk its exports; convert any mesh we find.
        var pkg = provider.LoadPackage(objectPath);
        var exports = pkg.GetExports().ToList();
        Console.WriteLine($"package exports: {exports.Count}");
        foreach (var exp in exports)
        {
            Console.WriteLine($"  [{exp.GetType().Name}] {exp.Name}");
            if (exp is USkeletalMesh skel && skel.TryConvert(out var cs))
            {
                var lod = cs.LODs[0];
                Console.WriteLine($"      -> skeletal: {cs.LODs.Count} LODs, {cs.RefSkeleton.Count} bones, " +
                                  $"LOD0 {lod.NumVerts} verts / {lod.Sections.Value.Length} sections");
            }
            else if (exp is UStaticMesh stat && stat.TryConvert(out var cm))
            {
                var lod = cm.LODs[0];
                Console.WriteLine($"      -> static: LOD0 {lod.NumVerts} verts / {lod.Sections.Value.Length} sections");
            }
        }

        Console.WriteLine(new string('-', 60));
        Console.WriteLine("OK");
        return 0;
    }
}
