using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

/// <summary>Read-only, bind-pose vehicle assembly for the offline workshop. Never writes donor files.</summary>
internal static class VehicleWorkshopService
{
    internal sealed record Pose(float[] Position, float[] Quaternion, float[] Scale);
    internal sealed record MaterialSlot(int Slot, string Package, string Family, float[]? Color, string[] Parameters);
    internal sealed record Part(string Id, string Label, string Group, string File, string Socket, string Bone,
        bool Editable, Pose Anchor, VehicleComponentTransform Native, VehicleComponentTransform Current,
        IReadOnlyList<MaterialSlot> Materials, string Note)
    {
        public int[] PrimitiveSlots { get; init; } = [];
        public bool CanDisable { get; init; }
        public VehicleLightSettings? Light { get; init; }
        public bool LockScale { get; init; }
        public float[] MarkerDirection { get; init; } = [1, 0, 0];
    }
    internal sealed record Scene(string VehicleId, string Session, string Name, IReadOnlyList<Part> Parts,
        IReadOnlyList<MaterialSlot> NativeMaterials, IReadOnlyList<VehicleComponentTransform> Saved, IReadOnlyList<string> Warnings)
    {
        public IReadOnlyList<VehicleCustomizationService.MaterialChoice> MaterialCatalog { get; init; } = [];
        public IReadOnlyList<VehicleMaterialOverride> MaterialOverrides { get; init; } = [];
        public IReadOnlyList<string> DisabledParts { get; init; } = [];
        public IReadOnlyList<VehicleLightSettings> Lights { get; init; } = [];
        public VehicleRgbColor? AccentColor { get; init; }
        public IReadOnlyList<VehicleLightSurface> LightSurfaces { get; init; } = [];
        public IReadOnlyList<VehicleLightSurfaceService.Surface> LightSurfaceChoices { get; init; } = [];
        public IReadOnlyList<VehicleLightSurfaceService.Role> LightRoles { get; init; } = VehicleLightSurfaceService.Roles;
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly Pose Identity = new([0, 0, 0], [0, 0, 0, 1], [1, 1, 1]);

    // FRotator is yaw(+Z), pitch(-Y), roll(-X); do not treat its fields as XYZ Euler angles.
    internal static Quaternion Rotation(float pitch, float yaw, float roll)
    {
        const float rad = MathF.PI / 180;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * rad) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, -pitch * rad) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -roll * rad));
    }
    internal static Pose Compose(Pose parent, Pose child)
    {
        var pq = Q(parent.Quaternion); var scale = V(parent.Scale);
        var pos = V(parent.Position) + Vector3.Transform(V(child.Position) * scale, pq);
        var q = Quaternion.Normalize(pq * Q(child.Quaternion)); var s = scale * V(child.Scale);
        return new([pos.X, pos.Y, pos.Z], [q.X, q.Y, q.Z, q.W], [s.X, s.Y, s.Z]);
    }
    private static Vector3 V(float[] v) => new(v[0], v[1], v[2]);
    private static Quaternion Q(float[] q) => new(q[0], q[1], q[2], q[3]);
    private static float[] Triple(JToken? t, float fallback = 0) => [t?["X"]?.Value<float>() ?? fallback, t?["Y"]?.Value<float>() ?? fallback, t?["Z"]?.Value<float>() ?? fallback];
    private static string Package(JToken? reference)
    {
        var path = reference?["ObjectPath"]?.ToString() ?? "";
        const string content = "LEGOBatmanLotDK/Content/";
        if (path.StartsWith(content, StringComparison.OrdinalIgnoreCase)) path = "/Game/" + path[content.Length..];
        return path.Split('.')[0];
    }
    private static Pose SocketPose(JToken props)
    {
        var rot = props["RelativeRotation"]; var q = Rotation(rot?["Pitch"]?.Value<float>() ?? 0, rot?["Yaw"]?.Value<float>() ?? 0, rot?["Roll"]?.Value<float>() ?? 0);
        return new(Triple(props["RelativeLocation"]), [q.X, q.Y, q.Z, q.W], Triple(props["RelativeScale"], 1));
    }
    internal static string Group(string name) => name.StartsWith("seat:") ? "Seating" : name.StartsWith("B_") ? "Brake lights" : name.StartsWith("H_") ? "Headlights" : name.StartsWith("R_") ? "Rear lights" : name.StartsWith("C_") ? "Accent lights" : "Attachments";
    internal static string Label(string name) => VehicleLightService.IsSource(name) ? (name.StartsWith("H_") ? "Headlight beam " : "Rear / brake beam ") + (name.Contains("_01_") ? "1" : "2") : name.Replace("_GEN_VARIABLE", "").Replace("_Mesh", "").Replace('_', ' ');

    internal static Scene Create(string directory, VehicleProject project, string folder, string session, CancellationToken cancellation = default, Action<string>? progress = null)
    {
        VehicleProjectService.ValidateIdentity(project);
        Directory.CreateDirectory(folder);
        var settings = AppSettings.Current;
        var overlays = new List<string>();
        string? bodyCache = null;
        if (project.Model is { } model)
        {
            progress?.Invoke("Checking the imported body…");
            var stage = Path.Combine(folder, "Stage", "LEGOBatmanLotDK", "Content");
            SkinnedMeshStageService.BakeMesh(stage, directory, model); overlays.Add(stage);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes("vehicle-preview-v1/" + typeof(MeshExporter).Assembly.FullName));
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var file = Path.Combine(stage, model.MeshPackage[6..] + extension);
                if (File.Exists(file)) { using var stream = File.OpenRead(file); var buffer = new byte[131072]; int count; while ((count = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); } }
            }
            bodyCache = Path.Combine(AppSettings.CacheRoot, "VehicleGeometry-v1", Convert.ToHexString(hash.GetHashAndReset()) + ".glb");
        }
        progress?.Invoke("Reading the native driving rig and sockets…");
        using var provider = ModelPreviewService.MakeProvider(settings.EffectiveGamePaksRoot(), settings.EffectiveUsmapPath()!, overlays);
        var body = provider.LoadPackageObject<USkeletalMesh>(VehicleAssetService.NativeMesh);
        var bones = new Dictionary<string, Pose>(StringComparer.OrdinalIgnoreCase);
        var boneWorld = new List<Pose>();
        for (int i = 0; i < body.ReferenceSkeleton.FinalRefBoneInfo.Length; i++)
        {
            var info = body.ReferenceSkeleton.FinalRefBoneInfo[i]; var p = body.ReferenceSkeleton.FinalRefBonePose[i];
            var local = new Pose([p.Translation.X, p.Translation.Y, p.Translation.Z], [p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W], [p.Scale3D.X, p.Scale3D.Y, p.Scale3D.Z]);
            var world = info.ParentIndex < 0 ? local : Compose(boneWorld[info.ParentIndex], local);
            boneWorld.Add(world); bones.Add(info.Name.Text, world);
        }
        var sockets = new Dictionary<string, (Pose Pose, string Bone)>(StringComparer.OrdinalIgnoreCase);
        foreach (var socket in provider.LoadPackage(VehicleAssetService.NativeSkeleton).GetExports().Where(e => e.ExportType == "SkeletalMeshSocket"))
        {
            var p = JObject.FromObject(socket)["Properties"]!; var bone = p["BoneName"]!.ToString();
            if (bones.TryGetValue(bone, out var bonePose)) sockets.Add(p["SocketName"]!.ToString(), (Compose(bonePose, SocketPose(p)), bone));
        }
        var warnings = new List<string>(); var parts = new List<Part>(); var exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primitiveSlots = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        string Export(string package)
        {
            if (exports.TryGetValue(package, out var existing)) return existing;
            cancellation.ThrowIfCancellationRequested();
            var name = "mesh-" + exports.Count + ".glb";
            var mesh = provider.LoadPackageObject(package);
            primitiveSlots[package] = MeshPrimitiveSlots(mesh);
            if (package == project.Model?.MeshPackage && bodyCache is not null && ValidGlb(bodyCache))
            {
                File.Copy(bodyCache, Path.Combine(folder, name)); exports.Add(package, name); return name;
            }
            progress?.Invoke(package == project.Model?.MeshPackage ? "Preparing body geometry (first preview can take a few minutes)…" : "Reading attachment geometry…");
            var options = new ExporterOptions { MeshFormat = EMeshFormat.Gltf2, LodFormat = ELodFormat.FirstLod, ExportMaterials = false, ExportMorphTargets = false };
            var exporter = mesh switch { USkeletalMesh sk => new MeshExporter(sk, options), UStaticMesh sm => new MeshExporter(sm, options), _ => throw new InvalidDataException("Not a mesh: " + package) };
            var exportDir = Path.Combine(folder, "MeshExport", exports.Count.ToString());
            if (!exporter.TryWriteToDir(new DirectoryInfo(exportDir), out _, out var file)) throw new InvalidDataException("Mesh preview export failed: " + package);
            File.Copy(file, Path.Combine(folder, name));
            if (package == project.Model?.MeshPackage && bodyCache is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bodyCache)!);
                var temporary = bodyCache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(file, temporary); File.Move(temporary, bodyCache, true); TrimGeometryCache(bodyCache); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (FileSystemPathUtil.IsWithinDirectory(exportDir, Path.Combine(folder, "MeshExport"))) Directory.Delete(exportDir, true);
            exports.Add(package, name); return name;
        }
        var nativeMaterials = Slots(body, null, provider);
        var bodyPackage = project.Model?.MeshPackage ?? VehicleAssetService.NativeMesh;
        var bodySlots = project.Model is null ? nativeMaterials : project.Model.Materials.Select(m => new MaterialSlot(m.Slot, m.MaterialPath, "Custom material",
            project.Palette.FirstOrDefault(c => c.Slot == m.Slot) is { } color ? [color.R, color.G, color.B] : null, [])).ToArray();
        parts.Add(new("body", "Vehicle body", "Body", Export(bodyPackage), "", "", false, Identity, new() { Component = "body" }, new() { Component = "body" }, bodySlots,
            "Click a surface to select its material slot. Shared slots affect every piece using that material. Import a body through Setup / body.") { PrimitiveSlots = primitiveSlots[bodyPackage] });
        var blueprint = provider.LoadPackage(VehicleAssetService.NativeBlueprint).GetExports().Select(JObject.FromObject).ToArray();
        var editable = VehicleAssetService.Components(settings.EffectiveExtractedContentRoot()).ToDictionary(c => c.Name);
        foreach (var component in blueprint.Where(e => editable.ContainsKey(e["Name"]!.ToString())))
        {
            var id = component["Name"]!.ToString(); var p = component["Properties"];
            var original = editable[id]; var tags = p?["ComponentTags"]?.Values<string>().ToArray() ?? [];
            // Native BP_PlayableVehicleBase passes tag index 1 to AttachSocketOnChildrenViaTag,
            // whose K2_AttachToComponent uses KeepRelative for location, rotation and scale.
            var socketName = tags.Contains("AutoGenSocketData") ? tags.ElementAtOrDefault(1) ?? "" : p?["AttachSocketName"]?.ToString() ?? "";
            var anchor = Identity; var bone = ""; var note = "";
            if (socketName.Length > 0)
            {
                if (sockets.TryGetValue(socketName, out var socket)) { anchor = socket.Pose; bone = socket.Bone; }
                else { note = "Unresolved native socket; positioning disabled."; warnings.Add(id + ": " + note); }
            }
            var meshPackage = Package(p?["StaticMesh"]); var file = ""; IReadOnlyList<MaterialSlot> slots = [];
            if (meshPackage.Length > 0)
            {
                try { file = Export(meshPackage); slots = Slots(provider.LoadPackageObject(meshPackage), p?["OverrideMaterials"], provider); }
                catch (Exception ex) { note = "Mesh could not be previewed; marker only. " + ex.Message; warnings.Add(id + ": " + note); }
            }
            var source = VehicleLightService.IsSource(id);
            parts.Add(new(id, Label(id), Group(id), file, socketName, bone, note.Length == 0, anchor, original.Transform,
                project.Transforms.FirstOrDefault(t => t.Component == id) ?? original.Transform, slots,
                source ? "Actual light beam. Move / rotate with the gizmo. Rear lights keep their native braking response. Bulb and glow meshes are separate parts; preview brightness is approximate." : note)
                { PrimitiveSlots = primitiveSlots.GetValueOrDefault(meshPackage, []), CanDisable = original.Kind == "StaticMeshComponent", Light = source ? VehicleLightService.Defaults(id) : null });
        }
        foreach (var seat in editable.Values.Where(c => VehicleCustomizationService.IsSeat(c.Name)))
        {
            if (!sockets.TryGetValue(seat.Attachment, out var socket)) { warnings.Add("Missing seat socket: " + seat.Attachment); continue; }
            parts.Add(new(seat.Name, seat.Attachment == "SeatDriver" ? "Driver seat" : "Passenger seat", "Seating", "", seat.Attachment, socket.Bone, true, socket.Pose, seat.Transform,
                project.Transforms.FirstOrDefault(t => t.Component == seat.Name) ?? seat.Transform, [], "Seated figures previews native idle poses at this seat. Capes and flight gear are hidden. Runtime mounting, hand IK and entry/exit need an in-game comparison."));
        }
        var nativeSkeleton = VehicleAssetService.Read(settings.EffectiveExtractedContentRoot(), VehicleAssetService.NativeSkeleton);
        foreach (var socketName in VehicleSocketService.EditableSockets)
        {
            var id = "socket:" + socketName; var socket = sockets[socketName];
            var original = VehicleSocketService.Transform(VehicleSocketService.Socket(nativeSkeleton, socketName), id);
            var launcherMesh = socketName.StartsWith("LauncherGadget_") ? "/Game/Models/Vehicles/VEH_LauncherGadget_01/SK_VEH_LauncherGadget_01" : socketName == "Grapple_01" ? "/Game/Models/Vehicles/VEH_GrappleLauncherGadget_01/SK_VEH_GrappleLauncherGadget_01" : "";
            var file = "";
            if (launcherMesh.Length > 0) { try { file = Export(launcherMesh); } catch (Exception ex) { warnings.Add("Launcher marker only: " + ex.Message); } }
            var effect = VehicleSocketService.EffectSockets.Contains(socketName, StringComparer.Ordinal);
            parts.Add(new(id, VehicleSocketService.Label(socketName), effect ? "Boost & exhaust" : "Weapons & grapple", file, socketName, socket.Bone, true, bones[socket.Bone], original,
                project.Transforms.FirstOrDefault(t => t.Component == id) ?? original, [], effect
                ? "Moves boost and engine start / idle / shutdown effects together. Arrow follows the native exhaust direction; particles are not simulated. Color and effect size stay native for now."
                : launcherMesh.Length > 0
                ? "Move the whole launcher here. Native deployment and aiming remain active. Model preview is the rest pose; test deployed clearance in game."
                : "Fallback firing / VFX reference. This donor normally fires from the animated launcher's own socket; move Rocket launcher 1 or 2 first.") { LockScale = true, MarkerDirection = VehicleSocketService.MarkerDirection(socketName) });
        }
        // Only show a reference marker when there is no resolved, editable source component.
        foreach (var socket in sockets.Where(s => s.Key.Contains("_Light_", StringComparison.Ordinal) && !parts.Any(p => p.Socket == s.Key && p.Light is not null)))
            parts.Add(new("source:" + socket.Key, Label(socket.Key), "Light sources (reference)", "", socket.Key, socket.Value.Bone, false, socket.Value.Pose,
                new() { Component = "source:" + socket.Key }, new() { Component = "source:" + socket.Key }, [], "Light-source reference. Runtime light components are not edited in this pass."));
        progress?.Invoke("Reading material library…");
        var scene = new Scene(project.Id, session, project.DisplayName, parts, nativeMaterials, project.Transforms, warnings)
        { MaterialCatalog = VehicleMaterialCatalogService.Read(settings.EffectiveProjectRoot(), provider), MaterialOverrides = project.MaterialOverrides, DisabledParts = project.DisabledParts, Lights = project.Lights, AccentColor = project.AccentColor,
            LightSurfaces = project.LightSurfaces, LightSurfaceChoices = project.Model is null ? [] : VehicleLightSurfaceService.Analyze(provider.LoadPackageObject<USkeletalMesh>(bodyPackage), project) };
        foreach (var asset in new[] { "three.min.js", "GLTFLoader.js", "OrbitControls.js", "TransformControls.js", "VehicleWorkshop.js", "VehicleRiders.js", "VehicleWorkshop.html" })
            File.WriteAllBytes(Path.Combine(folder, asset == "VehicleWorkshop.html" ? "index.html" : asset), EmbeddedAssets.ReadBytes("preview/" + asset) ?? throw new FileNotFoundException("Missing vehicle viewer asset: " + asset));
        File.WriteAllText(Path.Combine(folder, "scene.js"), "window.VEHICLE_SCENE=" + JsonSerializer.Serialize(scene, Json) + ";");
        return scene;
    }
    private static bool ValidGlb(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream); return stream.Length >= 20 && reader.ReadUInt32() == 0x46546c67 && reader.ReadUInt32() == 2 && reader.ReadUInt32() == stream.Length; }
        catch (IOException) { return false; }
    }
    private static int[] MeshPrimitiveSlots(UObject mesh)
    {
        // GLTF materials are deduplicated by package. Keep original section slots separate so
        // fourteen sections using MI_Black can still have fourteen distinct color overrides.
        if (mesh is USkeletalMesh sk && sk.TryConvert(out var skeletal) && skeletal?.LODs.FirstOrDefault()?.Sections?.Value is { } skSections) return skSections.Where(s => s.NumFaces > 0).Select(s => s.MaterialIndex).ToArray();
        if (mesh is UStaticMesh sm && sm.TryConvert(out var stat) && stat?.LODs.FirstOrDefault()?.Sections?.Value is { } smSections) return smSections.Where(s => s.NumFaces > 0).Select(s => s.MaterialIndex).ToArray();
        return [];
    }
    private static void TrimGeometryCache(string keep)
    {
        // Only disposable files in this tool-owned, versioned geometry cache; never authored cooks.
        var root = Path.Combine(AppSettings.CacheRoot, "VehicleGeometry-v1");
        try
        {
            long retained = new FileInfo(keep).Length;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.glb").Where(f => f.FullName != keep).OrderByDescending(f => f.LastWriteTimeUtc))
            {
                retained += file.Length;
                if (retained > 512L * 1024 * 1024 && FileSystemPathUtil.IsWithinDirectory(file.FullName, root)) { try { file.Delete(); } catch (IOException) { } }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static IReadOnlyList<MaterialSlot> Slots(UObject mesh, JToken? overrides, DefaultFileProvider provider)
    {
        var defaults = ModelPreviewService.MeshSlotMaterials(mesh);
        return Enumerable.Range(0, defaults.Count).Select(i =>
        {
            var material = defaults[i]; var path = Package(overrides?.ElementAtOrDefault(i));
            if (path.Length > 0) { try { material = provider.LoadPackageObject(path); } catch { } }
            path = path.Length > 0 ? path : material?.GetPathName().Split('.')[0] ?? "";
            var family = path.Contains("ShadowImposter", StringComparison.OrdinalIgnoreCase) ? "Shadow helper" : path.Contains("Rubber", StringComparison.OrdinalIgnoreCase) ? "Rubber" : path.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ? "Metal" : path.Contains("Transp", StringComparison.OrdinalIgnoreCase) ? "Transparent" : path.Contains("VehicleLight", StringComparison.OrdinalIgnoreCase) ? "Light glow / bulb" : path.Contains("MI_VEH_", StringComparison.OrdinalIgnoreCase) ? "Decal + emission" : "LEGO plastic";
            var properties = material is null ? null : JObject.FromObject(material)["Properties"];
            var parameters = new[] { "ScalarParameterValues", "VectorParameterValues", "TextureParameterValues" }.SelectMany(key => properties?[key]?.Children() ?? [])
                .Select(p => p["ParameterInfo"]?["Name"]?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
            return new MaterialSlot(i, path, family, null, parameters);
        }).ToArray();
    }
}
