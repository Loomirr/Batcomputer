namespace Batcomputer;

internal static class ThemeRegressionChecks
{
    public static void Run(ICollection<string> failures, TextWriter output)
    {
        var classic = Theme.ResolveVisualTheme("Classic");
        var alternate = Theme.ResolveVisualTheme("Alternate");
        var mayhem = Theme.ResolveVisualTheme("Mayhem Mode");
        Check(
            Theme.VisualThemes.Count == 3 &&
            classic.Name == "Classic" &&
            classic.HeaderAsset.Equals("Header.png", StringComparison.OrdinalIgnoreCase) &&
            classic.IconAsset.Equals("Icon.ico", StringComparison.OrdinalIgnoreCase) &&
            alternate.Name == "Alternate" &&
            alternate.HeaderAsset.Equals("header2.png", StringComparison.OrdinalIgnoreCase) &&
            mayhem.Name == "Mayhem Mode" &&
            mayhem.HeaderAsset.Equals("HeaderMayhem.png", StringComparison.OrdinalIgnoreCase) &&
            mayhem.IconAsset.Equals("Mayhem.ico", StringComparison.OrdinalIgnoreCase) &&
            mayhem.Accent != mayhem.SecondaryAccent &&
            classic.Accent != alternate.Accent &&
            Theme.VisualThemes.All(theme =>
                EmbeddedAssets.Exists(theme.HeaderAsset) &&
                EmbeddedAssets.Exists(theme.IconAsset)) &&
            Theme.ResolveVisualTheme("Batcompuper").Name == alternate.Name &&
            Theme.ResolveVisualTheme("Mayhem").Name == mayhem.Name &&
            Theme.ResolveVisualTheme("unknown").Name == classic.Name &&
            new AppSettings().VisualTheme == classic.Name,
            "visual themes bind each saved choice to its header, icon, and accent palette",
            failures,
            output);

        var translucentClassic = Color.FromArgb(47, classic.Accent);
        var remapped = Theme.RemapAccent(translucentClassic, classic, alternate);
        Check(
            remapped.A == translucentClassic.A &&
            remapped.R == alternate.Accent.R &&
            remapped.G == alternate.Accent.G &&
            remapped.B == alternate.Accent.B &&
            Theme.RemapAccent(Theme.Crit, classic, alternate) == Theme.Crit,
            "theme refresh remaps captured accent colors while preserving alpha and semantic status colors",
            failures,
            output);

        var priorSettings = AppSettings.Current;
        try
        {
            AppSettings.Current = new AppSettings { VisualTheme = mayhem.Name };
            Check(
                Theme.Gold == mayhem.Accent &&
                Theme.GoldDim == mayhem.AccentDim &&
                Theme.GoldHover == mayhem.AccentHover &&
                Theme.SecondaryAccent == mayhem.SecondaryAccent &&
                Theme.CurrentVisualTheme.HeaderAsset == mayhem.HeaderAsset &&
                Theme.CurrentVisualTheme.IconAsset == mayhem.IconAsset,
                "the active saved visual theme drives new controls and the header without changing the dark layout",
                failures,
                output);
        }
        finally
        {
            AppSettings.Current = priorSettings;
        }
    }

    private static void Check(
        bool condition,
        string description,
        ICollection<string> failures,
        TextWriter output)
    {
        output.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
        if (!condition)
        {
            failures.Add(description);
        }
    }
}
