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
    /// <summary>Mount key for LotDK's unencrypted paks.</summary>
    private const string ZeroAes = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly AsyncLocal<Action<string>?> PreviewDiagnosticSink = new();

    private sealed class PreviewDiagnosticScope(Action<string>? sink) : IDisposable
    {
        private readonly Action<string>? _previous = PreviewDiagnosticSink.Value;

        public void Dispose()
        {
            PreviewDiagnosticSink.Value = _previous;
        }

        public void Start()
        {
            PreviewDiagnosticSink.Value = sink;
        }
    }

    private static void PreviewTrace(string message)
    {
        Console.WriteLine(message);
        PreviewDiagnosticSink.Value?.Invoke(message);
    }

    private const string GameContentFilePrefix = "LEGOBatmanLotDK/Content/";

    private static DefaultFileProvider MakeProvider(
        string paksDir,
        string usmapPath,
        IEnumerable<string>? looseContentRoots = null)
    {
        // Asset paths use mixed casing, so preview lookups stay case-insensitive.
        var provider = new DefaultFileProvider(
            paksDir, BaseGamePakSource.ShippedContainerSearchOption,
            versions: new VersionContainer(EGame.GAME_UE5_6),
            pathComparer: StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(ZeroAes));
        AddLooseContentOverlays(provider, looseContentRoots);
        return provider;
    }

    private static void AddLooseContentOverlays(DefaultFileProvider provider, IEnumerable<string>? contentRoots)
    {
        if (contentRoots is null)
        {
            return;
        }

        var roots = contentRoots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < roots.Count; index++)
        {
            var root = roots[index];
            try
            {
                using var loose = new DefaultFileProvider(
                    root,
                    SearchOption.AllDirectories,
                    new VersionContainer(EGame.GAME_UE5_6),
                    StringComparer.OrdinalIgnoreCase);
                loose.Initialize();
                if (loose.LooseFileCount == 0)
                {
                    continue;
                }

                var files = loose.Files.ToDictionary(
                    pair => GameContentFilePrefix + pair.Key.TrimStart('/', '\\').Replace('\\', '/'),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                provider.Files.AddFiles(files, long.MaxValue - index);
                Console.WriteLine($"  preview overlay: {loose.LooseFileCount} asset(s) from {root}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  preview overlay skipped '{root}': {ex.Message.Split('\n')[0]}");
            }
        }
    }

    /// <summary>
    /// Preview scratch lives in <c>Generated\Preview</c> beside the exe - not the system temp folder -
    /// so the tool stays portable and everything it writes is in one place the user controls.
    /// Each build gets its own folder. Older builds are removed by default, while the setting can
    /// be switched off when an author needs to inspect the generated GLB and texture files.
    /// </summary>
    private static string NewPreviewRoot()
    {
        var root = Path.Combine(
            AppSettings.GeneratedRootFor(AppSettings.Current.EffectiveProjectRoot()), "Preview");
        Directory.CreateDirectory(root);
        if (AppSettings.Current.AutoCleanPreviewFiles)
        {
            CleanPreviewRoot(root);
        }
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
    /// The bare head piece. Also runtime-assigned, and separate from both the face print
    /// (SK_LEGOface) and the cowl (the BP's "Head" component), which layer on top of it.
    /// </summary>
    private const string DefaultHeadMesh = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig_Head.SK_LEGOfig_Minifig_Head";

    // Neutral face profiles are safe to show; expression playback remains intentionally disabled.
    private const bool IncludeNeutralFacePreview = true;

    /// <summary>Project-specific data layered over a base character preview.</summary>
    public sealed class CharacterPreviewOptions
    {
        public IReadOnlyCollection<string> HiddenComponents { get; init; } = Array.Empty<string>();
        public IReadOnlyCollection<PreviewAdditionalPart> AdditionalParts { get; init; } = Array.Empty<PreviewAdditionalPart>();
        public IReadOnlyCollection<PreviewMaterialOverride> MaterialOverrides { get; init; } = Array.Empty<PreviewMaterialOverride>();
        public IReadOnlyCollection<SavedPreviewPartPlacement> PlacementOverrides { get; init; } = Array.Empty<SavedPreviewPartPlacement>();
        public string? ViewerLayoutKey { get; init; }
        public string? ViewerLayoutProjectRoot { get; init; }
        public string? StagedPlayablePath { get; init; }
        public bool AllowPartMover { get; init; } = true;
        public IReadOnlyCollection<PreviewRedBrickTint> RedBrickTints { get; init; } = Array.Empty<PreviewRedBrickTint>();
    }

    /// <summary>A base-game palette made available to an eligible, read-only playable preview.</summary>
    public sealed record PreviewRedBrickTint(
        string DisplayName,
        string PrimaryHex,
        string SecondaryHex,
        string TertiaryHex);

    /// <summary>A component added by a saved part graft, without needing to mount the staged package.</summary>
    public sealed record PreviewAdditionalPart(
        string ComponentName,
        string MeshPath,
        bool IsStaticAttachment,
        string? ParentComponent = null,
        string? AttachSocket = null,
        IReadOnlyList<string>? MaterialPaths = null,
        bool ReplaceExisting = true,
        string? SourceObjPath = null,
        float SourceObjScale = 1f,
        Vector3? SourceObjOffset = null,
        Vector3? SourceObjRotation = null,
        string? CustomMeshId = null,
        string? DisplayName = null);

    /// <summary>An explicit material assignment stored on a suit project.</summary>
    public sealed record PreviewMaterialFallback(
        string ParentMaterialPath,
        IReadOnlyDictionary<string, string> TextureOverrides,
        IReadOnlyDictionary<string, string> SourceTextureOverrides,
        IReadOnlyDictionary<string, Color> ColourOverrides);

    public sealed record PreviewMaterialOverride(
        string ComponentName,
        int Slot,
        string MaterialPath,
        PreviewMaterialFallback? LocalFallback = null);

    /// <summary>
    /// A mesh to preview. <paramref name="AttachToHead"/> marks the local-authored head attachments
    /// (face, cowl) that must be aligned onto the head piece; everything else is world-authored and
    /// renders where it already sits.
    /// </summary>
    private readonly record struct PreviewPart(
        string MeshPath, bool AttachToHead, bool IsHeadPiece = false, FPackageIndex[]? Overrides = null,
        bool IsStaticAttachment = false, string ComponentName = "", PreviewComponentTransform? Transform = null,
        BlueprintAttachment? Attachment = null, Vector3? AttachmentOffset = null,
        bool UsesRuntimeSocketCalibration = false,
        IReadOnlyDictionary<int, string>? MaterialPaths = null,
        IReadOnlyDictionary<int, PreviewMaterialFallback>? MaterialFallbacks = null,
        SavedPreviewPartPlacement? Adjustment = null,
        string? SourceObjPath = null,
        float SourceObjScale = 1f,
        Vector3? SourceObjOffset = null,
        Vector3? SourceObjRotation = null,
        string? CustomMeshId = null,
        string? DisplayName = null);

    /// <summary>
    /// The SCS is the Blueprint's real component hierarchy. Component templates themselves often
    /// omit AttachParent/AttachSocketName after cooking, so this data is required to place a part.
    /// </summary>
    private sealed record BlueprintAttachment(string? ParentName, string? SocketName);

    private sealed record AttachmentPlacement(
        Vector3 Offset,
        PreviewComponentTransform? SocketTransform = null,
        bool UsesRuntimeCalibration = false,
        string? ProfileName = null);

    /// <summary>
    /// The final state of one visual component after applying the selected Blueprint over each of
    /// its authored character/archetype parents. Cooked child templates commonly contain only a
    /// material override, so resolving a whole component at once would lose the inherited mesh.
    /// </summary>
    private sealed record ResolvedBlueprintComponent(
        string Key,
        string? MeshPath,
        FPackageIndex[]? Overrides,
        bool IsStaticAttachment,
        PreviewComponentTransform? Transform,
        BlueprintAttachment? Attachment,
        bool Hidden,
        string SourcePackage,
        string? HeadReferenceBodyPath = null);

    private sealed record PreviewComponentTransform(Vector3 Translation, System.Numerics.Quaternion Rotation, Vector3 Scale)
    {
        public bool IsIdentity =>
            Translation.LengthSquared() < 0.0000001f
            && Math.Abs(Rotation.X) < 0.000001f
            && Math.Abs(Rotation.Y) < 0.000001f
            && Math.Abs(Rotation.Z) < 0.000001f
            && Math.Abs(Rotation.W - 1f) < 0.000001f
            && Vector3.DistanceSquared(Scale, Vector3.One) < 0.0000001f;
    }

    /// <summary>
    /// Applies a Blueprint component transform to geometry bounds using the same scale, rotation,
    /// translation order as three.js. Static hair used to be centred from untransformed bounds and
    /// then had this transform applied in the viewer, so a non-zero yaw or relative location could
    /// pull the finished part away from the head.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) TransformBounds(
        (Vector3 Min, Vector3 Max) bounds, PreviewComponentTransform? transform)
    {
        if (transform is null)
        {
            return bounds;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 1; y++)
        for (var z = 0; z <= 1; z++)
        {
            var point = new Vector3(
                x == 0 ? bounds.Min.X : bounds.Max.X,
                y == 0 ? bounds.Min.Y : bounds.Max.Y,
                z == 0 ? bounds.Min.Z : bounds.Max.Z);
            point *= transform.Scale;
            point = Vector3.Transform(point, transform.Rotation) + transform.Translation;
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    /// <summary>
    /// World position of a bone in the UE reference skeleton, converted to the exported glTF's space.
    ///
    /// Why the UE skeleton and not the glTF nodes: at bind pose the skinning cancels
    /// (bone.matrixWorld * inverseBind = identity), so CUE4Parse's exported bone nodes are NOT
    /// spatially aligned with the mesh - they put this body's head at z=-1.168 while the geometry
    /// stands 0..1.388 along +Y. The reference skeleton is the real, consistent source: a clean Z-up
    /// spine. The exporter writes geometry Y-up in metres, so the conversion is Z-up -> Y-up / 100.
    /// </summary>
    private static PreviewComponentTransform? BoneWorldTransformInGltfSpace(USkeletalMesh mesh, string boneName)
    {
        if (!mesh.TryConvert(out var converted))
        {
            return null;
        }
        var bones = converted.RefSkeleton;
        var world = new PreviewComponentTransform?[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            var local = new PreviewComponentTransform(
                new Vector3(b.Position.X / 100f, b.Position.Z / 100f, -b.Position.Y / 100f),
                UeQuaternionToGltf(new Quaternion(b.Orientation.X, b.Orientation.Y, b.Orientation.Z, b.Orientation.W)),
                Vector3.One);
            world[i] = b.ParentIndex >= 0 ? ComposeTransforms(world[b.ParentIndex], local) ?? local : local;

            if (string.Equals(b.Name.Text, boneName, StringComparison.OrdinalIgnoreCase))
            {
                return world[i];
            }
        }
        return null;
    }

    private static Vector3? BoneWorldInGltfSpace(USkeletalMesh mesh, string boneName) =>
        BoneWorldTransformInGltfSpace(mesh, boneName)?.Translation;

    /// <summary>
    /// Component name -> body bone the attachment hangs off. Body-skinned parts (body, cape) carry the
    /// full skeleton and need no attach bone. Extend this as more attachment slots are supported.
    /// </summary>
    private static string? AttachBoneFor(string componentName, string? socketName = null)
    {
        // Prefer the Blueprint's explicit attachment relationship over a component-name guess.
        // An explicit SCS socket is stronger evidence than a component name. Several long-hair
        // components are named Hair but attach at Spine_02_Socket, so treating every "Hair" as a
        // head piece centres ponytails and back hair on the skull.
        if (!string.IsNullOrWhiteSpace(socketName))
        {
            return socketName.Contains("Head", StringComparison.OrdinalIgnoreCase)
                   || socketName.Contains("Face", StringComparison.OrdinalIgnoreCase)
                ? "Head_Attach_01"
                : null;
        }

        return componentName switch
        {
            var n when n.StartsWith("Face", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
            var n when n.StartsWith("Head", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
            var n when n.StartsWith("Hair", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
            var n when n.StartsWith("Hat", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
            var n when n.StartsWith("Helmet", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
            _ => null,
        };
    }

    private static PreviewComponentTransform? ReadComponentTransform(UObject component)
    {
        var loc = component.GetOrDefault<FVector>("RelativeLocation");
        var rot = component.GetOrDefault<FRotator>("RelativeRotation");
        var scale = component.GetOrDefault<FVector>("RelativeScale3D");

        var translation = new Vector3(loc.X / 100f, loc.Z / 100f, -loc.Y / 100f);
        var scaleVector = Math.Abs(scale.X) < 0.000001f
                          && Math.Abs(scale.Y) < 0.000001f
                          && Math.Abs(scale.Z) < 0.000001f
            ? Vector3.One
            : new Vector3(scale.X, scale.Z, scale.Y);
        var rotation = UeRotatorToGltf(rot);
        var transform = new PreviewComponentTransform(translation, rotation, scaleVector);
        return transform.IsIdentity ? null : transform;
    }

    private static System.Numerics.Quaternion UeRotatorToGltf(FRotator rot)
    {
        if (Math.Abs(rot.Pitch) < 0.000001f
            && Math.Abs(rot.Yaw) < 0.000001f
            && Math.Abs(rot.Roll) < 0.000001f)
        {
            return System.Numerics.Quaternion.Identity;
        }

        static float Rad(float deg) => deg * (MathF.PI / 180f);
        // Unreal rotators are applied as yaw(Z), pitch(Y), roll(X). Convert that UE-space
        // quaternion into CUE4Parse's glTF basis: (X,Y,Z)UE -> (X,Z,-Y)glTF.
        var yaw = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Rad(rot.Yaw));
        var pitch = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitY, Rad(rot.Pitch));
        var roll = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, Rad(rot.Roll));
        var ue = System.Numerics.Quaternion.Normalize(yaw * pitch * roll);
        var basis = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f);
        return System.Numerics.Quaternion.Normalize(basis * ue * System.Numerics.Quaternion.Inverse(basis));
    }

    private static System.Numerics.Quaternion UeQuaternionToGltf(System.Numerics.Quaternion ue)
    {
        if (ue.LengthSquared() < 0.000001f)
        {
            return System.Numerics.Quaternion.Identity;
        }

        var basis = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f);
        return System.Numerics.Quaternion.Normalize(
            basis * System.Numerics.Quaternion.Normalize(ue) * System.Numerics.Quaternion.Inverse(basis));
    }

    private static PreviewComponentTransform? ComposeTransforms(
        PreviewComponentTransform? parent,
        PreviewComponentTransform? child)
    {
        if (parent is null)
        {
            return child;
        }
        if (child is null)
        {
            return parent;
        }

        var childTranslation = child.Translation * parent.Scale;
        var translation = parent.Translation + Vector3.Transform(childTranslation, parent.Rotation);
        var rotation = System.Numerics.Quaternion.Normalize(parent.Rotation * child.Rotation);
        var scale = parent.Scale * child.Scale;
        return new PreviewComponentTransform(translation, rotation, scale);
    }

    private static PreviewComponentTransform ToGltfTransform(RuntimeSocketProfileService.SocketTransform transform) => new(
        new Vector3(transform.TranslationUe.X / 100f, transform.TranslationUe.Z / 100f, -transform.TranslationUe.Y / 100f),
        UeQuaternionToGltf(transform.RotationUe),
        new Vector3(transform.ScaleUe.X, transform.ScaleUe.Z, transform.ScaleUe.Y));

    private static Dictionary<string, BlueprintAttachment> ReadBlueprintAttachments(IEnumerable<UObject> exports)
    {
        var result = new Dictionary<string, BlueprintAttachment>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in exports.Where(e => e.ExportType.Contains("SCS_Node", StringComparison.OrdinalIgnoreCase)))
        {
            static string? NameValue(UObject o, string property)
            {
                var value = o.GetOrDefault<FName>(property).Text;
                return string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : value;
            }

            var attachment = new BlueprintAttachment(
                NameValue(node, "ParentComponentOrVariableName"),
                NameValue(node, "AttachToName"));
            var templateName = node.GetOrDefault<FPackageIndex>("ComponentTemplate")?.ResolvedObject?.Name.Text;
            var variableName = NameValue(node, "InternalVariableName");
            if (!string.IsNullOrWhiteSpace(templateName))
            {
                result[templateName] = attachment;
            }
            if (!string.IsNullOrWhiteSpace(variableName))
            {
                result[variableName] = attachment;
            }
        }
        return result;
    }

    private static BlueprintAttachment? AttachmentFor(
        UObject component, IReadOnlyDictionary<string, BlueprintAttachment> scsAttachments)
    {
        if (scsAttachments.TryGetValue(component.Name, out var fromScs))
        {
            return fromScs;
        }
        if (scsAttachments.TryGetValue(ComponentFromSlotKey(component.Name), out fromScs))
        {
            return fromScs;
        }

        // Inherited/native templates may not have an SCS node in this package, but their cooked
        // component data can still retain the equivalent fields.
        var parent = component.GetOrDefault<FPackageIndex>("AttachParent")?.ResolvedObject?.Name.Text;
        var socket = component.GetOrDefault<FName>("AttachSocketName").Text;
        parent = string.IsNullOrWhiteSpace(parent) || parent.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? null
            : parent;
        socket = string.IsNullOrWhiteSpace(socket) || socket.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? null
            : socket;
        return parent is null && socket is null ? null : new BlueprintAttachment(parent, socket);
    }

    private static bool HasProperty(UObject component, string property) =>
        component.Properties.Any(tag => tag.Name.Text.Equals(property, StringComparison.OrdinalIgnoreCase));

    private static bool TryAttachmentFor(
        UObject component,
        IReadOnlyDictionary<string, BlueprintAttachment> scsAttachments,
        out BlueprintAttachment? attachment)
    {
        if (scsAttachments.TryGetValue(component.Name, out attachment) ||
            scsAttachments.TryGetValue(ComponentFromSlotKey(component.Name), out attachment))
        {
            return true;
        }

        if (HasProperty(component, "AttachParent") || HasProperty(component, "AttachSocketName"))
        {
            attachment = AttachmentFor(component, scsAttachments);
            return true;
        }

        attachment = null;
        return false;
    }

    private static bool? ReadComponentHidden(UObject component)
    {
        var hasVisibility = false;
        var hidden = false;
        if (HasProperty(component, "bHidden") && component.GetOrDefault<bool>("bHidden"))
        {
            hasVisibility = true;
            hidden = true;
        }
        else if (HasProperty(component, "bHidden"))
        {
            hasVisibility = true;
        }
        if (HasProperty(component, "bHiddenInGame") && component.GetOrDefault<bool>("bHiddenInGame"))
        {
            hasVisibility = true;
            hidden = true;
        }
        else if (HasProperty(component, "bHiddenInGame"))
        {
            hasVisibility = true;
        }
        if (HasProperty(component, "bVisible"))
        {
            hasVisibility = true;
            hidden |= !component.GetOrDefault<bool>("bVisible");
        }

        return hasVisibility ? hidden : null;
    }

    private static bool HasComponentTransform(UObject component) =>
        HasProperty(component, "RelativeLocation") ||
        HasProperty(component, "RelativeRotation") ||
        HasProperty(component, "RelativeScale3D");

    private static string? MeshPathFor(UObject component) =>
        (component.GetOrDefault<FPackageIndex>("SkeletalMeshAsset")
         ?? component.GetOrDefault<FPackageIndex>("SkeletalMesh")
         ?? component.GetOrDefault<FPackageIndex>("StaticMesh"))?.ResolvedObject?.GetPathName();

    private static bool IsCharacterMeshComponent(UObject component) =>
        component.Name.Contains("CharacterMesh", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ImportedPackageNames(object package)
    {
        if (package is CUE4Parse.UE4.Assets.IoPackage io)
        {
            return io.ImportedPackages.Value
                .Where(imported => imported is not null)
                .Select(imported => imported!.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));
        }

        return Array.Empty<string>();
    }

    private static bool IsVisualBlueprintPackage(string packagePath)
    {
        var assetName = Path.GetFileName(packagePath);
        return assetName.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) &&
               !assetName.Contains("Component", StringComparison.OrdinalIgnoreCase);
    }

    private static int VisualBlueprintParentPriority(string packagePath)
    {
        var assetName = Path.GetFileName(packagePath);
        if (assetName.StartsWith("BP_CAT_Archetype_", StringComparison.OrdinalIgnoreCase))
        {
            // A playable BP inherits character-specific body overrides from its CAT archetype.
            return 0;
        }
        if (packagePath.Contains("/BP_Master/", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return 1;
    }

    /// <summary>
    /// Returns the selected Blueprint followed by the character Blueprint parents that actually
    /// contribute component templates. The package dependency list is the cooked representation of
    /// that inheritance chain: a Robin playable references its CAT archetype, which in turn
    /// references BP_Playable. We deliberately inspect only visual Blueprints, never a generic mesh
    /// fallback, so a smallfig, creature, or custom mod body remains the one its BP selected.
    /// </summary>
    private static IReadOnlyList<string> ResolveVisualBlueprintSourcePaths(
        DefaultFileProvider provider, string selectedBpPath)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !visited.Add(packagePath))
            {
                return;
            }

            result.Add(packagePath);
            try
            {
                var package = provider.LoadPackage(packagePath);
                foreach (var parent in ImportedPackageNames(package)
                             .Where(IsVisualBlueprintPackage)
                             .OrderBy(VisualBlueprintParentPriority)
                             .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    Visit(parent);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  could not read visual BP parent {packagePath}: {ex.Message.Split('\n')[0]}");
            }
        }

        Visit(selectedBpPath);
        return result;
    }

    /// <summary>
    /// Merges visual component template properties from parent to child. This mirrors Unreal's
    /// Blueprint inheritance closely enough for cooked character assets: a child with only a body
    /// material preserves its parent mesh, while a child with a new mesh, transform, or visibility
    /// setting replaces only that aspect of the inherited component.
    /// </summary>
    private static IReadOnlyList<ResolvedBlueprintComponent> ResolveVisualBlueprintComponents(
        DefaultFileProvider provider, string selectedBpPath)
    {
        var resolved = new Dictionary<string, ResolvedBlueprintComponent>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = ResolveVisualBlueprintSourcePaths(provider, selectedBpPath);

        // Resolve root-to-leaf so the selected playable/cutscene BP is authoritative.
        foreach (var sourcePath in sourcePaths.Reverse())
        {
            try
            {
                var package = provider.LoadPackage(sourcePath);
                var scsAttachments = ReadBlueprintAttachments(package.GetExports());
                foreach (var component in package.GetExports())
                {
                    var isSkeletal = component.ExportType.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase);
                    var isStatic = component.ExportType.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
                    if (!isSkeletal && !isStatic)
                    {
                        continue;
                    }

                    var isBody = IsCharacterMeshComponent(component);
                    var key = isBody ? "CharacterMesh0" : ComponentFromSlotKey(component.Name);
                    var meshPath = MeshPathFor(component);
                    var hasMesh = !string.IsNullOrWhiteSpace(meshPath);
                    var hasOverrides = HasProperty(component, "OverrideMaterials");
                    var overrides = component.GetOrDefault<FPackageIndex[]>("OverrideMaterials");
                    var hasTransform = HasComponentTransform(component);
                    var transform = hasTransform ? ReadComponentTransform(component) : null;
                    var hasAttachment = TryAttachmentFor(component, scsAttachments, out var attachment);
                    var hidden = ReadComponentHidden(component);

                    if (resolved.TryGetValue(key, out var inherited))
                    {
                        var referenceBody = inherited.HeadReferenceBodyPath;
                        if (isBody && hasMesh && !string.Equals(meshPath, inherited.MeshPath, StringComparison.OrdinalIgnoreCase))
                        {
                            // The previous body is the rig the shared native head was authored against.
                            referenceBody = inherited.MeshPath ?? referenceBody;
                        }

                        resolved[key] = inherited with
                        {
                            MeshPath = hasMesh ? meshPath : inherited.MeshPath,
                            Overrides = hasOverrides ? overrides : inherited.Overrides,
                            IsStaticAttachment = hasMesh ? isStatic : inherited.IsStaticAttachment,
                            Transform = hasTransform ? transform : inherited.Transform,
                            Attachment = hasAttachment ? attachment : inherited.Attachment,
                            Hidden = hidden ?? inherited.Hidden,
                            SourcePackage = hasMesh ? sourcePath : inherited.SourcePackage,
                            HeadReferenceBodyPath = referenceBody,
                        };
                    }
                    else
                    {
                        resolved[key] = new ResolvedBlueprintComponent(
                            key,
                            hasMesh ? meshPath : null,
                            hasOverrides ? overrides : null,
                            isStatic,
                            transform,
                            attachment,
                            hidden ?? false,
                            sourcePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skipping unreadable visual BP {sourcePath}: {ex.Message.Split('\n')[0]}");
            }
        }

        return resolved.Values
            .OrderBy(component => !component.Key.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static AttachmentPlacement? ResolveBodyAttachmentPlacement(
        RuntimeSocketProfileService.ProfileSet socketProfiles,
        string? bodyMeshPath,
        USkeletalMesh? bodyMesh,
        BlueprintAttachment? attachment,
        string componentName,
        string meshPath)
    {
        if (bodyMesh is null)
        {
            return null;
        }

        var socket = attachment?.SocketName;
        var parentIsBody = attachment?.ParentName is { } parent && IsBodyMeshParent(parent);
        if (string.IsNullOrWhiteSpace(socket) &&
            (componentName.Contains("Hip", StringComparison.OrdinalIgnoreCase)
             || componentName.Contains("Belt", StringComparison.OrdinalIgnoreCase)
             || meshPath.Contains("/Hip/", StringComparison.OrdinalIgnoreCase)
             || meshPath.Contains("Belt", StringComparison.OrdinalIgnoreCase)))
        {
            socket = "Pelvis_Minifig_Socket";
            parentIsBody = true;
        }

        if (parentIsBody && socketProfiles.TryGet(bodyMeshPath, socket, out var calibrated) &&
            BoneWorldTransformInGltfSpace(bodyMesh, calibrated.BoneName) is { } parentTransform)
        {
            var transform = ComposeTransforms(parentTransform, ToGltfTransform(calibrated))!;
            Console.WriteLine($"  {componentName}: authored socket {socket} ({calibrated.ProfileName}) -> " +
                              $"({transform.Translation.X:0.###}, {transform.Translation.Y:0.###}, {transform.Translation.Z:0.###})");
            return new AttachmentPlacement(Vector3.Zero, transform, true, calibrated.ProfileName);
        }

        string? bone = null;
        if (parentIsBody && !string.IsNullOrWhiteSpace(socket))
        {
            // The cooked meshes in LotDK have zero serialized sockets. These socket-to-bone mappings
            // are the native LEGOfig rig anchors and let parts remain in their authored local space.
            bone = socket switch
            {
                var s when s.Equals("Root", StringComparison.OrdinalIgnoreCase) => "Root",
                var s when s.StartsWith("HeadStud", StringComparison.OrdinalIgnoreCase) => "Head_Attach_01",
                var s when s.StartsWith("Head", StringComparison.OrdinalIgnoreCase) => "Head",
                var s when s.Contains("Babyfig_Face", StringComparison.OrdinalIgnoreCase) => "Head",
                var s when s.Contains("Chest", StringComparison.OrdinalIgnoreCase) => "Chest",
                var s when s.Contains("Neck", StringComparison.OrdinalIgnoreCase) => "Neck",
                var s when s.Contains("Hip", StringComparison.OrdinalIgnoreCase)
                           || s.Contains("Pelvis", StringComparison.OrdinalIgnoreCase)
                           || s.Contains("Waist", StringComparison.OrdinalIgnoreCase)
                           || s.Contains("Belt", StringComparison.OrdinalIgnoreCase) => "Pelvis",
                var s when s.Contains("Spine_01", StringComparison.OrdinalIgnoreCase) => "Spine_01",
                var s when s.Contains("Spine_02", StringComparison.OrdinalIgnoreCase) => "Spine_02",
                var s when s.Contains("Spine_03", StringComparison.OrdinalIgnoreCase) => "Spine_03",
                var s when s.Contains("Spine", StringComparison.OrdinalIgnoreCase) => "Spine_03",
                var s when s.Contains("WristRoll_L", StringComparison.OrdinalIgnoreCase) => "WristRoll_L",
                var s when s.Contains("WristRoll_R", StringComparison.OrdinalIgnoreCase) => "WristRoll_R",
                var s when s.Contains("Hand_L", StringComparison.OrdinalIgnoreCase) => "Hand_L_Attach_01",
                var s when s.Contains("Hand_R", StringComparison.OrdinalIgnoreCase) => "Hand_R_Attach_01",
                _ => null,
            };
        }

        // Many static Hip components lose their SCS parent/socket while cooking. A utility belt is
        // authored around its local origin, so leaving it at origin puts it by the feet. Its role is
        // unambiguous in both the component name and the mesh package; anchor it to the body pelvis.
        if (bone is null && (componentName.Contains("Hip", StringComparison.OrdinalIgnoreCase)
                             || componentName.Contains("Belt", StringComparison.OrdinalIgnoreCase)
                             || meshPath.Contains("/Hip/", StringComparison.OrdinalIgnoreCase)
                             || meshPath.Contains("Belt", StringComparison.OrdinalIgnoreCase)))
        {
            bone = "Pelvis";
            Console.WriteLine($"  {componentName}: no serialized hip socket; fallback -> Pelvis");
        }
        if (bone is null)
        {
            return null;
        }
        if (bone.Equals("Head_Attach_01", StringComparison.OrdinalIgnoreCase))
        {
            return HeadAttachmentPoint(bodyMesh) is { } head ? new AttachmentPlacement(head) : null;
        }
        return BoneWorldInGltfSpace(bodyMesh, bone) is { } point ? new AttachmentPlacement(point) : null;
    }

    private static Vector3? ResolveHeadAttachmentPoint(USkeletalMesh? bodyMesh) =>
        bodyMesh is null ? null : HeadAttachmentPoint(bodyMesh);

    /// <summary>
    /// SCS nodes spell the native body parent two ways: playable Blueprints use
    /// <c>CharacterMesh0</c>, while many indexed donor records preserve the editor label
    /// <c>Mesh (CharacterMesh0)</c>. Both attach to the same runtime LEGOfig skeleton.
    /// </summary>
    private static bool IsBodyMeshParent(string parent) =>
        parent.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)
        || parent.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
        || parent.Contains("CharacterMesh0", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a preview of a saved suit by layering its project edits over the donor BP.</summary>
    public static string BuildPreviewSuit(
        string paksDir,
        string usmapPath,
        NativeSuitProject project,
        string projectRoot,
        Action<string>? diagnostics = null,
        IReadOnlyCollection<PreviewRedBrickTint>? redBrickTints = null)
    {
        using var diagnosticScope = new PreviewDiagnosticScope(diagnostics);
        diagnosticScope.Start();
        var previewContentRoots = PreviewSuitContentRoots(project, projectRoot);
        var basePath = MountedObjectPath(project.PlayableTemplate?.Uasset);
        var stagedBasePath = MountedObjectPath(project.TargetPackages.Playable);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException("This suit has no playable base character yet.");
        }

        var liveParts = new PartIndexService(projectRoot).LoadPartIndex()?.Parts;
        var additions = new List<PreviewAdditionalPart>();
        foreach (var graft in project.PartGrafts ?? Enumerable.Empty<SavedPartGraft>())
        {
            var donor = graft.Playable ?? graft.Cutscene;
            if (donor is null || string.IsNullOrWhiteSpace(donor.MeshObjectPath))
            {
                continue;
            }

            var component = string.IsNullOrWhiteSpace(graft.ResolvedComponent)
                ? graft.Slot
                : graft.ResolvedComponent;
            if (string.IsNullOrWhiteSpace(component))
            {
                component = donor.TemplateSlot;
            }
            if (string.IsNullOrWhiteSpace(component))
            {
                component = donor.MeshObjectPath.Split('.').LastOrDefault() ?? "GraftedPart";
            }

            var live = liveParts?.FirstOrDefault(part =>
                part.SourcePackagePath.Equals(donor.SourcePackagePath, StringComparison.OrdinalIgnoreCase) &&
                part.MeshObjectPath.Equals(donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(donor.Context) ||
                 part.Context.Equals(donor.Context, StringComparison.OrdinalIgnoreCase)));
            var materialPaths = live?.Materials
                .Select(material => material.ObjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            var parent = string.IsNullOrWhiteSpace(donor.ParentComponentOrVariableName)
                ? live?.ParentComponentOrVariableName
                : donor.ParentComponentOrVariableName;
            var socket = string.IsNullOrWhiteSpace(donor.AttachSocket)
                ? live?.AttachSocket
                : donor.AttachSocket;
            var inferredAttachment = InferAttachment(component, donor.MeshObjectPath);
            parent = string.IsNullOrWhiteSpace(parent) ? inferredAttachment.Parent : parent;
            socket = string.IsNullOrWhiteSpace(socket) ? inferredAttachment.Socket : socket;

            additions.Add(new PreviewAdditionalPart(
                component,
                donor.MeshObjectPath,
                donor.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase),
                parent,
                socket,
                materialPaths,
                ReplaceExisting: true));
        }

        // Imported static meshes are staged beside the suit rather than stored in the game paks.
        // Include them explicitly so the viewer renders the same mesh and anchor the build uses.
        foreach (var custom in project.CustomStaticMeshes ?? Enumerable.Empty<CustomStaticMeshImport>())
        {
            if (string.IsNullOrWhiteSpace(custom.Id))
            {
                continue;
            }

            var component = CustomStaticMeshImportService.ComponentNameFor(custom);
            var attachment = CustomStaticMeshImportService.ResolveAttachmentSlot(custom.Target, custom.AttachSocket);
            var material = string.IsNullOrWhiteSpace(custom.MaterialPath)
                ? "/Game/Characters/Attachments/Hat/Batman08/MI_Hat_Batman08"
                : custom.MaterialPath;
            additions.Add(new PreviewAdditionalPart(
                component,
                UnrealPathUtil.ObjectPath(CustomStaticMeshImportService.MeshPackagePathFor(project, custom)),
                IsStaticAttachment: true,
                ParentComponent: "CharacterMesh0",
                AttachSocket: attachment.AttachSocket,
                MaterialPaths: [material],
                // The staged Blueprint supplies the original component transform. This OBJ entry
                // replaces its mesh export only, so the viewer uses the same attachment chain.
                ReplaceExisting: true,
                SourceObjPath: ProjectOwnedObjPath(project, projectRoot, custom),
                SourceObjScale: custom.Scale,
                SourceObjOffset: new Vector3(custom.OffsetX, custom.OffsetY, custom.OffsetZ),
                SourceObjRotation: new Vector3(custom.RotationPitch, custom.RotationYaw, custom.RotationRoll),
                CustomMeshId: custom.Id,
                DisplayName: custom.DisplayName));
        }

        // Early OBJ proofs were staged before custom imports were saved in the project file.
        // Keep those projects viewable without treating the recovered mesh as editable data.
        if (project.CustomStaticMeshes is not { Count: > 0 })
        {
            foreach (var legacy in LegacyStagedStaticMeshes(project, projectRoot))
            {
                additions.Add(new PreviewAdditionalPart(
                    "LegacyStatic_" + Path.GetFileNameWithoutExtension(legacy.MeshPackagePath),
                    UnrealPathUtil.ObjectPath(legacy.MeshPackagePath),
                    IsStaticAttachment: true,
                    ParentComponent: "CharacterMesh0",
                    AttachSocket: "HeadStud_Attach_Socket",
                    MaterialPaths: ["/Game/Characters/Attachments/Hat/Batman08/MI_Hat_Batman08"],
                    ReplaceExisting: false,
                    SourceObjPath: legacy.SourceObjPath,
                    SourceObjScale: legacy.SourceObjScale,
                    SourceObjOffset: legacy.SourceObjOffset));
            }
        }

        var hidden = project.Requirements
            .Where(requirement => requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => ComponentFromSlotKey(requirement.TargetComponent))
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .ToList();
        if (project.CustomStaticMeshes?.Any(mesh =>
                mesh.HideBaseHead &&
                CustomStaticMeshImportService.ResolveAttachmentSlot(mesh.Target, mesh.AttachSocket).CanHideBaseHead) == true &&
            !hidden.Any(component => SameComponent(component, "Head")))
        {
            // The build removes Head:0 too, but the loose preview must hide it before a build.
            hidden.Add("Head");
        }
        var generatedTextureSources = project.GeneratedTextures
            .Where(texture => !string.IsNullOrWhiteSpace(texture.PackagePath) && File.Exists(texture.SourcePng))
            .GroupBy(texture => UnrealPathUtil.NormalizePackagePath(texture.PackagePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().SourcePng, StringComparer.OrdinalIgnoreCase);
        var materials = project.MaterialAssignments
            .Where(material => material.Context is "both" or "playable")
            .Where(material => !IsFaceComponent(material.Component, null))
            .Where(material => material.Slot >= 0 && !string.IsNullOrWhiteSpace(material.MiPackagePath))
            .Select(material => new PreviewMaterialOverride(
                material.Component,
                material.Slot,
                material.MiPackagePath,
                ReadLocalMaterialFallback(material.MiPackagePath, previewContentRoots, projectRoot, generatedTextureSources)))
            .ToList();

        var layoutKey = ViewerLayoutService.SuitKey(project);
        ViewerLayoutService.ImportLegacyIfEmpty(projectRoot, layoutKey, project.PreviewPartPlacements);

        return BuildPreviewCharacter(paksDir, usmapPath, basePath, previewOptions: new CharacterPreviewOptions
        {
            HiddenComponents = hidden,
            AdditionalParts = additions,
            MaterialOverrides = materials,
            ViewerLayoutKey = layoutKey,
            ViewerLayoutProjectRoot = projectRoot,
            StagedPlayablePath = HasLoosePackage(previewContentRoots, stagedBasePath) ? stagedBasePath : null,
            AllowPartMover = true,
            RedBrickTints = redBrickTints ?? Array.Empty<PreviewRedBrickTint>(),
        }, looseContentRoots: previewContentRoots);
    }

    private static bool HasLoosePackage(IEnumerable<string> contentRoots, string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return false;
        }

        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath).TrimStart('/');
        if (!normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativeUasset = normalized["Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        return contentRoots.Any(root => File.Exists(Path.Combine(root, relativeUasset)));
    }

    private static IReadOnlyList<string> PreviewSuitContentRoots(NativeSuitProject project, string projectRoot)
    {
        var roots = new List<string> { AppSettings.Current.EffectiveExportContentRoot() };
        var generatedRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId);
        roots.AddRange(new[]
        {
            Path.Combine(generatedRoot, "GraftedPartStage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(generatedRoot, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(generatedRoot, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(generatedRoot, "IoStore", "Stage", "LEGOBatmanLotDK", "Content"),
        });

        foreach (var texture in project.GeneratedTextures.Where(texture => !string.IsNullOrWhiteSpace(texture.OutputRoot)))
        {
            roots.Add(Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content"));
            roots.Add(Path.Combine(texture.OutputRoot, "IoStore", "Stage", "LEGOBatmanLotDK", "Content"));
        }

        return roots;
    }

    private sealed record LegacyStagedStaticMesh(
        string MeshPackagePath,
        string? SourceObjPath,
        float SourceObjScale,
        Vector3 SourceObjOffset);

    private static string? ProjectOwnedObjPath(NativeSuitProject project, string projectRoot, CustomStaticMeshImport import)
    {
        if (string.IsNullOrWhiteSpace(import.SourceObjRelativePath))
        {
            return null;
        }

        var path = Path.Combine(new SuitProjectService(projectRoot).ProjectOutputDirectory(project), import.SourceObjRelativePath);
        return File.Exists(path) ? path : null;
    }

    private static IReadOnlyList<LegacyStagedStaticMesh> LegacyStagedStaticMeshes(NativeSuitProject project, string projectRoot)
    {
        var contentRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId,
            "GraftedPartStage",
            "LEGOBatmanLotDK",
            "Content");
        var modsRoot = Path.Combine(contentRoot, "Mods");
        if (!Directory.Exists(modsRoot))
        {
            return Array.Empty<LegacyStagedStaticMesh>();
        }

        return Directory.EnumerateFiles(modsRoot, "*.uasset", SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(path)?.EndsWith("Meshes", StringComparison.OrdinalIgnoreCase) == true)
            .Select(path => ReadLegacyStagedStaticMesh(contentRoot, path))
            .OrderBy(item => item.MeshPackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LegacyStagedStaticMesh ReadLegacyStagedStaticMesh(string contentRoot, string meshUassetPath)
    {
        var packagePath = "/Game/" + Path.ChangeExtension(Path.GetRelativePath(contentRoot, meshUassetPath), null)!.Replace('\\', '/');
        var reportPath = Path.ChangeExtension(meshUassetPath, ".obj-probe-report.json");
        if (!File.Exists(reportPath))
        {
            return new LegacyStagedStaticMesh(packagePath, null, 1f, Vector3.Zero);
        }

        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = report.RootElement;
            var source = root.TryGetProperty("sourceObjPath", out var value)
                ? value.GetString()
                : null;
            // The CLI OBJ proof predated persisted imports. It always used 150 unless the
            // report recorded another value, so retain that real cook scale for faithful previews.
            var scale = root.TryGetProperty("scale", out var scaleValue) && scaleValue.TryGetSingle(out var recordedScale)
                ? recordedScale
                : 150f;
            static float ReadOffset(JsonElement root, string name) =>
                root.TryGetProperty(name, out var value) && value.TryGetSingle(out var recorded) ? recorded : 0f;
            return new LegacyStagedStaticMesh(
                packagePath,
                !string.IsNullOrWhiteSpace(source) && File.Exists(source) ? source : null,
                scale,
                new Vector3(ReadOffset(root, "offsetX"), ReadOffset(root, "offsetY"), ReadOffset(root, "offsetZ")));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  legacy OBJ report ignored '{reportPath}': {ex.Message}");
            return new LegacyStagedStaticMesh(packagePath, null, 1f, Vector3.Zero);
        }
    }

    private static PreviewMaterialFallback? ReadLocalMaterialFallback(
        string materialPath,
        IReadOnlyList<string> contentRoots,
        string projectRoot,
        IReadOnlyDictionary<string, string> generatedTextureSources)
    {
        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        var diskPath = contentRoots
            .Select(root => Path.Combine(root, relative))
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(diskPath))
        {
            return null;
        }

        var info = new MaterialGenService(projectRoot).ReadTemplate(diskPath);
        if (!info.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(info.ParentMaterialPath))
        {
            return null;
        }

        var textures = info.TextureParams
            .Where(texture => texture.ObjectPath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(texture => texture.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => UnrealPathUtil.ObjectPath(group.Last().ObjectPath),
                StringComparer.OrdinalIgnoreCase);
        var sources = info.TextureParams
            .Where(texture => !string.IsNullOrWhiteSpace(texture.ObjectPath))
            .Select(texture => new
            {
                texture.Name,
                PackagePath = UnrealPathUtil.NormalizePackagePath(texture.ObjectPath),
            })
            .Where(texture => generatedTextureSources.ContainsKey(texture.PackagePath))
            .GroupBy(texture => texture.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => generatedTextureSources[group.Last().PackagePath],
                StringComparer.OrdinalIgnoreCase);
        var colours = info.ColorParams
            .GroupBy(colour => colour.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Color.FromArgb(
                    255,
                    (int)Math.Clamp(group.Last().R * 255f + 0.5f, 0, 255),
                    (int)Math.Clamp(group.Last().G * 255f + 0.5f, 0, 255),
                    (int)Math.Clamp(group.Last().B * 255f + 0.5f, 0, 255)),
                StringComparer.OrdinalIgnoreCase);
        PreviewTrace($"Preview material: {Path.GetFileNameWithoutExtension(diskPath)} -> "
                     + $"{info.ParentMaterialPath} ({textures.Count} cooked texture, {sources.Count} source, "
                     + $"{colours.Count} colour override(s)).");
        return new PreviewMaterialFallback(info.ParentMaterialPath, textures, sources, colours);
    }

    private static string? MountedObjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".uasset".Length];
        }
        if (normalized.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("LEGOBatmanLotDK/Content/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        const string contentRoot = "LEGOBatmanLotDK/Content/";
        var contentIndex = normalized.IndexOf(contentRoot, StringComparison.OrdinalIgnoreCase);
        return contentIndex >= 0 ? normalized[contentIndex..] : null;
    }

    private static string ComponentFromSlotKey(string? component)
    {
        var value = component?.Trim() ?? "";
        var colon = value.LastIndexOf(':');
        if (colon > 0)
        {
            value = value[..colon];
        }
        const string generated = "_GEN_VARIABLE";
        return value.EndsWith(generated, StringComparison.OrdinalIgnoreCase)
            ? value[..^generated.Length]
            : value;
    }

    private static bool SameComponent(string? left, string? right) =>
        ComponentFromSlotKey(left).Equals(ComponentFromSlotKey(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Last-resort attachment conventions for legacy graft records with no SCS metadata.</summary>
    private static (string? Parent, string? Socket) InferAttachment(string component, string meshPath)
    {
        var name = ComponentFromSlotKey(component);
        if (name.StartsWith("Face", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase))
        {
            return ("CharacterMesh0", "Head_Socket");
        }

        if (name.StartsWith("Head", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Hair", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Hat", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Helmet", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("/Hair/", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("/Hat/", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("/Helmet/", StringComparison.OrdinalIgnoreCase))
        {
            return ("CharacterMesh0", "HeadStud_Attach_Socket");
        }

        if (name.Contains("Left", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("Hand", StringComparison.OrdinalIgnoreCase) || name.Contains("Wrist", StringComparison.OrdinalIgnoreCase)))
        {
            return ("CharacterMesh0", "Hand_L_Attach_01");
        }

        if (name.Contains("Right", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("Hand", StringComparison.OrdinalIgnoreCase) || name.Contains("Wrist", StringComparison.OrdinalIgnoreCase)))
        {
            return ("CharacterMesh0", "Hand_R_Attach_01");
        }

        if (name.Contains("Hip", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Belt", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("/Hip/", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("Belt", StringComparison.OrdinalIgnoreCase))
        {
            return ("CharacterMesh0", "Pelvis_Minifig_Socket");
        }

        if (name.StartsWith("Cape", StringComparison.OrdinalIgnoreCase) ||
            meshPath.Contains("/Cape/", StringComparison.OrdinalIgnoreCase))
        {
            return ("CharacterMesh0", "Root");
        }

        if (name.StartsWith("Torso", StringComparison.OrdinalIgnoreCase))
        {
            return ("CharacterMesh0", "Chest_Socket");
        }

        return (null, null);
    }

    private static bool IsFaceComponent(string? component, string? meshPath) =>
        (!string.IsNullOrWhiteSpace(component) &&
         component.Contains("Face", StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(meshPath) &&
         meshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Static hair meshes are authored just above the shared head anchor. A small viewer-only lift
    /// gives them the same resting alignment as the in-game character, while preserving the real
    /// Blueprint socket, transform, and any per-character placement the user has saved.
    /// </summary>
    private static SavedPreviewPartPlacement? DefaultStaticHairHeadAdjustment(PreviewPart part)
    {
        if (part.UsesRuntimeSocketCalibration)
        {
            return null;
        }

        var attachesToHead = AttachBoneFor(part.ComponentName, part.Attachment?.SocketName)
            ?.Equals("Head_Attach_01", StringComparison.OrdinalIgnoreCase) == true;
        return part.IsStaticAttachment && attachesToHead &&
               part.MeshPath.Contains("/Hair/", StringComparison.OrdinalIgnoreCase)
            ? new SavedPreviewPartPlacement
            {
                Component = part.ComponentName,
                OffsetY = 0.05f,
            }
            : null;
    }

    private static Vector3? HeadAttachmentPoint(USkeletalMesh mesh) =>
        BoneWorldInGltfSpace(mesh, "Head_Attach_01") ?? BoneWorldInGltfSpace(mesh, "Head");

    /// <summary>
    /// The shared bare LEGO head only belongs on LEGOfig-derived bodies. A creature can have a
    /// perfectly valid bone named "Head", but layering the minifig head over it is never correct.
    /// The body asset path is the stable distinction exposed by the cooked Blueprints.
    /// </summary>
    private static bool IsLegoFigureBody(string meshPath) =>
        meshPath.Contains("/LEGOfig/", StringComparison.OrdinalIgnoreCase) ||
        meshPath.Contains("LEGOfig_", StringComparison.OrdinalIgnoreCase) ||
        meshPath.Contains("/Smallfig/", StringComparison.OrdinalIgnoreCase);

    public static string BuildPreviewCharacter(
        string paksDir,
        string usmapPath,
        string bpPath,
        string? bodyMeshPath = null,
        CharacterPreviewOptions? previewOptions = null,
        IEnumerable<string>? looseContentRoots = null)
    {
        var options = previewOptions ?? new CharacterPreviewOptions();
        var viewerLayoutKey = string.IsNullOrWhiteSpace(options.ViewerLayoutKey)
            ? ViewerLayoutService.CharacterKey(bpPath, bodyMeshPath)
            : options.ViewerLayoutKey!;
        var viewerLayoutRoot = string.IsNullOrWhiteSpace(options.ViewerLayoutProjectRoot)
            ? AppSettings.Current.EffectiveProjectRoot()
            : options.ViewerLayoutProjectRoot!;
        var viewerPlacements = ViewerLayoutService.Load(viewerLayoutRoot, viewerLayoutKey);
        var socketProfiles = RuntimeSocketProfileService.Load();
        if (socketProfiles.Count > 0)
        {
            Console.WriteLine($"  authored socket profiles: {socketProfiles.Count} bundled rig profile(s)");
        }
        var provider = MakeProvider(paksDir, usmapPath, looseContentRoots);
        var resolvedComponents = ResolveVisualBlueprintComponents(provider, bpPath);
        var stagedComponents = string.IsNullOrWhiteSpace(options.StagedPlayablePath)
            ? Array.Empty<ResolvedBlueprintComponent>()
            : ResolveVisualBlueprintComponents(provider, options.StagedPlayablePath).ToArray();
        var resolvedBody = resolvedComponents.FirstOrDefault(component =>
            component.Key.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase));
        var bodyPath = !string.IsNullOrWhiteSpace(bodyMeshPath)
            ? bodyMeshPath!
            : resolvedBody?.MeshPath;
        var parts = new List<PreviewPart>();
        if (string.IsNullOrWhiteSpace(bodyPath))
        {
            Console.WriteLine("  no CharacterMesh0 mesh was found in this Blueprint's visual inheritance chain; no body fallback was added.");
        }
        else
        {
            parts.Add(new PreviewPart(
                bodyPath,
                AttachToHead: false,
                Overrides: resolvedBody?.Overrides,
                ComponentName: "CharacterMesh0"));
            var source = string.IsNullOrWhiteSpace(bodyMeshPath)
                ? resolvedBody?.SourcePackage ?? bpPath
                : "explicit preview argument";
            Console.WriteLine($"  body mesh from visual BP chain: {source} -> {bodyPath}");
            if (resolvedBody?.Overrides is { Length: > 0 })
            {
                Console.WriteLine($"  body material: {resolvedBody.Overrides[0]?.ResolvedObject?.GetPathName()}");
            }
        }

        // The standard head mesh is authored against the regular minifig's head anchor. Shift it
        // by the delta to the active body's own anchor so smallfigs inherit the correct neck height
        // without a hand-tuned Robin special case. Bodies with no compatible head anchor are not
        // minifigs at all (creatures, vehicles, etc.), so they must not receive a LEGO head.
        var bodyMesh = string.IsNullOrWhiteSpace(bodyPath)
            ? null
            : provider.LoadPackageObject(bodyPath) as USkeletalMesh;
        var bodyHeadPoint = ResolveHeadAttachmentPoint(bodyMesh);
        if (bodyMesh is not null)
        {
            if (bodyHeadPoint is { } point)
            {
                _headAttachPoint = point;
                Console.WriteLine($"  body head anchor -> ({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###})");
            }
            else
            {
                Console.WriteLine("  body has no minifig head anchor; omitting the default LEGO head.");
            }
        }

        var hasAuthoredBareHead = resolvedComponents.Any(component =>
            !component.Key.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) &&
            component.MeshPath?.Contains("LEGOfig_Minifig_Head", StringComparison.OrdinalIgnoreCase) == true);
        if (bodyHeadPoint is { } activeHeadPoint && !string.IsNullOrWhiteSpace(bodyPath) &&
            IsLegoFigureBody(bodyPath) && !hasAuthoredBareHead)
        {
            var referenceHeadPoint = activeHeadPoint;
            var referenceBodyPath = string.IsNullOrWhiteSpace(bodyMeshPath)
                ? resolvedBody?.HeadReferenceBodyPath
                : null;
            if (!string.IsNullOrWhiteSpace(referenceBodyPath))
            {
                try
                {
                    var inheritedBody = provider.LoadPackageObject(referenceBodyPath) as USkeletalMesh;
                    referenceHeadPoint = ResolveHeadAttachmentPoint(inheritedBody)
                                         ?? activeHeadPoint;
                }
                catch
                {
                    // An unreadable inherited body merely leaves the native head at the active rig anchor.
                }
            }
            var headOffset = activeHeadPoint - referenceHeadPoint;
            parts.Add(new PreviewPart(
                DefaultHeadMesh,
                AttachToHead: false,
                IsHeadPiece: true,
                ComponentName: "__BareHead",
                AttachmentOffset: headOffset));
        }

        _bpCharacter = System.Text.RegularExpressions.Regex
            .Match(Path.GetFileNameWithoutExtension(bpPath), @"^BP_([A-Za-z0-9]+)")
            .Groups[1].Value is { Length: > 0 } bpName ? bpName : null;

        foreach (var component in resolvedComponents)
        {
            if (component.Key.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var path = component.MeshPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var componentName = component.Key;
            if (component.Hidden)
            {
                Console.WriteLine($"  {componentName}: hidden by Blueprint component state");
                continue;
            }
            if ((!IncludeNeutralFacePreview && IsFaceComponent(componentName, path)) ||
                options.HiddenComponents.Any(hidden => SameComponent(hidden, componentName)) ||
                options.AdditionalParts.Any(extra => extra.ReplaceExisting && SameComponent(extra.ComponentName, componentName)))
            {
                continue;
            }
            // The glide cape (Torso slot) is only shown while gliding - skip it for the standing look.
            if (path!.Contains("Glide", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // The character's real look lives in the component's override materials (e.g.
            // MI_Batman_89_EOM), not in the base mesh's own material slots.
            var inferredAttachment = InferAttachment(componentName, path);
            var attachment = component.Attachment
                ?? new BlueprintAttachment(inferredAttachment.Parent, inferredAttachment.Socket);
            var attachmentPlacement = ResolveBodyAttachmentPlacement(
                socketProfiles, bodyPath, bodyMesh, attachment, componentName, path);
            if (attachment is not null)
            {
                Console.WriteLine($"  {componentName}: parent={attachment.ParentName ?? "(none)"}"
                                  + $" socket={attachment.SocketName ?? "(none)"}"
                                  + (attachmentPlacement is { } placement
                                      ? $" -> ({placement.Offset.X:0.###}, {placement.Offset.Y:0.###}, {placement.Offset.Z:0.###})"
                                      : ""));
            }
            parts.Add(new PreviewPart(path, AttachToHead: AttachBoneFor(componentName, attachment?.SocketName) is not null,
                Overrides: component.Overrides, IsStaticAttachment: component.IsStaticAttachment,
                ComponentName: componentName,
                Transform: ComposeTransforms(attachmentPlacement?.SocketTransform, component.Transform),
                Attachment: attachment,
                AttachmentOffset: attachmentPlacement?.Offset,
                UsesRuntimeSocketCalibration: attachmentPlacement?.UsesRuntimeCalibration == true));
        }

        foreach (var extra in options.AdditionalParts)
        {
            if (string.IsNullOrWhiteSpace(extra.MeshPath) ||
                (!IncludeNeutralFacePreview && IsFaceComponent(extra.ComponentName, extra.MeshPath)) ||
                options.HiddenComponents.Any(hidden => SameComponent(hidden, extra.ComponentName)))
            {
                continue;
            }

            // A custom mesh is rendered from its OBJ, but its staged component is still the
            // authority for the donor-relative transform and SCS attachment.
            var stagedComponent = string.IsNullOrWhiteSpace(extra.CustomMeshId)
                ? null
                : stagedComponents.FirstOrDefault(component => SameComponent(component.Key, extra.ComponentName));
            var attachment = stagedComponent?.Attachment
                ?? new BlueprintAttachment(extra.ParentComponent, extra.AttachSocket);
            var attachmentPlacement = ResolveBodyAttachmentPlacement(
                socketProfiles, bodyPath, bodyMesh, attachment, extra.ComponentName, extra.MeshPath);
            var materialPaths = extra.MaterialPaths?
                .Select((path, index) => (path, index))
                .Where(item => !string.IsNullOrWhiteSpace(item.path))
                .ToDictionary(item => item.index, item => item.path);
            parts.Add(new PreviewPart(
                extra.MeshPath,
                AttachToHead: AttachBoneFor(extra.ComponentName, attachment.SocketName) is not null,
                IsStaticAttachment: extra.IsStaticAttachment,
                ComponentName: ComponentFromSlotKey(extra.ComponentName),
                Transform: ComposeTransforms(attachmentPlacement?.SocketTransform, stagedComponent?.Transform),
                Attachment: attachment,
                AttachmentOffset: attachmentPlacement?.Offset,
                UsesRuntimeSocketCalibration: attachmentPlacement?.UsesRuntimeCalibration == true,
                MaterialPaths: materialPaths,
                SourceObjPath: extra.SourceObjPath,
                SourceObjScale: extra.SourceObjScale,
                SourceObjOffset: extra.SourceObjOffset,
                SourceObjRotation: extra.SourceObjRotation,
                CustomMeshId: extra.CustomMeshId,
                DisplayName: extra.DisplayName));
            if (stagedComponent?.Transform is { } transform)
            {
                Console.WriteLine($"  {extra.ComponentName}: staged component transform -> "
                                  + $"({transform.Translation.X:0.###}, {transform.Translation.Y:0.###}, {transform.Translation.Z:0.###})");
            }
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var materialPaths = part.MaterialPaths is null
                ? new Dictionary<int, string>()
                : new Dictionary<int, string>(part.MaterialPaths);
            var materialFallbacks = part.MaterialFallbacks is null
                ? new Dictionary<int, PreviewMaterialFallback>()
                : new Dictionary<int, PreviewMaterialFallback>(part.MaterialFallbacks);
            foreach (var material in options.MaterialOverrides.Where(material => SameComponent(material.ComponentName, part.ComponentName)))
            {
                materialPaths[material.Slot] = material.MaterialPath;
                if (material.LocalFallback is not null)
                {
                    materialFallbacks[material.Slot] = material.LocalFallback;
                }
            }
            // Imported meshes own their transform in the suit project. A generic viewer-only
            // nudge would make the preview disagree with the mesh that gets baked for the game.
            var adjustment = !string.IsNullOrWhiteSpace(part.CustomMeshId) || IsFaceComponent(part.ComponentName, part.MeshPath)
                ? null
                : viewerPlacements
                    .FirstOrDefault(placement => SameComponent(placement.Component, part.ComponentName))
                    ?? options.PlacementOverrides
                        .FirstOrDefault(placement => SameComponent(placement.Component, part.ComponentName))
                    ?? DefaultStaticHairHeadAdjustment(part);
            parts[i] = part with
            {
                MaterialPaths = materialPaths.Count == 0 ? null : materialPaths,
                MaterialFallbacks = materialFallbacks.Count == 0 ? null : materialFallbacks,
                Adjustment = adjustment,
            };
        }

        return BuildPreviewCore(provider, parts, options.AllowPartMover, viewerLayoutKey, options.RedBrickTints);
    }

    /// <summary>Exports each mesh to glTF and writes the viewer that loads them into one scene.</summary>
    public static string BuildPreview(string paksDir, string usmapPath, IReadOnlyList<string> objectPaths)
        => BuildPreviewCore(MakeProvider(paksDir, usmapPath),
            objectPaths.Select(p => new PreviewPart(p, AttachToHead: false)).ToList());

    private static string BuildPreviewCore(
        DefaultFileProvider provider,
        IReadOnlyList<PreviewPart> parts,
        bool allowPartMover = false,
        string? viewerLayoutKey = null,
        IReadOnlyCollection<PreviewRedBrickTint>? redBrickTints = null)
    {
        _faceMaterial = null;
        _faceBaseline = null;
        _faceMeshPath = null;
        _faceAnimHome = null;
        var faceProfiles = RuntimeFaceProfileService.Load();
        if (faceProfiles.Count > 0)
        {
            Console.WriteLine($"  neutral face profiles: {faceProfiles.Count}");
        }

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

            // The head has no bound material; fall back to the LEGO skin tone so it is not untextured.
            var headSkin = part.IsHeadPiece ? SkinTone : (Color?)null;

            // Resolve EVERY slot separately: a mesh's sections have different materials (the cowl's
            // shell vs its eyes, the cape's LOD variants), so one texture must not be sprayed across all.
            var slotShading = new List<SlotShading>();
            var slotMats = MeshSlotMaterials(mesh);
            var disabledSlots = DisabledSectionSlots(mesh);
            for (var si = 0; si < slotMats.Count; si++)
            {
                var fallback = part.MaterialFallbacks is not null && part.MaterialFallbacks.TryGetValue(si, out var localFallback)
                    ? localFallback
                    : null;
                if (fallback is not null)
                {
                    Console.WriteLine($"    generated material fallback [{si}]: {fallback.TextureOverrides.Count} texture, "
                                      + $"{fallback.SourceTextureOverrides.Count} source, "
                                      + $"{fallback.ColourOverrides.Count} colour override(s)");
                }
                UObject? slotMat = fallback is null
                    ? ResolvePreviewMaterial(provider, part, si)
                    : LoadPreviewMaterial(provider, fallback.ParentMaterialPath) ?? ResolvePreviewMaterial(provider, part, si);
                slotMat ??= (part.Overrides is not null && si < part.Overrides.Length
                    ? part.Overrides[si]?.ResolvedObject?.Load()
                    : null) ?? slotMats[si];
                if (si == 0 && part.MeshPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase))
                {
                    _faceMaterial = slotMat;
                    _faceMeshPath = part.MeshPath;
                    _faceBaseline = faceProfiles.TryGet(slotMat?.GetPathName(), out var profile) ? profile : null;
                    Console.WriteLine(_faceBaseline is null
                        ? "    face neutral profile: material defaults"
                        : $"    face neutral profile: {_faceBaseline.MaterialPath} ({_faceBaseline.Scalars.Count} scalar values)");
                }
                var resolved = ResolveSlot(provider, slotMat, previewDir, fallback);
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
            if (slotShading.Count == 0 && part.MaterialPaths is { Count: > 0 } savedMaterials)
            {
                foreach (var (slot, materialPath) in savedMaterials.OrderBy(entry => entry.Key))
                {
                    var fallback = part.MaterialFallbacks is not null && part.MaterialFallbacks.TryGetValue(slot, out var localFallback)
                        ? localFallback
                        : null;
                    UObject? material = fallback is null
                        ? ResolvePreviewMaterial(provider, part, materialPath)
                        : LoadPreviewMaterial(provider, fallback.ParentMaterialPath) ?? ResolvePreviewMaterial(provider, part, materialPath);
                    var solo = ResolveSlot(provider, material, previewDir, fallback);
                    slotShading.Add(solo);
                }
            }
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

            if (!string.IsNullOrWhiteSpace(part.SourceObjPath) && File.Exists(part.SourceObjPath))
            {
                try
                {
                    var name = $"model{i}.glb";
                    var output = Path.Combine(previewDir, name);
                    var offset = part.SourceObjOffset ?? Vector3.Zero;
                    var rotation = part.SourceObjRotation ?? Vector3.Zero;
                    StaticMeshObjProbeService.WritePreviewGlb(
                        part.SourceObjPath,
                        output,
                        part.SourceObjScale,
                        offset.X,
                        offset.Y,
                        offset.Z,
                        rotation.X,
                        rotation.Y,
                        rotation.Z);
                    models.Add((name, part, slotShading));
                    Console.WriteLine($"  {Path.GetFileName(part.SourceObjPath)} -> preview GLB from source OBJ");
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  OBJ preview fallback failed '{part.SourceObjPath}': {ex.Message}");
                }
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

        var placed = AlignToHead(previewDir, models);
        if (IncludeNeutralFacePreview)
        {
            placed = PrepareFaceFeatures(provider, previewDir, placed);
        }
        WriteViewerAssets(previewDir, placed, allowPartMover, viewerLayoutKey, redBrickTints);
        return previewDir;
    }

    private static CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial? ResolvePreviewMaterial(
        DefaultFileProvider provider,
        PreviewPart part,
        int slot)
    {
        if (part.MaterialPaths is null || !part.MaterialPaths.TryGetValue(slot, out var path) ||
            string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return provider.LoadPackageObject(path) as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial;
        }
        catch (Exception ex)
        {
            // User-made /Game/Mods assets live in the staged output rather than the stock paks.
            // Their package cannot be decoded by this read-only provider yet; keep the donor slot
            // instead of blanking the model and make the limitation visible in diagnostics.
            Console.WriteLine($"  material preview fallback '{path}': {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    private static CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial? LoadPreviewMaterial(
        DefaultFileProvider provider,
        string path)
    {
        try
        {
            return provider.LoadPackageObject(path) as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial;
        }
        catch
        {
            return null;
        }
    }

    private static CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial? ResolvePreviewMaterial(
        DefaultFileProvider provider,
        PreviewPart part,
        string path)
    {
        try
        {
            return provider.LoadPackageObject(path) as CUE4Parse.UE4.Assets.Exports.Material.UUnrealMaterial;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  material preview fallback '{path}': {ex.Message.Split('\n')[0]}");
            return null;
        }
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
        /// <summary>Authored component transform from the BP, converted into glTF space.</summary>
        public PreviewComponentTransform? Transform { get; init; }
        /// <summary>True when the model is placed with a captured CharacterMesh0 socket transform.</summary>
        public bool UsesRuntimeSocketCalibration { get; init; }
        /// <summary>Stable component identity used by the project-aware part mover.</summary>
        public string ComponentName { get; init; } = "";
        /// <summary>Author-facing label; the stable component identity remains internal.</summary>
        public string? DisplayName { get; init; }
        /// <summary>Saved small translation layered over the Blueprint's authored placement.</summary>
        public SavedPreviewPartPlacement? Adjustment { get; init; }
        /// <summary>Project identity and baked transform for an imported static mesh.</summary>
        public string? CustomMeshId { get; init; }
        public float CustomMeshScale { get; init; }
        public Vector3? CustomMeshOffset { get; init; }
        public Vector3? CustomMeshRotation { get; init; }
        /// <summary>Triangle counts of the reordered face index buffer: [base, mouth, hidden].</summary>
        public int[]? FaceGroups { get; init; }
        /// <summary>Preview-relative path of the mouth feature print (alpha = cutout).</summary>
        public string? MouthTex { get; init; }
        /// <summary>Alternate expression feature bands: (ExtraUV0 slot id, triangle count).</summary>
        public List<FaceBand>? Bands { get; init; }
        /// <summary>True when the character binds a dummy to the mouth feature (draw nothing).</summary>
        public bool MouthHidden { get; init; }
        /// <summary>Facial rig pose (bone name -> local transform, glTF space) from the expression anim.</summary>
        public Dictionary<string, Dictionary<int, Dictionary<string, (Vector3 P, System.Numerics.Quaternion Q, Vector3 S)>>>? Poses { get; init; }
        /// <summary>
        /// Material-parameter curves from the same expression clips as <see cref="Poses"/>. The
        /// outer mouth, teeth, tongue, eyelids, and eye highlights are driven by these curves in
        /// the game; posing the bones alone cannot reproduce a face.
        /// </summary>
        public Dictionary<string, Dictionary<int, Dictionary<string, float>>>? Curves { get; init; }
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
    private static readonly double[] FacePoseSamples = { 0.15, 0.3, 0.45, 0.6, 0.75, 0.9 };

    private static Dictionary<int, Dictionary<string, (Vector3 P, System.Numerics.Quaternion Q, Vector3 S)>>? LoadFacePose(
        DefaultFileProvider provider, string expression, string? character,
        out Dictionary<int, Dictionary<string, float>>? materialCurves)
    {
        materialCurves = null;
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
        // A character's face AnimBP commonly references an AnimMontage (AM_*) rather than the
        // source sequence (A_*). Bruce Wayne's Neutral state is exactly that: the montage loops
        // A_Idle_BruceWayne_LEGOface on its Expression slot. Try both asset forms together so a
        // direct sequence remains preferred when the game actually uses one.
        void AddAnimationCandidate(string sequencePath)
        {
            candidates.Add(sequencePath);
            candidates.Add(sequencePath.Replace("/A_", "/AM_", StringComparison.Ordinal));
        }
        foreach (var who in new[] { character, _bpCharacter }.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct())
        {
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_{who}/A_{expression}_{who}_LEGOface");
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_{who}/A_{expression}_{who}_LEGOFace");
            AddAnimationCandidate($"/Game/Animation/LEGOfig/{who}/Movement/A_{expression}_{who}_LEGOface");
            AddAnimationCandidate($"/Game/Animation/LEGOfig/{who}/Movement/A_{expression}_{who}_LEGOFace");
            // Some characters keep their face animation one level deeper, under Attachments/
            // (Batman: Movement/A_Idle_Batman_LEGOface, Bruce: Movement/Attachments/A_Idle_BruceWayne_LEGOface).
            AddAnimationCandidate($"/Game/Animation/LEGOfig/{who}/Movement/Attachments/A_{expression}_{who}_LEGOface");
            AddAnimationCandidate($"/Game/Animation/LEGOfig/{who}/Movement/Attachments/A_{expression}_{who}_LEGOFace");
        }
        // The character's own anim blueprint names the folder its expressions live in (Bruce Wayne
        // -> LEGOface_Batman). That is the character's own declaration, so it outranks the shared
        // sets - but it usually only ships a Neutral, hence the fallbacks below.
        if (_faceAnimHome is not null)
        {
            foreach (var who in new[] { character, _bpCharacter, null }
                     .Where(w => w is null || !string.IsNullOrWhiteSpace(w)).Distinct())
            {
                var suffix = who is null ? "" : "_" + who;
                AddAnimationCandidate($"{_faceAnimHome}/A_{expression}{suffix}_LEGOface");
                AddAnimationCandidate($"{_faceAnimHome}/A_{expression}{suffix}_LEGOFace");
            }
        }
        // Then the shared set that matches this character's RIG. Only a Superhero face mesh is posed
        // by the Superhero sequences; everyone else uses the generic ones. Falling through to
        // Superhero for every character was giving ordinary faces a cowled hero's expressions.
        var superheroRig = _faceMeshPath?.Contains("Superhero", StringComparison.OrdinalIgnoreCase) == true;
        if (superheroRig)
        {
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_Superhero/A_{expression}_LEGOFace_Superhero");
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_Expressions/A_{expression}_LEGOFace");
        }
        else
        {
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_Expressions/A_{expression}_LEGOFace");
            AddAnimationCandidate($"/Game/Animation/LEGOface/LEGOface_Superhero/A_{expression}_LEGOFace_Superhero");
        }

        foreach (var path in candidates)
        {
            try
            {
                var source = provider.LoadPackageObject(path);
                var anim = ResolveAnimationSequence(source);
                if (anim is null)
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
                // Animation tracks are not guaranteed to be stored in reference-skeleton order.
                // UE serialises the authoritative mapping beside the compressed tracks; ignoring it
                // can put an eyelid or lip transform on a neighbouring bone and visibly warp a face.
                var trackToSkeleton = ReadAnimMember(anim, "CompressedTrackToSkeletonMapTable") as Array;

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
                for (var i = 0; i < seq.Tracks.Count; i++)
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
                    var boneIndex = i;
                    if (trackToSkeleton is not null && i < trackToSkeleton.Length)
                    {
                        boneIndex = ReadAnimInt(ReadAnimMember(trackToSkeleton.GetValue(i)!, "BoneTreeIndex", "BoneIndex"))
                                    ?? i;
                    }
                    if (boneIndex < 0 || boneIndex >= refBones.Length)
                    {
                        continue;
                    }
                    var refBone = refBones[boneIndex];
                    if (refBone.ParentIndex < 0)
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
                    pose[refBone.Name.Text] = (
                        new Vector3(p.X / 100f, p.Z / 100f, p.Y / 100f),
                        new System.Numerics.Quaternion(q.X, q.Z, q.Y, q.W),
                        new Vector3(s.X, s.Z, s.Y));
                }
                byFrame[bestFrame] = pose;
                }
                var sampleTracks = byFrame.Count > 0
                    ? string.Join(", ", byFrame.Values.First().Keys.Take(6))
                    : "(none)";
                var sourceName = source.Name == anim.Name
                    ? anim.Name
                    : $"{source.Name} -> {anim.Name}";
                Console.WriteLine($"  face pose '{expression}': {byFrame.Count} frames {string.Join("/", byFrame.Keys)} of {frameCount} from {sourceName}");
                Console.WriteLine($"      tracks: {(byFrame.Count > 0 ? byFrame.Values.First().Count : 0)} -> {sampleTracks}");
                materialCurves = LoadFaceMaterialCurves(anim, byFrame.Keys, frameCount);
                if (materialCurves?.Count > 0)
                {
                    Console.WriteLine($"      material curves: {materialCurves.Values.First().Count} at each sampled frame");
                }
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
    /// Skeletal meshes expose their material references directly, while static attachment meshes
    /// keep them inside StaticMaterials structs. Treating the latter as slotless skips native hair,
    /// hats, and props whenever their component does not happen to provide an override.
    /// </summary>
    private static IReadOnlyList<UObject?> MeshSlotMaterials(UObject mesh)
    {
        if (mesh is USkeletalMesh skeletal)
        {
            return skeletal.Materials?.Select(material => material?.Load()).ToArray()
                   ?? Array.Empty<UObject?>();
        }

        if (mesh is UStaticMesh stat)
        {
            var staticMaterials = stat.GetOrDefault<FStructFallback[]>("StaticMaterials");
            if (staticMaterials is not null)
            {
                return staticMaterials.Select(material =>
                    material.GetOrDefault<FPackageIndex>("MaterialInterface")?.ResolvedObject?.Load()
                    ?? material.GetOrDefault<FPackageIndex>("Material")?.ResolvedObject?.Load())
                    .ToArray();
            }
        }

        return Array.Empty<UObject?>();
    }

    /// <summary>
    /// Reads the named float curves embedded in a face <see cref="UAnimSequence"/>. Unreal applies
    /// these as dynamic-material parameters while the pose plays: e.g. <c>teethuoffsetv</c> slides
    /// the upper-teeth sheet into the mouth and <c>mouthhide</c> turns the whole shell off. CUE4Parse
    /// exposes this cooked data, but its rich-curve types are implementation details, so keep the
    /// small reflection bridge here rather than depending on a private parser type.
    /// </summary>
    private static Dictionary<int, Dictionary<string, float>>? LoadFaceMaterialCurves(
        UAnimSequence anim, IEnumerable<int> sampleFrames, int frameCount)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                                                    | System.Reflection.BindingFlags.Public
                                                    | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var type = anim.GetType();
            var names = type.GetField("CompressedCurveNames", flags)?.GetValue(anim) as Array;
            var curveData = type.GetField("CompressedCurveData", flags)?.GetValue(anim);
            var floatCurves = curveData is null ? null : ReadAnimMember(curveData, "FloatCurves") as Array;
            if (names is null || floatCurves is null || floatCurves.Length == 0)
            {
                return null;
            }

            var decoded = new List<(string Name, List<(float Time, float Value)> Keys)>();
            for (var i = 0; i < floatCurves.Length; i++)
            {
                var name = i < names.Length
                    ? ReadAnimMember(names.GetValue(i)!, "DisplayName")?.ToString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var curve = floatCurves.GetValue(i);
                var rich = curve is null ? null : ReadAnimMember(curve, "FloatCurve", "Curve");
                var rawKeys = rich is null ? null : ReadAnimMember(rich, "Keys");
                var keys = rawKeys switch
                {
                    Array a => a.Cast<object?>(),
                    System.Collections.IEnumerable e => e.Cast<object?>(),
                    _ => Enumerable.Empty<object?>(),
                };
                var values = new List<(float Time, float Value)>();
                foreach (var key in keys)
                {
                    if (key is null) continue;
                    var time = ReadAnimFloat(ReadAnimMember(key, "Time"));
                    var value = ReadAnimFloat(ReadAnimMember(key, "Value"));
                    if (time is { } t && value is { } v)
                    {
                        values.Add((t, v));
                    }
                }
                values.Sort((a, b) => a.Time.CompareTo(b.Time));
                if (values.Count > 0)
                {
                    decoded.Add((name!, values));
                }
            }
            if (decoded.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<int, Dictionary<string, float>>();
            foreach (var frame in sampleFrames)
            {
                var time = anim.SequenceLength * frame / Math.Max(1, frameCount - 1);
                var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, keys) in decoded)
                {
                    values[name] = SampleRichCurve(keys, time);
                }
                result[frame] = values;
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      material curve decode failed: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    private static object? ReadAnimMember(object target, params string[] names)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                                                    | System.Reflection.BindingFlags.Public
                                                    | System.Reflection.BindingFlags.NonPublic;
        foreach (var name in names)
        {
            var field = target.GetType().GetField(name, flags);
            if (field is not null)
            {
                return field.GetValue(target);
            }
            var property = target.GetType().GetProperty(name, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target);
            }
        }
        return null;
    }

    private static float? ReadAnimFloat(object? value) => value switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        _ when value is not null && float.TryParse(value.ToString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static int? ReadAnimInt(object? value) => value switch
    {
        byte b => b,
        short s => s,
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ when value is not null && int.TryParse(value.ToString(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static float SampleRichCurve(IReadOnlyList<(float Time, float Value)> keys, float time)
    {
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        for (var i = 1; i < keys.Count; i++)
        {
            if (time > keys[i].Time) continue;
            var left = keys[i - 1];
            var right = keys[i];
            var span = Math.Max(0.000001f, right.Time - left.Time);
            var t = Math.Clamp((time - left.Time) / span, 0f, 1f);
            // UE's curve keys in these clips are authored at every sample, so a linear sample
            // mirrors their rendered value without having to reconstruct private tangent types.
            return left.Value + (right.Value - left.Value) * t;
        }
        return keys[^1].Value;
    }

    /// <summary>
    /// Returns the sequence played by an animation asset. Face blueprints use both UAnimSequence
    /// and UAnimMontage assets; the latter stores its playable source under
    /// SlotAnimTracks -> AnimTrack -> AnimSegments -> AnimReference. Reading the cooked data here
    /// follows the same asset relationship the post-process AnimBP uses at runtime.
    /// </summary>
    private static UAnimSequence? ResolveAnimationSequence(UObject source)
    {
        if (source is UAnimSequence direct)
        {
            return direct;
        }

        static FStructFallback? StructValue(object? value) =>
            value as FStructFallback
            ?? (value as FScriptStruct)?.StructType as FStructFallback;

        var tracks = source.GetOrDefault<UScriptArray>("SlotAnimTracks");
        foreach (var trackEntry in tracks?.Properties ?? [])
        {
            var track = StructValue(trackEntry.GenericValue);
            var animTrackValue = track?.Properties
                .FirstOrDefault(p => p.Name.Text.Equals("AnimTrack", StringComparison.OrdinalIgnoreCase))
                ?.Tag?.GenericValue;
            var animTrack = StructValue(animTrackValue);
            var segments = animTrack?.GetOrDefault<UScriptArray>("AnimSegments");
            foreach (var segmentEntry in segments?.Properties ?? [])
            {
                var segment = StructValue(segmentEntry.GenericValue);
                var reference = segment?.GetOrDefault<FPackageIndex>("AnimReference")
                    ?.ResolvedObject?.Load();
                if (reference is UAnimSequence sequence)
                {
                    return sequence;
                }
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
    /// Reads the face material's "Enable Zone NN (Feature)" static switches, which are the game's
    /// OWN statement of which ExtraUV0 band draws which feature and whether it is enabled at all.
    /// The zone number IS the band number. Walks the parent chain so inherited zones are seen.
    ///
    /// This replaces guessing band -> feature from geometry: a character with no "Enable Zone" for
    /// a layer simply does not draw it (which is why Bruce Wayne must not show the Jim Gordon
    /// stubble bound to his HeadLowerOver parameter).
    /// </summary>
    /// Every static switch (name, value) on the instance and its parent chain. The child instance is
    /// yielded first, so callers naturally see its override before an inherited controller default.
    private static IEnumerable<(string Name, bool Value)> MaterialStaticSwitches(UObject? material, int depth = 0)
    {
        while (material is not null && depth++ < 6)
        {
            var statics = material.GetOrDefault<FStructFallback>("StaticParametersRuntime");
            foreach (var prop in statics?.Properties ?? new List<CUE4Parse.UE4.Assets.Objects.FPropertyTag>())
            {
                if (prop.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.UScriptArray arr)
                {
                    continue;
                }
                foreach (var item in arr.Properties)
                {
                    // Entries come through either as a bare FStructFallback or wrapped in an
                    // FScriptStruct depending on the property tag - unwrap both.
                    var sf = item.GenericValue as FStructFallback
                             ?? (item.GenericValue as CUE4Parse.UE4.Assets.Objects.FScriptStruct)
                                ?.StructType as FStructFallback;
                    var name = sf?.GetOrDefault<FStructFallback>("ParameterInfo")
                                 ?.GetOrDefault<FName>("Name").Text;
                    if (name is not null)
                    {
                        yield return (name, sf!.GetOrDefault<bool>("Value"));
                    }
                }
            }
            material = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load();
        }
    }

    private static bool? FindStaticSwitch(UObject? material, string name)
    {
        foreach (var (candidate, value) in MaterialStaticSwitches(material))
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    /// The complete zone vocabulary, read off all 312 MI_FACE_* materials in the shipped paks.
    /// A character's own instance only lists the zones it OVERRIDES, so zones it inherits from the
    /// (stripped) master material never appear in its switch list - this table is how those are
    /// recovered. Zone number == the ExtraUV0 integer band on SK_LEGOface.
    /// The colours the master material defaults to, recovered by censusing every "<feature> Tint"
    /// set across all 312 face materials in the shipped paks (probe mode "facetints:"). Cooking
    /// strips the master's own defaults, but where dozens of characters agree on a value that value
    /// IS the intended one - measured rather than guessed. Notably no character ever tints an eye:
    /// the eye is a white sclera with separate pupil/eyelid shells, not one dark disc.
    private static readonly Dictionary<string, Color> FaceFeatureDefaultTint = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BrowFull"] = Color.FromArgb(255, 0x42, 0x24, 0x1B),
        ["BrowL"] = Color.FromArgb(255, 0x04, 0x04, 0x05),       // n=174, #040405 x49
        ["BrowR"] = Color.FromArgb(255, 0x04, 0x04, 0x05),       // n=169, #040405 x44
        ["BrowUnder"] = Color.FromArgb(255, 0xA4, 0x3F, 0x18),
        ["EyeL"] = Color.FromArgb(255, 0x04, 0x04, 0x05),        // M_LEGOface default linear #040405
        ["EyeR"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["EyeBackL"] = Color.FromArgb(255, 0x6B, 0x2A, 0x10),    // "Eye L Back Tint" n=17
        ["EyeBackR"] = Color.FromArgb(255, 0x6B, 0x2A, 0x10),
        ["EyelidUpperL"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["EyelidUpperR"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["EyelidLowerL"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["EyelidLowerR"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["Glasses"] = Color.FromArgb(255, 0x05, 0x04, 0x05),     // n=38
        ["HeadLowerOver"] = Color.FromArgb(255, 0x05, 0x04, 0x05),
        ["HeadLowerUnder"] = Color.FromArgb(255, 0xA4, 0x3F, 0x18),
        ["HeadUpperOver"] = Color.FromArgb(255, 0x0F, 0x07, 0x06),
        ["HeadUpperUnder"] = Color.FromArgb(255, 0xA4, 0x3F, 0x18),
        ["LashL"] = Color.FromArgb(255, 0x05, 0x04, 0x05),
        ["LashR"] = Color.FromArgb(255, 0x05, 0x04, 0x05),
        ["MaskSuperhero"] = Color.FromArgb(255, 0x04, 0x04, 0x05),
        ["Mouth"] = Color.FromArgb(255, 0x04, 0x04, 0x05),       // n=5, #040405 x3
        ["MouthInside"] = Color.FromArgb(255, 0x00, 0x08, 0x16),
        ["TeethT"] = Color.FromArgb(255, 0xE7, 0xD6, 0xC0),     // master Eye/Teeth cream, linear
        ["TeethB"] = Color.FromArgb(255, 0xE7, 0xD6, 0xC0),
        ["Tongue"] = Color.FromArgb(255, 0x8D, 0x00, 0x00),      // master Tongue Tint, linear
    };

    /// The master's own layer ordering. UE pushes each coincident shell back with a Pixel Depth
    /// Offset; these are the measured values, and using them replaces guessing at polygon offsets.
    private static readonly Dictionary<string, float> FaceFeaturePdo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HeadLowerOver"] = 0.15f, ["Mouth"] = 0.2f, ["MouthInside"] = 0.2f,
        ["EyeL"] = 0.5f, ["EyeR"] = 0.5f,
        ["EyelidUpperL"] = 0.6f, ["EyelidUpperR"] = 0.6f,
        ["EyelidLowerL"] = 0.6f, ["EyelidLowerR"] = 0.6f,
        ["BrowUnder"] = 0.9f, ["Glasses"] = 1.0f,
        ["HeadUpperUnder"] = 1.4f, ["HeadUpperOver"] = 1.4f,
        ["EyeBackL"] = 1.5f, ["EyeBackR"] = 1.5f,
    };

    /// One ExtraUV0 band of the face, with EVERY map its material declares - not just the base
    /// colour. The cooked instance still carries BC/NML/MMR (plus Prestine variants), the roughness
    /// and metallic scalars and the emissive set; binding all of them is what makes a face render
    /// as the game's material rather than as a flat coloured decal.
    public sealed record FaceBand(
        int Band, int Tris, string? Tex, Color? Tint, int Mode, string Feature, float Pdo,
        string? Nrm = null, string? Orm = null, float? Roughness = null, float? Metallic = null,
        string? Emissive = null, Color? EmissiveColour = null, float? EmissiveStrength = null,
        FaceUvLayer? EyeSpecLayer = null,
        FaceMouthLayers? MouthLayers = null);

    /// <summary>
    /// One of the mouth's material-only layers. Teeth and tongue share Mouth's geometry/UV shell;
    /// Unreal moves their sheets with the scalar parameters named by <see cref="CurvePrefix"/>.
    /// </summary>
    public sealed record FaceUvLayer(
        string? Tex, Color Tint, string CurvePrefix,
        float OffsetU, float OffsetV, float Rotation, float ScaleU, float ScaleV);

    /// <summary>
    /// The master M_LEGOface shader composites these three maps over the black mouth rim. They are
    /// kept with band 13 so all three remain correctly skinned by the same lip bones.
    /// </summary>
    public sealed record FaceMouthLayers(FaceUvLayer? TeethU, FaceUvLayer? TeethD, FaceUvLayer? Tongue);

    private static readonly Dictionary<int, string> FaceZoneVocabulary = new()
    {
        [0] = "BrowFull", [1] = "BrowL", [2] = "BrowR", [3] = "BrowUnder",
        [4] = "EyeL", [5] = "EyeR", [6] = "Glasses",
        [7] = "HeadLowerOver", [8] = "HeadLowerUnder",
        [9] = "HeadUpperOver", [10] = "HeadUpperUnder",
        [11] = "LashL", [12] = "LashR", [13] = "Mouth", [14] = "MouthInside",
        // 15-22 carry no name in the master's switches ("Enable Zone 16", ...). These are pinned by
        // matching each band's dominant skin weight against the parameter families: the band driven
        // by EyelidDeform_DL is EyelidLowerL, the band driven by EyeBack_L is Eye Back L, and so on.
        [15] = "Zone15", [16] = "EyelidLowerL", [17] = "EyelidLowerR",
        [18] = "EyelidUpperL", [19] = "EyelidUpperR", [20] = "Zone20",
        [21] = "EyeBackL", [22] = "EyeBackR",
    };

    private static Dictionary<int, string> EnabledFaceZones(UObject? material, int depth = 0)
    {
        var zones = new Dictionary<int, string>();
        foreach (var (name, value) in MaterialStaticSwitches(material, depth))
        {
            if (!value)
            {
                continue;
            }
            var m = System.Text.RegularExpressions.Regex.Match(
                name, @"^Enable Zone (\d+)\s*\(([^)]+)\)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var zone))
            {
                zones.TryAdd(zone, m.Groups[2].Value.Trim());
            }
        }
        return zones;
    }

    /// "<feature> CustomColour Off/On" is the game's own per-character declaration that a face zone
    /// is RECOLOURED. Every zone that ships a "<feature> Tint" sets it; the zones that do not
    /// (eyes, mouth interior, lashes) deliberately keep the master material's colour, which for a
    /// printed feature is near-black. Reading the switch rather than guessing from the presence of
    /// a tint is what makes the face pipeline correct for any character without calibration.
    private static HashSet<string> TintedFaceFeatures(UObject? material)
    {
        var tinted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in MaterialStaticSwitches(material))
        {
            if (!value)
            {
                continue;
            }
            var m = System.Text.RegularExpressions.Regex.Match(
                name, @"^(.+?)\s+CustomColour\s+Off/On$");
            if (m.Success)
            {
                tinted.Add(m.Groups[1].Value.Trim());
            }
        }
        return tinted;
    }

    /// <summary>
    /// Candidate parameter names for a zone's feature. The switch spells it "EyeL" while the
    /// parameter is "Eye L BC", so a couple of spacing variants are tried.
    /// </summary>
    private static IEnumerable<string> FeatureNameVariants(string feature)
    {
        yield return feature;
        var spaced = System.Text.RegularExpressions.Regex.Replace(feature, @"^(Eye|Brow|Lash)([LR])$", "$1 $2");
        if (spaced != feature)
        {
            yield return spaced;
        }
        var eyeBack = System.Text.RegularExpressions.Regex.Match(feature, @"^EyeBack([LR])$");
        if (eyeBack.Success)
        {
            yield return $"Eye Back {eyeBack.Groups[1].Value}";
            yield return $"Eye {eyeBack.Groups[1].Value} Back";
        }
        var eyelid = System.Text.RegularExpressions.Regex.Match(feature, @"^Eyelid(Upper|Lower)([LR])$");
        if (eyelid.Success)
        {
            yield return $"Eyelid{eyelid.Groups[1].Value} {eyelid.Groups[2].Value}";
            yield return $"Eyelid {eyelid.Groups[1].Value} {eyelid.Groups[2].Value}";
        }
        if (feature.StartsWith("Mouth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Mouth";
        }
    }

    /// <summary>Face material of the current build, so feature bands can read their own params.</summary>
    private static UObject? _faceMaterial;

    /// <summary>Captured neutral values for the current face material, when one is bundled.</summary>
    private static RuntimeFaceProfileService.FaceProfile? _faceBaseline;

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

    /// The face mesh this character actually wears. The game ships five (SK_LEGOface,
    /// _Superhero, _Batman, _Joker89, _MrFreeze_BaR) and the rig decides which shared expression
    /// set applies - a Superhero face is posed by the _Superhero sequences, everyone else by the
    /// generic ones. Previously every character fell through to Superhero.
    private static string? _faceMeshPath;

    /// Folder the character's own face anim blueprint points at, e.g. Bruce Wayne's
    /// ABP_LEGOface_BruceWayne references /Game/Animation/LEGOface/LEGOface_Batman/. A cooked anim
    /// blueprint keeps its dependency list even though the graph is stripped, so this is the
    /// character's own declaration of where its expressions live.
    private static string? _faceAnimHome;

    /// Reads the character's face anim blueprint and returns the LEGOface animation folder it
    /// references, or null when it ships none.
    private static string? ResolveFaceAnimHome(DefaultFileProvider provider, string? who)
    {
        if (string.IsNullOrWhiteSpace(who))
        {
            return null;
        }
        try
        {
            var pkg = provider.LoadPackage(
                $"/Game/Characters/Attachments/LEGOface/ABP_LEGOface_{who}");
            if (pkg is not CUE4Parse.UE4.Assets.IoPackage io)
            {
                return null;
            }
            foreach (var dep in io.ImportedPackages.Value)
            {
                var n = dep?.Name;
                if (n is null) continue;
                var m = System.Text.RegularExpressions.Regex.Match(
                    n, @"^(/Game/Animation/LEGOface/[^/]+)/");
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
        }
        catch
        {
            // No face anim blueprint for this character - fall back to the shared sets.
        }
        return null;
    }

    /// <summary>
    /// True for the placeholder textures the game binds to features a character does not use
    /// (T_Dummy_Alpha_Off / T_Dummy_NML). They are fully transparent, so the feature draws nothing.
    /// </summary>
    private static bool IsDummyTexture(UTexture2D t) =>
        t.Name.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("Alpha_Off", StringComparison.OrdinalIgnoreCase);

    // The cooked M_LEGOface has no serialised expression graph, but its streaming table names these
    // direct master defaults. Ordinary faces inherit this distressed mouth stencil even when their
    // instance's Mouth BC override is a dummy.
    private const string DefaultMouthTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Mouth_DIST_BC";
    private const string DefaultMouthNormalTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Mouth_DIST_DNRM";
    private const string DefaultTeethUTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Teeth_U_BC";
    private const string DefaultTeethDTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Teeth_D_BC";
    private const string DefaultTongueTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Tongue_DIST_BC";
    private const string DefaultEyeSpecTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_EyeSpec_BC";
    private const string DefaultEyeTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Eye_DIST_BC";
    private const string DefaultEyeNormalTexPath =
        "/Game/Characters/Textures/Attachments/LEGOface/T_LEGOface_Eye_DIST_DNRM";

    /// <summary>
    /// Resolve one animated layer that the cooked master normally supplies itself. Material
    /// instances may override the art or tint (Oswald's teeth, for example); when they do not, use
    /// the exact default texture imported by M_LEGOface rather than substituting a drawn shape.
    /// </summary>
    private static FaceUvLayer? ResolveMouthLayer(
        DefaultFileProvider provider, string previewDir, UObject? material,
        string materialPrefix, string curvePrefix, string defaultTexturePath,
        Color defaultTint, float defaultOffsetV)
    {
        var texture = FindFirstRealTexture(material,
            materialPrefix + " BC Prestine", materialPrefix + " BC");
        if (texture is null)
        {
            try { texture = provider.LoadPackageObject(defaultTexturePath) as UTexture2D; }
            catch (Exception ex)
            {
                Console.WriteLine($"      {materialPrefix} default texture failed: {ex.Message.Split('\n')[0]}");
            }
        }
        if (texture is null)
        {
            return null;
        }

        var rel = "textures/" + MakeSafeName(texture.Name) + "_mouth-layer.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(dest) && !TextureDecodeService.TryExportPng(texture, dest, keepAlpha: true))
        {
            return null;
        }

        float Scalar(string suffix, float fallback) =>
            FindFaceScalarParam(material, curvePrefix + suffix) ?? fallback;
        return new FaceUvLayer(
            rel,
            FindColourParam(material, materialPrefix + " Tint", 0) ?? defaultTint,
            curvePrefix.ToLowerInvariant(),
            Scalar("OffsetU", 0f),
            Scalar("OffsetV", defaultOffsetV),
            Scalar("Rotate", 0f),
            Scalar("ScaleU", 1f),
            Scalar("ScaleV", 1f));
    }

    /// <summary>
    /// The eye highlight is another master-material layer: a large cream disc moved across the dark
    /// eye stencil by the EyeSpecL/R scalars and expression curves. The overlap creates the small
    /// LEGO highlight crescent, so use the shipped map even when a character instance stores a
    /// dummy placeholder.
    /// </summary>
    private static FaceUvLayer? ResolveEyeSpecLayer(
        DefaultFileProvider provider, string previewDir, UObject? material, char side)
    {
        var texture = FindFirstRealTexture(material,
            $"Eye {side} Spec BC Prestine", $"Eye {side} Spec BC");
        if (texture is null)
        {
            try { texture = provider.LoadPackageObject(DefaultEyeSpecTexPath) as UTexture2D; }
            catch (Exception ex)
            {
                Console.WriteLine($"      Eye {side} Spec default texture failed: {ex.Message.Split('\n')[0]}");
            }
        }
        if (texture is null)
        {
            return null;
        }

        var rel = "textures/" + MakeSafeName(texture.Name) + $"_eye-spec-{char.ToLowerInvariant(side)}.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(dest) && !TextureDecodeService.TryExportPng(texture, dest, keepAlpha: true))
        {
            return null;
        }

        var scalarPrefix = "EyeSpec" + side;
        float Scalar(string suffix, float fallback) =>
            FindFaceScalarParam(material, scalarPrefix + suffix) ?? fallback;
        return new FaceUvLayer(
            rel,
            FindColourParam(material, $"Eye {side} Spec Tint", 0)
            ?? Color.FromArgb(255, 0xE7, 0xD6, 0xC0),
            "eyespec" + char.ToLowerInvariant(side),
            Scalar("OffsetU", 0f),
            Scalar("OffsetV", -0.184f),
            Scalar("Rotate", 0f),
            Scalar("ScaleU", 1f),
            Scalar("ScaleV", 1f));
    }

    private static UTexture2D? FindFirstRealTexture(UObject? material, params string[] slots)
    {
        foreach (var slot in slots)
        {
            var candidate = FindTextureParam(material, slot, 0);
            if (candidate is not null && !IsDummyTexture(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
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

            // Most face bands take their texture from the character material. Mouth is different:
            // ordinary faces inherit the master mouth stencil, while their instance serialises a
            // dummy override. MouthHide, not that dummy, is the game-side opt-out.
            string? mouthRel = null;
            var mouthHidden = (FindFaceScalarParam(_faceMaterial, "MouthHide") ?? 0f) > 0.5f;
            var faceMaterial = _faceMaterial;
            var mouthTex = FindFirstRealTexture(faceMaterial, "Mouth BC Prestine", "Mouth BC");
            if (!mouthHidden && mouthTex is null)
            {
                try { mouthTex = provider.LoadPackageObject(DefaultMouthTexPath) as UTexture2D; }
                catch (Exception ex)
                {
                    Console.WriteLine($"  face master mouth texture failed: {ex.Message.Split('\n')[0]}");
                }
            }
            try
            {
                if (mouthTex is not null)
                {
                    mouthRel = "textures/" + MakeSafeName(mouthTex.Name) + "_mouth.png";
                    var dest = Path.Combine(previewDir, mouthRel.Replace('/', Path.DirectorySeparatorChar));
                    // M_LEGOface needs the texture's original alpha stencil.
                    if (!File.Exists(dest) && !TextureDecodeService.TryExportPng(mouthTex, dest, keepAlpha: true))
                    {
                        mouthRel = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  face mouth texture failed: {ex.Message.Split('\n')[0]}");
            }

            // Mouth, upper teeth, lower teeth, and tongue all share band 13's skinned lip shell.
            // The master material imports these defaults directly, even when an MI stores dummies;
            // resolve real per-character overrides first and only then use those master textures.
            FaceMouthLayers? mouthLayers = null;
            if (!mouthHidden && mouthRel is not null)
            {
                var upper = ResolveMouthLayer(provider, previewDir, faceMaterial,
                    "Teeth T", "TeethU", DefaultTeethUTexPath, Color.White, defaultOffsetV: -20f);
                var lower = ResolveMouthLayer(provider, previewDir, faceMaterial,
                    "Teeth B", "TeethD", DefaultTeethDTexPath, Color.White, defaultOffsetV: 17.90631f);
                var tongue = ResolveMouthLayer(provider, previewDir, faceMaterial,
                    "Tongue", "Tongue", DefaultTongueTexPath, FaceFeatureDefaultTint["Tongue"], defaultOffsetV: -7f);
                mouthLayers = new FaceMouthLayers(upper, lower, tongue);
                Console.WriteLine("  face mouth layers: " + string.Join(", ", new[]
                {
                    upper is null ? null : "teeth U",
                    lower is null ? null : "teeth D",
                    tongue is null ? null : "tongue",
                }.Where(x => x is not null)));
            }

            // Give every band the texture its own material parameter names. A band whose feature the
            // character does not use resolves to a dummy (fully transparent) and is left untextured,
            // so cowled faces stay clean while ordinary faces get their brows, eyes and upper layer.
            // The material tells us which zone (= ExtraUV0 band) is which feature and whether it
            // is enabled; anything without an enabled zone is simply not drawn.
            var zones = EnabledFaceZones(faceMaterial);
            var tinted = TintedFaceFeatures(faceMaterial);
            Console.WriteLine($"  face material: {faceMaterial?.GetPathName() ?? "(null)"}");
            Console.WriteLine("  face zones tinted (CustomColour): " + (tinted.Count == 0
                ? "(none)" : string.Join(", ", tinted)));
            Console.WriteLine("  face zones enabled: " + (zones.Count == 0
                ? "(none declared)"
                : string.Join(", ", zones.OrderBy(z => z.Key).Select(z => $"{z.Key}={z.Value}"))));

            // Only zones the material SWITCHES ON are drawn. A bound texture proves nothing: 71 of
            // the 125 materials that bind a HeadLowerOver print never enable zone 7, and 54 bind a
            // lash they never enable. Leftover bindings are why a female lash once showed up on
            // Bruce's face.
            var draw = new Dictionary<int, string>(zones);

            // Zone 13 is the animated mouth shell. Its master switch defaults on but that default
            // is stripped during cooking, so it is absent from ordinary material instances such as
            // Bruce's. Restore it whenever the material has not explicitly hidden the mouth.
            if (!mouthHidden && mouthRel is not null && !draw.ContainsKey(13))
            {
                draw[13] = "Mouth";
                Console.WriteLine("  face: Zone 13 Mouth inherited from M_LEGOface");
            }

            // Do not infer EyeBack from EyeL/EyeR. The stripped master material's eye-back defaults
            // are not present in cooked data; putting the generic eye oval on those shells creates
            // the solid double-dark disks visible in the old preview. Explicitly enabled/bound
            // eye-back layers still render below, but absent data now means absent artwork.
            var bands = new List<FaceBand>();
            foreach (var (band, tris) in GlbInspector.FaceBandLayout)
            {
                string? rel = null;
                Color? tint = null;
                var additive = false;
                UTexture2D? printSource = null;
                if (draw.TryGetValue(band, out var feature))
                {
                    var collapsedMouthInside = feature.Equals("MouthInside", StringComparison.OrdinalIgnoreCase)
                                               && tris <= 1;
                    var unsupportedMouth = feature.Equals("Mouth", StringComparison.OrdinalIgnoreCase)
                                           && (mouthHidden || mouthRel is null || mouthTex is null);
                    // Every face feature is an alpha stencil. Some layers are conceptually "Over",
                    // but their texture alpha is still the cutout; treating them as additive RGB
                    // overlays ignores the measured alpha path and changes the game's layering.
                    additive = false;
                    if (!collapsedMouthInside && !unsupportedMouth)
                    {
                        if (feature.Equals("Mouth", StringComparison.OrdinalIgnoreCase))
                        {
                            rel = mouthRel;
                            printSource = mouthTex;
                            tint = FindColourParam(faceMaterial, "Mouth Tint", 0)
                                   ?? FaceFeatureDefaultTint["Mouth"];
                            Console.WriteLine($"      zone {band} (Mouth) -> inherited {mouthTex!.Name}");
                        }
                        foreach (var variant in FeatureNameVariants(feature))
                        {
                            if (rel is not null)
                            {
                                break;
                            }
                            // "<feature> BC" is the DISTRESSED variant and is 98-99% transparent; the
                            // artwork is in the sibling the game spells "Prestine".
                            var t = FindTextureParam(faceMaterial, variant + " BC Prestine", 0)
                                    ?? FindTextureParam(faceMaterial, variant + " BC", 0);
                            if (t is null || IsDummyTexture(t))
                            {
                                continue;
                            }
                            var name = "textures/" + MakeSafeName(t.Name) + "_stencil.png";
                            var dest = Path.Combine(previewDir, name.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(dest) || TextureDecodeService.TryExportPng(t, dest, keepAlpha: true))
                            {
                                rel = name;
                                printSource = t;
                                // Only zones the material flags with "CustomColour Off/On" take their
                                // "<feature> Tint" - those are white stencils (brows, skin shells).
                                if (!additive && (tinted.Contains(variant) || tinted.Contains(feature)))
                                {
                                    tint = FindColourParam(faceMaterial, variant + " Tint", 0);
                                }
                                break;
                            }
                        }
                    }

                    if (rel is null && !additive && !collapsedMouthInside && !unsupportedMouth)
                    {
                        // No per-character artwork: fall back to the SHARED feature sheet, which is
                        // what the (stripped) master material points at. Its shape lives in ALPHA.
                        var shared = feature switch
                        {
                            var f when f.Equals("EyeL", StringComparison.OrdinalIgnoreCase)
                                       || f.Equals("EyeR", StringComparison.OrdinalIgnoreCase)
                                => "T_LEGOface_Eye_BC",
                            var f when f.Contains("lid", StringComparison.OrdinalIgnoreCase)
                                => "T_LEGOface_EyelidLower_BC",
                            var f when f.StartsWith("Mouth", StringComparison.OrdinalIgnoreCase)
                                => "T_LEGOface_Mouth_BC",
                            _ => null,
                        };
                        try
                        {
                            if (shared is not null && provider.LoadPackageObject(
                                    "/Game/Characters/Textures/Attachments/LEGOface/" + shared) is UTexture2D st)
                            {
                                var sname = "textures/" + MakeSafeName(st.Name) + "_stencil.png";
                                var sdest = Path.Combine(previewDir, sname.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(sdest)
                                    || TextureDecodeService.TryExportPng(st, sdest, keepAlpha: true))
                                {
                                    rel = sname;
                                    printSource = st;
                                    Console.WriteLine($"      zone {band} ({feature}) -> shared {shared}");
                                }
                            }
                        }
                        catch
                        {
                            // Shared sheet missing - fall through to the flat fill below.
                        }
                    }

                    if (tint is null && !additive && !collapsedMouthInside && !unsupportedMouth
                        && !feature.StartsWith("EyeBack", StringComparison.OrdinalIgnoreCase))
                    {
                        // The zone draws but is not flagged "CustomColour", so its colour is the
                        // master material's default - which cooking strips. These sheets are white
                        // masks (the eye is a filled ellipse, the mouth a ring), so leaving them
                        // untinted renders a white blob; the LEGO default for a print is near-black.
                        tint = FaceFeatureDefaultTint.TryGetValue(feature.Replace(" ", ""), out var def)
                            ? def
                            : Color.FromArgb(255, 24, 22, 20);
                        Console.WriteLine($"      zone {band} ({feature}) not CustomColour -> master default "
                                          + $"#{tint.Value.R:X2}{tint.Value.G:X2}{tint.Value.B:X2}");
                    }
                }

                // EyeSpec belongs to the master material, not necessarily the character instance.
                // It must receive the same curve-driven transform as the game; using it at a static
                // offset is what previously painted the full eye white instead of a small crescent.
                FaceUvLayer? eyeSpecLayer = null;
                if (rel is not null && draw.TryGetValue(band, out var eyeFeature)
                    && (eyeFeature.Equals("EyeL", StringComparison.OrdinalIgnoreCase)
                        || eyeFeature.Equals("EyeR", StringComparison.OrdinalIgnoreCase)))
                {
                    var side = eyeFeature[^1];
                    eyeSpecLayer = ResolveEyeSpecLayer(provider, previewDir, faceMaterial, side);
                    if (eyeSpecLayer is not null)
                    {
                        Console.WriteLine($"      zone {band} ({eyeFeature}) -> master eye spec "
                                          + $"offset=({eyeSpecLayer.OffsetU:0.###},{eyeSpecLayer.OffsetV:0.###})");
                    }
                }

                // Bind the REST of the material for this feature, not just its base colour: the
                // normal map (the prints are raised ink), the MMR (metal/roughness, repacked to ORM
                // for three.js), the roughness/metallic scalars and the emissive set.
                string? nrmRel = null, ormRel = null, emisRel = null;
                float? roughness = null, metallic = null, emisStrength = null;
                Color? emisColour = null;
                if (draw.TryGetValue(band, out var mf))
                {
                    foreach (var variant in FeatureNameVariants(mf))
                    {
                        var nml = FindTextureParam(faceMaterial, variant + " NML Prestine", 0)
                                  ?? FindTextureParam(faceMaterial, variant + " NML", 0);
                        if (nrmRel is null && nml is not null && !IsDummyTexture(nml))
                        {
                            var n = "textures/" + MakeSafeName(nml.Name) + "_nrm.png";
                            var d = Path.Combine(previewDir, n.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(d) || TextureDecodeService.TryExportPng(nml, d, reconstructNormalZ: true))
                            {
                                nrmRel = n;
                            }
                        }
                        var mmr = FindTextureParam(faceMaterial, variant + " MMR Prestine", 0)
                                  ?? FindTextureParam(faceMaterial, variant + " MMR", 0);
                        if (ormRel is null && mmr is not null && !IsDummyTexture(mmr))
                        {
                            var n = "textures/" + MakeSafeName(mmr.Name) + "_orm.png";
                            var d = Path.Combine(previewDir, n.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(d) || TextureDecodeService.TryExportMmrAsOrm(mmr, d))
                            {
                                ormRel = n;
                            }
                        }
                        var em = FindTextureParam(faceMaterial, variant + " Emissive", 0);
                        if (emisRel is null && em is not null && !IsDummyTexture(em))
                        {
                            var n = "textures/" + MakeSafeName(em.Name) + "_emis.png";
                            var d = Path.Combine(previewDir, n.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(d) || TextureDecodeService.TryExportPng(em, d, keepAlpha: true))
                            {
                                emisRel = n;
                            }
                        }
                        roughness ??= FindScalarParam(faceMaterial, variant + " Roughness", 0);
                        metallic ??= FindScalarParam(faceMaterial, variant + " Metallic", 0);
                        emisStrength ??= FindScalarParam(faceMaterial, variant + " Emissive Strength", 0);
                        emisColour ??= FindColourParam(faceMaterial, variant + " Emissive Custom Colour", 0);
                    }
                    if (mf.Equals("Mouth", StringComparison.OrdinalIgnoreCase))
                    {
                        if (nrmRel is null)
                        {
                            try
                            {
                                if (provider.LoadPackageObject(DefaultMouthNormalTexPath) is UTexture2D mouthNormal)
                                {
                                    var n = "textures/" + MakeSafeName(mouthNormal.Name) + "_nrm.png";
                                    var d = Path.Combine(previewDir, n.Replace('/', Path.DirectorySeparatorChar));
                                    if (File.Exists(d) || TextureDecodeService.TryExportPng(
                                            mouthNormal, d, reconstructNormalZ: true))
                                    {
                                        nrmRel = n;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"      zone {band} (Mouth) normal failed: "
                                                  + ex.Message.Split('\n')[0]);
                            }
                        }
                        roughness ??= 0.15f;
                    }
                    if (nrmRel is not null || ormRel is not null || roughness is not null)
                    {
                        Console.WriteLine($"      zone {band} ({mf}) maps:"
                                          + (nrmRel is not null ? " NML" : "")
                                          + (ormRel is not null ? " MMR" : "")
                                          + (roughness is not null ? $" rough={roughness}" : "")
                                          + (metallic is not null ? $" metal={metallic}" : "")
                                          + (emisRel is not null ? " EMISSIVE" : ""));
                    }
                }

                // No pre-baking: the viewer binds the raw stencil (shape in alpha) and applies the
                // tint as material.color, so the normal/MMR maps below can light it as a surface.
                var mode = additive ? 1 : 0;

                bands.Add(new FaceBand(band, tris, rel, tint, mode,
                    draw.TryGetValue(band, out var fname) ? fname : $"Zone{band}",
                    FaceFeaturePdo.TryGetValue(draw.TryGetValue(band, out var pf) ? pf : "", out var pdo) ? pdo : 0f,
                    nrmRel, ormRel, roughness, metallic, emisRel, emisColour, emisStrength,
                    eyeSpecLayer,
                    string.Equals(draw.TryGetValue(band, out var mouthFeature) ? mouthFeature : null, "Mouth",
                        StringComparison.OrdinalIgnoreCase) ? mouthLayers : null));
            }
            Console.WriteLine("  face bands (* = textured): " + string.Join(", ",
                bands.Select(b => $"{b.Band}:{b.Tris}{(b.Tex is null ? "" : "*")}{(b.Tint is null ? "" : "t")}{(b.Mode == 2 ? "x" : b.Mode == 1 ? "+" : "")}")));
            Console.WriteLine($"  face mesh: {_faceMeshPath ?? "(unknown)"}"
                              + (_faceMeshPath?.Contains("Superhero", StringComparison.OrdinalIgnoreCase) == true
                                 ? "  -> SUPERHERO rig" : "  -> standard rig"));
            placed[i] = placed[i] with
            {
                FaceGroups = groups, MouthTex = mouthRel, Bands = bands,
                MouthHidden = mouthHidden,
            };
        }
        return placed;
    }

    /// <summary>
    /// Exports a material and returns the preview-relative path of the texture to use as base colour.
    ///
    /// CUE4Parse does NOT embed textures in the .glb, so the preview has to export the material's
    /// actual base-colour input separately. CT is intentionally not part of that selection: it is a
    /// channel/control texture in the EoM controller family, not an albedo map.
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

    private static string? ExportMaterialTexture(UObject? material, string previewDir)
    {
        return ExportBaseColourTexture(FindBaseColourTexture(material, 0), previewDir);
    }

    private static string? ExportBaseColourTexture(UTexture2D? tex, string previewDir)
    {
        if (tex is null)
        {
            return null;
        }

        // A tiny texture (e.g. the cowl's 8x8 T_LEGO_Black17) IS the flat plastic colour, not a print.
        // A large sheet is the decal layer and needs a plastic colour underneath it.
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
        Console.WriteLine($"    base colour export failed: {tex.Name} ({tex.Format})");
        return null;

        static string MakeSafe(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    /// <summary>
    /// LEGO minifig skin tone, taken from the face material's "HeadLowerUnder Tint" (#D28856). Used
    /// for the head piece, whose own material slot is empty in the shipped asset.
    /// </summary>
    private static readonly Color SkinTone = Color.FromArgb(255, 0xD2, 0x88, 0x56);

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
        bool Hidden = false, bool Cutout = false, string? Nrm2 = null, string? Ao = null,
        float? Roughness = null, float? Metalness = null, string? ColourMask = null);

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
    /// Scalar parameter off the instance chain - "<feature> Roughness", "<feature> Metallic",
    /// "<feature> Emissive Strength". The face material sets Roughness 0.3 almost everywhere.
    private static float? FindScalarParam(UObject? material, string name, int depth)
    {
        if (material is null || depth > 5)
        {
            return null;
        }
        var ps = material.GetOrDefault<FStructFallback[]>("ScalarParameterValues");
        foreach (var entry in ps ?? Array.Empty<FStructFallback>())
        {
            var pn = entry.GetOrDefault<FStructFallback>("ParameterInfo")?.GetOrDefault<FName>("Name").Text;
            if (string.Equals(pn, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.GetOrDefault<float>("ParameterValue");
            }
        }
        return FindScalarParam(material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load(), name, depth + 1);
    }

    private static float? FindFaceScalarParam(UObject? material, string name)
    {
        if (ReferenceEquals(material, _faceMaterial) && _faceBaseline?.TryGetScalar(name, out var value) == true)
        {
            return value;
        }
        return FindScalarParam(material, name, 0);
    }

    private static Color? FindColourParam(UObject? material, string name, int depth)
    {
        if (material is null || depth > 5)
        {
            return null;
        }
        return FindColourParamOnMaterial(material, name)
               ?? FindColourParam(material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load(), name, depth + 1);
    }

    private static Color? FindColourParamOnMaterial(UObject material, string name)
    {
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
        return null;
    }

    /// <summary>Resolves one material slot to a texture or a flat colour, plus its normal/MMR maps.</summary>
    private static SlotShading ResolveSlot(
        DefaultFileProvider provider,
        UObject? material,
        string previewDir,
        PreviewMaterialFallback? fallback = null)
    {
        if (material is null)
        {
            return new SlotShading(null, null, null, null);
        }

        // Capes are woven cloth: the base M_Cape_EoM graph (stripped from the cooked build, wiring
        // recovered from a near-exact Blender recreation) shades them from the shared PongeeFabric
        // texture set, not from any parameter on the instance. Bake those instead of the generic path.
        var fallbackColour = FindFallbackColour(fallback);
        if (IsCapeMaterial(material, fallback))
        {
            return ResolveCapeSlot(provider, material, previewDir, fallback);
        }

        // The game's own BaseColour_SolidColour switch distinguishes a vector-coloured piece from
        // a real BC atlas. It applies to hair, hats, and other attachment parts, so use it instead
        // of guessing from an asset name or the presence of CT (which is a control map, not albedo).
        if (ResolveSolidColourSlot(provider, material, previewDir, fallback) is { } solidColourSlot)
        {
            return fallbackColour is null ? solidColourSlot : solidColourSlot with { Colour = fallbackColour };
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

        var sourceBaseColour = ExportFallbackSourceTexture(fallback, BaseColourSlots, previewDir);
        var fallbackBaseColour = sourceBaseColour is null
            ? FindFallbackTexture(provider, fallback, BaseColourSlots)
            : null;
        var tex = fallbackColour is not null && sourceBaseColour is null && fallbackBaseColour is null
            ? null
            : sourceBaseColour ?? ExportBaseColourTexture(fallbackBaseColour ?? FindBaseColourTexture(material, 0), previewDir);
        var normal = ExportFallbackSourceTexture(
                         fallback,
                         new[] { "DNRM_Pristine", "DNRM", "HeadLowerUnder NML", "NRM" },
                         previewDir,
                         isNormal: true)
                     ?? ExportTexture(
                         FindFallbackTexture(provider, fallback, "DNRM_Pristine", "DNRM", "HeadLowerUnder NML"),
                         previewDir,
                         isNormal: true)
                     ?? ExportSlot(material, "DNRM_Pristine", previewDir, isNormal: true)
                     ?? ExportSlot(material, "DNRM", previewDir, isNormal: true)
                     ?? ExportSlot(material, "HeadLowerUnder NML", previewDir, isNormal: true);
        // The DNRM parameter is the material's authored normal map. Only fall back to the baked
        // base normal when it is absent; combining both UV spaces in the preview changes the
        // lighting on atlas materials such as Electric's body.
        if (normal is null)
        {
            normal = BakeNoisedNrm(provider, material, previewDir);
        }
        var mmr = ExportMmrSlot(material, previewDir)
                  ?? ExportFallbackMmrSlot(provider, fallback, previewDir);
        // Prefer the material's explicit colour-mask parameters. CT remains a legacy fallback for
        // older materials that expose their colour channels under that name.
        var colourMask = ExportFallbackSourceTexture(fallback, ["ColourMask", "ColorMask", "CT"], previewDir)
                         ?? ExportSlot(material, "ColourMask", previewDir)
                         ?? ExportSlot(material, "ColorMask", previewDir)
                         ?? ExportSlot(material, "CT", previewDir);

        // No texture anywhere: the material states its colour directly (capes do this). Keep the
        // tint when a texture exists as well; body TPAGEs usually leave it at white.
        Color? colour = ResolveColour(fallback, material,
            "HeadLowerUnder Tint", "Base Color", "BaseColor", "Base Colour", "BaseColour");

        if (tex is null && colour is not null)
        {
            Console.WriteLine($"      flat colour #{colour.Value.R:X2}{colour.Value.G:X2}{colour.Value.B:X2}");
        }
        return new SlotShading(
            tex,
            normal,
            mmr,
            fallbackColour ?? colour,
            ColourMask: colourMask);
    }

    private static Color? FindFallbackColour(PreviewMaterialFallback? fallback, params string[] names)
    {
        if (fallback is null)
        {
            return null;
        }

        if (names.Length == 0)
        {
            names = ["Base Color", "BaseColor", "Base Colour", "BaseColour", "Cape Color", "CapeColor", "Cape Colour", "CapeColour", "HeadLowerUnder Tint"];
        }

        foreach (var name in names)
        {
            if (fallback.ColourOverrides.TryGetValue(name, out var colour))
            {
                return colour;
            }
        }

        return null;
    }

    private static Color? ResolveColour(PreviewMaterialFallback? fallback, UObject material, params string[] names)
    {
        return FindFallbackColour(fallback, names)
               ?? names.Select(name => FindColourParam(material, name, 0)).FirstOrDefault(colour => colour is not null);
    }

    private static UTexture2D? FindFallbackTexture(
        DefaultFileProvider provider,
        PreviewMaterialFallback? fallback,
        params string[] names)
    {
        if (fallback is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!fallback.TextureOverrides.TryGetValue(name, out var path) || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var objectPath = UnrealPathUtil.ObjectPath(path);
                PreviewTrace($"Preview texture lookup: {name} -> {objectPath}.");
                var loaded = provider.LoadPackageObject(objectPath);
                if (loaded is UTexture2D texture)
                {
                    PreviewTrace($"Preview texture loaded: {name} -> {texture.Name} ({texture.Format}).");
                    return texture;
                }
                PreviewTrace($"Preview texture ignored: {name} -> {objectPath} ({loaded.ExportType}).");
            }
            catch (Exception ex)
            {
                PreviewTrace($"Preview texture unavailable: {name} -> {path} ({ex.Message.Split('\n')[0]}).");
            }
        }

        return null;
    }

    private static string? ExportFallbackSourceTexture(
        PreviewMaterialFallback? fallback,
        IEnumerable<string> names,
        string previewDir,
        bool isNormal = false)
    {
        if (fallback is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!fallback.SourceTextureOverrides.TryGetValue(name, out var source) || !File.Exists(source))
            {
                continue;
            }

            var rel = "textures/" + MakeSafeName(Path.GetFileNameWithoutExtension(source))
                + (isNormal ? "_source_nrm.png" : "_source.png");
            var destination = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(source, destination);
            }
            Console.WriteLine($"    generated source texture: {Path.GetFileName(source)} ({name})");
            return rel;
        }

        return null;
    }

    /// <summary>
    /// Resolves a material that explicitly enables BaseColour_SolidColour. This is a material
    /// contract, not a hair special case: its Base Color vector is the albedo, while NRM and RAO
    /// retain the surface definition. A material without this switch continues through the normal
    /// BC/BC_Pristine atlas resolver below.
    /// </summary>
    private static SlotShading? ResolveSolidColourSlot(
        DefaultFileProvider provider,
        UObject material,
        string previewDir,
        PreviewMaterialFallback? fallback)
    {
        if (FindStaticSwitch(material, "BaseColour_SolidColour") != true)
        {
            return null;
        }

        var colour = ResolveColour(fallback, material, "Base Color", "BaseColor", "Base Colour", "BaseColour");
        if (colour is null)
        {
            return null;
        }

        var normal = ExportFallbackSourceTexture(fallback, ["DNRM_Pristine", "DNRM", "NRM"], previewDir, isNormal: true)
                     ?? ExportTexture(FindFallbackTexture(provider, fallback, "DNRM_Pristine", "DNRM", "NRM"), previewDir, isNormal: true)
                     ?? ExportSlot(material, "DNRM_Pristine", previewDir, isNormal: true)
                     ?? ExportSlot(material, "DNRM", previewDir, isNormal: true);
        if (normal is null)
        {
            normal = BakeNoisedNrm(provider, material, previewDir);
        }
        var mmr = ExportMmrSlot(material, previewDir)
                  ?? ExportFallbackMmrSlot(provider, fallback, previewDir);
        var ao = ExportRaoAoSlot(material, previewDir);
        Console.WriteLine($"    solid Base Color: #{colour.Value.R:X2}{colour.Value.G:X2}{colour.Value.B:X2}"
                          + (ao is null ? " (no RAO)" : " + RAO.G AO")
                          + " (CT ignored)");
        return new SlotShading(
            null,
            normal,
            mmr,
            colour,
            Ao: ao,
            Roughness: 0.36f);
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

    /// <summary>True when the instance or its parent chain belongs to the cape cloth family.</summary>
    private static bool IsCapeMaterial(UObject? material, PreviewMaterialFallback? fallback)
    {
        if (!string.IsNullOrWhiteSpace(fallback?.ParentMaterialPath) &&
            IsCapePath(fallback.ParentMaterialPath))
        {
            return true;
        }

        for (var depth = 0; material is not null && depth < 12; depth++)
        {
            if (IsCapePath(material.Name) || IsCapePath(material.GetPathName()))
            {
                return true;
            }
            material = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load();
        }
        return false;
    }

    private static bool IsCapePath(string value) =>
        value.Contains("M_Cape_EoM", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("/Cape/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Cape_", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("MI_Cape", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shared cape fabric source textures - referenced by the stripped base graph directly.</summary>
    private const string CapeFabricDir = "/Game/Characters/Textures/Attachments/Cape/Batman_EOM/";

    /// <summary>
    /// Cape cloth shading: flat "Base Colour" from the instance (linear, usually near-black), plus the
    /// baked PongeeFabric weave maps (roughness/normal/alpha). See TryBakeCapeFabric for the recipe.
    /// </summary>
    private static SlotShading ResolveCapeSlot(
        DefaultFileProvider provider,
        UObject material,
        string previewDir,
        PreviewMaterialFallback? fallback)
    {
        var colour = ResolveColour(fallback, material,
            "Base Colour", "BaseColour", "Base Color", "BaseColor",
            "Cape Colour", "CapeColour", "Cape Color", "CapeColor");
        var sourceBaseColour = ExportFallbackSourceTexture(fallback, BaseColourSlots, previewDir);
        var overrideBaseColour = sourceBaseColour is null
            ? FindFallbackTexture(provider, fallback, BaseColourSlots)
            : null;
        var baseColour = sourceBaseColour
                         ?? ExportBaseColourTexture(overrideBaseColour ?? FindBaseColourTexture(material, 0), previewDir);

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

        var overrideNormal = ExportFallbackSourceTexture(fallback, ["DNRM_Pristine", "DNRM", "NRM"], previewDir, isNormal: true)
                             ?? ExportTexture(FindFallbackTexture(provider, fallback, "DNRM_Pristine", "DNRM", "NRM"), previewDir, isNormal: true);
        var overrideMmr = ExportFallbackMmrSlot(provider, fallback, previewDir);
        return new SlotShading(
            Texture: baseColour,
            Normal: overrideNormal ?? (baked && File.Exists(nrm) ? nrmRel : null),
            Mmr: overrideMmr ?? (baked ? ormRel : null),
            Colour: colour ?? (baseColour is null ? Color.FromArgb(255, 4, 4, 5) : Color.White),
            Alpha: baked && File.Exists(alpha) ? alphaRel : null);
    }

    /// <summary>
    /// Prints every material slot on a mesh: index, the material bound there, and whether an override
    /// replaced it. Evidence for per-slot assignment - a mesh's sections do NOT share one material.
    /// </summary>
    private static void ReportSlots(UObject mesh, PreviewPart part)
    {
        var slots = MeshSlotMaterials(mesh);
        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(part.MeshPath.Split('.')[0])}: {slots.Count} slot(s), {part.Overrides?.Length ?? 0} override(s)");
        for (var i = 0; i < slots.Count; i++)
        {
            var m = slots[i];
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
        var exported = ExportMmrTexture(t, previewDir);
        if (exported is not null)
        {
            Console.WriteLine("    MMR source: resolved material parameter.");
        }
        return exported;
    }

    private static string? ExportFallbackMmrSlot(
        DefaultFileProvider provider,
        PreviewMaterialFallback? fallback,
        string previewDir)
    {
        var metadataTexture = ExportMmrTexture(
            FindFallbackTexture(provider, fallback, "MMR_Pristine", "MMR"),
            previewDir);
        if (metadataTexture is not null)
        {
            PreviewTrace("Preview MMR: using the cooked generated material texture.");
            return metadataTexture;
        }

        if (fallback is not null)
        {
            foreach (var parameter in new[] { "MMR_Pristine", "MMR" })
            {
                if (!fallback.SourceTextureOverrides.TryGetValue(parameter, out var source) || !File.Exists(source))
                {
                    continue;
                }

                var rel = "textures/" + MakeSafeName(Path.GetFileNameWithoutExtension(source)) + "_source_orm.png";
                var destination = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination) || TextureDecodeService.TryConvertMmrPngToOrm(source, destination))
                {
                    PreviewTrace($"Preview MMR: source map converted to ORM after cooked decode failed ({Path.GetFileName(source)}).");
                    return rel;
                }
            }
        }

        PreviewTrace("Preview MMR: no usable MMR texture was resolved.");
        return null;
    }

    private static string? ExportMmrTexture(UTexture2D? t, string previewDir)
    {
        if (t is null)
        {
            PreviewTrace("Preview MMR: material did not expose an MMR parameter.");
            return null;
        }
        var safe = string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var rel = "textures/" + safe + "_orm.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        var mip = t.GetFirstMip();
        PreviewTrace($"Preview MMR decode: {t.Name} ({t.Format}, {mip?.SizeX ?? 0}x{mip?.SizeY ?? 0}, {mip?.BulkData?.Data?.Length ?? 0} bytes).");
        if (File.Exists(dest) || TextureDecodeService.TryExportMmrAsOrm(t, dest))
        {
            PreviewTrace($"Preview MMR decoded: {t.Name} -> {Path.GetFileName(dest)}.");
            return rel;
        }
        PreviewTrace($"Preview MMR decode failed: {t.Name} ({t.Format}).");
        return null;
    }

    /// <summary>
    /// The EoM master uses RAO.G as ambient occlusion. Export it separately from MMR so material
    /// families that use a solid Base Color still retain their authored plastic surface definition.
    /// </summary>
    private static string? ExportRaoAoSlot(UObject? material, string previewDir)
    {
        var rao = FindTextureParam(material, "RAO", 0);
        if (rao is null)
        {
            return null;
        }
        var rel = "textures/" + MakeSafeName(rao.Name) + "_ao.png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest) || TextureDecodeService.TryExportRaoGreenAsAo(rao, dest))
        {
            Console.WriteLine($"    RAO.G->AO: {rao.Name} ({rao.Format})");
            return rel;
        }
        return null;
    }

    /// <summary>Decodes a named texture slot to PNG and returns its preview-relative path.</summary>
    private static string? ExportSlot(UObject? material, string slot, string previewDir, bool isNormal = false)
    {
        var t = FindTextureParam(material, slot, 0);
        return ExportTexture(t, previewDir, isNormal, slot);
    }

    private static string? ExportTexture(UTexture2D? texture, string previewDir, bool isNormal, string? slot = null)
    {
        if (texture is null)
        {
            return null;
        }

        var t = texture;
        var rel = "textures/" + string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".png";
        var dest = Path.Combine(previewDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest) || TextureDecodeService.TryExportPng(t, dest, isNormal))
        {
            Console.WriteLine($"    {slot ?? "texture"}: {t.Name} ({t.Format})");
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

        if (FindDirectBaseColourTexture(material) is { } texture)
        {
            return texture;
        }

        // Not on this instance - inherit from the parent material.
        var parent = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load();
        return FindBaseColourTexture(parent, depth + 1);
    }

    private static UTexture2D? FindDirectBaseColourTexture(UObject material)
    {
        foreach (var slot in BaseColourSlots)
        {
            if (FindTextureParamOnMaterial(material, slot) is { } texture)
            {
                return texture;
            }
        }
        return null;
    }

    private static UTexture2D? FindTextureParamOnMaterial(UObject material, string slot)
    {
        var textureParams = material.GetOrDefault<FStructFallback[]>("TextureParameterValues");
        if (textureParams is null)
        {
            return null;
        }

        foreach (var entry in textureParams)
        {
            var name = entry.GetOrDefault<FStructFallback>("ParameterInfo")?.GetOrDefault<FName>("Name").Text;
            if (string.Equals(name, slot, StringComparison.OrdinalIgnoreCase) &&
                entry.GetOrDefault<FPackageIndex>("ParameterValue")?.ResolvedObject?.Load() is UTexture2D texture)
            {
                return texture;
            }
        }
        return null;
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
        // Face prints sit nearly on top of the head. Apply this after the socket/component
        // transform so it stays a consistent frontward clearance for every character.
        const float facePostSocketClearanceX = 0.005f;
        var head = models.FirstOrDefault(m => m.Part.IsHeadPiece);
        (Vector3 Min, Vector3 Max)? headB3 = null;
        if (head.File is not null && GlbInspector.Bounds3(Path.Combine(dir, head.File)) is { } rawHeadBounds)
        {
            var transformedHeadBounds = TransformBounds(rawHeadBounds, head.Part.Transform);
            var headShift = head.Part.AttachmentOffset ?? Vector3.Zero;
            headB3 = (transformedHeadBounds.Min + headShift, transformedHeadBounds.Max + headShift);
        }
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
            var attachmentOffset = part.AttachmentOffset ?? Vector3.Zero;
            PlacedModel Place(Vector3 offset)
            {
                // `offset` is applied by the viewer after `Transform`, which already contains
                // the captured attachment socket. Keep the face nudge in that final layer.
                if (isFace)
                {
                    offset.X += facePostSocketClearanceX;
                }

                return new(file, offset, slots, isBody, isFace, isHead)
                {
                    Transform = part.Transform,
                    UsesRuntimeSocketCalibration = part.UsesRuntimeSocketCalibration,
                    ComponentName = part.ComponentName,
                    DisplayName = part.DisplayName,
                    Adjustment = part.Adjustment,
                    CustomMeshId = part.CustomMeshId,
                    CustomMeshScale = part.SourceObjScale,
                    CustomMeshOffset = part.SourceObjOffset,
                    CustomMeshRotation = part.SourceObjRotation,
                };
            }
            if (part.UsesRuntimeSocketCalibration || !part.AttachToHead || headBase is null)
            {
                result.Add(Place(attachmentOffset));
                continue;
            }
            // Hair and other rigid head pieces are authored around their own origin and pinned to
            // the head socket, so bounds-matching them to the head (which suits shells like cowls)
            // puts them in the wrong place. Anchor them to the head attach bone instead.
            if (part.IsStaticAttachment && headB3 is { } hb)
            {
                // Imported OBJs are centered into their StaticMesh payload, then attached at
                // their chosen game socket. Preserve that authored origin; generic static hairs
                // need the older bounds-centering treatment below.
                if (!string.IsNullOrWhiteSpace(part.SourceObjPath))
                {
                    result.Add(Place(attachmentOffset));
                    Console.WriteLine($"  {file}: imported mesh anchored at {part.Attachment?.SocketName ?? "component origin"}");
                    continue;
                }

                // Cooked meshes carry no sockets, and the head attach bone sits at the very TOP of
                // the skull - anchoring there leaves hair hovering. A hair piece is modelled to
                // sheathe the head, so centre it on the head in all three axes: that also corrects
                // the small front/back bias in how the piece is authored.
                var hairB = GlbInspector.Bounds3(Path.Combine(dir, file));
                if (hairB is { } rawBounds)
                {
                    var pb = TransformBounds(rawBounds, part.Transform);
                    var headCentre = (hb.Min + hb.Max) / 2f;
                    var partCentre = (pb.Min + pb.Max) / 2f;
                    var delta = headCentre - partCentre;
                    Console.WriteLine($"  {file}: head attachment centred after component transform -> "
                                      + $"({delta.X:0.###}, {delta.Y:0.###}, {delta.Z:0.###})");
                    result.Add(Place(delta));
                    continue;
                }
            }

            var b3 = GlbInspector.Bounds3(Path.Combine(dir, file));
            if (b3 is null)
            {
                result.Add(Place(attachmentOffset));
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
                // The stable face overlay needs a further 0.01m clearance beyond the original
                // shell nudge; it keeps every neutral print in front of the head at close range.
                dx = headB3.Value.Max.X + 0.016f - b3.Value.Max.X;
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
            result.Add(Place(new Vector3(dx, dy, 0)));
        }
        return result;
    }

    /// <summary>Extracts the vendored three.js + writes the viewer HTML + model list into the dir.</summary>
    private static void WriteViewerAssets(
        string dir,
        IReadOnlyList<PlacedModel> models,
        bool allowPartMover,
        string? viewerLayoutKey = null,
        IReadOnlyCollection<PreviewRedBrickTint>? redBrickTints = null)
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
                $"\"ao\":{Q(sl.Ao)},\"rough\":{(sl.Roughness is null ? "null" : sl.Roughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}," +
                $"\"metal\":{(sl.Metalness is null ? "null" : sl.Metalness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}," +
                $"\"mask\":{Q(sl.ColourMask)}," +
                $"\"col\":{(sl.Colour is null ? "null" : $"\"#{sl.Colour.Value.R:X2}{sl.Colour.Value.G:X2}{sl.Colour.Value.B:X2}\"")}" +
                "}"));
            // Extract every UV channel so the viewer's switcher can bind sets three.js drops on import.
            var baseName = Path.GetFileNameWithoutExtension(m.File);
            Console.WriteLine($"    uv extract {m.File}");
            var uvs = GlbInspector.ExtractUvChannels(Path.Combine(dir, m.File), dir, baseName);
            var defaultUv = m.IsBody && uvs.Contains(1)
                ? 1
                : uvs.Contains(0) ? 0 : uvs.FirstOrDefault();
            var selectedUv = m.Adjustment?.UvChannel is int savedUv && uvs.Contains(savedUv)
                ? savedUv
                : defaultUv;
            var fg = m.FaceGroups is null ? "null" : $"[{string.Join(",", m.FaceGroups)}]";
            var transform = m.Transform is null
                ? "\"pos\":null,\"rot\":null,\"scale\":null"
                : $"\"pos\":[{F(m.Transform.Translation.X)},{F(m.Transform.Translation.Y)},{F(m.Transform.Translation.Z)}]," +
                  $"\"rot\":[{F(m.Transform.Rotation.X)},{F(m.Transform.Rotation.Y)},{F(m.Transform.Rotation.Z)},{F(m.Transform.Rotation.W)}]," +
                  $"\"scale\":[{F(m.Transform.Scale.X)},{F(m.Transform.Scale.Y)},{F(m.Transform.Scale.Z)}]";
            var adjustment = m.IsFace || m.Adjustment is null
                ? "[0,0,0]"
                : $"[{F(m.Adjustment.OffsetX)},{F(m.Adjustment.OffsetY)},{F(m.Adjustment.OffsetZ)}]";
            var isCustomStaticMesh = !string.IsNullOrWhiteSpace(m.CustomMeshId);
            var movable = !isCustomStaticMesh && !m.IsBody && !m.IsHead && !m.IsFace &&
                          !string.IsNullOrWhiteSpace(m.ComponentName) &&
                          !m.ComponentName.StartsWith("__", StringComparison.Ordinal);
            var customMesh = !isCustomStaticMesh
                ? "null"
                : $"{{\"id\":{Q(m.CustomMeshId)},\"scale\":{F(m.CustomMeshScale)}," +
                  $"\"offset\":[{F(m.CustomMeshOffset?.X ?? 0f)},{F(m.CustomMeshOffset?.Y ?? 0f)},{F(m.CustomMeshOffset?.Z ?? 0f)}]," +
                  $"\"rotation\":[{F(m.CustomMeshRotation?.X ?? 0f)},{F(m.CustomMeshRotation?.Y ?? 0f)},{F(m.CustomMeshRotation?.Z ?? 0f)}]}}";
            return $"{{\"file\":\"{m.File}\",\"base\":\"{baseName}\",\"body\":{(m.IsBody ? "true" : "false")},\"isface\":{(m.IsFace ? "true" : "false")},\"ishead\":{(m.IsHead ? "true" : "false")}," +
                    $"{transform}," +
                    $"\"part\":{Q(m.ComponentName)},\"label\":{Q(m.DisplayName)},\"move\":{(movable ? "true" : "false")},\"custom\":{(isCustomStaticMesh ? "true" : "false")},\"mesh\":{customMesh},\"adj\":{adjustment}," +
                   $"\"fgroups\":{fg},\"mouth\":{Q(m.MouthTex)},\"mhide\":{(m.MouthHidden ? "true" : "false")}," +
                    $"\"fbands\":[{string.Join(",", (m.Bands ?? new()).Select(b => $"[{b.Band},{b.Tris},{Q(b.Tex)},{(b.Tint is null ? "null" : $"\"#{b.Tint.Value.R:X2}{b.Tint.Value.G:X2}{b.Tint.Value.B:X2}\"")},{b.Mode},{Q(b.Feature)},{F(b.Pdo)},{Q(b.Nrm)},{Q(b.Orm)},{(b.Roughness is null ? "null" : F(b.Roughness.Value))},{(b.Metallic is null ? "null" : F(b.Metallic.Value))},{Q(b.Emissive)},{(b.EmissiveColour is null ? "null" : $"\"#{b.EmissiveColour.Value.R:X2}{b.EmissiveColour.Value.G:X2}{b.EmissiveColour.Value.B:X2}\"")},{(b.EmissiveStrength is null ? "null" : F(b.EmissiveStrength.Value))},{UvLayerJson(b.EyeSpecLayer)},{MouthLayersJson(b.MouthLayers)}]"))}]," +
                   $"\"poses\":{PoseJson(m.Poses)},\"curves\":{CurveJson(m.Curves)}," +
                    $"\"uvs\":[{string.Join(",", uvs)}],\"uv\":{selectedUv},\"uvdefault\":{defaultUv}," +
                   $"\"offset\":[{m.Offset.X:0.#####},{m.Offset.Y:0.#####},{m.Offset.Z:0.#####}]," +
                   $"\"slots\":[{slots}]}}";
        })) + "]";
        static string Q(string? v) => v is null ? "null" : System.Text.Json.JsonSerializer.Serialize(v);
        static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string UvLayerJson(FaceUvLayer? layer) => layer is null
            ? "null"
            : $"[{Q(layer.Tex)},\"#{layer.Tint.R:X2}{layer.Tint.G:X2}{layer.Tint.B:X2}\",{Q(layer.CurvePrefix)}," +
              $"{F(layer.OffsetU)},{F(layer.OffsetV)},{F(layer.Rotation)},{F(layer.ScaleU)},{F(layer.ScaleV)}]";
        string MouthLayersJson(FaceMouthLayers? layers) => layers is null
            ? "null"
            : $"[{UvLayerJson(layers.TeethU)},{UvLayerJson(layers.TeethD)},{UvLayerJson(layers.Tongue)}]";
        static string CurveJson(Dictionary<string, Dictionary<int, Dictionary<string, float>>>? curves) =>
            curves is null || curves.Count == 0 ? "{}" : JsonSerializer.Serialize(curves);

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

        static bool HasExportedPng(string previewRoot, string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(
                    previewRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!FileSystemPathUtil.IsWithinDirectory(fullPath, previewRoot) || !File.Exists(fullPath))
                {
                    return false;
                }

                ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
                Span<byte> header = stackalloc byte[pngSignature.Length];
                using var stream = File.OpenRead(fullPath);
                return stream.Read(header) == header.Length && header.SequenceEqual(pngSignature);
            }
            catch
            {
                return false;
            }
        }

        var bodyHasColourMask = models
            .Where(model => IsBodyMeshParent(model.ComponentName))
            .SelectMany(model => model.Slots)
            .Any(slot => HasExportedPng(dir, slot.ColourMask));
        var eligibleTints = bodyHasColourMask
            ? redBrickTints ?? Array.Empty<PreviewRedBrickTint>()
            : Array.Empty<PreviewRedBrickTint>();
        var tintJson = JsonSerializer.Serialize(eligibleTints.Select(tint => new
        {
            name = tint.DisplayName,
            primary = tint.PrimaryHex,
            secondary = tint.SecondaryHex,
            tertiary = tint.TertiaryHex,
        }));
        File.WriteAllText(Path.Combine(dir, "models.js"),
            $"window.PREVIEW_MODELS={jsonList};window.PREVIEW_CAN_SAVE_PLACEMENTS={(allowPartMover ? "true" : "false")};" +
            $"window.PREVIEW_LAYOUT_KEY={JsonSerializer.Serialize(viewerLayoutKey ?? string.Empty)};" +
            $"window.PREVIEW_RED_BRICKS={tintJson};window.PREVIEW_REDBRICK_BODY_MASK={(bodyHasColourMask ? "true" : "false")};");
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
  #partmove,#meshmove,#redbrick,#matedit{position:absolute;right:14px;top:12px;width:214px;color:#dfe4ea;font-size:12px;
    background:rgba(26,29,34,.9);padding:9px 10px;border:1px solid #333a44;border-radius:6px}
  #redbrick{top:auto;bottom:14px}
  #matedit{right:244px;max-height:calc(100vh - 28px);overflow:auto}
  #partmove label,#meshmove label,#redbrick label,#matedit label{display:block;color:#f0c230;margin-bottom:5px}
  #partmove select,#meshmove select,#redbrick select,#matedit select{box-sizing:border-box;width:100%;margin-bottom:7px;background:#22262c;color:#e6e9ee;
    border:1px solid #3a4048;border-radius:4px;padding:4px;font:inherit}
  #partmove .axis,#meshmove .axis{display:grid;grid-template-columns:38px 1fr;align-items:center;gap:5px;margin:3px 0;color:#9ea6b2}
  #partmove input,#meshmove input{box-sizing:border-box;width:100%;background:#171a1f;color:#e6e9ee;border:1px solid #3a4048;
    border-radius:4px;padding:3px 5px;font:12px Consolas,monospace}
  #partmove .actions,#meshmove .actions{display:flex;gap:6px;margin-top:8px}
  #partmove button,#meshmove button,#redbrick button,#matedit button{border:1px solid #3a4048;border-radius:4px;background:#232833;color:#dfe4ea;padding:4px 7px;cursor:pointer;font:inherit}
  #partmove button.save,#meshmove button.save{border-color:#aa8b1b;color:#f0c230}
  #partmove button:disabled,#meshmove button:disabled{opacity:.45;cursor:default}
  #matedit .maptoggle{display:flex;align-items:center;justify-content:space-between;border-top:1px solid #303640;padding:5px 0;color:#cbd1d9}
  #matedit .maptoggle input{width:15px;height:15px;accent-color:#f0c230}
  #matedit .summary{margin:0 0 7px;color:#9ea6b2;line-height:1.35}
  #matedit .actions{display:flex;gap:6px;margin-top:8px}
  #matedit .actions button{border-color:#aa8b1b;color:#f0c230}
  .panel-drag-handle{cursor:move;user-select:none;touch-action:none}
  .panel-dragging{opacity:.92}
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
const partStates=new Map();
const redBrickMaskMaterials=[];
const materialEditorEntries=[];
function makePanelDraggable(panel,handle){
  if(!panel||!handle)return;
  handle.classList.add('panel-drag-handle');handle.title='Drag to move this panel';
  let drag=null;
  const move=e=>{if(!drag)return;
    const maxX=Math.max(0,window.innerWidth-panel.offsetWidth),maxY=Math.max(0,window.innerHeight-panel.offsetHeight);
    panel.style.left=Math.min(maxX,Math.max(0,drag.left+e.clientX-drag.x))+'px';
    panel.style.top=Math.min(maxY,Math.max(0,drag.top+e.clientY-drag.y))+'px';};
  const stop=e=>{if(!drag)return;drag=null;panel.classList.remove('panel-dragging');
    try{handle.releasePointerCapture(e.pointerId);}catch(_){}};
  handle.addEventListener('pointerdown',e=>{if(e.button!==0)return;
    const rect=panel.getBoundingClientRect();
    panel.style.left=rect.left+'px';panel.style.top=rect.top+'px';panel.style.right='auto';panel.style.bottom='auto';
    drag={x:e.clientX,y:e.clientY,left:rect.left,top:rect.top};panel.classList.add('panel-dragging');
    try{handle.setPointerCapture(e.pointerId);}catch(_){}e.preventDefault();});
  handle.addEventListener('pointermove',move);handle.addEventListener('pointerup',stop);handle.addEventListener('pointercancel',stop);
}
function setRedBrickPalette(palette){
  redBrickMaskMaterials.forEach(state=>{
    state.palette=palette;
    if(!state.uniforms)return;
    state.uniforms.redBrickEnabled.value=palette?1:0;
    if(palette){
      state.uniforms.redBrickPrimary.value.set(palette.primary).convertSRGBToLinear();
      state.uniforms.redBrickSecondary.value.set(palette.secondary).convertSRGBToLinear();
      state.uniforms.redBrickTertiary.value.set(palette.tertiary).convertSRGBToLinear();
    }
  });
}
function buildRedBrickTintUi(){
  const presets=window.PREVIEW_RED_BRICKS||[];
  // The C# side emits no presets for Cutscene entries or for a playable/modded body without a valid
  // mask, so the control cannot imply support where the preview cannot reproduce the game's tint pipeline.
  if(!presets.length||!window.PREVIEW_REDBRICK_BODY_MASK||!redBrickMaskMaterials.length)return;
  const panel=document.createElement('div');panel.id='redbrick';
  const label=document.createElement('label');label.textContent='Base-game Red Brick preview';panel.appendChild(label);
  const select=document.createElement('select');
  const off=document.createElement('option');off.value='';off.textContent='Original colours';select.appendChild(off);
  presets.forEach((preset,index)=>{const option=document.createElement('option');option.value=String(index);
    option.textContent=preset.name;select.appendChild(option);});
  select.onchange=()=>setRedBrickPalette(select.value===''?null:presets[Number(select.value)]);
  panel.appendChild(select);document.body.appendChild(panel);makePanelDraggable(panel,label);
}
function buildMaterialEditor(){
  if(!materialEditorEntries.length)return;
  const panel=document.createElement('div');panel.id='matedit';panel.title='Viewer-only map switches. These never change the suit or cooked files.';
  const label=document.createElement('label');label.textContent='Material editor';panel.appendChild(label);
  const summary=document.createElement('div');summary.className='summary';summary.textContent='Toggle live maps to isolate preview shading.';panel.appendChild(summary);
  const select=document.createElement('select');
  materialEditorEntries.forEach((entry,index)=>{const option=document.createElement('option');option.value=String(index);
    option.textContent=entry.label;select.appendChild(option);});
  panel.appendChild(select);
  const toggles=[];
  const addToggle=(label,key)=>{
    const row=document.createElement('label');row.className='maptoggle';
    const text=document.createElement('span');text.textContent=label;row.appendChild(text);
    const input=document.createElement('input');input.type='checkbox';input.checked=true;
    input.onchange=()=>{const entry=materialEditorEntries[Number(select.value)];if(!entry)return;
      entry.enabled[key]=input.checked;applyMaterialEditorEntry(entry);};
    row.appendChild(input);panel.appendChild(row);toggles.push([key,input]);
  };
  addToggle('Base colour map','base');
  addToggle('Normal map','normal');
  addToggle('MMR maps','mmr');
  addToggle('Ambient occlusion','ao');
  const actions=document.createElement('div');actions.className='actions';
  const reset=document.createElement('button');reset.type='button';reset.textContent='Reset material';
  reset.onclick=()=>{const entry=materialEditorEntries[Number(select.value)];if(!entry)return;
    Object.keys(entry.enabled).forEach(key=>entry.enabled[key]=true);applyMaterialEditorEntry(entry);sync();};
  actions.appendChild(reset);panel.appendChild(actions);
  function sync(){const entry=materialEditorEntries[Number(select.value)];if(!entry)return;
    summary.textContent=entry.label+' - viewer only';
    toggles.forEach(([key,input])=>{input.checked=entry.available[key]&&!!entry.enabled[key];
      input.disabled=!entry.available[key];});
  }
  select.onchange=sync;sync();document.body.appendChild(panel);makePanelDraggable(panel,label);
}
function applyMaterialEditorEntry(entry){
  const m=entry.material,original=entry.original,enabled=entry.enabled;
  m.map=enabled.base?original.map:null;
  m.normalMap=enabled.normal?original.normalMap:null;
  m.roughnessMap=enabled.mmr?original.roughnessMap:null;
  m.metalnessMap=enabled.mmr?original.metalnessMap:null;
  m.roughness=enabled.mmr?original.roughness:0.5;
  m.metalness=enabled.mmr?original.metalness:0;
  m.aoMap=enabled.ao?original.aoMap:null;
  m.needsUpdate=true;
  const disabled=Object.entries(enabled).filter(([,on])=>!on).map(([key])=>key+' off').join(', ');
  say('material editor: '+entry.label+' - '+(disabled||'all maps on'));
}
function postToHost(message){
  if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(message);
}
function setPartAdjustment(component,adjustment){
  const state=partStates.get(component);if(!state)return;
  state.adjustment=adjustment.slice(0,3).map(value=>Number.isFinite(value)?value:0);
  state.scene.position.copy(state.basePosition).add(new THREE.Vector3(
    state.adjustment[0],state.adjustment[1],state.adjustment[2]));
}
function setPartScale(component,multiplier){
  const state=partStates.get(component);if(!state||!state.custom)return;
  state.scale=Math.min(100,Math.max(.01,Number(multiplier)||1));
  state.scene.scale.copy(state.baseScale).multiplyScalar(state.scale);
}
function uvAttribute(mesh,channel){
  const attrs=mesh.geometry.attributes;
  if(channel===0)return attrs.aUv0||attrs.uv||null;
  if(channel===1)return attrs.aUv1||attrs.uv2||null;
  return attrs['previewUv'+channel]||null;
}
async function prepareUvChannels(g,info){
  const channels=(info.uvs||[]).filter(channel=>channel>1);if(!channels.length)return;
  const meshes=[];g.scene.traverse(o=>{if(o.isMesh)meshes.push(o);});
  await Promise.all(channels.map(async channel=>{
    try{
      const response=await fetch(info.base+'_uv'+channel+'.f32');if(!response.ok)return;
      const values=new Float32Array(await response.arrayBuffer());
      meshes.forEach(o=>{const position=o.geometry.attributes.position;
        if(position&&values.length===position.count*2){
          o.geometry.setAttribute('previewUv'+channel,new THREE.BufferAttribute(values.slice(),2));
        }});
    }catch(_){/* Missing optional UV data just leaves this channel unavailable. */}
  }));
}
function usableUvChannels(scene,channels){
  return (channels||[]).filter(channel=>{let found=false;
    scene.traverse(o=>{if(o.isMesh&&uvAttribute(o,channel))found=true;});return found;});
}
function setPartUv(component,channel){
  const state=partStates.get(component);if(!state)return false;
  let applied=0;state.scene.traverse(o=>{if(!o.isMesh)return;
    const attribute=uvAttribute(o,channel);if(!attribute)return;
    o.geometry.setAttribute('uv',attribute);attribute.needsUpdate=true;applied++;
  });
  if(applied)state.uvChannel=channel;
  return applied>0;
}
function buildPartMover(){
  const parts=[...partStates.values()].filter(state=>!state.custom&&!state.face);if(!parts.length)return;
  const panel=document.createElement('div');panel.id='partmove';
   const label=document.createElement('label');label.textContent='Part';panel.appendChild(label);
  const select=document.createElement('select');
  parts.forEach(state=>{const option=document.createElement('option');option.value=state.component;
    option.textContent=state.label||state.component;select.appendChild(option);});
  panel.appendChild(select);
  const inputs=[];
   ['X','Y','Z'].forEach((axis,index)=>{
    const row=document.createElement('div');row.className='axis';
    const axisLabel=document.createElement('span');axisLabel.textContent=axis;row.appendChild(axisLabel);
    const input=document.createElement('input');input.type='number';input.step='0.005';input.value='0';
    input.addEventListener('input',()=>{
      const state=partStates.get(select.value);if(!state)return;
      const next=state.adjustment.slice();next[index]=Number(input.value)||0;setPartAdjustment(select.value,next);
    });
     row.appendChild(input);panel.appendChild(row);inputs.push(input);
   });
  const uvLabel=document.createElement('label');uvLabel.textContent='UV';panel.appendChild(uvLabel);
  const uvSelect=document.createElement('select');panel.appendChild(uvSelect);
  uvSelect.onchange=()=>{const state=partStates.get(select.value);if(!state)return;
    const channel=Number(uvSelect.value);if(setPartUv(state.component,channel))sync();};
  const actions=document.createElement('div');actions.className='actions';
  const reset=document.createElement('button');reset.type='button';reset.textContent='↺';reset.title='Reset alignment';
   reset.onclick=()=>{setPartAdjustment(select.value,[0,0,0]);sync();};actions.appendChild(reset);
  const save=document.createElement('button');save.type='button';save.className='save';save.textContent='Save';
   save.disabled=!window.PREVIEW_CAN_SAVE_PLACEMENTS||!window.PREVIEW_LAYOUT_KEY;
   save.onclick=()=>{const state=partStates.get(select.value);if(!state)return;
      postToHost({type:'save-placement',layout:window.PREVIEW_LAYOUT_KEY,component:state.component,
        offset:state.adjustment,uv:state.uvChannel===state.defaultUv?null:state.uvChannel,
        scale:null});
     const label=save.textContent;save.textContent='Saved';setTimeout(()=>save.textContent=label,850);};
  actions.appendChild(save);panel.appendChild(actions);
   function sync(){const state=partStates.get(select.value);if(!state)return;
    label.textContent=state.face?'Face placement':'Part';
     inputs.forEach((input,index)=>{input.value=(state.adjustment[index]||0).toFixed(4);input.disabled=!state.movable;});
    save.title='Save this viewer-only alignment';
     reset.disabled=!state.movable;
    uvSelect.innerHTML='';state.uvs.forEach(channel=>{const option=document.createElement('option');
      option.value=channel;option.textContent='UV '+channel;uvSelect.appendChild(option);});
    uvSelect.value=String(state.uvChannel);uvSelect.disabled=state.uvs.length<2;}
  select.onchange=sync;sync();document.body.appendChild(panel);
}
function buildCustomMeshMover(){
  const parts=[...partStates.values()].filter(state=>state.custom&&state.customId&&state.authored);
  if(!parts.length)return;
  const panel=document.createElement('div');panel.id='meshmove';
  panel.title='Custom mesh changes are local to the selected attachment socket. Offsets use Unreal centimeters.';
  const label=document.createElement('label');label.textContent='Custom mesh';panel.appendChild(label);
  const select=document.createElement('select');
  parts.forEach(state=>{const option=document.createElement('option');option.value=state.component;
    option.textContent=state.label||state.component;select.appendChild(option);});
  select.disabled=parts.length===1;panel.appendChild(select);
  const inputs={};
  [['Scale','scale',.1],['X offset (cm)','x',.1],['Y offset (cm)','y',.1],['Z offset (cm)','z',.1],
   ['Pitch','pitch',1],['Yaw','yaw',1],['Roll','roll',1]].forEach(([title,key,step])=>{
    const row=document.createElement('div');row.className='axis';
    const rowLabel=document.createElement('span');rowLabel.textContent=title;row.appendChild(rowLabel);
    const input=document.createElement('input');input.type='number';input.step=String(step);input.dataset.key=key;
    row.appendChild(input);panel.appendChild(row);inputs[key]=input;
  });
  const actions=document.createElement('div');actions.className='actions';
  const save=document.createElement('button');save.type='button';save.className='save';save.textContent='Bake to game';
  save.disabled=!window.PREVIEW_CAN_SAVE_PLACEMENTS||!window.PREVIEW_LAYOUT_KEY;
  save.title='Rebuild the game mesh using these saved values. Preview changes are saved automatically.';
  const readTransform=()=>{const number=key=>Number(inputs[key].value)||0;return {
    scale:number('scale'),offset:[number('x'),number('y'),number('z')],rotation:[number('pitch'),number('yaw'),number('roll')]};};
  const postTransform=(type,state,transform=readTransform())=>{if(!state||!state.authored)return;
    postToHost({type,layout:window.PREVIEW_LAYOUT_KEY,component:state.component,customId:state.customId,
      transform});};
  save.onclick=()=>{const state=partStates.get(select.value);if(!state||!state.authored)return;
    postTransform('save-custom-mesh',state);
    save.textContent='Saving...';save.disabled=true;};
  actions.appendChild(save);panel.appendChild(actions);
  function sync(){const state=partStates.get(select.value);if(!state||!state.authored)return;
    const transform=state.liveTransform||state.authored;
    inputs.scale.value=Number(transform.scale||1).toFixed(3);
    inputs.x.value=Number((transform.offset||[])[0]||0).toFixed(3);
    inputs.y.value=Number((transform.offset||[])[1]||0).toFixed(3);
    inputs.z.value=Number((transform.offset||[])[2]||0).toFixed(3);
    inputs.pitch.value=Number((transform.rotation||[])[0]||0).toFixed(1);
    inputs.yaw.value=Number((transform.rotation||[])[1]||0).toFixed(1);
    inputs.roll.value=Number((transform.rotation||[])[2]||0).toFixed(1);
    applyCustomMeshPreview(state);
  }
  let draftTimer=0;
  Object.values(inputs).forEach(input=>input.oninput=()=>{
    const state=partStates.get(select.value);if(!state)return;applyCustomMeshPreview(state);
    const transform=readTransform();state.liveTransform=transform;
    save.textContent='Bake to game';save.disabled=!window.PREVIEW_CAN_SAVE_PLACEMENTS||!window.PREVIEW_LAYOUT_KEY;
    window.clearTimeout(draftTimer);draftTimer=window.setTimeout(()=>postTransform('save-custom-mesh-draft',state,transform),550);
  });
  window.addEventListener('pagehide',()=>{const state=partStates.get(select.value);if(state)postTransform('save-custom-mesh-draft',state);},{once:true});
  select.onchange=sync;sync();document.body.appendChild(panel);
  const partPanel=document.getElementById('partmove');
  if(partPanel)partPanel.style.top=(panel.offsetHeight+26)+'px';
  function applyCustomMeshPreview(state){
    if(!state.customGeometry||!state.authored)return;
    const number=key=>Number(inputs[key].value)||0;
    const saved=state.authored;
    const ratio=number('scale')/Math.max(.0001,Number(saved.scale)||1);
    const delta=ueToGltfRotation(number('pitch'),number('yaw'),number('roll'))
      .multiply(ueToGltfRotation(Number((saved.rotation||[])[0])||0,Number((saved.rotation||[])[1])||0,Number((saved.rotation||[])[2])||0).invert());
    const oldOffset=ueToGltfPosition(saved.offset||[]);
    const newOffset=ueToGltfPosition([number('x'),number('y'),number('z')]);
    state.customGeometry.forEach(entry=>{
      const position=entry.mesh.geometry.attributes.position;
      const normal=entry.mesh.geometry.attributes.normal;
      for(let i=0;i<entry.position.length;i+=3){
        temp.set(entry.position[i],entry.position[i+1],entry.position[i+2]).sub(oldOffset).multiplyScalar(ratio).applyQuaternion(delta).add(newOffset);
        position.setXYZ(i/3,temp.x,temp.y,temp.z);
      }
      position.needsUpdate=true;
      if(normal&&entry.normal){
        for(let i=0;i<entry.normal.length;i+=3){
          temp.set(entry.normal[i],entry.normal[i+1],entry.normal[i+2]).applyQuaternion(delta).normalize();
          normal.setXYZ(i/3,temp.x,temp.y,temp.z);
        }
        normal.needsUpdate=true;
      }
      entry.mesh.geometry.computeBoundingBox();entry.mesh.geometry.computeBoundingSphere();
    });
  }
}
const temp=new THREE.Vector3();
function ueToGltfPosition(values){
  // Custom mesh offsets are authored in Unreal centimeters; preview geometry is in glTF meters.
  return new THREE.Vector3(Number(values[0])||0,Number(values[2])||0,-(Number(values[1])||0)).multiplyScalar(.01);
}
function ueToGltfRotation(pitch,yaw,roll){
  const rad=Math.PI/180;
  const ue=new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,0,1),yaw*rad)
    .multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,1,0),pitch*rad))
    .multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1,0,0),roll*rad));
  const basis=new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1,0,0),-Math.PI/2);
  return basis.clone().multiply(ue).multiply(basis.clone().invert());
}
// CUE4Parse writes textures as loose .png beside the .glb rather than embedding them, so the base
// colour map is applied here from the path the exporter reported.
function tex(path,sRGB){if(!path)return null;const t=texLoader.load(path);t.flipY=false;
  if(sRGB)t.encoding=THREE.sRGBEncoding;return t;}
// The face master stores TeethU/D and Tongue offsets in centi-UV authoring units. The shipped hide
// values (-20, +17.906 and -7) therefore move a sheet a few tenths of a UV space, while expression
// curves around -12 bring it into the mouth. Treating those as tenths moved the layers off-sheet.
const FACE_UV_OFFSET_UNIT=0.01;
const faceTransparentTex=new THREE.DataTexture(new Uint8Array([255,255,255,0]),1,1,THREE.RGBAFormat);
faceTransparentTex.needsUpdate=true;
function faceMouthLayer(spec){
  if(!spec)return null;
  const [path,tint,prefix,offsetU,offsetV,rotation,scaleU,scaleV]=spec;
  return {map:tex(path,true)||faceTransparentTex,
    tint:new THREE.Color(tint||'#ffffff').convertSRGBToLinear(),prefix:(prefix||'').toLowerCase(),
    base:{offsetU:offsetU||0,offsetV:offsetV||0,rotation:rotation||0,
      scaleU:scaleU===undefined?1:scaleU,scaleV:scaleV===undefined?1:scaleV}};
}
function installEyeSpec(mat,spec){
  const layer=faceMouthLayer(spec);if(!layer)return;
  const state={layer:layer,uniforms:null,curves:null};mat.userData.faceEyeSpec=state;
  mat.onBeforeCompile=sh=>{
    state.uniforms={
      faceEyeSpecMap:{value:layer.map},faceEyeSpecTint:{value:layer.tint},
      faceEyeSpecOffset:{value:new THREE.Vector2()},faceEyeSpecRotate:{value:0},
      faceEyeSpecScale:{value:new THREE.Vector2(1,1)}
    };
    setEyeSpecUniforms(state,state.curves);
    Object.assign(sh.uniforms,state.uniforms);
    sh.fragmentShader=sh.fragmentShader
      .replace('#include <common>',`#include <common>
uniform sampler2D faceEyeSpecMap;
uniform vec3 faceEyeSpecTint;
uniform vec2 faceEyeSpecOffset;
uniform float faceEyeSpecRotate;
uniform vec2 faceEyeSpecScale;
vec2 faceEyeSpecUv(vec2 uv,vec2 offset,float rotation,vec2 scale){
  vec2 p=(uv-vec2(0.5))*scale;
  float c=cos(rotation),s=sin(rotation);
  p=mat2(c,-s,s,c)*p;
  return p+vec2(0.5)+offset;
}`)
      .replace('#include <map_fragment>',`#include <map_fragment>
vec4 faceEyeSpec=mapTexelToLinear(texture2D(faceEyeSpecMap,faceEyeSpecUv(vUv,faceEyeSpecOffset,faceEyeSpecRotate,faceEyeSpecScale)));
float faceEyeSpecA=faceEyeSpec.a*diffuseColor.a;
diffuseColor.rgb=mix(diffuseColor.rgb,faceEyeSpecTint,faceEyeSpecA);`);
  };
  mat.customProgramCacheKey=function(){return 'faceEyeSpec-v2';};
}
function installMouthLayers(mat,spec,hidden){
  if(!spec)return;
  const state={layers:spec.map(faceMouthLayer),hidden:hidden?1:0,uniforms:null,curves:null};
  mat.userData.faceMouth=state;
  mat.onBeforeCompile=sh=>{
    state.uniforms={
      faceMouthHide:{value:state.hidden},
      faceTeethUMap:{value:(state.layers[0]||{}).map||faceTransparentTex},
      faceTeethDMap:{value:(state.layers[1]||{}).map||faceTransparentTex},
      faceTongueMap:{value:(state.layers[2]||{}).map||faceTransparentTex},
      faceTeethUTint:{value:(state.layers[0]||{}).tint||new THREE.Color(0xffffff)},
      faceTeethDTint:{value:(state.layers[1]||{}).tint||new THREE.Color(0xffffff)},
      faceTongueTint:{value:(state.layers[2]||{}).tint||new THREE.Color(0xffffff)},
      faceTeethUOffset:{value:new THREE.Vector2()},faceTeethDOffset:{value:new THREE.Vector2()},faceTongueOffset:{value:new THREE.Vector2()},
      faceTeethURotate:{value:0},faceTeethDRotate:{value:0},faceTongueRotate:{value:0},
      faceTeethUScale:{value:new THREE.Vector2(1,1)},faceTeethDScale:{value:new THREE.Vector2(1,1)},faceTongueScale:{value:new THREE.Vector2(1,1)}
    };
    setMouthLayerUniforms(state,state.curves);
    Object.assign(sh.uniforms,state.uniforms);
    sh.fragmentShader=sh.fragmentShader
      .replace('#include <common>',`#include <common>
uniform sampler2D faceTeethUMap;
uniform sampler2D faceTeethDMap;
uniform sampler2D faceTongueMap;
uniform vec3 faceTeethUTint;
uniform vec3 faceTeethDTint;
uniform vec3 faceTongueTint;
uniform vec2 faceTeethUOffset;
uniform vec2 faceTeethDOffset;
uniform vec2 faceTongueOffset;
uniform vec2 faceTeethUScale;
uniform vec2 faceTeethDScale;
uniform vec2 faceTongueScale;
uniform float faceTeethURotate;
uniform float faceTeethDRotate;
uniform float faceTongueRotate;
uniform float faceMouthHide;
vec2 faceLayerUv(vec2 uv,vec2 offset,float rotation,vec2 scale){
  vec2 p=(uv-vec2(0.5))*scale;
  float c=cos(rotation),s=sin(rotation);
  p=mat2(c,-s,s,c)*p;
  return p+vec2(0.5)+offset*${FACE_UV_OFFSET_UNIT.toFixed(1)};
}`)
      .replace('#include <map_fragment>',`vec4 faceBase=mapTexelToLinear(texture2D(map,vUv));
vec4 faceTeethU=mapTexelToLinear(texture2D(faceTeethUMap,faceLayerUv(vUv,faceTeethUOffset,faceTeethURotate,faceTeethUScale)));
vec4 faceTeethD=mapTexelToLinear(texture2D(faceTeethDMap,faceLayerUv(vUv,faceTeethDOffset,faceTeethDRotate,faceTeethDScale)));
vec4 faceTongue=mapTexelToLinear(texture2D(faceTongueMap,faceLayerUv(vUv,faceTongueOffset,faceTongueRotate,faceTongueScale)));
float faceVisible=1.0-clamp(faceMouthHide,0.0,1.0);
float faceTeethUA=faceTeethU.a*faceVisible;
float faceTeethDA=faceTeethD.a*faceVisible;
float faceTongueA=faceTongue.a*faceVisible;
vec3 faceRgb=diffuseColor.rgb*faceBase.rgb;
faceRgb=mix(faceRgb,faceTeethU.rgb*faceTeethUTint,faceTeethUA);
faceRgb=mix(faceRgb,faceTeethD.rgb*faceTeethDTint,faceTeethDA);
faceRgb=mix(faceRgb,faceTongue.rgb*faceTongueTint,faceTongueA);
// The black Mouth BC ring is the front-most lip rim; its alpha must cover the teeth at the
// perimeter, otherwise a white teeth texel leaks through the rim as it animates.
float faceMouthA=faceBase.a*faceVisible;
faceRgb=mix(faceRgb,diffuseColor.rgb*faceBase.rgb,faceMouthA);
diffuseColor.rgb=faceRgb;
diffuseColor.a*=max(faceMouthA,max(faceTeethUA,max(faceTeethDA,faceTongueA)));`);
  };
  mat.customProgramCacheKey=function(){return 'faceMouthLayers-v1';};
}
function curveValue(curves,name,fallback){
  if(!curves)return fallback;
  if(typeof curves[name]==='number')return curves[name];
  const key=Object.keys(curves).find(k=>k.toLowerCase()===name.toLowerCase());
  return key!==undefined&&typeof curves[key]==='number'?curves[key]:fallback;
}
function setEyeSpecUniforms(state,curves){
  if(!state)return;state.curves=curves;
  if(!state.uniforms)return;
  const layer=state.layer,key=layer.prefix;
  state.uniforms.faceEyeSpecOffset.value.set(
    curveValue(curves,key+'offsetu',layer.base.offsetU),
    curveValue(curves,key+'offsetv',layer.base.offsetV));
  state.uniforms.faceEyeSpecRotate.value=curveValue(curves,key+'rotate',layer.base.rotation);
  state.uniforms.faceEyeSpecScale.value.set(
    curveValue(curves,key+'scaleu',layer.base.scaleU),
    curveValue(curves,key+'scalev',layer.base.scaleV));
}
function setMouthLayerUniforms(state,curves){
  if(!state)return;state.curves=curves;
  if(!state.uniforms)return;
  state.uniforms.faceMouthHide.value=curveValue(curves,'mouthhide',state.hidden);
  const slots=[['TeethU',0],['TeethD',1],['Tongue',2]];
  slots.forEach(([name,index])=>{
    const layer=state.layers[index];if(!layer)return;
    const key=layer.prefix;
    const offset=state.uniforms['face'+name+'Offset'].value;
    offset.set(curveValue(curves,key+'offsetu',layer.base.offsetU),curveValue(curves,key+'offsetv',layer.base.offsetV));
    state.uniforms['face'+name+'Rotate'].value=curveValue(curves,key+'rotate',layer.base.rotation);
    state.uniforms['face'+name+'Scale'].value.set(curveValue(curves,key+'scaleu',layer.base.scaleU),curveValue(curves,key+'scalev',layer.base.scaleV));
  });
}
function applyFaceMaterialCurves(curves){faceBandMats.forEach(b=>{setEyeSpecUniforms(b.eyeSpec,curves);setMouthLayerUniforms(b.mouth,curves);});}
// Each mesh section has its OWN material slot. Shading is resolved per slot in C# and applied by
// index here - spraying one texture across every section is what mixed up the cape/face/cowl.
function dress(g,info){
  const slots=(info&&info.slots)||[];
  let matIndex=0,applied=0;
  g.scene.traverse(o=>{if(!o.isMesh)return;
    // Preserve the glTF UV sets before AO and the body atlas reuse three.js's uv/uv2 names. The
    // part panel can then rebind map sampling to any extracted channel without disturbing plastic
    // normal or AO sampling.
    if(o.geometry.attributes.uv&&!o.geometry.attributes.aUv0){
      o.geometry.setAttribute('aUv0',o.geometry.attributes.uv);}
    if(o.geometry.attributes.uv2&&!o.geometry.attributes.aUv1){
      o.geometry.setAttribute('aUv1',o.geometry.attributes.uv2);}
    // The shared LEGOfig body defaults to TEXCOORD_1 (ExtraUV0); other components default to UV0.
    if(info.body&&o.geometry.attributes.aUv1){o.geometry.setAttribute('uv',o.geometry.attributes.aUv1);}
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
        // The face tint is authoring-space sRGB; solid Base Color values came from Unreal
        // FLinearColor and are already in the space three.js expects.
        m.color=s.col?(s.cut?new THREE.Color(s.col).convertSRGBToLinear():new THREE.Color(s.col)):new THREE.Color(0xffffff);applied++;}
      else if(s.col){m.map=null;m.color=new THREE.Color(s.col);applied++;}
      else if(!m.map){m.color=new THREE.Color(0x9aa0a8);}
      const n=tex(s.nrm,false); if(n)m.normalMap=n;else m.normalMap=null;
      // RAO.G is the EoM ambient-occlusion channel. three.js aoMap samples UV2, so give static
      // attachments their structural UV0 there; body meshes preserve the same UV as aUv0.
      const ao=tex(s.ao,false);
      if(ao){const auv=o.geometry.attributes.aUv0||o.geometry.attributes.uv;
        if(auv)o.geometry.setAttribute('uv2',auv);
        m.aoMap=ao;m.aoMapIntensity=0.65;}
      else{m.aoMap=null;}
      // Read-only Red Brick preview. A selector is only emitted for a playable or modded suit whose
      // body has a successfully exported Colour Mask; additional masked parts follow the same palette.
      const cm=s.mask?tex(s.mask,false):null;
      if(cm){
        const tintState={uniforms:null,palette:null};
        redBrickMaskMaterials.push(tintState);
        m.onBeforeCompile=sh=>{
          sh.uniforms.redBrickMaskMap={value:cm};
          sh.uniforms.redBrickEnabled={value:0};
          sh.uniforms.redBrickPrimary={value:new THREE.Color('#ffffff')};
          sh.uniforms.redBrickSecondary={value:new THREE.Color('#ffffff')};
          sh.uniforms.redBrickTertiary={value:new THREE.Color('#ffffff')};
          tintState.uniforms=sh.uniforms;
          if(tintState.palette)setRedBrickPalette(tintState.palette);
          sh.vertexShader=sh.vertexShader
            .replace('#include <common>','#include <common>\nvarying vec2 vColourMaskUv;')
            .replace('#include <uv_vertex>','#include <uv_vertex>\nvColourMaskUv=vUv;');
          sh.fragmentShader=sh.fragmentShader
            .replace('#include <common>','#include <common>\nuniform sampler2D redBrickMaskMap;varying vec2 vColourMaskUv;uniform float redBrickEnabled;uniform vec3 redBrickPrimary;uniform vec3 redBrickSecondary;uniform vec3 redBrickTertiary;')
            .replace('#include <map_fragment>',
              '#include <map_fragment>\n'+
              'vec3 redBrickMask=texture2D(redBrickMaskMap,vColourMaskUv).rgb;\n'+
              'float redBrickTotal=redBrickMask.r+redBrickMask.g+redBrickMask.b;\n'+
              'float redBrickWeight=clamp(max(redBrickMask.r,max(redBrickMask.g,redBrickMask.b)),0.0,1.0);\n'+
              'vec3 redBrickColour=(redBrickMask.r*redBrickPrimary+redBrickMask.g*redBrickSecondary+redBrickMask.b*redBrickTertiary)/max(redBrickTotal,0.0001);\n'+
              'diffuseColor.rgb=mix(diffuseColor.rgb,redBrickColour,redBrickEnabled*redBrickWeight);');
        };
        m.customProgramCacheKey=()=> 'viewer-base-red-brick';
      }
      // MMR is exported repacked into ORM order (roughness->green, metalness->blue) so one texture
      // drives both maps the way three.js samples them. The scene has an environment map, so the
      // metallic belt/buckle now reflects it instead of rendering black (which is why metalness used
      // to be forced to 0). Plastic areas have metalness 0 in the map and stay diffuse.
      const r=tex(s.mmr,false);
      if(r){m.roughnessMap=r;m.metalnessMap=r;m.roughness=1;m.metalness=1;
        say('  '+(info.file||'')+': MMR '+s.mmr.split('/').pop());}
      else{m.roughnessMap=null;m.metalnessMap=null;
        m.roughness=(s.rough===null||s.rough===undefined)?0.55:s.rough;
        m.metalness=(s.metal===null||s.metal===undefined)?0:s.metal;}
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
      // CUE4Parse's converted face zones do not retain a consistent winding order. Keep them
      // two-sided, but use the face-specific depth rules below so the layers stay stable.
      m.side=THREE.DoubleSide;
      // The body mesh carries a COLOR_0 vertex-colour set (a mask/AO), which GLTFLoader turns into
      // vertexColors=true. That multiplies the albedo - if those colours are dark the whole surface
      // goes black regardless of texture or UV. It is not the display colour, so switch it off.
      if(m.vertexColors){m.vertexColors=false;say('  '+(info.file||'')+': disabled vertexColors');}
      // Same opt-in as above: any material we build or rebind on a skinned mesh must carry it.
      if(o.isSkinnedMesh&&!m.skinning)m.skinning=true;
      // Keep the live maps around for the viewer-only material editor. This is deliberately
      // separate from suit state: it helps diagnose a material without rewriting anything.
      if(!info.isface){
        const part=info.label||info.part||info.base||info.file||'Part';
        materialEditorEntries.push({
          label:part+' - material '+(li+1),material:m,
          enabled:{base:true,normal:true,mmr:true,ao:true},
          available:{base:!!m.map,normal:!!m.normalMap,mmr:!!(m.roughnessMap||m.metalnessMap),ao:!!m.aoMap},
          original:{map:m.map,normalMap:m.normalMap,roughnessMap:m.roughnessMap,metalnessMap:m.metalnessMap,
            aoMap:m.aoMap,roughness:m.roughness,metalness:m.metalness}
        });
      }
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
        const band=b[0],tris=b[1],texPath=b[2],tint=b[3],mode=b[4]||0,additive=mode===1;
        const feature=b[5]||('Zone'+band),pdo=b[6]||0;
        const nrmPath=b[7],ormPath=b[8],rough=b[9],metal=b[10],emisPath=b[11],emisCol=b[12],emisStr=b[13];
        const eyeSpecLayer=b[14],mouthLayers=b[15];
        // Use the material's OWN roughness/metallic (the face sets 0.3 nearly everywhere) rather
        // than a viewer default.
        const m2=new THREE.MeshStandardMaterial({color:0xffffff,
          roughness:(rough===null||rough===undefined)?0.3:rough,
          metalness:(metal===null||metal===undefined)?0:metal});
        // Some exported face-zone triangles are reverse-wound after the Unreal-to-glTF conversion.
        // They must stay visible from both sides; depth writes are controlled per layer below.
        m2.side=THREE.DoubleSide;
        // r128 requires a material to OPT IN to skeletal deformation. GLTFLoader sets this on the
        // materials it creates; ours are built from scratch, so without it the face renders in BIND
        // pose forever - the bones move, the skeleton updates, and the GPU ignores all of it.
        m2.skinning=o.isSkinnedMesh;
        // Feature textures are WHITE STENCILS: the alpha is the shape, the colour comes from the
        // material's "<feature> Tint" (brows 442E2B, skin D28856). Tint is linear, so convert.
        if(texPath){const t=tex(texPath,true);if(t){
          m2.map=t;
          // M_LEGOface is BLEND_Masked in the cooked game material. Keep a depth-writing cutout
          // rather than alpha-blending the feature shell into the head; Unreal's default masked
          // clip value is 0.333.
          m2.transparent=false;m2.alphaTest=0.333;
          // The rest of the feature's material. Without these the prints render as flat decals:
          // the normal map is what makes printed ink sit proud of the plastic, and the MMR is what
          // stops every zone sharing one uniform gloss.
          if(nrmPath){const n=tex(nrmPath,false);if(n){m2.normalMap=n;}}
          if(ormPath){const om2=tex(ormPath,false);if(om2){m2.roughnessMap=om2;m2.metalnessMap=om2;}}
          if(emisPath){const e=tex(emisPath,true);if(e){m2.emissiveMap=e;
            m2.emissive=new THREE.Color(emisCol||0xffffff).convertSRGBToLinear();
            m2.emissiveIntensity=(emisStr===null||emisStr===undefined)?1:emisStr;}}
          // EyeSpec is a shipped master layer with its own UV transform and expression curves.
          // Install it after the base eye is available, before the first material compilation.
          if(eyeSpecLayer)installEyeSpec(m2,eyeSpecLayer);
          if(additive){
            // An "Over" layer is the printed detail: artwork in RGB on a BLACK field, opaque alpha.
            // Added over the skin shell, black contributes nothing and only the print shows. It is
            // unlit, because the skin underneath already carries the lighting.
            const om=new THREE.MeshBasicMaterial({map:t,transparent:true,depthWrite:false,
              blending:THREE.AdditiveBlending,side:THREE.DoubleSide,toneMapped:false,skinning:o.isSkinnedMesh});
            om.polygonOffset=true;om.polygonOffsetFactor=-4;om.polygonOffsetUnits=-4;
            om.needsUpdate=true;
            mats.push(om);faceBandMats.push({band:band,mat:om,tris:tris,tex:texPath,feature:feature,tint:tint,pdo:pdo,mesh:o,slot:mats.length-1});
            geo.addGroup(off,tris*3,mats.length-1);off+=tris*3;return;
          }
        }}
        else if(!tint)m2.visible=false;   // no texture AND no tint = feature this face does not use
        // Band 13's texture is only the black mouth rim. The master material composites its real
        // TeethU, TeethD and Tongue maps on this same skinned shell, then the expression curves
        // slide those sheets independently. Do that before the material first compiles.
        if(mouthLayers&&m2.map)installMouthLayers(m2,mouthLayers,info.mhide);
        // Face tints behave as sRGB, not linear: taken raw, brow 442E2B renders mid-brown and the
        // D28856 nose/mouth print is so close to skin it vanishes. Converting matches the game -
        // near-black brows and a print that actually reads against the head.
        if(tint)m2.color=new THREE.Color(tint).convertSRGBToLinear();
        // Keep the full skin shell in the depth buffer. Every printed feature is a masked overlay:
        // it should test just in front of the skin but never write depth over another feature.
        // Three.js polygon-offset factors depend on camera angle, so they are not a replacement for
        // Unreal's PixelDepthOffset on these nearly coincident face shells.
        m2.depthTest=true;m2.depthWrite=band===8;
        if(band!==8){m2.polygonOffset=true;m2.polygonOffsetFactor=0;m2.polygonOffsetUnits=-2;}
        // Setting .skinning alone is not enough: the flag feeds SHADER COMPILATION, and a plain
        // property assignment does not mark the program dirty. Without needsUpdate the material
        // keeps its unskinned program and the face stays in bind pose.
        m2.needsUpdate=true;
        mats.push(m2);
        faceBandMats.push({band:band,mat:m2,tris:tris,tex:texPath,feature:feature,tint:tint,pdo:pdo,mesh:o,slot:mats.length-1,eyeSpec:m2.userData.faceEyeSpec||null,mouth:m2.userData.faceMouth||null});
        geo.addGroup(off,tris*3,mats.length-1);
        off+=tris*3;
      });
      o.material=mats;
      // Fires after this mesh's first real draw, which is the moment the material swap becomes
      // meaningful. Works even where requestAnimationFrame never runs.
      o.onAfterRender=function(){faceDrawn=true;};
      say(info.file+': neutral face layers '+info.fbands.filter(b=>b[2]).length+'/'+info.fbands.length+' textured');
    }
  });
}
// Apply the facial rig pose from the game's expression animation. Without this the face renders in
// BIND pose, which the game never shows.
const faceRig={bones:[],bind:new Map(),poses:null,curves:null,frameKeys:[]};
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
  faceRig.curves=info.curves||{};
  let skinned=0,verts=0;
  g.scene.traverse(o=>{
    if(o.isBone||o.type==='Bone'){
      faceRig.bones.push(o);
      faceRig.bind.set(o,{p:o.position.clone(),q:o.quaternion.clone(),s:o.scale.clone()});
    }
    if(o.isSkinnedMesh){skinned++;verts+=(o.geometry.attributes.position||{count:0}).count;}
  });
  // Diagnose the rig rather than guessing: a pose does nothing if the face mesh came through
  // unskinned, or if the animation's bone names do not match the exported node names.
  const any=faceRig.poses[Object.keys(faceRig.poses)[0]];
  const first=any?any[Object.keys(any)[0]]:null;
  const poseNames=first?Object.keys(first):[];
  const boneNames=faceRig.bones.map(b=>b.name);
  const hit=poseNames.filter(n=>boneNames.indexOf(n)>=0).length;
  say('face rig: '+faceRig.bones.length+' bones, '+skinned+' skinned meshes ('+verts+' verts), pose targets '
      +poseNames.length+', matched '+hit);
  if(poseNames.length&&!hit){
    say('  pose bones: '+poseNames.slice(0,6).join(', '));
    say('  glb bones:  '+boneNames.slice(0,6).join(', '));
  }
  buildExpressionUi();
}
// Apply one of the game's expression poses to the facial rig (or restore the bind pose).
let applyCount=0;
function applyExpression(name,frameIdx){
  // The swap needs the face to have been drawn once. onAfterRender covers the normal case; this
  // second call onwards covers anything that renders without firing it.
  if(++applyCount>1)faceDrawn=true;
  forceSkinningRecompile();
  const frames=name&&faceRig.poses?faceRig.poses[name]:null;
  let pose=null,materialCurves=null;
  if(frames){
    const keys=Object.keys(frames).map(Number).sort((a,b)=>a-b);
    faceRig.frameKeys=keys;
    const fi=Math.min(frameIdx===undefined?Math.floor(keys.length/2):frameIdx,keys.length-1);
    pose=frames[keys[fi]];
    materialCurves=faceRig.curves&&faceRig.curves[name]?faceRig.curves[name][keys[fi]]:null;
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
  applyFaceMaterialCurves(materialCurves);
}
// Live band inspector: every ExtraUV0 band with the feature it was identified as, its triangle
// count, the colour it resolved to and a visibility toggle. This is the tool for answering "which
// shell is that?" by eye instead of another round of guess-and-rebuild.
// The program the renderer builds for a band material on its FIRST draw comes out UNSKINNED, so
// the face renders in bind pose while the bones move. Marking the material needsUpdate does NOT
// clear it - measured: still 0 pixels of change. REPLACING the material objects after that first
// draw does: 8958 pixels between Neutral and Screaming. So swap in clones, once, post-draw.
let skinningFixed=false,faceDrawn=false;
function forceSkinningRecompile(){
  // Only valid once the face has genuinely been drawn - swapping before that just recreates the
  // same broken state, and latching a "done" flag then would lock it in permanently.
  if(skinningFixed||faceDrawn===false||!faceBandMats.length)return;
  const mesh=faceBandMats[0].mesh;
  if(!mesh||!Array.isArray(mesh.material))return;
  const fresh=mesh.material.map(mm=>{const n=mm.clone();n.needsUpdate=true;return n;});
  mesh.material=fresh;
  faceBandMats.forEach(f=>{if(f.slot<fresh.length)f.mat=fresh[f.slot];});
  skinningFixed=true;
  say('face: rebound '+fresh.length+' band materials so skinning takes effect');
}
function buildBandInspector(){
  if(document.getElementById('bands'))return;
  const p=document.createElement('div');p.id='bands';
  p.style.cssText='position:fixed;left:8px;top:8px;max-height:88vh;overflow:auto;background:rgba(12,14,18,.88);'
    +'color:#dfe4ea;font:11px/1.45 Consolas,monospace;padding:8px 10px;border:1px solid #2b3038;border-radius:6px;z-index:20';
  const h=document.createElement('div');
  h.style.cssText='font-weight:bold;color:#e8b64c;margin-bottom:6px;cursor:pointer';
  h.textContent='Face bands ('+faceBandMats.length+') - click to collapse';
  const body=document.createElement('div');
  h.onclick=()=>{body.style.display=body.style.display==='none'?'block':'none';};
  p.appendChild(h);p.appendChild(body);
  faceBandMats.slice().sort((a,b)=>a.band-b.band).forEach(f=>{
    const row=document.createElement('label');
    row.style.cssText='display:flex;align-items:center;gap:6px;padding:1px 0;white-space:nowrap;cursor:pointer';
    const cb=document.createElement('input');cb.type='checkbox';cb.checked=f.mat.visible!==false;
    cb.onchange=()=>{f.mat.visible=cb.checked;};
    const sw=document.createElement('span');
    sw.style.cssText='width:11px;height:11px;border:1px solid #555;display:inline-block;flex:none;background:'
      +(f.tint||'#ffffff');
    const t=document.createElement('span');
    t.textContent=String(f.band).padStart(2,'0')+'  '+f.feature+'  '+f.tris+'t'
      +(f.tex?'':'  (no tex)')+(f.pdo?'  pdo '+f.pdo:'');
    if(!f.tex)t.style.opacity='.6';
    row.appendChild(cb);row.appendChild(sw);row.appendChild(t);body.appendChild(row);
  });
  const all=document.createElement('div');
  all.style.cssText='margin-top:6px;display:flex;gap:6px';
  [['all on',true],['all off',false]].forEach(([lbl,v])=>{
    const b=document.createElement('button');b.textContent=lbl;
    b.style.cssText='font:11px Consolas,monospace;background:#232833;color:#dfe4ea;border:1px solid #39404d;border-radius:3px;cursor:pointer;padding:2px 6px';
    b.onclick=()=>{faceBandMats.forEach(f=>{f.mat.visible=v;});
      body.querySelectorAll('input').forEach(i=>{i.checked=v;});};
    all.appendChild(b);});
  body.appendChild(all);
  document.body.appendChild(p);
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
  sel.innerHTML='<option value="">Bind pose (debug)</option>'+names.map(n=>'<option>'+n+'</option>').join('');
  sel.onchange=()=>applyExpression(sel.value);
  wrap.appendChild(sel);
  // Scrub the sampled frames: these clips ease in, hold, then relax, so the pose you
  // want is somewhere in the middle - this finds it by eye instead of by guessing.
  const row=document.createElement('div');
  row.style.cssText='margin-top:6px;display:flex;align-items:center;gap:6px';
  const sl=document.createElement('input');
  sl.type='range';sl.id='frame';sl.min=0;sl.max=4;sl.value=2;sl.style.width='110px';
  sl.oninput=()=>applyExpression(sel.value,+sl.value);
  const lab=document.createElement('span');lab.id='frameLabel';lab.textContent='frame';
  lab.style.cssText='font-size:12px;color:#9ea6b2;min-width:64px';
  row.appendChild(sl);row.appendChild(lab);
  wrap.appendChild(row);
  document.body.appendChild(wrap);
  // The exported glTF's bind pose is not the game at rest: its post-process face AnimBP applies
  // A_Neutral at runtime. Start there, while leaving the raw bind pose available for diagnostics.
  if(names.indexOf('Neutral')>=0){sel.value='Neutral';applyExpression('Neutral',+sl.value);}
}
function frameAll(){
  const box=new THREE.Box3().setFromObject(root);
  if(box.isEmpty()){say('frame: scene is EMPTY - nothing was added');return;}
  const size=box.getSize(new THREE.Vector3());const center=box.getCenter(new THREE.Vector3());
  root.position.sub(center);
  const d=Math.max(size.x,size.y,size.z)||1;
  // LEGOfig faces +X in the exported glTF basis. Framing from +Z opened every preview in profile,
  // which makes face and head-attachment checks need an immediate manual orbit.
  camera.position.set(d*1.7,d*0.25,0);camera.near=d/100;camera.far=d*40;camera.updateProjectionMatrix();
  controls.target.set(0,0,0);controls.update();
  // One line saying what actually reached the scene, so a bad render can be read off the screen.
  say('frame: '+root.children.length+' parts, size '
      +size.x.toFixed(2)+'x'+size.y.toFixed(2)+'x'+size.z.toFixed(2));
}
function load(m){return new Promise(res=>loader.load(m.file,g=>{try{dress(g,m);poseFace(g,m);
  prepareUvChannels(g,m).then(()=>res({m,scene:g.scene}),e=>{
    say('Prepare error ('+m.file+'): '+(e&&e.message||e));res({m,scene:g.scene});});}
  catch(e){say('Model error ('+m.file+'): '+(e&&e.message||e));res(null);}},
  undefined,e=>{document.getElementById('err').textContent='Load error ('+m.file+'): '+(e&&e.message||e);res(null);}));}
Promise.all(models.map(load)).then(loaded=>{
  loaded=loaded.filter(Boolean);
  // Offsets are precomputed in C# from the exported glb skeletons, so the viewer just places them.
  loaded.forEach(x=>{
    if(x.m.scale)x.scene.scale.set(x.m.scale[0],x.m.scale[1],x.m.scale[2]);
    if(x.m.rot)x.scene.quaternion.set(x.m.rot[0],x.m.rot[1],x.m.rot[2],x.m.rot[3]);
    if(x.m.pos)x.scene.position.add(new THREE.Vector3(x.m.pos[0],x.m.pos[1],x.m.pos[2]));
    const o=x.m.offset;
    if(o&&(o[0]||o[1]||o[2]))x.scene.position.add(new THREE.Vector3(o[0],o[1],o[2]));
    const basePosition=x.scene.position.clone();
    const adjustment=Array.isArray(x.m.adj)?x.m.adj:[0,0,0];
    if(adjustment[0]||adjustment[1]||adjustment[2]){
      x.scene.position.add(new THREE.Vector3(adjustment[0]||0,adjustment[1]||0,adjustment[2]||0));
    }
    const availableUvs=usableUvChannels(x.scene,x.m.uvs);
    if(x.m.part&&!x.m.part.startsWith('__')&&(x.m.move||availableUvs.length)){
      const defaultUv=availableUvs.indexOf(x.m.uvdefault)>=0?x.m.uvdefault:availableUvs[0];
      const selectedUv=availableUvs.indexOf(x.m.uv)>=0?x.m.uv:defaultUv;
      partStates.set(x.m.part,{component:x.m.part,label:x.m.label||x.m.part,scene:x.scene,basePosition:basePosition,baseScale:x.scene.scale.clone(),custom:!!x.m.custom,face:!!x.m.isface,
        customId:x.m.mesh&&x.m.mesh.id||null,authored:x.m.mesh||null,scale:1,
        movable:!!x.m.move,adjustment:[adjustment[0]||0,adjustment[1]||0,adjustment[2]||0],
        uvs:availableUvs,defaultUv:defaultUv,uvChannel:selectedUv});
      const state=partStates.get(x.m.part);
      if(state&&state.custom){
        state.customGeometry=[];
        x.scene.traverse(o=>{
          const position=o.isMesh&&o.geometry&&o.geometry.attributes.position;
          if(!position)return;
          const normal=o.geometry.attributes.normal;
          state.customGeometry.push({mesh:o,position:Float32Array.from(position.array),normal:normal?Float32Array.from(normal.array):null});
        });
      }
      setPartUv(x.m.part,selectedUv);
    }
    root.add(x.scene);
  });
  // Frame the scene BEFORE anything optional runs: frameAll is what positions the camera, so if
  // a later step throws the camera is left at the origin - inside the character, which reads as a
  // "stuck camera" with no error on screen.
  frameAll();
  buildPartMover();
  buildCustomMeshMover();
  buildRedBrickTintUi();
  buildMaterialEditor();
}).catch(e=>say('Scene error: '+(e&&e.stack||e&&e.message||e)));
addEventListener('error',e=>say('Script error: '+(e&&e.message||e)));
addEventListener('unhandledrejection',e=>say('Promise error: '+(e&&e.reason&&e.reason.message||e&&e.reason||e)));
// Calibration aid: arrows move the face piece in 0.004 steps and report the total, so the right
// permanent offset can be read straight off the HUD instead of guessed.
addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight);});
let skinFixFrame=0;
(function loop(){requestAnimationFrame(loop);controls.update();renderer.render(scene,camera);
  // Once the face has actually been drawn, swap its band materials (see forceSkinningRecompile).
  if(faceBandMats.length&&!skinningFixed&&++skinFixFrame>2){faceDrawn=true;forceSkinningRecompile();}
  // Project the face onto the head a few frames in, when the skeleton and world
  // matrices are actually live - doing it during load silently no-ops.
})();
</script></body></html>
""";
}
