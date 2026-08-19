namespace Batcomputer;

/// <summary>Owns the game-side folders used by the LOTDK Expanded native runtime.</summary>
internal static class LotdkExpandedLayout
{
    public const string ModuleId = "LOTDKExpanded";
    public const string CoreRegistryPluginName = "LOTDKExpandedCoreRegistry";

    public static string CoreRegistryDescriptorPath(string gameRoot) =>
        Path.Combine(CoreRegistryPluginDirectory(gameRoot), CoreRegistryPluginName + ".uplugin");

    public static string CoreRegistryGameIniPath(string gameRoot) =>
        Path.Combine(CoreRegistryPluginDirectory(gameRoot), "Config", "Game.ini");

    public static IReadOnlyList<string> MissingCoreRegistryFiles(string gameRoot)
    {
        var required = new[]
        {
            CoreRegistryDescriptorPath(gameRoot),
            CoreRegistryGameIniPath(gameRoot),
        };
        return required.Where(path => !File.Exists(path)).ToArray();
    }

    public static bool HasInstalledCoreRegistry(string gameRoot) =>
        MissingCoreRegistryFiles(gameRoot).Count == 0;

    public static string Ue4ssRoot(string gameRoot) =>
        Path.Combine(gameRoot, "Binaries", "Win64", "ue4ss");

    public static string DataRoot(string gameRoot) =>
        Path.Combine(Ue4ssRoot(gameRoot), ModuleId);

    public static string ContentPacksRoot(string gameRoot) =>
        Path.Combine(DataRoot(gameRoot), "Mods");

    public static string ContentPackDirectory(string gameRoot, string packId) =>
        Path.Combine(ContentPacksRoot(gameRoot), RequireLeafName(packId, "pack ID"));

    public static string ExpandedPaksRoot(string gameRoot) =>
        Path.Combine(gameRoot, "Content", "Paks", "~mods", "Expanded");

    /// <summary>
    /// GameplayTags imports loose tag lists from the game's project Config/Tags
    /// directory during startup. A content-only registry plugin cannot register
    /// its own Config/Tags directory with the tag manager.
    /// </summary>
    public static string GameConfigTagsRoot(string gameRoot) =>
        Path.Combine(gameRoot, "Config", "Tags");

    public static string ModGameplayTagsPath(string gameRoot, string modId) =>
        Path.Combine(GameConfigTagsRoot(gameRoot), $"{RequireLeafName(modId, "mod ID")}Tags.ini");

    public static string RegistryPluginsRoot(string gameRoot) =>
        Path.Combine(DataRoot(gameRoot), "RegistryPlugins");

    public static string RegistryPluginDirectory(string gameRoot, string pluginName) =>
        Path.Combine(RegistryPluginsRoot(gameRoot), RequireLeafName(pluginName, "plugin name"));

    public static string CoreRegistryPluginDirectory(string gameRoot) =>
        RegistryPluginDirectory(gameRoot, CoreRegistryPluginName);

    public static string? TryFindGameRoot(string? paksFolder)
    {
        if (string.IsNullOrWhiteSpace(paksFolder)) return null;

        try
        {
            var cursor = new DirectoryInfo(Path.GetFullPath(paksFolder));
            while (cursor is not null)
            {
                if (cursor.Name.Equals("LEGOBatmanLotDK", StringComparison.OrdinalIgnoreCase))
                {
                    return cursor.FullName;
                }
                cursor = cursor.Parent;
            }
        }
        catch
        {
            // Setup may still contain a partial path. The caller can present its own guidance.
        }

        return null;
    }

    private static string RequireLeafName(string value, string label)
    {
        var clean = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or ".." ||
            clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Path.GetFileName(clean).Equals(clean, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {label} must be one folder name.", nameof(value));
        }
        return clean;
    }

}
