namespace Batcomputer;

/// <summary>
/// Finds the logical character package roots in an extracted game Content folder without walking
/// the entire multi-gigabyte dump. Base-game characters are direct children of /Game, Batcave
/// display assets may be below /Game/AdditionalContent, and actual DLC playables live in sibling
/// Game Feature mounts such as /DLC_BeyondPack/Characters.
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
        foreach (var mount in ExtractedPackagePathService.EnumerateMounts(contentRoot))
        {
            var directCharacters = Path.Combine(mount.ContentRoot, "Characters");
            if (Directory.Exists(directCharacters))
            {
                roots.Add(directCharacters);
            }

            var additionalContent = Path.Combine(mount.ContentRoot, "AdditionalContent");
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
        }

        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
