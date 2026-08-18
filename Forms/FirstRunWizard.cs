namespace Batcomputer;

/// <summary>
/// Guided first-time setup. The Settings dialog shows every path at once, which is the right shape
/// for editing but a poor first experience - this walks one path at a time, explains what each is
/// for and where to find it, and validates as you type.
///
/// Re-runnable from Settings → General → "Run first-time setup again".
/// </summary>
public sealed class FirstRunWizard : Form
{
    private sealed class Step
    {
        public required string Title;
        public required string Blurb;
        public required string Hint;
        public required bool IsFile;
        public string Filter = "";
        public required Func<AppSettings, string?> Get;
        public required Action<AppSettings, string?> Set;
        /// <summary>Setup can finish without this one.</summary>
        public bool Optional;
    }

    private readonly AppSettings _settings;
    private readonly List<Step> _steps;
    private int _index;

    private readonly Label _stepCount = new();
    private readonly Label _title = new();
    private readonly Label _blurb = new();
    private readonly RoundedPanel _inputWrap = new();
    private readonly TextBox _input = new();
    private readonly Button _browse = new();
    private readonly StatusDot _dot = new();
    private readonly Label _status = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Button _skip = new();
    private readonly ThemedProgressBar _bar = new();
    private readonly ToolTip _tips = new();

    /// <summary>
    /// Set when setup has all prerequisites but no usable extracted dump, and the user chooses the
    /// full character-asset extraction. Program starts the existing refresh pipeline after MainForm opens.
    /// </summary>
    public bool InitialExtractionRequested { get; private set; }

    /// <summary>Set when setup can prepare the local UE registry writer before the first build.</summary>
    public bool RegistryWriterPreparationRequested { get; private set; }

    public FirstRunWizard(AppSettings settings)
    {
        _settings = settings;
        _steps = new List<Step>
        {
            new()
            {
                Title = "Workspace folder",
                Blurb = "Batcomputer's packaging helper and registry-writer source are already included. " +
                        "Choose where projects, extracted assets and builds should live, or leave this blank for a portable workspace beside the app.",
                Hint = "Blank = beside Batcomputer.exe. A separate writable drive is useful for the large game-asset extraction.",
                Optional = true,
                IsFile = false,
                Get = s => s.ProjectRoot,
                Set = (s, v) => s.ProjectRoot = v,
            },
            new()
            {
                Title = "Mappings (.usmap)",
                Blurb = "Choose the current mappings file for the installed game version. Batcomputer uses it to read and write cooked assets and copies it into Data\\Mappings.",
                Hint = "A current .usmap dumped from the game, such as Dinner.usmap.",
                IsFile = true,
                Filter = "Mappings|*.usmap|All files|*.*",
                Get = s => s.UsmapPath,
                Set = (s, v) => s.UsmapPath = v,
            },
            new()
            {
                Title = "Game Content\\Paks folder",
                Blurb = "Select the game's Content\\Paks folder. Batcomputer reads it during the one-click asset refresh; it never modifies the shipped game containers.",
                Hint = "…\\LEGO Batman - Legacy of the Dark Knight\\LEGOBatmanLotDK\\Content\\Paks",
                IsFile = false,
                Get = s => s.GamePaksRoot,
                Set = (s, v) => s.GamePaksRoot = v,
            },
            new()
            {
                Title = "Extracted game Content",
                Blurb = "Already have a current unpacked Content dump? Select it here. Otherwise leave this blank and setup will offer the complete character-related extraction automatically. Budget about 18 GB of free space.",
                Hint = "Optional: the Content folder itself, or Content\\Characters\\Minifig.",
                Optional = true,
                IsFile = false,
                Get = s => s.ExtractedContentRoot,
                Set = (s, v) => s.ExtractedContentRoot = v,
            },
            new()
            {
                Title = "Unreal Engine 5.6 (optional for now)",
                Blurb = "Mod authors need UE 5.6 when Batcomputer writes a startup Asset Registry. Players do not need Unreal. You may skip this while browsing assets, but a complete native mod build requires it.",
                Hint = "Usually C:\\Program Files\\Epic Games\\UE_5.6",
                Optional = true,
                IsFile = false,
                Get = s => string.IsNullOrWhiteSpace(s.UnrealEngineRoot) ? AppSettings.DefaultUnrealEngineRoot() : s.UnrealEngineRoot,
                Set = (s, v) => s.UnrealEngineRoot = v,
            },
            new()
            {
                Title = "Oodle runtime (optional)",
                Blurb = "Compact packages use oo2core_9_win64.dll from your own UE 5.6 installation. Batcomputer detects it automatically when possible and never copies it into a mod or release.",
                Hint = "Usually ...\\UE_5.6\\Engine\\Binaries\\DotNET\\AutomationTool\\oo2core_9_win64.dll.",
                IsFile = true,
                Filter = "Oodle runtime|oo2core*_win64.dll|DLLs|*.dll|All files|*.*",
                Optional = true,
                Get = s => string.IsNullOrWhiteSpace(s.OodleRuntimeDllPath) ? s.EffectiveOodleRuntimeDllPath() : s.OodleRuntimeDllPath,
                Set = (s, v) => s.OodleRuntimeDllPath = v,
            },
        };

        BuildChrome();
        LoadStep();
    }

    /// <summary>Uses a short, neutral example path for documentation screenshots.</summary>
    internal void ConfigureForUiAudit()
    {
        _input.Text = @"C:\BatcomputerWorkspace";
        _input.SelectionStart = 0;
        _input.SelectionLength = 0;
        _dot.DotColor = Theme.Good;
        _status.Text = "Example workspace.";
    }

    private void BuildChrome()
    {
        Text = "Batcomputer — Setup";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 380);
        MinimumSize = new Size(620, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Theme.StyleTooltip(_tips);

        const int Pad = 26;
        var w = ClientSize.Width - Pad * 2;

        _stepCount.SetBounds(Pad, 22, w, 16);
        _stepCount.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _stepCount.Font = Theme.Eyebrow;
        _stepCount.ForeColor = Theme.Gold;

        _title.SetBounds(Pad, 42, w, 30);
        _title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _title.Font = AppFonts.Condensed(15f, FontStyle.Bold);
        _title.ForeColor = Theme.OnDark;

        _blurb.SetBounds(Pad, 76, w, 60);
        _blurb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _blurb.Font = Theme.Body;
        _blurb.ForeColor = Theme.OnDarkMuted;

        _inputWrap.SetBounds(Pad, 146, w - 104, 36);
        _inputWrap.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _inputWrap.BackColor = Theme.Slate;
        _inputWrap.BorderColor = Theme.SlateLight;
        _inputWrap.CornerRadius = Theme.RadiusSm;
        _input.SetBounds(11, 0, _inputWrap.Width - 22, 20);
        _input.BorderStyle = BorderStyle.None;
        _input.BackColor = Theme.Slate;
        _input.ForeColor = Theme.OnDark;
        _input.Font = Theme.Body;
        _input.TextChanged += (_, _) => UpdateValidation();
        _inputWrap.Controls.Add(_input);
        _inputWrap.Layout += (_, _) => _input.Top = (_inputWrap.Height - _input.Height) / 2;

        _browse.SetBounds(Pad + w - 96, 146, 96, 36);
        _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browse.Text = "Browse…";
        Theme.StyleDarkButton(_browse);
        _browse.Click += (_, _) => Browse();

        _dot.SetBounds(Pad + 2, 196, 10, 10);
        _status.SetBounds(Pad + 18, 190, w - 20, 20);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _status.Font = Theme.Caption;
        _status.ForeColor = Theme.OnDarkMuted;

        var hintLabel = new Label
        {
            Name = "hint",
            Bounds = new Rectangle(Pad, 216, w, 34),
            Font = Theme.Caption,
            ForeColor = Theme.Blend(Theme.OnDarkMuted, Theme.WindowBg, 0.75),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _bar.SetBounds(Pad, 268, w, 8);
        _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _bar.BackColor = Theme.WindowBg;
        _bar.Maximum = _steps.Count;

        _back.SetBounds(Pad, 16, 90, 32);
        _back.Text = "Back";
        Theme.StyleDarkButton(_back);
        _back.Click += (_, _) => GoTo(-1);

        _next.Width = 130;
        _next.Text = "Next";
        Theme.StyleGoldButton(_next);
        _next.Click += (_, _) => GoTo(+1);

        _skip.Width = 88;
        _skip.Text = "Skip";
        Theme.StyleDarkButton(_skip);
        _skip.Click += (_, _) => { Save(); GoTo(+1, skip: true); };

        var footer = DialogActionFooter.Create(_next, _skip);
        footer.Height = 64;
        if (footer.Controls.OfType<FlowLayoutPanel>().FirstOrDefault() is { } actions)
        {
            actions.Padding = new Padding(Pad, 16, Pad, 16);
        }
        footer.Controls.Add(_back);
        _back.BringToFront();

        Controls.AddRange(new Control[]
        {
            _stepCount, _title, _blurb, _inputWrap, _browse, _dot, _status, hintLabel, _bar, footer
        });
        AcceptButton = _next;
    }

    private Step Current => _steps[_index];

    private void LoadStep()
    {
        var s = Current;
        _stepCount.Text = $"STEP {_index + 1} OF {_steps.Count}";
        _title.Text = s.Title;
        _blurb.Text = s.Blurb;
        if (Controls.Find("hint", false).FirstOrDefault() is Label hint)
        {
            hint.Text = s.Hint;
        }
        _input.Text = s.Get(_settings) ?? "";
        _bar.Value = _index + 1;
        _back.Enabled = _index > 0;
        _skip.Visible = s.Optional;
        _next.Text = _index == _steps.Count - 1 ? "Finish" : "Next";
        UpdateValidation();
        _input.Select();
    }

    private bool CurrentValid()
    {
        var v = _input.Text.Trim();
        if (v.Length == 0) return false;
        return Current.IsFile ? File.Exists(v) : Directory.Exists(v);
    }

    private void UpdateValidation()
    {
        var v = _input.Text.Trim();
        if (v.Length == 0)
        {
            _dot.DotColor = Theme.Warn;
            _status.Text = Current.Optional ? "Not set — you can skip this and add it later." : "Not set yet.";
        }
        else if (CurrentValid())
        {
            _dot.DotColor = Theme.Good;
            _status.Text = Current.IsFile ? "File found." : "Folder found.";
        }
        else
        {
            _dot.DotColor = Theme.Crit;
            _status.Text = Current.IsFile ? "No file at that path." : "No folder at that path.";
        }
        // Never hard-block: a wrong path is recoverable, and trapping someone in setup is worse.
        _next.Enabled = true;
    }

    private void Browse()
    {
        if (Current.IsFile)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = string.IsNullOrEmpty(Current.Filter) ? "All files|*.*" : Current.Filter,
            };
            var cur = _input.Text.Trim();
            if (cur.Length > 0 && File.Exists(cur))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(cur);
                dlg.FileName = Path.GetFileName(cur);
            }
            if (dlg.ShowDialog(this) == DialogResult.OK) _input.Text = dlg.FileName;
        }
        else
        {
            using var dlg = new FolderBrowserDialog();
            var cur = _input.Text.Trim();
            if (cur.Length > 0 && Directory.Exists(cur)) dlg.InitialDirectory = cur;
            if (dlg.ShowDialog(this) == DialogResult.OK) _input.Text = dlg.SelectedPath;
        }
    }

    private void Save()
    {
        var v = _input.Text.Trim();
        Current.Set(_settings, v.Length == 0 ? null : v);
    }

    private void GoTo(int delta, bool skip = false)
    {
        if (delta > 0 && !skip)
        {
            Save();
            if (!CurrentValid() && !Current.Optional)
            {
                var proceed = Dialog.Confirm(this,
                    $"{Current.Title} isn't set",
                    _input.Text.Trim().Length == 0
                        ? "You can continue and set it later in Settings, but the tool won't be able to use it yet."
                        : "That path doesn't exist. You can continue and fix it later in Settings.",
                    confirmText: "Continue anyway", cancelText: "Go back");
                if (!proceed) return;
            }
        }
        else if (delta < 0)
        {
            Save();
        }

        var next = _index + delta;
        if (next < 0) return;
        if (next >= _steps.Count)
        {
            Finish();
            return;
        }
        _index = next;
        LoadStep();
    }

    private void Finish()
    {
        _settings.Save();
        RegistryWriterPreparationRequested = RegistryPluginService.NeedsWriterPreparation();
        var missing = _steps.Where(s => !s.Optional)
            .Where(s => { var v = s.Get(_settings); return string.IsNullOrWhiteSpace(v) || !(s.IsFile ? File.Exists(v) : Directory.Exists(v)); })
            .Select(s => s.Title)
            .ToList();

        if (missing.Count == 0)
        {
            InitialExtractionRequested = NeedsInitialExtraction() && Dialog.Confirm(this,
                "First-time game extraction",
                $"Batcomputer is ready to extract every character, shared animation, and localisation asset needed by the builder.\n\n" +
                $"This uses about 18 GB and can take a while. The extract will be stored under:\n{_settings.EffectiveAssetExtractRoot()}\n\n" +
                "The extraction is read-only against your game files. It validates the result and builds the indexes before you start editing.",
                confirmText: "Extract assets", cancelText: "Finish without extracting", severity: Dialog.Level.Warn,
                windowTitle: "Batcomputer - Setup");

            var setupMessage = InitialExtractionRequested
                ? "Setup is saved. The full first-time extraction will begin as Batcomputer opens."
                : "Everything is pointed at a real path. You're ready to build.";
            if (RegistryWriterPreparationRequested)
            {
                setupMessage += "\n\nBatcomputer will now prepare and verify the UE 5.6 registry writer for future mod builds.";
            }
            Dialog.Success(this, "Setup complete",
                setupMessage,
                "Batcomputer - Setup");
        }
        else
        {
            Dialog.Warn(this, "Setup saved, with gaps",
                "These still need a valid path before you can build or package:\n\n  " +
                string.Join("\n  ", missing) +
                "\n\nSet them any time in Settings.", "Batcomputer — Setup");
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool NeedsInitialExtraction()
    {
        var contentRoot = _settings.EffectiveExtractedContentRoot();
        return !Directory.Exists(Path.Combine(contentRoot, "Characters"));
    }
}
