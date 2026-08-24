namespace Batcomputer;

/// <summary>
/// Small, non-user-facing hooks used by the release screenshot audit. Keeping the navigation in
/// MainForm means the audit exercises the real workspace switching code instead of recreating it.
/// </summary>
public sealed partial class MainForm
{
    internal static IReadOnlyList<string> UiAuditSurfaceNames { get; } = new[]
    {
        "Home - Mods",
        "Home - Suits",
        "Home - Build mod",
        "Home - Review",
        "Suits - Home",
        "Suits - Base",
        "Suits - Materials",
        "Suits - All tool materials",
        "Suits - Faces",
        "Suits - Textures",
        "Suits - Parts",
        "Suits - Equipment",
        "Suits - Gliders",
        "Suits - Animations",
        "Suits - Mod notebook",
        "3D viewer",
    };

    internal void SelectUiAuditSurface(string name)
    {
        switch (name)
        {
            case "Home - Mods":
                SelectHomeWorkspaceSection(HomeWorkspaceSection.Mods);
                break;
            case "Home - Suits":
                SelectHomeWorkspaceSection(HomeWorkspaceSection.Suits);
                break;
            case "Home - Build mod":
                SelectHomeWorkspaceSection(HomeWorkspaceSection.BuildMod);
                break;
            case "Home - Review":
                SelectHomeWorkspaceSection(HomeWorkspaceSection.Review);
                break;
            case "3D viewer":
                SelectWorkspaceFolder(WorkspaceFolder.Viewer);
                break;
            case "Suits - Mod notebook":
                SelectWorkspaceFolder(WorkspaceFolder.Suits, refresh: false);
                SelectComboValue(_toyboxCategoryCombo, "Materials");
                RefreshToyboxTiles();
                _inspectorTabs.SelectTab(NotebookTabName);
                ConfigureModNotebookForUiAudit();
                break;
            case "Suits - All tool materials":
                SelectWorkspaceFolder(WorkspaceFolder.Suits, refresh: false);
                SelectComboValue(_toyboxCategoryCombo, "Materials");
                SelectComboValue(_toyboxTypeCombo, "All tool materials");
                ShowVirtualTiles(
                [
                    new VirtualTilePanel.Tile
                    {
                        Section = "TOOL MATERIAL LIBRARY",
                        Title = "ClassicSuit_Body",
                        Subtitle = "shared tool MI · drag to apply",
                        Accent = Theme.Materials,
                    },
                    new VirtualTilePanel.Tile
                    {
                        Section = "TOOL MATERIAL LIBRARY",
                        Title = "ClassicSuit_Cowl",
                        Subtitle = "shared tool MI · drag to apply",
                        Accent = Theme.Materials,
                    },
                    new VirtualTilePanel.Tile
                    {
                        Section = "TOOL MATERIAL LIBRARY",
                        Title = "NeutralFace_Print",
                        Subtitle = "shared face MI · apply to Face",
                        Accent = Theme.Faces,
                    },
                ],
                header: "Every material created by the tool in this workspace. Drag one onto the current suit or right-click to apply/edit it; packaging brings the referenced cooked material into this suit automatically.");
                break;
            default:
                SelectWorkspaceFolder(WorkspaceFolder.Suits, refresh: false);
                var separator = name.IndexOf(" - ", StringComparison.Ordinal);
                var category = separator >= 0 ? name[(separator + 3)..] : "Home";
                SelectComboValue(_toyboxCategoryCombo, category);
                RefreshToyboxTiles();
                break;
        }
    }
}
