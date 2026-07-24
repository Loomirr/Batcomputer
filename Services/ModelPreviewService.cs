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
        string MeshPath, bool AttachToHead, bool IsHeadPiece = false, FPackageIndex[]? Overrides = null);

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

        var bp = provider.LoadPackage(bpPath);
        foreach (var exp in bp.GetExports())
        {
            if (!exp.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var meshRef = exp.GetOrDefault<FPackageIndex>("SkeletalMeshAsset")
                          ?? exp.GetOrDefault<FPackageIndex>("SkeletalMesh");
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
                Overrides: overrides));
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
            for (var si = 0; si < (slotMats?.Length ?? 0); si++)
            {
                var slotOverride = part.Overrides is not null && si < part.Overrides.Length
                    ? part.Overrides[si]?.ResolvedObject?.Load() : null;
                var resolved = ResolveSlot(slotOverride ?? slotMats![si]?.Load(), previewDir);
                if (headSkin is not null && resolved.Texture is null && resolved.Colour is null)
                {
                    resolved = resolved with { Colour = headSkin };
                    Console.WriteLine($"      head: no material bound -> skin tone #{headSkin.Value.R:X2}{headSkin.Value.G:X2}{headSkin.Value.B:X2}");
                }
                slotShading.Add(resolved);
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

        WriteViewerAssets(previewDir, AlignToHead(previewDir, models));
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
    private readonly record struct PlacedModel(string File, Vector3 Offset, List<SlotShading> Slots, bool IsBody);

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
    private sealed record SlotShading(string? Texture, string? Normal, string? Mmr, Color? Colour);

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
                return Color.FromArgb(255,
                    (int)Math.Clamp(v.R * 255f, 0, 255),
                    (int)Math.Clamp(v.G * 255f, 0, 255),
                    (int)Math.Clamp(v.B * 255f, 0, 255));
            }
        }
        return FindColourParam(material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load(), name, depth + 1);
    }

    /// <summary>Resolves one material slot to a texture or a flat colour, plus its normal/MMR maps.</summary>
    private static SlotShading ResolveSlot(UObject? material, string previewDir)
    {
        if (material is null)
        {
            return new SlotShading(null, null, null, null);
        }

        var tex = ExportMaterialTexture(material as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial, previewDir, previewDir);
        var normal = ExportSlot(material, "DNRM_Pristine", previewDir, isNormal: true)
                     ?? ExportSlot(material, "DNRM", previewDir, isNormal: true)
                     ?? ExportSlot(material, "HeadLowerUnder NML", previewDir, isNormal: true)
                     ?? ExportSlot(material, "NRM", previewDir, isNormal: true);
        var mmr = ExportSlot(material, "MMR_Pristine", previewDir) ?? ExportSlot(material, "MMR", previewDir);

        // No texture anywhere: the material states its colour directly (capes do this).
        Color? colour = tex is null
            ? FindColourParam(material, "Base Colour", 0) ?? FindColourParam(material, "BaseColour", 0)
            : FindColourParam(material, "HeadLowerUnder Tint", 0);

        if (tex is null && colour is not null)
        {
            Console.WriteLine($"      flat colour #{colour.Value.R:X2}{colour.Value.G:X2}{colour.Value.B:X2}");
        }
        return new SlotShading(tex, normal, mmr, colour);
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
        (float Min, float Max)? headBounds =
            head.File is null ? null : GlbInspector.VerticalBounds(Path.Combine(dir, head.File));
        var headBase = headBounds?.Min;
        if (headBase is not null)
        {
            Console.WriteLine($"  head piece spans {headBounds!.Value.Min:0.###}..{headBounds.Value.Max:0.###} (base {headBase:0.###})");
        }

        var result = new List<PlacedModel>();
        foreach (var (file, part, slots) in models)
        {
            var isBody = part.MeshPath.Contains("LEGOfig", StringComparison.OrdinalIgnoreCase);
            if (!part.AttachToHead || headBase is null)
            {
                result.Add(new PlacedModel(file, Vector3.Zero, slots, isBody));
                continue;
            }
            var b = GlbInspector.VerticalBounds(Path.Combine(dir, file));
            if (b is null)
            {
                result.Add(new PlacedModel(file, Vector3.Zero, slots, isBody));
                continue;
            }
            var dy = headBase.Value - b.Value.Min;
            Console.WriteLine($"  {file}: spans {b.Value.Min:0.###}..{b.Value.Max:0.###} -> lift {dy:0.###}");
            result.Add(new PlacedModel(file, new Vector3(0, dy, 0), slots, isBody));
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
        var jsonList = "[" + string.Join(",", models.Select(m =>
        {
            var slots = string.Join(",", m.Slots.Select(sl =>
                "{" +
                $"\"tex\":{Q(sl.Texture)},\"nrm\":{Q(sl.Normal)},\"mmr\":{Q(sl.Mmr)}," +
                $"\"col\":{(sl.Colour is null ? "null" : $"\"#{sl.Colour.Value.R:X2}{sl.Colour.Value.G:X2}{sl.Colour.Value.B:X2}\"")}" +
                "}"));
            // Extract every UV channel so the viewer's switcher can bind sets three.js drops on import.
            var baseName = Path.GetFileNameWithoutExtension(m.File);
            var uvs = GlbInspector.ExtractUvChannels(Path.Combine(dir, m.File), dir, baseName);
            return $"{{\"file\":\"{m.File}\",\"base\":\"{baseName}\",\"body\":{(m.IsBody ? "true" : "false")},\"uvs\":[{string.Join(",", uvs)}]," +
                   $"\"offset\":[{m.Offset.X:0.#####},{m.Offset.Y:0.#####},{m.Offset.Z:0.#####}]," +
                   $"\"slots\":[{slots}]}}";
        })) + "]";
        static string Q(string? v) => v is null ? "null" : $"\"{v}\"";

        File.WriteAllText(Path.Combine(dir, "models.js"), $"window.PREVIEW_MODELS={jsonList};");
        File.WriteAllText(Path.Combine(dir, "index.html"), ViewerHtml);
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
renderer.outputEncoding=THREE.sRGBEncoding;document.body.appendChild(renderer.domElement);
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
    if(info.body&&o.geometry.attributes.uv2){o.geometry.setAttribute('uv',o.geometry.attributes.uv2);}
    // Keep the switcher available on body meshes for tuning other suits.
    if(info.body&&info.base&&info.uvs&&info.uvs.length){uvMeshes.push({o:o,base:info.base,uvs:info.uvs});}
    const list=Array.isArray(o.material)?o.material:[o.material];
    list.forEach((m,li)=>{if(!m)return;
      const s=slots[matIndex]||slots[0]||{};
      matIndex++;
      if(s.tex){const t=texLoader.load(s.tex,
          ()=>say('  '+s.tex.split('/').pop()+' loaded'),
          undefined,()=>say('  TEX FAIL '+s.tex));
        t.flipY=false;t.encoding=THREE.sRGBEncoding;m.map=t;m.color=new THREE.Color(0xffffff);applied++;}
      else if(s.col){m.map=null;m.color=new THREE.Color(s.col);applied++;}
      else if(!m.map){m.color=new THREE.Color(0x9aa0a8);}
      const n=tex(s.nrm,false); if(n)m.normalMap=n;
      // LEGO minifig plastic is not metal. A metalnessMap with no environment renders the surface
      // pure BLACK, which is what buried the body. Force plastic: metalness 0, MMR only for roughness.
      const r=tex(s.mmr,false);
      m.metalness=0.0;
      if(r){m.roughnessMap=r;m.roughness=1;}else{m.roughness=0.6;}
      // LEGO packs masks into alpha - it is not opacity.
      m.transparent=false;m.alphaTest=0;m.opacity=1;m.depthWrite=true;
      // The body mesh carries a COLOR_0 vertex-colour set (a mask/AO), which GLTFLoader turns into
      // vertexColors=true. That multiplies the albedo - if those colours are dark the whole surface
      // goes black regardless of texture or UV. It is not the display colour, so switch it off.
      if(m.vertexColors){m.vertexColors=false;say('  '+(info.file||'')+': disabled vertexColors');}
      m.needsUpdate=true;});
  });
  say(info.file+': slots='+slots.length+' applied='+applied+' ['+
      slots.map(s=>s.tex?'tex':(s.col?s.col:'-')).join(',')+']');
}
function frameAll(){
  const box=new THREE.Box3().setFromObject(root);if(box.isEmpty())return;
  const size=box.getSize(new THREE.Vector3());const center=box.getCenter(new THREE.Vector3());
  root.position.sub(center);
  const d=Math.max(size.x,size.y,size.z)||1;
  camera.position.set(d*0.55,d*0.3,d*1.7);camera.near=d/100;camera.far=d*40;camera.updateProjectionMatrix();
  controls.target.set(0,0,0);controls.update();
}
function load(m){return new Promise(res=>loader.load(m.file,g=>{dress(g,m);res({m,scene:g.scene});},
  undefined,e=>{document.getElementById('err').textContent='Load error ('+m.file+'): '+(e&&e.message||e);res(null);}));}
Promise.all(models.map(load)).then(loaded=>{
  loaded=loaded.filter(Boolean);
  // Offsets are precomputed in C# from the exported glb skeletons, so the viewer just places them.
  loaded.forEach(x=>{
    const o=x.m.offset;
    if(o&&(o[0]||o[1]||o[2]))x.scene.position.set(o[0],o[1],o[2]);
    root.add(x.scene);
  });
  frameAll();
  const avail=[...new Set(uvMeshes.flatMap(u=>u.uvs))].sort();
  say('UV switch: press '+avail.join('/')+' to change texture UV channel (now: glTF default)');
});
// Live UV-channel switcher: bind a mesh's chosen TEXCOORD set (extracted to .f32 by the exporter)
// as its 'uv' attribute. Three.js only imports channels 0/1, so this is the only way to test 2+.
function applyUv(ch){
  let done=0;
  uvMeshes.forEach(u=>{
    if(u.uvs.indexOf(ch)<0)return;
    fetch(u.base+'_uv'+ch+'.f32').then(r=>r.arrayBuffer()).then(buf=>{
      const arr=new Float32Array(buf);
      u.o.geometry.setAttribute('uv',new THREE.BufferAttribute(arr,2));
      u.o.geometry.attributes.uv.needsUpdate=true;done++;
      say('UV channel = '+ch+' (applied to '+done+' mesh'+(done>1?'es':'')+')');
    }).catch(e=>say('uv'+ch+' load failed: '+e));
  });
}
addEventListener('keydown',e=>{if(e.key>='0'&&e.key<='7')applyUv(parseInt(e.key));});
addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight);});
(function loop(){requestAnimationFrame(loop);controls.update();renderer.render(scene,camera);})();
</script></body></html>
""";
}
