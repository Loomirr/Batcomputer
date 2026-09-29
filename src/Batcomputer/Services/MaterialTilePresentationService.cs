namespace Batcomputer;

internal static class MaterialTilePresentationService
{
    internal sealed record Details(string Title, string Subtitle, string ToolTip);

    internal static Details Describe(string packagePath, GeneratedMaterialEntry? entry, bool isFace, bool shared = false)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var name = UnrealPathUtil.AssetName(package);
        var role = entry?.TemplateOutputRole?.Trim() ?? "";
        // Old generated pairs may have lost their recipe metadata. Only the precise
        // authored suffixes identify these roles; never merge packages by display name.
        if (role.Length == 0 && name.EndsWith("_EoM", StringComparison.OrdinalIgnoreCase)) role = "gameplay";
        if (role.Length == 0 && name.EndsWith("_CUT", StringComparison.OrdinalIgnoreCase)) role = "cutscene";
        var label = role.ToLowerInvariant() switch
        {
            "gameplay" => "Gameplay",
            "cutscene" => "Cutscene",
            _ => "",
        };
        var subtitle = label.Length > 0
            ? label + (shared ? " · shared MI" : " · your MI")
            : isFace ? "your face MI · apply to Face" : shared ? "shared tool MI · drag to apply" : "your MI · drag to apply";
        var description = label.Length > 0 ? label + " material" : isFace ? "Face material" : "Material";
        var tooltip = description + "\n" + package;
        if (!string.IsNullOrWhiteSpace(entry?.SourceMaterialPackagePath)) tooltip += "\nSource: " + entry.SourceMaterialPackagePath;
        if (!string.IsNullOrWhiteSpace(entry?.TemplateGroupId))
            tooltip += "\nGenerated as a paired recipe; gameplay and cutscene use separate native material parents.";
        return new(name.Replace("MI_", ""), subtitle, tooltip);
    }
}
