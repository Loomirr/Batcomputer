using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

public sealed class UAssetPatchService
{
    private const CustomSerializationFlags NameMapOnlyPatchFlags =
        CustomSerializationFlags.SkipParsingExports |
        CustomSerializationFlags.SkipPreloadDependencyLoading;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ProjectRoot { get; }
    public string GuiOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitGuiProjects");

    public UAssetPatchService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public UAssetPatchBatchResult CreateNameMapPatchedStage(NativeSuitProject project)
    {
        var suitProjectService = new SuitProjectService(ProjectRoot);
        var unpatchedContentRoot = suitProjectService.CreateUnpatchedStage(project);
        var patchedContentRoot = Path.Combine(GuiOutputRoot, project.SlotId, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content");

        if (Directory.Exists(patchedContentRoot))
        {
            Directory.Delete(patchedContentRoot, recursive: true);
        }
        Directory.CreateDirectory(patchedContentRoot);

        var batch = new UAssetPatchBatchResult
        {
            Status = "created",
            CreatedUtc = DateTime.UtcNow,
            UnpatchedContentRoot = unpatchedContentRoot,
            PatchedContentRoot = patchedContentRoot,
            MappingsPath = FindDefaultMappingsPath()
        };

        var mappings = string.IsNullOrWhiteSpace(batch.MappingsPath) ? null : MappingsCache.Load(batch.MappingsPath);

        var requests = CreatePackagePatchRequests(project);
        foreach (var request in requests)
        {
            var result = PatchPackageNameMap(unpatchedContentRoot, patchedContentRoot, request, mappings);
            batch.PackageResults.Add(result);
            if (!result.Success)
            {
                batch.Status = "partial-failure";
            }
        }

        var reportPath = Path.Combine(GuiOutputRoot, project.SlotId, "uassetapi-name-map-patch-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(batch, JsonOptions));
        batch.ReportPath = reportPath;

        return batch;
    }

    // Donor family archetype for mod-local clones.
    public const string DonorArchetypePackage = "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman";
    public const string DonorArchetypeStem = "BP_CAT_Archetype_Batman";

    /// <summary>The mod-local archetype clone package path for a project (or null if not using one).</summary>
    public static string? CustomArchetypePackage(NativeSuitProject project)
    {
        if (!project.UseCustomArchetype)
        {
            return null;
        }
        var playable = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable);
        var mod = ExtractModSegment(playable);
        return string.IsNullOrWhiteSpace(mod) ? null : $"/Game/Mods/{mod}/Characters/BP_CAT_Archetype_{mod}";
    }

    // "/Game/Mods/<Mod>/Characters/..." -> "<Mod>"
    private static string ExtractModSegment(string packagePath)
    {
        const string prefix = "/Game/Mods/";
        if (!packagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        var rest = packagePath[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest;
    }

    // Point playable and cutscene parent refs at the mod-local archetype.
    private static void AddArchetypeReparentReplacements(Dictionary<string, string> extra, string customArchetypePkg)
    {
        var customStem = UnrealPathUtil.AssetName(customArchetypePkg);
        // Longest-first ordering is handled downstream in CreateReplacements.
        extra[DonorArchetypePackage] = customArchetypePkg;
        extra["Default__" + DonorArchetypeStem + "_C"] = "Default__" + customStem + "_C";
        extra[DonorArchetypeStem + "_C"] = customStem + "_C";
        extra[DonorArchetypeStem] = customStem;
    }

    private static List<UAssetPackagePatchRequest> CreatePackagePatchRequests(NativeSuitProject project)
    {
        var requests = new List<UAssetPackagePatchRequest>();
        var customArchetypePkg = CustomArchetypePackage(project);
        if (project.PlayableTemplate is not null)
        {
            var sourcePackage = UnrealPathUtil.NormalizePackagePath(project.PlayableTemplate.PackagePath);
            var targetPackage = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable);
            var sourceStem = string.IsNullOrWhiteSpace(project.PlayableTemplate.Stem)
                ? UnrealPathUtil.AssetName(sourcePackage)
                : project.PlayableTemplate.Stem;
            var targetStem = UnrealPathUtil.AssetName(targetPackage);
            var playableExtra = new Dictionary<string, string>();
            if (customArchetypePkg is not null)
            {
                AddArchetypeReparentReplacements(playableExtra, customArchetypePkg);
            }
            requests.Add(new UAssetPackagePatchRequest
            {
                Role = "playable",
                SourcePackagePath = sourcePackage,
                TargetPackagePath = targetPackage,
                SourceStem = sourceStem,
                TargetStem = targetStem,
                SourceGeneratedClassName = sourceStem + "_C",
                TargetGeneratedClassName = targetStem + "_C",
                ExtraReplacements = playableExtra
            });
        }
        if (project.CutsceneTemplate is not null)
        {
            var sourcePackage = UnrealPathUtil.NormalizePackagePath(project.CutsceneTemplate.PackagePath);
            var targetPackage = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Cutscene);
            var sourceStem = string.IsNullOrWhiteSpace(project.CutsceneTemplate.Stem)
                ? UnrealPathUtil.AssetName(sourcePackage)
                : project.CutsceneTemplate.Stem;
            var targetStem = UnrealPathUtil.AssetName(targetPackage);
            var cutsceneExtra = new Dictionary<string, string>();
            if (customArchetypePkg is not null)
            {
                AddArchetypeReparentReplacements(cutsceneExtra, customArchetypePkg);
            }
            requests.Add(new UAssetPackagePatchRequest
            {
                Role = "cutscene",
                SourcePackagePath = sourcePackage,
                TargetPackagePath = targetPackage,
                SourceStem = sourceStem,
                TargetStem = targetStem,
                SourceGeneratedClassName = sourceStem + "_C",
                TargetGeneratedClassName = targetStem + "_C",
                ExtraReplacements = cutsceneExtra
            });
        }
        if (project.DcmdTemplate is not null)
        {
            var sourcePackage = UnrealPathUtil.NormalizePackagePath(project.DcmdTemplate.PackagePath);
            var targetPackage = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Dcmd);
            var sourceStem = string.IsNullOrWhiteSpace(project.DcmdTemplate.Stem)
                ? UnrealPathUtil.AssetName(sourcePackage)
                : project.DcmdTemplate.Stem;
            var targetStem = UnrealPathUtil.AssetName(targetPackage);
            requests.Add(new UAssetPackagePatchRequest
            {
                Role = "dcmd",
                SourcePackagePath = sourcePackage,
                TargetPackagePath = targetPackage,
                SourceStem = sourceStem,
                TargetStem = targetStem,
                SourceGeneratedClassName = sourceStem,
                TargetGeneratedClassName = targetStem,
                ExtraReplacements = CreateDcmdExtraReplacements(project)
            });
        }

        // Clone the donor archetype for the reparented blueprints.
        if (customArchetypePkg is not null)
        {
            var customStem = UnrealPathUtil.AssetName(customArchetypePkg);
            requests.Add(new UAssetPackagePatchRequest
            {
                Role = "archetype",
                SourcePackagePath = DonorArchetypePackage,
                TargetPackagePath = customArchetypePkg,
                SourceStem = DonorArchetypeStem,
                TargetStem = customStem,
                SourceGeneratedClassName = DonorArchetypeStem + "_C",
                TargetGeneratedClassName = customStem + "_C"
            });
        }

        return requests;
    }

    private static Dictionary<string, string> CreateDcmdExtraReplacements(NativeSuitProject project)
    {
        var replacements = new Dictionary<string, string>();

        static void Add(Dictionary<string, string> replacements, string? before, string? after)
        {
            if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after))
            {
                return;
            }
            replacements[before] = after;
        }

        if (project.PlayableTemplate is not null && !string.IsNullOrWhiteSpace(project.TargetPackages.Playable))
        {
            var sourcePlayablePackage = UnrealPathUtil.NormalizePackagePath(project.PlayableTemplate.PackagePath);
            var targetPlayablePackage = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable);
            var targetPlayableStem = UnrealPathUtil.AssetName(targetPlayablePackage);
            Add(replacements, sourcePlayablePackage, targetPlayablePackage);
            Add(replacements, project.PlayableTemplate.Stem, targetPlayableStem);
            Add(replacements, project.PlayableTemplate.Stem + "_C", targetPlayableStem + "_C");
            Add(replacements,
                sourcePlayablePackage + "." + project.PlayableTemplate.Stem + "_C",
                targetPlayablePackage + "." + targetPlayableStem + "_C");
        }

        if (project.CutsceneTemplate is not null && !string.IsNullOrWhiteSpace(project.TargetPackages.Cutscene))
        {
            var sourceCutscenePackage = UnrealPathUtil.NormalizePackagePath(project.CutsceneTemplate.PackagePath);
            var targetCutscenePackage = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Cutscene);
            var targetCutsceneStem = UnrealPathUtil.AssetName(targetCutscenePackage);
            Add(replacements, sourceCutscenePackage, targetCutscenePackage);
            Add(replacements, project.CutsceneTemplate.Stem, targetCutsceneStem);
            Add(replacements, project.CutsceneTemplate.Stem + "_C", targetCutsceneStem + "_C");
            Add(replacements,
                sourceCutscenePackage + "." + project.CutsceneTemplate.Stem + "_C",
                targetCutscenePackage + "." + targetCutsceneStem + "_C");
        }

        // Generated DCMDs currently use the shared unlocked Batman2025 bridge tag.
        // The runtime decides which custom DCMD payload is active per hovered
        // button; the cooked generated assets should not invent per-suit tags yet.
        Add(replacements, "Pawns.Playable.Batman.Absolute", "Pawns.Playable.Batman.TheBatman2025");
        Add(replacements, "GameProgress.Definitions.Characters.Batman.Absolute", "GameProgress.Definitions.Characters.Batman.TheBatman2025");
        Add(replacements, "Pawns.Playable.ThomasWayne.Default", "Pawns.Playable.Batman.TheBatman2025");
        Add(replacements, "GameProgress.Definitions.Characters.ThomasWayne.Casual", "GameProgress.Definitions.Characters.Batman.TheBatman2025");

        return replacements;
    }

    private static UAssetPackagePatchResult PatchPackageNameMap(string unpatchedContentRoot, string patchedContentRoot, UAssetPackagePatchRequest request, Usmap? mappings)
    {
        var result = new UAssetPackagePatchResult
        {
            Role = request.Role,
            SourcePackagePath = request.SourcePackagePath,
            TargetPackagePath = request.TargetPackagePath
        };

        try
        {
            var sourceBase = PackagePathToBasePath(unpatchedContentRoot, request.TargetPackagePath);
            var targetBase = PackagePathToBasePath(patchedContentRoot, request.TargetPackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);

            CopyIfExists(sourceBase + ".uasset", targetBase + ".uasset");
            CopyIfExists(sourceBase + ".uexp", targetBase + ".uexp");
            CopyIfExists(sourceBase + ".ubulk", targetBase + ".ubulk");

            result.InputUasset = sourceBase + ".uasset";
            result.OutputUasset = targetBase + ".uasset";

            var asset = new UAsset(targetBase + ".uasset", EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
            result.Loaded = true;
            result.CustomSerializationFlags = NameMapOnlyPatchFlags.ToString();
            result.PackageNameBeforePatch = asset.FolderName.ToString();
            if (!string.Equals(result.PackageNameBeforePatch, request.TargetPackagePath, StringComparison.Ordinal))
            {
                asset.FolderName = new FString(request.TargetPackagePath);
                result.PackageNameChanged = true;
            }
            result.PackageNameAfterPatch = asset.FolderName.ToString();

            try
            {
                result.BinaryEqualityBeforePatch = asset.VerifyBinaryEquality();
            }
            catch (Exception ex)
            {
                result.BinaryEqualityBeforePatchError = ex.Message;
            }

            var replacements = CreateReplacements(request);
            var nameMap = asset.GetNameMapIndexList();
            result.NameMapCount = nameMap.Count;

            for (var index = 0; index < nameMap.Count; index++)
            {
                var original = nameMap[index].ToString();
                var patched = ApplyReplacements(original, replacements);
                if (patched == original)
                {
                    continue;
                }

                asset.SetNameReference(index, new FString(patched));
                result.NameMapReplacements.Add(new UAssetNameMapReplacement
                {
                    Index = index,
                    Before = original,
                    After = patched
                });
            }

            asset.Write(targetBase + ".uasset");
            result.Written = true;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.ToString();
        }

        return result;
    }

    private static Dictionary<string, string> CreateReplacements(UAssetPackagePatchRequest request)
    {
        var replacements = new Dictionary<string, string>
        {
            [request.SourcePackagePath] = request.TargetPackagePath,
            [request.SourceStem] = request.TargetStem,
            [request.SourceGeneratedClassName] = request.TargetGeneratedClassName,
            ["Default__" + request.SourceGeneratedClassName] = "Default__" + request.TargetGeneratedClassName
        };

        foreach (var pair in request.ExtraReplacements)
        {
            replacements[pair.Key] = pair.Value;
        }

        return replacements
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderByDescending(pair => pair.Key.Length)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static string ApplyReplacements(string value, Dictionary<string, string> replacements)
    {
        var output = value;
        foreach (var pair in replacements)
        {
            output = output.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }
        return output;
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only /Game package paths are supported. Got: {packagePath}");
        }

        return Path.Combine(contentRoot, packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private string? FindDefaultMappingsPath()
    {
        var configured = AppSettings.Current.UsmapPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            AppSettings.BundledUsmapPath() ?? "",
            Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "PartGraphProbe", "input", "Dinner.usmap"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UAssetGUI", "Mappings", "Dinner-5.6.1-1283556+++Dinner+mainline-7f7cc36f.usmap"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class UAssetPackagePatchRequest
{
    public string Role { get; set; } = "";
    public string SourcePackagePath { get; set; } = "";
    public string TargetPackagePath { get; set; } = "";
    public string SourceStem { get; set; } = "";
    public string TargetStem { get; set; } = "";
    public string SourceGeneratedClassName { get; set; } = "";
    public string TargetGeneratedClassName { get; set; } = "";
    public Dictionary<string, string> ExtraReplacements { get; set; } = new();
}

public sealed class UAssetPatchBatchResult
{
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string UnpatchedContentRoot { get; set; } = "";
    public string PatchedContentRoot { get; set; } = "";
    public string? MappingsPath { get; set; }
    public string ReportPath { get; set; } = "";
    public List<UAssetPackagePatchResult> PackageResults { get; set; } = new();
}

public sealed class UAssetPackagePatchResult
{
    public string Role { get; set; } = "";
    public string SourcePackagePath { get; set; } = "";
    public string TargetPackagePath { get; set; } = "";
    public string InputUasset { get; set; } = "";
    public string OutputUasset { get; set; } = "";
    public bool Loaded { get; set; }
    public bool Written { get; set; }
    public bool Success { get; set; }
    public bool? BinaryEqualityBeforePatch { get; set; }
    public string? BinaryEqualityBeforePatchError { get; set; }
    public string? CustomSerializationFlags { get; set; }
    public string? PackageNameBeforePatch { get; set; }
    public string? PackageNameAfterPatch { get; set; }
    public bool PackageNameChanged { get; set; }
    public int NameMapCount { get; set; }
    public string? Error { get; set; }
    public List<UAssetNameMapReplacement> NameMapReplacements { get; set; } = new();
}

public sealed class UAssetNameMapReplacement
{
    public int Index { get; set; }
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
}
