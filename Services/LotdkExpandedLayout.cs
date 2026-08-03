namespace Batcomputer;

/// <summary>Owns the game-side folders used by the LOTDK Expanded native runtime.</summary>
internal static class LotdkExpandedLayout
{
    public const string ModuleId = "LOTDKExpanded";

    public static string Ue4ssRoot(string gameRoot) =>
        Path.Combine(gameRoot, "Binaries", "Win64", "ue4ss");

    public static string DataRoot(string gameRoot) =>
        Path.Combine(Ue4ssRoot(gameRoot), ModuleId);

    public static string ContentPacksRoot(string gameRoot) =>
        Path.Combine(DataRoot(gameRoot), "Mods");

    public static string ContentPackDirectory(string gameRoot, string packId) =>
        Path.Combine(ContentPacksRoot(gameRoot), RequireLeafName(packId, "pack ID"));

    public static string RegistryPluginsRoot(string gameRoot) =>
        Path.Combine(DataRoot(gameRoot), "RegistryPlugins");

    public static string RegistryPluginDirectory(string gameRoot, string pluginName) =>
        Path.Combine(RegistryPluginsRoot(gameRoot), RequireLeafName(pluginName, "plugin name"));

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
