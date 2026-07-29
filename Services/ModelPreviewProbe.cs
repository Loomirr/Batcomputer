using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Animations;
using CUE4Parse.UE4.Objects.UObject;

namespace Batcomputer;

/// <summary>CLI diagnostics for preview data and CUE4Parse asset loading.</summary>
internal static class ModelPreviewProbe
{
    public static int Run(string paksDir, string usmapPath, string objectPath)
    {
        Console.WriteLine("Model preview probe");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"paks:   {paksDir}");
        Console.WriteLine($"usmap:  {usmapPath}");
        Console.WriteLine($"object: {objectPath}");
        Console.WriteLine();

        if (!Directory.Exists(paksDir)) { Console.Error.WriteLine("paks dir not found"); return 2; }
        if (!File.Exists(usmapPath)) { Console.Error.WriteLine("usmap not found"); return 2; }

        var provider = new DefaultFileProvider(
            paksDir, SearchOption.AllDirectories,
            versions: new VersionContainer(EGame.GAME_UE5_6),
            pathComparer: StringComparer.OrdinalIgnoreCase);
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

        // "texturefind:<path needle>|<width>|<format>|<limit>" scans cooked Texture2D
        // assets by their actual first mip and pixel format. It is deliberately read-only:
        // used to locate a same-role native donor before any texture cooking experiment.
        if (objectPath.StartsWith("texturefind:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = objectPath["texturefind:".Length..].Split('|');
            var needle = spec.ElementAtOrDefault(0) ?? "";
            var wantedWidth = int.TryParse(spec.ElementAtOrDefault(1), out var parsedWidth) ? parsedWidth : 0;
            var wantedFormat = spec.ElementAtOrDefault(2) ?? "";
            var limit = int.TryParse(spec.ElementAtOrDefault(3), out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 500) : 80;
            var candidates = provider.Files.Keys
                .Where(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("T_", StringComparison.OrdinalIgnoreCase))
                .Where(path => string.IsNullOrWhiteSpace(needle) || path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .ToList();
            Console.WriteLine($"scanning {candidates.Count} Texture2D candidate(s): needle='{needle}', width={wantedWidth}, format='{wantedFormat}'");

            var matches = 0;
            var loaded = 0;
            foreach (var file in candidates)
            {
                try
                {
                    if (provider.LoadPackageObject(file[..file.LastIndexOf('.')]) is not CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D texture)
                    {
                        continue;
                    }
                    loaded++;
                    var mip = texture.GetFirstMip();
                    if (mip is null || (wantedWidth > 0 && (mip.SizeX != wantedWidth || mip.SizeY != wantedWidth)) ||
                        (!string.IsNullOrWhiteSpace(wantedFormat) && !texture.Format.ToString().Equals(wantedFormat, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    Console.WriteLine($"  {file[..file.LastIndexOf('.')]}  {mip.SizeX}x{mip.SizeY} {texture.Format}");
                    matches++;
                    if (matches >= limit)
                    {
                        break;
                    }
                }
                catch
                {
                    // Some package variants are intentionally unavailable to the parser.
                }
            }
            Console.WriteLine($"texture matches: {matches} (loaded {loaded} of {candidates.Count})");
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
            var outDir = Path.Combine(AppSettings.GeneratedRootFor(AppSettings.Current.EffectiveProjectRoot()), "Preview", "Probe");
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

        // "components:<bpPath>" dumps each mesh component's mesh + material refs.
        if (objectPath.StartsWith("components:", StringComparison.OrdinalIgnoreCase))
        {
            var bpPath = objectPath["components:".Length..];
            var bp = provider.LoadPackage(bpPath);
            foreach (var exp in bp.GetExports())
            {
                var isSkeletal = exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase);
                var isStatic = exp.ExportType.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
                if (!isSkeletal && !isStatic)
                {
                    continue;
                }
                var meshRef = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMeshAsset")
                              ?? exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMesh")
                              ?? exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh");
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

        // "componentfind:<needle>" scans playable minifig Blueprints for a component or referenced
        // mesh/material name. It makes a placement issue reproducible without guessing which
        // character happens to use an attachment such as SM_UtilityBelt.
        if (objectPath.StartsWith("componentfind:", StringComparison.OrdinalIgnoreCase))
        {
            var needle = objectPath["componentfind:".Length..];
            var candidates = provider.Files.Keys
                .Where(k => k.StartsWith("LEGOBatmanLotDK/Content/Characters/Minifig/", StringComparison.OrdinalIgnoreCase)
                            && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileNameWithoutExtension(k).StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k)
                .ToList();
            Console.WriteLine($"searching {candidates.Count} minifig Blueprints for '{needle}'...");
            var matches = 0;
            foreach (var path in candidates)
            {
                try
                {
                    var package = provider.LoadPackage(path);
                    foreach (var exp in package.GetExports())
                    {
                        var isSkeletal = exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase);
                        var isStatic = exp.ExportType.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
                        if (!isSkeletal && !isStatic)
                        {
                            continue;
                        }

                        var mesh = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMeshAsset")
                                   ?? exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMesh")
                                   ?? exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh");
                        var meshPath = mesh?.ResolvedObject?.GetPathName() ?? "";
                        var materials = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials")
                                        ?? Array.Empty<CUE4Parse.UE4.Objects.UObject.FPackageIndex>();
                        var materialPaths = materials
                            .Select(m => m?.ResolvedObject?.GetPathName() ?? "")
                            .Where(m => m.Length > 0)
                            .ToArray();
                        var haystack = string.Join(" ", new[] { exp.Name, meshPath }.Concat(materialPaths));
                        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var socket = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("AttachSocketName");
                        Console.WriteLine($"  {path}");
                        Console.WriteLine($"    {exp.Name}: {meshPath}  socket={socket}");
                        foreach (var material in materialPaths) Console.WriteLine($"      mat: {material}");
                        matches++;
                    }
                }
                catch
                {
                    // A bad or abstract Blueprint should not hide a valid attachment in another BP.
                }
            }
            Console.WriteLine($"component matches: {matches}");
            return 0;
        }

        // "attachmentaudit:<needle>" reads the cooked Simple Construction Script (SCS) for every
        // matching minifig component. The component templates themselves commonly lose their
        // AttachParent/AttachSocketName during cooking, so the SCS is the source of truth for
        // preview placement. This stays diagnostic-only: it never exports or modifies assets.
        if (objectPath.StartsWith("attachmentaudit:", StringComparison.OrdinalIgnoreCase))
        {
            var needle = objectPath["attachmentaudit:".Length..];
            var candidates = provider.Files.Keys
                .Where(k => k.StartsWith("LEGOBatmanLotDK/Content/Characters/Minifig/", StringComparison.OrdinalIgnoreCase)
                            && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileNameWithoutExtension(k).StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k)
                .ToList();
            var sockets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unusual = new List<string>();
            var matched = 0;
            var bodyParented = 0;
            var nested = 0;
            var missing = 0;

            static string? NameValue(CUE4Parse.UE4.Assets.Exports.UObject obj, string property)
            {
                var value = obj.GetOrDefault<FName>(property).Text;
                return string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : value;
            }

            static bool IsBodyParent(string? parent) => parent is not null
                && (parent.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)
                    || parent.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                    || parent.Contains("CharacterMesh0", StringComparison.OrdinalIgnoreCase));

            static bool HasAuthoredTransform(FVector location, FRotator rotation, FVector scale)
            {
                const float epsilon = 0.0001f;
                var scaleIsDefault = (Math.Abs(scale.X) < epsilon && Math.Abs(scale.Y) < epsilon && Math.Abs(scale.Z) < epsilon)
                                     || (Math.Abs(scale.X - 1) < epsilon && Math.Abs(scale.Y - 1) < epsilon && Math.Abs(scale.Z - 1) < epsilon);
                return Math.Abs(location.X) >= epsilon || Math.Abs(location.Y) >= epsilon || Math.Abs(location.Z) >= epsilon
                       || Math.Abs(rotation.Pitch) >= epsilon || Math.Abs(rotation.Yaw) >= epsilon || Math.Abs(rotation.Roll) >= epsilon
                       || !scaleIsDefault;
            }

            foreach (var path in candidates)
            {
                try
                {
                    var package = provider.LoadPackage(path);
                    var scs = new Dictionary<string, (string? Parent, string? Socket)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var node in package.GetExports().Where(e => e.ExportType.Contains("SCS_Node", StringComparison.OrdinalIgnoreCase)))
                    {
                        var pair = (NameValue(node, "ParentComponentOrVariableName"), NameValue(node, "AttachToName"));
                        var template = node.GetOrDefault<FPackageIndex>("ComponentTemplate")?.ResolvedObject?.Name.Text;
                        var variable = NameValue(node, "InternalVariableName");
                        if (!string.IsNullOrWhiteSpace(template)) scs[template] = pair;
                        if (!string.IsNullOrWhiteSpace(variable)) scs[variable] = pair;
                    }

                    foreach (var exp in package.GetExports())
                    {
                        var isSkeletal = exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase);
                        var isStatic = exp.ExportType.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
                        if (!isSkeletal && !isStatic)
                        {
                            continue;
                        }

                        var mesh = exp.GetOrDefault<FPackageIndex>("SkeletalMeshAsset")
                                   ?? exp.GetOrDefault<FPackageIndex>("SkeletalMesh")
                                   ?? exp.GetOrDefault<FPackageIndex>("StaticMesh");
                        var meshPath = mesh?.ResolvedObject?.GetPathName() ?? "";
                        if (!($"{exp.Name} {meshPath}".Contains(needle, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        matched++;
                        var attachment = scs.TryGetValue(exp.Name, out var fromScs)
                            ? fromScs
                            : (Parent: exp.GetOrDefault<FPackageIndex>("AttachParent")?.ResolvedObject?.Name.Text,
                               Socket: NameValue(exp, "AttachSocketName"));
                        var parent = attachment.Parent;
                        var socket = attachment.Socket;
                        if (IsBodyParent(parent)) bodyParented++;
                        else if (parent is null) missing++;
                        else nested++;
                        var socketLabel = socket ?? "(none)";
                        sockets[socketLabel] = sockets.GetValueOrDefault(socketLabel) + 1;

                        var location = exp.GetOrDefault<FVector>("RelativeLocation");
                        var rotation = exp.GetOrDefault<FRotator>("RelativeRotation");
                        var scale = exp.GetOrDefault<FVector>("RelativeScale3D");
                        if (!IsBodyParent(parent) || string.IsNullOrWhiteSpace(socket) || HasAuthoredTransform(location, rotation, scale))
                        {
                            unusual.Add($"  {path}: {exp.Name} ({(isStatic ? "static" : "skeletal")})"
                                        + $" parent={parent ?? "(none)"} socket={socket ?? "(none)"}"
                                        + $" loc={location} rot={rotation} scale={scale}");
                        }
                    }
                }
                catch
                {
                    // One malformed or abstract BP should not prevent the remaining placement audit.
                }
            }

            Console.WriteLine($"attachment audit '{needle}': {matched} matching components");
            Console.WriteLine($"  body-parented={bodyParented}, nested={nested}, missing-parent={missing}");
            Console.WriteLine("  sockets:");
            foreach (var (socket, count) in sockets.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(20))
            {
                Console.WriteLine($"    {socket}: {count}");
            }
            Console.WriteLine($"  nonstandard or transformed (showing {Math.Min(80, unusual.Count)} of {unusual.Count}):");
            foreach (var line in unusual.Take(80)) Console.WriteLine(line);
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
                        var secs = conv2.LODs[li].Sections?.Value ?? [];
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

        // "animmeta:<animPath>" reveals the cooked animation object's compressed members. UE packs
        // curves below UAsset property tags, so this is the first step in discovering whether a face
        // expression drives material parameters such as MouthHide or the teeth UV offsets.
        if (objectPath.StartsWith("animmeta:", StringComparison.OrdinalIgnoreCase))
        {
            var obj = provider.LoadPackageObject(objectPath["animmeta:".Length..]);
            var type = obj.GetType();
            Console.WriteLine($"[{obj.ExportType}] {obj.Name} -> {type.FullName}");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic;
            foreach (var member in type.GetMembers(flags)
                         .Where(m => m.MemberType is System.Reflection.MemberTypes.Field
                                     or System.Reflection.MemberTypes.Property)
                         .Where(m => m.Name.Contains("curve", StringComparison.OrdinalIgnoreCase)
                                     || m.Name.Contains("compressed", StringComparison.OrdinalIgnoreCase)
                                     || m.Name.Contains("raw", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.Name))
            {
                object? value = null;
                try
                {
                    value = member switch
                    {
                        System.Reflection.FieldInfo f => f.GetValue(obj),
                        System.Reflection.PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(obj),
                        _ => null,
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {member.Name}: <read failed: {ex.Message.Split('\n')[0]}>");
                    continue;
                }
                var description = value switch
                {
                    null => "null",
                    Array a => $"{value.GetType().Name}[{a.Length}]",
                    _ => value.GetType().FullName ?? value.GetType().Name,
                };
                Console.WriteLine($"  {member.MemberType} {member.Name}: {description}");
            }

            if (type.GetField("CompressedCurveNames", flags)?.GetValue(obj) is Array curveNames)
            {
                Console.WriteLine($"  curve names ({curveNames.Length}):");
                foreach (var entry in curveNames)
                {
                    if (entry is null) continue;
                    var fields = entry.GetType().GetFields(flags)
                        .Select(f => $"{f.Name}={f.GetValue(entry)}")
                        .ToArray();
                    var properties = entry.GetType().GetProperties(flags)
                        .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                        .Select(p =>
                        {
                            try { return $"{p.Name}={p.GetValue(entry)}"; }
                            catch { return $"{p.Name}=<unreadable>"; }
                        })
                        .ToArray();
                    Console.WriteLine("    " + string.Join(" ", fields.Concat(properties)));
                }
            }

            if (type.GetField("CompressedCurveData", flags)?.GetValue(obj) is { } curveData)
            {
                Console.WriteLine($"  compressed curve data: {curveData.GetType().FullName}");
                foreach (var member in curveData.GetType().GetMembers(flags)
                             .Where(m => m.MemberType is System.Reflection.MemberTypes.Field
                                         or System.Reflection.MemberTypes.Property)
                             .OrderBy(m => m.Name))
                {
                    object? value = null;
                    try
                    {
                        value = member switch
                        {
                            System.Reflection.FieldInfo f => f.GetValue(curveData),
                            System.Reflection.PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(curveData),
                            _ => null,
                        };
                    }
                    catch { }
                    var description = value switch
                    {
                        null => "null",
                        Array a => $"{value.GetType().Name}[{a.Length}]",
                        _ => value.GetType().FullName ?? value.GetType().Name,
                    };
                    Console.WriteLine($"    {member.MemberType} {member.Name}: {description}");
                }

                var names = type.GetField("CompressedCurveNames", flags)?.GetValue(obj) as Array;
                var floats = ReadMember(curveData, "FloatCurves") as Array;
                if (floats is not null)
                {
                    Console.WriteLine($"  decoded float-curve keys ({floats.Length}):");
                    for (var i = 0; i < floats.Length; i++)
                    {
                        var curve = floats.GetValue(i);
                        var name = names is not null && i < names.Length
                            ? ReadMember(names.GetValue(i)!, "DisplayName")?.ToString() ?? $"curve{i}"
                            : $"curve{i}";
                        var rich = curve is null ? null : ReadMember(curve, "FloatCurve", "Curve");
                        var rawKeys = rich is null ? null : ReadMember(rich, "Keys");
                        var keys = rawKeys switch
                        {
                            Array a => a.Cast<object?>(),
                            System.Collections.IEnumerable e => e.Cast<object?>(),
                            _ => Enumerable.Empty<object?>(),
                        };
                        var samples = keys.Select(k =>
                        {
                            if (k is null) return "?";
                            var time = ReadMember(k, "Time");
                            var value = ReadMember(k, "Value");
                            return $"{time}:{value}";
                        }).ToArray();
                        Console.WriteLine($"    {name} = {(samples.Length == 0 ? "(no keys)" : string.Join(", ", samples))}");
                    }
                }
            }
            return 0;

            static object? ReadMember(object target, params string[] names)
            {
                const System.Reflection.BindingFlags memberFlags = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (var name in names)
                {
                    try
                    {
                        var field = target.GetType().GetField(name, memberFlags);
                        if (field is not null) return field.GetValue(target);
                        var property = target.GetType().GetProperty(name, memberFlags);
                        if (property is not null && property.GetIndexParameters().Length == 0)
                        {
                            return property.GetValue(target);
                        }
                    }
                    catch
                    {
                        // A private implementation detail may reject reflection; try the next name.
                    }
                }
                return null;
            }
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
                        if (prop.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.UScriptArray arr)
                        {
                            continue;
                        }
                        Console.WriteLine($"  {prop.Name.Text}: {arr.Properties.Count}");
                        foreach (var item in arr.Properties)
                        {
                            // Each entry is a struct: unwrap to its FStructFallback and read the
                            // parameter name + bool value.
                            var sf = item.GenericValue as CUE4Parse.UE4.Assets.Objects.FStructFallback
                                     ?? (item.GenericValue as CUE4Parse.UE4.Assets.Objects.FScriptStruct)
                                        ?.StructType as CUE4Parse.UE4.Assets.Objects.FStructFallback;
                            if (sf is null)
                            {
                                Console.WriteLine($"      ? {item.GenericValue?.GetType().Name}");
                                continue;
                            }
                            var nm = sf.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                       ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "?";
                            var pairs = sf.Properties
                                .Where(x => x.Name.Text is "Value" or "bOverride")
                                .Select(x => $"{x.Name.Text}={x.Tag?.GenericValue}");
                            Console.WriteLine($"      SWITCH {nm}  {string.Join(" ", pairs)}");
                        }
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
                // "tex:<path>|<out>|a" keeps ALPHA. Face artwork lives in the alpha channel, so a
                // dump without it shows a blank white square and looks like an empty texture.
                var keepAlpha = spec.Length > 2 && spec[2].StartsWith("a", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(TextureDecodeService.TryExportPng(t2, spec[1], keepAlpha: keepAlpha)
                    ? $"wrote {spec[1]}{(keepAlpha ? " (alpha kept)" : "")}" : "decode failed");
            }
            else Console.WriteLine("not a Texture2D");
            return 0;
        }

        // "facezones:" scans EVERY face material in the game and reports the complete zone
        // vocabulary - which zone numbers exist, what feature each is, and how many characters
        // enable it. This is how the band map is derived instead of guessed.
        if (objectPath.StartsWith("facezones:", StringComparison.OrdinalIgnoreCase))
        {
            var zoneNames = new SortedDictionary<int, string>();
            var enabledBy = new SortedDictionary<int, List<string>>();
            var scanned = 0;
            foreach (var f in provider.Files.Keys)
            {
                if (!f.Contains("MI_FACE", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains("MI_LEGOface", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                CUE4Parse.UE4.Assets.Exports.UObject? mat = null;
                try { mat = provider.LoadPackageObject(f[..f.LastIndexOf('.')]); } catch { }
                if (mat is null) continue;
                scanned++;
                var statics = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                foreach (var prop in statics?.Properties ?? new List<CUE4Parse.UE4.Assets.Objects.FPropertyTag>())
                {
                    if (prop.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.UScriptArray arr) continue;
                    foreach (var item in arr.Properties)
                    {
                        var sf = item.GenericValue as CUE4Parse.UE4.Assets.Objects.FStructFallback
                                 ?? (item.GenericValue as CUE4Parse.UE4.Assets.Objects.FScriptStruct)
                                    ?.StructType as CUE4Parse.UE4.Assets.Objects.FStructFallback;
                        var name = sf?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                     ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text;
                        if (name is null) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(name, @"^Enable Zone (\d+)\s*\(([^)]+)\)");
                        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var zone)) continue;
                        zoneNames[zone] = m.Groups[2].Value.Trim();
                        if (sf!.GetOrDefault<bool>("Value"))
                        {
                            if (!enabledBy.TryGetValue(zone, out var list)) enabledBy[zone] = list = new List<string>();
                            list.Add(mat.Name);
                        }
                    }
                }
            }
            Console.WriteLine($"scanned {scanned} face materials");
            Console.WriteLine("binding vs enabling (does a bound texture imply the zone draws?):");
            foreach (var (zone, feature) in zoneNames)
            {
                int binds = 0, bindsAndEnables = 0, bindsNotEnables = 0;
                foreach (var f in provider.Files.Keys)
                {
                    if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!f.Contains("MI_FACE", StringComparison.OrdinalIgnoreCase)) continue;
                    CUE4Parse.UE4.Assets.Exports.UObject? mat = null;
                    try { mat = provider.LoadPackageObject(f[..f.LastIndexOf('.')]); } catch { }
                    if (mat is null) continue;
                    var tp = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues");
                    var bound = false;
                    foreach (var e in tp ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>())
                    {
                        var pn = e.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                  ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text;
                        if (pn is null) continue;
                        var bare = pn.Replace(" ", "");
                        if (!bare.StartsWith(feature.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!bare.EndsWith("BC", StringComparison.OrdinalIgnoreCase)
                            && !bare.EndsWith("BCPrestine", StringComparison.OrdinalIgnoreCase)) continue;
                        var texName = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParameterValue")
                                       ?.ResolvedObject?.Name.Text ?? "";
                        if (texName.Contains("Dummy", StringComparison.OrdinalIgnoreCase)) continue;
                        bound = true; break;
                    }
                    if (!bound) continue;
                    binds++;
                    if (enabledBy.TryGetValue(zone, out var el) && el.Contains(mat.Name)) bindsAndEnables++;
                    else bindsNotEnables++;
                }
                if (binds > 0)
                {
                    Console.WriteLine($"  {zone,2} {feature,-18} bound by {binds,3}: {bindsAndEnables,3} also enable, {bindsNotEnables,3} do NOT");
                }
            }
            Console.WriteLine("zone vocabulary (zone = ExtraUV0 band):");
            foreach (var (zone, feature) in zoneNames)
            {
                var n = enabledBy.TryGetValue(zone, out var l) ? l.Count : 0;
                var sample = n > 0 ? "  e.g. " + string.Join(", ", l!.Take(3)) : "";
                Console.WriteLine($"  {zone,2} {feature,-18} enabled by {n,3} materials{sample}");
            }
            return 0;
        }

        // "facetints:" aggregates every "<feature> Tint" set by every face material in the game.
        // The master's own defaults are stripped from cooked paks, but if dozens of characters set
        // the same colour for a feature, that IS the intended colour - measured, not guessed.
        if (objectPath.StartsWith("facetints:", StringComparison.OrdinalIgnoreCase))
        {
            var byParam = new SortedDictionary<string, Dictionary<string, int>>();
            foreach (var f in provider.Files.Keys)
            {
                if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                if (!f.Contains("MI_FACE", StringComparison.OrdinalIgnoreCase)) continue;
                CUE4Parse.UE4.Assets.Exports.UObject? mat = null;
                try { mat = provider.LoadPackageObject(f[..f.LastIndexOf('.')]); } catch { }
                if (mat is null) continue;
                var vp = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues");
                foreach (var e in vp ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>())
                {
                    var pn = e.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                              ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text;
                    if (pn is null) continue;
                    var v = e.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue");
                    var hex = $"{(int)Math.Clamp(v.R * 255f + 0.5f, 0, 255):X2}"
                            + $"{(int)Math.Clamp(v.G * 255f + 0.5f, 0, 255):X2}"
                            + $"{(int)Math.Clamp(v.B * 255f + 0.5f, 0, 255):X2}";
                    if (!byParam.TryGetValue(pn, out var d)) byParam[pn] = d = new Dictionary<string, int>();
                    d[hex] = d.TryGetValue(hex, out var c) ? c + 1 : 1;
                }
            }
            foreach (var (pn, d) in byParam)
            {
                var total = d.Values.Sum();
                var top = d.OrderByDescending(x => x.Value).Take(4)
                           .Select(x => $"#{x.Key} x{x.Value}");
                Console.WriteLine($"  {pn,-28} set by {total,3}: {string.Join("  ", top)}");
            }
            return 0;
        }

        // "faceaudit:" is a full census of the LEGOface material namespace: every parameter of every
        // kind across every face material in the game, with how many characters set it and the
        // values they choose. This is the closest thing to the stripped master material's own
        // definition that cooked paks can yield.
        if (objectPath.StartsWith("faceaudit:", StringComparison.OrdinalIgnoreCase))
        {
            var tex = new SortedDictionary<string, (int Set, int NonDummy, Dictionary<string, int> Vals)>();
            var vec = new SortedDictionary<string, Dictionary<string, int>>();
            var sca = new SortedDictionary<string, Dictionary<string, int>>();
            var sw = new SortedDictionary<string, (int True, int False)>();
            var scanned = 0;
            foreach (var f in provider.Files.Keys)
            {
                if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                if (!f.Contains("MI_FACE", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains("MI_LEGOface", StringComparison.OrdinalIgnoreCase)) continue;
                CUE4Parse.UE4.Assets.Exports.UObject? mat = null;
                try { mat = provider.LoadPackageObject(f[..f.LastIndexOf('.')]); } catch { }
                if (mat is null) continue;
                scanned++;
                static string? PName(CUE4Parse.UE4.Assets.Objects.FStructFallback e) =>
                    e.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                     ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text;

                foreach (var e in mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues")
                                  ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>())
                {
                    var pn = PName(e); if (pn is null) continue;
                    var tn = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParameterValue")
                              ?.ResolvedObject?.Name.Text ?? "(null)";
                    var dummy = tn.Contains("Dummy", StringComparison.OrdinalIgnoreCase);
                    if (!tex.TryGetValue(pn, out var t)) t = (0, 0, new Dictionary<string, int>());
                    t.Vals[tn] = t.Vals.TryGetValue(tn, out var c) ? c + 1 : 1;
                    tex[pn] = (t.Set + 1, t.NonDummy + (dummy ? 0 : 1), t.Vals);
                }
                foreach (var e in mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues")
                                  ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>())
                {
                    var pn = PName(e); if (pn is null) continue;
                    var v = e.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue");
                    var hex = $"{(int)Math.Clamp(v.R*255f+0.5f,0,255):X2}{(int)Math.Clamp(v.G*255f+0.5f,0,255):X2}{(int)Math.Clamp(v.B*255f+0.5f,0,255):X2}";
                    if (!vec.TryGetValue(pn, out var d)) vec[pn] = d = new Dictionary<string, int>();
                    d[hex] = d.TryGetValue(hex, out var c) ? c + 1 : 1;
                }
                foreach (var e in mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameterValues")
                                  ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>())
                {
                    var pn = PName(e); if (pn is null) continue;
                    var v = e.GetOrDefault<float>("ParameterValue").ToString("0.###");
                    if (!sca.TryGetValue(pn, out var d)) sca[pn] = d = new Dictionary<string, int>();
                    d[v] = d.TryGetValue(v, out var c) ? c + 1 : 1;
                }
                var statics = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                foreach (var prop in statics?.Properties ?? new List<CUE4Parse.UE4.Assets.Objects.FPropertyTag>())
                {
                    if (prop.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.UScriptArray arr) continue;
                    foreach (var item in arr.Properties)
                    {
                        var sf = item.GenericValue as CUE4Parse.UE4.Assets.Objects.FStructFallback
                                 ?? (item.GenericValue as CUE4Parse.UE4.Assets.Objects.FScriptStruct)
                                    ?.StructType as CUE4Parse.UE4.Assets.Objects.FStructFallback;
                        if (sf is null) continue;
                        var pn = PName(sf); if (pn is null) continue;
                        var val = sf.GetOrDefault<bool>("Value");
                        var cur = sw.TryGetValue(pn, out var x) ? x : (0, 0);
                        sw[pn] = val ? (cur.Item1 + 1, cur.Item2) : (cur.Item1, cur.Item2 + 1);
                    }
                }
            }
            Console.WriteLine($"=== {scanned} face materials ===");
            Console.WriteLine($"--- STATIC SWITCHES ({sw.Count}) ---");
            foreach (var (k, v) in sw) Console.WriteLine($"  {k,-46} true={v.True,3} false={v.False,3}");
            Console.WriteLine($"--- VECTOR (colour) PARAMS ({vec.Count}) ---");
            foreach (var (k, d) in vec)
                Console.WriteLine($"  {k,-40} n={d.Values.Sum(),3}  " + string.Join("  ", d.OrderByDescending(x => x.Value).Take(3).Select(x => $"#{x.Key}x{x.Value}")));
            Console.WriteLine($"--- SCALAR PARAMS ({sca.Count}) ---");
            foreach (var (k, d) in sca)
                Console.WriteLine($"  {k,-40} n={d.Values.Sum(),3}  " + string.Join("  ", d.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key}x{x.Value}")));
            Console.WriteLine($"--- TEXTURE PARAMS ({tex.Count}) ---");
            foreach (var (k, t) in tex)
                Console.WriteLine($"  {k,-40} set={t.Set,3} real={t.NonDummy,3}  top=" + string.Join(", ", t.Vals.OrderByDescending(x => x.Value).Take(2).Select(x => $"{x.Key}x{x.Value}")));
            return 0;
        }

        // "faceatlas:" maps each face material that explicitly binds a FaceTex sprite atlas to
        // its atlas, expression-grid scalars, sprite index, and relevant static switches. The
        // cooked M_LEGOface graph is stripped, so this is the direct evidence for rebuilding its
        // sprite path rather than guessing from texture names.
        if (objectPath.StartsWith("faceatlas:", StringComparison.OrdinalIgnoreCase))
        {
            var matched = 0;
            foreach (var f in provider.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    || (!f.Contains("MI_FACE", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains("MI_LEGOface", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                CUE4Parse.UE4.Assets.Exports.UObject? mat = null;
                try { mat = provider.LoadPackageObject(f[..f.LastIndexOf('.')]); } catch { }
                if (mat is null) continue;

                static string? PName(CUE4Parse.UE4.Assets.Objects.FStructFallback entry) =>
                    entry.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                         ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text;

                var textures = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues")
                               ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
                var atlas = textures.Where(e => PName(e)?.Contains("FaceTexAtlasTexture", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(e => $"{PName(e)}={e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParameterValue")?.ResolvedObject?.GetPathName() ?? "(none)"}")
                    .ToList();
                if (atlas.Count == 0) continue;

                var scalars = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameterValues")
                              ?? Array.Empty<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
                var spriteValues = scalars.Where(e =>
                        PName(e)?.Contains("Expression", StringComparison.OrdinalIgnoreCase) == true
                        || PName(e)?.Contains("Sprite", StringComparison.OrdinalIgnoreCase) == true
                        || PName(e)?.Contains("MouthHide", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(e => $"{PName(e)}={e.GetOrDefault<float>("ParameterValue"):0.###}");

                var switches = new List<string>();
                var runtime = mat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                foreach (var property in runtime?.Properties ?? new List<CUE4Parse.UE4.Assets.Objects.FPropertyTag>())
                {
                    if (property.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.UScriptArray array) continue;
                    foreach (var item in array.Properties)
                    {
                        var entry = item.GenericValue as CUE4Parse.UE4.Assets.Objects.FStructFallback
                                    ?? (item.GenericValue as CUE4Parse.UE4.Assets.Objects.FScriptStruct)
                                       ?.StructType as CUE4Parse.UE4.Assets.Objects.FStructFallback;
                        var name = entry is null ? null : PName(entry);
                        if (name?.Contains("FaceTex", StringComparison.OrdinalIgnoreCase) == true
                            || name?.Contains("Sprite", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            switches.Add($"{name}={entry!.GetOrDefault<bool>("Value")}");
                        }
                    }
                }

                matched++;
                Console.WriteLine($"[{mat.Name}] {f}");
                Console.WriteLine("  atlas: " + string.Join("; ", atlas));
                Console.WriteLine("  scalars: " + (spriteValues.Any() ? string.Join("; ", spriteValues) : "(none)"));
                Console.WriteLine("  switches: " + (switches.Count > 0 ? string.Join("; ", switches) : "(none)"));
            }
            Console.WriteLine($"face atlas bindings: {matched}");
            return 0;
        }

        // "imports:<path>" lists a package's referenced objects. A cooked anim blueprint loses its
        // graph but KEEPS its dependency list, so an ABP_LEGOface_<Char> still names the exact
        // expression sequences that character actually uses - the per-character ground truth for
        // which face animation set to play.
        if (objectPath.StartsWith("imports:", StringComparison.OrdinalIgnoreCase))
        {
            var loaded = provider.LoadPackage(objectPath["imports:".Length..]);
            Console.WriteLine($"package: {loaded.Name}");
            var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (loaded is CUE4Parse.UE4.Assets.IoPackage io)
            {
                foreach (var n in io.ImportedPackages.Value)
                {
                    if (n is not null) seen.Add(n.Name);
                }
            }
            else if (loaded is CUE4Parse.UE4.Assets.Package legacy)
            {
                foreach (var imp in legacy.ImportMap)
                {
                    var n = imp.ObjectName.Text;
                    if (!string.IsNullOrWhiteSpace(n)) seen.Add(n);
                }
            }
            Console.WriteLine($"referenced packages: {seen.Count}");
            foreach (var n in seen) Console.WriteLine("  " + n);
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

        // "material-shape:<pkgPath>" is a deliberately shallow material-graph inspector. Cooked
        // master materials preserve their expression exports, but calling ToString() on the graph
        // recursively walks enough data to make a probe appear hung. This prints types, property
        // names, and direct object references only, which is enough to reconstruct the inputs and
        // outputs we need to emulate.
        if (objectPath.StartsWith("material-shape:", StringComparison.OrdinalIgnoreCase))
        {
            var package = provider.LoadPackage(objectPath["material-shape:".Length..]);
            var graphExports = package.GetExports().ToList();
            Console.WriteLine($"package: {package.Name}  exports: {graphExports.Count}");
            for (var i = 0; i < graphExports.Count; i++)
            {
                var exp = graphExports[i];
                Console.WriteLine($"[{i}] [{exp.ExportType}] {exp.Name} ({exp.Properties.Count} props)");
                foreach (var prop in exp.Properties)
                {
                    Console.WriteLine($"    {prop.Name.Text}: {DescribeValue(prop.Tag?.GenericValue)}");
                }
            }
            return 0;

            static string DescribeValue(object? value, int depth = 0)
            {
                if (value is null) return "null";
                if (value is CUE4Parse.UE4.Objects.UObject.FPackageIndex index)
                {
                    var resolved = index.ResolvedObject;
                    return resolved is null
                        ? "FPackageIndex (unresolved)"
                        : $"FPackageIndex -> {resolved.GetPathName()}";
                }
                // The useful montage reference is five wrappers down (SlotAnimTracks ->
                // AnimTrack -> AnimSegments -> AnimReference). Keep direct object references
                // visible at any depth, but stop expanding arbitrary data after that point.
                if (depth >= 5) return value.GetType().Name;
                if (value is CUE4Parse.UE4.Assets.Objects.FStructFallback sf)
                {
                    var fields = sf.Properties.Take(12)
                        .Select(p => $"{p.Name.Text}={DescribeValue(p.Tag?.GenericValue, depth + 1)}");
                    var suffix = sf.Properties.Count > 12 ? ", ..." : "";
                    return $"struct {{{string.Join(", ", fields)}{suffix}}}";
                }
                if (value is CUE4Parse.UE4.Assets.Objects.FScriptStruct scriptStruct)
                {
                    // FScriptStruct is only a wrapper; its StructType owns the actual named
                    // fields. Keep the same depth so one wrapper does not hide useful segment
                    // references such as SlotAnimTracks -> AnimSegments -> AnimReference.
                    return $"scriptStruct {DescribeValue(scriptStruct.StructType, depth)}";
                }
                if (value is CUE4Parse.UE4.Assets.Objects.UScriptArray scriptArray)
                {
                    var first = scriptArray.Properties.Take(3)
                        .Select(p => DescribeValue(p.GenericValue, depth + 1));
                    return $"scriptArray[{scriptArray.Properties.Count}] [{string.Join(", ", first)}]";
                }
                if (value is Array array)
                {
                    var first = array.Cast<object?>().Take(3)
                        .Select(v => DescribeValue(v, depth + 1));
                    return $"{value.GetType().GetElementType()?.Name ?? "array"}[{array.Length}] [{string.Join(", ", first)}]";
                }
                var text = value.ToString() ?? value.GetType().Name;
                return text.Length > 100 ? text[..100] + "..." : text;
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
                var sections = lod.Sections?.Value?.Length ?? 0;
                Console.WriteLine($"      -> skeletal: {cs.LODs.Count} LODs, {cs.RefSkeleton.Count} bones, " +
                                  $"LOD0 {lod.NumVerts} verts / {sections} sections");
            }
            else if (exp is UStaticMesh stat && stat.TryConvert(out var cm))
            {
                var lod = cm.LODs[0];
                var sections = lod.Sections?.Value?.Length ?? 0;
                Console.WriteLine($"      -> static: LOD0 {lod.NumVerts} verts / {sections} sections");
            }
        }

        Console.WriteLine(new string('-', 60));
        Console.WriteLine("OK");
        return 0;
    }
}
