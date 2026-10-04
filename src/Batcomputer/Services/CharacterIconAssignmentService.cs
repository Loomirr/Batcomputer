namespace Batcomputer;

/// <summary>Validated roles and atomic project changes for one or all studio icons.</summary>
internal static class CharacterIconAssignmentService
{
    internal static readonly string[] Roles = ["menu", "left", "right", "suit"];
    internal static int Size(string role) => role == "suit" ? 256 : Roles.Contains(role, StringComparer.Ordinal)
        ? 512 : throw new InvalidDataException("Unknown character icon role.");
    internal static string TextureModFolder(NativeSuitProject project)
    {
        var package = UnrealPathUtil.NormalizePackagePath(project.TargetPackages?.Playable);
        const string prefix = "/Game/Mods/";
        if (!package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save valid generated playable target packages before assigning studio icons.");
        var remaining = package[prefix.Length..];
        var slash = remaining.IndexOf('/');
        if (slash <= 0 || slash == remaining.Length - 1)
            throw new InvalidDataException("The playable target has no mod-owned asset folder.");
        return remaining[..slash];
    }
    internal static void Validate(IReadOnlyDictionary<string, byte[]> icons)
    {
        if (icons.Count != 1 && (icons.Count != 4 || !Roles.All(icons.ContainsKey)))
            throw new InvalidDataException("Assign one selected icon or the complete four-icon set.");
        foreach (var (role, png) in icons) SuitIconDryRunService.ValidatePngHeader(png, Size(role));
    }
    internal static void AssignAndSave(NativeSuitProject project, IReadOnlyDictionary<string, GeneratedTextureEntry> entries, Action save)
    {
        var texturePrefix = $"/Game/Mods/{TextureModFolder(project)}/Textures/";
        if (entries.Count is not (1 or 4) || entries.Keys.Any(role => !Roles.Contains(role, StringComparer.Ordinal)) ||
            (entries.Count == 4 && !Roles.All(entries.ContainsKey)) ||
            entries.Values.Any(entry => !entry.PackagePath.StartsWith(texturePrefix, StringComparison.OrdinalIgnoreCase) ||
                entry.PackagePath != UnrealPathUtil.NormalizePackagePath(entry.PackagePath)) ||
            entries.Values.Select(entry => entry.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
            entries.Values.Any(entry => project.GeneratedTextures.Any(old => old.PackagePath.Equals(entry.PackagePath, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Conflicting or incomplete icon assignment.");
        var before = (project.IconMenu, project.IconLeft, project.IconRight, project.IconSuit);
        try
        {
            foreach (var (role, entry) in entries)
            {
                project.GeneratedTextures.Add(entry);
                switch (role) { case "menu": project.IconMenu = entry.PackagePath; break;
                    case "left": project.IconLeft = entry.PackagePath; break;
                    case "right": project.IconRight = entry.PackagePath; break;
                    case "suit": project.IconSuit = entry.PackagePath; break; }
            }
            save();
        }
        catch
        {
            (project.IconMenu, project.IconLeft, project.IconRight, project.IconSuit) = before;
            foreach (var entry in entries.Values) project.GeneratedTextures.Remove(entry);
            throw;
        }
    }
}
