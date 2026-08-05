using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Produces the small, cooked AssetRegistry plugin that tells the game about an
/// enabled mod's primary assets during startup. The actual binary is deliberately
/// written by the verified UE 5.6 commandlet; Batcomputer owns layout,
/// invocation, and the release-facing verification contract.
/// </summary>
public sealed class RegistryPluginService
{
    public const string PrimaryAssetType = "PawnMetaData";
    public const string PawnMetadataClass = "/Script/DinnerPawnMetaData.DinnerCharacterMetaData";
    public const string WriterResultMarker = "SUIT_SLOTS_REGISTRY_WRITER_RESULT";
    private const string ProofSentinelRoot = "/Game/Developers/NstDevScan";
    private const string WriterProbePackage = "/Game/Mods/BatcomputerWriterProbe/Characters/DA_DCMD_BatcomputerWriterProbe_Playable";

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
        string WriterProject,
        string BuildScript,
        string EditorCommand);

    private sealed record WriterEnsureResult(bool Succeeded, bool UsedCache, string Error = "");

    private sealed class WriterCacheRecord
    {
        public string Fingerprint { get; set; } = "";
    }

    public static PluginLayout CreateLayout(string buildRoot, string modId, bool containsRedBricks = false)
    {
        var pluginName = containsRedBricks ? $"{modId}RedBricksRegistry" : $"{modId}Registry";
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

        var probeDirectory = Path.Combine(AppSettings.CacheRoot, "RegistryWriter", "SetupProbe");
        var probeOutput = Path.Combine(probeDirectory, "AssetRegistry.bin");
        Directory.CreateDirectory(probeDirectory);
        if (File.Exists(probeOutput)) File.Delete(probeOutput);

        log("Verifying the UE 5.6 Asset Registry writer...");
        var rows = new[] { new RegistryRow(WriterProbePackage) };
        var run = await RunWriterAsync(toolchain, probeOutput, rows, "BatcomputerWriterProbe", log);
        if (run.ExitCode != 0 && ensured.UsedCache)
        {
            log("The cached registry writer did not load; rebuilding it once...");
            ensured = await EnsureWriterAsync(toolchain, log, forceBuild: true);
            if (!ensured.Succeeded)
            {
                return new WriterPreparationResult { Error = ensured.Error };
            }
            run = await RunWriterAsync(toolchain, probeOutput, rows, "BatcomputerWriterProbe", log);
        }

        var verification = FindVerificationLine(run.Output);
        if (run.ExitCode != 0 || !File.Exists(probeOutput) || !VerificationMatches(verification, rows.Length))
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
        Action<string> log,
        bool containsRedBricks = false)
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

        var layout = CreateLayout(buildRoot, modId, containsRedBricks);
        try
        {
            var alternateLayout = CreateLayout(buildRoot, modId, !containsRedBricks);
            if (!string.Equals(alternateLayout.PluginDirectory, layout.PluginDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(alternateLayout.PluginDirectory))
            {
                Directory.Delete(alternateLayout.PluginDirectory, recursive: true);
                log($"Removed stale registry plugin '{alternateLayout.PluginName}'.");
            }
            Directory.CreateDirectory(layout.PluginDirectory);
            File.WriteAllText(layout.DescriptorPath, BuildDescriptorJson(layout.PluginName, modDisplayName));
            // This registry plugin has no configuration of its own. Clear an old copy
            // so a rebuilt release cannot accidentally ship unrelated settings.
            var staleGameIni = Path.Combine(layout.PluginDirectory, "Config", "Game.ini");
            if (File.Exists(staleGameIni))
            {
                File.Delete(staleGameIni);
            }
            var staleConfigDirectory = Path.GetDirectoryName(staleGameIni)!;
            if (Directory.Exists(staleConfigDirectory) && !Directory.EnumerateFileSystemEntries(staleConfigDirectory).Any())
            {
                Directory.Delete(staleConfigDirectory);
            }
            if (File.Exists(layout.RegistryPath))
            {
                File.Delete(layout.RegistryPath);
            }

            var types = string.Join(", ", rows.Select(row => row.EffectivePrimaryAssetType).Distinct(StringComparer.OrdinalIgnoreCase));
            log($"Writing and verifying {rows.Count} registry row(s): {types}.");
            var writerRun = await RunWriterAsync(toolchain, layout.RegistryPath, rows, layout.PluginName, log);
            if (writerRun.ExitCode != 0 && ensured.UsedCache)
            {
                log("The cached registry writer did not load; rebuilding it once...");
                ensured = await EnsureWriterAsync(toolchain, log, forceBuild: true);
                if (!ensured.Succeeded)
                {
                    return new BuildResult { Error = ensured.Error, Layout = layout, Rows = rows };
                }
                writerRun = await RunWriterAsync(toolchain, layout.RegistryPath, rows, layout.PluginName, log);
            }
            var verification = FindVerificationLine(writerRun.Output);
            if (writerRun.ExitCode != 0 || !File.Exists(layout.RegistryPath))
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
            if (!VerificationMatches(verification, rows.Count))
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

    public static bool VerificationMatches(string verificationLine, int expectedRows) =>
        verificationLine.Contains(WriterResultMarker, StringComparison.Ordinal) &&
        verificationLine.Contains("cooked_header=yes", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"expected_primary_rows={expectedRows}", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"exact_primary_rows={expectedRows}", StringComparison.OrdinalIgnoreCase) &&
        verificationLine.Contains($"exact_primary_ids={expectedRows}", StringComparison.OrdinalIgnoreCase) &&
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
            error = "Static Asset Registry generation needs Unreal Engine 5.6 and SuitSlotsRegistryWriter. " +
                    "Open Settings and set the UE 5.6 folder plus the writer .uproject path.";
            return false;
        }

        toolchain = new WriterToolchain(engineRoot, writerProject, buildScript, editorCommand);
        error = "";
        return true;
    }

    private static async Task<WriterEnsureResult> EnsureWriterAsync(
        WriterToolchain toolchain,
        Action<string> log,
        bool forceBuild = false)
    {
        if (!forceBuild && HasCurrentWriterCache(toolchain))
        {
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
                "SuitSlotsRegistryWriterEditor", "Win64", "Development",
                $"-Project={toolchain.WriterProject}", "-WaitMutex", "-NoHotReloadFromIDE",
            },
            log);
        if (writerBuild.ExitCode != 0)
        {
            return new WriterEnsureResult(false, UsedCache: false,
                $"The Asset Registry writer build failed (exit {writerBuild.ExitCode}).");
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
            "-run=SuitSlotsRegistryWriter",
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
        return File.Exists(Path.Combine(directory, "UnrealEditor-SuitSlotsRegistryWriter.dll")) &&
            Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, "*.modules", SearchOption.TopDirectoryOnly).Any();
    }

    private static string WriterCachePath =>
        Path.Combine(AppSettings.CacheRoot, "RegistryWriter", "writer-cache.json");

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

            var projectRoot = Path.GetDirectoryName(toolchain.WriterProject) ?? "";
            AddFingerprintFile(hash, toolchain.WriterProject, Path.GetFileName(toolchain.WriterProject));
            foreach (var directory in new[] { "Config", "Source" })
            {
                var root = Path.Combine(projectRoot, directory);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddFingerprintFile(hash, file, Path.GetRelativePath(projectRoot, file));
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
