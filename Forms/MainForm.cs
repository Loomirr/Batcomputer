using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

public sealed partial class MainForm : Form
{
    private readonly TextBox _projectRootText = new();

    // Header placeholders shown when no suit is loaded.
    private const string NoSuitTitle = "No suit selected";

    private const string NoModSubtitle = "None";

    private readonly TextBox _suitNameText = new();

    private readonly TextBox _modFolderText = new();

    private readonly TextBox _basePlayableText = new();

    private readonly TextBox _baseCutsceneText = new();

    private readonly TextBox _baseDcmdText = new();

    private readonly Button _useAsBaseButton = new();

    private readonly Label _detectedLabel = new();

    private readonly Button _installButton = new();

    private readonly Button _settingsButton = new();

    private readonly Button _menuButton = new();

    /// <summary>True while a bulk operation drives many suits - suppresses per-suit dialogs.</summary>
    private bool _batchMode;

    private readonly Button _refreshGameAssetsButton = new();

    private readonly Button _runIndexerButton = new();

    private readonly Button _loadIndexButton = new();

    private readonly Button _buildPartIndexButton = new();

    private readonly Button _useRecommendedButton = new();

    private readonly Button _saveProjectButton = new();

    private readonly Button _createPatchPlanButton = new();

    private readonly Button _stageButton = new();

    private readonly Button _uassetNameMapPatchButton = new();

    private readonly Button _graftTorso2Button = new();

    private readonly Button _graftSelectedPartButton = new();

    private readonly Button _packagePatchedIoStoreButton = new();

    private readonly Button _v2PreflightButton = new();

    private readonly Button _verifyGameLogButton = new();

    private readonly ComboBox _partContextCombo = new();

    private readonly ComboBox _partSlotCombo = new();

    private readonly TextBox _partSearchText = new();

    private readonly Button _refreshPartGridButton = new();

    private readonly Button _usePartAsPlayableButton = new();

    private readonly Button _usePartAsCutsceneButton = new();

    private readonly TextBox _slotIdText = new();

    private readonly TextBox _displayNameText = new();

    private readonly TextBox _descriptionText = new();

    private readonly TextBox _targetPlayableText = new();

    private readonly TextBox _targetCutsceneText = new();

    private readonly TextBox _targetDcmdText = new();

    private readonly TextBox _packageBaseNameText = new();

    private readonly Label _selectedPlayableLabel = new();

    private readonly Label _selectedCutsceneLabel = new();

    private readonly Label _selectedDcmdLabel = new();

    private readonly Label _selectedVisualLabel = new();

    private readonly Label _selectedPlayablePartLabel = new();

    private readonly Label _selectedCutscenePartLabel = new();

    private readonly DataGridView _playableGrid = new();

    private readonly DataGridView _cutsceneGrid = new();

    private readonly DataGridView _partGrid = new();

    private readonly TextBox _matBaseText = new();

    private readonly Button _matBrowseButton = new();

    private readonly Button _matReadButton = new();

    private readonly TextBox _matOutputText = new();

    private readonly Button _matGenerateButton = new();

    private readonly DataGridView _matParamGrid = new();

    private readonly TextBox _matAssignComponentText = new();

    private readonly TextBox _matAssignSlotText = new();

    private readonly TextBox _matAssignMiText = new();

    private readonly ComboBox _matAssignContextCombo = new();

    private readonly Button _matApplyButton = new();

    // The diagnostics/log surface now lives in a designer-editable control.
    private readonly DiagnosticsControl _diagnostics = new();

    // Toybox (simple builder) controls.
    // The "Your Character" panel is a designer-editable control owning its row flow.
    private readonly YourCharacterControl _yourCharacter = new();
    private TableLayoutPanel? _toyboxBodyLayout;
    private SplitContainer? _toyboxWorkspaceSplit;

    private readonly FlowLayoutPanel _toyboxTileFlow = new();

    // Virtualized grid used for the big parts list (owner-drawn, no per-tile controls).
    private readonly VirtualTilePanel _toyboxTileGrid = new();

    // Debounce toybox search so a full re-filter of the ~1800-part catalog runs once the user
    // pauses typing (~200 ms), not on every keystroke.
    private readonly System.Windows.Forms.Timer _toyboxSearchDebounce = new() { Interval = 200 };

    private readonly ComboBox _toyboxCategoryCombo = new();

    private readonly ComboBox _toyboxTypeCombo = new();

    // One button holding every filter for the current category (ConfigureToyboxFilters).
    private readonly FilterBar _toyboxFilters = new();

    private readonly SearchBox _toyboxSearchText = new();

    private readonly Label _toyboxSelectionLabel = new();

    private readonly Button _toyboxPackageButton = new();

    private readonly Button _toyboxSaveButton = new();

    // The Inspector is a designer-editable control owning its tree/info/buttons.
    private readonly InspectorControl _inspector = new();

    // Read-only character research lives in its own tab so it cannot accidentally edit the suit.
    private readonly CharacterResearchInspectorControl _researchInspector = new();

    private readonly SegmentedTabs _inspectorTabs = new();

    private const string SuitTabName = "Suit";

    private const string ResearchTabName = "Research";

    private Button? _researchRailButton;

    private readonly Button _toyboxPrimaryActionButton = new();

    private readonly Label _toyboxStatusChip = new();

    private readonly ToolTip _toyboxToolTip = new();

    // The selection now lives in a shared SelectionController; these stay as transparent
    // read aliases so existing call sites are unchanged while ownership moves to the controller.
    private readonly SelectionController _selection = new();

    private string _toyboxComponent => _selection.Component;

    private string _toyboxSlotLabel => _selection.Label;

    private string _lastAutoPackageBaseName = "";

    // Home is mod-first. This is intentionally a session selection rather than a derived output:
    // the user may keep several mods open and choose which collection they are working on.
    private string _homeActiveModProjectPath = "";

    private int _toyboxSlot => _selection.Slot;

    private string? _pendingInspectorComponentFocus;

    private int _pendingInspectorSlotFocus;

    private bool _isRefreshingInspector;

    private CharacterResearchService? _characterResearchService;

    private string _characterResearchRoot = "";

    private Point _toyboxDragStartPoint;

    private TemplateIndexService? _indexService;

    private SuitProjectService? _projectService;

    private List<TemplateRecord> _playableCandidates = new();

    private List<TemplateRecord> _cutsceneCandidates = new();

    private NativeSuitPartIndex? _partIndex;

    private List<NativeSuitPartRecord> _partCandidates = new();

    private NativeSuitPartRecord? _selectedPlayablePart;

    private NativeSuitPartRecord? _selectedCutscenePart;

    private RecommendedDonorPlan? _recommendedPlan;

    // The session owns the current project (single source of truth). _currentProject
    // is now a transparent alias so existing call sites are unchanged while ownership moves here.
    private readonly BuilderSession _session = new();

    private NativeSuitProject? _currentProject
    {
        get => _session.Project;
        set => _session.Project = value;
    }

    // Change log lives on the project (NativeSuitProject.Changes) so it persists
    // across sessions - reopening a suit restores the full Review history.
    private List<SavedChange> Changes => _currentProject?.Changes ?? new List<SavedChange>();

    private sealed class ToyboxDragPayload
    {
        public string Kind { get; init; } = "";
        public string? MaterialPath { get; init; }
        public NativeSuitPartRecord? Part { get; init; }
    }

    public MainForm()
    {
        InitializeComponent();

        // Keep the Visual Studio designer light and editable. The real builder
        // surface is data-driven and is composed only when the app runs.
        if (IsDesignerHost())
        {
            return;
        }

        Icon = EmbeddedAssets.LoadIcon("Icon.ico") ?? Icon;
        BuildLayout();
        WireEvents();
        SetDefaults();

        // One change-notification → one refresh from the current snapshot. Mutation
        // sites are migrated onto _session.RaiseChanged() incrementally; existing ad-hoc refresh
        // calls remain until each is converted, so behavior is unchanged during the migration.
        _session.Changed += (_, _) => RefreshAllViews();
    }

    /// <summary>
    /// Refreshes the views that depend on PROJECT STATE from the current
    /// snapshot - the single path mutation sites converge on via _session.RaiseChanged(). It does
    /// NOT touch navigation-driven UI (the category type dropdown / primary action), which is owned
    /// by the category selection, so a project edit never resets the toybox filter mid-edit.
    /// Mirrors exactly what the mutation sites already call: Your Character + the Inspector. (The
    /// toybox tile browser is navigation-driven - refreshed by the category combo - so it is left
    /// alone here to avoid resetting the tile scroll position after an edit.)
    /// </summary>
    private void RefreshAllViews()
    {
        PopulateToyboxSlots(); RefreshInspector();
    }

    private static bool IsDesignerHost()
    {
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
        {
            return true;
        }

        try
        {
            var processName = Process.GetCurrentProcess().ProcessName;
            return processName.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("DesignToolsServer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool _diagnosticsCollapsed;

    /// <summary>Collapses/expands the diagnostics drawer - collapsed leaves just the toggle bar so
    /// the workspace gets the room; expanded restores the log to its normal height.</summary>
    private void ToggleDiagnostics(Button header)
    {
        _diagnosticsCollapsed = !_diagnosticsCollapsed;
        _diagnostics.Visible = !_diagnosticsCollapsed;
        header.Text = _diagnosticsCollapsed ? "▸  Diagnostics" : "▾  Diagnostics";
        // Row 1 of the root layout holds the log panel; collapsed leaves just the toggle bar.
        _mainRootLayout.RowStyles[1].Height = _diagnosticsCollapsed ? 32 : 160;
    }

    /// <summary>The header's flat ground. Everything sitting on the bar clears to this.</summary>
    private static readonly Color HeaderGround = Color.FromArgb(30, 33, 40);

    /// <summary>Same ground for the mod-folder field, which cannot be transparent (TextBox).</summary>
    private static readonly Color HeaderMetaGround = HeaderGround;

    /// <summary>The suit name field sits slightly proud of the bar so it reads as editable.</summary>
    private static readonly Color SuitNameGround = Color.FromArgb(36, 40, 48);

    private Label? _suitNamePencil;

    private Label? _headerMetaLabel;

    private bool _suitNameHover;

    private Color _statusChipAccent = Theme.OnDarkMuted;

    private readonly ToolTip _tipsHeader = new();

    /// <summary>Keeps the suit-name field and its pencil lit together on hover/focus.</summary>
    private void RefreshSuitNameState()
    {
        if (_suitNamePencil is null)
        {
            return;
        }
        var hot = _suitNameText.Focused || _suitNameHover;
        _suitNameText.BackColor = hot ? Color.FromArgb(38, 42, 51) : SuitNameGround;
        _suitNamePencil.ForeColor = hot ? Theme.Gold : Theme.OnDarkMuted;
    }

    /// <summary>Slot and pak echo, shown after the editable mod folder in the header meta line.</summary>
    private void RefreshHeaderMeta()
    {
        if (_headerMetaLabel is null)
        {
            return;
        }
        var slot = _slotIdText.Text.Trim();
        var pak = CurrentPackageBaseName();
        var parts = new List<string>();
        if (slot.Length > 0) { parts.Add("slot " + slot); }
        if (!string.IsNullOrWhiteSpace(pak)) { parts.Add("pak " + pak); }
        _headerMetaLabel.Text = parts.Count > 0 ? "·  " + string.Join("  ·  ", parts) : "";
    }

    private Button RailButton(string category, string glyph)
    {
        var color = Theme.CategoryColor(category);
        var b = new Button
        {
            // Glyph above the label; smaller font so long labels ("Animations") fit
            // on one line instead of wrapping and indenting.
            Text = glyph + "\n" + category,
            Width = 74,
            Height = 52,
            Margin = new Padding(1, 1, 1, 3),
            FlatStyle = FlatStyle.Flat,
            Font = new Font(Font.FontFamily, 7.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = color,
            BackColor = Theme.PanelBg,
            Cursor = Cursors.Hand,
            Tag = category
        };
        if (category.Equals("3D viewer", StringComparison.OrdinalIgnoreCase) &&
            EmbeddedAssets.LoadAnimated("3D.gif") is { } animatedIcon)
        {
            // PictureBox owns animated GIF playback; Button.Image would render only a static frame.
            b.Text = category;
            b.TextAlign = ContentAlignment.BottomCenter;
            b.Padding = new Padding(0, 0, 0, 2);
            var animation = new PictureBox
            {
                Image = animatedIcon,
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(23, 23),
                TabStop = false,
            };
            void PlaceAnimation() => animation.Location = new Point((b.ClientSize.Width - animation.Width) / 2, 3);
            PlaceAnimation();
            b.Resize += (_, _) => PlaceAnimation();
            animation.Click += (_, _) => b.PerformClick();
            b.Controls.Add(animation);
        }
        else if (TryLoadCategoryIcon(category) is { } icon)
        {
            // Icon stacked above the label, with the whole block centered in the
            // button so the icon lines up over its text.
            b.Image = icon;
            b.Text = category;
            b.TextImageRelation = TextImageRelation.ImageAboveText;
            b.ImageAlign = ContentAlignment.MiddleCenter;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Padding = new Padding(0, 2, 0, 0);
        }
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Tint(color);
        b.Click += (_, _) =>
        {
            SelectComboValue(_toyboxCategoryCombo, category);
            foreach (Control c in ((FlowLayoutPanel)b.Parent!).Controls)
            {
                c.BackColor = ReferenceEquals(c, b) ? Theme.Tint(color) : Theme.PanelBg;
            }
        };
        return b;
    }

    /// <summary>
    /// Deletes the extract dumps the new one replaces. Deliberately narrow: only sibling folders of
    /// <paramref name="keepDumpRoot"/>, only ones matching the tool's own "&lt;Profile&gt;_&lt;timestamp&gt;"
    /// naming, and never the dump that is now active. Skipped when the user opts to keep them.
    /// </summary>
    private void PruneOldExtracts(string keepDumpRoot)
    {
        if (AppSettings.Current.KeepPreviousExtracts)
        {
            AppendLog("Kept previous extracts (Settings → keep previous extracts).");
            return;
        }

        try
        {
            var keep = Path.GetFullPath(keepDumpRoot).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(keep);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                return;
            }

            // Only the profile-stamped folders this tool creates - never anything else a user
            // may have parked in the extract folder.
            var prefixes = new[] { "Batman_", "AllCharacters_", "DeveloperResearch_" };
            var freed = 0L;
            var removed = 0;

            foreach (var dir in Directory.GetDirectories(parent))
            {
                var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Equals(keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(full);
                if (!prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var size = new DirectoryInfo(full).EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                    Directory.Delete(full, recursive: true);
                    freed += size;
                    removed++;
                    AppendLog($"  removed old extract: {name}");
                }
                catch (Exception ex)
                {
                    AppendLog($"  could not remove {name}: {ex.Message}");
                }
            }

            if (removed > 0)
            {
                AppendLog($"Reclaimed {freed / 1024d / 1024d / 1024d:N1} GB from {removed} old extract(s).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Old-extract cleanup skipped: {ex.Message}");
        }
    }

    private void UpdatePrimaryAction()
    {
        var category = _toyboxCategoryCombo.SelectedItem?.ToString();
        switch (category)
        {
            case "Base":
                _toyboxPrimaryActionButton.Text = "＋ Set visual base";
                _toyboxPrimaryActionButton.Visible = true;
                break;
            case "Materials":
                // No toolbar button: the grid's "＋ Create new material" tile already does this.
                _toyboxPrimaryActionButton.Visible = false;
                break;
            case "Textures":
                _toyboxPrimaryActionButton.Text = "＋ Import PNG";
                _toyboxPrimaryActionButton.Visible = true;
                break;
            case "Parts":
                // No toolbar button: parts are applied by dragging a tile onto the figure.
                _toyboxPrimaryActionButton.Visible = false;
                break;
            case "Review":
                _toyboxPrimaryActionButton.Text = "Copy summary";
                _toyboxPrimaryActionButton.Visible = true;
                break;
            case "Build mod":
                _toyboxPrimaryActionButton.Text = "Build active mod";
                _toyboxPrimaryActionButton.Visible = true;
                break;
            default:
                _toyboxPrimaryActionButton.Visible = false;
                break;
        }
    }

    private async void RunPrimaryAction()
    {
        switch (_toyboxCategoryCombo.SelectedItem?.ToString())
        {
            case "Base": OpenBaseWizard(); break;
            case "Materials": OpenMaterialWizard(applyToSelectedSlot: false); break;
            case "Textures": await ImportTextureFromPngAsync(); break;
            case "Review": CopyChangeSummary(); break;
            case "Build mod": BuildActiveModFromWorkspace(); break;
        }
    }

    /// <summary>Edits the suit's menu description (shown under the suit in-game). Was only
    /// round-tripped in a hidden field before - this is the only way to change it.</summary>
    private void EditSuitDescription()
    {
        if (_currentProject is null)
        {
            AppendLog("Set or load a base suit first, then edit its description.");
            return;
        }

        var value = PromptForText("Suit description", "Shown under the suit in the character menu:", _descriptionText.Text.Trim());
        if (value is null)
        {
            return;
        }
        _descriptionText.Text = value.Trim();
        _currentProject.Description = value.Trim();
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        AppendLog($"Suit description set: \"{value.Trim()}\". Repackage to bake it into the DCMD/runtime JSON.");
        RefreshToyboxTiles();
    }

    /// <summary>
    /// Sets the suit's native identity: its unique PawnTag plus the menu text that lands
    /// in the mod StringTable (name / description / locked). These are the SUIT's source
    /// of truth; a mod aggregates them. See docs/native-suit-mod-bundles-...-2026-07-16.md.
    /// </summary>
    private void EditNativeIdentity()
    {
        if (_currentProject is null)
        {
            AppendLog("Set or load a base suit first, then set its native identity.");
            return;
        }

        using var dlg = new NativeIdentityDialog(_currentProject, SuggestPawnTag(_currentProject));
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // The dialog will not return OK with a blank or donor pawn tag, so no re-check here.
        _currentProject.PawnTag = dlg.PawnTag;
        _currentProject.DisplayName = dlg.DisplayName;
        _currentProject.Description = dlg.Description;
        _currentProject.LockedDescription = dlg.LockedDescription;
        _currentProject.ProgressTag = dlg.ProgressTag;

        // Keep the visible name/description fields in sync with the project.
        _suitNameText.Text = _currentProject.DisplayName;
        _descriptionText.Text = _currentProject.Description;

        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        AppendLog($"Native identity saved. PawnTag = {_currentProject.PawnTag}. Rebuild the mod to regenerate its PawnTags.ini + StringTable.");
        RefreshToyboxTiles();
        RefreshInspector();
    }

    private void LoadSuit()
    {
        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        using var dlg = new LoadSuitDialog(svc.ListProjects(), DeleteSavedSuit);
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        NativeSuitProject? project;
        try
        {
            project = svc.LoadProject(dlg.SelectedPath!);
        }
        catch (Exception ex)
        {
            AppendLog($"Load failed: {ex.Message}");
            Dialog.Error(this, "Could not open that suit",
                $"{ex.Message}\n\nThe suit folder may be from an older version of the tool, or its " +
                "project file may be corrupt.");
            return;
        }
        if (project is null)
        {
            AppendLog("Load failed: could not read project.");
            return;
        }

        _projectService = svc;
        LoadProjectIntoUi(project);
    }

    /// <summary>
    /// Resets the editor to a fresh, named suit after a confirmation. The previous
    /// suit stays saved in its own project file (reopen via Open suit). Prompting for
    /// a name up front also prevents nameless placeholder projects.
    /// </summary>
    private void StartNewSuit(Action<NativeSuitProject>? afterCreated = null)
    {
        if (!Dialog.Confirm(this,
                "Start a new suit?",
                "The editor will reset to a blank suit. Your current suit stays saved in its own file — reopen it any time with 'Open suit'.",
                confirmText: "New suit", severity: Dialog.Level.Info))
        {
            return;
        }

        var name = PromptForText("New suit", "Name your new suit", "", confirmText: "Create");
        if (string.IsNullOrWhiteSpace(name))
        {
            AppendLog("New suit cancelled (no name entered).");
            return;
        }

        var mod = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(mod))
        {
            mod = "Suit";
        }

        // Fresh project - nothing carried over from the previous suit.
        _currentProject = new NativeSuitProject
        {
            DisplayName = name,
            EquipmentSlots = new(),
            MaterialAssignments = new(),
            Changes = new(),
            AnimationOverrides = new(),
            LocomotionOverrides = new(),
            Requirements = new(),
        };
        _customSlotKeys.Clear();
        _selectedPlayablePart = null;
        _selectedCutscenePart = null;

        // Name + mod drive DeriveOutputs → SlotId + target packages.
        _modFolderText.Text = mod;
        _suitNameText.Text = name;
        _basePlayableText.Text = "";
        _baseCutsceneText.Text = "";
        _baseDcmdText.Text = "";
        _packageBaseNameText.Text = "";
        _lastAutoPackageBaseName = "";

        ReadFieldsIntoProject(_currentProject);
        afterCreated?.Invoke(_currentProject);
        AppendLog($"Started new suit '{name}' (mod {mod}). Next: Base → Pick base character to choose the character to build from.");
        SelectComboValue(_toyboxCategoryCombo, "Base");
        _session.RaiseChanged(); // UI Phase 2: single project-state refresh (Your Character + Inspector)
        RefreshToyboxTiles();
        UpdateToyboxChips();
    }

    /// <summary>Minimal single-line text prompt (WinForms has no built-in InputBox).</summary>
    /// <summary>Themed single-field prompt (rounded input + a confirm button named for the action).</summary>
    private string? PromptForText(string title, string label, string initial, string confirmText = "OK")
    {
        const int W = 430, Pad = 18;
        using var dlg = new Form
        {
            Text = title,
            ClientSize = new Size(W, 158),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            Font = Theme.Body,
        };

        var lbl = new Label
        {
            Text = label, Left = Pad, Top = Pad, Width = W - Pad * 2, Height = 18,
            ForeColor = Theme.OnDarkMuted, Font = Theme.Caption,
        };

        var wrap = new RoundedPanel
        {
            Left = Pad, Top = 42, Width = W - Pad * 2, Height = 34,
            BackColor = Theme.Slate, BorderColor = Theme.SlateLight, CornerRadius = Theme.RadiusSm,
        };
        var box = new TextBox
        {
            Left = 11, Width = wrap.Width - 22, Text = initial,
            BorderStyle = BorderStyle.None, BackColor = Theme.Slate,
            ForeColor = Theme.OnDark, Font = Theme.Body,
        };
        box.Top = (wrap.Height - box.Height) / 2;
        wrap.Controls.Add(box);

        var footer = new Panel { Left = 0, Top = 92, Width = W, Height = 54, BackColor = Theme.SlateDark };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var ok = new Button { Text = confirmText, DialogResult = DialogResult.OK, Height = 32, Top = 11 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Height = 32, Top = 11, Width = 90 };
        Theme.StyleGoldButton(ok);
        Theme.StyleDarkButton(cancel);
        ok.Width = Math.Max(96, TextRenderer.MeasureText(ok.Text, ok.Font).Width + 34);
        ok.Left = W - Pad - ok.Width;
        cancel.Left = ok.Left - cancel.Width - 8;
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);

        dlg.Controls.AddRange(new Control[] { lbl, wrap, footer });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        box.Select();
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    /// <summary>Shows the playables-only picker for choosing a machinery donor (a hero to
    /// inherit abilities/equipment/animation/cutscene from). Returns its /Game package or null.</summary>
    private string? PromptForMachineryDonor()
    {
        if (!GameDataService.Instance.HasCatalog)
        {
            return null;
        }
        using var picker = new BaseCharacterPicker(playablesOnly: true);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.BrowseManuallyRequested)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(picker.SelectedPlayablePackage) ? null : picker.SelectedPlayablePackage;
    }

    /// <summary>Finds the _Cutscene sibling of a playable package on disk (same folder).</summary>
    private static string? ResolveCutsceneSibling(string playablePackage)
    {
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        var playableDisk = Path.Combine(extracted, playablePackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var folder = Path.GetDirectoryName(playableDisk);
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }
        var baseStem = UnrealPathUtil.AssetName(playablePackage);
        if (baseStem.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase))
        {
            baseStem = baseStem[..^"_Playable".Length];
        }
        var matches = Directory.EnumerateFiles(folder, "*.uasset")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.StartsWith(baseStem, StringComparison.OrdinalIgnoreCase) &&
                        n.EndsWith("_Cutscene", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n!.Length)
            .ToList();
        var chosen = matches.FirstOrDefault(n => n!.Contains("Default", StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        return chosen is null ? null : Path.Combine(folder, chosen + ".uasset");
    }

    /// <summary>
    /// Resolves an MI /Game path to its .uasset on disk. User-made MIs live in
    /// the export content root; base-game MIs in the extracted content root. We
    /// try both, preferring the expected root for the tile kind.
    /// </summary>
    internal static string? ResolveMiDiskPath(string miGamePath, bool preferExport)
    {
        var pkg = miGamePath.Contains('.') ? miGamePath[..miGamePath.IndexOf('.')] : miGamePath;
        if (!pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var rel = pkg["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        var export = AppSettings.Current.EffectiveExportContentRoot();
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        var roots = preferExport ? new[] { export, extracted } : new[] { extracted, export };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static readonly (string Label, string Component, int Slot)[] ToyboxSlots =
    {
        ("Body", "CharacterMesh0", 0),
        ("Head / cowl", "Head", 0),
        ("Cape LOD0", "Cape", 0),
        ("Cape LOD1", "Cape", 1),
        ("Cape LOD2", "Cape", 2),
        ("Gliding LOD0", "Torso", 0),
        ("Gliding LOD1", "Torso", 1),
    };

    private readonly HashSet<string> _customSlotKeys = new(StringComparer.Ordinal);

    /// <summary>The character's mesh slots, backing the minifig figure (the panel no longer
    /// renders a row per slot - the figure IS the slot picker).</summary>
    private readonly List<(string Label, string Component, int Slot)> _characterSlots = new();

    /// <summary>Mesh + material per "component:slot", so the figure can show what's applied.</summary>
    private readonly Dictionary<string, (string Mesh, string Material, bool IsDefault)> _slotDetails =
        new(StringComparer.OrdinalIgnoreCase);

    private static (string BaseComponent, int DuplicateNumber) SplitGeneratedDuplicateComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return ("", 1);
        }

        var underscore = component.LastIndexOf('_');
        if (underscore > 0 &&
            underscore < component.Length - 1 &&
            int.TryParse(component[(underscore + 1)..], out var suffix) &&
            suffix > 1)
        {
            return (component[..underscore], suffix);
        }

        return (component, 1);
    }

    private static string AppendDuplicateLabel(string label, int duplicateNumber)
    {
        return duplicateNumber > 1 ? $"{label} #{duplicateNumber}" : label;
    }

    private static void ClearAndDisposeControls(Control parent)
    {
        if (parent.Controls.Count == 0)
        {
            return;
        }

        var oldControls = parent.Controls.Cast<Control>().ToArray();
        parent.Controls.Clear();
        foreach (var control in oldControls)
        {
            control.Dispose();
        }
    }

    /// <summary>Cover image for the currently-loaded suit (clone owned by the hero), or null.</summary>
    private Image? LoadSuitCoverForCurrent()
    {
        try
        {
            var slot = _slotIdText.Text.Trim();
            if (string.IsNullOrWhiteSpace(slot)) return null;
            foreach (var p in new SuitProjectService(_projectRootText.Text.Trim()).ListProjects())
            {
                if (string.Equals(p.SlotId, slot, StringComparison.OrdinalIgnoreCase))
                {
                    return LoadSuitCoverImage(p);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Cached master thumbnails of imported textures' source PNGs, keyed by path + write time +
    /// size (so re-importing over the same filename re-decodes). Masters live for the life of the
    /// form; callers get clones. See <see cref="LoadTextureThumbnail"/>.
    /// </summary>
    private readonly Dictionary<string, Image> _textureThumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    private Image? LoadSuitCoverImage(SuitProjectService.ProjectSummary summary)
    {
        var path = summary.CoverImagePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(Path.GetDirectoryName(summary.Path) ?? "", path);
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var source = Image.FromFile(path);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private void SetSuitCoverImage(SuitProjectService.ProjectSummary summary)
    {
        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        var project = svc.LoadProject(summary.Path);
        if (project is null)
        {
            AppendLog($"Cover image skipped: could not load {summary.Path}");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"Choose cover image for {project.DisplayName}",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|PNG files|*.png|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            CopyCoverIntoProject(svc, project, dialog.FileName);
            AppendLog($"Set cover image for '{project.DisplayName}'.");
            RefreshHomeTiles();
        }
        catch (Exception ex)
        {
            AppendLog($"Cover image failed: {ex.Message}");
        }
    }

    private static void CopyCoverIntoProject(SuitProjectService svc, NativeSuitProject project, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected image no longer exists.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
        {
            extension = ".png";
        }

        var projectDir = svc.ProjectOutputDirectory(project);
        Directory.CreateDirectory(projectDir);
        var destination = Path.Combine(projectDir, "cover" + extension);
        File.Copy(sourcePath, destination, overwrite: true);

        var previous = project.CoverImagePath;
        project.CoverImagePath = destination;
        if (!string.IsNullOrWhiteSpace(previous) &&
            !previous.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            IsPathUnder(previous, projectDir) &&
            File.Exists(previous))
        {
            File.Delete(previous);
        }

        svc.SaveProject(project);
    }

    private void ClearSuitCoverImage(SuitProjectService.ProjectSummary summary)
    {
        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        var project = svc.LoadProject(summary.Path);
        if (project is null)
        {
            return;
        }

        var projectDir = svc.ProjectOutputDirectory(project);
        if (IsPathUnder(project.CoverImagePath, projectDir) && File.Exists(project.CoverImagePath))
        {
            try { File.Delete(project.CoverImagePath); } catch { /* best effort */ }
        }
        project.CoverImagePath = "";
        svc.SaveProject(project);
        AppendLog($"Cleared cover image for '{project.DisplayName}'.");
        RefreshHomeTiles();
    }

    private bool DeleteSavedSuit(SuitProjectService.ProjectSummary summary, bool deleteFromGame, bool deleteFromTool)
    {
        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        var project = svc.LoadProject(summary.Path);
        if (project is null)
        {
            AppendLog($"Delete skipped: could not load {summary.Path}");
            return false;
        }

        var target = deleteFromGame && deleteFromTool
            ? "the game and the tool"
            : deleteFromGame ? "the game" : "the tool";
        if (!Dialog.Confirm(this,
                $"Delete '{project.DisplayName}'?",
                $"This removes it from {target}. It cannot be undone from the builder.",
                confirmText: "Delete suit", severity: Dialog.Level.Crit))
        {
            return false;
        }

        try
        {
            if (deleteFromGame)
            {
                DeleteSuitFromGame(project);
            }
            if (deleteFromTool)
            {
                svc.DeleteProjectFromTool(summary.Path, project);
            }

            if (_currentProject is not null && _currentProject.SlotId.Equals(project.SlotId, StringComparison.OrdinalIgnoreCase))
            {
                ClearCurrentSuitAfterDeletion();
            }

            AppendLog($"Deleted '{project.DisplayName}' from {target}.");
            RefreshHomeTiles();
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Suit delete failed: {ex.Message}");
            Dialog.Error(this, "Delete suit failed", ex.Message);
            return false;
        }
    }

    private void DeleteSuitFromGame(NativeSuitProject project)
    {
        var modFolder = Path.GetFullPath(AppSettings.Current.EffectiveGamePaksModFolder());
        if (!Directory.Exists(modFolder))
        {
            throw new DirectoryNotFoundException($"The configured game mod folder was not found: {modFolder}");
        }

        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(project.PackageBaseName))
        {
            packageNames.Add(Path.GetFileNameWithoutExtension(project.PackageBaseName));
        }

        var ioStoreDir = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", project.SlotId, "IoStore");
        if (Directory.Exists(ioStoreDir))
        {
            foreach (var file in Directory.EnumerateFiles(ioStoreDir))
            {
                if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                {
                    packageNames.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        var removed = 0;
        foreach (var packageName in packageNames)
        {
            foreach (var extension in new[] { ".pak", ".ucas", ".utoc" })
            {
                var installed = Path.Combine(modFolder, packageName + extension);
                if (File.Exists(installed))
                {
                    File.Delete(installed);
                    removed++;
                }
            }
        }

        var suitsRoot = Path.GetFullPath(EffectiveGameRuntimeSuitsFolder());
        var suitDir = Path.GetFullPath(Path.Combine(suitsRoot, SafeSuitFolderName(project.SlotId)));
        if (IsPathUnder(suitDir, suitsRoot) && Directory.Exists(suitDir))
        {
            var json = Path.Combine(suitDir, "suit.json");
            if (File.Exists(json))
            {
                File.Delete(json);
                removed++;
            }
            if (!Directory.EnumerateFileSystemEntries(suitDir).Any())
            {
                Directory.Delete(suitDir);
            }
        }

        AppendLog($"Removed {removed} installed file(s) for '{project.DisplayName}'.");
    }

    private void ClearCurrentSuitAfterDeletion()
    {
        _currentProject = null;
        _customSlotKeys.Clear();
        _selectedPlayablePart = null;
        _selectedCutscenePart = null;
        _suitNameText.Text = NoSuitTitle;
        _modFolderText.Text = NoModSubtitle;
        _descriptionText.Text = "Custom native suit.";
        _basePlayableText.Text = "";
        _baseCutsceneText.Text = "";
        _baseDcmdText.Text = "";
        _packageBaseNameText.Text = "";
        _lastAutoPackageBaseName = "";
        _session.RaiseChanged();
        UpdateToyboxChips();
    }

    private static string SafeSuitFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? "Suit").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim('_');
    }

    private static bool IsPathUnder(string? child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        try
        {
            var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OpenRecentProject(string path)
    {
        try
        {
            var svc = new SuitProjectService(_projectRootText.Text.Trim());
            var project = svc.LoadProject(path);
            if (project is null)
            {
                AppendLog($"Could not open project: {path}");
                return;
            }
            _projectService = svc;
            LoadProjectIntoUi(project);
            SelectComboValue(_toyboxCategoryCombo, "Materials");
        }
        catch (Exception ex)
        {
            AppendLog($"Open failed: {ex.Message}");
        }
    }

    private Usmap? _uiMappings;

    private Usmap? UiMappings()
    {
        if (_uiMappings is null)
        {
            var p = AppSettings.Current.EffectiveUsmapPath();
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) _uiMappings = MappingsCache.Load(p);
        }
        return _uiMappings;
    }

    private void PickLocomotionReplacement(string donorName, string donorPackage)
    {
        EnsureProject();
        if (_currentProject is null) return;

        var pick = PickAnimReplacement($"Replace {donorName} with…");
        _currentProject.LocomotionOverrides.RemoveAll(o => o.DonorSequence == donorName);
        if (pick is null)
        {
            RecordChange("Animations", donorName, "reverted to donor default", status: "staged");
            AppendLog($"{donorName}: reverted to donor default.");
        }
        else
        {
            var pkg = pick.Contains('.') ? pick[..pick.IndexOf('.')] : pick;
            var replName = pkg[(pkg.LastIndexOf('/') + 1)..];
            _currentProject.LocomotionOverrides.Add(new AnimSequenceOverride
            {
                DonorSequence = donorName,
                DonorSequencePackage = donorPackage,
                ReplacementSequence = replName,
                ReplacementPackage = pkg,
            });
            RecordChange("Animations", donorName, $"→ {replName}", status: "staged");
            AppendLog($"{donorName} → {replName}. Regenerate to apply.");
        }
        RefreshToyboxTiles();
        PopulateToyboxSlots();
    }

    private static string FormatWhen(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToString("g")
            : iso;

    /// <summary>A note tile that forces the tile flow to wrap after it (full-width row).</summary>
    private Label FullWidthNote(string text)
    {
        var note = MakeNoteTile(text);
        note.Width = _toyboxTileFlow.ClientSize.Width - 24;
        _toyboxTileFlow.SetFlowBreak(note, true);
        return note;
    }

    private void CopyText(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppendLog("Nothing to copy.");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            AppendLog(successMessage);
        }
        catch (Exception ex)
        {
            AppendLog($"Copy failed: {ex.Message}");
        }
    }

    private (string Name, string Kind, TextureCookPreset Preset)? PromptForTextureImportSettings(string suggestedName, string projectRoot)
    {
        const int Width = 560;
        const int Padding = 18;
        using var form = new Form
        {
            Text = "Batcomputer - Texture import",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(Width, 340),
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            Font = Theme.Body
        };

        form.Shown += (_, _) => Theme.UseDarkTitleBar(form);
        var header = new Panel { Left = 0, Top = 0, Width = Width, Height = 72, BackColor = Theme.WindowBg };
        header.Controls.Add(new Panel { Left = Padding, Top = 18, Width = 3, Height = 36, BackColor = Theme.Textures });
        header.Controls.Add(new Label
        {
            Left = Padding + 12, Top = 14, Width = Width - Padding * 2 - 12, Height = 16,
            Text = "TEXTURES", Font = Theme.Eyebrow, ForeColor = Theme.Textures
        });
        header.Controls.Add(new Label
        {
            Left = Padding + 12, Top = 31, Width = Width - Padding * 2 - 12, Height = 22,
            Text = "Import texture", Font = Theme.Heading, ForeColor = Theme.OnDark
        });

        var fields = new RoundedPanel
        {
            Left = Padding, Top = 80, Width = Width - Padding * 2, Height = 190,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };

        var input = new TextBox
        {
            Text = MakeSafeTextureToken(suggestedName),
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Slate,
            ForeColor = Theme.OnDark,
            Font = Theme.Body
        };
        AddTextureDialogField(fields, "TEXTURE NAME", input, 14);

        var kind = new ThemedDropDown { Height = 34 };
        foreach (var textureKind in new[] { "Character texture", "Color mask", "UI icon", "Normal map", "Roughness/spec mask", "Other texture" })
        {
            kind.Items.Add(textureKind);
        }
        var guessedKind = GuessTextureImportKind(suggestedName);
        kind.SelectedItem = guessedKind;
        if (kind.SelectedIndex < 0)
        {
            kind.SelectedIndex = 0;
        }
        AddTextureDialogField(fields, "USE", kind, 74);

        var profile = new ThemedDropDown { Height = 34 };
        AddTextureDialogField(fields, "NATIVE COOK PROFILE", profile, 134);

        void ReloadProfiles()
        {
            profile.Items.Clear();
            foreach (var preset in AvailableTextureCookPresets(projectRoot, kind.SelectedItem?.ToString() ?? "Texture"))
            {
                profile.Items.Add(preset);
            }

            if (profile.Items.Count > 0)
            {
                profile.SelectedIndex = 0;
            }
        }

        kind.SelectedIndexChanged += (_, _) => ReloadProfiles();
        ReloadProfiles();

        var footer = new Panel
        {
            Left = 0, Top = 286, Width = Width, Height = 54, BackColor = Theme.SlateDark
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, Width = 96, Height = 32, Left = Width - Padding - 96, Top = 11 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 32, Left = Width - Padding - 96 - 98, Top = 11 };
        Theme.StyleGoldButton(ok);
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);

        form.Controls.Add(header);
        form.Controls.Add(fields);
        form.Controls.Add(footer);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        input.Select();

        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return profile.SelectedItem is TextureCookPreset preset
            ? (input.Text.Trim(), kind.SelectedItem?.ToString() ?? "Texture", preset)
            : null;
    }

    private static void AddTextureDialogField(RoundedPanel parent, string label, Control input, int top)
    {
        parent.Controls.Add(new Label
        {
            Left = 14, Top = top, Width = parent.Width - 28, Height = 15,
            Text = label, Font = Theme.Eyebrow, ForeColor = Theme.OnDarkMuted, BackColor = Color.Transparent
        });
        if (input is ThemedDropDown)
        {
            input.Left = 14;
            input.Top = top + 17;
            input.Width = parent.Width - 28;
            input.Height = 34;
            input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(input);
            return;
        }

        var wrap = new RoundedPanel
        {
            Left = 14, Top = top + 17, Width = parent.Width - 28, Height = 34,
            BackColor = Theme.Slate, BorderColor = Theme.SlateLight, CornerRadius = Theme.RadiusSm
        };
        input.Left = 10;
        input.Top = (wrap.Height - input.Height) / 2;
        input.Width = wrap.Width - 20;
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        wrap.Controls.Add(input);
        parent.Controls.Add(wrap);
    }

    private TextureCookPreset? PromptForTextureCookPreset(string textureKind, string projectRoot, string? currentProfileId)
    {
        var presets = AvailableTextureCookPresets(projectRoot, textureKind);
        if (presets.Count == 0)
        {
            AppendLog($"No native cook templates are available for '{textureKind}'.");
            return null;
        }

        const int Width = 540;
        const int Padding = 18;
        using var form = new Form
        {
            Text = "Batcomputer - Texture cook profile",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(Width, 238),
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            Font = Theme.Body
        };

        form.Shown += (_, _) => Theme.UseDarkTitleBar(form);
        var header = new Panel { Left = 0, Top = 0, Width = Width, Height = 72, BackColor = Theme.WindowBg };
        header.Controls.Add(new Panel { Left = Padding, Top = 18, Width = 3, Height = 36, BackColor = Theme.Textures });
        header.Controls.Add(new Label
        {
            Left = Padding + 12, Top = 14, Width = Width - Padding * 2 - 12, Height = 16,
            Text = "TEXTURES", Font = Theme.Eyebrow, ForeColor = Theme.Textures
        });
        header.Controls.Add(new Label
        {
            Left = Padding + 12, Top = 31, Width = Width - Padding * 2 - 12, Height = 22,
            Text = "Change cook profile", Font = Theme.Heading, ForeColor = Theme.OnDark
        });

        var fields = new RoundedPanel
        {
            Left = Padding, Top = 80, Width = Width - Padding * 2, Height = 82,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        var profile = new ThemedDropDown { Height = 34 };
        foreach (var preset in presets)
        {
            profile.Items.Add(preset);
        }
        var currentIndex = presets.FindIndex(p => p.Id.Equals(currentProfileId, StringComparison.OrdinalIgnoreCase));
        profile.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
        AddTextureDialogField(fields, "NATIVE COOK PROFILE", profile, 14);

        var footer = new Panel
        {
            Left = 0, Top = 184, Width = Width, Height = 54, BackColor = Theme.SlateDark
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var ok = new Button { Text = "Recook", DialogResult = DialogResult.OK, Width = 96, Height = 32, Left = Width - Padding - 96, Top = 11 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 32, Left = Width - Padding - 96 - 98, Top = 11 };
        Theme.StyleGoldButton(ok);
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);

        form.Controls.Add(header);
        form.Controls.Add(fields);
        form.Controls.Add(footer);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(this) == DialogResult.OK
            ? profile.SelectedItem as TextureCookPreset
            : null;
    }

    private static string MakeFixedLengthModFolderName(string value, int index, int targetLength)
    {
        var token = MakeSafeTextureToken(value);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = "Mod";
        }

        var hash = LongHash($"{token}|ui-folder|{index}");
        if (targetLength <= 0)
        {
            throw new InvalidOperationException("Target mod folder length must be positive.");
        }

        if (targetLength <= 4)
        {
            return hash[..targetLength];
        }

        var hashLength = Math.Min(4, targetLength - 1);
        var coreLength = targetLength - hashLength;
        var core = token.Length > coreLength ? token[..coreLength] : token.PadRight(coreLength, 'X');
        return core + hash[..hashLength];
    }

    private static (int PackageLength, int NameLength) ReadTextureTemplateLengths(string templateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = FindTexture2DJsonRoot(doc.RootElement);
            if (root.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("Texture2D root not found in template JSON.");
            }
            var package = root.TryGetProperty("Package", out var pkgEl)
                ? UnrealPathUtil.NormalizePackagePath(pkgEl.GetString())
                : "/Game/Mods/ElectricLBM2/T_Batman_ElectricLBM2_ColorMask";
            var name = root.TryGetProperty("Name", out var nameEl)
                ? nameEl.GetString() ?? UnrealPathUtil.AssetName(package)
                : UnrealPathUtil.AssetName(package);
            return (package.Length, name.Length);
        }
        catch
        {
            const string fallbackPackage = "/Game/Mods/ElectricLBM2/T_Batman_ElectricLBM2_ColorMask";
            const string fallbackName = "T_Batman_ElectricLBM2_ColorMask";
            return (fallbackPackage.Length, fallbackName.Length);
        }
    }

    private static string ShortHash(string value)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return string.Concat(hash.Take(4).Select(b => b.ToString("X2")));
    }

    private static string LongHash(string value)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return string.Concat(hash.Select(b => b.ToString("X2")));
    }

    private static string TrimMiddle(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        var keep = Math.Max(4, (maxLength - 1) / 2);
        return value[..keep] + "…" + value[^keep..];
    }

    private static string CleanAssetDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(mesh ref)";
        }

        var name = value.Trim();
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
        }

        foreach (var prefix in new[] { "SKM_", "SK_", "SM_", "MI_", "M_", "T_", "BP_", "DA_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return name.Replace('_', ' ').Trim();
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static void AddLabeledText(TableLayoutPanel panel, string label, TextBox textBox, int labelColumn, int row, int textColumnSpan = 1)
    {
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, labelColumn, row);
        textBox.Dock = DockStyle.Fill;
        panel.Controls.Add(textBox, labelColumn + 1, row);
        if (textColumnSpan > 1)
        {
            panel.SetColumnSpan(textBox, textColumnSpan);
        }
    }

    private static GroupBox CreateGridGroup(string title, DataGridView grid)
    {
        var box = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = title
        };
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoGenerateColumns = false;
        grid.RowHeadersVisible = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Score", DataPropertyName = nameof(CandidateRow.Score), Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Package", DataPropertyName = nameof(CandidateRow.PackagePath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Pair", DataPropertyName = nameof(CandidateRow.HasPair), Width = 45 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "DCMD", DataPropertyName = nameof(CandidateRow.HasDcmd), Width = 55 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Torso2", DataPropertyName = nameof(CandidateRow.HasTorso2), Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Abs torso", DataPropertyName = nameof(CandidateRow.HasAbsoluteTorso), Width = 75 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Hair", DataPropertyName = nameof(CandidateRow.HasHair), Width = 55 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "SlickBack", DataPropertyName = nameof(CandidateRow.HasSlickBack), Width = 75 });
        box.Controls.Add(grid);
        return box;
    }

    private static TabPage CreateTabPage(string title, Control content)
    {
        var tab = new TabPage(title);
        content.Dock = DockStyle.Fill;
        tab.Controls.Add(content);
        return tab;
    }

    private static void ConfigureMatParamGrid(DataGridView grid)
    {
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Param", HeaderText = "Parameter", ReadOnly = true, FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current texture", ReadOnly = true, FillWeight = 35 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "YourTexture", HeaderText = "Your texture path (/Game/Mods/.../T_...)", ReadOnly = false, FillWeight = 45 });
    }

    private void WireEvents()
    {
        _refreshGameAssetsButton.Click += async (_, _) => await RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile.AllCharacterAssets);
        _runIndexerButton.Click += async (_, _) => await RunIndexerAsync();
        _loadIndexButton.Click += (_, _) => LoadIndex();
        _buildPartIndexButton.Click += async (_, _) => await BuildPartIndexAsync();
        _useRecommendedButton.Click += (_, _) => UseRecommendedPlan();
        _saveProjectButton.Click += (_, _) => SaveProject();
        _createPatchPlanButton.Click += (_, _) => CreatePatchPlan();
        _stageButton.Click += (_, _) => StageUnpatchedFiles();
        _uassetNameMapPatchButton.Click += (_, _) => PatchNameMapsWithUAssetApi();
        _graftTorso2Button.Click += async (_, _) => await GraftTorso2Async();
        _graftSelectedPartButton.Click += async (_, _) => await GraftSelectedPartsAsync();
        _packagePatchedIoStoreButton.Click += async (_, _) => await BuildModForCurrentSuitAsync();
        _playableGrid.SelectionChanged += (_, _) => PickPlayableFromGrid();
        _cutsceneGrid.SelectionChanged += (_, _) => PickCutsceneFromGrid();
        _refreshPartGridButton.Click += (_, _) => LoadPartIndexAndRefreshGrid();
        _partContextCombo.SelectedIndexChanged += (_, _) => RefreshPartGrid();
        _partSlotCombo.TextChanged += (_, _) => RefreshPartGrid();
        _partSearchText.TextChanged += (_, _) => RefreshPartGrid();
        _usePartAsPlayableButton.Click += (_, _) => UseSelectedPartForPlayable();
        _usePartAsCutsceneButton.Click += (_, _) => UseSelectedPartForCutscene();
    }

    private void SetDefaults()
    {
        _projectRootText.Text = AppSettings.Current.EffectiveProjectRoot();
        _descriptionText.Text = "Custom native suit.";
        _suitNameText.Text = NoSuitTitle;
        _modFolderText.Text = NoModSubtitle;
        DeriveOutputs();
        UpdateSelectedLabels();
        UpdateSelectedPartLabels();
        AppendLog("Ready. Step 1: pick a base playable + cutscene, then \"Use as base\".");
        LoadPartIndexAndRefreshGrid(logIfMissing: false);
    }

    private void DeriveOutputs()
    {
        var suit = _suitNameText.Text.Trim();
        var mod = _modFolderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(suit) || string.IsNullOrWhiteSpace(mod) ||
            suit == NoSuitTitle || mod == NoModSubtitle)
        {
            return;
        }

        var words = suit.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var slug = string.Join("_", words.Select(w => w.ToLowerInvariant()));
        if (words.Count > 1 && words[^1].Equals("Suit", StringComparison.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }
        var stem = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..] : "")));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Custom";
        }

        _displayNameText.Text = suit;
        _slotIdText.Text = $"batman_{slug}";
        var basePath = $"/Game/Mods/{mod}/Characters";
        _targetPlayableText.Text = $"{basePath}/BP_Batman_{stem}_Playable";
        _targetCutsceneText.Text = $"{basePath}/BP_Batman_{stem}_Cutscene";
        _targetDcmdText.Text = $"{basePath}/DA_DCMD_Batman_{stem}_Playable";

        var derivedPackageBaseName = MakeSafePackageBaseName($"{mod}_{stem}_P");
        if (string.IsNullOrWhiteSpace(_packageBaseNameText.Text) ||
            string.Equals(_packageBaseNameText.Text.Trim(), _lastAutoPackageBaseName, StringComparison.OrdinalIgnoreCase))
        {
            _packageBaseNameText.Text = derivedPackageBaseName;
        }
        _lastAutoPackageBaseName = derivedPackageBaseName;
    }

    private static TemplateRecord? TemplateFromUasset(string filePath, string role, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }
        var full = Path.GetFullPath(filePath);
        var root = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null; // must live under the extracted game Content root
        }
        var rel = full[(root.Length + 1)..];
        var ext = Path.GetExtension(rel);
        var relNoExt = string.IsNullOrEmpty(ext)
            ? rel.Replace('\\', '/')
            : rel[..^ext.Length].Replace('\\', '/');
        var uexp = Path.ChangeExtension(full, ".uexp");
        var ubulk = Path.ChangeExtension(full, ".ubulk");
        return new TemplateRecord
        {
            Uasset = full,
            Uexp = File.Exists(uexp) ? uexp : null,
            Ubulk = File.Exists(ubulk) ? ubulk : null,
            Stem = Path.GetFileNameWithoutExtension(full),
            ContentRelative = relNoExt,
            PackagePath = "/Game/" + relNoExt,
            Role = role
        };
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(AppSettings.Current, firstRun: false);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            AppSettings.Current = AppSettings.Load();
            _projectRootText.Text = AppSettings.Current.EffectiveProjectRoot();
            ApplyResearchToolsVisibility();
            PopulateToyboxSlots(); // picks up a changed "Your Character" panel style immediately
            AppendLog("Settings saved. Tool paths reloaded.");
        }
    }

    private async Task RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile profile)
    {
        var projectRoot = _projectRootText.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            AppendLog("Asset refresh cannot start: project root is missing. Open Setup first.");
            OpenSettings();
            return;
        }

        var retoc = AppSettings.Current.EffectiveRetocExePath();
        var paks = AppSettings.Current.EffectiveGamePaksRoot();
        if (!File.Exists(retoc) || !Directory.Exists(paks))
        {
            AppendLog("Asset refresh needs a valid retoc.exe and game Content\\Paks folder. Opening Setup.");
            OpenSettings();
            retoc = AppSettings.Current.EffectiveRetocExePath();
            paks = AppSettings.Current.EffectiveGamePaksRoot();
            if (!File.Exists(retoc) || !Directory.Exists(paks))
            {
                AppendLog("Asset refresh cancelled: setup is still incomplete.");
                return;
            }
        }

        using var cancellation = new CancellationTokenSource();
        using var progressWindow = new AssetRefreshProgressForm();
        progressWindow.CancelRequested += (_, _) => cancellation.Cancel();
        progressWindow.Show(this);
        _refreshGameAssetsButton.Enabled = false;

        try
        {
            AppendLog($"Refreshing game assets from IoStore (profile: {profile})...");
            var progress = new Progress<GameAssetRefreshService.Progress>(progressWindow.SetProgress);
            var service = new GameAssetRefreshService(projectRoot);
            var result = await service.RefreshAsync(profile, cancellation.Token, progress);

            foreach (var line in result.Logs)
            {
                AppendLog("  " + line);
            }
            foreach (var warning in result.Warnings)
            {
                AppendLog("  validation warning: " + warning);
            }

            // Make the new version active only after extraction and validation have completed.
            AppSettings.Current.ExtractedContentRoot = result.ContentRoot;
            AppSettings.Current.Save();
            AppendLog($"New extracted Content root selected: {result.ContentRoot}");

            // Each dump is ~18 GB, so replace rather than accumulate (Settings → keep old extracts).
            PruneOldExtracts(result.OutputRoot);

            progressWindow.SetIndeterminate("Rebuilding template index...");
            await RunIndexerAsync();

            progressWindow.SetIndeterminate("Rebuilding native part index...");
            await BuildPartIndexAsync();

            progressWindow.SetFinished("Refresh complete. Re-select the base suit before packaging.");
            AppendLog($"Game asset refresh complete: {result.AssetsExtracted} assets, {result.PairsFound} pairs, UAssetAPI parsed {result.AssetsValidated}.");
            AppendLog("Re-select the playable/cutscene base in the current suit project so it uses this refreshed dump.");
            await Task.Delay(500);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Game asset refresh cancelled. The previous extracted dump remains active.");
        }
        catch (Exception ex)
        {
            AppendLog("Game asset refresh failed: " + ex.Message);
            Dialog.Error(this, "Game asset refresh failed", ex.Message);
        }
        finally
        {
            _refreshGameAssetsButton.Enabled = true;
            progressWindow.Close();
        }
    }

    /// <summary>Runs the same complete asset refresh used by the normal menu after first-time setup.</summary>
    public Task RunFirstTimeAssetExtractionAsync() =>
        RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile.AllCharacterAssets);

    private static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "CMakeLists.txt")) &&
                Directory.Exists(Path.Combine(dir, "NewSuitSlotNative")))
            {
                return dir.TrimEnd(Path.DirectorySeparatorChar);
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }
            dir = parent.FullName;
        }

        return AppSettings.DefaultProjectRoot();
    }

    private async Task RunIndexerAsync()
    {
        var projectRoot = _projectRootText.Text.Trim();
        var bundledScript = Path.Combine(AppSettings.ToolRoot, "Tools", "Build-NativeSuitTemplateIndex.ps1");
        var usesBundledScript = File.Exists(bundledScript);
        var script = usesBundledScript
            ? bundledScript
            : Path.Combine(projectRoot, "tools", "Build-NativeSuitTemplateIndex.ps1");
        if (!File.Exists(script))
        {
            AppendLog($"Indexer script not found: {script}");
            return;
        }

        AppendLog("Running template indexer...");
        _runIndexerButton.Enabled = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-ExtractedContentRoot");
            psi.ArgumentList.Add(AppSettings.Current.EffectiveExtractedContentRoot());
            psi.ArgumentList.Add("-JsonExportContentRoot");
            psi.ArgumentList.Add(AppSettings.Current.EffectiveExportContentRoot());
            psi.ArgumentList.Add("-OutputRoot");
            psi.ArgumentList.Add(Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitTemplates"));
            if (usesBundledScript)
            {
                // The portable indexer lives beside the app while a large workspace
                // may intentionally sit on a different drive.
                psi.ArgumentList.Add("-AllowExternalOutputRoot");
            }
            using var process = Process.Start(psi);
            if (process is null)
            {
                AppendLog("Failed to start powershell.");
                return;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            AppendLog(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                AppendLog(stderr.Trim());
            }
            AppendLog($"Indexer exit code: {process.ExitCode}");
            if (process.ExitCode == 0)
            {
                LoadIndex();
            }
        }
        finally
        {
            _runIndexerButton.Enabled = true;
        }
    }

    private void LoadIndex()
    {
        var projectRoot = _projectRootText.Text.Trim();
        _indexService = new TemplateIndexService(projectRoot);
        _projectService = new SuitProjectService(projectRoot);

        _playableCandidates = _indexService.LoadPlayableCandidates();
        _cutsceneCandidates = _indexService.LoadCutsceneCandidates();
        _recommendedPlan = _indexService.LoadRecommendedDonorPlan();

        _playableGrid.DataSource = _playableCandidates.Take(200).Select(CandidateRow.FromRecord).ToList();
        _cutsceneGrid.DataSource = _cutsceneCandidates.Take(200).Select(CandidateRow.FromRecord).ToList();

        AppendLog($"Loaded {_playableCandidates.Count} playable candidates.");
        AppendLog($"Loaded {_cutsceneCandidates.Count} cutscene candidates.");
        AppendLog(_recommendedPlan is null
            ? "Recommended Thomas plan not found."
            : "Recommended Thomas plan loaded.");

        LoadPartIndexAndRefreshGrid(logIfMissing: false);
    }

    private void UseRecommendedPlan()
    {
        if (_recommendedPlan is null)
        {
            LoadIndex();
        }
        if (_recommendedPlan is null)
        {
            AppendLog("No recommended plan available.");
            return;
        }

        _currentProject = PatchPlanService.CreateProjectFromRecommendedPlan(_recommendedPlan);
        ApplyProjectToFields(_currentProject);
        UpdateSelectedLabels();
        AppendLog("Applied recommended Thomas donor plan.");
    }

    private void PickPlayableFromGrid()
    {
        if (_currentProject is null || _playableGrid.CurrentRow?.DataBoundItem is not CandidateRow row)
        {
            return;
        }

        var record = _playableCandidates.FirstOrDefault(x => x.PackagePath == row.PackagePath);
        if (record is null)
        {
            return;
        }

        _currentProject.PlayableTemplate = record;
        UpdateSelectedLabels();
    }

    private void PickCutsceneFromGrid()
    {
        if (_currentProject is null || _cutsceneGrid.CurrentRow?.DataBoundItem is not CandidateRow row)
        {
            return;
        }

        var record = _cutsceneCandidates.FirstOrDefault(x => x.PackagePath == row.PackagePath);
        if (record is null)
        {
            return;
        }

        _currentProject.CutsceneTemplate = record;
        UpdateSelectedLabels();
    }

    private void SaveProject()
    {
        EnsureProject();
        if (_currentProject is null || _projectService is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var path = _projectService.SaveProject(_currentProject);
        AppendLog($"Saved project: {path}");
    }

    /// <summary>Saves the open suit from the visible builder command bar.</summary>
    private void SaveCurrentSuit()
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            ReadFieldsIntoProject(_currentProject);
            var projectService = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
            var path = projectService.SaveProject(_currentProject);
            AppendLog($"Saved suit: {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"Save suit failed: {ex.Message}");
            Dialog.Error(this, "Save suit failed", ex.Message);
        }
    }

    private void CreatePatchPlan()
    {
        EnsureProject();
        if (_currentProject is null || _projectService is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var plan = PatchPlanService.CreatePatchPlan(_currentProject);
        var projectPath = _projectService.SaveProject(_currentProject);
        var planPath = _projectService.SavePatchPlan(plan);
        AppendLog($"Saved project: {projectPath}");
        AppendLog($"Saved patch plan: {planPath}");
    }

    private void PatchNameMapsWithUAssetApi()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        try
        {
            var patchService = new UAssetPatchService(_projectRootText.Text.Trim());
            var result = patchService.CreateNameMapPatchedStage(_currentProject);
            AppendLog($"UAssetAPI patch status: {result.Status}");
            AppendLog($"Patched content root: {result.PatchedContentRoot}");
            AppendLog($"Patch report: {result.ReportPath}");

            foreach (var package in result.PackageResults)
            {
                AppendLog($"{package.Role}: success={package.Success} loaded={package.Loaded} written={package.Written} nameMapChanges={package.NameMapReplacements.Count}");
                if (!package.Success && !string.IsNullOrWhiteSpace(package.Error))
                {
                    AppendLog(package.Error);
                }
            }

            // The name-map stage was just rebuilt from clean donors, wiping any
            // staged edits - replay persisted materials + component removals so the
            // suit's saved changes survive the regenerate.
            ApplySavedMaterials(_currentProject, logIfNone: false);
            ApplySavedComponentRemovals(_currentProject, logNoRemovals: false);

            // Custom-archetype equipment anim graft (clones MAS_Char/LAS_Char, injects
            // foreign gadget anim blocks, repoints the archetype). No-op unless the
            // custom archetype is on and a foreign gadget was added.
            var animGraft = new AnimArchetypeGraftService().Graft(_currentProject, result.PatchedContentRoot);
            if (animGraft.Status != "skipped")
            {
                AppendLog($"Equipment anim graft: {animGraft.Status}");
                foreach (var line in animGraft.Log)
                {
                    AppendLog("  " + line);
                }
                if (!string.IsNullOrWhiteSpace(animGraft.Error))
                {
                    AppendLog("  " + animGraft.Error);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog("UAssetAPI patch failed:");
            AppendLog(ex.ToString());
        }
    }

    /// <summary>
    /// Rebuilds the graft stage from the CLEAN patched base and replays every declared part in
    /// <c>project.PartGrafts</c>, then re-applies saved removals + materials. This is the single
    /// authoritative path for grafting: because it always starts clean and applies exactly the
    /// declared set, parts never accumulate or produce duplicate exports across repeated drops.
    /// </summary>
    // Serializes ALL stage-file operations (rebuilds AND UseAsBase's name-map staging). Several
    // fire-and-forget callers (load-suit, use-as-base, part drop, villain transplant) can overlap
    // and race on the staged .uasset files - one path writes a package exclusively while another
    // reads/copies it ("used by another process" + half-read assets, count -1806). Everything that
    // touches a stage must hold this while doing so.
    private static readonly System.Threading.SemaphoreSlim RebuildGate = new(1, 1);

    /// <summary>Reports a packaging step to whichever progress window is active (single or bulk).</summary>
    private ProgressDialog? _packageProgress;

    /// <summary>
    /// Bulk "make everything current": for each chosen suit - rebase onto the active dump, re-stage
    /// from the new base, package, and install. This is the post-game-update chore that otherwise
    /// has to be repeated by hand for every suit.
    ///
    /// Deliberately explicit: you pick the suits, you're warned that installed paks get overwritten,
    /// one suit failing doesn't abort the rest, and a summary reports what actually happened.
    /// </summary>
    private async Task UpdateAllSuitsAsync()
    {
        var projectRoot = _projectRootText.Text.Trim();
        var newRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        if (string.IsNullOrWhiteSpace(newRoot) || !Directory.Exists(newRoot))
        {
            Dialog.Warn(this, "Update all suits", $"The active extracted Content root is not usable:\n{newRoot}\n\nSet it in Settings first.");
            return;
        }

        var svc = new SuitProjectService(projectRoot);
        var projects = svc.ListProjects().ToList();
        if (projects.Count == 0)
        {
            Dialog.Info(this, "Update all suits", "No saved suits found.");
            return;
        }

        // Pre-scan: what would rebase actually do to each suit?
        var rebaseSvc = new RebaseSuitService();
        var rows = new List<(SuitProjectService.ProjectSummary Summary, string Status)>();
        foreach (var p in projects)
        {
            var status = "ready";
            try
            {
                var proj = svc.LoadProject(p.Path);
                if (proj is null)
                {
                    status = "unreadable — will skip";
                }
                else
                {
                    var changes = rebaseSvc.Rebase(proj, newRoot, apply: false);
                    var missing = changes.Count(c => c.Status == "missing");
                    var willRebase = changes.Count(c => c.Status == "ok");
                    status = missing > 0
                        ? $"⚠ {missing} template(s) missing from dump"
                        : willRebase > 0 ? $"rebase {willRebase} template(s)" : "already current";
                }
            }
            catch (Exception ex)
            {
                status = "error: " + ex.Message;
            }
            rows.Add((p, status));
        }

        // Checklist + warning.
        using var dlg = new Form
        {
            Text = "Update all suits",
            Width = 720,
            Height = 520,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(12, 10, 12, 0),
            ForeColor = Theme.OnDarkMuted,
            Text = "For each selected suit: rebase to the active dump → re-stage from the new base → package → install.\n\n" +
                   $"Dump: {newRoot}",
        };
        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            ForeColor = Theme.OnDark,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        foreach (var (summary, status) in rows)
        {
            list.Items.Add($"{summary.DisplayName}   —   {status}", !status.StartsWith("unreadable") && !status.StartsWith("error"));
        }
        var warn = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(12, 6, 12, 0),
            ForeColor = Color.FromArgb(232, 96, 96),
            Text = "⚠ This overwrites the installed paks in your game's ~mods folder and rebuilds each suit's staging.\n" +
                   "Suits are updated one at a time; a failure is logged and the rest continue.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        var go = new Button { Text = "Update selected", Width = 140, Height = 30, DialogResult = DialogResult.OK };
        Theme.StyleGoldButton(go);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(go);
        dlg.AcceptButton = go;
        dlg.CancelButton = cancel;
        dlg.Controls.Add(list);
        dlg.Controls.Add(buttons);
        dlg.Controls.Add(warn);
        dlg.Controls.Add(info);

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var chosen = rows.Where((_, i) => list.GetItemChecked(i)).Select(r => r.Summary).ToList();
        if (chosen.Count == 0)
        {
            AppendLog("Update all suits: nothing selected.");
            return;
        }

        var okCount = 0;
        var failed = new List<string>();
        _batchMode = true;
        using var progress = new ProgressDialog(this, "Updating all suits", chosen.Count);
        _packageProgress = progress; // packaging reports its steps into this same window
        try
        {
            AppendLog($"=== Update all suits: {chosen.Count} suit(s) ===");
            var done = 0;
            foreach (var summary in chosen)
            {
                progress.SetStep($"Suit {done + 1} of {chosen.Count}: {summary.DisplayName}");
                progress.Advance(done, "Loading project…");
                AppendLog($"--- {summary.DisplayName} ---");
                try
                {
                    var project = svc.LoadProject(summary.Path);
                    if (project is null)
                    {
                        failed.Add($"{summary.DisplayName}: could not read project");
                        continue;
                    }

                    _projectService = svc;
                    LoadProjectIntoUi(project);

                    // Pin THIS suit's own pak name. Never assign _lastAutoPackageBaseName here -
                    // that would mark a custom name as auto-derived and let DeriveOutputs rename it.
                    var pinnedPak = string.IsNullOrWhiteSpace(project.PackageBaseName)
                        ? MakeSafePackageBaseName($"{project.SlotId}_P")
                        : project.PackageBaseName;
                    _packageBaseNameText.Text = pinnedPak;
                    AppendLog($"  pak name: {pinnedPak}");

                    // 1. Rebase source templates onto the active dump.
                    progress.Report("Rebasing to the active dump…");
                    var changes = rebaseSvc.Rebase(project, newRoot, apply: true);
                    if (changes.Any(c => c.Status == "missing"))
                    {
                        failed.Add($"{summary.DisplayName}: template(s) missing from the dump — skipped");
                        AppendLog("  ✗ missing template(s) in the new dump — skipping this suit.");
                        continue;
                    }
                    _basePlayableText.Text = project.PlayableTemplate?.Uasset ?? "";
                    _baseCutsceneText.Text = project.CutsceneTemplate?.Uasset ?? "";
                    _baseDcmdText.Text = project.DcmdTemplate?.Uasset ?? "";
                    svc.SaveProject(project);

                    // 2. Re-stage from the new base, 3. package, 4. install.
                    progress.Report("Re-staging from the new base…");
                    await UseAsBase();

                    // UseAsBase → DeriveOutputs can re-derive the pak name. Re-assert this suit's
                    // own name so it never builds/installs under another suit's (or a derived) name.
                    if (!string.Equals(_packageBaseNameText.Text.Trim(), pinnedPak, StringComparison.Ordinal))
                    {
                        AppendLog($"  pak name re-asserted: {pinnedPak} (was {_packageBaseNameText.Text.Trim()})");
                        _packageBaseNameText.Text = pinnedPak;
                    }

                    await PackagePatchedIoStoreAsync();

                    progress.Report($"Installing {pinnedPak} into the game…");
                    InstallTrio();

                    okCount++;
                    AppendLog($"  ✓ {summary.DisplayName} updated.");
                }
                catch (Exception ex)
                {
                    failed.Add($"{summary.DisplayName}: {ex.Message}");
                    AppendLog($"  ✗ {summary.DisplayName} failed: {ex.Message}");
                }
                finally
                {
                    done++;
                    progress.Advance(done, $"{done} of {chosen.Count} done");
                }
            }
        }
        finally
        {
            _packageProgress = null;
            _batchMode = false;
        }

        AppendLog($"=== Update all suits complete: {okCount} updated, {failed.Count} failed ===");
        Dialog.Warn(this, "Update all suits", $"Updated {okCount} of {chosen.Count} suit(s)." +
            (failed.Count > 0 ? "\n\nFailed:\n  " + string.Join("\n  ", failed) : "") +
            "\n\nTest in-game one suit at a time.");
    }

    /// <summary>
    /// Deletes only this suit's GENERATED staging output (stages + IoStore trio + runtime json).
    /// Never touches source textures, extracted game assets, or the project JSON - so stale test
    /// assets can't survive into the next package.
    /// </summary>
    private void CleanGeneratedOutputForCurrentSuit()
    {
        EnsureProject();
        if (_currentProject is null || string.IsNullOrWhiteSpace(_currentProject.SlotId))
        {
            AppendLog("Clean output: open a suit first.");
            return;
        }

        var slotRoot = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", _currentProject.SlotId);
        if (!Directory.Exists(slotRoot))
        {
            AppendLog($"Clean output: nothing generated yet for '{_currentProject.SlotId}'.");
            return;
        }

        // Explicit allow-list of generated subfolders - never delete the whole slot root, which also
        // holds the project's own saved artifacts.
        var targets = new[] { "PatchedNameMapStage", "GraftedPartStage", "GraftedTorso2Stage", "UnpatchedStage", "IoStore", "RuntimeJson" }
            .Select(name => Path.Combine(slotRoot, name))
            .Where(Directory.Exists)
            .ToList();

        if (targets.Count == 0)
        {
            AppendLog($"Clean output: nothing to clean for '{_currentProject.SlotId}'.");
            return;
        }

        if (!Dialog.Confirm(this,
                $"Delete generated output for '{_currentProject.SlotId}'?",
                string.Join("\n", targets.Select(t => "  " + Path.GetFileName(t))) +
                "\n\nThis removes staged assets and the built trio only. Your project JSON, source textures, and the extracted game dump are NOT touched.\n\n" +
                "The next Package will rebuild everything from the base.",
                confirmText: "Clean output"))
        {
            return;
        }

        var removed = 0;
        foreach (var dir in targets)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                AppendLog($"  removed {Path.GetFileName(dir)}");
                removed++;
            }
            catch (Exception ex)
            {
                AppendLog($"  ⚠ could not remove {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        AppendLog($"Clean output: removed {removed}/{targets.Count} generated folder(s). Re-stage the base, then package.");
    }

    private string EffectiveGameRootFolder()
    {
        var paksFolder = Path.GetFullPath(AppSettings.Current.EffectiveGamePaksModFolder());
        var cursor = new DirectoryInfo(paksFolder);
        while (cursor is not null)
        {
            if (cursor.Name.Equals("LEGOBatmanLotDK", StringComparison.OrdinalIgnoreCase))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(paksFolder) ?? paksFolder) ?? paksFolder) ?? paksFolder;
    }

    private sealed class GeneratedTextureStageManifest
    {
        public List<string> Files { get; set; } = new();
    }

    /// <summary>Turns a /Game package path into its object path (Package.AssetName).</summary>
    private static string ToObjectPath(string packagePath)
    {
        var pkg = packagePath.Trim();
        if (pkg.Length == 0 || pkg.Contains('.'))
        {
            return pkg;
        }
        var leaf = pkg[(pkg.LastIndexOf('/') + 1)..];
        return $"{pkg}.{leaf}";
    }

    private string EffectiveGameRuntimeSuitsFolder()
    {
        var paksFolder = Path.GetFullPath(AppSettings.Current.EffectiveGamePaksModFolder());
        var cursor = new DirectoryInfo(paksFolder);
        while (cursor is not null)
        {
            if (cursor.Name.Equals("LEGOBatmanLotDK", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(
                    cursor.FullName,
                    "Binaries",
                    "Win64",
                    "ue4ss",
                    "Mods",
                    "NewSuitSlotNative",
                    "Suits");
            }

            cursor = cursor.Parent;
        }

        return Path.Combine(
            _projectRootText.Text.Trim(),
            "Mods",
            "NewSuitSlotNative",
            "Suits");
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    // The donor tag used by the legacy bridge architecture, where every generated
    // suit shared one unlocked/default source tag and the runtime swapped the donor
    // DCMD payload per hovered button. Retained as the fallback for suits authored
    // before PawnTag became a required native-suit field.
    private const string LegacyDonorPawnTag = "Pawns.Playable.Batman.TheBatman2025";

    /// <summary>
    /// A starting pawn tag for a suit that has none, built from its display name or slot id. Only a
    /// suggestion - the author still has to accept it, and uniqueness is enforced at package time.
    /// </summary>
    // Seams for SelfTest. These two are pure functions; exposing them beats making the real
    // members public or reflecting into them.
    internal static string SuggestPawnTagForTest(NativeSuitProject project) => SuggestPawnTag(project);
    internal static string ToGameplayTagLeafForTest(string value) => ToGameplayTagLeaf(value);

    /// <summary>
    /// NativeSuitProject ships with the donor's own name as its default DisplayName/SlotId
    /// ("Thomas Wayne" / "batman_thomas"). Seeding a suggestion from those would hand every
    /// un-renamed suit the SAME tag - the collision this field exists to prevent - so they are
    /// treated as "no name yet" and the box opens empty instead.
    /// </summary>
    private static readonly string[] DonorDefaultNames = { "Thomas Wayne", "batman_thomas", "ThomasWayne" };

    private static string SuggestPawnTag(NativeSuitProject project)
    {
        var seed = Seed(project.DisplayName) ?? Seed(project.SlotId);
        var leaf = ToGameplayTagLeaf(seed ?? "");
        return string.IsNullOrWhiteSpace(leaf) ? "" : $"Pawns.Playable.Batman.{leaf}";

        static string? Seed(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !DonorDefaultNames.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)
                ? value
                : null;
    }

    private static string DerivePawnTag(NativeSuitProject project)
    {
        // Native-suit path: the suit owns its globally-unique pawn tag. Fall back to
        // the legacy donor tag only when a suit has no PawnTag set (older projects /
        // the donor-bridge codepath). See docs/native-suit-mod-bundles-...-2026-07-16.md.
        return string.IsNullOrWhiteSpace(project.PawnTag)
            ? LegacyDonorPawnTag
            : project.PawnTag.Trim();
    }

    private static string ToGameplayTagLeaf(string value)
    {
        var builder = new StringBuilder(value.Length);
        var capitalizeNext = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        return builder.ToString();
    }

    private static string? ExtractModFolder(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        const string marker = "/Mods/";
        var start = packagePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = packagePath.IndexOf('/', start);
        return end < 0 ? packagePath[start..] : packagePath[start..end];
    }

    private void EnsureProject()
    {
        if (_projectService is null)
        {
            _projectService = new SuitProjectService(_projectRootText.Text.Trim());
        }

        if (_currentProject is not null)
        {
            return;
        }

        if (_recommendedPlan is not null)
        {
            _currentProject = PatchPlanService.CreateProjectFromRecommendedPlan(_recommendedPlan);
        }
        else
        {
            _currentProject = new NativeSuitProject();
        }
        ReadFieldsIntoProject(_currentProject);
        UpdateSelectedLabels();
    }

    private void ApplyProjectToFields(NativeSuitProject project)
    {
        _slotIdText.Text = project.SlotId;
        _displayNameText.Text = project.DisplayName;
        _descriptionText.Text = project.Description;
        _targetPlayableText.Text = project.TargetPackages.Playable;
        _targetCutsceneText.Text = project.TargetPackages.Cutscene;
        _targetDcmdText.Text = project.TargetPackages.Dcmd;
        _packageBaseNameText.Text = string.IsNullOrWhiteSpace(project.PackageBaseName)
            ? MakeSafePackageBaseName($"{project.SlotId}_P")
            : project.PackageBaseName;
        _lastAutoPackageBaseName = _packageBaseNameText.Text.Trim();
    }

    private void ReadFieldsIntoProject(NativeSuitProject project)
    {
        project.SlotId = _slotIdText.Text.Trim();
        project.DisplayName = _displayNameText.Text.Trim();
        project.Description = _descriptionText.Text.Trim();
        project.PackageBaseName = CurrentPackageBaseName();
        project.TargetPackages.Playable = _targetPlayableText.Text.Trim();
        project.TargetPackages.Cutscene = _targetCutsceneText.Text.Trim();
        project.TargetPackages.Dcmd = _targetDcmdText.Text.Trim();
    }

    private void UpdateSelectedLabels()
    {
        _selectedPlayableLabel.Text = $"Playable: {_currentProject?.PlayableTemplate?.PackagePath ?? "<none>"}";
        _selectedCutsceneLabel.Text = $"Cutscene: {_currentProject?.CutsceneTemplate?.PackagePath ?? "<none>"}";
        _selectedDcmdLabel.Text = $"DCMD: {_currentProject?.DcmdTemplate?.PackagePath ?? "<none>"}";
        _selectedVisualLabel.Text = $"Visual source: {_currentProject?.VisualSourceTemplate?.PackagePath ?? "<none>"}";
    }

    /// <summary>
    /// True if a villain/NPC's own scanned part should be auto-transplanted onto the reskin donor.
    /// We graft the unique attachments (hair, hats, capes, accessories) but SKIP the shared minifig
    /// body + face - those are the same base mesh on every character and are handled by the reskin's
    /// material assignments, so grafting them would double up / fight the materials.
    /// </summary>
    /// <summary>Reduces a component/slot name to its visual KIND by stripping a trailing
    /// "_&lt;number&gt;" instance suffix (e.g. "Head_2" → "Head", "Cape" → "Cape",
    /// "CharacterMesh0" → "CharacterMesh0"). Used to match donor components against the
    /// villain's part kinds when auto-hiding parts the base character doesn't have.</summary>
    private static string VisualKindOf(string componentOrSlot)
    {
        var s = (componentOrSlot ?? string.Empty).Trim();
        var idx = s.LastIndexOf('_');
        if (idx > 0 && idx < s.Length - 1 && s[(idx + 1)..].All(char.IsDigit))
        {
            return s[..idx];
        }
        return s;
    }

    /// <summary>Component-instance model: derives a fine-grained OCCUPANCY GROUP - narrower
    /// than the broad SCS slot - from a part's slot + mesh identity. Different Head-family attachments
    /// (hair vs hat vs generic head accessory) map to DIFFERENT groups so they COEXIST; two parts of
    /// the same group REPLACE. This is what stops "drop hair" from deleting the cowl. Groups:
    /// head.scalp_hair / head.hat / head.attachment / cape.primary / torso.overlay / &lt;slot&gt;.</summary>
    private static string OccupancyGroupOf(string slot, string meshObjectPath, string meshObjectName)
    {
        var probe = (meshObjectPath ?? string.Empty) + " " + (meshObjectName ?? string.Empty);
        bool Has(string token) => probe.Contains(token, StringComparison.OrdinalIgnoreCase);
        var s = (slot ?? string.Empty).Trim();

        if (Has("/Hair/") || Has("SM_HAIR") || Has("_HAIR_") || Has("HAIR")) return "head.scalp_hair";
        if (Has("/HAT/") || Has("_HAT_") || Has("SM_HAT")) return "head.hat";
        if (s.StartsWith("Cape", StringComparison.OrdinalIgnoreCase)) return "cape.primary";
        if (s.StartsWith("Torso", StringComparison.OrdinalIgnoreCase)) return "torso.overlay";
        if (s.StartsWith("Head", StringComparison.OrdinalIgnoreCase)) return "head.attachment";
        return string.IsNullOrWhiteSpace(s) ? "misc" : s.ToLowerInvariant();
    }

    private static string OccupancyGroupOf(NativeSuitPartRecord? part) =>
        part is null ? "misc" : OccupancyGroupOf(part.Slot, part.MeshObjectPath, part.MeshObjectName);

    private static string OccupancyGroupOf(SavedPartGraftDonor? donor) =>
        donor is null ? "misc" : OccupancyGroupOf(donor.Slot, donor.MeshObjectPath, string.Empty);

    private static string GuessAttachSocket(NativeSuitPartRecord part)
    {
        if (!string.IsNullOrWhiteSpace(part.AttachSocket))
        {
            return part.AttachSocket;
        }

        return part.Slot.ToLowerInvariant() switch
        {
            "face" => "Head_Socket",
            "head" or "hair" or "hat" or "headattachment" or "hat_hair" or "customhead" => "HeadStud_Attach_Socket",
            "torso" or "torso1" or "torso2" => "Chest_Socket",
            "hip" => "Pelvis",
            _ => "Chest_Socket"
        };
    }

    private sealed class CandidateRow
    {
        public int Score { get; set; }
        public string PackagePath { get; set; } = "";
        public bool HasPair { get; set; }
        public bool HasDcmd { get; set; }
        public bool HasTorso2 { get; set; }
        public bool HasAbsoluteTorso { get; set; }
        public bool HasHair { get; set; }
        public bool HasSlickBack { get; set; }

        public static CandidateRow FromRecord(TemplateRecord record)
        {
            return new CandidateRow
            {
                Score = record.Score,
                PackagePath = record.PackagePath,
                HasPair = record.HasPair,
                HasDcmd = record.HasDcmd,
                HasTorso2 = record.Features.HasTorso2,
                HasAbsoluteTorso = record.Features.HasBatmanAbsoluteTorso,
                HasHair = record.Features.HasAnyHair,
                HasSlickBack = record.Features.HasSlickBack
            };
        }
    }

    private sealed class PartRow
    {
        public string Context { get; set; } = "";
        public string Slot { get; set; } = "";
        public string CharacterFolder { get; set; } = "";
        public string MeshKind { get; set; } = "";
        public string MeshObjectName { get; set; } = "";
        public string MaterialsSummary { get; set; } = "";
        public string SourcePackagePath { get; set; } = "";
        public string ComponentTemplateExport { get; set; } = "";

        public static PartRow FromRecord(NativeSuitPartRecord record)
        {
            return new PartRow
            {
                Context = record.Context,
                Slot = record.Slot,
                CharacterFolder = record.CharacterFolder,
                MeshKind = record.MeshKind,
                MeshObjectName = record.MeshObjectName,
                MaterialsSummary = string.Join(", ", record.Materials.Select(material => material.ObjectName).Take(3)),
                SourcePackagePath = record.SourcePackagePath,
                ComponentTemplateExport = record.ComponentTemplateExport
            };
        }
    }

    private sealed class GeneratedTextureListItem
    {
        public GeneratedTextureEntry Texture { get; }

        public GeneratedTextureListItem(GeneratedTextureEntry texture)
        {
            Texture = texture;
        }

        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Texture.DisplayName)
                ? UnrealPathUtil.AssetName(Texture.PackagePath)
                : Texture.DisplayName;
            return $"{name} - {Texture.Kind} - {Texture.PackagePath}";
        }
    }
}

