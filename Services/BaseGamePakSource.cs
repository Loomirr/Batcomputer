namespace Batcomputer;

/// <summary>Restricts game reads to shipped pak containers, never installed author mods.</summary>
internal static class BaseGamePakSource
{
    public const SearchOption SearchOption = System.IO.SearchOption.TopDirectoryOnly;

    public static IReadOnlyList<string> FindUtocs(string paksDirectory)
    {
        if (!Directory.Exists(paksDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(paksDirectory, "*.utoc", SearchOption)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
