namespace Batcomputer;

internal static class EquipmentUiTextureCatalog
{
    internal const string ImportKind = "Equipment icon (BCA + SDF)";
    internal const string SdfKind = "Equipment HUD / upgrade icon (SDF)";
    internal const string SdfProfile = "ui-equipment-sdf-64-bgra8";
    internal const string AlphaKind = "Equipment HUD icon (transparent PNG)";
    internal const string ColorKind = "Equipment color icon (BCA)";
    internal const string AccentKind = "Equipment SDF icon (white + green PNG)";
    internal const string AccentProfile = "ui-equipment-green-to-sdf-64-bgra8";
    internal static IReadOnlyList<string> NewImportKinds { get; } = new[] { ColorKind, AccentKind };
    // Saved alpha-only, prepared-SDF and paired imports retain their original interpretation.
    internal static bool IsEquipmentIcon(string? kind) => new[] { ImportKind, SdfKind, AlphaKind, ColorKind, AccentKind }.Contains(kind, StringComparer.OrdinalIgnoreCase);
    internal static string Format(string kind, string profile) => kind == ColorKind || profile == "ui-equipment-bca-256-bgra8" ? "BCA" :
        kind == AlphaKind || kind == SdfKind || kind == AccentKind || profile == AccentProfile || profile == SdfProfile || profile == "ui-equipment-alpha-to-sdf-64-bgra8" ? "SDF" : "Other UI";
    internal sealed record Entry(string Name, string Owner, string Package, string Profile)
    {
        public override string ToString() => $"{Profile} · {Name} — {Owner}";
    }
    internal static IReadOnlyList<Entry> Discover(string projectRoot, NativeSuitProject? current)
    {
        var service = new SuitProjectService(projectRoot);
        var projects = new List<NativeSuitProject>();
        if (current is not null) projects.Add(current);
        foreach (var summary in service.ListProjects())
        {
            var project = service.LoadProject(summary.Path);
            if (project is not null && project.SlotId != current?.SlotId) projects.Add(project);
        }
        return Entries(projects);
    }
    internal static IReadOnlyList<Entry> Entries(IEnumerable<NativeSuitProject> projects) => projects
        .SelectMany(project => project.GeneratedTextures
            .Where(texture => MainForm.IsUiTextureKind(texture.Kind) || IsEquipmentIcon(texture.Kind))
            .Where(texture => HeldItemService.ValidPackage(texture.PackagePath))
            .Select(texture => new Entry(texture.DisplayName, project.DisplayName,
                UnrealPathUtil.NormalizePackagePath(texture.PackagePath), Format(texture.Kind, texture.CookProfile))))
        .DistinctBy(entry => entry.Package, StringComparer.OrdinalIgnoreCase)
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Owner).ToArray();
}
