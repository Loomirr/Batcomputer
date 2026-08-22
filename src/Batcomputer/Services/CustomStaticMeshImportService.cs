namespace Batcomputer;

/// <summary>Stages project-owned OBJ attachments into the current suit.</summary>
public sealed class CustomStaticMeshImportService
{
    private const string ComponentDonorPlayable = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Playable";
    private const string ComponentDonorCutscene = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Cutscene";
    private const string DefaultHeadMaterial = "/Game/Characters/Attachments/Hat/Batman08/MI_Hat_Batman08";

    // These are the attachment definitions declared by CAE_Default_AttachmentDef in the game.
    // A static component shell can be mounted on any of them; whether it is visually useful is
    // up to the imported mesh and the material the author assigns.
    private static readonly IReadOnlyList<AttachmentSlotDefinition> AttachmentSlotDefinitions =
    [
        new("Cape", "Back attachment / cape", "Root"),
        new("Head", "Head / hat / cowl", "HeadStud_Attach_Socket", CanHideBaseHead: true),
        new("Face", "Face overlay", "Head_Socket"),
        new("Torso", "Chest attachment", "Chest_Socket"),
        new("Costume", "Costume root", "Root"),
        new("CustomHead", "Custom head", "Neck_Socket"),
        new("Hip", "Hip / belt", "Pelvis_Minifig_Socket"),
        new("Shoulder", "Shoulder", "Neck"),
        new("Offset_Neck", "Neck offset", "Neck"),
        new("Offset_Hip", "Hip offset", "Spine_01"),
        new("Collar", "Collar", "Root"),
        new("LWrist", "Left wrist", "WristRoll_L"),
        new("Spine", "Spine / back", "Spine_02_Socket"),
        new("Torso2", "Second chest attachment", "Chest_Socket"),
    ];

    public sealed record AttachmentSlotDefinition(
        string Id,
        string Label,
        string AttachSocket,
        bool CanHideBaseHead = false)
    {
        public override string ToString() => $"{Label} - {AttachSocket}";
    }

    public static IReadOnlyList<AttachmentSlotDefinition> AttachmentSlots => AttachmentSlotDefinitions;

    /// <summary>One OBJ proof made before imported meshes were stored in the suit project.</summary>
    public sealed record LegacyObjProof(
        string DisplayName,
        string SourceObjPath,
        float Scale,
        float OffsetX,
        float OffsetY,
        float OffsetZ);

    public static AttachmentSlotDefinition ResolveAttachmentSlot(string? id, string? socket = null) =>
        AttachmentSlotDefinitions.FirstOrDefault(slot =>
            slot.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? AttachmentSlotDefinitions.FirstOrDefault(slot =>
            slot.AttachSocket.Equals(socket?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? AttachmentSlotDefinitions.Single(slot => slot.Id.Equals("Head", StringComparison.OrdinalIgnoreCase));

    public LegacyObjProof? FindLegacyObjProof(NativeSuitProject project, string projectRoot)
    {
        var meshesRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId,
            "GraftedPartStage",
            "LEGOBatmanLotDK",
            "Content",
            "Mods");
        if (!Directory.Exists(meshesRoot))
        {
            return null;
        }

        foreach (var reportPath in Directory.EnumerateFiles(meshesRoot, "*.obj-probe-report.json", SearchOption.AllDirectories))
        {
            try
            {
                using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = report.RootElement;
                var sourcePath = root.TryGetProperty("sourceObjPath", out var sourceValue)
                    ? sourceValue.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                static float ReadFloat(System.Text.Json.JsonElement element, string name, float fallback = 0f) =>
                    element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number)
                        ? number
                        : fallback;

                return new LegacyObjProof(
                    Path.GetFileNameWithoutExtension(sourcePath),
                    sourcePath,
                    ReadFloat(root, "scale", 150f),
                    ReadFloat(root, "offsetX"),
                    ReadFloat(root, "offsetY"),
                    ReadFloat(root, "offsetZ"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  legacy OBJ report ignored '{reportPath}': {ex.Message}");
            }
        }

        return null;
    }

    public static string MeshPackagePathFor(NativeSuitProject project, CustomStaticMeshImport import)
    {
        if (!string.IsNullOrWhiteSpace(import.MeshPackagePath))
        {
            return UnrealPathUtil.NormalizePackagePath(import.MeshPackagePath);
        }

        return $"/Game/Mods/{ModIdFromProject(project)}/Meshes/SM_Custom_{MakeSafeToken(import.Id)}";
    }

    /// <summary>
    /// Returns the stable Blueprint component identity for an imported mesh. The generated token is
    /// deliberately separate from <see cref="CustomStaticMeshImport.DisplayName"/> so an author can
    /// rename the mesh in Batcomputer without invalidating staged assets or saved material rows.
    /// </summary>
    public static string ComponentNameFor(CustomStaticMeshImport import)
    {
        return string.IsNullOrWhiteSpace(import.ResolvedComponent)
            ? "CustomMesh_" + MakeSafeToken(import.Id)
            : import.ResolvedComponent.Trim();
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public bool TransientFileLock { get; set; }
        public string MeshPackagePath { get; set; } = "";
        public string ResolvedComponent { get; set; } = "";
        public List<string> Log { get; set; } = [];
    }

    public string CopySourceIntoProject(string projectRoot, NativeSuitProject project, CustomStaticMeshImport import, string sourceObjPath)
    {
        if (!File.Exists(sourceObjPath))
        {
            throw new FileNotFoundException("The selected OBJ file was not found.", sourceObjPath);
        }
        if (!Path.GetExtension(sourceObjPath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Custom static mesh import currently supports Wavefront OBJ files only.");
        }

        NormalizeImport(import);
        var destinationDirectory = Path.Combine(new SuitProjectService(projectRoot).ProjectOutputDirectory(project), "ImportedMeshes");
        Directory.CreateDirectory(destinationDirectory);
        var destinationName = MakeSafeToken(import.Id) + ".obj";
        var destination = Path.Combine(destinationDirectory, destinationName);
        if (!Path.GetFullPath(sourceObjPath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceObjPath, destination, overwrite: true);
        }
        import.SourceObjRelativePath = Path.Combine("ImportedMeshes", destinationName);
        return destination;
    }

    public Result Stage(NativeSuitProject project, string projectRoot, CustomStaticMeshImport import)
    {
        var result = new Result();
        try
        {
            NormalizeImport(import);
            var attachment = ResolveAttachmentSlot(import.Target, import.AttachSocket);
            if (import.Scale is < 1f or > 1000f)
            {
                throw new InvalidOperationException("Custom mesh scale must be between 1 and 1000.");
            }
            if (!float.IsFinite(import.OffsetX) || !float.IsFinite(import.OffsetY) || !float.IsFinite(import.OffsetZ))
            {
                throw new InvalidOperationException("Custom mesh offsets must be finite numbers.");
            }
            if (!float.IsFinite(import.RotationPitch) || !float.IsFinite(import.RotationYaw) || !float.IsFinite(import.RotationRoll) ||
                MathF.Abs(import.RotationPitch) > 360f || MathF.Abs(import.RotationYaw) > 360f || MathF.Abs(import.RotationRoll) > 360f)
            {
                throw new InvalidOperationException("Custom mesh rotations must be between -360 and 360 degrees.");
            }

            var sourceObjPath = ResolveProjectObjPath(projectRoot, project, import);
            if (!File.Exists(sourceObjPath))
            {
                throw new FileNotFoundException("The OBJ saved with this suit could not be found.", sourceObjPath);
            }

            var extractedContentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
            if (!Directory.Exists(extractedContentRoot))
            {
                throw new DirectoryNotFoundException("Extracted game Content is required to stage a custom static mesh.");
            }
            if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
            {
                throw new FileNotFoundException("The Unreal mappings file is required to stage a custom static mesh.", mappingsPath);
            }

            var partIndexService = new PartIndexService(projectRoot);
            var partIndex = partIndexService.LoadPartIndex() ?? partIndexService.BuildPartIndex();
            var modId = ModIdFromProject(project);
            var meshName = "SM_Custom_" + MakeSafeToken(import.Id);
            var meshPackage = $"/Game/Mods/{modId}/Meshes/{meshName}";
            var componentSlot = ComponentNameFor(import);
            var savedMaterial = project.MaterialAssignments.LastOrDefault(assignment =>
                assignment.Slot == 0 &&
                "both".Equals(assignment.Context, StringComparison.OrdinalIgnoreCase) &&
                ComponentWithoutSlot(assignment.Component).Equals(
                    ComponentWithoutSlot(componentSlot),
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(savedMaterial?.MiPackagePath))
            {
                // Older projects could have a valid inspector assignment while the import still
                // remembered its donor material. Reconcile that state before regenerating the mesh.
                import.MaterialPath = savedMaterial.MiPackagePath;
            }
            var materialPackage = UnrealPathUtil.NormalizePackagePath(string.IsNullOrWhiteSpace(import.MaterialPath)
                ? DefaultHeadMaterial
                : import.MaterialPath);
            if (!materialPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Custom mesh material must be a /Game/ material path.");
            }

            var playablePart = CreateStaticAttachmentPart(partIndex, "playable", ComponentDonorPlayable, attachment, meshPackage, meshName, materialPackage);
            var cutscenePart = CreateStaticAttachmentPart(partIndex, "cutscene", ComponentDonorCutscene, attachment, meshPackage, meshName, materialPackage);
            var graft = new PartGraftService(projectRoot).CreateSelectedPartGraftedStage(
                project,
                playablePart,
                cutscenePart,
                componentSlot,
                cloneSlot: playablePart.TemplateSlot,
                attachSocket: attachment.AttachSocket);
            if (graft.PackageResults.Any(package => package.TransientFileLock))
            {
                var lockError = graft.PackageResults
                    .First(package => package.TransientFileLock).Error;
                throw new TransientFileLockException(
                    "A generated character package was temporarily locked while grafting the custom mesh. " + lockError);
            }
            if (!HasCompleteCharacterGraft(graft.PackageResults))
            {
                var errors = graft.PackageResults
                    .Where(package => !string.IsNullOrWhiteSpace(package.Error))
                    .Select(package => $"{package.Role}: {package.Error}")
                    .ToList();
                if (!graft.PackageResults.Any(package =>
                        package.Role.Equals("playable", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("playable: no graft result");
                }
                if (!graft.PackageResults.Any(package =>
                        package.Role.Equals("cutscene", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("cutscene: no graft result");
                }
                throw new InvalidOperationException(
                    "The custom static mesh component could not be grafted. " + string.Join(" | ", errors));
            }

            var mesh = new StaticMeshObjProbeService().CreateObjHeadProbe(new StaticMeshObjProbeService.Request
            {
                ExtractedContentRoot = extractedContentRoot,
                UsmapPath = mappingsPath,
                OutputContentRoot = graft.GraftedContentRoot,
                OutputPackagePath = meshPackage,
                ObjPath = sourceObjPath,
                Scale = import.Scale,
                OffsetX = import.OffsetX,
                OffsetY = import.OffsetY,
                OffsetZ = import.OffsetZ,
                RotationPitch = import.RotationPitch,
                RotationYaw = import.RotationYaw,
                RotationRoll = import.RotationRoll,
            });
            if (!mesh.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                if (mesh.TransientFileLock)
                {
                    throw new TransientFileLockException(
                        "The generated custom mesh asset was temporarily locked. " + mesh.Error);
                }
                throw new InvalidOperationException("The OBJ mesh could not be generated. " + mesh.Error);
            }

            import.ResolvedComponent = graft.PackageResults
                .Where(package => package.Success && !string.IsNullOrWhiteSpace(package.TargetSlot))
                .Select(package => package.TargetSlot)
                .FirstOrDefault() ?? componentSlot;
            import.MeshPackagePath = meshPackage;
            result.MeshPackagePath = meshPackage;
            result.ResolvedComponent = import.ResolvedComponent;
            result.Status = "created";
            result.Log.Add($"Imported {mesh.VertexCount} vertices and {mesh.TriangleCount} double-sided triangles from {Path.GetFileName(sourceObjPath)}.");
            result.Log.Add($"Mounted as {attachment.Label} on {attachment.AttachSocket}.");
            result.Log.Add($"Applied scale {import.Scale:0.###}, offset ({import.OffsetX:0.###}, {import.OffsetY:0.###}, {import.OffsetZ:0.###}), and rotation ({import.RotationPitch:0.##}, {import.RotationYaw:0.##}, {import.RotationRoll:0.##}).");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
            result.TransientFileLock = FileLockUtil.IsTransient(ex);
        }
        return result;
    }

    internal static bool HasCompleteCharacterGraft(IEnumerable<PartGraftPackageResult> packages)
    {
        var results = packages.ToList();
        return results.Any(package =>
                   package.Role.Equals("playable", StringComparison.OrdinalIgnoreCase) && package.Success) &&
               results.Any(package =>
                   package.Role.Equals("cutscene", StringComparison.OrdinalIgnoreCase) && package.Success) &&
               results.All(package => package.Success);
    }

    private static NativeSuitPartRecord CreateStaticAttachmentPart(
        NativeSuitPartIndex partIndex,
        string context,
        string sourcePackage,
        AttachmentSlotDefinition attachment,
        string meshPackage,
        string meshName,
        string materialPackage)
    {
        var donor = partIndex.Parts.FirstOrDefault(part =>
            part.Context.Equals(context, StringComparison.OrdinalIgnoreCase) &&
            part.SourcePackagePath.Equals(sourcePackage, StringComparison.OrdinalIgnoreCase) &&
            part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            part.ComponentClass.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) &&
            part.AttachSocket.Equals("HeadStud_Attach_Socket", StringComparison.OrdinalIgnoreCase));
        donor ??= partIndex.Parts.FirstOrDefault(part =>
            part.Context.Equals(context, StringComparison.OrdinalIgnoreCase) &&
            part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            part.ComponentClass.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) &&
            part.AttachSocket.Equals("HeadStud_Attach_Socket", StringComparison.OrdinalIgnoreCase));
        if (donor is null)
        {
            throw new InvalidOperationException($"The verified {context} static-component donor was not found in the part index.");
        }

        var custom = PartRecipeService.Clone(donor);
        custom.MeshPackagePath = meshPackage;
        custom.MeshObjectName = meshName;
        custom.MeshObjectPath = meshPackage + "." + meshName;
        custom.AttachSocket = attachment.AttachSocket;
        var materialName = UnrealPathUtil.AssetName(materialPackage);
        custom.Materials =
        [
            new NativeSuitObjectRef
            {
                ObjectName = materialName,
                PackagePath = materialPackage,
                ObjectPath = materialPackage + "." + materialName,
                ClassName = "MaterialInstanceConstant"
            }
        ];
        custom.SemanticKind = "CustomStaticMesh";
        custom.IsSynthesized = true;
        custom.RecipeKey = PartRecipeService.BuildRecipeKey(custom);
        return custom;
    }

    private static string ResolveProjectObjPath(string projectRoot, NativeSuitProject project, CustomStaticMeshImport import)
    {
        if (string.IsNullOrWhiteSpace(import.SourceObjRelativePath))
        {
            throw new InvalidOperationException("This custom mesh has no project-owned OBJ source.");
        }
        var root = Path.GetFullPath(new SuitProjectService(projectRoot).ProjectOutputDirectory(project))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, import.SourceObjRelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The saved OBJ path points outside this suit project.");
        }
        return path;
    }

    private static string ModIdFromProject(NativeSuitProject project)
    {
        var segments = (project.TargetPackages.Playable ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var modsIndex = Array.FindIndex(segments, segment => segment.Equals("Mods", StringComparison.OrdinalIgnoreCase));
        var mod = modsIndex >= 0 && modsIndex + 1 < segments.Length ? segments[modsIndex + 1] : "";
        mod = ModProjectService.DeriveModId(mod);
        if (string.IsNullOrWhiteSpace(mod))
        {
            throw new InvalidOperationException("Set a visual base before importing a custom static mesh.");
        }
        return mod;
    }

    private static void NormalizeImport(CustomStaticMeshImport import)
    {
        import.Id = MakeSafeToken(import.Id);
        if (string.IsNullOrWhiteSpace(import.Id))
        {
            import.Id = Guid.NewGuid().ToString("N")[..12];
        }
        import.DisplayName = string.IsNullOrWhiteSpace(import.DisplayName) ? "Custom mesh" : import.DisplayName.Trim();
        var attachment = ResolveAttachmentSlot(import.Target, import.AttachSocket);
        import.Target = attachment.Id;
        import.AttachSocket = attachment.AttachSocket;
        import.MaterialPath = string.IsNullOrWhiteSpace(import.MaterialPath) ? DefaultHeadMaterial : UnrealPathUtil.NormalizePackagePath(import.MaterialPath);
    }

    private static string MakeSafeToken(string value)
    {
        var token = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        return token.Length > 24 ? token[..24] : token;
    }

    private static string ComponentWithoutSlot(string value)
    {
        var safe = value ?? "";
        var colon = safe.IndexOf(':');
        return (colon >= 0 ? safe[..colon] : safe).Trim();
    }
}
