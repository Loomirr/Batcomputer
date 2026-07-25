using System.Numerics;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Animations;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace Batcomputer;

/// <summary>
/// Turns a cooked mesh in the game paks into a self-contained folder the WebView2 preview can serve:
/// the mesh as glTF plus the vendored three.js viewer. Read-only - it only reads the paks and writes
/// scratch files; it never touches the suit project.
/// </summary>
public static class ModelPreviewService
{
    /// <summary>The zero key mounts LotDK's unencrypted paks (confirmed in the Phase 0 probe).</summary>
    private const string ZeroAes = "0x0000000000000000000000000000000000000000000000000000000000000000";

    private static DefaultFileProvider MakeProvider(string paksDir, string usmapPath)
    {
        // Case-insensitive lookup: asset import paths carry mixed casing (a component points at
        // ".../Hat/..." while the file lives under ".../HAT/..."), so a case-sensitive match drops
        // parts like the cowl.
        var provider = new DefaultFileProvider(
            paksDir, SearchOption.AllDirectories, isCaseInsensitive: true,
            new VersionContainer(EGame.GAME_UE5_6));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(ZeroAes));
        return provider;
    }

    /// <summary>
    /// Preview scratch lives in <c>Generated\Preview</c> beside the exe - not the system temp folder -
    /// so the tool stays portable and everything it writes is in one place the user controls.
    /// Each build gets its own folder and older ones are deleted, so this never accumulates.
    /// </summary>
    private static string NewPreviewRoot()
    {
        var root = Path.Combine(
            AppSettings.GeneratedRootFor(AppSettings.Current.EffectiveProjectRoot()), "Preview");
        Directory.CreateDirectory(root);
        CleanPreviewRoot(root);
        return root;
    }

    /// <summary>Removes previous preview builds. Best effort - a locked folder is skipped.</summary>
    private static void CleanPreviewRoot(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* still open in a viewer window - it'll be swept next time */ }
        }
    }

    /// <summary>Convenience for a single mesh.</summary>
    public static string BuildPreview(string paksDir, string usmapPath, string objectPath)
        => BuildPreview(paksDir, usmapPath, new[] { objectPath });

    /// <summary>
    /// Assembles the character from a playable BP: reads its skeletal-mesh components, collects the
    /// mesh each points to, and previews them together. The body (CharacterMesh0) is assigned at
    /// runtime, so it is passed in separately when known.
    /// </summary>
    /// <summary>The shared minifig body used by CharacterMesh0 (assigned at runtime, so not on the BP).</summary>
    private const string DefaultBodyMesh = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig_Body.SK_LEGOfig_Minifig_Body";

    /// <summary>
    /// The bare head piece. Also runtime-assigned, and separate from both the face print
    /// (SK_LEGOface) and the cowl (the BP's "Head" component), which layer on top of it.
    /// </summary>
    private const string DefaultHeadMesh = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig_Head.SK_LEGOfig_Minifig_Head";

    /// <summary>
    /// A mesh to preview. <paramref name="AttachToHead"/> marks the local-authored head attachments
    /// (face, cowl) that must be aligned onto the head piece; everything else is world-authored and
    /// renders where it already sits.
    /// </summary>
    private readonly record struct PreviewPart(
        string MeshPath, bool AttachToHead, bool IsHeadPiece = false, FPackageIndex[]? Overrides = null,
        bool IsStaticAttachment = false);

    /// <summary>
    /// World position of a bone in the UE reference skeleton, converted to the exported glTF's space.
    ///
    /// Why the UE skeleton and not the glTF nodes: at bind pose the skinning cancels
    /// (bone.matrixWorld * inverseBind = identity), so CUE4Parse's exported bone nodes are NOT
    /// spatially aligned with the mesh - they put this body's head at z=-1.168 while the geometry
    /// stands 0..1.388 along +Y. The reference skeleton is the real, consistent source: a clean Z-up
    /// spine. The exporter writes geometry Y-up in metres, so the conversion is Z-up -> Y-up / 100.
    /// </summary>
    private static Vector3? BoneWorldInGltfSpace(USkeletalMesh mesh, string boneName)
    {
        if (!mesh.TryConvert(out var converted))
        {
            return null;
        }
        var bones = converted.RefSkeleton;
        var world = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            var local = Matrix4x4.CreateFromQuaternion(
                            new Quaternion(b.Orientation.X, b.Orientation.Y, b.Orientation.Z, b.Orientation.W))
                        * Matrix4x4.CreateTranslation(b.Position.X, b.Position.Y, b.Position.Z);
            world[i] = b.ParentIndex >= 0 ? local * world[b.ParentIndex] : local;

            if (string.Equals(b.Name.Text, boneName, StringComparison.OrdinalIgnoreCase))
            {
                var t = world[i].Translation;
                // UE (X fwd, Y right, Z up) cm -> glTF (X, Y up, Z) metres.
                return new Vector3(t.X / 100f, t.Z / 100f, -t.Y / 100f);
            }
        }
        return null;
    }

    /// <summary>
    /// Component name -> body bone the attachment hangs off. Body-skinned parts (body, cape) carry the
    /// full skeleton and need no attach bone. Extend this as more attachment slots are supported.
    /// </summary>
    private static string? AttachBoneFor(string componentName) => componentName switch
    {
        var n when n.StartsWith("Face", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
        var n when n.StartsWith("Head", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
        _ => null,
    };

    public static string BuildPreviewCharacter(string paksDir, string usmapPath, string bpPath, string? bodyMeshPath = null)
    {
        var provider = MakeProvider(paksDir, usmapPath);
        var bodyPath = string.IsNullOrWhiteSpace(bodyMeshPath) ? DefaultBodyMesh : bodyMeshPath!;
        var parts = new List<PreviewPart> { new(bodyPath, AttachToHead: false) };

        // Resolve attach points once, off the body's reference skeleton.
        var attachPoints = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        if (provider.LoadPackageObject(bodyPath) is USkeletalMesh bodyMesh)
        {
            foreach (var boneName in new[] { "Head_Attach_01" })
            {
                var p = BoneWorldInGltfSpace(bodyMesh, boneName);
                if (p is not null)
                {
                    attachPoints[boneName] = p.Value;
                    _headAttachPoint = p.Value;
                    Console.WriteLine($"  attach '{boneName}' -> ({p.Value.X:0.###}, {p.Value.Y:0.###}, {p.Value.Z:0.###})");
                }
                else
                {
                    Console.WriteLine($"  ! attach bone '{boneName}' not in body skeleton");
                }
            }
        }

        // The head piece is authored in world space like the body/cape - it already sits on the neck.
        // Its material slot is genuinely empty in the asset (the game binds one at runtime), so there
        // is nothing to read; it takes the face material's skin tint instead.
        parts.Add(new PreviewPart(DefaultHeadMesh, AttachToHead: false, IsHeadPiece: true));

        _bpCharacter = System.Text.RegularExpressions.Regex
            .Match(Path.GetFileNameWithoutExtension(bpPath), @"^BP_([A-Za-z0-9]+)")
            .Groups[1].Value is { Length: > 0 } bpName ? bpName : null;

        var bp = provider.LoadPackage(bpPath);
        foreach (var exp in bp.GetExports())
        {
            var isSkeletal = exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase);
            var isStatic = exp.ExportType.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
            if (!isSkeletal && !isStatic)
            {
                continue;
            }
            // Hair and other rigid head pieces are STATIC meshes (SM_HAIR_*), not skeletal ones -
            // skipping them is why every character without a cowl came out bald.
            var meshRef = exp.GetOrDefault<FPackageIndex>("SkeletalMeshAsset")
                          ?? exp.GetOrDefault<FPackageIndex>("SkeletalMesh")
                          ?? exp.GetOrDefault<FPackageIndex>("StaticMesh");
            var path = meshRef?.ResolvedObject?.GetPathName();
            if (string.IsNullOrWhiteSpace(path))
            {
                // CharacterMesh0 carries no mesh (assigned at runtime) but DOES carry the body's real
                // material, e.g. MI_Batman_89_EOM. Hand it to the body mesh we substituted in.
                if (exp.Name.Contains("CharacterMesh", StringComparison.OrdinalIgnoreCase) &&
                    exp.GetOrDefault<FPackageIndex[]>("OverrideMaterials") is { Length: > 0 } bodyMats)
                {
                    parts[0] = parts[0] with { Overrides = bodyMats };
                    Console.WriteLine($"  body material from {exp.Name}: {bodyMats[0]?.ResolvedObject?.GetPathName()}");
                }
                continue;
            }
            // The glide cape (Torso slot) is only shown while gliding - skip it for the standing look.
            if (path!.Contains("Glide", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // The character's real look lives in the component's override materials (e.g.
            // MI_Batman_89_EOM), not in the base mesh's own material slots.
            var overrides = exp.GetOrDefault<FPackageIndex[]>("OverrideMaterials");
            parts.Add(new PreviewPart(path, AttachToHead: AttachBoneFor(exp.Name) is not null,
                Overrides: overrides, IsStaticAttachment: isStatic));
        }

        return BuildPreviewCore(provider, parts);
    }

    /// <summary>Exports each mesh to glTF and writes the viewer that loads them into one scene.</summary>
    public static string BuildPreview(string paksDir, string usmapPath, IReadOnlyList<string> objectPaths)
        => BuildPreviewCore(MakeProvider(paksDir, usmapPath),
            objectPaths.Select(p => new PreviewPart(p, AttachToHead: false)).ToList());

    private static string BuildPreviewCore(DefaultFileProvider provider, IReadOnlyList<PreviewPart> parts)
    {
        var options = new ExporterOptions
        {
            MeshFormat = EMeshFormat.Gltf2,
            LodFormat = ELodFormat.FirstLod,
            ExportMorphTargets = false,
        };

        var previewDir = Path.Combine(NewPreviewRoot(), Guid.NewGuid().ToString("N"));
        var exportDir = Path.Combine(previewDir, "export");
        Directory.CreateDirectory(exportDir);

        // Geometry-only fallback: some material textures use BC7/BC6H, which need the native Detex
        // decoder. Without it, we still show the mesh shape rather than dropping the whole part.
        var geomOptions = new ExporterOptions
        {
            MeshFormat = EMeshFormat.Gltf2,
            LodFormat = ELodFormat.FirstLod,
            ExportMorphTargets = false,
            ExportMaterials = false,
        };

        var models = new List<(string File, PreviewPart Part, List<SlotShading> Slots)>();
        List<SlotShading>? bodyShading = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            UObject mesh;
            try { mesh = provider.LoadPackageObject(part.MeshPath); }
            catch (Exception ex) { Console.WriteLine($"skip '{part.MeshPath}': {ex.Message}"); continue; }

            ApplyOverrideMaterials(mesh, part);
            ReportSlots(mesh, part);

            // Skin source: the component's override material if it has one, else the mesh's own.
            var matObj = (part.Overrides is { Length: > 0 } ovr
                ? ovr[0]?.ResolvedObject?.Load()
                : (mesh as USkeletalMesh)?.Materials?.FirstOrDefault()?.Load())
                as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial;
            // The head has no bound material; fall back to the LEGO skin tone so it is not untextured.
            var headSkin = part.IsHeadPiece ? SkinTone : (Color?)null;

            // Resolve EVERY slot separately: a mesh's sections have different materials (the cowl's
            // shell vs its eyes, the cape's LOD variants), so one texture must not be sprayed across all.
            var slotShading = new List<SlotShading>();
            var slotMats = (mesh as USkeletalMesh)?.Materials;
            var disabledSlots = DisabledSectionSlots(mesh);
            for (var si = 0; si < (slotMats?.Length ?? 0); si++)
            {
                var slotOverride = part.Overrides is not null && si < part.Overrides.Length
                    ? part.Overrides[si]?.ResolvedObject?.Load() : null;
                var slotMat = slotOverride ?? slotMats![si]?.Load();
                if (si == 0 && part.MeshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase))
                {
                    _faceMaterial = slotMat;
                }
                var resolved = ResolveSlot(provider, slotMat, previewDir);
                if (disabledSlots.Contains(si))
                {
                    resolved = resolved with { Hidden = true };
                    Console.WriteLine($"      [{si}] section disabled in cooked mesh -> hidden");
                }
                if (headSkin is not null && resolved.Texture is null && resolved.Colour is null)
                {
                    // The head is part of the LEGOfig body system: its FACE PRINT (frown, brows, chin
                    // shading) lives in the character's TPAGE atlas, sampled through the same uv2
                    // channel as the body. The separate SK_LEGOface shell is the cutscene expression
                    // layer on top. So the head wears the BODY's shading, not a flat skin tone.
                    if (bodyShading is { Count: > 0 })
                    {
                        resolved = bodyShading[0];
                        Console.WriteLine("      head: no material bound -> body TPAGE shading (face print lives in the atlas)");
                    }
                    else
                    {
                        resolved = resolved with { Colour = headSkin };
                        Console.WriteLine($"      head: no material bound -> skin tone #{headSkin.Value.R:X2}{headSkin.Value.G:X2}{headSkin.Value.B:X2}");
                    }
                }
                slotShading.Add(resolved);
            }

            // Hair and other static parts report no material slots of their own - their look lives
            // entirely in the component's override. Without this they render untextured.
            if (slotShading.Count == 0 && part.Overrides is { Length: > 0 } soloOverride)
            {
                var solo = ResolveSlot(provider, soloOverride[0]?.ResolvedObject?.Load(), previewDir);
                slotShading.Add(solo);
                Console.WriteLine($"      no mesh slots -> override material "
                                  + soloOverride[0]?.ResolvedObject?.GetPathName()?.Split('.')[^1]);
            }

            if (i == 0 && slotShading.Count > 0)
            {
                bodyShading = slotShading;
            }

            bool Build(ExporterOptions opt)
            {
                try
                {
                    MeshExporter ex = mesh switch
                    {
                        USkeletalMesh skm => new MeshExporter(skm, opt),
                        UStaticMesh stm => new MeshExporter(stm, opt),
                        _ => throw new InvalidOperationException($"{mesh.GetType().Name} is not a mesh"),
                    };
                    if (ex.TryWriteToDir(new DirectoryInfo(exportDir), out _, out var saved))
                    {
                        var name = $"model{i}.glb";
                        var destGlb = Path.Combine(previewDir, name);
                        File.Copy(saved, destGlb, overwrite: true);
                        // Promote the decal UV set into slot 0 - three.js only reads TEXCOORD_0/1.
                        // (CTUV bake lands in UV0, so no channel promotion is needed.)
                        models.Add((name, part, slotShading));
                        return true;
                    }
                }
                catch (Exception e) { Console.WriteLine($"  export attempt failed '{part.MeshPath}': {e.Message}"); }
                return false;
            }

            // Full materials first; if texture decode blows up, retry the same mesh geometry-only.
            if (!Build(options))
            {
                Console.WriteLine($"  '{part.MeshPath}' -> geometry only (textures need native decoder)");
                Build(geomOptions);
            }
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("No meshes could be exported for preview.");
        }

        WriteViewerAssets(previewDir, PrepareFaceFeatures(provider, previewDir, AlignToHead(previewDir, models)));
        return previewDir;
    }

    /// <summary>
    /// Points the mesh's material slots at the component's override materials before export, so the
    /// preview shows the character's actual look rather than the donor mesh's stock materials.
    /// </summary>
    private static void ApplyOverrideMaterials(UObject mesh, PreviewPart part)
    {
        if (part.Overrides is not { Length: > 0 } overrides || mesh is not USkeletalMesh skm)
        {
            return;
        }
        var slots = skm.Materials;
        if (slots is null || slots.Length == 0)
        {
            return;
        }

        // Materials is a ResolvedObject[] - each entry IS the material, so swap elements in place.
        var applied = 0;
        for (var i = 0; i < slots.Length && i < overrides.Length; i++)
        {
            var ov = overrides[i]?.ResolvedObject;
            if (ov is null)
            {
                continue;
            }
            slots[i] = ov;
            applied++;
        }
        if (applied > 0)
        {
            Console.WriteLine($"    materials: {applied} override(s) applied to {Path.GetFileName(part.MeshPath)}");
        }
    }

    /// <summary>
    /// UV set the printed decals are laid out against. The meshes carry up to 8; TEXCOORD_2's range
    /// matches the atlas layout, while 0 and 1 do not.
    /// </summary>
    private const int DecalUvChannel = 2;

    /// <summary>A model file, where to place it, and the base-colour texture to skin it with.</summary>
    private readonly record struct PlacedModel(
        string File, Vector3 Offset, List<SlotShading> Slots, bool IsBody, bool IsFace = false, bool IsHead = false)
    {
        /// <summary>Triangle counts of the reordered face index buffer: [base, mouth, hidden].</summary>
        public int[]? FaceGroups { get; init; }
        /// <summary>Preview-relative path of the mouth feature print (alpha = cutout).</summary>
        public string? MouthTex { get; init; }
        /// <summary>Alternate expression feature bands: (ExtraUV0 slot id, triangle count).</summary>
        public List<(int Band, int Tris, string? Tex, Color? Tint)>? Bands { get; init; }
        /// <summary>True when the character binds a dummy to the mouth feature (draw nothing).</summary>
        public bool MouthHidden { get; init; }
        /// <summary>Facial rig pose (bone name -> local transform, glTF space) from the expression anim.</summary>
        public Dictionary<string, Dictionary<int, Dictionary<string, (Vector3 P, System.Numerics.Quaternion Q, Vector3 S)>>>? Poses { get; init; }
    }

    /// <summary>
    /// Loads a LEGOface expression animation and returns its first-frame pose per bone, already
    /// converted to glTF space.
    ///
    /// SK_LEGOface has no morph targets: the game poses a facial BONE RIG (Brows_*, Eye_*, Eyelid_*,
    /// Mouth_*, Lips_*, Cheek_*) through its PostProcessAnimBlueprint, from per-character sequences
    /// at /Game/Animation/LEGOface/LEGOface_&lt;Character&gt;/A_&lt;Expression&gt;_&lt;Character&gt;_LEGOface. Without
    /// this the preview shows the mesh in BIND pose, which the game never displays.
    ///
    /// Axis mapping measured by comparing the exported glTF bind nodes against the UE reference
    /// skeleton (verified on Eye_L, Mouth_L, Lips_UL_3, Brows_M): position (X, Z, Y)/100,
    /// quaternion (x, z, y, w) - the axis swap alone, W is NOT negated - and scale (X, Z, Y).
    /// </summary>
    private static Dictionary<int, Dictionary<string, (Vector3 P, System.Numerics.Quaternion Q, Vector3 S)>>? LoadFacePose(
        DefaultFileProvider provider, string expression, string? character)
    {
        // Expression sets, best first. A character's own folder wins; otherwise use the SHARED
        // sets the game ships for this rig: LEGOface_Superhero (Batman's face material is
        // MI_LEGOface-Defaults_Superhero) then the generic LEGOface_Expressions. Both pose the rig
        // at root scale 1.0 - unlike a per-character donor such as Bane, whose sequences scale
        // AttachRoot by 1.485 to fit his larger head.
        // Try the CHARACTER'S OWN animation folders first - both the name from the face material
        // (FACE_BruceAdult) and the one from the blueprint (BP_BruceWayne_... -> BruceWayne), since
        // the two differ and the animation folders are keyed on the blueprint's name. Only then
        // fall back to the shared sets, which pose the same rig at root scale 1.0.
        var candidates = new List<string>();
        foreach (var who in new[] { character, _bpCharacter }.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct())
        {
            candidates.Add($"/Game/Animation/LEGOface/LEGOface_{who}/A_{expression}_{who}_LEGOface");
            candidates.Add($"/Game/Animation/LEGOface/LEGOface_{who}/A_{expression}_{who}_LEGOFace");
            candidates.Add($"/Game/Animation/LEGOfig/{who}/Movement/A_{expression}_{who}_LEGOface");
            candidates.Add($"/Game/Animation/LEGOfig/{who}/Movement/A_{expression}_{who}_LEGOFace");
            // Some characters keep their face animation one level deeper, under Attachments/
            // (Batman: Movement/A_Idle_Batman_LEGOface, Bruce: Movement/Attachments/A_Idle_BruceWayne_LEGOface).
            candidates.Add($"/Game/Animation/LEGOfig/{who}/Movement/Attachments/A_{expression}_{who}_LEGOface");
            candidates.Add($"/Game/Animation/LEGOfig/{who}/Movement/Attachments/A_{expression}_{who}_LEGOFace");
        }
        candidates.Add($"/Game/Animation/LEGOface/LEGOface_Superhero/A_{expression}_LEGOFace_Superhero");
        candidates.Add($"/Game/Animation/LEGOface/LEGOface_Expressions/A_{expression}_LEGOFace");

        foreach (var path in candidates)
        {
            try
            {
                if (provider.LoadPackageObject(path) is not UAnimSequence anim)
                {
                    continue;
                }
                var skel = anim.Skeleton?.Load<USkeleton>();
                if (skel is null)
                {
                    continue;
                }
                var set = skel.ConvertAnims(anim);
                var seq = set.Sequences.FirstOrDefault();
                if (seq is null)
                {
                    continue;
                }
                var refBones = skel.ReferenceSkeleton.FinalRefBoneInfo;

                // Sample a fixed frame partway in. These sequences ease into the expression, hold
                // it, then relax, so neither frame 0 nor the last key is the pose - and a
                // "most deviation" heuristic picks whatever extreme the ease-out passes through.
                var frameCount = seq.Tracks.Max(t2 => Math.Max(t2.KeyQuat.Length, t2.KeyPos.Length));
                // Sample several points through the clip so the viewer can scrub: the pose is held
                // somewhere in the middle and neither end is it.
                var samples = FacePoseSamples
                    .Select(f => Math.Clamp((int)(frameCount * f), 0, Math.Max(0, frameCount - 1)))
                    .Distinct().ToList();
                var byFrame = new Dictionary<int, Dictionary<string, (Vector3, System.Numerics.Quaternion, Vector3)>>();
                foreach (var bestFrame in samples)
                {

                var pose = new Dictionary<string, (Vector3, System.Numerics.Quaternion, Vector3)>();
                for (var i = 0; i < seq.Tracks.Count && i < refBones.Length; i++)
                {
                    var tr = seq.Tracks[i];
                    if (tr.KeyPos.Length == 0 && tr.KeyQuat.Length == 0)
                    {
                        continue;
                    }
                    // The ROOT track is locked by the game (every sequence sets bForceRootLock).
                    // It is not a pose - it is the donor character's fit: Bane's expressions scale
                    // AttachRoot by 1.485 for his larger head. Applying it inflates the whole face
                    // rig and lifts it off the skull.
                    if (refBones[i].ParentIndex < 0)
                    {
                        continue;
                    }
                    // Sample the LAST key: these sequences ease from a neutral start into the held
                    // expression, so frame 0 is the transition, not the pose the game rests on.
                    var p = tr.KeyPos.Length > 0 ? tr.KeyPos[Math.Min(bestFrame, tr.KeyPos.Length - 1)] : default;
                    var q = tr.KeyQuat.Length > 0 ? tr.KeyQuat[Math.Min(bestFrame, tr.KeyQuat.Length - 1)] : default;
                    // Scale is NOT decorative here: the rig scales feature shells (mouth 1.4x/1.3x,
                    // unused features toward zero) to form the expression. Dropping it leaves every
                    // shell at bind size, which renders as slabs across the face.
                    var s = tr.KeyScale.Length > 0 ? tr.KeyScale[Math.Min(bestFrame, tr.KeyScale.Length - 1)] : new FVector(1, 1, 1);
                    pose[refBones[i].Name.Text] = (
                        new Vector3(p.X / 100f, p.Z / 100f, p.Y / 100f),
                        new System.Numerics.Quaternion(q.X, q.Z, q.Y, q.W),
                        new Vector3(s.X, s.Z, s.Y));
                }
                byFrame[bestFrame] = pose;
                }
                Console.WriteLine($"  face pose '{expression}': {byFrame.Count} frames {string.Join("/", byFrame.Keys)} of {frameCount} from {path.Split('/')[^1]}");
                return byFrame;
            }
            catch
            {
                // Try the next candidate path.
            }
        }
        return null;
    }

    /// <summary>
    /// The default mouth print sampled by M_LEGOface when "Mouth BC" is not overridden (the cooked
    /// master's defaults are stripped, but the community recreation confirms this texture - the stern
    /// Batman mouth shared by every cowled Batman face).
    /// </summary>
    /// <summary>
    /// Pulls the character name out of a face material path, e.g.
    /// /Game/Characters/Attachments/Face/FACE_Batman/MI_FACE_Batman_NoEyes -> "Batman".
    /// </summary>
    private static string? CharacterFromFaceMaterial(UObject? faceMaterial)
    {
        var path = faceMaterial?.GetPathName();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var m = System.Text.RegularExpressions.Regex.Match(path, @"/FACE_([A-Za-z0-9]+)/");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Which material parameter drives each ExtraUV0 band of the LEGO face mesh.
    ///
    /// The face is layered: "HeadLowerUnder" is the base skin shell and "HeadLowerOver" is a second
    /// full shell on top (band 7) carrying the upper-face print. Batman binds a dummy to Over, so
    /// hiding it was invisible on him - on an ordinary face it drops the entire top half. The
    /// remaining bands are the paired features, resolved left/right by the sign of z.
    /// </summary>
    /// <summary>
    /// Which face feature each ExtraUV0 band draws: the texture parameter and its tint parameter.
    ///
    /// Every LEGO face feature is a WHITE STENCIL (shape in alpha, RGB pure white) coloured by a
    /// "&lt;feature&gt; Tint" vector parameter - brows are white sheets tinted 442E2B, skin is tinted
    /// D28856. The head is split into LOWER and UPPER halves, each with an Under (skin) and Over
    /// (print) layer; missing HeadUpperUnder is what left ordinary faces with no top half.
    /// Bands were identified by cycling each one alone in the viewer (press B).
    /// </summary>
    private static (string Tex, string? Tint)[] ParamsForBand(int band, float centreZ) => band switch
    {
        8 => new[] { ("HeadLowerUnder BC", (string?)"HeadLowerUnder Tint") },
        7 => new[] { ("HeadLowerOver BC", (string?)null) },
        0 => new[] { ("HeadUpperUnder BC", (string?)"HeadUpperUnder Tint") },
        3 => new[] { ("HeadUpperOver BC", (string?)null) },
        13 => new[] { ("Mouth BC", (string?)null) },
        1 or 2 => centreZ < 0
            ? new[] { ("BrowL BC", (string?)"BrowL Tint") }
            : new[] { ("BrowR BC", (string?)"BrowR Tint") },
        // Bands 11/12 are the LASH quads. Deliberately NOT drawn: the game ships exactly one lash
        // texture (T_LEGOface_Lash_Generic_Female_A_BC - there is no male variant) and binds it on
        // male faces too, with no matching "Lash Tint", so it rendered as a white swoosh above the
        // eye. A bound parameter is not evidence the game draws the layer.
        // Remaining bands are alternate variants / unused feature quads: left undrawn until a
        // character is found that binds them, rather than guessed at.
        _ => Array.Empty<(string, string?)>(),
    };

    /// <summary>Face material of the current build, so feature bands can read their own params.</summary>
    private static UObject? _faceMaterial;

    /// <summary>
    /// World position (glTF space) of the head attach bone. Cooked meshes carry NO sockets - the
    /// Sockets array is empty on the body, head, face and full-figure meshes - so the component's
    /// AttachToName ("HeadStud_Attach_Socket") cannot be resolved from the assets. The head attach
    /// bone is the closest real anchor the data does give us.
    /// </summary>
    private static Vector3? _headAttachPoint;

    /// <summary>
    /// Character name taken from the blueprint being previewed (BP_BruceWayne_Ninja_Playable ->
    /// "BruceWayne"). The face MATERIAL often uses a different name (FACE_BruceAdult), and the
    /// animation folders are keyed on the blueprint's name, so both are tried.
    /// </summary>
    private static string? _bpCharacter;

    /// <summary>
    /// True for the placeholder textures the game binds to features a character does not use
    /// (T_Dummy_Alpha_Off / T_Dummy_NML). They are fully transparent, so the feature draws nothing.
    /// </summary>
    private static bool IsDummyTexture(UTexture2D t) =>
        t.Name.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("Alpha_Off", StringComparison.OrdinalIgnoreCase);

    private const string DefaultMouthTexPath = "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Mouth_BC";

    /// <summary>
    /// Expression applied to the face rig, and the character whose set to take it from.
    ///
    /// OFF by default: the ACL pose decodes correctly (59 bones, real position/rotation/scale), but
    /// mapping it onto the exported glTF rig still renders wrong - the rotation handedness and the
    /// scale axis order are inferred, not measured, so shells come out deformed. The unposed face
    /// (band-classified geometry + game materials) is the good state; this stays opt-in until the
    /// conversion is verified against the reference skeleton's own bone orientations.
    /// </summary>
    public static bool ApplyFacePose { get; set; }
    /// <summary>Every expression name the game ships for the LEGOface rig.</summary>
    public static readonly string[] FaceExpressions =
    {
        "Neutral", "Smiling", "Smirking", "Grinning", "Laughing", "Frowning", "Sullen", "Enraged",
        "Grimacing", "Screaming", "Crying", "Dazed", "Sensing", "Yearning", "Open", "Closed",
        // Character-specific gameplay poses - these resolve only for characters that ship them
        // (Batman has Idle/Jump/Land in his own LEGOface folder).
        "Idle", "Jump", "Land",
    };

    /// <summary>Points through each expression clip to sample, as fractions of its length.</summary>
    public static readonly double[] FacePoseSamples = { 0.15, 0.3, 0.45, 0.6, 0.75, 0.9 };

    public static string FaceExpression { get; set; } = "Neutral";
    /// <summary>Override for whose expression set to use; null = detect from the face material.</summary>
    public static string? FaceCharacter { get; set; }

    /// <summary>
    /// Face post-pass: split the merged face mesh into base/mouth/hidden groups (see
    /// GlbInspector.TryGroupFaceFeatures) and export the mouth print. The mouth is a separate shell
    /// ~0.06 above the under-layer's lip decal - rendering the lip decal alone is what made the
    /// "mouth" look tiny, low and skin-coloured.
    /// </summary>
    private static List<PlacedModel> PrepareFaceFeatures(
        DefaultFileProvider provider, string previewDir, List<PlacedModel> placed)
    {
        for (var i = 0; i < placed.Count; i++)
        {
            if (!placed[i].IsFace)
            {
                continue;
            }
            var groups = GlbInspector.TryGroupFaceFeatures(Path.Combine(previewDir, placed[i].File));
            if (groups is null || groups[1] == 0)
            {
                Console.WriteLine("  face: feature split unavailable");
                continue;
            }

            // Each feature band takes its texture from the face material's OWN parameter for that
            // feature ("Mouth BC" for the mouth shells). When the character binds a dummy there -
            // cowled Batman faces set nearly every feature to T_Dummy_Alpha_Off, a fully transparent
            // texture - the game draws nothing, so the band must be hidden rather than shaded.
            string? mouthRel = null;
            var mouthHidden = false;
            var faceMaterial = _faceMaterial;
            var mouthTex = FindTextureParam(faceMaterial, "Mouth BC", 0)
                           ?? FindTextureParam(faceMaterial, "Mouth BC Prestine", 0);
            if (mouthTex is not null && IsDummyTexture(mouthTex))
            {
                // A dummy here means the character overrides nothing, not that the mouth is absent -
                // the shared sheet is the base the rig deforms. Fall through to it.
                mouthTex = null;
            }
            try
            {
                mouthTex ??= mouthHidden ? null : provider.LoadPackageObject(DefaultMouthTexPath) as UTexture2D;
                if (mouthTex is not null)
                {
                    mouthRel = "textures/" + MakeSafeName(mouthTex.Name) + "_mouth.png";
                    var dest = Path.Combine(previewDir, mouthRel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(dest) && !TextureDecodeService.TryExportMouthSheet(mouthTex, dest))
                    {
                        mouthRel = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  face mouth texture failed: {ex.Message.Split('\n')[0]}");
            }

            // Give every band the texture its own material parameter names. A band whose feature the
            // character does not use resolves to a dummy (fully transparent) and is left untextured,
            // so cowled faces stay clean while ordinary faces get their brows, eyes and upper layer.
            var bands = new List<(int Band, int Tris, string? Tex, Color? Tint)>();
            foreach (var (band, tris) in GlbInspector.FaceBandLayout)
            {
                string? rel = null;
                Color? tint = null;
                var z = GlbInspector.FaceBandCentres.TryGetValue(band, out var c) ? c : 0f;
                foreach (var (texParam, tintParam) in ParamsForBand(band, z))
                {
                    // "<feature> BC" is the DISTRESSED variant - a wear mask that measures 98-99%
                    // transparent, so drawing it renders nothing (this is what lost the eyes and the
                    // upper-face print). The artwork is in the pristine sibling, which the game
                    // spells "Prestine". Prefer it, fall back to the distressed one.
                    var t = FindTextureParam(faceMaterial, texParam + " Prestine", 0)
                            ?? FindTextureParam(faceMaterial, texParam, 0);
                    if (t is null || IsDummyTexture(t))
                    {
                        continue;
                    }
                    // Keep the alpha: it carries the feature's SHAPE. A distinct suffix so this is
                    // never confused with an alpha-forced-opaque export of the same texture.
                    var name = "textures/" + MakeSafeName(t.Name) + "_stencil.png";
                    var dest = Path.Combine(previewDir, name.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(dest) || TextureDecodeService.TryExportPng(t, dest, keepAlpha: true))
                    {
                        rel = name;
                        tint = tintParam is null ? null : FindColourParam(faceMaterial, tintParam, 0);
                        break;
                    }
                }
                bands.Add((band, tris, rel, tint));
            }
            Console.WriteLine("  face bands (* = textured): " + string.Join(", ",
                bands.Select(b => $"{b.Band}:{b.Tris}{(b.Tex is null ? "" : "*")}{(b.Tint is null ? "" : "t")}")));
            // Load every expression the game ships for this rig so the viewer can switch between
            // them. Batman's own folder only has Neutral; the rest of the set lives under other
            // characters, and they all pose the same shared SKEL_LEGOface rig.
            // Whose expression set to use is read from the face material itself
            // (…/Face/FACE_<Character>/MI_FACE_<Character>_…), so any character works without
            // being named here. Falls back to the shared sets when they ship no own animations.
            var character = FaceCharacter ?? CharacterFromFaceMaterial(_faceMaterial);
            if (character is not null)
            {
                Console.WriteLine($"  face character: {character}");
            }
            var poses = new Dictionary<string, Dictionary<int, Dictionary<string, (Vector3, System.Numerics.Quaternion, Vector3)>>>();
            foreach (var expr in FaceExpressions)
            {
                var one = LoadFacePose(provider, expr, character);
                if (one is not null)
                {
                    poses[expr] = one;
                }
            }
            Console.WriteLine($"  face expressions available: {string.Join(", ", poses.Keys)}");
            placed[i] = placed[i] with { FaceGroups = groups, MouthTex = mouthRel, Bands = bands, Poses = poses, MouthHidden = mouthHidden };
        }
        return placed;
    }

    /// <summary>
    /// Exports a material and returns the preview-relative path of the texture to use as base colour.
    ///
    /// CUE4Parse does NOT embed textures in the .glb - it writes loose .png files plus a .json listing
    /// the material's texture slots. So we export the material, read that json, and pick the slot that
    /// carries the visible colour: "CT" (the printed colour/detail map) if present, else "BC" (the flat
    /// LEGO colour swatch, e.g. T_LEGO_Black17).
    /// </summary>
    /// <summary>
    /// The slots that actually carry visible colour, best first.
    ///
    /// "BC_Pristine" comes FIRST: on characters that have both, plain "BC" is the *distressed*
    /// variant (T_TPAGE_Batman_89_DIST_BC) - a damage overlay meant to be blended over the base, which
    /// measures as near-fully-transparent (avg alpha 18) and almost black. The pristine texture
    /// (T_Batman_89_BC) is the actual base colour. Where there is no pristine, "BC" is the base and may
    /// be either a full texture or a flat LEGO colour swatch (the cowl's T_LEGO_Black17).
    ///
    /// "CT" is deliberately NOT here: it is inherited from the shared MI_Minifig_EoM_Controller parent
    /// as T_LEGOFIG_CTUV, a UV/control map - painting it on as albedo is what turned the cowl green.
    /// </summary>
    private static readonly string[] BaseColourSlots =
        { "BC_Pristine", "BC", "HeadLowerUnder BC", "Diffuse", "BaseColor" };

    private static string? ExportMaterialTexture(
        CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial? material, string previewDir, string exportDir)
    {
        var tex = FindBaseColourTexture(material, 0);
        if (tex is null)
        {
            return null;
        }

        // A tiny texture (e.g. the cowl's 8x8 T_LEGO_Black17) IS the flat plastic colour, not a print.
        // A large sheet is the decal layer and needs a plastic colour underneath it.
        var decoded = TextureDecodeService.TryDecode(tex);
        var isSwatch = decoded is not null && decoded.Width <= 16 && decoded.Height <= 16;
        var plastic = isSwatch ? AverageColour(decoded!) : DefaultPlastic;

        // BC is a complete albedo once alpha is ignored: the "transparent" area still carries the
        // plastic colour in RGB. LEGO packs masks into alpha, so honouring it is what blacked the
        // model out. TryExportPng forces alpha opaque.
        var rel = "textures/" + MakeSafe(tex.Name) + ".png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));

        // BC decoded with alpha forced opaque is a complete albedo laid out in the character's atlas.
        // The shared LEGOfig body maps into that atlas through one of its extra UV channels (not UV0),
        // so no shader/CTUV emulation is needed - the mesh already carries the right coordinates. Which
        // channel is the open question the viewer's UV switcher answers.
        var ok = File.Exists(dest) || TextureDecodeService.TryExportPng(tex, dest);
        if (ok)
        {
            Console.WriteLine($"    base colour: {tex.Name} ({tex.Format})");
            return rel;
        }
        return null;

        static string MakeSafe(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    /// <summary>
    /// Plastic colour used when the material carries no flat swatch. The real value comes from the
    /// ColourMask's channel slots (red = suit, green = logo, blue = belt) resolved against the
    /// character's palette - not yet wired up, so this stands in as a neutral LEGO dark.
    /// </summary>
    private static readonly Color DefaultPlastic = Color.FromArgb(255, 32, 33, 36);

    /// <summary>
    /// LEGO minifig skin tone, taken from the face material's "HeadLowerUnder Tint" (#D28856). Used
    /// for the head piece, whose own material slot is empty in the shipped asset.
    /// </summary>
    private static readonly Color SkinTone = Color.FromArgb(255, 0xD2, 0x88, 0x56);

    private static Color AverageColour(TextureDecodeService.Decoded d)
    {
        long r = 0, g = 0, b = 0;
        foreach (var p in d.Pixels) { r += p.r; g += p.g; b += p.b; }
        var n = Math.Max(1, d.Pixels.Length);
        return Color.FromArgb(255, (int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>
    /// How one material slot should be shaded. A LEGO material supplies its base colour in one of
    /// three ways, confirmed by reading the shipped materials:
    ///   texture  - MI_Batman_89_EOM has BC/BC_Pristine (a full albedo once alpha is ignored)
    ///   swatch   - MI_HAT_TheBatman's BC is an 8x8 LEGO colour chip (T_LEGO_Black17)
    ///   colour   - MI_CAPE_Spiked_* has NO base-colour texture at all, only a "Base Colour" vector
    /// Faces are a fourth shape again: the print lives in "HeadLowerUnder BC" with a separate tint.
    /// </summary>
    private sealed record SlotShading(
        string? Texture, string? Normal, string? Mmr, Color? Colour, string? Alpha = null,
        bool Hidden = false, bool Cutout = false, string? Nrm2 = null);

    /// <summary>
    /// Material-slot indices whose LOD0 render sections the game itself never draws. Cape meshes
    /// carry a low-poly cloth-sim proxy sheet (the "box" around the cape) whose section is marked
    /// bDisabled in the cooked mesh - the engine hides it at runtime, so the preview must too.
    /// </summary>
    private static HashSet<int> DisabledSectionSlots(UObject mesh)
    {
        var hidden = new HashSet<int>();
        try
        {
            var lods = (mesh as USkeletalMesh)?.LODModels;
            if (lods is { Length: > 0 } && lods[0]?.Sections is { } sections)
            {
                foreach (var s in sections)
                {
                    if (s.bDisabled)
                    {
                        hidden.Add(s.MaterialIndex);
                    }
                }
            }
        }
        catch
        {
            // Best effort - an unreadable LOD just means nothing extra gets hidden.
        }
        return hidden;
    }

    /// <summary>Reads a vector (colour) parameter, walking up the parent chain.</summary>
    private static Color? FindColourParam(UObject? material, string name, int depth)
    {
        if (material is null || depth > 5)
        {
            return null;
        }
        var ps = material.GetOrDefault<FStructFallback[]>("VectorParameterValues");
        if (ps is not null)
        {
            foreach (var entry in ps)
            {
                var n = entry.GetOrDefault<FStructFallback>("ParameterInfo")?.GetOrDefault<FName>("Name").Text;
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var v = entry.GetOrDefault<FLinearColor>("ParameterValue");
                // FLinearColor is already LINEAR, which is exactly what three.js r128 wants for
                // material.color (it gamma-encodes at output). Pass the linear value straight through;
                // gamma-encoding it here washes flat colours out (the cape's #030305 -> near white).
                return Color.FromArgb(255,
                    (int)Math.Clamp(v.R * 255f + 0.5f, 0, 255),
                    (int)Math.Clamp(v.G * 255f + 0.5f, 0, 255),
                    (int)Math.Clamp(v.B * 255f + 0.5f, 0, 255));
            }
        }
        return FindColourParam(material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load(), name, depth + 1);
    }

    /// <summary>Resolves one material slot to a texture or a flat colour, plus its normal/MMR maps.</summary>
    private static SlotShading ResolveSlot(DefaultFileProvider provider, UObject? material, string previewDir)
    {
        if (material is null)
        {
            return new SlotShading(null, null, null, null);
        }

        // Capes are woven cloth: the base M_Cape_EoM graph (stripped from the cooked build, wiring
        // recovered from a near-exact Blender recreation) shades them from the shared PongeeFabric
        // texture set, not from any parameter on the instance. Bake those instead of the generic path.
        if (IsCapeMaterial(material))
        {
            return ResolveCapeSlot(provider, material, previewDir);
        }

        // Faces: the Blender recreation binds the face print's REAL alpha as opacity - the opposite
        // of the body treatment. The face piece is a shell over the head; without the cutout the
        // whole shell renders as an opaque mask instead of just the printed features.
        var facePrint = FindTextureParam(material, "HeadLowerUnder BC", 0);
        if (facePrint is not null)
        {
            // The instance binds the DIST(ressed) variant, whose alpha is a wear mask that measures
            // ~0 everywhere - cutting on it deletes the whole face. The pristine sibling (same path
            // minus _DIST) carries the actual print cutout; the paks ship it even though no
            // parameter references it.
            facePrint = LoadPristineSibling(provider, facePrint) ?? facePrint;
            var rel = "textures/" + MakeSafeName(facePrint.Name) + "_cut.png";
            var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest) || TextureDecodeService.TryExportPng(facePrint, dest, keepAlpha: true))
            {
                Console.WriteLine($"    face print (alpha cutout): {facePrint.Name} ({facePrint.Format})");
                // Pristine normal too - the DIST variant's scratch strokes read as stray eyebrows.
                string? faceNrm = null;
                if (FindTextureParam(material, "HeadLowerUnder NML", 0) is { } nmlTex)
                {
                    nmlTex = LoadPristineSibling(provider, nmlTex) ?? nmlTex;
                    var nrmRel = "textures/" + MakeSafeName(nmlTex.Name) + ".png";
                    var nrmDest = Path.Combine(previewDir, nrmRel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(nrmDest) || TextureDecodeService.TryExportPng(nmlTex, nrmDest, reconstructNormalZ: true))
                    {
                        faceNrm = nrmRel;
                    }
                }
                return new SlotShading(rel, faceNrm, null,
                    FindColourParam(material, "HeadLowerUnder Tint", 0), Cutout: true);
            }
        }

        var tex = ExportMaterialTexture(material as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial, previewDir, previewDir);
        var normal = ExportSlot(material, "DNRM_Pristine", previewDir, isNormal: true)
                     ?? ExportSlot(material, "DNRM", previewDir, isNormal: true)
                     ?? ExportSlot(material, "HeadLowerUnder NML", previewDir, isNormal: true);
        // No atlas normal: the part's base "NRM" (UV0) becomes the normal map, with the game's
        // micro-surface noise overlay baked in (Blender cowl graph). When there IS an atlas normal
        // (the body's DNRM on uv2), the noised base normal rides along separately - the viewer
        // blends the two UV spaces in the shader.
        string? nrm2 = null;
        if (normal is null)
        {
            normal = BakeNoisedNrm(provider, material, previewDir);
        }
        else
        {
            nrm2 = BakeNoisedNrm(provider, material, previewDir);
        }
        var mmr = ExportMmrSlot(material, previewDir);

        // No texture anywhere: the material states its colour directly (capes do this).
        Color? colour = tex is null
            ? FindColourParam(material, "Base Colour", 0) ?? FindColourParam(material, "BaseColour", 0)
            : FindColourParam(material, "HeadLowerUnder Tint", 0);

        if (tex is null && colour is not null)
        {
            Console.WriteLine($"      flat colour #{colour.Value.R:X2}{colour.Value.G:X2}{colour.Value.B:X2}");
        }
        return new SlotShading(tex, normal, mmr, colour, Nrm2: nrm2);
    }

    /// <summary>
    /// Loads the pristine variant of a distressed texture by naming convention (drop "_DIST"), e.g.
    /// T_LOWER_UNDER_Batman_DIST_BC -> T_LOWER_UNDER_Batman_BC. Null when there is no such asset.
    /// </summary>
    private static UTexture2D? LoadPristineSibling(DefaultFileProvider provider, UTexture2D distressed)
    {
        var path = distressed.GetPathName();
        if (!path.Contains("_DIST", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            return provider.LoadPackageObject(
                path.Replace("_DIST", "", StringComparison.OrdinalIgnoreCase).Split('.')[0]) as UTexture2D;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Micro-surface noise normal shared by every part material (tiled 6.9x in M_TPAGE).</summary>
    private const string MicroNoisePath = "/Game/Characters/Textures/Shared/T_Noise_Norm_SEB_N";

    /// <summary>
    /// Exports the material's base "NRM" (UV0 space) with the micro-surface noise overlay baked in.
    /// Cached per source texture; returns the preview-relative path or null when the material has no
    /// base NRM.
    /// </summary>
    private static string? BakeNoisedNrm(DefaultFileProvider provider, UObject material, string previewDir)
    {
        var baseNrm = FindTextureParam(material, "NRM", 0);
        if (baseNrm is null)
        {
            return null;
        }
        var rel = "textures/" + MakeSafeName(baseNrm.Name) + "_noised.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest))
        {
            return rel;
        }
        UTexture2D? noise = null;
        try { noise = provider.LoadPackageObject(MicroNoisePath) as UTexture2D; }
        catch { /* overlay-less bake still better than nothing */ }
        if (TextureDecodeService.TryBakeNoisedNormal(baseNrm, noise, tile: 6.9f, dest))
        {
            Console.WriteLine($"    base NRM + micro noise: {baseNrm.Name}");
            return rel;
        }
        return null;
    }

    private static string MakeSafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>True when the material's parent chain reaches the M_Cape_EoM cloth master.</summary>
    private static bool IsCapeMaterial(UObject? material)
    {
        for (var depth = 0; material is not null && depth < 6; depth++)
        {
            if (material.Name.StartsWith("M_Cape_EoM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            material = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load();
        }
        return false;
    }

    /// <summary>Shared cape fabric source textures - referenced by the stripped base graph directly.</summary>
    private const string CapeFabricDir = "/Game/Characters/Textures/Attachments/Cape/Batman_EOM/";

    /// <summary>
    /// Cape cloth shading: flat "Base Colour" from the instance (linear, usually near-black), plus the
    /// baked PongeeFabric weave maps (roughness/normal/alpha). See TryBakeCapeFabric for the recipe.
    /// </summary>
    private static SlotShading ResolveCapeSlot(DefaultFileProvider provider, UObject material, string previewDir)
    {
        var colour = FindColourParam(material, "Base Colour", 0) ?? FindColourParam(material, "BaseColour", 0);

        const string ormRel = "textures/cape_fabric_orm.png";
        const string nrmRel = "textures/cape_fabric_nrm.png";
        const string alphaRel = "textures/cape_fabric_alpha.png";
        var orm = Path.Combine(previewDir, ormRel.Replace('/', Path.DirectorySeparatorChar));
        var nrm = Path.Combine(previewDir, nrmRel.Replace('/', Path.DirectorySeparatorChar));
        var alpha = Path.Combine(previewDir, alphaRel.Replace('/', Path.DirectorySeparatorChar));

        var baked = File.Exists(orm);
        if (!baked)
        {
            try
            {
                var height = provider.LoadPackageObject(CapeFabricDir + "T_PongeeFabric_height") as UTexture2D;
                var fuzz = provider.LoadPackageObject(CapeFabricDir + "T_PongeeFabric_HairFuzzNoise") as UTexture2D;
                var weave = provider.LoadPackageObject(CapeFabricDir + "T_PongeeFabric_NRM") as UTexture2D;
                var scratch = provider.LoadPackageObject(CapeFabricDir + "T_PongeeFabric_Scratches_NRM") as UTexture2D;
                baked = height is not null && weave is not null &&
                        TextureDecodeService.TryBakeCapeFabric(height, fuzz, weave, scratch, orm, nrm, alpha);
                Console.WriteLine(baked ? "    cape: PongeeFabric weave baked" : "    cape: fabric bake unavailable");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    cape fabric load failed: {ex.Message.Split('\n')[0]}");
            }
        }

        return new SlotShading(
            Texture: null,
            Normal: baked && File.Exists(nrm) ? nrmRel : null,
            Mmr: baked ? ormRel : null,
            Colour: colour ?? Color.FromArgb(255, 4, 4, 5),
            Alpha: baked && File.Exists(alpha) ? alphaRel : null);
    }

    /// <summary>
    /// Prints every material slot on a mesh: index, the material bound there, and whether an override
    /// replaced it. Evidence for per-slot assignment - a mesh's sections do NOT share one material.
    /// </summary>
    private static void ReportSlots(UObject mesh, PreviewPart part)
    {
        var slots = (mesh as USkeletalMesh)?.Materials;
        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(part.MeshPath.Split('.')[0])}: {slots?.Length ?? 0} slot(s), {part.Overrides?.Length ?? 0} override(s)");
        if (slots is null) return;
        for (var i = 0; i < slots.Length; i++)
        {
            var m = slots[i]?.Load();
            var ovr = part.Overrides is not null && i < part.Overrides.Length
                ? part.Overrides[i]?.ResolvedObject?.GetPathName() : null;
            Console.WriteLine($"      [{i}] {m?.Name ?? "(none)"}" + (ovr is not null ? $"   <- override {ovr.Split('.')[^1]}" : ""));
        }
    }

    /// <summary>
    /// Exports the MMR slot repacked into ORM channel order (roughness->green, metalness->blue) so a
    /// single texture can drive both roughnessMap and metalnessMap correctly. See
    /// <see cref="TextureDecodeService.TryExportMmrAsOrm"/> for the channel mapping.
    /// </summary>
    private static string? ExportMmrSlot(UObject? material, string previewDir)
    {
        var t = FindTextureParam(material, "MMR_Pristine", 0) ?? FindTextureParam(material, "MMR", 0);
        if (t is null) return null;
        var safe = string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var rel = "textures/" + safe + "_orm.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest) || TextureDecodeService.TryExportMmrAsOrm(t, dest))
        {
            Console.WriteLine($"    MMR->ORM: {t.Name} ({t.Format})");
            return rel;
        }
        return null;
    }

    /// <summary>Decodes a named texture slot to PNG and returns its preview-relative path.</summary>
    private static string? ExportSlot(UObject? material, string slot, string previewDir, bool isNormal = false)
    {
        var t = FindTextureParam(material, slot, 0);
        if (t is null) return null;
        var rel = "textures/" + string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest) || TextureDecodeService.TryExportPng(t, dest, isNormal))
        {
            Console.WriteLine($"    {slot}: {t.Name} ({t.Format})");
            return rel;
        }
        return null;
    }

    /// <summary>Finds a named texture parameter, walking up the parent chain.</summary>
    private static UTexture2D? FindTextureParam(UObject? material, string slot, int depth)
    {
        if (material is null || depth > 5) return null;
        var ps = material.GetOrDefault<FStructFallback[]>("TextureParameterValues");
        if (ps is not null)
        {
            foreach (var entry in ps)
            {
                var name = entry.GetOrDefault<FStructFallback>("ParameterInfo")?.GetOrDefault<FName>("Name").Text;
                if (!string.Equals(name, slot, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.GetOrDefault<FPackageIndex>("ParameterValue")?.ResolvedObject?.Load() is UTexture2D t) return t;
            }
        }
        return FindTextureParam(material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load(), slot, depth + 1);
    }

    /// <summary>Walks a material instance and its parents looking for a base-colour texture.</summary>
    private static UTexture2D? FindBaseColourTexture(UObject? material, int depth)
    {
        if (material is null || depth > 5)
        {
            return null;
        }

        var textureParams = material.GetOrDefault<FStructFallback[]>("TextureParameterValues");
        if (textureParams is not null)
        {
            foreach (var slot in BaseColourSlots)
            {
                foreach (var entry in textureParams)
                {
                    var name = entry.GetOrDefault<FStructFallback>("ParameterInfo")
                                    ?.GetOrDefault<FName>("Name").Text;
                    if (!string.Equals(name, slot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (entry.GetOrDefault<FPackageIndex>("ParameterValue")?.ResolvedObject?.Load() is UTexture2D t)
                    {
                        return t;
                    }
                }
            }
        }

        // Not on this instance - inherit from the parent material.
        var parent = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load();
        return FindBaseColourTexture(parent, depth + 1);
    }

    /// <summary>
    /// Seats the local-authored head attachments (face, cowl) on the head piece by matching their
    /// BASE to the head's base - the head's chin line is what they're authored against.
    ///
    /// Measured from the exported geometry rather than the skeleton, because the parts are authored in
    /// two different spaces: body/head/cape carry world coordinates (the head already sits at
    /// y 1.154-1.615), while face/cowl sit around their own origin, and the two use different origin
    /// conventions. Base-matching fits both: the cowl is 0.713 tall against a 0.461 head, and that
    /// 0.252 difference is exactly the ears, which then land above the crown.
    /// </summary>
    private static List<PlacedModel> AlignToHead(string dir, IReadOnlyList<(string File, PreviewPart Part, List<SlotShading> Slots)> models)
    {
        var head = models.FirstOrDefault(m => m.Part.IsHeadPiece);
        (Vector3 Min, Vector3 Max)? headB3 =
            head.File is null ? null : GlbInspector.Bounds3(Path.Combine(dir, head.File));
        var headBase = headB3?.Min.Y;
        if (headBase is not null)
        {
            Console.WriteLine($"  head piece spans {headB3!.Value.Min.Y:0.###}..{headB3.Value.Max.Y:0.###} (base {headBase:0.###})");
        }

        var result = new List<PlacedModel>();
        foreach (var (file, part, slots) in models)
        {
            var isBody = part.MeshPath.Contains("LEGOfig", StringComparison.OrdinalIgnoreCase);
            var isFace = part.MeshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase);
            var isHead = part.IsHeadPiece;
            if (!part.AttachToHead || headBase is null)
            {
                result.Add(new PlacedModel(file, Vector3.Zero, slots, isBody, isFace, isHead));
                continue;
            }
            // Hair and other rigid head pieces are authored around their own origin and pinned to
            // the head socket, so bounds-matching them to the head (which suits shells like cowls)
            // puts them in the wrong place. Anchor them to the head attach bone instead.
            if (part.IsStaticAttachment && headB3 is { } hb)
            {
                // Cooked meshes carry no sockets, and the head attach bone sits at the very TOP of
                // the skull - anchoring there leaves hair hovering. A hair piece is modelled to
                // sheathe the head, so centre it on the head in all three axes: that also corrects
                // the small front/back bias in how the piece is authored.
                var hairB = GlbInspector.Bounds3(Path.Combine(dir, file));
                if (hairB is { } pb)
                {
                    var headCentre = (hb.Min + hb.Max) / 2f;
                    var partCentre = (pb.Min + pb.Max) / 2f;
                    var delta = headCentre - partCentre;
                    Console.WriteLine($"  {file}: head attachment centred -> "
                                      + $"({delta.X:0.###}, {delta.Y:0.###}, {delta.Z:0.###})");
                    result.Add(new PlacedModel(file, delta, slots, isBody, isFace, isHead));
                    continue;
                }
            }

            var b3 = GlbInspector.Bounds3(Path.Combine(dir, file));
            if (b3 is null)
            {
                result.Add(new PlacedModel(file, Vector3.Zero, slots, isBody, isFace, isHead));
                continue;
            }
            var dy = headBase.Value - b3.Value.Min.Y;

            // Face shells are authored at the head's exact radius (front x 0.203 vs head 0.204) -
            // effectively coincident with the head surface, so they lose the depth test and vanish.
            // The game pulls them out with a PixelDepthOffset material function; the preview nudges
            // the shell forward instead. The character faces +X (chest/cowl-opening bulge that way).
            var dx = 0f;
            if (part.MeshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase) && headB3 is not null)
            {
                dx = headB3.Value.Max.X + 0.002f - b3.Value.Max.X;
                // The face does NOT sit flush with the head base. Calibrated against the aligned
                // community Blender scene by anchoring the mouth print itself (it must land 18.6% of
                // head height above the head base): our exported SK_LEGOface glb includes every
                // feature shell (eyes/brows/mouth), so its bbox bottom sits lower than the base
                // shell's - the net lift that lands the mouth correctly is 8.6% of head height.
                var headHeight = headB3.Value.Max.Y - headB3.Value.Min.Y;
                dy += headHeight * 0.086f;
            }
            Console.WriteLine($"  {file}: spans y {b3.Value.Min.Y:0.###}..{b3.Value.Max.Y:0.###} -> lift {dy:0.###}" +
                              (dx != 0 ? $", forward {dx:0.####}" : ""));
            result.Add(new PlacedModel(file, new Vector3(dx, dy, 0), slots, isBody, isFace, isHead));
        }
        return result;
    }

    /// <summary>Extracts the vendored three.js + writes the viewer HTML + model list into the dir.</summary>
    private static void WriteViewerAssets(string dir, IReadOnlyList<PlacedModel> models)
    {
        foreach (var js in new[] { "three.min.js", "GLTFLoader.js", "OrbitControls.js" })
        {
            var bytes = EmbeddedAssets.ReadBytes($"preview/{js}")
                        ?? throw new FileNotFoundException($"embedded viewer asset missing: {js}");
            File.WriteAllBytes(Path.Combine(dir, js), bytes);
        }
        Console.WriteLine("  writing viewer assets...");
        var jsonList = "[" + string.Join(",", models.Select(m =>
        {
            var slots = string.Join(",", m.Slots.Select(sl =>
                "{" +
                $"\"tex\":{Q(sl.Texture)},\"nrm\":{Q(sl.Normal)},\"mmr\":{Q(sl.Mmr)},\"alpha\":{Q(sl.Alpha)}," +
                $"\"hide\":{(sl.Hidden ? "true" : "false")},\"cut\":{(sl.Cutout ? "true" : "false")},\"nrm2\":{Q(sl.Nrm2)}," +
                $"\"col\":{(sl.Colour is null ? "null" : $"\"#{sl.Colour.Value.R:X2}{sl.Colour.Value.G:X2}{sl.Colour.Value.B:X2}\"")}" +
                "}"));
            // Extract every UV channel so the viewer's switcher can bind sets three.js drops on import.
            var baseName = Path.GetFileNameWithoutExtension(m.File);
            Console.WriteLine($"    uv extract {m.File}");
            var uvs = GlbInspector.ExtractUvChannels(Path.Combine(dir, m.File), dir, baseName);
            var fg = m.FaceGroups is null ? "null" : $"[{string.Join(",", m.FaceGroups)}]";
            return $"{{\"file\":\"{m.File}\",\"base\":\"{baseName}\",\"body\":{(m.IsBody ? "true" : "false")},\"isface\":{(m.IsFace ? "true" : "false")},\"ishead\":{(m.IsHead ? "true" : "false")}," +
                   $"\"fgroups\":{fg},\"mouth\":{Q(m.MouthTex)},\"mhide\":{(m.MouthHidden ? "true" : "false")}," +
                   $"\"fbands\":[{string.Join(",", (m.Bands ?? new()).Select(b => $"[{b.Band},{b.Tris},{Q(b.Tex)},{(b.Tint is null ? "null" : $"\"#{b.Tint.Value.R:X2}{b.Tint.Value.G:X2}{b.Tint.Value.B:X2}\"")}]"))}]," +
                   $"\"poses\":{PoseJson(m.Poses)}," +
                   $"\"uvs\":[{string.Join(",", uvs)}]," +
                   $"\"offset\":[{m.Offset.X:0.#####},{m.Offset.Y:0.#####},{m.Offset.Z:0.#####}]," +
                   $"\"slots\":[{slots}]}}";
        })) + "]";
        static string Q(string? v) => v is null ? "null" : $"\"{v}\"";

        // Serialises every expression pose as {name:{bone:[px,py,pz,qx,qy,qz,qw,sx,sy,sz]}}.
        // {expression:{frame:{bone:[px,py,pz,qx,qy,qz,qw,sx,sy,sz]}}}
        static string PoseJson(Dictionary<string, Dictionary<int, Dictionary<string, (Vector3 P, System.Numerics.Quaternion Q, Vector3 S)>>>? poses)
        {
            if (poses is null || poses.Count == 0)
            {
                return "{}";
            }
            var sb = new System.Text.StringBuilder("{");
            var firstExpr = true;
            foreach (var (name, frames) in poses)
            {
                if (!firstExpr) sb.Append(',');
                firstExpr = false;
                sb.Append('"').Append(name).Append("\":{");
                var firstFrame = true;
                foreach (var (frame, bones) in frames)
                {
                    if (!firstFrame) sb.Append(',');
                    firstFrame = false;
                    sb.Append('"').Append(frame).Append("\":{");
                    var firstBone = true;
                    foreach (var (bone, t) in bones)
                    {
                        if (!firstBone) sb.Append(',');
                        firstBone = false;
                        sb.Append('"').Append(bone).Append("\":[")
                          .Append($"{t.P.X:0.####},{t.P.Y:0.####},{t.P.Z:0.####},")
                          .Append($"{t.Q.X:0.####},{t.Q.Y:0.####},{t.Q.Z:0.####},{t.Q.W:0.####},")
                          .Append($"{t.S.X:0.####},{t.S.Y:0.####},{t.S.Z:0.####}]");
                    }
                    sb.Append('}');
                }
                sb.Append('}');
            }
            return sb.Append('}').ToString();
        }

        File.WriteAllText(Path.Combine(dir, "models.js"), $"window.PREVIEW_MODELS={jsonList};");
        File.WriteAllText(Path.Combine(dir, "index.html"), ViewerHtml);
        Console.WriteLine("  viewer assets written");
    }

    /// <summary>
    /// three.js scene: dark ground, hemisphere + key light, orbit/zoom, auto-frames the model. Loaded
    /// from local files over the WebView2 virtual host, so it runs fully offline.
    /// </summary>
    private const string ViewerHtml = """
<!doctype html><html><head><meta charset="utf-8"><style>
  html,body{margin:0;height:100%;background:#1a1d22;overflow:hidden;font-family:Segoe UI,sans-serif}
  #hud{position:absolute;left:12px;top:10px;color:#9ea6b2;font-size:13px;pointer-events:none}
  #hud b{color:#f0c230}
  #exprwrap{position:absolute;right:14px;top:12px;color:#9ea6b2;font-size:13px;
    background:rgba(26,29,34,.85);padding:8px 10px;border:1px solid #333a44;border-radius:8px}
  #exprwrap label{color:#f0c230;margin-right:4px}
  #expr{background:#22262c;color:#e6e9ee;border:1px solid #3a4048;border-radius:5px;padding:3px 6px;
    font-family:inherit;font-size:13px;outline:none}
  #err{position:absolute;left:12px;bottom:12px;color:#f0c230;font-size:12px;line-height:1.5;font-family:Consolas,monospace}
  canvas{display:block}
</style></head><body>
<div id="hud"><b>Preview</b> — drag to orbit, scroll to zoom</div>
<div id="err"></div>
<script src="three.min.js"></script>
<script src="GLTFLoader.js"></script>
<script src="OrbitControls.js"></script>
<script src="models.js"></script>
<script>
const scene=new THREE.Scene();scene.background=new THREE.Color(0x1a1d22);
const camera=new THREE.PerspectiveCamera(45,innerWidth/innerHeight,0.1,100000);
const renderer=new THREE.WebGLRenderer({antialias:true});
renderer.setPixelRatio(devicePixelRatio);renderer.setSize(innerWidth,innerHeight);
renderer.outputEncoding=THREE.sRGBEncoding;
// The game renders through UE's ACES filmic tonemapper - without it saturated colours (the face
// print's nougat tint) come out light and candy-like instead of the in-game deep brown.
renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=1.1;
document.body.appendChild(renderer.domElement);
// A soft studio environment so metals (the belt/buckle from the MMR metalness channel) have something
// to reflect - without it a metallic surface renders pure black. Built from a tiny vertical gradient
// (bright top, dark floor) run through PMREM; also lifts the plastic with a subtle sheen.
function buildEnv(){
  const c=document.createElement('canvas');c.width=8;c.height=64;const ctx=c.getContext('2d');
  const g=ctx.createLinearGradient(0,0,0,64);
  g.addColorStop(0,'#d6dde8');g.addColorStop(0.55,'#7a828e');g.addColorStop(1,'#24272c');
  ctx.fillStyle=g;ctx.fillRect(0,0,8,64);
  const t=new THREE.CanvasTexture(c);t.mapping=THREE.EquirectangularReflectionMapping;
  const pm=new THREE.PMREMGenerator(renderer);const env=pm.fromEquirectangular(t).texture;
  pm.dispose();t.dispose();return env;
}
scene.environment=buildEnv();
scene.add(new THREE.HemisphereLight(0xffffff,0x50525a,1.5));
const key=new THREE.DirectionalLight(0xffffff,1.6);key.position.set(4,6,5);scene.add(key);
const fill=new THREE.DirectionalLight(0xffffff,0.7);fill.position.set(-5,2,-3);scene.add(fill);
const rim=new THREE.DirectionalLight(0xffffff,0.5);rim.position.set(0,3,-6);scene.add(rim);
const controls=new THREE.OrbitControls(camera,renderer.domElement);
controls.enableDamping=true;controls.dampingFactor=0.08;
const root=new THREE.Group();scene.add(root);
const loader=new THREE.GLTFLoader();const models=window.PREVIEW_MODELS||[];
const texLoader=new THREE.TextureLoader();
const diag=[];
function say(s){diag.push(s);document.getElementById('err').innerHTML=diag.join('<br>');}
// CUE4Parse writes textures as loose .png beside the .glb rather than embedding them, so the base
// colour map is applied here from the path the exporter reported.
function tex(path,sRGB){if(!path)return null;const t=texLoader.load(path);t.flipY=false;
  if(sRGB)t.encoding=THREE.sRGBEncoding;return t;}
// Each mesh section has its OWN material slot. Shading is resolved per slot in C# and applied by
// index here - spraying one texture across every section is what mixed up the cape/face/cowl.
const uvMeshes=[]; // textured meshes we can retarget to a different UV channel live
function dress(g,info){
  const slots=(info&&info.slots)||[];
  let matIndex=0,applied=0;
  g.scene.traverse(o=>{if(!o.isMesh)return;
    // The shared LEGOfig body maps into the character's decal atlas through its SECOND UV set
    // (TEXCOORD_1 = ExtraUV0 = three.js uv2), not UV0. UV0 is a per-part structural unwrap, so the
    // atlas tiles onto every limb. Bind uv2 as the sampling UV. (Confirmed via the channel switcher.)
    if(info.body&&o.geometry.attributes.uv2){
      // Keep the structural UV0 reachable as aUv0 - the plastic base normal samples it while the
      // atlas maps sample uv (rebound to uv2 below).
      o.geometry.setAttribute('aUv0',o.geometry.attributes.uv);
      o.geometry.setAttribute('uv',o.geometry.attributes.uv2);}
    // Keep the switcher available on body meshes for tuning other suits.
    if(info.body&&info.base&&info.uvs&&info.uvs.length){uvMeshes.push({o:o,base:info.base,uvs:info.uvs});}
    const list=Array.isArray(o.material)?o.material:[o.material];
    list.forEach((m,li)=>{if(!m)return;
      const s=slots[matIndex]||slots[0]||{};
      matIndex++;
      // Sections the cooked mesh marks bDisabled (cape cloth-sim proxy sheets) are never drawn
      // by the game - hide the whole primitive.
      if(s.hide){o.visible=false;say(info.file+': hid disabled section');return;}
      if(s.tex){const t=texLoader.load(s.tex,
          ()=>say('  '+s.tex.split('/').pop()+' loaded'),
          undefined,()=>say('  TEX FAIL '+s.tex));
        t.flipY=false;t.encoding=THREE.sRGBEncoding;m.map=t;
        // Face prints are white stencils - their colour comes from the material's tint (the
        // Blender recreation drives Base Colour flat and takes only the texture's alpha). The tint
        // hex is an sRGB colour, so decode it to linear for the shader - passing it raw washes the
        // mouth out to pale tan instead of the reference's darker nougat.
        m.color=(s.cut&&s.col)?new THREE.Color(s.col).convertSRGBToLinear():new THREE.Color(0xffffff);applied++;}
      else if(s.col){m.map=null;m.color=new THREE.Color(s.col);applied++;}
      else if(!m.map){m.color=new THREE.Color(0x9aa0a8);}
      const n=tex(s.nrm,false); if(n)m.normalMap=n;
      // Body: blend the uv0-space plastic base normal (LEGOfig seams + micro noise, baked in C#)
      // under the uv2-space DNRM sculpt. three.js samples every normal map with one UV set, so the
      // second map needs a small shader patch reading the preserved aUv0 attribute.
      if(s.nrm2&&n){
        const bn=tex(s.nrm2,false);bn.wrapS=bn.wrapT=THREE.RepeatWrapping;
        m.onBeforeCompile=sh=>{
          sh.uniforms.baseNormalMap={value:bn};
          sh.vertexShader=sh.vertexShader
            .replace('#include <common>','#include <common>\nattribute vec2 aUv0;varying vec2 vBaseUv;')
            .replace('#include <uv_vertex>','#include <uv_vertex>\nvBaseUv=aUv0;');
          // onBeforeCompile sees the template BEFORE #include expansion, so the mapN line cannot be
          // patched directly - expand the chunk ourselves, patch inside it, and splice it back.
          sh.fragmentShader=sh.fragmentShader
            .replace('#include <common>','#include <common>\nuniform sampler2D baseNormalMap;varying vec2 vBaseUv;')
            .replace('#include <normal_fragment_maps>',
              THREE.ShaderChunk.normal_fragment_maps.replace(
                'vec3 mapN = texture2D( normalMap, vUv ).xyz * 2.0 - 1.0;',
                'vec3 mapN = texture2D( normalMap, vUv ).xyz * 2.0 - 1.0;\n'+
                'vec3 baseN = texture2D( baseNormalMap, vBaseUv ).xyz * 2.0 - 1.0;\n'+
                'mapN = normalize( vec3( mapN.xy + baseN.xy, mapN.z * baseN.z ) );'));
        };
        m.customProgramCacheKey=function(){return 'baseNormal';};
      }
      // MMR is exported repacked into ORM order (roughness->green, metalness->blue) so one texture
      // drives both maps the way three.js samples them. The scene has an environment map, so the
      // metallic belt/buckle now reflects it instead of rendering black (which is why metalness used
      // to be forced to 0). Plastic areas have metalness 0 in the map and stay diffuse.
      const r=tex(s.mmr,false);
      if(r){m.roughnessMap=r;m.metalnessMap=r;m.roughness=1;m.metalness=1;}
      else{m.roughness=0.55;m.metalness=0;}
      m.envMapIntensity=0.5;
      // LEGO packs masks into alpha - it is not opacity.
      m.transparent=false;m.alphaTest=0;m.opacity=1;m.depthWrite=true;
      // Cape cloth: the baked weave alpha makes the deep weave holes see-through (the game's
      // BLEND_Masked + hashed look). alphaTest keeps depth-write on, so no sorting artifacts.
      if(s.alpha){const a=tex(s.alpha,false);if(a){m.alphaMap=a;m.alphaTest=0.4;}}
      // Face prints: their texture alpha IS opacity (unlike the body) - cut the shell away so only
      // the printed features remain over the head piece. The shell hugs the head surface, so bias
      // the depth test toward the camera (the game's PixelDepthOffset equivalent).
      if(s.cut){m.alphaTest=0.5;m.polygonOffset=true;m.polygonOffsetFactor=-2;m.polygonOffsetUnits=-2;}
      // Cloth (the cape) is authored single-sided; without double-siding you see through to its
      // inside faces. Cheap to always enable for a static preview.
      m.side=THREE.DoubleSide;
      // The body mesh carries a COLOR_0 vertex-colour set (a mask/AO), which GLTFLoader turns into
      // vertexColors=true. That multiplies the albedo - if those colours are dark the whole surface
      // goes black regardless of texture or UV. It is not the display colour, so switch it off.
      if(m.vertexColors){m.vertexColors=false;say('  '+(info.file||'')+': disabled vertexColors');}
      m.needsUpdate=true;});
    // Face feature split: the cooked face mesh is ONE section; C# reordered its index buffer into
    // [base][mouth][hidden] runs so the mouth shell can wear its own print (black stern mouth) and
    // the unused feature shells (eyes etc., dummied by this character's material) stay hidden.
    if(info.isface&&info.fbands&&info.fbands.length&&o.geometry.index){
      // One group + material per ExtraUV0 band. A band with no texture is a feature this
      // character does not use (its material parameter is a dummy), so it is not drawn.
      const geo=o.geometry;
      geo.clearGroups();
      const mats=[];let off=0;
      info.fbands.forEach(b=>{
        const band=b[0],tris=b[1],texPath=b[2],tint=b[3];
        const m2=new THREE.MeshStandardMaterial({color:0xffffff,roughness:0.42,metalness:0});
        m2.side=THREE.DoubleSide;
        // Feature textures are WHITE STENCILS: the alpha is the shape, the colour comes from the
        // material's "<feature> Tint" (brows 442E2B, skin D28856). Tint is linear, so convert.
        if(texPath){const t=tex(texPath,true);if(t){m2.map=t;m2.alphaTest=0.5;}}
        else m2.visible=false;
        if(tint)m2.color=new THREE.Color(tint).convertSRGBToLinear();
        // Layered shells sit on the same surface; bias the ones above the base skin forward.
        if(band!==8){m2.polygonOffset=true;m2.polygonOffsetFactor=-2;m2.polygonOffsetUnits=-2;}
        mats.push(m2);
        faceBandMats.push({band:band,mat:m2,tris:tris,tex:texPath});
        geo.addGroup(off,tris*3,mats.length-1);
        off+=tris*3;
      });
      o.material=mats;
      say(info.file+': face bands '+info.fbands.filter(b=>b[2]).length+'/'+info.fbands.length+' textured');
    }
  });
}
// Apply the facial rig pose from the game's expression animation. Without this the face renders in
// BIND pose, which the game never shows.
const faceRig={bones:[],bind:new Map(),poses:null,frameKeys:[]};
// Band identification aid: paint every ExtraUV0 band a distinct colour with a legend, so which
// band is which facial feature can be read off one screenshot instead of inferred.
const faceBandMats=[];let bandDebug=false;
const BAND_COLOURS=[0xe6194b,0x3cb44b,0xffe119,0x4363d8,0xf58231,0x911eb4,0x46f0f0,0xf032e6,
                    0xbcf60c,0xfabebe,0x008080,0xe6beff,0x9a6324,0xfffac8,0x800000,0xaaffc3,
                    0x808000,0xffd8b1,0x000075,0x808080,0xffffff];
let bandIndex=-1;
addEventListener('keydown',e=>{
  if(e.key!=='b'&&e.key!=='B')return;
  if(!faceBandMats.length){say('no face bands to identify');return;}
  // Cycle ONE band at a time: these shells are stacked full-face variants, so showing them all
  // together just z-fights into stripes and identifies nothing.
  if(bandIndex<0){
    faceBandMats.forEach(b=>{b.mat.userData.savedMap=b.mat.map;b.mat.userData.savedVisible=b.mat.visible;});
  }
  bandIndex++;
  if(bandIndex>=faceBandMats.length){
    bandIndex=-1;
    faceBandMats.forEach(b=>{
      b.mat.map=b.mat.userData.savedMap||null;
      b.mat.color=new THREE.Color(0xffffff);
      b.mat.visible=b.mat.userData.savedVisible!==false&&!!b.mat.map;
      if(b.mat.map)b.mat.alphaTest=0.5;
      b.mat.needsUpdate=true;});
    say('band debug off - normal face restored');
    return;
  }
  faceBandMats.forEach((b,i)=>{
    const on=i===bandIndex;
    b.mat.map=null;b.mat.alphaTest=0;
    b.mat.color=new THREE.Color(on?0xff2d55:0x202428);
    b.mat.visible=on;
    b.mat.needsUpdate=true;
  });
  const cur=faceBandMats[bandIndex];
  say('BAND '+cur.band+' — '+cur.tris+' tris'+(cur.tex?' [has texture]':' [no texture]')
      +'  ('+(bandIndex+1)+' of '+faceBandMats.length+', press B for next)');
});
function poseFace(g,info){
  if(!info.poses||!Object.keys(info.poses).length)return;
  faceRig.poses=info.poses;
  g.scene.traverse(o=>{
    if(o.isBone||o.type==='Bone'){
      faceRig.bones.push(o);
      faceRig.bind.set(o,{p:o.position.clone(),q:o.quaternion.clone(),s:o.scale.clone()});
    }
  });
  buildExpressionUi();
}
// Apply one of the game's expression poses to the facial rig (or restore the bind pose).
function applyExpression(name,frameIdx){
  const frames=name&&faceRig.poses?faceRig.poses[name]:null;
  let pose=null;
  if(frames){
    const keys=Object.keys(frames).map(Number).sort((a,b)=>a-b);
    faceRig.frameKeys=keys;
    const fi=Math.min(frameIdx===undefined?Math.floor(keys.length/2):frameIdx,keys.length-1);
    pose=frames[keys[fi]];
    const lab=document.getElementById('frameLabel');
    if(lab)lab.textContent='frame '+keys[fi];
    const sl=document.getElementById('frame');
    if(sl){sl.max=keys.length-1;sl.value=fi;}
  }
  faceRig.bones.forEach(b=>{
    const t=pose&&pose[b.name];
    if(t){b.position.set(t[0],t[1],t[2]);b.quaternion.set(t[3],t[4],t[5],t[6]);b.scale.set(t[7],t[8],t[9]);}
    else{const bp=faceRig.bind.get(b);if(bp){b.position.copy(bp.p);b.quaternion.copy(bp.q);b.scale.copy(bp.s);}}
  });
}
function buildExpressionUi(){
  if(!faceRig.poses||document.getElementById('expr'))return;
  const names=Object.keys(faceRig.poses);
  if(!names.length)return;
  const wrap=document.createElement('div');
  wrap.id='exprwrap';
  wrap.innerHTML='<label for="expr">Expression</label> ';
  const sel=document.createElement('select');
  sel.id='expr';
  sel.innerHTML='<option value="">None (rest)</option>'+names.map(n=>'<option>'+n+'</option>').join('');
  sel.onchange=()=>{applyExpression(sel.value);applyShrinkwrap();};
  wrap.appendChild(sel);
  // Scrub the sampled frames: these clips ease in, hold, then relax, so the pose you
  // want is somewhere in the middle - this finds it by eye instead of by guessing.
  const row=document.createElement('div');
  row.style.cssText='margin-top:6px;display:flex;align-items:center;gap:6px';
  const sl=document.createElement('input');
  sl.type='range';sl.id='frame';sl.min=0;sl.max=4;sl.value=2;sl.style.width='110px';
  sl.oninput=()=>{applyExpression(sel.value,+sl.value);applyShrinkwrap();};
  const lab=document.createElement('span');lab.id='frameLabel';lab.textContent='frame';
  lab.style.cssText='font-size:12px;color:#9ea6b2;min-width:64px';
  row.appendChild(sl);row.appendChild(lab);
  wrap.appendChild(row);
  document.body.appendChild(wrap);
}
function frameAll(){
  const box=new THREE.Box3().setFromObject(root);
  if(box.isEmpty()){say('frame: scene is EMPTY - nothing was added');return;}
  const size=box.getSize(new THREE.Vector3());const center=box.getCenter(new THREE.Vector3());
  root.position.sub(center);
  const d=Math.max(size.x,size.y,size.z)||1;
  camera.position.set(d*0.55,d*0.3,d*1.7);camera.near=d/100;camera.far=d*40;camera.updateProjectionMatrix();
  controls.target.set(0,0,0);controls.update();
  // One line saying what actually reached the scene, so a bad render can be read off the screen.
  say('frame: '+root.children.length+' parts, size '
      +size.x.toFixed(2)+'x'+size.y.toFixed(2)+'x'+size.z.toFixed(2));
}
function load(m){return new Promise(res=>loader.load(m.file,g=>{dress(g,m);poseFace(g,m);res({m,scene:g.scene});},
  undefined,e=>{document.getElementById('err').textContent='Load error ('+m.file+'): '+(e&&e.message||e);res(null);}));}
Promise.all(models.map(load)).then(loaded=>{
  loaded=loaded.filter(Boolean);
  // Offsets are precomputed in C# from the exported glb skeletons, so the viewer just places them.
  loaded.forEach(x=>{
    const o=x.m.offset;
    if(o&&(o[0]||o[1]||o[2]))x.scene.position.set(o[0],o[1],o[2]);
    root.add(x.scene);
  });
  // Frame the scene BEFORE anything optional runs: frameAll is what positions the camera, so if
  // a later step throws the camera is left at the origin - inside the character, which reads as a
  // "stuck camera" with no error on screen.
  frameAll();
  try{
  // Head is the shrinkwrap target; the face shells project onto it.
    const headEntry=loaded.find(x=>x.m.ishead), faceEntry=loaded.find(x=>x.m.isface);
    if(headEntry&&faceEntry){
      headEntry.scene.updateMatrixWorld(true);
      let headMesh=null; headEntry.scene.traverse(o=>{if(o.isMesh&&!headMesh)headMesh=o;});
      let faceMesh=null; faceEntry.scene.traverse(o=>{if(o.isMesh&&!faceMesh)faceMesh=o;});
      if(headMesh&&faceMesh&&faceMesh.isSkinnedMesh){
        buildWrapTarget(headMesh);
        const g=faceMesh.geometry;
        const vg=new Int32Array(g.attributes.position.count);
        g.groups.forEach(grp=>{for(let i=grp.start;i<grp.start+grp.count;i++)vg[g.index.getX(i)]=grp.materialIndex;});
        // Render a BAKED copy: GPU-skinned positions cannot be read back, so the
        // shell is posed + projected on the CPU into this twin, while the skinned
        // original stays (hidden) as the source the skeleton drives.
        const bg=g.clone();
        const baked=new THREE.Mesh(bg,faceMesh.material);
        baked.position.copy(faceMesh.position);
        baked.quaternion.copy(faceMesh.quaternion);
        baked.scale.copy(faceMesh.scale);
        faceMesh.parent.add(baked);
        faceMesh.visible=false;
        baked.updateMatrixWorld(true);
        faceSkins.push({mesh:faceMesh,baked:baked,
                        offsets:{0:0.0005,1:0.0017,2:0.0028},vertGroup:vg});
        // Defer a frame: world matrices (and the skeleton) must be current, or the
        // projection reads stale transforms and silently no-ops.
        // Shrinkwrap is OFF: projecting the shells onto the head still explodes the geometry on
        // some characters, and it runs after the camera has framed the scene, so the failure looks
        // like a stuck camera. Set wrapPending=3 to re-enable once the projection is trustworthy.
        wrapPending=0;
      }
    }
  }catch(e){say('face setup skipped: '+e.message);}

  const avail=[...new Set(uvMeshes.flatMap(u=>u.uvs))].sort();
  say('UV switch: press '+avail.join('/')+' to change texture UV channel (now: glTF default)');
});
// Calibration aid: arrows move the face piece in 0.004 steps and report the total, so the right
// permanent offset can be read straight off the HUD instead of guessed.
addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight);});
(function loop(){requestAnimationFrame(loop);controls.update();renderer.render(scene,camera);
  // Project the face onto the head a few frames in, when the skeleton and world
  // matrices are actually live - doing it during load silently no-ops.
  if(wrapPending>0&&--wrapPending===0){applyShrinkwrap();say('face shrinkwrapped onto the head');}
})();
</script></body></html>
""";
}
