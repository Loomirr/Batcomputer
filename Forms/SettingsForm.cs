namespace Batcomputer;

/// <summary>
/// First-time-setup / settings dialog. Reused for both the initial run (when no
/// usable settings file exists) and later edits via the Settings button.
///
/// FModel-style two-pane layout - a left nav rail, a sectioned dark
/// form with rounded inputs + inline "…" browse buttons and status dots, and a footer bar.
/// </summary>
public sealed partial class SettingsForm : Form
{
    private sealed class PathRow
    {
        public required string Key;
        public required string Label;
        public required string Section;
        public required bool IsFile;
        public required string Filter;
        public required Func<AppSettings, string?> Get;
        public required Action<AppSettings, string?> Set;
        public TextBox Box = new();
        public StatusDot Status = new();
    }

    private AppSettings _settings;
    private List<PathRow> _rows;
    private readonly ToolTip _tips = new();

    // Panels swapped by the left nav.
    private Panel? _pathsPanel;
    private Panel? _generalPanel;
    private Panel? _visualPanel;
    private readonly List<(Panel item, Label bar)> _navItems = new();

    public SettingsForm()
    {
        _settings = AppSettings.BuiltInDefaults();
        _rows = new List<PathRow>();
        InitializeComponent();
    }

    public SettingsForm(AppSettings settings, bool firstRun)
    {
        _settings = settings;
        _rows = new List<PathRow>();

        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();
        Theme.StyleTooltip(_tips);

        Text = firstRun ? "Batcomputer — First-time setup" : "Batcomputer — Settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 620);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        _rows = new List<PathRow>
        {
            new() { Key = "OodleRuntimeDllPath", Label = "Oodle runtime (local UE 5.6)", Section = "Tools", IsFile = true, Filter = "Oodle runtime|oo2core*_win64.dll|DLLs|*.dll|All files|*.*",
                Get = s => string.IsNullOrWhiteSpace(s.OodleRuntimeDllPath) ? s.EffectiveOodleRuntimeDllPath() : s.OodleRuntimeDllPath,
                Set = (s, v) => s.OodleRuntimeDllPath = v },
            new() { Key = "UsmapPath", Label = "Mappings (.usmap)", Section = "Tools", IsFile = true, Filter = "Mappings|*.usmap|All files|*.*",
                Get = s => s.UsmapPath, Set = (s, v) => s.UsmapPath = v },
            new() { Key = "UnrealEngineRoot", Label = "Unreal Engine 5.6 (Asset Registry writer)", Section = "Tools", IsFile = false, Filter = "",
                Get = s => string.IsNullOrWhiteSpace(s.UnrealEngineRoot) ? AppSettings.DefaultUnrealEngineRoot() : s.UnrealEngineRoot,
                Set = (s, v) => s.UnrealEngineRoot = v },
            new() { Key = "ProjectRoot", Label = "Workspace folder (blank = beside Batcomputer)", Section = "Project", IsFile = false, Filter = "",
                Get = s => s.ProjectRoot, Set = (s, v) => s.ProjectRoot = v },
            new() { Key = "ExportContentRoot", Label = "Export Content root (staging source)", Section = "Project", IsFile = false, Filter = "",
                Get = s => s.ExportContentRoot, Set = (s, v) => s.ExportContentRoot = v },
            new() { Key = "ExtractedContentRoot", Label = "Extracted game Content", Section = "Project", IsFile = false, Filter = "",
                Get = s => s.ExtractedContentRoot, Set = (s, v) => s.ExtractedContentRoot = v },
            new() { Key = "GamePaksRoot", Label = "Game Content\\Paks (asset refresh source)", Section = "Game", IsFile = false, Filter = "",
                Get = s => s.GamePaksRoot, Set = (s, v) => s.GamePaksRoot = v },
            new() { Key = "AssetExtractRoot", Label = "Extracted assets output (blank = workspace\\Generated\\GameExtracts)", Section = "Project", IsFile = false, Filter = "",
                Get = s => s.AssetExtractRoot, Set = (s, v) => s.AssetExtractRoot = v },
        };

        // --- footer (Dock=Bottom) ---
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.SlateDark };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var hint = new Label
        {
            AutoSize = false, Left = 20, Top = 0, Width = 520, Height = 58,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.OnDarkMuted, Font = Theme.Caption,
            Text = "Status dots: green = found, amber = not set, red = path missing."
        };
        var save = new Button { Width = 108, Height = 34, Top = 12, Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Width = 100, Height = 34, Top = 12, Text = "Cancel", DialogResult = DialogResult.Cancel };
        Theme.StyleGoldButton(save);
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(hint);
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        void LayoutFooter()
        {
            cancel.Left = footer.Width - cancel.Width - 20;
            save.Left = cancel.Left - save.Width - 10;
        }
        footer.Resize += (_, _) => LayoutFooter();

        save.Click += (_, _) =>
        {
            foreach (var row in _rows)
            {
                var value = row.Box.Text.Trim();
                row.Set(_settings, string.IsNullOrWhiteSpace(value) ? null : value);
            }
            _settings.ShowResearchTools = _researchToggle?.Checked ?? _settings.ShowResearchTools;
            _settings.UseMinifigCharacterPanel = _minifigToggle?.Checked ?? _settings.UseMinifigCharacterPanel;
            _settings.AnimationsEnabled = _animationsToggle?.Checked ?? _settings.AnimationsEnabled;
            _settings.KeepPreviousExtracts = _keepExtractsToggle?.Checked ?? _settings.KeepPreviousExtracts;
            _settings.AutoCleanPreviewFiles = _autoCleanPreviewFilesToggle?.Checked ?? _settings.AutoCleanPreviewFiles;
            _settings.VisualTheme = _themePicker?.SelectedItem?.ToString() ?? _settings.VisualTheme;
            // Apply immediately so the change takes effect without a restart.
            Animator.Enabled = _settings.AnimationsEnabled;
            _settings.Save();
        };

        // --- left nav rail (Dock=Left) ---
        var rail = new Panel { Dock = DockStyle.Left, Width = 176, BackColor = Theme.SlateDark };
        rail.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, rail.Width - 1, 0, rail.Width - 1, rail.Height);
        };
        var railTitle = new Label
        {
            AutoSize = false, Left = 18, Top = 18, Width = 150, Height = 26,
            Text = "Settings", Font = Theme.Heading, ForeColor = Theme.OnDark
        };
        rail.Controls.Add(railTitle);

        // --- content host (Dock=Fill) holds the swappable panels ---
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WindowBg };

        _pathsPanel = BuildPathsPanel(firstRun);
        _generalPanel = BuildGeneralPanel();
        _visualPanel = BuildVisualPanel();
        _pathsPanel.Dock = DockStyle.Fill;
        _generalPanel.Dock = DockStyle.Fill;
        _visualPanel.Dock = DockStyle.Fill;
        _generalPanel.Visible = false;
        _visualPanel.Visible = false;
        host.Controls.Add(_pathsPanel);
        host.Controls.Add(_generalPanel);
        host.Controls.Add(_visualPanel);

        var navPaths = BuildNavItem("Paths", 60);
        var navGeneral = BuildNavItem("General", 104);
        var navVisual = BuildNavItem("Visual", 148);
        rail.Controls.Add(navPaths.item);
        rail.Controls.Add(navGeneral.item);
        rail.Controls.Add(navVisual.item);
        navPaths.item.Click += (_, _) => SelectTab(0);
        navGeneral.item.Click += (_, _) => SelectTab(1);
        foreach (Control c in navPaths.item.Controls) c.Click += (_, _) => SelectTab(0);
        foreach (Control c in navGeneral.item.Controls) c.Click += (_, _) => SelectTab(1);
        navVisual.item.Click += (_, _) => SelectTab(2);
        foreach (Control c in navVisual.item.Controls) c.Click += (_, _) => SelectTab(2);

        // Order matters: Fill first, then Left, then Bottom, so docking carves correctly.
        Controls.Add(host);
        Controls.Add(rail);
        Controls.Add(footer);

        AcceptButton = save;
        CancelButton = cancel;
        LayoutFooter();
        SelectTab(0);
    }

    private void SelectTab(int index)
    {
        if (_pathsPanel is not null) _pathsPanel.Visible = index == 0;
        if (_generalPanel is not null) _generalPanel.Visible = index == 1;
        if (_visualPanel is not null) _visualPanel.Visible = index == 2;
        for (var i = 0; i < _navItems.Count; i++)
        {
            var active = i == index;
            _navItems[i].item.BackColor = active ? Theme.Slate : Theme.SlateDark;
            _navItems[i].bar.BackColor = active ? Theme.Gold : Theme.SlateDark;
            foreach (Control c in _navItems[i].item.Controls)
            {
                if (c is Label l && l != _navItems[i].bar) l.ForeColor = active ? Theme.OnDark : Theme.OnDarkMuted;
            }
        }
    }

    private (Panel item, Label bar) BuildNavItem(string text, int top)
    {
        var item = new Panel { Left = 8, Top = top, Width = 160, Height = 38, Cursor = Cursors.Hand, BackColor = Theme.SlateDark };
        var bar = new Label { Left = 0, Top = 6, Width = 3, Height = 26, BackColor = Theme.SlateDark };
        var label = new Label
        {
            AutoSize = false, Left = 16, Top = 0, Width = 138, Height = 38,
            TextAlign = ContentAlignment.MiddleLeft, Text = text, Font = Theme.BodyStrong, ForeColor = Theme.OnDarkMuted
        };
        item.Controls.Add(bar);
        item.Controls.Add(label);
        _navItems.Add((item, bar));
        return (item, bar);
    }

    // Layout constants for a field row within the paths panel.
    private const int RowLabelX = 28, RowLabelW = 196, RowInputX = 232, RowInputW = 420;
    private const int RowBrowseX = 660, RowBrowseW = 38, RowDotX = 706;

    private Panel BuildPathsPanel(bool firstRun)
    {
        var panel = new Panel { AutoScroll = true, BackColor = Theme.WindowBg, Padding = new Padding(0, 12, 0, 12) };

        var intro = new Label
        {
            AutoSize = false, Left = RowLabelX, Top = 16, Width = 690, Height = firstRun ? 40 : 22,
            ForeColor = Theme.OnDarkMuted, Font = Theme.Caption,
            Text = firstRun
                ? "First-time setup: Batcomputer has already found its bundled packaging helper. Select your .usmap and game Content\\Paks folder; the tool can create the required character-asset dump for you. Unreal Engine 5.6 and its Oodle runtime are authoring dependencies that may be configured now or later."
                : "Tool & game paths."
        };
        panel.Controls.Add(intro);

        var y = intro.Bottom + 12;
        foreach (var section in new[] { "Tools", "Project", "Game" })
        {
            panel.Controls.Add(SectionDivider(section.ToUpperInvariant(), y));
            y += 38;
            foreach (var row in _rows.FindAll(r => r.Section == section))
            {
                AddFieldRow(panel, row, y);
                y += 52;
            }
            y += 6;
        }
        return panel;
    }

    private void AddFieldRow(Panel host, PathRow row, int y)
    {
        var label = new Label
        {
            Left = RowLabelX, Top = y + 12, Width = RowLabelW, Height = 20,
            Text = row.Label, ForeColor = Theme.OnDark, Font = Theme.Body, AutoEllipsis = true
        };

        var input = new RoundedPanel
        {
            Left = RowInputX, Top = y + 4, Width = RowInputW, Height = 36,
            CornerRadius = Theme.RadiusSm, BackColor = Theme.Slate, BorderColor = Theme.SlateLight
        };
        row.Box.BorderStyle = BorderStyle.None;
        row.Box.BackColor = Theme.Slate;
        row.Box.ForeColor = Theme.OnDark;
        row.Box.Font = Theme.Body;
        row.Box.Left = 11;
        row.Box.Width = input.Width - 22;
        row.Box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        row.Box.Text = row.Get(_settings) ?? "";
        row.Box.TextChanged += (_, _) => UpdateStatus(row);
        input.Controls.Add(row.Box);
        input.Layout += (_, _) => row.Box.Top = (input.Height - row.Box.Height) / 2;
        row.Box.Top = (input.Height - row.Box.Height) / 2;
        input.Click += (_, _) => row.Box.Focus();

        var browse = new Button { Left = RowBrowseX, Top = y + 4, Width = RowBrowseW, Height = 36, Text = "…" };
        Theme.StyleDarkButton(browse);
        browse.Font = new Font(Theme.Body.FontFamily, 12f, FontStyle.Bold);
        browse.Click += (_, _) => Browse(row);
        _tips.SetToolTip(browse, "Browse…");

        row.Status.Left = RowDotX;
        row.Status.Top = y + 15;
        row.Status.Width = 14;
        row.Status.Height = 14;

        host.Controls.Add(label);
        host.Controls.Add(input);
        host.Controls.Add(browse);
        host.Controls.Add(row.Status);
        UpdateStatus(row);
    }

    private static Label SectionDivider(string text, int y)
    {
        var lbl = new Label
        {
            Left = RowLabelX, Top = y, Width = RowDotX + 14 - RowLabelX, Height = 26,
            BackColor = Theme.WindowBg
        };
        lbl.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var tw = TextRenderer.MeasureText(text, Theme.Eyebrow).Width;
            TextRenderer.DrawText(g, text, Theme.Eyebrow, new Point(0, 8), Theme.Gold);
            using var pen = new Pen(Theme.LineSoft);
            g.DrawLine(pen, tw + 10, lbl.Height / 2, lbl.Width, lbl.Height / 2);
        };
        return lbl;
    }

    private ToggleSwitch? _researchToggle;
    private ToggleSwitch? _minifigToggle;
    private ToggleSwitch? _animationsToggle;
    private ToggleSwitch? _keepExtractsToggle;
    private ToggleSwitch? _autoCleanPreviewFilesToggle;
    private ThemedDropDown? _themePicker;

    private const int RowRightEdge = RowDotX + 14;

    private Panel BuildGeneralPanel()
    {
        var panel = new Panel { AutoScroll = true, BackColor = Theme.WindowBg, Padding = new Padding(0, 12, 0, 12) };

        // Every row advances a cursor. The absolute Tops this used to carry meant adding a row meant
        // hand-editing every Top below it, and one missed edit stacked two buttons on the same pixel.
        var y = 20;

        void Section(string title)
        {
            panel.Controls.Add(SectionDivider(title, y));
            y += 38;
        }

        void ToggleRow(string title, string hint, ToggleSwitch toggle, Color? hintColor = null)
        {
            var hintLeft = RowLabelX;
            var hintTop = y + 28;
            var hintWidth = RowRightEdge - hintLeft;
            var hintHeight = Math.Max(18, TextRenderer.MeasureText(
                hint,
                Theme.Caption,
                new Size(hintWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height);
            panel.Controls.Add(new Label
            {
                Left = RowLabelX, Top = y + 2, Width = 320, Height = 20,
                Text = title, ForeColor = Theme.OnDark, Font = Theme.Body,
            });
            toggle.Left = RowLabelX + 340;
            toggle.Top = y;
            panel.Controls.Add(toggle);
            panel.Controls.Add(new Label
            {
                Left = hintLeft, Top = hintTop, Width = hintWidth, Height = hintHeight,
                Text = hint, ForeColor = hintColor ?? Theme.OnDarkMuted, Font = Theme.Caption,
            });
            y = hintTop + hintHeight + 12;
        }

        void ButtonRow(Button button, string hint)
        {
            button.Left = RowLabelX;
            button.Top = y;
            button.Height = 34;
            panel.Controls.Add(button);
            panel.Controls.Add(new Label
            {
                Left = RowLabelX + button.Width + 14, Top = y + 8, Height = 18,
                Width = Math.Max(0, RowRightEdge - (RowLabelX + button.Width + 14)),
                Text = hint, ForeColor = Theme.OnDarkMuted, Font = Theme.Caption, AutoEllipsis = true,
            });
            y += 46;
        }

        Section("DEVELOPER");
        _researchToggle = new ToggleSwitch { Checked = _settings.ShowResearchTools };
        ToggleRow("Show developer research tools",
            "Extra dump/probe tools for reverse-engineering the game. Off by default.",
            _researchToggle);

        Section("APPEARANCE");
        var artMissing = !MinifigDiagram.HasArt;
        _minifigToggle = new ToggleSwitch { Checked = _settings.UseMinifigCharacterPanel, Enabled = !artMissing };
        ToggleRow("Show the character as a minifig",
            artMissing
                ? "Part art not found (Assets/Parts) - the slot list is used regardless."
                : "Off: use the classic \"Your Character\" slot list instead of the figure.",
            _minifigToggle,
            artMissing ? Theme.Warn : null);

        _keepExtractsToggle = new ToggleSwitch { Checked = _settings.KeepPreviousExtracts };
        ToggleRow("Keep previous asset extracts",
            "Off: a refresh deletes the dump it replaces (each is ~18 GB).",
            _keepExtractsToggle);

        Section("3D VIEWER");
        _autoCleanPreviewFilesToggle = new ToggleSwitch { Checked = _settings.AutoCleanPreviewFiles };
        ToggleRow("Clean generated 3D previews automatically",
            "On: older Generated\\Preview folders are removed before the next preview. Turn it off to keep generated models and textures for inspection.",
            _autoCleanPreviewFilesToggle);

        Section("PATHS");
        // Re-run the guided setup when paths change or a fresh full asset extraction is needed.
        var rerun = new Button { Width = 200, Text = "Run first-time setup again" };
        Theme.StyleDarkButton(rerun);
        rerun.FlatAppearance.BorderColor = Theme.Crit;
        rerun.ForeColor = Theme.Crit;
        rerun.Click += (_, _) =>
        {
            using var wizard = new FirstRunWizard(_settings);
            if (wizard.ShowDialog(this) == DialogResult.OK)
            {
                foreach (var row in _rows)
                {
                    row.Box.Text = row.Get(_settings) ?? "";
                }
                SelectTab(0);
            }
        };
        ButtonRow(rerun, "Walks through every required path, then offers the full first-time game extraction.");

        return panel;
    }

    private Panel BuildVisualPanel()
    {
        var panel = new Panel { AutoScroll = true, BackColor = Theme.WindowBg, Padding = new Padding(0, 12, 0, 12) };
        var y = 20;

        panel.Controls.Add(SectionDivider("THEME", y));
        y += 38;
        panel.Controls.Add(new Label
        {
            Left = RowLabelX, Top = y + 6, Width = 320, Height = 20,
            Text = "Header style", ForeColor = Theme.OnDark, Font = Theme.Body,
        });
        _themePicker = new ThemedDropDown
        {
            Left = RowLabelX + 340, Top = y, Width = 260,
            Placeholder = "Choose a theme",
        };
        _themePicker.Items.Add("Classic");
        _themePicker.Items.Add("Alternate");
        _themePicker.SelectedItem = string.Equals(_settings.VisualTheme, "Batcompuper", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(_settings.VisualTheme, "Alternate", StringComparison.OrdinalIgnoreCase)
            ? "Alternate"
            : "Classic";
        panel.Controls.Add(_themePicker);
        panel.Controls.Add(new Label
        {
            Left = RowLabelX, Top = y + 42, Width = RowRightEdge - RowLabelX, Height = 34,
            Text = "Alternate uses header2.png. All other colors and controls stay the same.",
            ForeColor = Theme.OnDarkMuted, Font = Theme.Caption,
        });
        y += 94;

        panel.Controls.Add(SectionDivider("MOTION", y));
        y += 38;
        _animationsToggle = new ToggleSwitch { Checked = _settings.AnimationsEnabled };
        panel.Controls.Add(new Label
        {
            Left = RowLabelX, Top = y + 2, Width = 320, Height = 20,
            Text = "Enable animations", ForeColor = Theme.OnDark, Font = Theme.Body,
        });
        _animationsToggle.Left = RowLabelX + 340;
        _animationsToggle.Top = y;
        panel.Controls.Add(_animationsToggle);
        panel.Controls.Add(new Label
        {
            Left = RowLabelX, Top = y + 28, Width = RowRightEdge - RowLabelX, Height = 34,
            Text = "Off: hovers, toggles, tiles, and animated UI details stop moving.",
            ForeColor = Theme.OnDarkMuted, Font = Theme.Caption,
        });

        return panel;
    }

    private void Browse(PathRow row)
    {
        if (row.IsFile)
        {
            using var dlg = new OpenFileDialog { Filter = string.IsNullOrEmpty(row.Filter) ? "All files|*.*" : row.Filter };
            if (!string.IsNullOrWhiteSpace(row.Box.Text) && File.Exists(row.Box.Text))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(row.Box.Text);
                dlg.FileName = Path.GetFileName(row.Box.Text);
            }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                row.Box.Text = dlg.FileName;
            }
        }
        else
        {
            using var dlg = new FolderBrowserDialog();
            if (!string.IsNullOrWhiteSpace(row.Box.Text) && Directory.Exists(row.Box.Text))
            {
                dlg.InitialDirectory = row.Box.Text;
            }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                row.Box.Text = dlg.SelectedPath;
            }
        }
    }

    private void UpdateStatus(PathRow row)
    {
        var value = row.Box.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            // No built-in defaults: every install's paths are different, so blank means "not set"
            // rather than silently falling back to a path that only exists on one machine.
            row.Status.DotColor = Theme.Warn;
            _tips.SetToolTip(row.Status, "Not set — pick a path with Browse.");
            return;
        }

        var exists = row.IsFile ? File.Exists(value) : Directory.Exists(value);
        row.Status.DotColor = exists ? Theme.Good : Theme.Crit;
        _tips.SetToolTip(row.Status, exists ? "Found" : (row.IsFile ? "File not found" : "Folder not found"));
    }
}
