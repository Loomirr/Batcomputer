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

    // Developer-only character research surfaces. Kept off for the normal builder
    // workflow, but user-toggleable so the research tools remain available.
    public bool ShowResearchTools { get; set; }

    // Each extract is ~18 GB, so by default a successful refresh deletes the dumps it replaces.
    // Turn this on to keep them (e.g. to diff two game versions).
    public bool KeepPreviousExtracts { get; set; }

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

    public static string SettingsFilePath =>
        Path.Combine(AppContext.BaseDirectory, "Batcomputer.settings.json");

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
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOptions));
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

    public string EffectiveGamePaksModFolder() =>
        (!string.IsNullOrWhiteSpace(GamePaksModFolder) ? GamePaksModFolder! : DefaultGamePaksModFolder());

    public string EffectiveGamePaksRoot() =>
        UsableDir(GamePaksRoot) ?? DefaultGamePaksRoot();

    /// <summary>
    /// A settings file is "complete" for silent startup when the paths the tool
    /// genuinely needs to function resolve to something that exists.
    /// </summary>
    public bool IsUsable()
    {
        return Directory.Exists(EffectiveProjectRoot())
            && File.Exists(EffectiveRetocExePath())
            && !string.IsNullOrWhiteSpace(EffectiveUsmapPath()) && File.Exists(EffectiveUsmapPath()!)
            && Directory.Exists(EffectiveExtractedContentRoot())
            && Directory.Exists(EffectiveGamePaksRoot());
    }

    /// <summary>Returns a copy pre-filled with every built-in default (for "Detect defaults").</summary>
    public static AppSettings BuiltInDefaults() => new()
    {
        ProjectRoot = DefaultProjectRoot(),
        RetocExePath = DefaultRetocExePath(),
        UsmapPath = DefaultUsmapPath(),
        ExtractedContentRoot = DefaultExtractedContentRoot(),
        ExportContentRoot = DefaultExportContentRoot(),
        GamePaksModFolder = DefaultGamePaksModFolder(),
        GamePaksRoot = DefaultGamePaksRoot()
    };

    // ---- Built-in defaults (the original hardcoded paths) --------------------

    /// <summary>Folder name for everything the tool generates, directly under the project root.</summary>
    public const string GeneratedFolderName = "Generated";

    /// <summary>The pre-release name of that folder, still honoured if a project already uses it.</summary>
    private const string LegacyGeneratedFolderName = "_generated";

    /// <summary>
    /// Output folder for a project root. Uses an existing _generated folder if one is there,
    /// otherwise Generated.
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
            if (Directory.Exists(legacy))
            {
                return legacy;
            }
            return current;
        }
        catch
        {
            return Path.Combine(projectRoot, GeneratedFolderName);
        }
    }

    /// <summary>
    /// Where the tool keeps its work. For an installed copy this is simply the folder holding the
    /// exe, so <c>Generated\</c> sits alongside it. In the dev tree it walks up to the repo root so
    /// running from <c>bin\Debug\…</c> doesn't scatter output inside the build folder.
    /// </summary>
    public static string DefaultProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "CMakeLists.txt")) &&
                Directory.Exists(Path.Combine(dir, "NewSuitSlotNative")))
            {
                return dir.TrimEnd(Path.DirectorySeparatorChar);
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }
            dir = parent.FullName;
        }

        // Installed layout: everything lives next to the exe. No AppData fallback - if this folder
        // isn't writable the app warns (see DescribeRootWritability) rather than silently relocating.
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Null when the effective project root can be written to; otherwise a message explaining why
    /// not. Checked at startup so "install into Program Files" fails loudly instead of at the first
    /// package.
    /// </summary>
    public string? DescribeRootWritability()
    {
        var root = EffectiveProjectRoot();
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".write-probe.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"The tool can't write to its own folder:\n{root}\n\n" +
                   "Windows protects this location (Program Files and similar). Move the tool " +
                   "somewhere like your Desktop or Documents, or set a different project root in Settings.";
        }
        catch (Exception ex)
        {
            return $"The tool can't write to its own folder:\n{root}\n\n{ex.Message}";
        }
    }

    // retoc.exe has no standard install location - the user points at it in Setup.
    public static string DefaultRetocExePath() => "";

    public static string? DefaultUsmapPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(GeneratedRootFor(DefaultProjectRoot()), "PartGraphProbe", "input", "Dinner.usmap"),
            Path.Combine(local, "UAssetGUI", "Mappings", "Dinner.usmap")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public static string DefaultExtractedContentRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "UAssetGUI", "Extracted", "LEGOBatmanLotDK", "Content")
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    // Optional cooked-export root - user points at it in Setup if used.
    public static string DefaultExportContentRoot() => "";

    public static string DefaultGamePaksModFolder() =>
        @"C:\Program Files (x86)\Steam\steamapps\common\LEGO Batman - Legacy of the Dark Knight\LEGOBatmanLotDK\Content\Paks\~mods\Slot";

    public static string DefaultGamePaksRoot() =>
        Path.GetFullPath(Path.Combine(DefaultGamePaksModFolder(), "..", ".."));

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
