using System.Text.Json;

namespace Batcomputer;

internal static class WeaponModelService
{
    internal const int MaximumSourceLength = 8 * 1024 * 1024;
    internal static WeaponModelRecipe ImportObj(string path, WeaponModelRecipe? existing,
        IReadOnlyList<(int Slot, string Name, string Package)> nativeSlots)
    {
        if (new FileInfo(path).Length > MaximumSourceLength)
            throw new InvalidDataException("OBJ must be smaller than 8 MB.");
        var slots = StaticMeshObjProbeService.InspectObjMaterialSlots(path);
        var recipe = existing?.Clone() ?? new WeaponModelRecipe();
        var previousMaterials = recipe.Materials;
        recipe.SourceName = Path.GetFileName(path); recipe.ObjText = File.ReadAllText(path);
        recipe.Materials = slots.Select(s => new CustomStaticMeshMaterialSlot
        {
            Slot = s.Slot, SourceMaterialName = s.SourceMaterialName, StableSlotName = s.StableSlotName,
            MaterialPath = previousMaterials.FirstOrDefault(m => m.SourceMaterialName == s.SourceMaterialName)?.MaterialPath
                ?? previousMaterials.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.SourceMaterialName) && m.StableSlotName == s.StableSlotName)?.MaterialPath
                ?? nativeSlots.FirstOrDefault(m => m.Slot == s.Slot && !string.IsNullOrWhiteSpace(m.Package)).Package
                ?? nativeSlots.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Package)).Package
                ?? "/Game/Models/Props/Materials/Mi_LEGO_Bake_Katana"
        }).ToList();
        Validate(recipe);
        return recipe;
    }

    internal static void Validate(WeaponModelRecipe r)
    {
        if (string.IsNullOrWhiteSpace(r.ObjText) || r.ObjText.Length > MaximumSourceLength)
            throw new InvalidDataException("Import an OBJ smaller than 8 MB.");
        if (!float.IsFinite(r.Scale) || r.Scale < 0.001f || r.Scale > 1000)
            throw new InvalidDataException("Model scale must be between 0.001 and 1000.");
        if (new[] { r.X, r.Y, r.Z, r.Pitch, r.Yaw, r.Roll }.Any(v => !float.IsFinite(v)) ||
            new[] { r.Pitch, r.Yaw, r.Roll }.Any(v => Math.Abs(v) > 360))
            throw new InvalidDataException("Invalid weapon transform.");
        if (r.Materials.Count == 0 || r.Materials.Any(m => string.IsNullOrWhiteSpace(m.MaterialPath) ||
            !ExtractedPackagePathService.IsContentPackagePath(m.MaterialPath) || m.MaterialPath.Contains("..")))
            throw new InvalidDataException("Assign a cooked material package to every model slot.");
        if (r.Materials.OrderBy(m => m.Slot).Where((m, index) => m.Slot != index).Any())
            throw new InvalidDataException("Model material slots must be unique, contiguous and start at zero.");
    }

    internal static string WriteSource(WeaponModelRecipe r, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "weapon.obj");
        File.WriteAllText(path, r.ObjText);
        return path;
    }

    internal static void Preview(WeaponModelRecipe r, string folder, bool original, bool custom, int revision,
        ModelPreviewService.ItemMaterialPreviewSession? materials = null, bool editable = true)
    {
        Validate(r);
        var obj = WriteSource(r, folder);
        var file = WritePreviewGeometry(r, folder, obj);
        var own = materials is null;
        materials ??= new(folder);
        try
        {
            var slots = r.Materials.OrderBy(m => m.Slot).Select(m => materials.Resolve(m.MaterialPath)).ToArray();
            AtomicFileUtil.WriteAllText(Path.Combine(folder, "weapon.json"), JsonSerializer.Serialize(new { file, original, custom, revision, slots, editable,
                transform = ItemWorkshopTransform.FromRecipe(r) }));
        }
        finally { if (own) materials.Dispose(); }
    }

    internal static string WritePreviewGeometry(WeaponModelRecipe r, string folder, string? obj = null)
    {
        // Export centered, unit-scale geometry once. Numeric edits and gizmos share a scene transform;
        // Bake still uses the original recipe and the exact same centering/basis conversion.
        var identity = r.ObjText + JsonSerializer.Serialize(r.Materials.Select(m => new { m.Slot, m.SourceMaterialName, m.StableSlotName }));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        var file = "custom-" + hash + ".glb";
        if (!File.Exists(Path.Combine(folder, file)))
            StaticMeshObjProbeService.WritePreviewGlb(obj ?? WriteSource(r, folder), Path.Combine(folder, file), 1, 0, 0, 0, materialSlots: r.Materials);
        return file;
    }

    internal static IReadOnlyList<(int Slot, string Name, string Package)> NativeSlots(string folder)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "models.json")));
        var slots = json.RootElement[0].GetProperty("slots");
        return slots.EnumerateArray().Select((s, i) => (i, $"Material {i}", s.GetProperty("material").GetString() is { } p ? UnrealPathUtil.NormalizePackagePath(p) : "")).ToList();
    }

    internal static void PreviewNative(string folder, IReadOnlyList<NativeHeldMaterialOverride> overrides,
        bool visible, int revision, ModelPreviewService.ItemMaterialPreviewSession materials)
    {
        var originalSlots = overrides.ToDictionary(m => m.Slot.ToString(), m => materials.Resolve(m.Package));
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "weapon.json"), JsonSerializer.Serialize(new { original = visible, custom = false, revision, originalSlots }));
    }

    internal static void Bake(WeaponModelRecipe r, string extracted, string mappings, string content, string package)
    {
        Validate(r);
        var scratch = Path.Combine(Path.GetTempPath(), "Batcomputer-weapon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = WriteSource(r, scratch);
            var result = new StaticMeshObjProbeService().CreateObjHeadProbe(new()
            {
                ExtractedContentRoot = extracted, UsmapPath = mappings, OutputContentRoot = content,
                OutputPackagePath = package, ObjPath = source, Scale = r.Scale, OffsetX = r.X, OffsetY = r.Y, OffsetZ = r.Z,
                RotationPitch = r.Pitch, RotationYaw = r.Yaw, RotationRoll = r.Roll, MaterialSlots = r.Materials,
            });
            if (result.Status != "created") throw new InvalidDataException(result.Error ?? "Weapon bake failed.");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    internal static string CreateViewer(string referencePackage, IReadOnlyList<NativeHeldMaterialOverride>? nativeMaterials = null, string? outputDirectory = null)
    {
        var settings = AppSettings.Current;
        var objectPath = referencePackage.Contains('.') ? referencePackage : referencePackage + "." + UnrealPathUtil.AssetName(referencePackage);
        var folder = ModelPreviewService.BuildItemPreview(objectPath, outputDirectory);
        if (!Directory.EnumerateFiles(Path.Combine(folder, "export"), "*.glb", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("The reference weapon could not be exported. Choose an available native static mesh.");
        if (nativeMaterials is { Count: > 0 })
        {
            using var materials = new ModelPreviewService.ItemMaterialPreviewSession(folder);
            var models = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "models.json")))!.AsArray();
            var slots = models[0]!["slots"]!.AsArray();
            foreach (var material in nativeMaterials.Where(m => m.Slot >= 0 && m.Slot < slots.Count))
                slots[material.Slot] = System.Text.Json.Nodes.JsonNode.Parse(materials.Resolve(material.Package).GetRawText());
            AtomicFileUtil.WriteAllText(Path.Combine(folder, "models.json"), models.ToJsonString());
            AtomicFileUtil.WriteAllText(Path.Combine(folder, "models.js"), "window.PREVIEW_MODELS=" + models.ToJsonString() + ";");
        }
        File.WriteAllText(Path.Combine(folder, "index.html"), Viewer);
        File.WriteAllBytes(Path.Combine(folder, "ItemWorkshopViewer.js"), EmbeddedAssets.ReadBytes("preview/ItemWorkshopViewer.js")
            ?? throw new FileNotFoundException("Item workshop viewer is missing."));
        File.WriteAllBytes(Path.Combine(folder, "ItemTransformMath.js"), EmbeddedAssets.ReadBytes("preview/ItemTransformMath.js")
            ?? throw new FileNotFoundException("Item transform math is missing."));
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "weapon.json"), "{\"original\":true,\"custom\":true,\"revision\":0}");
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "effects.json"), "{\"effects\":[],\"selected\":-1,\"revision\":0}");
        return folder;
    }

    private const string Viewer = """
<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;background:#303944;color:#e8edf5;font:13px 'Segoe UI',sans-serif;overflow:hidden}canvas{display:block}#bar{position:absolute;z-index:2;top:14px;left:14px;right:14px;display:flex;gap:8px;align-items:center;flex-wrap:wrap;padding:10px;background:#242a34ec;border:1px solid #515d70;border-radius:10px;max-height:40vh;overflow:auto}button,select{padding:7px 10px;background:#343e4c;color:#eef3fa;border:1px solid #647084;border-radius:5px}button:hover,button.active{border-color:#ffe344}button.active{color:#ffe344}button:disabled{opacity:.45}input{accent-color:#ffe344}#error{position:absolute;left:16px;right:16px;color:#ffc376;pointer-events:none}#help{position:absolute;bottom:12px;left:16px;right:16px;color:#b7c7dd;pointer-events:none;background:#303944bb}#mode{color:#ffe344;font-weight:600;margin-right:auto}label{display:flex;gap:6px;align-items:center}#transformTools{flex-basis:100%;display:flex;gap:6px;flex-wrap:wrap;align-items:center}#transformTools[hidden]{display:none}#readout{flex-basis:100%;color:#b7c7dd}
</style></head><body><div id="bar"><span id="mode">Item preview</span><button id="frame" title="Frame visible models (F)">Frame · F</button><select id="view" aria-label="Camera view"><option value="perspective">Perspective</option><option value="front">Front</option><option value="side">Side</option><option value="top">Top</option></select><label><input id="grid" type="checkbox" checked>Grid</label><label><input id="axes" type="checkbox" checked>Axes</label><label>Light<input id="exposure" aria-label="Preview exposure" type="range" min="0.3" max="2" step="0.05" value="1" style="width:75px"></label></div><div id="error" role="status"></div><div id="help">Drag to orbit · right-drag to pan · scroll to zoom. Preview only; game lighting and effects can differ.</div>
<style>#workshopHud{position:absolute;bottom:12px;left:16px;right:16px;display:flex;flex-direction:column;gap:4px;pointer-events:none;color:#c4d1e2;background:#303944bb;font-size:12px}#readout,#dimensions,#help{position:static;font-size:12px;pointer-events:none}#bar{max-height:36vh}</style>
<script src="three.min.js"></script><script src="GLTFLoader.js"></script><script src="OrbitControls.js"></script><script src="TransformControls.js"></script><script src="CharacterNormals.js"></script><script src="CharacterSurface.js"></script><script src="models.js"></script><script src="ItemTransformMath.js"></script><script src="ItemWorkshopViewer.js"></script><script>
function tick(){requestAnimationFrame(tick);const t=performance.now()*.001;effectRoot.traverse(o=>{if(!o.userData.particle)return;const {i,shape}=o.userData,p=(t*.5+i/14)%1,a=i*2.399;
o.position.set(shape==='trail'?-.22*p:Math.cos(a)*.035*p,shape==='trail'?Math.sin(p*3)*.015:p*.16,Math.sin(a)*.025*p);o.material.opacity=(1-p)*(shape==='cloud'?.25:.65);});controls.update();renderer.render(scene,camera);}tick();
</script></body></html>
""";
}
