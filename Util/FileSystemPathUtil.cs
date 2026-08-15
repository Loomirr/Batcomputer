namespace Batcomputer;

internal static class FileSystemPathUtil
{
    /// <summary>
    /// True when <paramref name="candidate"/> resolves to the root itself (when allowed) or to a
    /// child path. The trailing separator prevents sibling-prefix paths such as GeneratedBackup
    /// from being accepted as children of Generated.
    /// </summary>
    public static bool IsWithinDirectory(string candidate, string root, bool allowRoot = false)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowRoot && fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
