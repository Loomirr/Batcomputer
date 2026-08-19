using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Produces the small, cooked AssetRegistry plugin that tells the game about an
/// enabled mod's primary assets during startup. The actual binary is deliberately
/// written by the verified UE 5.6 commandlet; Batcomputer owns layout,
/// invocation, and release validation.
/// </summary>
public sealed class RegistryPluginService
{
    public const string PrimaryAssetType = "PawnMetaData";
    public const string PawnMetadataClass = "/Script/DinnerPawnMetaData.DinnerCharacterMetaData";
    public const string WriterResultMarker = "BATCOMPUTER_REGISTRY_WRITER_RESULT";
    private const string ProofSentinelRoot = "/Game/Developers/BatcomputerRegistryProbe";
    private const string WriterProbePackage = "/Game/Mods/BatcomputerWriterProbe/Characters/DA_DCMD_BatcomputerWriterProbe_Playable";
    private const string PrebuiltDirectoryName = "Prebuilt";
    private const string PrebuiltManifestFileName = "prebuilt-manifest.json";
    private const string WriterModuleFileName = "UnrealEditor-BatcomputerRegistryWriter.dll";
    private const string WriterModulesFileName = "UnrealEditor.modules";

    /// <summary>
    /// One primary-asset row. Suit callers can continue to provide only
    /// <paramref name="PackagePath"/>; a mod can also include other native asset
    /// systems in the same cooked plugin registry.
    /// </summary>
    public sealed record RegistryRow(
        string PackagePath,
        string? PrimaryAssetTypeOverride = null,
        string? AssetClassOverride = null,
        string? PrimaryAssetNameOverride = null)
    {
        public string AssetName => string.IsNullOrWhiteSpace(PrimaryAssetNameOverride)
            ? UnrealPathUtil.AssetName(PackagePath)
            : PrimaryAssetNameOverride.Trim();

        public string EffectivePrimaryAssetType => string.IsNullOrWhiteSpace(PrimaryAssetTypeOverride)
            ? PrimaryAssetType
            : PrimaryAssetTypeOverride.Trim();

        public string EffectiveAssetClass => string.IsNullOrWhiteSpace(AssetClassOverride)
            ? PawnMetadataClass
            : AssetClassOverride.Trim();
    }

    public sealed record PluginLayout(
        string PluginName,
        string PluginDirectory,
        string DescriptorPath,
        string RegistryPath);

    public sealed class BuildResult
    {
        public bool Succeeded { get; init; }
        public string Error { get; init; } = "";
        public PluginLayout? Layout { get; init; }
        public IReadOnlyList<RegistryRow> Rows { get; init; } = Array.Empty<RegistryRow>();
        public string VerificationLine { get; init; } = "";
    }

    public sealed class WriterPreparationResult
    {
        public bool Succeeded { get; init; }
        public bool Rebuilt { get; init; }
        public string Error { get; init; } = "";
        public string VerificationLine { get; init; } = "";
    }

    private sealed record WriterToolchain(
        string EngineRoot,
        string SourceWriterProject,
        string WriterProject,
        string BuildScript,
        string EditorCommand);

    private sealed record WriterEnsureResult(bool Succeeded, bool UsedCache, string Error = "");

    private sealed class WriterCacheRecord
    {
        public string Fingerprint { get; set; } = "";
    }

    private sealed class WriterPrebuiltManifest
    {
        public string EngineBuildId { get; set; } = "";
        public string SourceFingerprint { get; set; } = "";
        public string BinarySha256 { get; set; } = "";
    }

    private sealed record WriterPrebuiltPayload(
        string ModulePath,
        string ModulesPath,
        WriterPrebuiltManifest Manifest);

    public static PluginLayout CreateLayout(string buildRoot, string modId)
    {
        var pluginName = $"{modId}Registry";
        var directory = Path.Combine(buildRoot, "Engine", "Plugins", "Mods", pluginName);
        return new PluginLayout(
            pluginName,
            directory,
            Path.Combine(directory, pluginName + ".uplugin"),
            Path.Combine(directory, "AssetRegistry.bin"));
    }

    public static bool NeedsWriterPreparation()
    {
        return TryGetWriterToolchain(out var toolchain, out _) && !HasCurrentWriterCache(toolchain);
    }

    public async Task<WriterPreparationResult> PrepareAsync(Action<string> log)
    {
        if (!TryGetWriterToolchain(out var toolchain, out var error))
        {
            return new WriterPreparationResult { Error = error };
        }

        var ensured = await EnsureWriterAsync(toolchain, log);
        if (!ensured.Succeeded)
        {
            return new WriterPreparationResult { Error = ensured.Error };
        }

        var probeDirectory = Path.Combine(WriterWorkspaceRoot, "SetupProbe");
        var probeOutput = Path.Combine(probeDirectory, "AssetRegistry.bin");
        Directory.CreateDirectory(probeDirectory);
        if (File.Exists(probeOutput)) File.Delete(probeOutput);

        log("Verifying the UE 5.6 Asset Registry writer...");
        var rows = new[] { new RegistryRow(WriterProbePackage) };
        var run = await RunWriterAsync(toolchain, probeOutput, rows, "BatcomputerWriterProbe", log);
        var verifiedOutput = File.Exists(probeOutput) && VerificationMatches(FindVerificationLine(run.Output), rows);
        if (run.ExitCode != 0 && !verifiedOutput && ensured.UsedCache)
        {
            log("The cached registry writer did not load; rebuilding it once...");
            ensured = await EnsureWriterAsync(toolchain, log, forceBuild: true);
            if (!ensured.Succeeded)
            {
                return new WriterPreparationResult { Error = ensured.Error };
            }
            run = await RunWriterAsync(toolchain, probeOutput, rows, "BatcomputerWriterProbe", log);
            verifiedOutput = File.Exists(probeOutput) && VerificationMatches(FindVerificationLine(run.Output), rows);
        }

        var verification = FindVerificationLine(run.Output);
        if ((!verifiedOutput && run.ExitCode != 0) || !File.Exists(probeOutput) || !VerificationMatches(verification, rows))
        {
            return new WriterPreparationResult
            {
                Error = run.ExitCode != 0
                    ? $"The Asset Registry writer verification failed (exit {run.ExitCode})."
                    : "The Asset Registry writer verification did not produce a valid AssetRegistry.bin.",
                Rebuilt = !ensured.UsedCache,
                VerificationLine = verification,
            };
        }

        return new WriterPreparationResult
        {
            Succeeded = true,
            Rebuilt = !ensured.UsedCache,
            VerificationLine = verification,
        };
    }

    /// <summary>Rejects malformed package paths and duplicate primary asset IDs before UE runs.</summary>
    public static List<string> ValidateRows(IEnumerable<RegistryRow> candidates)
    {
        var errors = new List<string>();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in candidates)
        {
            var package = UnrealPathUtil.NormalizePackagePath(raw.PackagePath);
            if (string.IsNullOrWhiteSpace(package) || !package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Registry asset must be a clean /Game/Mods package path: '{raw.PackagePath}'.");
                continue;
            }
            if (!packages.Add(package))
            {
                errors.Add($"Registry contains the package twice: '{package}'.");
            }

            var asset = raw.AssetName;
            var primaryId = $"{raw.EffectivePrimaryAssetType}:{asset}";
            if (string.IsNullOrWhiteSpace(asset) || !primaryNames.Add(primaryId))
            {
                errors.Add($"Registry primary asset ID collides: '{primaryId}'. Asset names must be unique within one primary-asset type.");
            }
            if (string.IsNullOrWhiteSpace(raw.EffectivePrimaryAssetType))
            {
                errors.Add($"Registry primary asset type is empty for '{package}'.");
            }
            if (!raw.EffectiveAssetClass.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Registry asset class must be a /Script class path for '{package}': '{raw.EffectiveAssetClass}'.");
            }
        }

        if (packages.Count == 0)
        {
            errors.Add("Registry has no enabled primary-asset rows.");
        }
        return errors;
    }

    public static string BuildDescriptorJson(string pluginName, string modDisplayName)
    {
        var descriptor = new
        {
            FileVersion = 3,
            Version = 1,
            VersionName = "1.0",
            FriendlyName = $"{modDisplayName} Registry",
            Description = "Cooked primary-asset Asset Registry rows generated by Batcomputer.",
            Category = "Mods",
            CanContainContent = true,
            NoCode = true,
            EnabledByDefault = true,
            ExplicitlyLoaded = false,
            Installed = true,
            IsBetaVersion = false,
            SupportedTargetPlatforms = new[] { "Win64" },
            Modules = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<BuildResult> BuildAsync(
        string buildRoot,
        string modId,
        string modDisplayName,
        IEnumerable<RegistryRow> candidateRows,
        Action<string> log)
    {
        var rows = candidateRows
            .Select(row => row with { PackagePath = UnrealPathUtil.NormalizePackagePath(row.PackagePath) })
            .ToList();
        var errors = ValidateRows(rows);
        if (errors.Count > 0)
        {
            return new BuildResult { Error = string.Join(" ", errors), Rows = rows };
        }

        if (!TryGetWriterToolchain(out var toolchain, out var toolchainError))
        {
            return new BuildResult
            {
                Error = toolchainError,
                Rows = rows,
            };
        }

        var ensured = await EnsureWriterAsync(toolchain, log);
        if (!ensured.Succeeded)
        {
            return new BuildResult { Error = ensured.Error, Rows = rows };
        }

        var layout = CreateLayout(buildRoot, modId);
        try
        {
            Directory.CreateDirectory(layout.PluginDirectory);
            // Builds produced before the loose-config fix may have left a
            // misleading Config/Tags copy in this reusable output directory.
            // It is not consumed by this content-only plugin, so remove it.
            var legacyPluginTagsDirectory = Path.Combine(layout.PluginDirectory, "Config", "Tags");
            if (Directory.Exists(legacyPluginTagsDirectory))
            {
                Directory.Delete(legacyPluginTagsDirectory, recursive: true);
            }
            File.WriteAllText(layout.DescriptorPath, BuildDescriptorJson(layout.PluginName, modDisplayName));
            if (File.Exists(layout.RegistryPath))
            {
                File.Delete(layout.RegistryPath);
            }

            var types = string.Join(", ", rows.Select(row => row.EffectivePrimaryAssetType).Distinct(StringComparer.OrdinalIgnoreCase));
            log($"Writing and verifying {rows.Count} registry row(s): {types}.");
            var writerRun = await RunWriterAsync(toolchain, layout.RegistryPath, rows, layout.PluginName, log);
            var writerVerifiedOutput = File.Exists(layout.RegistryPath) &&
                VerificationMatches(FindVerificationLine(writerRun.Output), rows);
            if (writerRun.ExitCode != 0 && !writerVerifiedOutput && ensured.UsedCache)
            {
                log("The cached registry writer did not load; rebuilding it once...");
                ensured = await EnsureWriterAsync(toolchain, log, forceBuild: true);
                if (!ensured.Succeeded)
                {
                    return new BuildResult { Error = ensured.Error, Layout = layout, Rows = rows };
                }
                writerRun = await RunWriterAsync(toolchain, layout.RegistryPath, rows, layout.PluginName, log);
                writerVerifiedOutput = File.Exists(layout.RegistryPath) &&
                    VerificationMatches(FindVerificationLine(writerRun.Output), rows);
            }
            var verification = FindVerificationLine(writerRun.Output);
            if ((!writerVerifiedOutput && writerRun.ExitCode != 0) || !File.Exists(layout.RegistryPath))
            {
                return new BuildResult
                {
                    Error = writerRun.ExitCode != 0
                        ? $"The Asset Registry writer failed verification (exit {writerRun.ExitCode})."
                        : "The mod registry release is missing AssetRegistry.bin.",
                    Layout = layout,
                    Rows = rows,
                    VerificationLine = verification,
                };
            }
            if (!VerificationMatches(verification, rows))
            {
                return new BuildResult
                {
                    Error = "The Asset Registry writer did not report every expected primary-asset row as verified.",
                    Layout = layout,
                    Rows = rows,
                    VerificationLine = verification,
                };
            }

            return new BuildResult
            {
                Succeeded = true,
                Layout = layout,
                Rows = rows,
                VerificationLine = verification,
            };
        }
        catch (Exception ex)
        {
            return new BuildResult { Error = ex.Message, Layout = layout, Rows = rows };
        }
    }

    public static bool VerificationMatches(string verificationLine, IReadOnlyList<RegistryRow> rows) =>
        verificationLine.Contains(WriterResultMarker, StringComparison.Ordinal) &&
        verificationLine.Contains("cooked_header=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"expected_primary_rows={rows.Count}", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"exact_primary_rows={rows.Count}", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"exact_primary_ids={rows.Count}", StringComparison.OrdinalIgnoreCase) &&
        rows.All(row => verificationLine.Contains(
            $"{row.EffectivePrimaryAssetType}:{row.AssetName}",
            StringComparison.Ordinal)) &&
        verificationLine.Contains("all_expected_rows=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains("all_expected_primary_ids=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains("sentinel_enabled=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains("sentinel_exact_row=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains("sentinel_exact_primary_id=yes", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetWriterToolchain(out WriterToolchain toolchain, out string error)
    {
        var engineRoot = AppSettings.Current.EffectiveUnrealEngineRoot();
        var writerProject = AppSettings.Current.EffectiveRegistryWriterProjectPath();
        var buildScript = Path.Combine(engineRoot, "Engine", "Build", "BatchFiles", "Build.bat");
        var editorCommand = Path.Combine(engineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        if (!File.Exists(buildScript) || !File.Exists(editorCommand) || !File.Exists(writerProject))
        {
            toolchain = null!;
            error = "Static Asset Registry generation needs Unreal Engine 5.6 and BatcomputerRegistryWriter. " +
                    "Open Settings and set the UE 5.6 folder plus the writer .uproject path.";
            return false;
        }

        if (!TryStageWriterProject(writerProject, engineRoot, out var stagedWriterProject, out error))
        {
            toolchain = null!;
            return false;
        }

        toolchain = new WriterToolchain(
            engineRoot,
            writerProject,
            stagedWriterProject,
            buildScript,
            editorCommand);
        error = "";
        return true;
    }

    /// <summary>
    /// UnrealBuildTool still rejects action paths over 260 characters even when
    /// Windows long-path support is enabled. A source checkout or Debug build can
    /// put the bundled writer several directories below the executable, so mirror
    /// only its small source project into a predictable local cache before building.
    /// Generated Unreal folders remain in that cache and never pollute the portable
    /// install or source tree.
    /// </summary>
    private static bool TryStageWriterProject(
        string sourceProject,
        string engineRoot,
        out string stagedProject,
        out string error)
    {
        stagedProject = Path.Combine(WriterWorkspaceRoot, "P", "BatcomputerRegistryWriter.uproject");
        error = "";
        try
        {
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(sourceProject))
                ?? throw new InvalidOperationException("The Asset Registry writer source folder could not be resolved.");
            var stagedRoot = Path.GetDirectoryName(Path.GetFullPath(stagedProject))
                ?? throw new InvalidOperationException("The Asset Registry writer cache folder could not be resolved.");

            if (string.Equals(sourceRoot, stagedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(stagedProject);
            }

            Directory.CreateDirectory(stagedRoot);
            File.Copy(sourceProject, stagedProject, overwrite: true);
            ReplaceWriterInputDirectory(
                Path.Combine(sourceRoot, "Source"),
                Path.Combine(stagedRoot, "Source"),
                stagedRoot);
            ReplaceWriterInputDirectory(
                Path.Combine(sourceRoot, "Config"),
                Path.Combine(stagedRoot, "Config"),
                stagedRoot);
            StageCompatiblePrebuiltWriter(sourceRoot, stagedRoot, engineRoot);
            return File.Exists(stagedProject) && Directory.Exists(Path.Combine(stagedRoot, "Source"));
        }
        catch (Exception ex)
        {
            error = "Could not prepare the Asset Registry writer in its short build path: " + ex.Message;
            return false;
        }
    }

    private static void ReplaceWriterInputDirectory(
        string sourceDirectory,
        string destinationDirectory,
        string stagedRoot)
    {
        var fullDestination = Path.GetFullPath(destinationDirectory);
        var fullStagedRoot = Path.GetFullPath(stagedRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullDestination.StartsWith(fullStagedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to update an Asset Registry writer folder outside its cache.");
        }

        if (Directory.Exists(fullDestination))
        {
            Directory.Delete(fullDestination, recursive: true);
        }
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(fullDestination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static async Task<WriterEnsureResult> EnsureWriterAsync(
        WriterToolchain toolchain,
        Action<string> log,
        bool forceBuild = false)
    {
        if (!forceBuild && HasCurrentWriterCache(toolchain))
        {
            if (WriterArtifactsMatchBundledPrebuilt(toolchain))
            {
                log("Using the bundled UE 5.6 Asset Registry writer.");
            }
            return new WriterEnsureResult(true, UsedCache: true);
        }

        log(forceBuild
            ? "Rebuilding the UE 5.6 Asset Registry writer..."
            : "Preparing the UE 5.6 Asset Registry writer (first build)...");
        var writerBuild = await RunProcessAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Path.GetDirectoryName(toolchain.BuildScript) ?? toolchain.EngineRoot,
            new[]
            {
                "/c", toolchain.BuildScript,
                "BatcomputerRegistryWriterEditor", "Win64", "Development",
                $"-Project={toolchain.WriterProject}", "-WaitMutex", "-NoHotReloadFromIDE",
            },
            log);
        if (writerBuild.ExitCode != 0)
        {
            return new WriterEnsureResult(false, UsedCache: false,
                DescribeWriterBuildFailure(writerBuild.ExitCode, writerBuild.Output));
        }
        if (!WriterArtifactsExist(toolchain.WriterProject))
        {
            return new WriterEnsureResult(false, UsedCache: false,
                "The Asset Registry writer build completed without its expected editor module.");
        }

        SaveWriterCache(toolchain);
        return new WriterEnsureResult(true, UsedCache: false);
    }

    private static async Task<(int ExitCode, string Output)> RunWriterAsync(
        WriterToolchain toolchain,
        string outputPath,
        IReadOnlyList<RegistryRow> rows,
        string pluginName,
        Action<string> log)
    {
        var first = rows[0];
        var arguments = new List<string>
        {
            toolchain.WriterProject,
            "-run=BatcomputerRegistryWriter",
            // The registry writer does not need a persistent UE Derived Data Cache.
            // This makes a headless write reliable on machines where the global
            // Zen/DDC location is unavailable or intentionally read-only.
            "-DDC-ForceMemoryCache",
            $"-Output={outputPath}",
            $"-Package={first.PackagePath}",
            $"-Class={first.EffectiveAssetClass}",
            $"-PrimaryAssetType={first.EffectivePrimaryAssetType}",
            $"-PrimaryAssetName={first.AssetName}",
        };
        if (rows.Count > 1)
        {
            arguments.Add("-AdditionalRows=" + string.Join(";", rows.Skip(1).Select(row =>
                $"{row.PackagePath}|{row.AssetName}|{row.EffectivePrimaryAssetType}|{row.EffectiveAssetClass}")));
        }
        arguments.Add($"-SentinelPackage={ProofSentinelRoot}/{pluginName}Sentinel");
        arguments.AddRange(new[] { "-Unattended", "-NoSplash", "-NoSourceControl", "-UTF8Output" });
        return await RunProcessAsync(
            toolchain.EditorCommand,
            Path.GetDirectoryName(toolchain.WriterProject) ?? toolchain.EngineRoot,
            arguments,
            log);
    }

    private static string FindVerificationLine(string output) =>
        output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.Contains(WriterResultMarker, StringComparison.Ordinal)) ?? "";

    private static bool HasCurrentWriterCache(WriterToolchain toolchain)
    {
        if (!WriterArtifactsExist(toolchain.WriterProject)) return false;
        if (WriterArtifactsMatchBundledPrebuilt(toolchain)) return true;
        var fingerprint = BuildWriterFingerprint(toolchain);
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        try
        {
            if (!File.Exists(WriterCachePath)) return false;
            var record = JsonSerializer.Deserialize<WriterCacheRecord>(File.ReadAllText(WriterCachePath));
            return string.Equals(record?.Fingerprint, fingerprint, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool WriterArtifactsExist(string writerProject)
    {
        var directory = Path.Combine(Path.GetDirectoryName(writerProject) ?? "", "Binaries", "Win64");
        return File.Exists(Path.Combine(directory, WriterModuleFileName)) &&
            Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, "*.modules", SearchOption.TopDirectoryOnly).Any();
    }

    private static void StageCompatiblePrebuiltWriter(string sourceRoot, string stagedRoot, string engineRoot)
    {
        if (!TryGetCompatiblePrebuiltWriter(sourceRoot, engineRoot, out var prebuilt)) return;

        var destination = Path.Combine(stagedRoot, "Binaries", "Win64");
        Directory.CreateDirectory(destination);
        File.Copy(prebuilt.ModulePath, Path.Combine(destination, WriterModuleFileName), overwrite: true);
        File.Copy(prebuilt.ModulesPath, Path.Combine(destination, WriterModulesFileName), overwrite: true);
    }

    private static bool WriterArtifactsMatchBundledPrebuilt(WriterToolchain toolchain)
    {
        try
        {
            var sourceRoot = Path.GetDirectoryName(toolchain.SourceWriterProject) ?? "";
            if (!TryGetCompatiblePrebuiltWriter(sourceRoot, toolchain.EngineRoot, out var prebuilt)) return false;

            var stagedRoot = Path.GetDirectoryName(toolchain.WriterProject) ?? "";
            var stagedModule = Path.Combine(stagedRoot, "Binaries", "Win64", WriterModuleFileName);
            var stagedModules = Path.Combine(stagedRoot, "Binaries", "Win64", WriterModulesFileName);
            return File.Exists(stagedModule) &&
                File.Exists(stagedModules) &&
                string.Equals(FileSha256(stagedModule), prebuilt.Manifest.BinarySha256, StringComparison.OrdinalIgnoreCase) &&
                TryReadWriterModuleDescriptor(stagedModules, out var stagedBuildId) &&
                string.Equals(stagedBuildId, prebuilt.Manifest.EngineBuildId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetCompatiblePrebuiltWriter(
        string sourceRoot,
        string engineRoot,
        out WriterPrebuiltPayload prebuilt)
    {
        prebuilt = null!;
        try
        {
            var directory = Path.Combine(sourceRoot, PrebuiltDirectoryName, "Win64");
            var manifestPath = Path.Combine(directory, PrebuiltManifestFileName);
            var modulePath = Path.Combine(directory, WriterModuleFileName);
            var modulesPath = Path.Combine(directory, WriterModulesFileName);
            if (!File.Exists(manifestPath) || !File.Exists(modulePath) || !File.Exists(modulesPath)) return false;

            var manifest = JsonSerializer.Deserialize<WriterPrebuiltManifest>(File.ReadAllText(manifestPath));
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.EngineBuildId) ||
                string.IsNullOrWhiteSpace(manifest.SourceFingerprint) ||
                string.IsNullOrWhiteSpace(manifest.BinarySha256))
            {
                return false;
            }

            if (!string.Equals(
                    BuildWriterSourceFingerprint(Path.Combine(sourceRoot, "BatcomputerRegistryWriter.uproject")),
                    manifest.SourceFingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(FileSha256(modulePath), manifest.BinarySha256, StringComparison.OrdinalIgnoreCase) ||
                !TryReadWriterModuleDescriptor(modulesPath, out var prebuiltBuildId) ||
                !string.Equals(prebuiltBuildId, manifest.EngineBuildId, StringComparison.Ordinal))
            {
                return false;
            }

            var engineModules = Path.Combine(engineRoot, "Engine", "Binaries", "Win64", WriterModulesFileName);
            if (!TryReadModuleBuildId(engineModules, out var engineBuildId) ||
                !string.Equals(engineBuildId, manifest.EngineBuildId, StringComparison.Ordinal))
            {
                return false;
            }

            prebuilt = new WriterPrebuiltPayload(modulePath, modulesPath, manifest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadModuleBuildId(string path, out string buildId)
    {
        buildId = "";
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("BuildId", out var value)) return false;
            buildId = value.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(buildId);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadWriterModuleDescriptor(string path, out string buildId)
    {
        buildId = "";
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("BuildId", out var buildIdValue) ||
                !json.RootElement.TryGetProperty("Modules", out var modules) ||
                !modules.TryGetProperty("BatcomputerRegistryWriter", out var moduleValue) ||
                !string.Equals(moduleValue.GetString(), WriterModuleFileName, StringComparison.Ordinal))
            {
                return false;
            }

            buildId = buildIdValue.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(buildId);
        }
        catch
        {
            return false;
        }
    }

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string DescribeWriterBuildFailure(int exitCode, string output)
    {
        if (output.Contains("Could not find NetFxSDK install dir", StringComparison.OrdinalIgnoreCase))
        {
            return "The configured UE 5.6 build does not match Batcomputer's bundled registry writer, " +
                   "and the fallback compile cannot find a .NET Framework SDK. In Visual Studio Installer, " +
                   "add the .NET Framework 4.8 SDK and .NET Framework 4.8 targeting pack, then retry. " +
                   $"UnrealBuildTool exited with code {exitCode}.";
        }

        if (output.Contains("No Visual C++ installation was found", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Unable to find a valid", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("toolchain", StringComparison.OrdinalIgnoreCase))
        {
            return "The configured UE 5.6 build does not match Batcomputer's bundled registry writer, " +
                   "and the fallback compile cannot find the Visual Studio C++ toolchain. Install the " +
                   "Game development with C++ workload in Visual Studio 2022, then retry. " +
                   $"UnrealBuildTool exited with code {exitCode}.";
        }

        return $"The Asset Registry writer build failed (exit {exitCode}). Check Diagnostics and " +
               "UnrealBuildTool's log for the first RulesError or compiler error.";
    }

    private static string WriterWorkspaceRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }
            return Path.Combine(localAppData, "Batcomputer", "RW");
        }
    }

    private static string WriterCachePath =>
        Path.Combine(WriterWorkspaceRoot, "writer-cache.json");

    private static void SaveWriterCache(WriterToolchain toolchain)
    {
        var fingerprint = BuildWriterFingerprint(toolchain);
        if (string.IsNullOrWhiteSpace(fingerprint)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WriterCachePath)!);
            File.WriteAllText(WriterCachePath, JsonSerializer.Serialize(new WriterCacheRecord { Fingerprint = fingerprint }));
        }
        catch
        {
            // Missing cache only costs one incremental UE build next time.
        }
    }

    private static string BuildWriterFingerprint(WriterToolchain toolchain)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AddFingerprintText(hash, Path.GetFullPath(toolchain.EngineRoot));
            AddFingerprintFile(hash, Path.Combine(toolchain.EngineRoot, "Engine", "Build", "Build.version"), "Engine/Build/Build.version");
            AddFingerprintText(hash, BuildWriterSourceFingerprint(toolchain.SourceWriterProject));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            return "";
        }
    }

    private static string BuildWriterSourceFingerprint(string sourceWriterProject)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var projectRoot = Path.GetDirectoryName(sourceWriterProject) ?? "";
            AddNormalizedFingerprintFile(hash, sourceWriterProject, Path.GetFileName(sourceWriterProject));
            foreach (var directory in new[] { "Config", "Source" })
            {
                var root = Path.Combine(projectRoot, directory);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddNormalizedFingerprintFile(hash, file, Path.GetRelativePath(projectRoot, file));
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            return "";
        }
    }

    private static void AddFingerprintText(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData(new byte[] { 0 });
    }

    private static void AddFingerprintFile(IncrementalHash hash, string path, string name)
    {
        AddFingerprintText(hash, name.Replace('\\', '/'));
        hash.AppendData(File.ReadAllBytes(path));
        hash.AppendData(new byte[] { 0 });
    }

    private static void AddNormalizedFingerprintFile(IncrementalHash hash, string path, string name)
    {
        AddFingerprintText(hash, name.Replace('\\', '/'));
        var normalized = File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n');
        hash.AppendData(Encoding.UTF8.GetBytes(normalized));
        hash.AppendData(new byte[] { 0 });
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        Action<string> log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "Could not start the Asset Registry writer process.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (!string.IsNullOrWhiteSpace(output)) log(output.Trim());
        if (!string.IsNullOrWhiteSpace(error)) log(error.Trim());
        return (process.ExitCode, output + Environment.NewLine + error);
    }
}
