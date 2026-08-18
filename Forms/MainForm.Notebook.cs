namespace Batcomputer;

/// <summary>
/// A small mod-scoped scratchpad for donor paths and author notes. It deliberately stores plain
/// text in the mod project instead of inventing a second database or attempting to interpret the
/// pasted Unreal paths.
/// </summary>
public sealed partial class MainForm
{
    private const string NotebookTabName = "Notes";

    private readonly TextBox _modNotebookText = new();
    private readonly Label _modNotebookContext = new();
    private readonly Label _modNotebookStatus = new();
    private readonly Button _modNotebookPasteButton = new();
    private readonly Button _modNotebookCopyButton = new();
    private readonly Button _modNotebookClearButton = new();
    private readonly Button _modNotebookSaveButton = new();
    private readonly System.Windows.Forms.Timer _modNotebookSaveDebounce = new() { Interval = 650 };

    private bool _modNotebookReady;
    private bool _modNotebookLoading;
    private bool _modNotebookDirty;
    private string _modNotebookProjectPath = "";

    private Control CreateModNotebookPanel()
    {
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
        };

        var heading = new Label
        {
            AutoEllipsis = true,
            Text = "MOD NOTEBOOK",
            Font = Theme.Eyebrow,
            ForeColor = Theme.Gold,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _modNotebookContext.AutoEllipsis = true;
        _modNotebookContext.Font = Theme.Caption;
        _modNotebookContext.ForeColor = Theme.OnDarkMuted;
        _modNotebookContext.Text = "Open a mod or a suit contained in one to keep shared notes.";

        _modNotebookText.Multiline = true;
        _modNotebookText.AcceptsReturn = true;
        _modNotebookText.AcceptsTab = true;
        _modNotebookText.WordWrap = true;
        _modNotebookText.ScrollBars = ScrollBars.Vertical;
        _modNotebookText.PlaceholderText = "Paste material, texture, mesh, or asset paths here…";
        _modNotebookText.Font = Theme.Mono;
        Theme.StyleDarkInput(_modNotebookText);

        var actions = new TableLayoutPanel
        {
            Padding = new Padding(0, 2, 0, 2),
            Margin = Padding.Empty,
            BackColor = Theme.CardBg,
            ColumnCount = 2,
            RowCount = 2,
        };
        for (var i = 0; i < 2; i++)
        {
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        }
        ConfigureNotebookButton(_modNotebookPasteButton, "Paste", primary: false);
        ConfigureNotebookButton(_modNotebookCopyButton, "Copy all", primary: false);
        ConfigureNotebookButton(_modNotebookClearButton, "Clear", primary: false);
        ConfigureNotebookButton(_modNotebookSaveButton, "Save", primary: true);
        actions.Controls.Add(_modNotebookPasteButton, 0, 0);
        actions.Controls.Add(_modNotebookCopyButton, 1, 0);
        actions.Controls.Add(_modNotebookClearButton, 0, 1);
        actions.Controls.Add(_modNotebookSaveButton, 1, 1);

        _modNotebookStatus.AutoEllipsis = true;
        _modNotebookStatus.Font = Theme.Caption;
        _modNotebookStatus.ForeColor = Theme.OnDarkMuted;
        _modNotebookStatus.TextAlign = ContentAlignment.MiddleLeft;

        root.Controls.Add(heading);
        root.Controls.Add(_modNotebookContext);
        root.Controls.Add(_modNotebookText);
        root.Controls.Add(actions);
        root.Controls.Add(_modNotebookStatus);

        // The inspector can be quite narrow. Explicit bounds avoid a WinForms TableLayout edge
        // case where the multiline editor claims the footer's absolute rows after DPI scaling.
        // Two action rows also keep every label readable at the release-audit width.
        void LayoutNotebook()
        {
            const int padding = 10;
            const int headingHeight = 24;
            const int contextHeight = 42;
            const int gap = 8;
            const int actionHeight = 68;
            const int statusHeight = 24;
            var width = Math.Max(1, root.ClientSize.Width - padding * 2);
            var statusTop = Math.Max(padding + headingHeight + contextHeight + gap + 40,
                root.ClientSize.Height - padding - statusHeight);
            var actionsTop = Math.Max(padding + headingHeight + contextHeight + gap + 32,
                statusTop - actionHeight - 4);
            var editorTop = padding + headingHeight + contextHeight;
            var editorHeight = Math.Max(32, actionsTop - gap - editorTop);

            heading.SetBounds(padding, padding, width, headingHeight);
            _modNotebookContext.SetBounds(padding, padding + headingHeight, width, contextHeight);
            _modNotebookText.SetBounds(padding, editorTop, width, editorHeight);
            actions.SetBounds(padding, actionsTop, width, actionHeight);
            _modNotebookStatus.SetBounds(padding, statusTop, width, statusHeight);
        }

        root.ClientSizeChanged += (_, _) => LayoutNotebook();
        LayoutNotebook();

        _modNotebookText.TextChanged += (_, _) =>
        {
            if (_modNotebookLoading || string.IsNullOrWhiteSpace(_modNotebookProjectPath))
            {
                return;
            }

            _modNotebookDirty = true;
            _modNotebookStatus.Text = "Unsaved changes…";
            _modNotebookSaveDebounce.Stop();
            _modNotebookSaveDebounce.Start();
            UpdateNotebookButtons(hasMod: true);
        };
        _modNotebookSaveDebounce.Tick += (_, _) =>
        {
            _modNotebookSaveDebounce.Stop();
            SaveModNotebook(log: false);
        };
        _modNotebookPasteButton.Click += (_, _) => PasteIntoModNotebook();
        _modNotebookCopyButton.Click += (_, _) => CopyModNotebook();
        _modNotebookClearButton.Click += (_, _) => ClearModNotebook();
        _modNotebookSaveButton.Click += (_, _) => SaveModNotebook(log: true);

        FormClosing += (_, _) => SaveModNotebook(log: false);
        FormClosed += (_, _) => _modNotebookSaveDebounce.Dispose();

        _modNotebookReady = true;
        RefreshModNotebook(force: true);
        return root;
    }

    private static void ConfigureNotebookButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.MinimumSize = new Size(0, 28);
        button.Margin = new Padding(2);
        button.Font = Theme.Caption;
        if (primary)
        {
            Theme.StyleGoldButton(button);
        }
        else
        {
            Theme.StyleSmallDarkButton(button);
        }
    }

    /// <summary>Uses the same suit/mod precedence as the header: containing active mod first.</summary>
    private ModProjectService.ModSummary? ResolveNotebookModSummary()
    {
        var summaries = ModService.ListMods();
        var slotId = _currentProject?.SlotId?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(slotId))
        {
            var suitPath = (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim()))
                .ProjectPathForSlot(slotId);
            var matches = FindModsForSuit(suitPath, slotId);
            var selectedMatch = matches.FirstOrDefault(summary =>
                string.Equals(summary.Path, _homeActiveModProjectPath, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (selectedMatch is not null)
            {
                return selectedMatch;
            }
        }

        return string.IsNullOrWhiteSpace(_homeActiveModProjectPath)
            ? null
            : summaries.FirstOrDefault(summary =>
                string.Equals(summary.Path, _homeActiveModProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshModNotebook(bool force = false)
    {
        if (!_modNotebookReady || IsDisposed)
        {
            return;
        }

        ModProjectService.ModSummary? summary;
        try
        {
            summary = ResolveNotebookModSummary();
        }
        catch (Exception ex)
        {
            _modNotebookStatus.Text = "Could not resolve mod: " + ex.Message;
            _modNotebookStatus.ForeColor = Theme.Crit;
            return;
        }

        var nextPath = summary?.Path ?? "";
        if (!force && nextPath.Equals(_modNotebookProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SaveModNotebook(log: false);
        _modNotebookSaveDebounce.Stop();
        _modNotebookLoading = true;
        try
        {
            _modNotebookProjectPath = nextPath;
            _modNotebookDirty = false;
            _modNotebookStatus.ForeColor = Theme.OnDarkMuted;

            if (summary is null)
            {
                _modNotebookText.Text = "";
                _modNotebookText.ReadOnly = true;
                _modNotebookContext.Text = "Open a mod or a suit contained in one to keep shared notes.";
                _modNotebookStatus.Text = "Notes are stored with the mod project.";
                UpdateNotebookButtons(hasMod: false);
                return;
            }

            var mod = ModService.LoadMod(summary.Path);
            _modNotebookText.ReadOnly = false;
            _modNotebookText.Text = mod?.NotebookText ?? "";
            _modNotebookContext.Text =
                $"{summary.DisplayName} ({summary.ModId}) · shared by {summary.SuitCount} suit{(summary.SuitCount == 1 ? "" : "s")}";
            _modNotebookStatus.Text = "Auto-saves to this mod project.";
            UpdateNotebookButtons(hasMod: true);
        }
        finally
        {
            _modNotebookLoading = false;
        }
    }

    private void SaveModNotebook(bool log)
    {
        if (!_modNotebookReady || !_modNotebookDirty || string.IsNullOrWhiteSpace(_modNotebookProjectPath))
        {
            if (log && !string.IsNullOrWhiteSpace(_modNotebookProjectPath))
            {
                _modNotebookStatus.Text = "Already saved.";
            }
            return;
        }

        try
        {
            var mod = ModService.LoadMod(_modNotebookProjectPath);
            if (mod is null)
            {
                _modNotebookStatus.Text = "Could not save: the mod project is unavailable.";
                _modNotebookStatus.ForeColor = Theme.Crit;
                return;
            }

            mod.NotebookText = _modNotebookText.Text;
            _modNotebookProjectPath = ModService.SaveMod(mod);
            _modNotebookDirty = false;
            _modNotebookStatus.Text = "Saved to the mod project.";
            _modNotebookStatus.ForeColor = Theme.Good;
            UpdateNotebookButtons(hasMod: true);
            if (log)
            {
                AppendLog($"Saved notebook for mod {mod.ModId}.");
            }
        }
        catch (Exception ex)
        {
            _modNotebookStatus.Text = "Could not save: " + ex.Message;
            _modNotebookStatus.ForeColor = Theme.Crit;
        }
    }

    private void PasteIntoModNotebook()
    {
        if (_modNotebookText.ReadOnly)
        {
            return;
        }

        try
        {
            var pasted = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(pasted))
            {
                _modNotebookStatus.Text = "The clipboard does not contain text.";
                return;
            }

            if (_modNotebookText.TextLength > 0 && !_modNotebookText.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                _modNotebookText.AppendText(Environment.NewLine);
            }
            _modNotebookText.AppendText(pasted);
            _modNotebookText.SelectionStart = _modNotebookText.TextLength;
            _modNotebookText.ScrollToCaret();
            _modNotebookText.Focus();
        }
        catch (Exception ex)
        {
            _modNotebookStatus.Text = "Paste failed: " + ex.Message;
            _modNotebookStatus.ForeColor = Theme.Crit;
        }
    }

    private void CopyModNotebook()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_modNotebookText.Text))
            {
                Clipboard.SetText(_modNotebookText.Text);
                _modNotebookStatus.Text = "Notebook copied.";
                _modNotebookStatus.ForeColor = Theme.Good;
            }
        }
        catch (Exception ex)
        {
            _modNotebookStatus.Text = "Copy failed: " + ex.Message;
            _modNotebookStatus.ForeColor = Theme.Crit;
        }
    }

    private void ClearModNotebook()
    {
        if (_modNotebookText.ReadOnly || string.IsNullOrEmpty(_modNotebookText.Text))
        {
            return;
        }

        if (Dialog.Confirm(this, "Clear mod notebook",
                "Remove every note and saved asset path from this mod?",
                "Clear", "Keep notes", Dialog.Level.Warn))
        {
            _modNotebookText.Clear();
        }
    }

    private void UpdateNotebookButtons(bool hasMod)
    {
        _modNotebookPasteButton.Enabled = hasMod;
        _modNotebookCopyButton.Enabled = hasMod && _modNotebookText.TextLength > 0;
        _modNotebookClearButton.Enabled = hasMod && _modNotebookText.TextLength > 0;
        _modNotebookSaveButton.Enabled = hasMod && _modNotebookDirty;
    }

    /// <summary>Populates the real notebook controls for the release screenshot audit.</summary>
    internal void ConfigureModNotebookForUiAudit()
    {
        if (!_modNotebookReady)
        {
            return;
        }

        _modNotebookLoading = true;
        try
        {
            _modNotebookProjectPath = @"C:\Audit\UiAuditMod.native-suit-mod-project.json";
            _modNotebookDirty = false;
            _modNotebookText.ReadOnly = false;
            _modNotebookText.Text =
                "/Game/Characters/Attachments/Face/FACE_Batman/MI_FACE_Batman_NoEyes\r\n" +
                "/Game/Characters/Textures/Attachments/LEGOface/T_LOWER_UNDER_Joker_Batman89_DIST_BC\r\n\r\n" +
                "Joker '89 lower print on the standard Batman no-eyes donor.";
            _modNotebookContext.Text = "UI Audit Mod (UiAuditMod) · shared by 2 suits";
            _modNotebookStatus.Text = "Auto-saves to this mod project.";
            _modNotebookStatus.ForeColor = Theme.OnDarkMuted;
            UpdateNotebookButtons(hasMod: true);
        }
        finally
        {
            _modNotebookLoading = false;
        }
    }
}
