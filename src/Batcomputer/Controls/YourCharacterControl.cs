namespace Batcomputer;

/// <summary>
/// The "Your Character" panel extracted into a designer-editable control that
/// owns the component-row flow. MainForm keeps the row-building logic (PopulateToyboxSlots) and the
/// drag-drop wiring, operating on the exposed <see cref="SlotFlow"/> - the safe structural step,
/// matching the Inspector extraction.
/// </summary>
public partial class YourCharacterControl : UserControl
{
    private readonly Panel _textureImportPanel = new RoundedPanel { Dock = DockStyle.Bottom, Height = 104, Visible = false, Padding = new Padding(12), CornerRadius = Theme.Radius };
    private readonly Label _textureImportLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly ThemedProgressBar _textureImportProgress = new() { Dock = DockStyle.Bottom, Height = 10,
        BackColor = Theme.CardBg, TrackColor = Theme.SlateDark, TrackBorderColor = Theme.FrameLine };
    private readonly Label _textureImportHeading = new() { Dock = DockStyle.Top, Height = 22, Text = "TEXTURE IMPORT", Font = Theme.Eyebrow };
    private readonly ListBox _textureQueueList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36, IntegralHeight = false, BackColor = Theme.CardBg, ForeColor = Theme.OnDark };
    private readonly Button _textureQueueToggle = new() { Text = "Stop", Width = 80, Height = 26 };
    private readonly Button _textureQueueRetry = new() { Text = "Retry", Width = 56, Height = 26 };
    private readonly Button _textureQueueClear = new() { Text = "Clear", Width = 56, Height = 26 };
    private readonly ToolTip _textureQueueTip = new();
    private int _textureQueueHover = -1;
    internal event EventHandler? TextureQueueToggleRequested;
    internal event EventHandler? TextureQueueRetryRequested;
    internal event EventHandler? TextureQueueClearRequested;

    internal void ShowTextureQueue(IReadOnlyList<TextureImportJob> jobs, bool running, bool paused)
    {
        _textureImportPanel.Visible = jobs.Count > 0;
        _textureImportPanel.Height = jobs.Count > 1 ? 236 : 200;
        var active = jobs.FirstOrDefault(j => j.State == TextureImportState.Importing);
        var waiting = jobs.Count(j => j.State == TextureImportState.Queued);
        _textureImportHeading.Text = $"TEXTURE QUEUE · {waiting} WAITING";
        var completed = jobs.Count(j => j.State == TextureImportState.Complete);
        var failed = jobs.Count(j => j.State == TextureImportState.Failed);
        _textureImportLabel.Text = active is not null ? $"{active.Name}\n{active.Detail}"
            : paused ? "Queue stopped · continue when ready"
            : $"{completed} imported" + (failed > 0 ? $" · {failed} failed (hover for details)" : " · saved to the suit");
        _textureImportProgress.Indeterminate = false;
        _textureImportProgress.Value = active?.Percent ?? (waiting == 0 && failed == 0 ? 100 : 0);
        if (_textureQueueList.Items.Count != jobs.Count ||
            !_textureQueueList.Items.Cast<object>().Zip(jobs).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            var selected = _textureQueueList.SelectedItem;
            var top = _textureQueueList.TopIndex;
            _textureQueueList.BeginUpdate();
            _textureQueueList.Items.Clear();
            _textureQueueList.Items.AddRange(jobs.Cast<object>().ToArray());
            if (selected is not null && _textureQueueList.Items.Contains(selected)) _textureQueueList.SelectedItem = selected;
            if (jobs.Count > 0) _textureQueueList.TopIndex = Math.Clamp(top, 0, jobs.Count - 1);
            _textureQueueList.EndUpdate();
        }
        else _textureQueueList.Invalidate();
        _textureQueueHover = -1;
        _textureQueueToggle.Text = running ? (paused ? "Stopping" : "Stop") : "Continue";
        _textureQueueToggle.Enabled = running ? !paused : waiting > 0 || jobs.Any(j => j.State == TextureImportState.Cancelled);
        _textureQueueRetry.Enabled = !running && jobs.Any(j => j.State is TextureImportState.Failed or TextureImportState.Cancelled);
        _textureQueueClear.Enabled = jobs.Any(j => j.State is TextureImportState.Complete or TextureImportState.Failed or TextureImportState.Cancelled);
        _textureImportPanel.SendToBack();
    }

    public YourCharacterControl()
    {
        InitializeComponent();
        // Darker ground so the CardBg rows below read as separated cards.
        BackColor = Theme.PanelBg;
        _textureImportPanel.BackColor = Theme.CardBg;
        _textureImportLabel.ForeColor = Theme.OnDark;
        _textureImportLabel.Font = Theme.Caption;
        _textureImportHeading.ForeColor = Theme.Textures;
        var queueLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        queueLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 22, 38, 10 }) queueLayout.RowStyles.Add(new(SizeType.Absolute, height));
        queueLayout.RowStyles.Add(new(SizeType.Percent, 100));
        queueLayout.RowStyles.Add(new(SizeType.Absolute, 30));
        var queueActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in new[] { _textureQueueToggle, _textureQueueRetry, _textureQueueClear })
        {
            Theme.StyleDarkButton(button); button.Font = Theme.Caption; button.Margin = new Padding(0, 3, 3, 0);
            queueActions.Controls.Add(button);
        }
        queueLayout.Controls.Add(_textureImportHeading, 0, 0);
        queueLayout.Controls.Add(_textureImportLabel, 0, 1);
        queueLayout.Controls.Add(_textureImportProgress, 0, 2);
        queueLayout.Controls.Add(_textureQueueList, 0, 3);
        queueLayout.Controls.Add(queueActions, 0, 4);
        _textureImportPanel.Controls.Add(queueLayout);
        _textureQueueToggle.Click += (_, _) => TextureQueueToggleRequested?.Invoke(this, EventArgs.Empty);
        _textureQueueRetry.Click += (_, _) => TextureQueueRetryRequested?.Invoke(this, EventArgs.Empty);
        _textureQueueClear.Click += (_, _) => TextureQueueClearRequested?.Invoke(this, EventArgs.Empty);
        _textureQueueTip.SetToolTip(_textureQueueToggle, "Stop safely after the current encoder operation. Continue restarts the interrupted texture.");
        _textureQueueTip.SetToolTip(_textureQueueClear, "Clear finished, failed and cancelled entries from this list. Imported textures remain saved.");
        _textureQueueList.DrawItem += DrawTextureQueueItem;
        _textureQueueList.MouseMove += (_, e) =>
        {
            var index = _textureQueueList.IndexFromPoint(e.Location);
            if (index == _textureQueueHover) return;
            _textureQueueHover = index;
            var job = index >= 0 ? _textureQueueList.Items[index] as TextureImportJob : null;
            _textureQueueTip.SetToolTip(_textureQueueList, job is null ? "" :
                $"{job.Name} · {job.OwnerLabel}\n{job.Profile}\n{job.Detail}\n{job.OutputPath}" +
                (job.OutputPath.Length > 0 ? "\nDouble-click to copy the package path." : ""));
        };
        _textureQueueList.MouseDoubleClick += (_, e) =>
        {
            var index = _textureQueueList.IndexFromPoint(e.Location);
            if (index >= 0 && _textureQueueList.Items[index] is TextureImportJob { OutputPath.Length: > 0 } job)
                try { Clipboard.SetText(job.OutputPath); } catch { }
        };
        Disposed += (_, _) => _textureQueueTip.Dispose();
        Controls.Add(_textureImportPanel);
        _textureImportPanel.SendToBack();
        Padding = new Padding(6);
        _slotFlow.BackColor = Theme.PanelBg;
        _titleLabel.ForeColor = Theme.Gold;
        _titleLabel.Font = Theme.Eyebrow;
        _diagram.BackColor = Theme.PanelBg;
        _minifigActions.BackColor = Theme.PanelBg;
        Theme.StyleDarkButton(_viewIn3dButton);
        _viewIn3dButton.FlatAppearance.BorderColor = Theme.Research;
        _viewIn3dButton.FlatAppearance.MouseOverBackColor = Theme.Tint(Theme.Research);
        _viewIn3dButton.ForeColor = Theme.Research;
        _viewIn3dButton.Font = Theme.Caption;
        _viewIn3dButton.Click += (_, _) => ViewIn3DRequested?.Invoke(this, EventArgs.Empty);
        _viewIn3dIcon.Click += (_, _) => _viewIn3dButton.PerformClick();
        if (EmbeddedAssets.LoadAnimated("3D.gif") is { } animatedIcon)
        {
            _viewIn3dIcon.Image = animatedIcon;
        }
        _minifigActions.BringToFront();
        SizeChanged += (_, _) => LayoutMinifigActions();
        LayoutMinifigActions();
    }

    private void DrawTextureQueueItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _textureQueueList.Items[e.Index] is not TextureImportJob job) return;
        using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.Slate : Theme.CardBg);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var color = job.State switch { TextureImportState.Complete => Theme.Good, TextureImportState.Failed => Theme.Crit,
            TextureImportState.Cancelled => Theme.Warn, _ => Theme.OnDarkMuted };
        var status = job.State == TextureImportState.Importing ? $"{job.Percent}%" : job.State.ToString();
        TextRenderer.DrawText(e.Graphics, status, Theme.Caption,
            new Rectangle(e.Bounds.Right - 70, e.Bounds.Top + 2, 68, 16), color, TextFormatFlags.Right | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(e.Graphics, job.Name, Theme.Caption,
            new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 2, Math.Max(1, e.Bounds.Width - 76), 16), Theme.OnDark,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, job.Profile?.Label ?? job.Kind, Theme.Caption,
            new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 18, Math.Max(1, e.Bounds.Width - 6), 16), Theme.OnDarkMuted,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The vertical flow of character-component rows (added/removed by PopulateToyboxSlots).</summary>
    public FlowLayoutPanel SlotFlow => _slotFlow;

    /// <summary>The minifig figure (region tint + click-to-select).</summary>
    public MinifigDiagram Diagram => _diagram;

    /// <summary>Raised when the minifig panel's preview command is selected.</summary>
    public event EventHandler? ViewIn3DRequested;

    /// <summary>
    /// Switches between the minifig figure and the classic slot list (Settings → General →
    /// "Show the character as a minifig"). Only one is ever visible.
    /// </summary>
    public void SetMinifigMode(bool useMinifig)
    {
        _diagram.Visible = useMinifig;
        _slotFlow.Visible = !useMinifig;
        _minifigActions.Visible = useMinifig;
        _minifigActions.BringToFront();
    }

    private void LayoutMinifigActions()
    {
        _minifigActions.Left = Math.Max(Padding.Left, ClientSize.Width - _minifigActions.Width - Padding.Right);
        _minifigActions.Top = 2;
    }
}
