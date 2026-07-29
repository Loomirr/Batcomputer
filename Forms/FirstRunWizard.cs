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
                Title = "retoc (included)",
                Blurb = "Batcomputer includes an Oodle-capable retoc helper for extracting game assets and packing mod trios. " +
                        "Keep the detected copy unless you deliberately use a different retoc build.",
                Hint = "...\\Tools\\retoc-oodle\\retoc.exe. This bundled helper handles normal and compact releases.",
                IsFile = true,
                Filter = "retoc.exe|retoc.exe|Executables|*.exe|All files|*.*",
                Get = s => string.IsNullOrWhiteSpace(s.RetocExePath) ? AppSettings.DefaultRetocExePath() : s.RetocExePath,
                Set = (s, v) => s.RetocExePath = v,
            },
            new()
            {
                Title = "Oodle packer (optional)",
                Blurb = "Compact mod releases use Batcomputer's Oodle-enabled retoc helper. " +
                        "Portable author installs include this helper; it does not contain the proprietary Oodle runtime.",
                Hint = "...\\Tools\\retoc-oodle\\retoc.exe. Skip until you have the compact-release helper.",
                IsFile = true,
                Filter = "retoc.exe|retoc.exe|Executables|*.exe|All files|*.*",
                Optional = true,
                Get = s => string.IsNullOrWhiteSpace(s.OodleRetocExePath) ? AppSettings.DefaultOodleRetocExePath() : s.OodleRetocExePath,
                Set = (s, v) => s.OodleRetocExePath = v,
            },
            new()
            {
                Title = "Oodle runtime (optional)",
                Blurb = "Choose oo2core_9_win64.dll from your own local UE 5.6 install. " +
                        "Batcomputer uses it to make compact packages but never copies it into a mod or release.",
                Hint = "Usually ...\\UE_5.6\\Engine\\Binaries\\DotNET\\AutomationTool\\oo2core_9_win64.dll.",
                IsFile = true,
                Filter = "Oodle runtime|oo2core*_win64.dll|DLLs|*.dll|All files|*.*",
                Optional = true,
                Get = s => string.IsNullOrWhiteSpace(s.OodleRuntimeDllPath) ? s.EffectiveOodleRuntimeDllPath() : s.OodleRuntimeDllPath,
                Set = (s, v) => s.OodleRuntimeDllPath = v,
            },
            new()
            {
                Title = "Unreal Engine 5.6 (optional for now)",
                Blurb = "Batcomputer uses UE 5.6 only when building a mod's startup Asset Registry plugin. " +
                        "Players who install your finished mod do not need Unreal. You can configure this later, " +
                        "but Build Mod needs it before it can create a complete native release.",
                Hint = "Usually C:\\Program Files\\Epic Games\\UE_5.6",
                IsFile = false,
                Optional = true,
                Get = s => string.IsNullOrWhiteSpace(s.UnrealEngineRoot) ? AppSettings.DefaultUnrealEngineRoot() : s.UnrealEngineRoot,
                Set = (s, v) => s.UnrealEngineRoot = v,
            },
            new()
            {
                Title = "SuitSlotsRegistryWriter project (optional for now)",
                Blurb = "This small UE project writes and round-trip verifies the cooked AssetRegistry.bin " +
                        "for every enabled suit in a mod. Keep it beside Batcomputer under Tools when making a portable author install.",
                Hint = "…\\Tools\\SuitSlotsRegistryWriter\\SuitSlotsRegistryWriter.uproject",
                IsFile = true,
                Filter = "Unreal project|*.uproject|All files|*.*",
                Optional = true,
                Get = s => string.IsNullOrWhiteSpace(s.RegistryWriterProjectPath) ? AppSettings.DefaultRegistryWriterProjectPath() : s.RegistryWriterProjectPath,
                Set = (s, v) => s.RegistryWriterProjectPath = v,
            },
            new()
            {
                Title = "Mappings (.usmap)",
                Blurb = "Tells the tool how the game's assets are laid out. Needed to read and write " +
                        "anything from the game. Batcomputer copies the selected mapping into its own Data\\Mappings folder.",
                Hint = "A .usmap file dumped from the game — e.g. Dinner.usmap.",
                IsFile = true,
                Filter = "Mappings|*.usmap|All files|*.*",
                Get = s => s.UsmapPath, Set = (s, v) => s.UsmapPath = v,
            },
            new()
            {
                Title = "Game Content\\Paks folder",
                Blurb = "Your LEGO Batman install's Paks folder. The tool reads the shipped game data " +
                        "from here when refreshing assets.",
                Hint = "…\\LEGO Batman - Legacy of the Dark Knight\\LEGOBatmanLotDK\\Content\\Paks",
                IsFile = false,
                Get = s => s.GamePaksRoot, Set = (s, v) => s.GamePaksRoot = v,
            },
            new()
            {
                Title = "Extracted game Content",
                Blurb = "An unpacked Content dump. This is what the part index, materials browser and " +
                        "base-character picker read from.\r\n\r\n" +
                        "Already have a dump? Select it here. Otherwise leave this blank: finishing setup offers the full character, animation, and localisation extraction automatically. Budget about 18 GB of free space.",
                Hint = "The Content folder itself, or Content\\Characters\\Minifig.",
                Optional = true,
                IsFile = false,
                Get = s => string.IsNullOrWhiteSpace(s.ExtractedContentRoot)
                    ? AppSettings.DefaultFirstRunExtractedContentRoot()
                    : s.ExtractedContentRoot,
                Set = (s, v) => s.ExtractedContentRoot = v,
            },
            new()
            {
                Title = "Workspace folder",
                Blurb = "Where your suits, mods and build output are saved. By default Batcomputer keeps its " +
                        "Generated, Data, Runtime, and settings folders beside the app.\r\n\r\n" +
                        "Leave this blank for a portable install. Choose another drive only when you want the large extracted game dump elsewhere.",
                Hint = "Blank = next to Batcomputer.exe. Pick another writable folder only for a larger workspace.",
                Optional = true,
                IsFile = false,
                Get = s => s.ProjectRoot, Set = (s, v) => s.ProjectRoot = v,
            },
            new()
            {
                Title = "Game mod folder",
                Blurb = "Where built mods get installed so the game loads them. You can set this later " +
                        "if you only want to build, not install.",
                Hint = "…\\LEGOBatmanLotDK\\Content\\Paks\\~mods\\Slot",
                IsFile = false, Optional = true,
                Get = s => s.GamePaksModFolder, Set = (s, v) => s.GamePaksModFolder = v,
            },
        };

        BuildChrome();
        LoadStep();
    }

    private void BuildChrome()
    {
        Text = "Batcomputer — Setup";
        ClientSize = new Size(620, 380);
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
        _stepCount.Font = Theme.Eyebrow;
        _stepCount.ForeColor = Theme.Gold;

        _title.SetBounds(Pad, 42, w, 30);
        _title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        _title.ForeColor = Theme.OnDark;

        _blurb.SetBounds(Pad, 76, w, 60);
        _blurb.Font = Theme.Body;
        _blurb.ForeColor = Theme.OnDarkMuted;

        _inputWrap.SetBounds(Pad, 146, w - 96, 36);
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

        _browse.SetBounds(Pad + w - 88, 146, 88, 36);
        _browse.Text = "Browse…";
        Theme.StyleDarkButton(_browse);
        _browse.Click += (_, _) => Browse();

        _dot.SetBounds(Pad + 2, 196, 10, 10);
        _status.SetBounds(Pad + 18, 190, w - 20, 20);
        _status.Font = Theme.Caption;
        _status.ForeColor = Theme.OnDarkMuted;

        var hintLabel = new Label
        {
            Name = "hint",
            Bounds = new Rectangle(Pad, 216, w, 34),
            Font = Theme.Caption,
            ForeColor = Theme.Blend(Theme.OnDarkMuted, Theme.WindowBg, 0.75),
        };

        _bar.SetBounds(Pad, 268, w, 8);
        _bar.BackColor = Theme.WindowBg;
        _bar.Maximum = _steps.Count;

        var footer = new Panel { Bounds = new Rectangle(0, 316, ClientSize.Width, 64), BackColor = Theme.SlateDark };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        _back.SetBounds(Pad, 16, 90, 32);
        _back.Text = "Back";
        Theme.StyleDarkButton(_back);
        _back.Click += (_, _) => GoTo(-1);

        _next.SetBounds(ClientSize.Width - Pad - 130, 16, 130, 32);
        _next.Text = "Next";
        Theme.StyleGoldButton(_next);
        _next.Click += (_, _) => GoTo(+1);

        _skip.SetBounds(_next.Left - 96, 16, 88, 32);
        _skip.Text = "Skip";
        Theme.StyleDarkButton(_skip);
        _skip.Click += (_, _) => { Save(); GoTo(+1, skip: true); };

        footer.Controls.AddRange(new Control[] { _back, _skip, _next });

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
