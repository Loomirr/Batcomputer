using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Creates one disposable native-suit stage that references the cube research mesh.
/// It is intentionally CLI-only until the full static-mesh pipeline has passed in-game.
/// </summary>
public sealed class StaticMeshAttachmentProbeService
{
    private const string ComponentDonorPlayable = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Playable";
    private const string ComponentDonorCutscene = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Cutscene";
    private const string MeshName = "SM_CubeAttachmentProof";
    private const string ClosedCubeMeshName = "SM_ClosedCubeAttachmentProof";
    private const string ObjHeadMeshName = "SM_ObjHeadAttachmentProof";
    private const string OpaqueCowlMaterialPackage = "/Game/Characters/Attachments/Hat/Batman08/MI_Hat_Batman08";
    private const string OpaqueCowlMaterialName = "MI_Hat_Batman08";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed class Request
    {
        public string ProjectRoot { get; set; } = "";
        public string SourceProjectPath { get; set; } = "";
        public string ModId { get; set; } = "";
        public float CubeScale { get; set; } = 1f;
        public bool RemoveCowl { get; set; }
        public bool UseNativeHairMaterial { get; set; }
        public bool UseOpaqueCowlMaterial { get; set; }
        public bool CenterCubeAtAttachmentOrigin { get; set; }
        public bool RewriteCubeRenderData { get; set; }
        public bool UseFourSidedHardEdgeShell { get; set; }
        public bool UseLargeClosedCube { get; set; }
        public string ObjPath { get; set; } = "";
        public float ObjScale { get; set; } = 150f;
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string ModId { get; set; } = "";
        public string SuitProjectPath { get; set; } = "";
        public string ModProjectPath { get; set; } = "";
        public string ContentRoot { get; set; } = "";
        public string MeshPackagePath { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public List<string> Log { get; set; } = [];
    }

    public Result CreateCubeAttachmentProbe(Request request)
    {
        var result = new Result { ModId = ModProjectService.DeriveModId(request.ModId) };
        try
        {
            if (string.IsNullOrWhiteSpace(result.ModId))
            {
                throw new ArgumentException("The research mod id must contain letters or numbers.");
            }

            var suitService = new SuitProjectService(request.ProjectRoot);
            var source = suitService.LoadProject(request.SourceProjectPath)
                ?? throw new FileNotFoundException("The source suit project could not be read.", request.SourceProjectPath);
            if (source.PlayableTemplate is null || source.CutsceneTemplate is null || source.DcmdTemplate is null)
            {
                throw new InvalidOperationException("The source suit must have a playable, cutscene, and DCMD base.");
            }

            var extractedContentRoot = FindContentRoot(source.PlayableTemplate.Uasset)
                ?? AppSettings.Current.EffectiveExtractedContentRoot();
            if (!Directory.Exists(extractedContentRoot))
            {
                throw new DirectoryNotFoundException("The source suit's extracted Content folder was not found.");
            }
            var mappingsPath = FindMappingsPath(request.ProjectRoot) ?? AppSettings.Current.EffectiveUsmapPath();
            if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
            {
                throw new FileNotFoundException("The Unreal mappings file is required for the static mesh proof.", mappingsPath);
            }

            // This command can be run from a development build while staging a portable install.
            // Keep dependent services pointed at the selected project's data for this process only.
            AppSettings.Current.ExtractedContentRoot = extractedContentRoot;
            AppSettings.Current.UsmapPath = mappingsPath;

            var slotId = "static_mesh_" + result.ModId.ToLowerInvariant();
            var suitPath = suitService.ProjectPathForSlot(slotId);
            var modService = new ModProjectService(request.ProjectRoot);
            var modPath = Path.Combine(modService.ModOutputRoot, result.ModId + ".native-suit-mod-project.json");
            var projectFolder = suitService.ProjectOutputDirectory(new NativeSuitProject { SlotId = slotId });
            if (File.Exists(suitPath) || File.Exists(modPath) || Directory.Exists(projectFolder))
            {
                throw new InvalidOperationException($"Research id '{result.ModId}' already exists. Pick a new id instead of overwriting a prior proof.");
            }

            var meshName = MeshNameForRequest(request);
            var playableName = $"BP_{result.ModId}_Playable";
            var cutsceneName = $"BP_{result.ModId}_Cutscene";
            var dcmdName = $"DA_DCMD_{result.ModId}_Playable";
            var project = new NativeSuitProject
            {
                SlotId = slotId,
                DisplayName = IsObjHeadRequest(request)
                    ? "Static Mesh OBJ Head Proof"
                    : request.UseLargeClosedCube
                    ? "Static Mesh Closed Cube Proof"
                    : request.UseFourSidedHardEdgeShell
                    ? "Static Mesh Side Shell Proof"
                    : request.RewriteCubeRenderData
                    ? "Static Mesh Cube Render Data Proof"
                    : request.CenterCubeAtAttachmentOrigin
                    ? "Static Mesh Cube Origin Proof"
                    : request.UseOpaqueCowlMaterial
                    ? "Static Mesh Cube Cowl Material Proof"
                    : request.UseNativeHairMaterial
                    ? "Static Mesh Cube Clean Material Proof"
                    : request.RemoveCowl ? "Static Mesh Cube No-Cowl Proof" : "Static Mesh Cube Proof",
                Description = IsObjHeadRequest(request)
                    ? "Experimental OBJ static head attachment using Batman's opaque cowl material and the validated Nightwing render-data donor."
                    : request.UseLargeClosedCube
                    ? "Experimental closed static mesh cube proof using Batman's opaque cowl material and a one-section Nightwing donor."
                    : request.UseFourSidedHardEdgeShell
                    ? "Experimental static mesh attachment proof using Batman's opaque cowl material with four independent hard-edge side faces."
                    : request.RewriteCubeRenderData
                    ? "Experimental static mesh attachment proof with rebuilt tangents, UVs, and double-sided face coverage."
                    : request.CenterCubeAtAttachmentOrigin && request.UseOpaqueCowlMaterial
                    ? "Experimental static mesh attachment proof centered at the attachment origin with Batman's opaque cowl material."
                    : request.CenterCubeAtAttachmentOrigin
                    ? "Experimental static mesh attachment proof centered at the attachment origin."
                    : request.UseOpaqueCowlMaterial
                    ? "Experimental static mesh attachment proof using Batman's opaque cowl material."
                    : request.UseNativeHairMaterial
                    ? "Experimental static mesh attachment proof using a native static-hair material."
                    : request.RemoveCowl
                    ? "Experimental oversized static mesh attachment proof without Batman's cowl."
                    : "Experimental static mesh attachment proof.",
                PawnTag = $"Pawns.Playable.Batman.{result.ModId}",
                ProgressTag = source.ProgressTag,
                PackageBaseName = result.ModId + "_P",
                TargetPackages = new TargetPackages
                {
                    Playable = $"/Game/Mods/{result.ModId}/Characters/{playableName}",
                    Cutscene = $"/Game/Mods/{result.ModId}/Characters/{cutsceneName}",
                    Dcmd = $"/Game/Mods/{result.ModId}/Characters/{dcmdName}"
                },
                PlayableTemplate = source.PlayableTemplate,
                CutsceneTemplate = source.CutsceneTemplate,
                DcmdTemplate = source.DcmdTemplate,
                VisualSourceTemplate = source.VisualSourceTemplate ?? source.PlayableTemplate,
                VisualCutsceneSourceTemplate = source.VisualCutsceneSourceTemplate ?? source.CutsceneTemplate,
                BaseProfile = source.BaseProfile,
                UseCustomArchetype = false,
                EquipmentSlots = [],
                MaterialAssignments = [],
                PartGrafts = [],
                PreviewPartPlacements = [],
                GeneratedTextures = [],
                Changes =
                [
                    new SavedChange
                    {
                        When = DateTimeOffset.UtcNow.ToString("O"),
                        Category = "Experimental",
                        Target = meshName,
                        Detail = "Disposable static mesh attachment proof.",
                        Status = "staged"
                    }
                ]
            };

            result.MeshPackagePath = $"/Game/Mods/{result.ModId}/Meshes/{meshName}";
            var partIndex = new PartIndexService(request.ProjectRoot).LoadPartIndex()
                ?? throw new InvalidOperationException("The native part index is missing. Refresh game assets and rebuild the part index before running this proof.");
            var playablePart = CreateAttachmentPart(partIndex, "playable", ComponentDonorPlayable, result.MeshPackagePath, meshName, request.UseNativeHairMaterial, request.UseOpaqueCowlMaterial);
            var cutscenePart = CreateAttachmentPart(partIndex, "cutscene", ComponentDonorCutscene, result.MeshPackagePath, meshName, request.UseNativeHairMaterial, request.UseOpaqueCowlMaterial);

            // This is a new project, so a fresh graft stage cannot affect a saved user suit.
            var graft = new PartGraftService(request.ProjectRoot).CreateSelectedPartGraftedStage(
                project,
                playablePart,
                cutscenePart,
                targetSlot: "StaticMeshCube",
                cloneSlot: "Head",
                attachSocket: "HeadStud_Attach_Socket");
            if (!graft.PackageResults.Any(package => package.Success))
            {
                throw new InvalidOperationException("The static component shell could not be grafted: " +
                    string.Join(" | ", graft.PackageResults.Where(package => !string.IsNullOrWhiteSpace(package.Error)).Select(package => package.Error)));
            }
            result.ContentRoot = graft.GraftedContentRoot;
            result.Log.Add("Cloned the verified Alfred static-component shell into the disposable playable and cutscene packages.");

            if (request.RemoveCowl)
            {
                var removal = new ComponentRemoveService(request.ProjectRoot).Remove(
                    project.SlotId,
                    project.TargetPackages.Playable,
                    project.TargetPackages.Cutscene,
                    "Head");
                if (!removal.Status.Equals("removed", StringComparison.OrdinalIgnoreCase) ||
                    removal.Files.Any(file => !file.Success))
                {
                    var detail = string.Join(" | ", removal.Files
                        .Where(file => !string.IsNullOrWhiteSpace(file.Error))
                        .Select(file => file.Error));
                    throw new InvalidOperationException("The cowl component could not be removed from both proof blueprints. " + detail);
                }
                result.Log.Add("Removed Batman's Head SCS component from both proof blueprints for an unambiguous attachment test.");
            }

            if (IsObjHeadRequest(request))
            {
                var mesh = new StaticMeshObjProbeService().CreateObjHeadProbe(new StaticMeshObjProbeService.Request
                {
                    ExtractedContentRoot = extractedContentRoot,
                    UsmapPath = mappingsPath,
                    OutputContentRoot = graft.GraftedContentRoot,
                    OutputPackagePath = result.MeshPackagePath,
                    ObjPath = request.ObjPath,
                    Scale = request.ObjScale
                });
                if (!mesh.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The OBJ static mesh proof could not be staged: " + mesh.Error);
                }
                result.Log.Add($"Imported {mesh.VertexCount} vertices and {mesh.TriangleCount} double-sided triangles from {Path.GetFileName(request.ObjPath)}.");
            }
            else if (request.UseLargeClosedCube)
            {
                var mesh = new StaticMeshClosedCubeProbeService().CreateClosedCubeProbe(new StaticMeshClosedCubeProbeService.Request
                {
                    ExtractedContentRoot = extractedContentRoot,
                    UsmapPath = mappingsPath,
                    OutputContentRoot = graft.GraftedContentRoot,
                    OutputPackagePath = result.MeshPackagePath,
                    CubeScale = request.CubeScale
                });
                if (!mesh.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The closed static mesh proof could not be staged: " + mesh.Error);
                }
                result.Log.Add("Generated a fully closed hard-edge cube from the one-section Nightwing mesh donor.");
            }
            else
            {
                var mesh = new StaticMeshMorphService().CreateCubeMorphProbe(new StaticMeshMorphService.Request
                {
                    ExtractedContentRoot = extractedContentRoot,
                    UsmapPath = mappingsPath,
                    OutputContentRoot = graft.GraftedContentRoot,
                    OutputPackagePath = result.MeshPackagePath,
                    CubeScale = request.CubeScale,
                    CenterAtAttachmentOrigin = request.CenterCubeAtAttachmentOrigin,
                    RewriteCubeRenderData = request.RewriteCubeRenderData,
                    UseFourSidedHardEdgeShell = request.UseFourSidedHardEdgeShell
                });
                if (!mesh.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The static mesh proof could not be staged: " + mesh.Error);
                }
            }
            var hasExplicitMaterial = request.UseNativeHairMaterial || request.UseOpaqueCowlMaterial;
            ValidateAttachmentComponent(graft.GraftedContentRoot, project.TargetPackages.Playable, result.MeshPackagePath, meshName, mappingsPath, "playable", hasExplicitMaterial);
            ValidateAttachmentComponent(graft.GraftedContentRoot, project.TargetPackages.Cutscene, result.MeshPackagePath, meshName, mappingsPath, "cutscene", hasExplicitMaterial);
            if (request.UseNativeHairMaterial)
            {
                result.Log.Add("Applied Alfred's native static-hair material override as a material-control test.");
            }
            if (request.UseOpaqueCowlMaterial)
            {
                result.Log.Add("Applied Batman's opaque cowl material override as a UV-independent silhouette test.");
            }
            if (request.CenterCubeAtAttachmentOrigin)
            {
                result.Log.Add(IsObjHeadRequest(request)
                    ? "Centered the generated OBJ and its mesh bounds records at the component attachment origin."
                    : "Centered the generated cube and both mesh bounds records at the component attachment origin.");
            }
            if (request.RewriteCubeRenderData)
            {
                result.Log.Add("Rebuilt the donor tangent frames and both UV channels, then made the critical cube faces double-sided.");
            }
            if (request.UseFourSidedHardEdgeShell)
            {
                result.Log.Add("Used four independent hard-edge side faces; the top and bottom remain intentionally open for this vertex-capacity proof.");
            }
            result.Log.Add("Generated the cube under the same mod content root as the disposable character packages.");

            result.SuitProjectPath = suitService.SaveProject(project);
            var mod = new NativeSuitModProject
            {
                ModId = result.ModId,
                DisplayName = project.DisplayName,
                Description = "Disposable static mesh attachment proof.",
                Suits =
                [
                    new ModSuitEntry
                    {
                        SuitProjectPath = modService.MakeRelativeSuitProjectPath(result.SuitProjectPath),
                        SuitId = project.SlotId,
                        Enabled = true,
                        MenuOrder = 100
                    }
                ]
            };
            result.ModProjectPath = modService.SaveMod(mod);

            result.Status = "created";
            result.Log.Add("Saved an isolated suit and mod project. Build that mod from Home to create the normal registry, IoStore trio, and install flow.");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }
        finally
        {
            var root = AppSettings.GeneratedRootFor(request.ProjectRoot);
            var reportDirectory = Path.Combine(root, "StaticMeshResearch");
            Directory.CreateDirectory(reportDirectory);
            result.ReportPath = Path.Combine(reportDirectory, result.ModId + "-attachment-probe-report.json");
            File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
        }

        return result;
    }

    public Result VerifyCubeAttachmentProbe(string projectRoot, string modId)
    {
        var result = new Result { ModId = ModProjectService.DeriveModId(modId) };
        try
        {
            if (string.IsNullOrWhiteSpace(result.ModId))
            {
                throw new ArgumentException("The research mod id must contain letters or numbers.");
            }

            var suitService = new SuitProjectService(projectRoot);
            var slotId = "static_mesh_" + result.ModId.ToLowerInvariant();
            result.SuitProjectPath = suitService.ProjectPathForSlot(slotId);
            var project = suitService.LoadProject(result.SuitProjectPath)
                ?? throw new FileNotFoundException("The saved research suit could not be read.", result.SuitProjectPath);
            var meshName = MeshNameForProject(project);
            result.MeshPackagePath = $"/Game/Mods/{result.ModId}/Meshes/{meshName}";
            result.ContentRoot = Path.Combine(suitService.ProjectOutputDirectory(project), "GraftedPartStage", "LEGOBatmanLotDK", "Content");
            var meshBase = Path.Combine(result.ContentRoot, "Mods", result.ModId, "Meshes", meshName);
            if (!File.Exists(meshBase + ".uasset") || !File.Exists(meshBase + ".uexp"))
            {
                throw new FileNotFoundException("The staged cube package is missing its .uasset or .uexp file.", meshBase + ".uasset");
            }

            var mappingsPath = FindMappingsPath(projectRoot) ?? AppSettings.Current.EffectiveUsmapPath();
            if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
            {
                throw new FileNotFoundException("The Unreal mappings file is required to validate the attachment probe.", mappingsPath);
            }

            var usesExplicitMaterial = project.DisplayName.Contains("Material Proof", StringComparison.OrdinalIgnoreCase) ||
                                       project.DisplayName.Contains("Render Data Proof", StringComparison.OrdinalIgnoreCase) ||
                                       project.DisplayName.Contains("Side Shell Proof", StringComparison.OrdinalIgnoreCase) ||
                                       project.Description.Contains("cowl material", StringComparison.OrdinalIgnoreCase) ||
                                       project.Description.Contains("hair material", StringComparison.OrdinalIgnoreCase);
            ValidateAttachmentComponent(result.ContentRoot, project.TargetPackages.Playable, result.MeshPackagePath, meshName, mappingsPath, "playable", usesExplicitMaterial);
            ValidateAttachmentComponent(result.ContentRoot, project.TargetPackages.Cutscene, result.MeshPackagePath, meshName, mappingsPath, "cutscene", usesExplicitMaterial);
            var expectsCowlRemoved = project.DisplayName.Contains("No-Cowl", StringComparison.OrdinalIgnoreCase) ||
                                     project.DisplayName.Contains("Origin Proof", StringComparison.OrdinalIgnoreCase) ||
                                     project.DisplayName.Contains("Render Data Proof", StringComparison.OrdinalIgnoreCase) ||
                                     project.DisplayName.Contains("Side Shell Proof", StringComparison.OrdinalIgnoreCase) ||
                                     project.DisplayName.Contains("Closed Cube Proof", StringComparison.OrdinalIgnoreCase) ||
                                     project.DisplayName.Contains("OBJ Head Proof", StringComparison.OrdinalIgnoreCase);
            if (expectsCowlRemoved)
            {
                ValidateCowlRemoved(result.ContentRoot, project.TargetPackages.Playable, mappingsPath, "playable");
                ValidateCowlRemoved(result.ContentRoot, project.TargetPackages.Cutscene, mappingsPath, "cutscene");
            }
            result.Status = "verified";
            result.Log.Add(usesExplicitMaterial
                ? "Both staged blueprints contain the mod-local static mesh component with its expected explicit material override."
                : "Both staged blueprints contain the mod-local static mesh component with no inherited material override.");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.Message;
        }

        return result;
    }

    private static NativeSuitPartRecord CreateAttachmentPart(
        NativeSuitPartIndex partIndex,
        string context,
        string sourcePackage,
        string meshPackagePath,
        string meshName,
        bool useNativeHairMaterial,
        bool useOpaqueCowlMaterial)
    {
        var donor = partIndex.Parts.FirstOrDefault(part =>
            part.Context.Equals(context, StringComparison.OrdinalIgnoreCase) &&
            part.SourcePackagePath.Equals(sourcePackage, StringComparison.OrdinalIgnoreCase) &&
            part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            part.ComponentClass.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) &&
            part.AttachSocket.Equals("HeadStud_Attach_Socket", StringComparison.OrdinalIgnoreCase));
        if (donor is null)
        {
            throw new InvalidOperationException($"The verified {context} static-component donor was not found in the part index.");
        }

        var custom = PartRecipeService.Clone(donor);
        custom.MeshPackagePath = meshPackagePath;
        custom.MeshObjectName = meshName;
        custom.MeshObjectPath = meshPackagePath + "." + meshName;
        if (useOpaqueCowlMaterial)
        {
            custom.Materials =
            [
                new NativeSuitObjectRef
                {
                    ObjectName = OpaqueCowlMaterialName,
                    PackagePath = OpaqueCowlMaterialPackage,
                    ObjectPath = OpaqueCowlMaterialPackage + "." + OpaqueCowlMaterialName,
                    ClassName = "MaterialInstanceConstant"
                }
            ];
        }
        else if (!useNativeHairMaterial)
        {
            custom.Materials = [];
        }
        custom.SemanticKind = "ExperimentalStaticMesh";
        return custom;
    }

    private static void ValidateAttachmentComponent(
        string contentRoot,
        string blueprintPackage,
        string meshPackage,
        string meshName,
        string mappingsPath,
        string role,
        bool expectsNativeHairMaterial = false)
    {
        var relative = blueprintPackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var uassetPath = Path.Combine(contentRoot, relative + ".uasset");
        var asset = new UAsset(
            uassetPath,
            EngineVersion.VER_UE5_6,
            new Usmap(mappingsPath),
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        var component = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.ObjectName.ToString().Equals("StaticMeshCube_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The {role} blueprint does not contain the generated static-mesh component.");
        var mesh = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
        if (mesh is null || mesh.Value.IsNull())
        {
            throw new InvalidOperationException($"The {role} static-mesh component has no mesh reference.");
        }
        var packageImport = asset.Imports.FirstOrDefault(import =>
            import.ObjectName.ToString().Equals(meshPackage, StringComparison.OrdinalIgnoreCase) && import.OuterIndex.IsNull());
        if (packageImport is null || !asset.Imports.Any(import =>
                import.ObjectName.ToString().Equals(meshName, StringComparison.OrdinalIgnoreCase) &&
                import.OuterIndex.Index == -(asset.Imports.IndexOf(packageImport) + 1)))
        {
            throw new InvalidOperationException($"The {role} static-mesh component is not imported from the mod-local cube package.");
        }
        var overrides = component.Data.OfType<ArrayPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("OverrideMaterials", StringComparison.OrdinalIgnoreCase));
        var overrideCount = overrides?.Value?.Length ?? 0;
        if (expectsNativeHairMaterial && overrideCount != 1)
        {
            throw new InvalidOperationException($"The {role} static-mesh component does not have its expected native hair-material override.");
        }
        if (!expectsNativeHairMaterial && overrideCount > 0)
        {
            throw new InvalidOperationException($"The {role} static-mesh component still has inherited material overrides.");
        }
    }

    private static bool IsObjHeadRequest(Request request) => !string.IsNullOrWhiteSpace(request.ObjPath);

    private static string MeshNameForRequest(Request request) => IsObjHeadRequest(request)
        ? ObjHeadMeshName
        : request.UseLargeClosedCube ? ClosedCubeMeshName : MeshName;

    private static string MeshNameForProject(NativeSuitProject project) =>
        project.DisplayName.Contains("OBJ Head Proof", StringComparison.OrdinalIgnoreCase)
            ? ObjHeadMeshName
            : project.DisplayName.Contains("Closed Cube Proof", StringComparison.OrdinalIgnoreCase)
            ? ClosedCubeMeshName
            : MeshName;

    private static void ValidateCowlRemoved(string contentRoot, string blueprintPackage, string mappingsPath, string role)
    {
        var relative = blueprintPackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var uassetPath = Path.Combine(contentRoot, relative + ".uasset");
        var asset = new UAsset(
            uassetPath,
            EngineVersion.VER_UE5_6,
            new Usmap(mappingsPath),
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        var cowlNodes = asset.Exports
            .Select((export, index) => new { Export = export as NormalExport, Index = FPackageIndex.FromExport(index).Index })
            .Where(item => item.Export is not null && item.Export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Export!.Data.OfType<NamePropertyData>().Any(property =>
                property.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ToString().Equals("Head", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Index)
            .ToHashSet();
        if (cowlNodes.Count == 0)
        {
            throw new InvalidOperationException($"The {role} blueprint no longer contains the expected Batman cowl SCS node.");
        }

        var stillConstructed = asset.Exports.OfType<NormalExport>()
            .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
            .Where(property => property.Name.ToString().Equals("RootNodes", StringComparison.OrdinalIgnoreCase) ||
                               property.Name.ToString().Equals("AllNodes", StringComparison.OrdinalIgnoreCase) ||
                               property.Name.ToString().Equals("ChildNodes", StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => property.Value ?? [])
            .OfType<ObjectPropertyData>()
            .Any(property => cowlNodes.Contains(property.Value.Index));
        if (stillConstructed)
        {
            throw new InvalidOperationException($"The {role} Batman cowl SCS node is still referenced by the construction script.");
        }
    }

    private static string? FindContentRoot(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(assetPath)!);
        while (directory is not null)
        {
            if (directory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindMappingsPath(string projectRoot)
    {
        var mappingsRoot = Path.Combine(projectRoot, "Data", "Mappings");
        return Directory.Exists(mappingsRoot)
            ? Directory.EnumerateFiles(mappingsRoot, "*.usmap")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }
}
