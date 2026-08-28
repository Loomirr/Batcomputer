namespace Batcomputer;

/// <summary>
/// Finds the logical character package roots in an extracted game Content folder without walking
/// the entire multi-gigabyte dump. Base-game characters are always direct children of Content;
/// shipped DLC may nest its own Characters folder anywhere below AdditionalContent.
/// </summary>
internal static class CharacterContentRootService
{
    internal static IReadOnlyList<string> Enumerate(string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
        {
            return Array.Empty<string>();
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseCharacters = Path.Combine(contentRoot, "Characters");
        if (Directory.Exists(baseCharacters))
        {
            roots.Add(baseCharacters);
        }

        var additionalContent = Path.Combine(contentRoot, "AdditionalContent");
        if (Directory.Exists(additionalContent))
        {
            foreach (var characters in Directory.EnumerateDirectories(
                         additionalContent,
                         "Characters",
                         SearchOption.AllDirectories))
            {
                roots.Add(characters);
            }
        }

        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
