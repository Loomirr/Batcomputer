using System.Text.Json;
using System.Text.Json.Serialization;

namespace Batcomputer;

/// <summary>
/// User-configurable tool paths. Persisted next to the .exe as
/// Batcomputer.settings.json so it is portable and survives per-user.
/// Every field is optional: an empty/invalid value falls back to the built-in
/// default (which is the original hardcoded path), so the original author's setup
/// works with no config while other modders can override each path.
/// </summary>
public sealed class AppSettings
{
    // Base folder the tool works out of; the Generated\ output folder is created directly under it.
    public string? ProjectRoot { get; set; }

    // retoc.exe used to build the IoStore trio.
    public string? RetocExePath { get; set; }

    // Patched retoc helper used for lossless Oodle-compressed IoStore releases.
    // It is MIT-licensed and can ship with Batcomputer; it does not include Oodle itself.
    public string? OodleRetocExePath { get; set; }

    // A user-owned oo2core runtime from their locally installed UE 5.6. This stays
    // outside Batcomputer and is never copied into a release or source repository.
    public string? OodleRuntimeDllPath { get; set; }

    // .usmap mappings file for UAssetAPI (read/write cooked assets).
    public string? UsmapPath { get; set; }

    // UAssetGUI-extracted game Content root (source of parts/materials to study/clone).
    public string? ExtractedContentRoot { get; set; }

    // Where "Refresh game assets" WRITES its extracted dumps. Blank = Generated\GameExtracts.
    // Separate from ExtractedContentRoot, which is where the tool READS a dump from.
    public string? AssetExtractRoot { get; set; }

    // Cooked/split export Content root the packager stages from.
    public string? ExportContentRoot { get; set; }

    // Game install folder where the packaged mod trio is installed (~mods\Slot).
    public string? GamePaksModFolder { get; set; }

    // Game Content\Paks folder used by the one-click asset refresh workflow.
    public string? GamePaksRoot { get; set; }

    // Unreal Engine 5.6 is used only by mod authors to run the verified static
    // AssetRegistry writer. Players installing a finished mod do not need it.
    public string? UnrealEngineRoot { get; set; }

    // The small UE project containing SuitSlotsRegistryWriterCommandlet.
    public string? RegistryWriterProjectPath { get; set; }

    // Developer-only character research surfaces. Kept off for the normal builder
    // workflow, but user-toggleable so the research tools remain available.
    public bool ShowResearchTools { get; set; }

    // Each extract is ~18 GB, so by default a successful refresh deletes the dumps it replaces.
    // Turn this on to keep them (e.g. to diff two game versions).
    public bool KeepPreviousExtracts { get; set; }

    // Generated 3D previews can contain several exported models and textures. Keep the portable
    // workspace tidy by removing older preview folders whenever a new preview is built. Authors
    // can turn this off when they need to inspect or compare the generated viewer assets.
    public bool AutoCleanPreviewFiles { get; set; } = true;

    // "Your Character" panel style: true = the minifig figure, false = the classic slot list.
    // Defaults to the figure; the list stays available for anyone who prefers the dense view.
    public bool UseMinifigCharacterPanel { get; set; } = true;

    // Hover/toggle/tile motion. Off makes every animation resolve instantly (no tweening).
    public bool AnimationsEnabled { get; set; } = true;

    // Loaded once at startup; services consult this for path overrides.
    [JsonIgnore]
    public static AppSettings Current { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The folder containing the running executable and all tool-owned state.</summary>
    public static string ToolRoot =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Small persistent tool data such as reusable indexes and downloaded mappings.</summary>
    public static string DataRoot => Path.Combine(ToolRoot, "Data");

    /// <summary>Tool-owned caches that can be rebuilt from configured game inputs.</summary>
    public static string CacheRoot => Path.Combine(DataRoot, "Cache");

    /// <summary>Ephemeral runtime state which must stay beside the executable, not in %TEMP%.</summary>
    public static string RuntimeRoot => Path.Combine(ToolRoot, "Runtime");

    public static string SettingsFilePath =>
        Path.Combine(ToolRoot, "Batcomputer.settings.json");

    public static bool SettingsFileExists => File.Exists(SettingsFilePath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsFilePath), JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings: fall through to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        AdoptUsmapIntoToolData();
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void AdoptUsmapIntoToolData()
    {
        if (string.IsNullOrWhiteSpace(UsmapPath) || !File.Exists(UsmapPath))
        {
            return;
        }

        var mappingsRoot = Path.Combine(DataRoot, "Mappings");
        var source = Path.GetFullPath(UsmapPath);
        if (source.StartsWith(Path.GetFullPath(mappingsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(mappingsRoot);
        var destination = Path.Combine(mappingsRoot, Path.GetFileName(source));
        if (!source.Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }

        UsmapPath = destination;
    }

    // ---- Effective values: user value if usable, else built-in default -------

    public string EffectiveProjectRoot() =>
        UsableDir(ProjectRoot) ?? DefaultProjectRoot();

    public string? EffectiveUsmapPath() =>
        UsableFile(UsmapPath) ?? DefaultUsmapPath();

    public string EffectiveExtractedContentRoot() =>
        NormalizeContentRoot(UsableDir(ExtractedContentRoot) ?? DefaultExtractedContentRoot());

    /// <summary>Destination for asset extraction. Defaults under the project root's Generated folder.</summary>
    public string EffectiveAssetExtractRoot() =>
        !string.IsNullOrWhiteSpace(AssetExtractRoot)
            ? AssetExtractRoot!
            : Path.Combine(GeneratedRootFor(EffectiveProjectRoot()), "GameExtracts");

    public string EffectiveExportContentRoot() =>
        UsableDir(ExportContentRoot) ?? DefaultExportContentRoot();

    public string EffectiveRetocExePath() =>
        UsableFile(RetocExePath) ?? DefaultRetocExePath();

    public string EffectiveOodleRetocExePath() =>
        UsableFile(OodleRetocExePath) ?? DefaultOodleRetocExePath();

    public string? EffectiveOodleRuntimeDllPath() =>
        UsableFile(OodleRuntimeDllPath) ?? OodleRuntimeFromEngine(EffectiveUnrealEngineRoot());

    public bool HasOodleCompressionPrerequisites()
    {
        var runtime = EffectiveOodleRuntimeDllPath();
        return File.Exists(EffectiveOodleRetocExePath()) &&
            !string.IsNullOrWhiteSpace(runtime) &&
            File.Exists(runtime);
    }

    public string EffectiveGamePaksModFolder() =>
        (!string.IsNullOrWhiteSpace(GamePaksModFolder) ? GamePaksModFolder! : DefaultGamePaksModFolder());

    public string EffectiveGamePaksRoot() =>
        UsableDir(GamePaksRoot) ?? DefaultGamePaksRoot();

    public string EffectiveUnrealEngineRoot() =>
        UsableDir(UnrealEngineRoot) ?? DefaultUnrealEngineRoot();

    public string EffectiveRegistryWriterProjectPath() =>
        UsableFile(RegistryWriterProjectPath) ?? DefaultRegistryWriterProjectPath();

    /// <summary>
    /// A settings file is "complete" for silent startup when the paths the tool
    /// genuinely needs to function resolve to something that exists.
    /// </summary>
    public bool IsUsable()
    {
        var extractedContent = EffectiveExtractedContentRoot();
        return Directory.Exists(EffectiveProjectRoot())
            && File.Exists(EffectiveRetocExePath())
            && !string.IsNullOrWhiteSpace(EffectiveUsmapPath()) && File.Exists(EffectiveUsmapPath()!)
            // An empty portable Generated folder is the intentional first-run
            // default, not an extracted game dump. Require the real Content
            // shape so setup still offers the full extraction.
            && Directory.Exists(Path.Combine(extractedContent, "Characters"))
            && Directory.Exists(EffectiveGamePaksRoot());
    }

    /// <summary>Files that every published author install must carry beside the executable.</summary>
    public static IReadOnlyList<string> PortableLayoutIssues()
    {
        var issues = new List<string>();
        var retoc = Path.Combine(ToolRoot, "Tools", "retoc-oodle", "retoc.exe");
        var indexer = Path.Combine(ToolRoot, "Tools", "Build-NativeSuitTemplateIndex.ps1");
        var registryProject = Path.Combine(ToolRoot, "Tools", "SuitSlotsRegistryWriter", "SuitSlotsRegistryWriter.uproject");
        var gameData = Path.Combine(ToolRoot, "gamedata");

        if (!File.Exists(retoc)) issues.Add("Tools\\retoc-oodle\\retoc.exe");
        if (!File.Exists(indexer)) issues.Add("Tools\\Build-NativeSuitTemplateIndex.ps1");
        if (!File.Exists(registryProject)) issues.Add("Tools\\SuitSlotsRegistryWriter\\SuitSlotsRegistryWriter.uproject");
        if (!Directory.Exists(gameData) || !Directory.EnumerateFiles(gameData, "*.json").Any()) issues.Add("gamedata\\*.json");
        return issues;
    }

    /// <summary>Returns a copy pre-filled with every built-in default (for "Detect defaults").</summary>
    public static AppSettings BuiltInDefaults() => new()
    {
        ProjectRoot = DefaultProjectRoot(),
        RetocExePath = DefaultRetocExePath(),
        OodleRetocExePath = DefaultOodleRetocExePath(),
        OodleRuntimeDllPath = DefaultOodleRuntimeDllPath(),
        UsmapPath = DefaultUsmapPath(),
        ExtractedContentRoot = DefaultExtractedContentRoot(),
        ExportContentRoot = DefaultExportContentRoot(),
        GamePaksModFolder = DefaultGamePaksModFolder(),
        GamePaksRoot = DefaultGamePaksRoot(),
        UnrealEngineRoot = DefaultUnrealEngineRoot(),
        RegistryWriterProjectPath = DefaultRegistryWriterProjectPath()
    };

    // ---- Built-in defaults (the original hardcoded paths) --------------------

    /// <summary>Folder name for everything the tool generates.</summary>
    public const string GeneratedFolderName = "Generated";

    /// <summary>The pre-release name of that folder, still honoured by configured project roots.</summary>
    private const string LegacyGeneratedFolderName = "_generated";

    /// <summary>
    /// Output folder for a project root. A portable install defaults its project root to the folder
    /// containing the executable, while a user-configured project continues to use its existing
    /// Generated or _generated folder.
    /// </summary>
    public static string GeneratedRootFor(string projectRoot)
    {
        try
        {
            var current = Path.Combine(projectRoot, GeneratedFolderName);
            if (Directory.Exists(current))
            {
                return current;
            }

            var legacy = Path.Combine(projectRoot, LegacyGeneratedFolderName);
            return Directory.Exists(legacy) ? legacy : current;
        }
        catch
        {
            return Path.Combine(projectRoot, GeneratedFolderName);
        }
    }

    /// <summary>The empty workspace path shown on first run before an asset dump exists.</summary>
    public static string DefaultFirstRunExtractedContentRoot() =>
        GeneratedRootFor(DefaultProjectRoot());

    /// <summary>
    /// Where the tool keeps its work. For an installed copy this is simply the folder holding the
    /// exe, so <c>Generated\</c> sits alongside it. In the dev tree it walks up to the repo root so
    /// running from <c>bin\Debug\…</c> doesn't scatter output inside the build folder.
    /// </summary>
    public static string DefaultProjectRoot()
    {
        // Installed layout: everything lives next to the exe. No AppData fallback - if this folder
        // isn't writable the app warns (see DescribeRootWritability) rather than silently relocating.
        return ToolRoot;
    }

    /// <summary>
    /// Null when the effective project root can be written to; otherwise a message explaining why
    /// not. Checked at startup so "install into Program Files" fails loudly instead of at the first
    /// package.
    /// </summary>
    public string? DescribeRootWritability()
    {
        var roots = new[] { ToolRoot, EffectiveProjectRoot() }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var root in roots)
        {
            try
            {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".write-probe.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException)
            {
                return $"The tool can't write to this workspace folder:\n{root}\n\n" +
                       "Windows protects this location (Program Files and similar). Move the tool " +
                       "somewhere like C:\\Tools\\Batcomputer, or set a different workspace in Settings.";
            }
            catch (Exception ex)
            {
                return $"The tool can't write to this workspace folder:\n{root}\n\n{ex.Message}";
            }
        }
        return null;
    }

    // A portable install can bundle retoc here; Setup still permits an external tool path.
    public static string DefaultRetocExePath()
    {
        var bundled = Path.Combine(ToolRoot, "Tools", "retoc", "retoc.exe");
        // The Oodle-capable fork remains fully compatible with normal to-legacy
        // extraction, so one bundled retoc covers both ordinary and compact builds.
        return File.Exists(bundled) ? bundled : DefaultOodleRetocExePath();
    }


    /// <summary>
    /// Returns the MIT-licensed Oodle-capable retoc helper bundled with a portable
    /// author install. The helper dynamically loads the separate local runtime below.
    /// </summary>
    public static string DefaultOodleRetocExePath()
    {
        var bundled = Path.Combine(ToolRoot, "Tools", "retoc-oodle", "retoc.exe");
        return File.Exists(bundled) ? bundled : "";
    }

    /// <summary>
    /// Finds a local UE 5.6 Oodle runtime without ever copying it into Batcomputer.
    /// The AutomationTool location is the verified path for the current engine release.
    /// </summary>
    public static string? DefaultOodleRuntimeDllPath()
        => OodleRuntimeFromEngine(DefaultUnrealEngineRoot());

    public static string? OodleRuntimeFromEngine(string? engineRoot)
    {
        if (string.IsNullOrWhiteSpace(engineRoot))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(engineRoot, "Engine", "Binaries", "DotNET", "AutomationTool", "oo2core_9_win64.dll"),
            Path.Combine(engineRoot, "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "oo2core_9_win64.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? DefaultUsmapPath()
    {
        return BundledUsmapPath();
    }

    /// <summary>Returns a mapping bundled with the portable tool, regardless of its versioned name.</summary>
    public static string? BundledUsmapPath()
    {
        try
        {
            var root = Path.Combine(DataRoot, "Mappings");
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.usmap").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string DefaultExtractedContentRoot()
    {
        return Path.Combine(DataRoot, "Extracted", "LEGOBatmanLotDK", "Content");
    }

    // Generated material and texture assets stage here unless an author overrides it.
    public static string DefaultExportContentRoot() =>
        Path.Combine(GeneratedRootFor(DefaultProjectRoot()), "ExportContent");

    public static string DefaultGamePaksModFolder() =>
        @"C:\Program Files (x86)\Steam\steamapps\common\LEGO Batman - Legacy of the Dark Knight\LEGOBatmanLotDK\Content\Paks\~mods\Slot";

    public static string DefaultGamePaksRoot() =>
        Path.GetFullPath(Path.Combine(DefaultGamePaksModFolder(), "..", ".."));

    /// <summary>Default UE 5.6 install used by the verified registry commandlet.</summary>
    public static string DefaultUnrealEngineRoot()
    {
        const string epic56 = @"C:\Program Files\Epic Games\UE_5.6";
        return Directory.Exists(epic56) ? epic56 : "";
    }

    /// <summary>Returns the registry-writer project shipped in a portable install.</summary>
    public static string DefaultRegistryWriterProjectPath()
    {
        var relative = Path.Combine("Tools", "SuitSlotsRegistryWriter", "SuitSlotsRegistryWriter.uproject");
        var bundled = Path.Combine(ToolRoot, relative);
        return File.Exists(bundled) ? bundled : "";
    }

    // ---- helpers ------------------------------------------------------------

    public static string NormalizeContentRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var full = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Let users point setup at the exact UAssetGUI dump folder they see, even
        // if that is one or two levels deeper than the Content root the services
        // internally need.
        if (Path.GetFileName(full).Equals("Minifig", StringComparison.OrdinalIgnoreCase) &&
            Directory.GetParent(full)?.Name.Equals("Characters", StringComparison.OrdinalIgnoreCase) == true &&
            Directory.GetParent(Directory.GetParent(full)!.FullName)?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Directory.GetParent(Directory.GetParent(full)!.FullName)!.FullName;
        }

        if (Path.GetFileName(full).Equals("Characters", StringComparison.OrdinalIgnoreCase) &&
            Directory.GetParent(full)?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Directory.GetParent(full)!.FullName;
        }

        var contentChild = Path.Combine(full, "Content");
        if (!Path.GetFileName(full).Equals("Content", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(contentChild))
        {
            return contentChild;
        }

        return full;
    }

    private static string? UsableDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    private static string? UsableFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
}
