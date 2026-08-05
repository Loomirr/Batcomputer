using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Creates one deliberately small, independently discoverable Red Brick metadata
/// payload. It is a research proof, not the eventual Red Brick authoring UI:
/// the cloned payload retains the stock metadata entries and changes only its
/// package/asset identity plus AssetTag. If RedBrickWorldSubsystem exposes a
/// second map key for this payload, author packs can own independent payloads.
/// </summary>
public sealed class RedBrickPayloadProbeService
{
    public const string SourcePackage = "/Game/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickData_Main";
    public const string PrimaryAssetType = "RedBrickMetaDataAsset";
    public const string AssetClass = "/Script/Dinner.RedBrickMetaDataAsset";

    public sealed class Request
    {
        /// <summary>Extracted LEGOBatmanLotDK/Content root containing the native donor.</summary>
        public string ExtractedContentRoot { get; init; } = "";
        public string UsmapPath { get; init; } = "";

        /// <summary>Empty/new proof output folder. The service writes a self-contained Install tree below it.</summary>
        public string OutputRoot { get; init; } = "";
        public string ModId { get; init; } = "RedBrickPayloadProbe";
        public string? AssetTag { get; init; }
    }

    public sealed class Result
    {
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
        public string OutputRoot { get; set; } = "";
        public string StageRoot { get; set; } = "";
        public string OutputContentRoot { get; set; } = "";
        public string TargetPackage { get; set; } = "";
        public string TargetObjectPath { get; set; } = "";
        public string AssetTag { get; set; } = "";
        public string OutputUasset { get; set; } = "";
        public string RegistryPluginSource { get; set; } = "";
        public string RegistryPluginInstallPath { get; set; } = "";
        public string AssetManagerConfigPath { get; set; } = "";
        public string TagConfigPath { get; set; } = "";
        public string TrioBasePath { get; set; } = "";
        public string InstallRoot { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public int RetocExitCode { get; set; } = -1;
        public List<string> Repointed { get; } = [];
        public List<string> Log { get; } = [];
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<Result> CreateAsync(Request request, Action<string>? log = null)
    {
        var result = new Result
        {
            OutputRoot = Path.GetFullPath(request.OutputRoot ?? ""),
        };
        void Note(string value)
        {
            result.Log.Add(value);
            log?.Invoke(value);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(request.OutputRoot))
            {
                return Finish(result, "invalid-request", "An output folder is required.");
            }
            if (!IsSafeId(request.ModId))
            {
                return Finish(result, "invalid-request", "ModId must contain only letters, digits, and underscores.");
            }

            var contentRoot = AppSettings.NormalizeContentRoot(request.ExtractedContentRoot);
            var mappingsPath = request.UsmapPath;
            if (!Directory.Exists(contentRoot))
            {
                return Finish(result, "missing-extracted-content", $"Extracted Content root was not found: {contentRoot}");
            }
            if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
            {
                return Finish(result, "missing-usmap", "A valid .usmap path is required to safely patch the cloned payload.");
            }

            var sourceBase = PackageToBase(contentRoot, SourcePackage);
            if (!File.Exists(sourceBase + ".uasset") || !File.Exists(sourceBase + ".uexp"))
            {
                return Finish(result, "missing-donor",
                    $"The native Red Brick payload donor is missing: {sourceBase}.uasset/.uexp");
            }

            var targetPackage = $"/Game/Mods/{request.ModId}/RedBrickEffects/DA_RedBrickData_{request.ModId}";
            var targetAssetName = UnrealPathUtil.AssetName(targetPackage);
            var assetTag = string.IsNullOrWhiteSpace(request.AssetTag)
                ? $"Collectables.RedBrickTaggedAssets.MetaData.Mods.{request.ModId}"
                : request.AssetTag.Trim();
            if (!IsSafeTag(assetTag))
            {
                return Finish(result, "invalid-request", $"AssetTag is not a clean gameplay tag: '{assetTag}'.");
            }

            result.TargetPackage = targetPackage;
            result.TargetObjectPath = UnrealPathUtil.ObjectPath(targetPackage);
            result.AssetTag = assetTag;
            result.StageRoot = Path.Combine(result.OutputRoot, "IoStoreStage");
            result.OutputContentRoot = Path.Combine(result.StageRoot, "LEGOBatmanLotDK", "Content");
            result.TrioBasePath = Path.Combine(result.OutputRoot, request.ModId + "_P");
            result.InstallRoot = Path.Combine(result.OutputRoot, "Install", "LEGOBatmanLotDK");
            result.ReportPath = Path.Combine(result.OutputRoot, "redbrick-payload-proof-report.json");

            var targetBase = PackageToBase(result.OutputContentRoot, targetPackage);
            Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
            CopyRequired(sourceBase + ".uasset", targetBase + ".uasset");
            CopyRequired(sourceBase + ".uexp", targetBase + ".uexp");
            CopyIfExists(sourceBase + ".ubulk", targetBase + ".ubulk");
            CopyIfExists(sourceBase + ".uptnl", targetBase + ".uptnl");

            Note($"Cloned Red Brick payload donor: {SourcePackage}");
            var mappings = MappingsCache.Load(mappingsPath);
            var asset = new UAsset(targetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            asset.FolderName = new FString(targetPackage);

            var sourceAssetName = UnrealPathUtil.AssetName(SourcePackage);
            var nameMapReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SourcePackage] = targetPackage,
                [UnrealPathUtil.ObjectPath(SourcePackage)] = result.TargetObjectPath,
                [sourceAssetName] = targetAssetName,
            };
            var nameMap = asset.GetNameMapIndexList();
            for (var i = 0; i < nameMap.Count; i++)
            {
                var original = nameMap[i].ToString();
                if (nameMapReplacements.TryGetValue(original, out var patched) && !string.Equals(original, patched, StringComparison.Ordinal))
                {
                    asset.SetNameReference(i, new FString(patched));
                    result.Repointed.Add($"{original} -> {patched}");
                }
            }
            UnrealPathUtil.RepairSplitPathNameMapEntries(asset, [targetPackage], result.Repointed);

            if (!NativeAssetTextPatch.SetGameplayTag(asset, "AssetTag", assetTag))
            {
                return Finish(result, "patch-failed", "The native payload donor did not expose its AssetTag property.");
            }
            result.Repointed.Add($"AssetTag -> {assetTag}");
            asset.Write(targetBase + ".uasset");
            result.OutputUasset = targetBase + ".uasset";

            // Round-trip and property validation catch a wrong mapping file or a failed typed write
            // before a misleadingly successful IoStore package is produced.
            var verified = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var verifiedTag = NativeAssetTextPatch.GetGameplayTag(verified, "AssetTag");
            if (!string.Equals(verifiedTag, assetTag, StringComparison.Ordinal))
            {
                return Finish(result, "validation-failed",
                    $"The written payload did not retain its requested AssetTag. expected='{assetTag}' actual='{verifiedTag ?? "<null>"}'.");
            }
            if (!string.Equals(verified.FolderName?.ToString(), targetPackage, StringComparison.Ordinal))
            {
                return Finish(result, "validation-failed",
                    $"The written payload has the wrong package FolderName: '{verified.FolderName}'.");
            }
            Note($"Validated payload package and AssetTag: {targetPackage} -> {assetTag}");

            var registry = await new RegistryPluginService().BuildAsync(
                result.OutputRoot,
                request.ModId,
                "Red Brick payload discovery proof",
                [new RegistryPluginService.RegistryRow(targetPackage, PrimaryAssetType, AssetClass)],
                Note,
                containsRedBricks: true);
            if (!registry.Succeeded || registry.Layout is null)
            {
                return Finish(result, "registry-failed", registry.Error);
            }

            result.RegistryPluginSource = registry.Layout.PluginDirectory;
            result.RegistryPluginInstallPath = Path.Combine(
                result.InstallRoot, "Binaries", "Win64", "ue4ss", "LOTDKExpanded", "RegistryPlugins", registry.Layout.PluginName);
            result.TagConfigPath = Path.Combine(registry.Layout.PluginDirectory, "Config", "Tags", request.ModId + "Tags.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(result.TagConfigPath)!);
            File.WriteAllText(
                result.TagConfigPath,
                PawnTagConfigService.Render([new PawnTagConfigService.TagRow(assetTag, "Batcomputer independent Red Brick payload discovery proof")]),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            result.AssetManagerConfigPath = result.TagConfigPath;
            Note($"Created one mod-owned Red Brick registry plugin with its /Game/Mods scan and AssetTag config.");

            result.RetocExitCode = await PackAsync(result.StageRoot, result.TrioBasePath + ".utoc", Note);
            if (result.RetocExitCode != 0)
            {
                return Finish(result, "pack-failed", $"retoc to-zen failed with exit code {result.RetocExitCode}.");
            }
            foreach (var extension in new[] { ".pak", ".utoc", ".ucas" })
            {
                var trioFile = result.TrioBasePath + extension;
                if (!File.Exists(trioFile))
                {
                    return Finish(result, "pack-failed", $"retoc did not produce {Path.GetFileName(trioFile)}.");
                }
            }

            var trioInstallDirectory = Path.Combine(result.InstallRoot, "Content", "Paks", "~mods");
            Directory.CreateDirectory(trioInstallDirectory);
            foreach (var extension in new[] { ".pak", ".utoc", ".ucas" })
            {
                File.Copy(result.TrioBasePath + extension,
                    Path.Combine(trioInstallDirectory, Path.GetFileName(result.TrioBasePath + extension)), overwrite: true);
            }
            CopyDirectory(registry.Layout.PluginDirectory, result.RegistryPluginInstallPath);
            Note("Created drop-in Install tree for the content trio and early-bootstrap registry plugin.");

            result.Status = "created";
            SaveReport(result);
            return result;
        }
        catch (Exception ex)
        {
            return Finish(result, "error", ex.ToString());
        }
    }

    private static Result Finish(Result result, string status, string? error)
    {
        result.Status = status;
        result.Error = error;
        SaveReport(result);
        return result;
    }

    private static void SaveReport(Result result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(result.ReportPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(result.ReportPath)!);
            File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, ReportJson));
        }
        catch
        {
            // The return status is more useful than masking the original error with a report-write error.
        }
    }

    private static async Task<int> PackAsync(string stageRoot, string outputUtoc, Action<string> log)
    {
        var settings = AppSettings.Current;
        var oodleRetoc = settings.EffectiveOodleRetocExePath();
        var oodleRuntime = settings.EffectiveOodleRuntimeDllPath();
        var useOodle = File.Exists(oodleRetoc) && !string.IsNullOrWhiteSpace(oodleRuntime) && File.Exists(oodleRuntime);
        var retoc = useOodle ? oodleRetoc : settings.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            log($"retoc.exe was not found: {retoc}");
            return -1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputUtoc)!);
        var psi = new ProcessStartInfo
        {
            FileName = retoc,
            WorkingDirectory = Path.GetDirectoryName(retoc) ?? AppSettings.ToolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (useOodle)
        {
            var runtimeFolder = Path.GetDirectoryName(oodleRuntime!)!;
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
                ? runtimeFolder
                : runtimeFolder + Path.PathSeparator + inheritedPath;
        }
        psi.ArgumentList.Add("to-zen");
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(GameAssetRefreshService.RetocEngineVersion);
        psi.ArgumentList.Add(stageRoot);
        psi.ArgumentList.Add(outputUtoc);

        using var process = Process.Start(psi);
        if (process is null)
        {
            log("Could not start retoc.exe.");
            return -1;
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout)) log(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) log(stderr.Trim());
        return process.ExitCode;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopyRequired(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source)) CopyRequired(source, destination);
    }

    private static string PackageToBase(string contentRoot, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only /Game package paths can be staged.", nameof(packagePath));
        }
        return Path.Combine(contentRoot, package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsSafeId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsSafeTag(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => IsAsciiLetterOrDigit(character) || character == '_' || character == '.');

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
