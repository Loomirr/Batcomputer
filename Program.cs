using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal static class Program
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // Load user path settings (next to the .exe) for both CLI and GUI modes.
        // Empty/invalid fields fall back to built-in defaults, so this is safe even
        // with no settings file present.
        AppSettings.Current = AppSettings.Load();

        if (args.Length >= 4 && args[0].Equals("--preview-probe", StringComparison.OrdinalIgnoreCase))
        {
            return ModelPreviewProbe.Run(args[1], args[2], args[3]);
        }

        if (args.Length >= 5 && args[0].Equals("--preview-suit", StringComparison.OrdinalIgnoreCase))
        {
            // --preview-suit <paksDir> <usmap> <suitProjectJson> <projectRoot>
            var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(args[3]), JsonOptions)
                ?? throw new InvalidOperationException("Could not read the suit project.");
            Console.WriteLine(ModelPreviewService.BuildPreviewSuit(
                args[1],
                args[2],
                project,
                args[4],
                redBrickTints: ViewerBaseGameRedBrickPaletteService.LoadPreviewTints()));
            return 0;
        }

        if (args.Length >= 5 && args[0].Equals("--static-mesh-donor-report", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-donor-report <extractedContent> <paksDir> <usmap> <outputDir>
            var report = new StaticMeshDonorService().CreateReport(args[1], args[2], args[3], args[4]);
            var reportPath = Path.Combine(args[4], "static-mesh-donor-report.json");
            Console.WriteLine($"report={reportPath}");
            foreach (var donor in report.Donors)
            {
                Console.WriteLine($"{donor.Id}: {donor.Status} lod0={donor.Lod0Vertices} verts / {donor.Lod0Sections} sections, " +
                                  $"uasset-roundtrip={donor.UassetRoundTripByteEqual?.ToString() ?? "unavailable"}");
                if (!string.IsNullOrWhiteSpace(donor.Error)) Console.WriteLine("  " + donor.Error);
            }
            return report.Donors.All(donor => donor.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
        }

        if (args.Length >= 5 && args[0].Equals("--static-mesh-cube-morph-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-cube-morph-probe <extractedContent> <usmap> <outputContent> <outputPackage>
            var result = new StaticMeshMorphService().CreateCubeMorphProbe(new StaticMeshMorphService.Request
            {
                ExtractedContentRoot = args[1],
                UsmapPath = args[2],
                OutputContentRoot = args[3],
                OutputPackagePath = args[4]
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"uasset={result.OutputUasset}");
            Console.WriteLine($"uexp={result.OutputUexp}");
            Console.WriteLine($"ubulk={result.OutputUbulk}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-bounds-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-bounds-probe <extractedContent> <usmap> <meshPackage>
            var bounds = new StaticMeshMorphService().ReadExtendedBounds(args[1], args[2], args[3]);
            Console.WriteLine($"package={bounds.PackagePath}");
            Console.WriteLine($"origin={bounds.OriginX:F4},{bounds.OriginY:F4},{bounds.OriginZ:F4}");
            Console.WriteLine($"extent={bounds.ExtentX:F4},{bounds.ExtentY:F4},{bounds.ExtentZ:F4}");
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-cube-attachment-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-cube-attachment-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3]
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-large-cube-no-cowl-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-large-cube-no-cowl-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-clean-material-cube-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-clean-material-cube-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true,
                UseNativeHairMaterial = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-cowl-material-cube-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-cowl-material-cube-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-origin-cube-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-origin-cube-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true,
                CenterCubeAtAttachmentOrigin = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-render-data-cube-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-render-data-cube-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true,
                CenterCubeAtAttachmentOrigin = true,
                RewriteCubeRenderData = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-side-shell-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-side-shell-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 4f,
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true,
                CenterCubeAtAttachmentOrigin = true,
                RewriteCubeRenderData = true,
                UseFourSidedHardEdgeShell = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--static-mesh-closed-cube-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-closed-cube-probe <projectRoot> <sourceSuitProject> <newModId>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                CubeScale = 2.5f,
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true,
                CenterCubeAtAttachmentOrigin = true,
                UseLargeClosedCube = true
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 5 && args[0].Equals("--static-mesh-obj-head-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --static-mesh-obj-head-probe <projectRoot> <sourceSuitProject> <newModId> <objPath>
            var result = new StaticMeshAttachmentProbeService().CreateCubeAttachmentProbe(new StaticMeshAttachmentProbeService.Request
            {
                ProjectRoot = args[1],
                SourceProjectPath = args[2],
                ModId = args[3],
                RemoveCowl = true,
                UseOpaqueCowlMaterial = true,
                CenterCubeAtAttachmentOrigin = true,
                ObjPath = args[4],
                ObjScale = 150f
            });
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"suitProject={result.SuitProjectPath}");
            Console.WriteLine($"modProject={result.ModProjectPath}");
            Console.WriteLine($"contentRoot={result.ContentRoot}");
            Console.WriteLine($"mesh={result.MeshPackagePath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var line in result.Log) Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.WriteLine(result.Error);
            return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 3 && args[0].Equals("--verify-static-mesh-cube-attachment-probe", StringComparison.OrdinalIgnoreCase))
        {
            // --verify-static-mesh-cube-attachment-probe <projectRoot> <modId>
            var result = new StaticMeshAttachmentProbeService().VerifyCubeAttachmentProbe(args[1], args[2]);
            Console.WriteLine("status=" + result.Status);
            Console.WriteLine("suitProject=" + result.SuitProjectPath);
            Console.WriteLine("contentRoot=" + result.ContentRoot);
            Console.WriteLine("mesh=" + result.MeshPackagePath);
            foreach (var line in result.Log)
            {
                Console.WriteLine("log=" + line);
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Console.Error.WriteLine("error=" + result.Error);
            }
            return result.Status.Equals("verified", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (args.Length >= 1 && args[0].Equals("--preview-window", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ModelPreviewForm(ModelPreviewForm.WebGlSmokeTestHtml(), "Preview — WebGL smoke test"));
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--preview-mesh", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            var folder = ModelPreviewService.BuildPreview(args[1], args[2], args[3]);
            // Folder name in the title: the viewer is served from a per-build guid directory, so this
            // is the only way to tell from a screenshot which build a window is actually showing.
            Application.Run(ModelPreviewForm.ForFolder(folder,
                "Preview — " + args[3].Split('/')[^1] + "  [" + Path.GetFileName(folder) + "]"));
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--preview-character", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            var bodyMesh = args.Length >= 5 ? args[4] : null;
            var folder = ModelPreviewService.BuildPreviewCharacter(args[1], args[2], args[3], bodyMesh);
            Application.Run(ModelPreviewForm.ForFolder(folder, "Preview — " + args[3].Split('/')[^1]));
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--preview-character-folder", StringComparison.OrdinalIgnoreCase))
        {
            var bodyMesh = args.Length >= 5 ? args[4] : null;
            var folder = ModelPreviewService.BuildPreviewCharacter(args[1], args[2], args[3], bodyMesh);
            Console.WriteLine(folder);
            return 0;
        }

        if (args.Length >= 2 && args[0].Equals("--create-recommended-project", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = args[1];
            var indexService = new TemplateIndexService(projectRoot);
            var plan = indexService.LoadRecommendedDonorPlan();
            if (plan is null)
            {
                Console.Error.WriteLine("Recommended donor plan not found. Run the template indexer first.");
                return 2;
            }

            var project = PatchPlanService.CreateProjectFromRecommendedPlan(plan);
            var projectService = new SuitProjectService(projectRoot);
            var path = projectService.SaveProject(project);
            Console.WriteLine($"project={path}");
            Console.WriteLine($"playable={project.PlayableTemplate?.PackagePath}");
            Console.WriteLine($"cutscene={project.CutsceneTemplate?.PackagePath}");
            Console.WriteLine($"dcmd={project.DcmdTemplate?.PackagePath}");
            return 0;
        }

        if (args.Length >= 3 && args[0].Equals("--patch-name-maps", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = args[1];
            var projectJson = args[2];
            var project = System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(projectJson), JsonOptions);
            if (project is null)
            {
                Console.Error.WriteLine("Failed to read native suit project JSON.");
                return 2;
            }

            var result = new UAssetPatchService(projectRoot).CreateNameMapPatchedStage(project);
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"patchedContentRoot={result.PatchedContentRoot}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var package in result.PackageResults)
            {
                Console.WriteLine($"{package.Role}: success={package.Success} loaded={package.Loaded} written={package.Written} changes={package.NameMapReplacements.Count}");
            }

            return result.PackageResults.All(x => x.Success) ? 0 : 1;
        }

        if (args.Length >= 2 && args[0].Equals("--graft-thomas-torso2", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = args[1];
            NativeSuitProject? project = null;
            if (args.Length >= 3 && File.Exists(args[2]))
            {
                project = System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(args[2]), JsonOptions);
            }
            else
            {
                var plan = new TemplateIndexService(projectRoot).LoadRecommendedDonorPlan();
                if (plan is not null)
                {
                    project = PatchPlanService.CreateProjectFromRecommendedPlan(plan);
                }
            }

            if (project is null)
            {
                Console.Error.WriteLine("Failed to load/create a native suit project. Run the template indexer first or pass a project JSON.");
                return 2;
            }

            var result = new PartGraftService(projectRoot).CreateTorso2GraftedStage(project);
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"patchedContentRoot={result.PatchedContentRoot}");
            Console.WriteLine($"graftedContentRoot={result.GraftedContentRoot}");
            Console.WriteLine($"partIndex={result.PartIndexPath}");
            Console.WriteLine($"report={result.ReportPath}");
            foreach (var package in result.PackageResults)
            {
                Console.WriteLine($"{package.Role}: success={package.Success} alreadyHadTorso2={package.AlreadyHadTorso2} addedImports={package.AddedImports} addedExports={package.AddedExports} componentExport={package.NewComponentExportIndex} scsNodeExport={package.NewScsNodeExportIndex}");
                if (!package.Success && !string.IsNullOrWhiteSpace(package.Error))
                {
                    Console.WriteLine(package.Error);
                }
            }

            return result.PackageResults.Any(x =>
                x.Role.Equals("playable", StringComparison.OrdinalIgnoreCase) && x.Success)
                ? 0
                : 1;
        }

        if (args.Length >= 2 && args[0].Equals("--build-part-index", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = args[1];
            var sourceContentRoot = args.Length >= 3 ? args[2] : null;
            var service = new PartIndexService(projectRoot);
            var index = service.BuildPartIndex(sourceContentRoot);
            Console.WriteLine($"status={index.Status}");
            Console.WriteLine($"sourceContentRoot={index.SourceContentRoot}");
            Console.WriteLine($"mappings={index.MappingsPath ?? "<none>"}");
            Console.WriteLine($"assetsFound={index.AssetsFound}");
            Console.WriteLine($"assetsParsed={index.AssetsParsed}");
            Console.WriteLine($"assetsWithParts={index.AssetsWithParts}");
            Console.WriteLine($"parts={index.Parts.Count}");
            Console.WriteLine($"errors={index.Errors.Count}");
            Console.WriteLine($"partIndex={service.PartIndexPath}");
            foreach (var group in index.Parts.GroupBy(x => x.Slot).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Take(12))
            {
                Console.WriteLine($"slot[{group.Key}]={group.Count()}");
            }

            return index.Status.Equals("missing-source-root", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        if (args.Length >= 3 && args[0].Equals("--probe-uasset-slots", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeUassetSlots(args[1], args[2]);
        }

        if (args.Length >= 4 && args[0].Equals("--probe-schema-pull", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeSchemaPull(args[1], args[2], args[3]);
        }

        if (args.Length >= 3 && args[0].Equals("--probe-uasset-exports", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeUassetExports(args[1], args[2]);
        }

        if (args.Length >= 5 && args[0].Equals("--gen-dcmd", StringComparison.OrdinalIgnoreCase))
        {
            return GenAndVerifyDcmd(args[1], args[2], args[3], args[4]);
        }

        if (args.Length >= 2 && args[0].Equals("--probe-refs", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeRefs(args[1]);
        }

        if (args.Length >= 2 && args[0].Equals("--probe-props", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeProps(args[1]);
        }

        if (args.Length >= 2 && args[0].Equals("--probe-material", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeMaterial(args[1]);
        }


        if (args.Length >= 4 && args[0].Equals("--repath-namemap", StringComparison.OrdinalIgnoreCase))
        {
            // --repath-namemap <asset.uasset> <fromSubstring> <toSubstring>
            // Rewrites every name-map entry containing <from> (e.g. "/TtDebugMenu/") to use
            // <to> (e.g. "/Game/DebugMenuMod/"). Used to move cooked plugin content onto a
            // mounted content root by fixing its internal package references.
            return RepathNameMap(args[1], args[2], args[3]);
        }

        if (args.Length >= 2 && args[0].Equals("--detect-glide-visual", StringComparison.OrdinalIgnoreCase))
        {
            // --detect-glide-visual <basePlayable.uasset>
            var proj = new NativeSuitProject { PlayableTemplate = new TemplateRecord { Uasset = args[1] } };
            var comp = new AnimArchetypeGraftService().BaseGlideVisualComponent(proj);
            Console.WriteLine($"glide-visual component: {(comp ?? "<none>")}");
            return 0;
        }

        if (args.Length >= 5 && args[0].Equals("--fix-cape-attach", StringComparison.OrdinalIgnoreCase))
        {
            // --fix-cape-attach <projectRoot> <slotId> <playablePkg> <cutscenePkg>
            // Sets every "Cape*" SCS node's AttachToName to "Root" in the stage.
            var n = new ComponentRemoveService(args[1]).SetScsNodeAttachSocketForPrefix(args[2], args[3], args[4], "Cape", "Root");
            Console.WriteLine($"cape nodes set to Root: {n}");
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--apply-packaged", StringComparison.OrdinalIgnoreCase))
        {
            // --apply-packaged <projectRoot> <projectJson> <contentRoot>
            var proj = new SuitProjectService(args[1]).LoadProject(args[2]);
            if (proj is null)
            {
                Console.Error.WriteLine($"Could not read suit project: {args[2]}");
                return 2;
            }
            if (args.Length >= 5 && args[4] == "loco")
            {
                var usmap = AppSettings.Current.EffectiveUsmapPath();
                Usmap? m = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
                var d = AnimArchetypeGraftService.DetectDonorForProject(proj, args[3], m);
                if (d is null)
                {
                    Console.Error.WriteLine("Could not detect an animation donor for the suit project.");
                    return 2;
                }
                proj.AnimationOverrides.Clear();
                proj.LocomotionOverrides.Clear();
                proj.LocomotionOverrides.Add(new AnimSequenceOverride
                {
                    DonorSequence = $"A_Idle_{d.Family}",
                    DonorSequencePackage = $"/Game/Animation/LEGOfig/{d.Family}/Movement/A_Idle_{d.Family}",
                    ReplacementSequence = "A_Idle_Catwoman",
                    ReplacementPackage = "/Game/Animation/LEGOfig/Catwoman/Movement/A_Idle_Catwoman",
                });
                proj.LocomotionOverrides.Add(new AnimSequenceOverride
                {
                    DonorSequence = $"A_Walk_{d.Family}",
                    DonorSequencePackage = $"/Game/Animation/LEGOfig/{d.Family}/Movement/A_Walk_{d.Family}",
                    ReplacementSequence = "A_Idle_Catwoman",
                    ReplacementPackage = "/Game/Animation/LEGOfig/Catwoman/Movement/A_Idle_Catwoman",
                });
            }
            Console.WriteLine($"slot={proj.SlotId} archetype={proj.UseCustomArchetype} animOverrides={proj.AnimationOverrides.Count} locoOverrides={proj.LocomotionOverrides.Count} gadgets={proj.EquipmentSlots.Count}");
            var r = new AnimArchetypeGraftService().ApplyToPackagedRoot(proj, args[3]);
            Console.WriteLine($"status={r.Status}");
            foreach (var l in r.Log) Console.WriteLine("  " + l);
            if (r.Error is not null) Console.WriteLine("ERR: " + r.Error.Split('\n')[0]);
            return 0;
        }

        if (args.Length >= 4 && args[0].Equals("--graft-anim", StringComparison.OrdinalIgnoreCase))
        {
            // --graft-anim <charSet.uasset> <TTLayerSet|TTAnimSet> <parentSetPackage>
            var r = new AnimGraftService().InjectParentSets(args[1], args[2], new[] { args[3] });
            Console.WriteLine($"status={r.Status} added=[{string.Join(",", r.Added)}] skipped=[{string.Join(",", r.Skipped)}]");
            if (r.Error is not null) Console.WriteLine(r.Error);
            return r.Status == "ok" ? 0 : 1;
        }

        if (args.Length >= 5 && args[0].Equals("--replace-equip", StringComparison.OrdinalIgnoreCase))
        {
            // --replace-equip <dcmd.uasset> <slot0based> <gadgetName> <etaPackage> [upgradePackage]
            var svc = new DcmdGenService(".");
            var refs = new List<DcmdGenService.EquipmentSlotRef>
            {
                new(int.Parse(args[2]), args[3], args[4], args.Length >= 6 ? args[5] : null),
            };
            var r = svc.ReplaceEquipment(args[1], refs);
            Console.WriteLine($"status={r.Status} applied=[{string.Join(",", r.Applied)}]");
            if (r.Error is not null) Console.WriteLine(r.Error);
            return r.Status == "ok" ? 0 : 1;
        }

        if (args.Length >= 3 && args[0].Equals("--build-gamedata", StringComparison.OrdinalIgnoreCase))
        {
            // --build-gamedata <extractedContentRoot> <outputJson> [gameBuild] [--full]
            var full = args.Any(a => a.Equals("--full", StringComparison.OrdinalIgnoreCase));
            var buildArg = args.Skip(3).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "unknown";
            return BuildGameData(args[1], args[2], buildArg, full);
        }

        if (args.Length >= 2 && args[0].Equals("--part-confidence", StringComparison.OrdinalIgnoreCase))
        {
            // --part-confidence <projectRoot>  - recipe-confidence distribution over the part index.
            var index = new PartIndexService(args[1]).LoadPartIndex();
            if (index is null)
            {
                Console.WriteLine("no part index found — build it first.");
                return 1;
            }
            var graded = index.Parts.Where(p => p.HasMesh)
                .Select(p => (Part: p, Result: PartRecipeService.Confidence(p)))
                .ToList();
            Console.WriteLine($"parts with mesh: {graded.Count}");
            foreach (var g in graded.GroupBy(g => g.Result.Level).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  {g.Key,-8} {g.Count(),5}  ({100.0 * g.Count() / graded.Count:0.#}%)");
                foreach (var reason in g.GroupBy(x => x.Result.Reason).OrderByDescending(r => r.Count()).Take(3))
                {
                    Console.WriteLine($"      {reason.Count(),4}x {reason.Key}");
                }
            }
            return 0;
        }

        if (args.Length >= 3 && args[0].Equals("--validate-stage", StringComparison.OrdinalIgnoreCase))
        {
            // --validate-stage <projectJson> <contentRoot> [usmapPath]
            var proj = System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(args[1]),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (proj is null) { Console.WriteLine("could not load project"); return 1; }
            var usmap = args.Length >= 4 ? args[3] : AppSettings.Current.EffectiveUsmapPath();
            var findings = new StageValidationService(args[2], usmap).Validate(proj);
            foreach (var f in findings) Console.WriteLine($"[{f.Severity}] {f.Message}");
            var errs = findings.Count(f => f.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"validation: {errs} error(s), {findings.Count - errs} warning(s)");
            return errs == 0 ? 0 : 1;
        }

        if (args.Length >= 4 && args[0].Equals("--cook-texture", StringComparison.OrdinalIgnoreCase))
        {
            return CookTextureCli(args);
        }

        if (args.Length >= 2 && args[0].Equals("--anim-lib", StringComparison.OrdinalIgnoreCase))
        {
            return AnimLibraryCli(args);
        }

        ApplicationConfiguration.Initialize();
        ConfigureGuiCrashReporting();

        // Keep the bundled typeface as the default for controls without an explicit font.
        try { Application.SetDefaultFont(AppFonts.Condensed(10f, FontStyle.Bold)); } catch { /* pre-window only */ }
        ThemedMenuRenderer.Apply(); // dark context menus app-wide
        Theme.ApplyDarkTitleBarsAppWide();
        Animator.Enabled = AppSettings.Current.AnimationsEnabled;

        var portableIssues = AppSettings.PortableLayoutIssues();
        if (portableIssues.Count > 0)
        {
            Dialog.Error(null, "Portable install is incomplete",
                "These files are missing beside Batcomputer.exe:\n\n  " + string.Join("\n  ", portableIssues) +
                "\n\nExtract a complete Batcomputer release zip, then launch it again.");
            return 1;
        }

        // First-time setup asks only for machine-specific game data. Batcomputer's
        // own retoc helper and registry-writer project are bundled and discovered
        // automatically from the portable install.
        var initialExtractionRequested = false;
        var registryWriterPreparationRequested = false;
        if (!AppSettings.Current.IsUsable())
        {
            using var setup = new FirstRunWizard(AppSettings.Current);
            setup.ShowDialog();
            initialExtractionRequested = setup.InitialExtractionRequested;
            registryWriterPreparationRequested = setup.RegistryWriterPreparationRequested;
            // Reload whatever was saved (or keep the in-memory defaults if cancelled).
            AppSettings.Current = AppSettings.Load();
            if (!AppSettings.Current.IsUsable() && !initialExtractionRequested)
            {
                Dialog.Warn(null, "Setup incomplete", "Batcomputer can open, but setup is incomplete.\n\n" +
                    "Before building indexes or packaging, open Setup and select your current .usmap mappings file and the game's Content\\Paks folder.");
            }
        }

        // The tool writes Generated\ beside itself. If that location is protected (Program Files),
        // say so now rather than failing at the first package. No silent relocation.
        if (AppSettings.Current.DescribeRootWritability() is { } writeProblem)
        {
            Dialog.Error(null, "Can't write to the tool folder", writeProblem);
            return 1;
        }

        var mainForm = new MainForm();
        if (initialExtractionRequested || registryWriterPreparationRequested)
        {
            mainForm.Shown += async (_, _) => await mainForm.RunInitialSetupTasksAsync(
                registryWriterPreparationRequested,
                initialExtractionRequested);
        }
        Application.Run(mainForm);
        return 0;
    }

    private static int _handlingGuiCrash;

    private static void ConfigureGuiCrashReporting()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            if (Interlocked.Exchange(ref _handlingGuiCrash, 1) != 0)
            {
                return;
            }

            var report = WriteCrashReport(eventArgs.Exception, "Windows Forms UI thread");
            try
            {
                MessageBox.Show(
                    "Batcomputer encountered an unexpected error and should be restarted.\n\n" +
                    "A diagnostic report was saved here:\n" + report,
                    "Batcomputer - Unexpected error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* the UI itself is already failing */ }
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                WriteCrashReport(exception, "Unhandled application thread");
            }
        };
    }

    private static string WriteCrashReport(Exception exception, string source)
    {
        var fileName = $"Batcomputer-crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log";
        var candidates = new[]
        {
            Path.Combine(AppSettings.DataRoot, "Logs"),
            Path.Combine(Path.GetTempPath(), "Batcomputer", "Logs"),
        };
        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.WriteAllText(path,
                    $"Batcomputer {typeof(Program).Assembly.GetName().Version}\n" +
                    $"UTC: {DateTime.UtcNow:O}\n" +
                    $"Source: {source}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $".NET: {Environment.Version}\n\n" +
                    exception);
                return path;
            }
            catch { /* try the next writable location */ }
        }

        return "The diagnostic report could not be written.";
    }

    /// <summary>
    /// Cooking spike.
    ///
    /// Usage:
    ///   --cook-texture &lt;source.png&gt; &lt;templateTexture.json&gt; &lt;outputContentRoot&gt; [outputPackagePath] [--linear] [--write-inline] [--bc7-input=rgba|bgra|argb|bgra-as-rgba] [--bc7-quality=fast|balanced|best] [--force-pixel-format=PF_DXT5]
    ///
    /// If outputPackagePath is omitted, the cooked texture keeps the donor template's
    /// original /Game package path. Standalone UAssetAPI-readable texture templates can
    /// be repathed to normal variable-length /Game paths. IoStore-payload templates fall
    /// back to the older same-length binary patch path.
    /// </summary>
    private static int CookTextureCli(string[] args)
    {
        var outputPackagePath = args.Skip(4).FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? "";
        var nearestNeighbor = !args.Any(x => x.Equals("--linear", StringComparison.OrdinalIgnoreCase));
        var writeInline = args.Any(x => x.Equals("--write-inline", StringComparison.OrdinalIgnoreCase));
        var bc7InputLayout = args
            .FirstOrDefault(x => x.StartsWith("--bc7-input=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--bc7-input=".Length) ?? "rgba";
        var bc7Quality = args
            .FirstOrDefault(x => x.StartsWith("--bc7-quality=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--bc7-quality=".Length) ?? "balanced";
        var forcePixelFormat = args
            .FirstOrDefault(x => x.StartsWith("--force-pixel-format=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--force-pixel-format=".Length) ?? "";

        var result = new TextureCookService(AppSettings.Current.EffectiveProjectRoot()).Cook(new TextureCookService.Request
        {
            SourceImagePath = args[1],
            TemplateJsonPath = args[2],
            OutputContentRoot = args[3],
            OutputPackagePath = outputPackagePath,
            NearestNeighborMips = nearestNeighbor,
            WriteInlineMips = writeInline,
            Bc7InputLayout = bc7InputLayout,
            Bc7Quality = bc7Quality,
            ForcePixelFormat = forcePixelFormat,
        });

        Console.WriteLine($"status={result.Status}");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.Error.WriteLine(result.Error);
        }

        Console.WriteLine($"templatePackage={result.TemplatePackagePath}");
        Console.WriteLine($"outputPackage={result.OutputPackagePath}");
        Console.WriteLine($"outputUasset={result.OutputUasset}");
        if (!string.IsNullOrWhiteSpace(result.OutputUexp))
        {
            Console.WriteLine($"outputUexp={result.OutputUexp}");
        }
        Console.WriteLine($"outputUbulk={result.OutputUbulk}");
        Console.WriteLine($"size={result.Width}x{result.Height}");
        Console.WriteLine($"pixelFormat={result.PixelFormat}");
        Console.WriteLine($"mips={result.MipCount} external={result.ExternalMipCount} inline={result.InlineMipCount}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning={warning}");
        }

        foreach (var line in result.Log)
        {
            Console.WriteLine(line);
        }

        return result.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static int BuildGameData(string extractedContentRoot, string outputJson, string gameBuild, bool includeFullCatalog = false)
    {
        var miner = new GameDataMiner(extractedContentRoot);
        var result = miner.Mine(gameBuild, includeFullCatalog);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson))!);
        File.WriteAllText(outputJson, JsonSerializer.Serialize(result.Db, options));

        Console.WriteLine($"gameBuild={result.Db.GameBuild}");
        Console.WriteLine($"families={result.Db.Families.Count}");
        Console.WriteLine($"equipment={result.Db.Equipment.Count}");
        Console.WriteLine($"equipmentLayerSets={result.Db.EquipmentLayerSets.Count}");
        Console.WriteLine($"animSets={result.Db.AnimSets.Count} (composites={result.Db.AnimSets.Count(a => a.IsCharacterComposite)})");
        Console.WriteLine($"assets={result.Db.Assets.Count}");
        if (result.Db.Assets.Count > 0)
        {
            var byClass = result.Db.Assets
                .GroupBy(a => string.IsNullOrEmpty(a.Class) ? "(unknown)" : a.Class)
                .OrderByDescending(g => g.Count())
                .Take(12);
            foreach (var g in byClass)
            {
                Console.WriteLine($"  class {g.Key}: {g.Count()}");
            }
        }
        Console.WriteLine($"assetsScanned={result.AssetsScanned} errors={result.Errors}");
        Console.WriteLine($"output={Path.GetFullPath(outputJson)}");
        foreach (var w in result.Warnings.Take(10))
        {
            Console.WriteLine($"  warn: {w}");
        }

        // Quick sanity spot-checks for the reader.
        var batman = result.Db.Families.FirstOrDefault(f => f.Name.Equals("Batman", StringComparison.OrdinalIgnoreCase));
        if (batman is not null)
        {
            Console.WriteLine($"Batman: MAS={batman.MontageAnimSet} LAS={batman.LayerAnimSet} nativeEquip=[{string.Join(", ", batman.NativeEquipment)}]");
        }

        return result.Db.Families.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Library CLI (testable surface until the redesigned UI lands):
    ///   --anim-lib list &lt;projectRoot&gt;
    ///   --anim-lib register &lt;projectRoot&gt; &lt;name&gt; &lt;/Game/package/path&gt; [sourceMode] [category]
    ///   --anim-lib import   &lt;projectRoot&gt; &lt;name&gt; &lt;cooked.uasset&gt; &lt;/Game/package/path&gt; [sourceMode] [category]
    ///   --anim-lib remove   &lt;projectRoot&gt; &lt;id&gt;
    /// </summary>
    private static int AnimLibraryCli(string[] args)
    {
        var sub = args[1].ToLowerInvariant();
        var projectRoot = args.Length >= 3 ? args[2] : AppSettings.Current.EffectiveProjectRoot();
        var svc = new AnimLibraryService(projectRoot, AppSettings.Current.EffectiveUsmapPath());
        var lib = svc.Load();

        void Print(AnimLibraryEntry e)
        {
            Console.WriteLine($"  [{e.Id[..8]}] v{e.Version} \"{e.Name}\" ({e.SourceMode}) {e.PackagePath}");
            Console.WriteLine($"      class={(string.IsNullOrEmpty(e.AssetClass) ? "?" : e.AssetClass)} skeleton={(string.IsNullOrEmpty(e.Skeleton) ? "?" : e.Skeleton)} rootMotion={e.RootMotion} additive={(string.IsNullOrEmpty(e.AdditiveMode) ? "?" : e.AdditiveMode)} inspected={e.Inspected} deps={e.Dependencies.Count}");
            if (!string.IsNullOrWhiteSpace(e.Notes)) Console.WriteLine($"      note: {e.Notes}");
        }

        switch (sub)
        {
            case "list":
                Console.WriteLine($"anim library: {lib.Entries.Count} entr(y/ies) @ {svc.IndexPath}");
                foreach (var e in lib.Entries) Print(e);
                return 0;

            case "register":
                if (args.Length < 5) { Console.WriteLine("usage: --anim-lib register <projectRoot> <name> </Game/path> [sourceMode] [category]"); return 1; }
                {
                    var e = svc.RegisterByPackagePath(lib, args[3], args[4],
                        args.Length >= 6 ? args[5] : "external",
                        args.Length >= 7 ? args[6] : "");
                    Console.WriteLine("registered:");
                    Print(e);
                }
                return 0;

            case "import":
                if (args.Length < 6) { Console.WriteLine("usage: --anim-lib import <projectRoot> <name> <cooked.uasset> </Game/path> [sourceMode] [category]"); return 1; }
                {
                    var e = svc.ImportCookedFile(lib, args[3], args[4], args[5],
                        args.Length >= 7 ? args[6] : "preserve-path",
                        args.Length >= 8 ? args[7] : "");
                    Console.WriteLine("imported:");
                    Print(e);
                }
                return 0;

            case "stage":
                // --anim-lib stage <projectRoot> <contentRoot> <comma-separated /Game refs>
                if (args.Length < 5) { Console.WriteLine("usage: --anim-lib stage <projectRoot> <contentRoot> </Game/ref1,/Game/ref2>"); return 1; }
                {
                    var refs = args[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var shippable = svc.ReferencedShippable(lib, refs);
                    Console.WriteLine($"referenced shippable entries: {shippable.Count}");
                    var total = 0;
                    foreach (var e in shippable)
                    {
                        var n = svc.StageInto(e, args[3]);
                        total += n;
                        Console.WriteLine($"  staged {n} file(s) for \"{e.Name}\" ({e.SourceMode}) → {e.PackagePath}");
                    }
                    Console.WriteLine($"total files staged: {total}");
                }
                return 0;

            case "remove":
                if (args.Length < 4) { Console.WriteLine("usage: --anim-lib remove <projectRoot> <id-or-prefix>"); return 1; }
                {
                    var matches = lib.Entries
                        .Where(e => e.Id.StartsWith(args[3], StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count == 0) { Console.WriteLine("no entry with that id."); return 1; }
                    if (matches.Count > 1) { Console.WriteLine($"ambiguous prefix — {matches.Count} entries match. Use a longer id."); return 1; }
                    Console.WriteLine(svc.Remove(lib, matches[0].Id) ? "removed." : "no entry with that id.");
                }
                return 0;

            default:
                Console.WriteLine("anim-lib subcommands: list | register | import | remove");
                return 1;
        }
    }

    private static int ProbeProps(string assetPath)
    {
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        Console.WriteLine($"usmap={usmap} exists={(usmap != null && File.Exists(usmap))}");
        Usmap? mappings = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
        try { Console.WriteLine($"binaryEqualityRoundTrip={asset.VerifyBinaryEquality()}"); }
        catch (Exception ex) { Console.WriteLine($"binaryEquality error: {ex.Message}"); }
        foreach (var exp in asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>())
        {
            string Names(List<FPackageIndex>? deps) => deps is null ? "-" : string.Join(",", deps.Select(d =>
                d.IsImport() ? d.ToImport(asset).ObjectName.ToString() : d.IsExport() ? "exp" + d.Index : "0"));
            Console.WriteLine($"DEPS {exp.ObjectName}: createBeforeCreate=[{Names(exp.CreateBeforeCreateDependencies)}] serBeforeCreate=[{Names(exp.SerializationBeforeCreateDependencies)}] createBeforeSer=[{Names(exp.CreateBeforeSerializationDependencies)}]");
        }
        foreach (var export in asset.Exports)
        {
            if (export is not UAssetAPI.ExportTypes.NormalExport ne) continue;
            Console.WriteLine($"=== export {export.ObjectName} ({export.GetExportClassType()?.Value.Value}) props={ne.Data.Count} ===");
            foreach (var prop in ne.Data)
            {
                var extra = prop switch
                {
                    UAssetAPI.PropertyTypes.Objects.ArrayPropertyData ap => $" [{ap.Value.Length} x {ap.ArrayType?.Value}]",
                    _ => " = " + prop.RawValue?.ToString(),
                };
                Console.WriteLine($"  {prop.Name} : {prop.PropertyType}{extra}");
                if (prop.Name.ToString() == "ParentSetsArray" && prop is UAssetAPI.PropertyTypes.Objects.ArrayPropertyData pa)
                {
                    for (var k = 0; k < pa.Value.Length; k++)
                    {
                        var el = pa.Value[k];
                        var kind = el is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData op
                            ? (op.Value.IsNull() ? "NULL" : op.Value.IsImport() ? "import " + op.Value.ToImport(asset).ObjectName : "export " + op.Value.Index)
                            : "?";
                        Console.WriteLine($"      [{k}] {kind}");
                    }
                }
                if (prop.Name.ToString() != "ParentSetsArray" && prop is UAssetAPI.PropertyTypes.Objects.ArrayPropertyData arr && arr.Value.Length > 0)
                {
                    foreach (var el in arr.Value.Take(4))
                    {
                        Console.WriteLine($"      - {el.PropertyType} raw={el.RawValue}");
                    }
                    if (prop.Name.ToString() == "EquipmentList")
                    {
                        var el0 = arr.Value[0];
                        Console.WriteLine($"    CLR type: {el0.GetType().FullName}");
                        var valProp = el0.GetType().GetProperty("Value");
                        var val = valProp?.GetValue(el0);
                        Console.WriteLine($"    Value CLR: {val?.GetType().FullName}");
                        if (val is not null)
                        {
                            foreach (var f in val.GetType().GetFields())
                            {
                                var fv = f.GetValue(val);
                                Console.WriteLine($"      field {f.FieldType.Name} {f.Name} = {fv}");
                                if (fv is not null && f.FieldType.Name == "FTopLevelAssetPath")
                                {
                                    foreach (var nf in fv.GetType().GetFields())
                                        Console.WriteLine($"          nested {nf.FieldType.Name} {nf.Name} = {nf.GetValue(fv)}");
                                }
                            }
                            foreach (var p2 in val.GetType().GetProperties())
                                Console.WriteLine($"      prop  {p2.PropertyType.Name} {p2.Name} = {p2.GetValue(val)}");
                        }
                    }

                    // Game Progress override assets have a compact, normal
                    // reflected struct array. Dump it recursively so new
                    // authoring code can mirror a real donor exactly instead
                    // of guessing at enum/tag serialisation.
                    if (prop.Name.ToString() == "Overrides")
                    {
                        Console.WriteLine("    --- Overrides expanded ---");
                        foreach (var el in arr.Value)
                        {
                            DumpProperty(el, "    ", 0);
                        }
                    }
                }
            }
        }
        return 0;

        static void DumpProperty(PropertyData property, string indent, int depth)
        {
            var raw = property is ArrayPropertyData arrayValue
                ? $"[{arrayValue.Value.Length} x {arrayValue.ArrayType?.Value}]"
                : DescribeRaw(property.RawValue);
            Console.WriteLine($"{indent}{property.Name} : {property.PropertyType} ({property.GetType().Name}) = {raw}");
            if (depth >= 6) return;

            switch (property)
            {
                case StructPropertyData structure:
                    foreach (var child in structure.Value)
                    {
                        DumpProperty(child, indent + "  ", depth + 1);
                    }
                    break;
                case ArrayPropertyData array:
                    foreach (var child in array.Value)
                    {
                        DumpProperty(child, indent + "  ", depth + 1);
                    }
                    break;
            }
        }

        static string DescribeRaw(object? raw)
        {
            if (raw is null) return "<null>";
            if (raw is string text) return text;
            if (raw is System.Collections.IEnumerable values)
            {
                var parts = new List<string>();
                foreach (var value in values)
                {
                    parts.Add(value?.ToString() ?? "<null>");
                    if (parts.Count == 24)
                    {
                        parts.Add("...");
                        break;
                    }
                }
                return "[" + string.Join(", ", parts) + "]";
            }
            return raw.ToString() ?? "<null>";
        }
    }

    private static int ProbeRefs(string assetPath)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        Usmap? mappings = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        Console.WriteLine($"asset={assetPath}");
        Console.WriteLine("--- imports (index: Class  Outer.Object) ---");
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            var outer = "";
            if (imp.OuterIndex.IsImport())
            {
                var oi = -imp.OuterIndex.Index - 1;
                if (oi >= 0 && oi < asset.Imports.Count) outer = asset.Imports[oi].ObjectName.ToString();
            }
            Console.WriteLine($"[{i}] {imp.ClassPackage}.{imp.ClassName}  {(string.IsNullOrEmpty(outer) ? "" : outer + ".")}{imp.ObjectName}");
        }
        return 0;
    }

    /// Dumps a MaterialInstanceConstant's parent + scalar/vector/texture parameter values
    /// AND static-switch parameters. Used to compare a custom suit material against a stock donor.
    private static int ProbeMaterial(string assetPath)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        Usmap? mappings = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
        Console.WriteLine($"asset={assetPath}");

        string ObjName(FPackageIndex idx)
        {
            if (idx is null || idx.IsNull())
            {
                return "(none)";
            }
            if (idx.IsImport())
            {
                var i = -idx.Index - 1;
                return i >= 0 && i < asset.Imports.Count ? asset.Imports[i].ObjectName.ToString() : "(bad import)";
            }
            var e = idx.Index - 1;
            return e >= 0 && e < asset.Exports.Count ? asset.Exports[e].ObjectName.ToString() : "(bad export)";
        }

        static string ParamName(StructPropertyData entry)
        {
            var info = entry.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "ParameterInfo");
            var nameProp = info?.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "Name");
            return nameProp?.Value.ToString()
                   ?? entry.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() is "Name" or "ParameterName")?.Value.ToString()
                   ?? "(unnamed)";
        }

        void DumpArray(List<PropertyData> data, string arrayName, string label)
        {
            var arr = data.OfType<ArrayPropertyData>().FirstOrDefault(a => a.Name.ToString() == arrayName);
            if (arr is null)
            {
                return;
            }
            foreach (var el in arr.Value.OfType<StructPropertyData>())
            {
                var name = ParamName(el);
                var pv = el.Value.FirstOrDefault(p => p.Name.ToString() is "ParameterValue" or "Value");
                var val = pv is ObjectPropertyData o ? ObjName(o.Value) : pv?.RawValue?.ToString() ?? "(none)";
                Console.WriteLine($"  {label} {name} = {val}");
            }
        }

        foreach (var exp in asset.Exports.OfType<NormalExport>())
        {
            var parent = exp.Data.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Parent");
            if (parent is not null)
            {
                Console.WriteLine($"Parent = {ObjName(parent.Value)}");
            }
            DumpArray(exp.Data, "ScalarParameterValues", "SCALAR");
            DumpArray(exp.Data, "VectorParameterValues", "VECTOR");
            DumpArray(exp.Data, "TextureParameterValues", "TEXTURE");

            // Static switches live nested inside StaticParameters / StaticParametersRuntime.
            foreach (var sp in exp.Data.OfType<StructPropertyData>()
                         .Where(p => p.Name.ToString().StartsWith("StaticParameters", StringComparison.OrdinalIgnoreCase)))
            {
                var sw = sp.Value.OfType<ArrayPropertyData>().FirstOrDefault(a => a.Name.ToString() == "StaticSwitchParameters");
                if (sw is null)
                {
                    continue;
                }
                foreach (var el in sw.Value.OfType<StructPropertyData>())
                {
                    var name = ParamName(el);
                    var v = el.Value.OfType<BoolPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Value");
                    Console.WriteLine($"  SWITCH {name} = {(v?.Value.ToString() ?? "?")}");
                }
            }
        }
        return 0;
    }

    private static int GenAndVerifyDcmd(string outputBase, string dcmdPkg, string playablePkg, string cutscenePkg)
    {
        // Generate a sibling UIMD and link the DCMD to it (mirrors packaging).
        var dir = Path.GetDirectoryName(outputBase)!;
        var uimdStem = "DA_UIMD_Batman_Verify";
        var uimdPkg = "/Game/Mods/VerifyMod/UI/" + uimdStem;
        var uimdResult = new UimdGenService(".").Generate(Path.Combine(dir, uimdStem), uimdPkg);
        Console.WriteLine($"uimdStatus={uimdResult.Status} uimdRepointed={uimdResult.Repointed.Count}");
        if (!string.IsNullOrWhiteSpace(uimdResult.Error))
        {
            Console.WriteLine(uimdResult.Error);
        }

        var result = new DcmdGenService(".").Generate(outputBase, dcmdPkg, playablePkg, cutscenePkg, uimdPkg);
        Console.WriteLine($"status={result.Status}");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.WriteLine(result.Error);
            return 1;
        }
        Console.WriteLine($"repointed={result.Repointed.Count}");
        foreach (var r in result.Repointed)
        {
            Console.WriteLine("  " + r);
        }

        // Re-open the written asset and verify it parses + the repoint is complete.
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        Usmap? mappings = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
        var asset = new UAsset(outputBase + ".uasset", EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        var names = asset.GetNameMapIndexList().Select(n => n.ToString()).ToList();

        Console.WriteLine($"reopened=ok folderName={asset.FolderName}");
        var thomasLeftovers = names.Where(n => n.Contains("TheBatman2025", StringComparison.Ordinal)).ToList();
        Console.WriteLine($"thomasLeftovers={thomasLeftovers.Count}");
        foreach (var n in thomasLeftovers)
        {
            Console.WriteLine("  LEFTOVER " + n);
        }
        Console.WriteLine($"hasElectricPlayable={names.Any(n => n.Contains("BP_Batman_Electric_Playable", StringComparison.Ordinal))}");
        Console.WriteLine($"hasElectricCutscene={names.Any(n => n.Contains("BP_Batman_Electric_Cutscene", StringComparison.Ordinal))}");
        Console.WriteLine($"hasElectricDcmdName={names.Any(n => n.Contains("DA_DCMD_Batman_Electric_Playable", StringComparison.Ordinal))}");
        Console.WriteLine($"uimdRepointed={names.Any(n => n.Contains(uimdStem, StringComparison.Ordinal))}");
        Console.WriteLine($"baseUimdLeftover={names.Any(n => n.Equals("/Game/Characters/Minifig/Batman/DA_UIMD_Batman", StringComparison.Ordinal) || n.Equals("DA_UIMD_Batman", StringComparison.Ordinal))}");
        Console.WriteLine($"keptBatarang={names.Any(n => n.Contains("DA_ETA_Batarang", StringComparison.Ordinal))}");
        Console.WriteLine($"keptBatclaw={names.Any(n => n.Contains("DA_ETA_Batclaw", StringComparison.Ordinal))}");
        // The two remaining TheBatman2025 names are the intended PawnTag + ProgressTag.
        return 0;
    }

    private static int ProbeUassetSlots(string projectRoot, string assetPath)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }

        var probeDefault = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "PartGraphProbe", "input", "Dinner.usmap");
        var mappingsPath = File.Exists(probeDefault) ? probeDefault : (AppSettings.Current.EffectiveUsmapPath() ?? probeDefault);
        Usmap? mappings = null;
        if (File.Exists(mappingsPath))
        {
            mappings = MappingsCache.Load(mappingsPath);
        }

        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        using var doc = JsonDocument.Parse(asset.SerializeJson(false));
        if (!doc.RootElement.TryGetProperty("Exports", out var exportsElement))
        {
            Console.Error.WriteLine("No Exports array found.");
            return 1;
        }
        var exports = exportsElement.EnumerateArray().ToList();
        var imports = doc.RootElement.TryGetProperty("Imports", out var importsElement)
            ? importsElement.EnumerateArray().ToList()
            : new List<JsonElement>();

        Console.WriteLine($"asset={assetPath}");
        Console.WriteLine($"mappings={(mappings is null ? "<none>" : mappingsPath)}");

        var slotCount = 0;
        foreach (var export in exports)
        {
            var objectName = GetString(export, "ObjectName");
            if (!objectName.StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!export.TryGetProperty("Data", out var dataElement) ||
                dataElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var slot = GetDataPropertyString(dataElement, "InternalVariableName");
            if (string.IsNullOrWhiteSpace(slot))
            {
                continue;
            }

            var template = GetDataPropertyInt(dataElement, "ComponentTemplate");
            var attachTo = GetDataPropertyString(dataElement, "AttachToName");
            var parent = GetDataPropertyString(dataElement, "ParentComponentOrVariableName");
            var componentDetails = DescribeComponentTemplate(template, imports, exports);
            Console.WriteLine($"slot={slot} node={objectName} template={template} parent={parent} attach={attachTo} {componentDetails}");
            slotCount++;
        }

        Console.WriteLine($"slots={slotCount}");
        return 0;
    }

    private static int ProbeSchemaPull(string projectRoot, string assetPath, string schemaObjectPath)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }

        var probeDefault = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "PartGraphProbe", "input", "Dinner.usmap");
        var mappingsPath = File.Exists(probeDefault) ? probeDefault : (AppSettings.Current.EffectiveUsmapPath() ?? probeDefault);
        if (!File.Exists(mappingsPath))
        {
            Console.Error.WriteLine($"Mappings not found: {mappingsPath}");
            return 2;
        }

        var mappings = MappingsCache.Load(mappingsPath);
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        var schemaName = Path.GetFileName(schemaObjectPath);
        if (schemaName.Contains('.'))
        {
            schemaName = schemaName[(schemaName.LastIndexOf('.') + 1)..];
        }

        Console.WriteLine($"asset={assetPath}");
        Console.WriteLine($"schemaObjectPath={schemaObjectPath}");
        Console.WriteLine($"schemaName={schemaName}");
        Console.WriteLine($"schemaBefore={mappings.Schemas.ContainsKey(schemaName)}");
        try
        {
            var diskPath = asset.FindAssetOnDiskFromPath(schemaObjectPath);
            Console.WriteLine($"findAssetOnDisk={diskPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"findAssetOnDiskError={ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var pulled = asset.PullSchemasFromAnotherAsset(new FName(asset, schemaObjectPath, 0));
            Console.WriteLine($"pullResult={pulled}");
            Console.WriteLine($"schemaAfter={mappings.Schemas.ContainsKey(schemaName)}");
            if (mappings.Schemas.TryGetValue(schemaName, out var schema))
            {
                Console.WriteLine($"schema={schema.Name} super={schema.SuperType} module={schema.ModulePath} propCount={schema.PropCount} fromAsset={schema.FromAsset}");
            }
            return pulled ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"pullError={ex}");
            Console.WriteLine($"schemaAfter={mappings.Schemas.ContainsKey(schemaName)}");
            return 1;
        }
    }

    private static int RepathNameMap(string assetPath, string from, string to)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }
        var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
        Usmap? mappings = (!string.IsNullOrWhiteSpace(mappingsPath) && File.Exists(mappingsPath)) ? MappingsCache.Load(mappingsPath) : null;
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        var names = asset.GetNameMapIndexList();
        var changed = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var s = names[i].ToString();
            if (s.Contains(from, StringComparison.Ordinal))
            {
                var rep = s.Replace(from, to);
                asset.SetNameReference(i, new FString(rep));
                Console.WriteLine($"  [{i}] {s} -> {rep}");
                changed++;
            }
        }
        // The package's OWN name lives in FolderName, NOT the name map - and retoc to-zen
        // derives the packaged package name from it. Repath it too, else the container keeps
        // the old package path (e.g. /TtDebugMenu/...) regardless of the file layout.
        var folder = asset.FolderName?.ToString() ?? "";
        if (folder.Contains(from, StringComparison.Ordinal))
        {
            var newFolder = folder.Replace(from, to);
            asset.FolderName = new FString(newFolder);
            Console.WriteLine($"  FolderName {folder} -> {newFolder}");
            changed++;
        }
        asset.Write(assetPath);
        Console.WriteLine($"repathed {changed} name(s) in {Path.GetFileName(assetPath)}");
        return 0;
    }

    private static int ProbeUassetExports(string projectRoot, string assetPath)
    {
        if (!File.Exists(assetPath))
        {
            Console.Error.WriteLine($"Asset not found: {assetPath}");
            return 2;
        }

        var probeDefault = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "PartGraphProbe", "input", "Dinner.usmap");
        var mappingsPath = File.Exists(probeDefault) ? probeDefault : (AppSettings.Current.EffectiveUsmapPath() ?? probeDefault);
        Usmap? mappings = File.Exists(mappingsPath) ? MappingsCache.Load(mappingsPath) : null;
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        using var doc = JsonDocument.Parse(asset.SerializeJson(false));
        var imports = doc.RootElement.TryGetProperty("Imports", out var importsElement)
            ? importsElement.EnumerateArray().ToList()
            : new List<JsonElement>();
        var exports = doc.RootElement.TryGetProperty("Exports", out var exportsElement)
            ? exportsElement.EnumerateArray().ToList()
            : new List<JsonElement>();

        Console.WriteLine($"asset={assetPath}");
        Console.WriteLine($"exports={exports.Count} imports={imports.Count}");
        for (var i = 0; i < exports.Count; i++)
        {
            var export = exports[i];
            var objectName = GetString(export, "ObjectName");
            var exportType = GetString(export, "$type");
            var classIndex = GetInt(export, "ClassIndex");
            var outerIndex = GetInt(export, "OuterIndex");
            var className = DescribePackageIndex(classIndex, imports, exports);
            var outerName = DescribePackageIndex(outerIndex, imports, exports);
            var dataNames = new List<string>();
            if (export.TryGetProperty("Data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var property in dataElement.EnumerateArray())
                {
                    var name = GetString(property, "Name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        dataNames.Add(name);
                    }
                }
            }

            var rawExport = asset.Exports[i];
            Console.WriteLine($"export[{i + 1}] name={objectName} type={exportType} classIndex={classIndex} class={className} outer={outerName} serial={rawExport.SerialOffset}+{rawExport.SerialSize} data=[{string.Join(", ", dataNames)}]");
        }

        return 0;
    }

    private static string DescribeComponentTemplate(int templateIndex, List<JsonElement> imports, List<JsonElement> exports)
    {
        if (templateIndex <= 0 || templateIndex > exports.Count)
        {
            return "component=<none>";
        }

        var component = exports[templateIndex - 1];
        var componentName = GetString(component, "ObjectName");
        if (!component.TryGetProperty("Data", out var componentData) ||
            componentData.ValueKind != JsonValueKind.Array)
        {
            return $"component={componentName}";
        }

        var skeletalMesh = GetDataPropertyInt(componentData, "SkeletalMesh");
        var staticMesh = GetDataPropertyInt(componentData, "StaticMesh");
        var skinnedAsset = GetDataPropertyInt(componentData, "SkinnedAsset");
        var animClass = GetDataPropertyInt(componentData, "AnimClass");
        var materials = GetDataPropertyObjectArray(componentData, "OverrideMaterials")
            .Select(index => DescribePackageIndex(index, imports, exports))
            .ToList();

        var meshIndex = skeletalMesh != 0 ? skeletalMesh : staticMesh;
        var meshLabel = skeletalMesh != 0 ? "skeletalMesh" : "staticMesh";
        return $"component={componentName} {meshLabel}={DescribePackageIndex(meshIndex, imports, exports)} skinnedAsset={DescribePackageIndex(skinnedAsset, imports, exports)} anim={DescribePackageIndex(animClass, imports, exports)} materials=[{string.Join("; ", materials)}]";
    }

    private static string GetDataPropertyString(JsonElement dataElement, string propertyName)
    {
        foreach (var property in dataElement.EnumerateArray())
        {
            if (GetString(property, "Name").Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.TryGetProperty("Value", out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
            }
        }

        return "";
    }

    private static int GetDataPropertyInt(JsonElement dataElement, string propertyName)
    {
        foreach (var property in dataElement.EnumerateArray())
        {
            if (GetString(property, "Name").Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.TryGetProperty("Value", out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    private static List<int> GetDataPropertyObjectArray(JsonElement dataElement, string propertyName)
    {
        foreach (var property in dataElement.EnumerateArray())
        {
            if (!GetString(property, "Name").Equals(propertyName, StringComparison.OrdinalIgnoreCase) ||
                !property.TryGetProperty("Value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var output = new List<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("Value", out var itemValue) &&
                    itemValue.ValueKind == JsonValueKind.Number &&
                    itemValue.TryGetInt32(out var index))
                {
                    output.Add(index);
                }
            }

            return output;
        }

        return new List<int>();
    }

    private static string DescribePackageIndex(int index, List<JsonElement> imports, List<JsonElement> exports)
    {
        if (index == 0)
        {
            return "<none>";
        }

        if (index > 0)
        {
            return index <= exports.Count
                ? $"export:{GetString(exports[index - 1], "ObjectName")}"
                : $"export:{index}:<out-of-range>";
        }

        var importIndex = -index - 1;
        if (importIndex < 0 || importIndex >= imports.Count)
        {
            return $"import:{index}:<out-of-range>";
        }

        var import = imports[importIndex];
        var objectName = GetString(import, "ObjectName");
        var outerIndex = GetInt(import, "OuterIndex");
        var outer = "";
        if (outerIndex < 0)
        {
            var outerImportIndex = -outerIndex - 1;
            if (outerImportIndex >= 0 && outerImportIndex < imports.Count)
            {
                outer = GetString(imports[outerImportIndex], "ObjectName");
            }
        }

        return string.IsNullOrWhiteSpace(outer)
            ? $"import:{objectName}"
            : $"import:{outer}.{objectName}";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : 0;
    }
}
