using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    public const string CharacterGadgetFilter = "Content/Models/Gadgets/";
    public const string CharacterMaterialsFilter = "Content/Characters/Materials/";
    // DLC packages mount their content below this normal /Game folder. This is
    // intentionally a package filter, not a physical Content directory: the
    // installed DLC IoStore containers live beside Paks in Content\DLC.
    public const string AdditionalContentFilter = "Content/AdditionalContent/";
    public const string CapeTransparentMaterialFilter =
        "Content/Art/TechnicalArt/Optimisation/M_Cape_Transparent";

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
        // One metadata asset used only by the Playable 3D viewer's read-only native
        // colour-preset selector. This does not restore Red Brick authoring assets.
        ViewerBaseGameRedBrickPaletteService.RetocFilter,
        // Shared parent used by native cape materials. It lives outside Characters,
        // so a character-only filter does not bring it into the extracted workspace.
        CapeTransparentMaterialFilter,
    }.Concat(TextureCookTemplateService.RetocFilters).ToArray();

    // The normal refresh profile used by the builder - this has to be SELF-SUFFICIENT, because it
    // is the one a new user runs. Content/Characters gives the part index every Minifig family,
    // attachment, material, mesh, DCMD/UIMD asset and the master character BP; the other two are
    // small but load-bearing:
    //   StringTables - ST_TagNames/ST_UI are the donors StringTableGenService clones for a suit's
    //                  display name and description. Without them a packaged suit has no text.
    //   Animation    - MAS_Char/LAS_Char sets, needed by the equipment/custom-archetype anim graft.
    //   Models/Gadgets - character equipment and glider meshes/materials. These live outside
    //                    Content/Characters even though the character catalog exposes them.
    // Together they add a small amount to an ~18 GB extract, which is worth it to avoid a half-usable dump.
    public static IReadOnlyList<string> AllCharacterFilters { get; } = new[]
    {
        "Content/Characters/",
        "Content/Localization/StringTables/",
        "Content/Animation/",
        CharacterGadgetFilter,
        // Shipped character DLC is authored below /Game/AdditionalContent rather
        // than /Game/Characters. Include the whole package tree so its visual
        // bases, materials, attachments, meshes and supporting metadata stay
        // together in the active extracted Content dump.
        AdditionalContentFilter,
        CapeTransparentMaterialFilter,
        // Keep the clean-install viewer self-sufficient without broadening this into
        // a Red Brick authoring or collectables extraction profile.
        ViewerBaseGameRedBrickPaletteService.RetocFilter,
    }.Concat(TextureCookTemplateService.RetocFilters.Where(filter =>
        !filter.StartsWith(CharacterGadgetFilter, StringComparison.OrdinalIgnoreCase))).ToArray();

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
        CharacterGadgetFilter,
        AdditionalContentFilter,
        CapeTransparentMaterialFilter,
        "Content/UI/",
        "Content/Localization/StringTables/",
        "Content/Plugins/GameFeatures/",
        ViewerBaseGameRedBrickPaletteService.RetocFilter,
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
        public string DlcSourceRoot { get; set; } = "";
        public int BaseContainersMounted { get; set; }
        public int DlcContainersMounted { get; set; }
        public int AdditionalContentAssets { get; set; }
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
        var dlcRoot = DlcRootForPaksRoot(paksRoot);
        var includeDlc = ProfileIncludesDlc(profile) && CountIoStoreContainers(dlcRoot) > 0;
        string? mountedInputRoot = null;

        progress?.Report(new Progress(2, "Preparing", includeDlc
            ? "Preparing base-game and DLC containers…"
            : $"Source: {paksRoot}"));
        result.Logs.Add($"Refresh profile: {profile}");
        result.BaseContainersMounted = CountIoStoreContainers(paksRoot);
        result.Logs.Add("retoc reads only the direct IoStore containers in its input folder; installed mods are never mounted.");

        try
        {
            var retocInputRoot = paksRoot;
            if (includeDlc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new Progress(3, "Preparing", "Mounting base-game script data with DLC containers…"));
                mountedInputRoot = CreateCombinedContainerMount(paksRoot, dlcRoot, outputRoot);
                retocInputRoot = mountedInputRoot;
                result.DlcSourceRoot = dlcRoot;
                result.DlcContainersMounted = CountIoStoreContainers(dlcRoot);
                result.Logs.Add($"Mounted {result.BaseContainersMounted} base and {result.DlcContainersMounted} DLC IoStore container(s).");
                result.Logs.Add($"DLC source: {dlcRoot}");
            }
            else if (ProfileIncludesDlc(profile))
            {
                result.Warnings.Add($"No DLC IoStore containers were found at {dlcRoot}. The base-game extract will remain usable, but DLC donors will not be available.");
            }

            for (var i = 0; i < filters.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = filters[i];
                var start = 5 + (i * 70 / filters.Count);
                var end = 5 + ((i + 1) * 70 / filters.Count);
                progress?.Report(new Progress(start, "Extracting", filter));

                var command = await RunRetocAsync(retoc, retocInputRoot, outputRoot, filter, cancellationToken);
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
        }
        finally
        {
            TryDeleteCombinedContainerMount(mountedInputRoot);
        }

        var contentRoot = FindContentRoot(outputRoot);
        if (contentRoot is null)
        {
            throw new InvalidDataException($"retoc completed, but no LEGOBatmanLotDK\\Content folder was produced under {outputRoot}.");
        }

        var assets = Directory.EnumerateFiles(contentRoot, "*.uasset", SearchOption.AllDirectories).ToList();
        ValidateProfileCoverage(profile, contentRoot, result);
        var pairs = assets.Count(path => File.Exists(Path.ChangeExtension(path, ".uexp")));
        result.ContentRoot = contentRoot;
        result.FiltersRun = filters.Count;
        result.AssetsExtracted = assets.Count;
        result.PairsFound = pairs;
        result.Logs.Add($"retoc output: {outputRoot}");
        result.Logs.Add($"Extracted assets={assets.Count}, asset/uexp pairs={pairs}");
        var additionalContentRoot = Path.Combine(contentRoot, "AdditionalContent");
        result.AdditionalContentAssets = Directory.Exists(additionalContentRoot)
            ? Directory.EnumerateFiles(additionalContentRoot, "*.uasset", SearchOption.AllDirectories).Count()
            : 0;
        if (result.DlcContainersMounted > 0)
        {
            if (result.AdditionalContentAssets == 0)
            {
                result.Warnings.Add(
                    "DLC containers were mounted, but no Content\\AdditionalContent assets were extracted. " +
                    "The installed DLC may not match this game's expected package layout.");
            }
            else
            {
                result.Logs.Add($"DLC AdditionalContent assets={result.AdditionalContentAssets}");
            }
        }

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
        var validationKind = profile == RefreshProfile.DeveloperResearch ? "character" : "extracted";
        result.Logs.Add($"Validation scope: {assetsToValidate.Count} {validationKind} asset(s) of {assets.Count} extracted asset(s).");
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

    private static bool ProfileIncludesDlc(RefreshProfile profile) =>
        profile is RefreshProfile.AllCharacterAssets or RefreshProfile.DeveloperResearch;

    internal static string DlcRootForPaksRoot(string paksRoot)
    {
        var paks = Path.GetFullPath(paksRoot.Trim());
        var contentRoot = Directory.GetParent(paks)?.FullName;
        return string.IsNullOrWhiteSpace(contentRoot)
            ? Path.Combine(paks, "..", "DLC")
            : Path.Combine(contentRoot, "DLC");
    }

    private static int CountIoStoreContainers(string? root)
    {
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.utoc", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    /// <summary>
    /// retoc accepts one flat container directory. DLC asset conversion still
    /// needs the base game's global script-object container, so a DLC-only pass
    /// fails even when the DLC itself is valid. A disposable link farm exposes
    /// the original base and DLC trios as one flat input without copying them or
    /// changing the installed game files.
    /// </summary>
    internal static string CreateCombinedContainerMount(string paksRoot, string dlcRoot, string outputRoot)
    {
        var mountRoot = Path.Combine(outputRoot, ".retoc-base-and-dlc-input");
        Directory.CreateDirectory(mountRoot);
        var sources = new[] { paksRoot, dlcRoot };
        foreach (var sourceRoot in sources)
        {
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsIoStoreContainerFile)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var destination = Path.Combine(mountRoot, Path.GetFileName(source));
                if (File.Exists(destination))
                {
                    throw new InvalidDataException(
                        $"The base and DLC container folders both contain '{Path.GetFileName(source)}'. " +
                        "Batcomputer will not guess which one retoc should mount.");
                }

                CreateContainerLink(destination, source);
            }
        }

        if (CountIoStoreContainers(mountRoot) == 0)
        {
            throw new InvalidDataException("The temporary base/DLC IoStore mount contains no .utoc containers.");
        }

        return mountRoot;
    }

    private static bool IsIoStoreContainerFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pak", StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateContainerLink(string destination, string source)
    {
        try
        {
            if (!CreateHardLink(destination, source, IntPtr.Zero))
            {
                throw new IOException(
                    $"Windows could not create a hard link for '{Path.GetFileName(source)}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return;
        }
        catch (Exception hardLinkError) when (
            hardLinkError is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.CreateSymbolicLink(destination, source);
                return;
            }
            catch (Exception symbolicLinkError) when (
                symbolicLinkError is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new IOException(
                    "Batcomputer could not create its temporary base/DLC container mount. " +
                    "Keep the workspace on the game drive, or enable Windows Developer Mode so file links are allowed. " +
                    $"Source: {source}",
                    new AggregateException(hardLinkError, symbolicLinkError));
            }
        }
    }

    internal static void TryDeleteCombinedContainerMount(string? mountRoot)
    {
        if (string.IsNullOrWhiteSpace(mountRoot) || !Directory.Exists(mountRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(mountRoot, recursive: true);
        }
        catch
        {
            // It is a disposable child of the new output root. A later refresh
            // uses a new timestamped root, so a locked cleanup never affects the
            // active dump or any original game file.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    internal static bool FiltersRecursivelyCover(
        IEnumerable<string> filters,
        string requiredFolder)
    {
        static string NormalizeFolder(string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            return normalized.EndsWith('/') ? normalized : normalized + "/";
        }

        var required = NormalizeFolder(requiredFolder);
        return filters.Any(filter =>
            required.StartsWith(NormalizeFolder(filter), StringComparison.OrdinalIgnoreCase));
    }

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

    private static void ValidateProfileCoverage(RefreshProfile profile, string contentRoot, Result result)
    {
        if (profile is not (RefreshProfile.AllCharacterAssets or RefreshProfile.DeveloperResearch))
        {
            return;
        }

        var characterMaterialsRoot = Path.Combine(contentRoot, "Characters", "Materials");
        if (!Directory.Exists(characterMaterialsRoot))
        {
            throw new InvalidDataException(
                "retoc completed, but the shared Content\\Characters\\Materials folder was not extracted. " +
                "The previous extracted dump remains active. Verify the original game Content\\Paks folder and retry the refresh.");
        }

        var characterMaterialAssets = Directory
            .EnumerateFiles(characterMaterialsRoot, "*.uasset", SearchOption.AllDirectories)
            .Count();
        if (characterMaterialAssets == 0)
        {
            throw new InvalidDataException(
                "retoc created Content\\Characters\\Materials but extracted no material assets. " +
                "The previous extracted dump remains active. Verify the original game Content\\Paks folder and retry the refresh.");
        }

        result.Logs.Add($"Shared character material assets={characterMaterialAssets}");

        var gadgetRoot = Path.Combine(contentRoot, "Models", "Gadgets");
        if (!Directory.Exists(gadgetRoot))
        {
            throw new InvalidDataException(
                "retoc completed, but the character-supporting Content\\Models\\Gadgets folder was not extracted. " +
                "The previous extracted dump remains active. Verify the original game Content\\Paks folder and retry the refresh.");
        }

        var gadgetAssets = Directory.EnumerateFiles(gadgetRoot, "*.uasset", SearchOption.AllDirectories).Count();
        if (gadgetAssets == 0)
        {
            throw new InvalidDataException(
                "retoc created Content\\Models\\Gadgets but extracted no gadget assets. " +
                "The previous extracted dump remains active. Verify the original game Content\\Paks folder and retry the refresh.");
        }

        result.Logs.Add($"Character-supporting gadget assets={gadgetAssets}");

        // This cataloged Nightwing material is a useful end-to-end sentinel: it is one of the
        // assets that exposed the old incomplete refresh profile. Do not fail a future game build
        // solely because it was renamed, but make the mismatch explicit in Diagnostics.
        var wingsuitMaterial = Path.Combine(
            gadgetRoot,
            "GA_Wingsuit_NightWing",
            "MI_DECAL_Wingsuit_Nightwing.uasset");
        if (!File.Exists(wingsuitMaterial))
        {
            result.Warnings.Add(
                "Supporting gadget assets were extracted, but MI_DECAL_Wingsuit_Nightwing was not found. " +
                "The installed game build may not match Batcomputer's material catalog.");
        }
    }

    private static string? FindContentRoot(string outputRoot, bool requireCharacters = true)
    {
        return Directory
            .EnumerateDirectories(outputRoot, "Content", SearchOption.AllDirectories)
            .FirstOrDefault(path => !requireCharacters || Directory.Exists(Path.Combine(path, "Characters")));
    }
}
