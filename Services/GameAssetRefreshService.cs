using System.Diagnostics;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// One-click refresh of the cooked donor assets needed by the Batman native-suit
/// workflow. retoc performs the IoStore Zen -> legacy extraction; UAssetAPI then
/// parses the resulting pairs so a bad/mismatched extraction is reported before
/// the GUI indexes it.
/// </summary>
public sealed class GameAssetRefreshService
{
    public const string RetocEngineVersion = "UE5_6";

    public enum RefreshProfile
    {
        BatmanDonors,
        AllCharacterAssets,
        DeveloperResearch,
    }

    // Keep this deliberately narrow. The Batman folder contains the playable,
    // cutscene, DCMD, UIMD, material, ability, and archetype donors. The other
    // filters are the shared parent/animation donors used when custom archetypes
    // or animation overrides are enabled.
    public static IReadOnlyList<string> BatmanFilters { get; } = new[]
    {
        "Content/Characters/Minifig/Batman/",
        "Content/Characters/BP_Master/BP_CutsceneMinifigCharacter",
        "Content/Characters/BP_Master/BPs_Playable/BP_Playable",
        "Content/Animation/MontageAnimSets/Character/MAS_Char_Batman",
        "Content/Animation/LayerAnimSets/Character/LAS_Char_Batman",
        "Content/Animation/LayerAnimSets/Default/LAS_Default_Batman",
        // StringTable donors for native-suit text generation: ST_TagNames maps pawn
        // tags -> variant names (DCMD DisplayName source), ST_UI holds the suit
        // descriptions (UIMD Description source). Cloned by StringTableGenService as
        // the ST_<ModId> template. Narrow substring filters -> just these two tables.
        "Content/Localization/StringTables/ST_TagNames",
        "Content/Localization/StringTables/ST_UI",
    }.Concat(TextureCookTemplateService.RetocFilters).ToArray();

    // The normal refresh profile used by the builder - this has to be SELF-SUFFICIENT, because it
    // is the one a new user runs. Content/Characters gives the part index every Minifig family,
    // attachment, material, mesh, DCMD/UIMD asset and the master character BP; the other two are
    // small but load-bearing:
    //   StringTables - ST_TagNames/ST_UI are the donors StringTableGenService clones for a suit's
    //                  display name and description. Without them a packaged suit has no text.
    //   Animation    - MAS_Char/LAS_Char sets, needed by the equipment/custom-archetype anim graft.
    // Together they add a small amount to an ~18 GB extract, which is worth it to avoid a half-usable dump.
    public static IReadOnlyList<string> AllCharacterFilters { get; } = new[]
    {
        "Content/Characters/",
        "Content/Localization/StringTables/",
        "Content/Animation/",
    }.Concat(TextureCookTemplateService.RetocFilters).ToArray();

    // Developer-only research profile. This is broader than the normal builder
    // refresh and may take substantially longer and consume more disk space. It
    // is still scoped to character-adjacent content rather than extracting the
    // entire game.
    public static IReadOnlyList<string> DeveloperResearchFilters { get; } = new[]
    {
        "Content/Characters/",
        "Content/Animation/",
        "Content/Equipment/",
        "Content/Abilities/",
        "Content/Gameplay/",
        "Content/UI/",
        "Content/Localization/StringTables/",
        "Content/Plugins/GameFeatures/",
    };

    private readonly string _projectRoot;

    public GameAssetRefreshService(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    public sealed record Progress(int Percent, string Phase, string Detail);

    public sealed class Result
    {
        public RefreshProfile Profile { get; set; }
        public string OutputRoot { get; set; } = "";
        public string ContentRoot { get; set; } = "";
        public int FiltersRun { get; set; }
        public int AssetsExtracted { get; set; }
        public int PairsFound { get; set; }
        public int AssetsValidated { get; set; }
        public int ValidationErrors { get; set; }
        public List<string> Warnings { get; } = new();
        public List<string> Logs { get; } = new();
    }

    public sealed class TextureTemplatePreparationResult
    {
        public List<string> Logs { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    public async Task<Result> RefreshBatmanAsync(
        CancellationToken cancellationToken,
        IProgress<Progress>? progress = null)
        => await RefreshAsync(RefreshProfile.BatmanDonors, cancellationToken, progress);

    public async Task<TextureTemplatePreparationResult> PrepareTextureCookTemplatesAsync(
        CancellationToken cancellationToken,
        IProgress<Progress>? progress = null)
    {
        var result = new TextureTemplatePreparationResult();
        if (TextureCookTemplateService.HasCoreTemplates(_projectRoot))
        {
            result.Logs.Add("Texture cook templates already exist.");
            return result;
        }

        var retoc = AppSettings.Current.EffectiveRetocExePath();
        var paksRoot = AppSettings.Current.EffectiveGamePaksRoot();
        if (!File.Exists(retoc))
        {
            throw new FileNotFoundException("retoc.exe was not found. Open Setup and select it.", retoc);
        }
        if (!Directory.Exists(paksRoot))
        {
            throw new DirectoryNotFoundException($"Game Paks folder was not found: {paksRoot}");
        }

        var generatedRoot = AppSettings.GeneratedRootFor(_projectRoot);
        var stageRoot = Path.Combine(generatedRoot, "TextureTemplateStage");
        if (Directory.Exists(stageRoot))
        {
            Directory.Delete(stageRoot, recursive: true);
        }
        Directory.CreateDirectory(stageRoot);

        try
        {
            var filters = TextureCookTemplateService.RetocFilters;
            for (var i = 0; i < filters.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = filters[i];
                progress?.Report(new Progress(5 + (i * 75 / filters.Count), "Preparing textures", filter));
                var command = await RunRetocAsync(retoc, paksRoot, stageRoot, filter, cancellationToken);
                result.Logs.AddRange(command.OutputLines.TakeLast(4));
                if (command.ExitCode != 0)
                {
                    var detail = command.ErrorLines.Count == 0
                        ? string.Join(Environment.NewLine, command.OutputLines.TakeLast(8))
                        : string.Join(Environment.NewLine, command.ErrorLines.TakeLast(8));
                    throw new InvalidOperationException($"retoc failed while preparing texture templates for '{filter}' (exit {command.ExitCode}).\n{detail}");
                }
            }

            var contentRoot = FindContentRoot(stageRoot, requireCharacters: false)
                ?? throw new InvalidDataException("retoc completed, but did not produce a Content folder for the texture templates.");
            var prepared = TextureCookTemplateService.PrepareFromContentRoot(_projectRoot, contentRoot);
            result.Logs.AddRange(prepared.Logs);
            result.Warnings.AddRange(prepared.Warnings);
            if (!TextureCookTemplateService.HasCoreTemplates(_projectRoot))
            {
                throw new InvalidDataException("The required world and UI texture templates were not prepared.");
            }

            progress?.Report(new Progress(90, "Preparing textures", "Texture cook templates are ready."));
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stageRoot))
                {
                    Directory.Delete(stageRoot, recursive: true);
                }
            }
            catch
            {
                // A later attempt can replace the staging folder.
            }
        }
    }

    public async Task<Result> RefreshAsync(
        RefreshProfile profile,
        CancellationToken cancellationToken,
        IProgress<Progress>? progress = null)
    {
        var settings = AppSettings.Current;
        var retoc = settings.EffectiveRetocExePath();
        var paksRoot = settings.EffectiveGamePaksRoot();

        if (!File.Exists(retoc))
        {
            throw new FileNotFoundException("retoc.exe was not found. Open Setup and select it.", retoc);
        }

        if (!Directory.Exists(paksRoot))
        {
            throw new DirectoryNotFoundException($"Game Paks folder was not found: {paksRoot}");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputPrefix = profile switch
        {
            RefreshProfile.AllCharacterAssets => "AllCharacters",
            RefreshProfile.DeveloperResearch => "DeveloperResearch",
            _ => "Batman",
        };
        // User-settable destination (Settings → Extracted assets output). Defaults under Generated\.
        var outputRoot = Path.Combine(AppSettings.Current.EffectiveAssetExtractRoot(), $"{outputPrefix}_{stamp}");
        Directory.CreateDirectory(outputRoot);

        var filters = FiltersFor(profile);
        var result = new Result { Profile = profile, OutputRoot = outputRoot };
        progress?.Report(new Progress(2, "Preparing", $"Source: {paksRoot}"));
        result.Logs.Add($"Refresh profile: {profile}");
        result.Logs.Add("retoc reads the top-level Paks containers; nested mod folders are not mounted.");

        for (var i = 0; i < filters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = filters[i];
            var start = 5 + (i * 70 / filters.Count);
            var end = 5 + ((i + 1) * 70 / filters.Count);
            progress?.Report(new Progress(start, "Extracting", filter));

            var command = await RunRetocAsync(retoc, paksRoot, outputRoot, filter, cancellationToken);
            result.Logs.AddRange(command.OutputLines.TakeLast(12));
            if (command.ExitCode != 0)
            {
                var detail = command.ErrorLines.Count == 0
                    ? string.Join(Environment.NewLine, command.OutputLines.TakeLast(8))
                    : string.Join(Environment.NewLine, command.ErrorLines.TakeLast(8));
                throw new InvalidOperationException($"retoc failed for '{filter}' (exit {command.ExitCode}).\n{detail}");
            }

            progress?.Report(new Progress(end, "Extracting", $"Finished {filter}"));
        }

        var contentRoot = FindContentRoot(outputRoot);
        if (contentRoot is null)
        {
            throw new InvalidDataException($"retoc completed, but no LEGOBatmanLotDK\\Content folder was produced under {outputRoot}.");
        }

        var assets = Directory.EnumerateFiles(contentRoot, "*.uasset", SearchOption.AllDirectories).ToList();
        var pairs = assets.Count(path => File.Exists(Path.ChangeExtension(path, ".uexp")));
        result.ContentRoot = contentRoot;
        result.FiltersRun = filters.Count;
        result.AssetsExtracted = assets.Count;
        result.PairsFound = pairs;
        result.Logs.Add($"retoc output: {outputRoot}");
        result.Logs.Add($"Extracted assets={assets.Count}, asset/uexp pairs={pairs}");

        // Developer research can include thousands of animation/UI/collectable
        // packages. Those assets are useful to inspect, but parsing every one
        // through UAssetAPI adds substantial memory pressure and can hit an
        // access violation inside a native compression dependency. Character
        // assets remain fully validated; the broad research-only folders are
        // extraction data and are intentionally left for on-demand inspection.
        var assetsToValidate = profile == RefreshProfile.DeveloperResearch
            ? assets.Where(path => Path.GetRelativePath(contentRoot, path)
                .StartsWith("Characters" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : assets;
        result.Logs.Add($"Validation scope: {assetsToValidate.Count} character asset(s) of {assets.Count} extracted asset(s).");
        progress?.Report(new Progress(78, "Validating", $"Parsing {assetsToValidate.Count} character assets with UAssetAPI..."));
        var validation = await Task.Run(
            () => ValidateAssets(contentRoot, assetsToValidate, cancellationToken),
            cancellationToken);
        result.AssetsValidated = validation.Validated;
        result.ValidationErrors = validation.Errors.Count;
        result.Logs.Add($"UAssetAPI validation: parsed={validation.Validated}, errors={validation.Errors.Count}, missingUexp={validation.MissingPairs}");
        result.Warnings.AddRange(validation.Errors.Take(30));
        if (validation.MissingPairs > 0)
        {
            result.Warnings.Add($"{validation.MissingPairs} extracted .uasset file(s) have no matching .uexp file.");
        }

        progress?.Report(new Progress(88, "Validated", $"Parsed {validation.Validated} assets; rebuilding indexes next."));
        progress?.Report(new Progress(90, "Complete", "Extraction and validation complete."));
        return result;
    }

    public static IReadOnlyList<string> FiltersFor(RefreshProfile profile) => profile switch
    {
        RefreshProfile.AllCharacterAssets => AllCharacterFilters,
        RefreshProfile.DeveloperResearch => DeveloperResearchFilters,
        _ => BatmanFilters,
    };

    private static async Task<ProcessResult> RunRetocAsync(
        string retoc,
        string paksRoot,
        string outputRoot,
        string filter,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = retoc,
            WorkingDirectory = Path.GetDirectoryName(retoc) ?? AppSettings.ToolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("to-legacy");
        startInfo.ArgumentList.Add("--no-shaders");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(filter);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(RetocEngineVersion);
        startInfo.ArgumentList.Add(paksRoot);
        startInfo.ArgumentList.Add(outputRoot);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start retoc.exe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation is still the primary result; cleanup is best effort.
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(
            process.ExitCode,
            SplitLines(stdout),
            SplitLines(stderr));
    }

    private sealed record ProcessResult(int ExitCode, List<string> OutputLines, List<string> ErrorLines);

    private sealed class ValidationResult
    {
        public int Validated { get; set; }
        public int MissingPairs { get; set; }
        public List<string> Errors { get; } = new();
    }

    private static ValidationResult ValidateAssets(string contentRoot, List<string> assets, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        Usmap? mappings = null;
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        if (!string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap))
        {
            try
            {
                mappings = MappingsCache.Load(usmap);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Could not load mappings for validation: {ex.Message}");
            }
        }

        foreach (var assetPath in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uexp = Path.ChangeExtension(assetPath, ".uexp");
            if (!File.Exists(uexp))
            {
                result.MissingPairs++;
                continue;
            }

            try
            {
                _ = new UAsset(
                    assetPath,
                    EngineVersion.VER_UE5_6,
                    mappings,
                    CustomSerializationFlags.SkipPreloadDependencyLoading);
                result.Validated++;
            }
            catch (Exception ex)
            {
                if (result.Errors.Count < 100)
                {
                    result.Errors.Add($"{Path.GetRelativePath(contentRoot, assetPath)}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string? FindContentRoot(string outputRoot, bool requireCharacters = true)
    {
        return Directory
            .EnumerateDirectories(outputRoot, "Content", SearchOption.AllDirectories)
            .FirstOrDefault(path => !requireCharacters || Directory.Exists(Path.Combine(path, "Characters")));
    }
}
