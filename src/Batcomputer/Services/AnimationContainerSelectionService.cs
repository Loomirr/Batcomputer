namespace Batcomputer;

/// <summary>
/// Resolves any file selected from a cooked IoStore animation package back to the complete
/// container. Authors should not need to know which member of the trio Batcomputer uses.
/// </summary>
public static class AnimationContainerSelectionService
{
    public sealed record Selection(
        string BasePath,
        string UtocPath,
        string UcasPath,
        string? PakPath)
    {
        public string DisplayName => Path.GetFileName(BasePath);
        public IReadOnlyList<string> Files => string.IsNullOrWhiteSpace(PakPath)
            ? [UtocPath, UcasPath]
            : [UtocPath, UcasPath, PakPath];
    }

    private static readonly HashSet<string> SupportedExtensions = new(
        [".utoc", ".ucas", ".pak"],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string? selectedPath, out Selection? selection, out string error)
    {
        selection = null;
        error = "";
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            error = "Choose a .utoc, .ucas, or .pak file from the cooked animation package.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(selectedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "That animation package path is not valid.";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            error = "Choose any .utoc, .ucas, or .pak file from the cooked animation package.";
            return false;
        }
        if (!File.Exists(fullPath))
        {
            error = $"The selected file no longer exists:\n{fullPath}";
            return false;
        }

        var basePath = fullPath[..^extension.Length];
        var utoc = basePath + ".utoc";
        var ucas = basePath + ".ucas";
        var missing = new[] { utoc, ucas }.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            error =
                "This cooked animation package is incomplete. Keep its .utoc and .ucas together in the same folder, then retry.\n\nMissing:\n" +
                string.Join("\n", missing.Select(Path.GetFileName));
            return false;
        }

        var pak = basePath + ".pak";
        selection = new Selection(
            basePath,
            utoc,
            ucas,
            File.Exists(pak) ? pak : null);
        return true;
    }
}
