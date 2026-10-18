namespace Batcomputer;

/// <summary>Batch settings only. Cooking belongs to the queue under Your Character.</summary>
internal sealed class TextureImportQueueForm : AdaptiveForm
{
    private sealed record TextureRow(TextureImportJob Job, RoundedPanel Card, CheckBox Selected,
        TextBox Name, ThemedDropDown Kind, ThemedDropDown Profile);

    private readonly List<TextureRow> _rows = [];
    private readonly Panel _files = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _fileCards = new() { Dock = DockStyle.Top, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Margin = Padding.Empty };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, Font = Theme.Caption, AutoEllipsis = true };
    private readonly Label _count = new() { AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Textures,
        Margin = new Padding(12, 10, 0, 0) };
    private readonly Button _import = new() { Text = "Import textures", AutoSize = true, Height = 36 };
    private readonly CheckBox _experimental = new() { Text = "Allow experimental profiles", AutoSize = true,
        Margin = new Padding(0, 11, 18, 0), Font = Theme.Caption };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private readonly Func<string, IReadOnlyList<TextureImportProfile>> _profiles;
    private readonly Func<string, string> _safeName;
    private readonly HashSet<string> _occupied;
    private readonly string[] _kinds;
    private TextureRow? _active;
    private bool _updating;
    internal IReadOnlyList<TextureImportJob> Jobs => _rows.Select(r => r.Job).ToArray();

    internal TextureImportQueueForm(string suitName, IEnumerable<string> sources, string[] kinds,
        IEnumerable<string> occupiedNames, Func<string, string> safeName,
        Func<string, IReadOnlyList<TextureImportProfile>> profiles)
    {
        if (kinds.Length == 0) throw new ArgumentException("At least one texture type is required.", nameof(kinds));
        _profiles = profiles; _safeName = safeName; _kinds = kinds;
        _occupied = new(occupiedNames.Select(safeName), StringComparer.OrdinalIgnoreCase);
        Text = "Batcomputer · Batch import textures";
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 720); MinimumSize = new Size(960, 580);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        ShowInTaskbar = false; AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 88)); layout.RowStyles.Add(new(SizeType.Absolute, 44));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 86));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        header.RowStyles.Add(new(SizeType.Absolute, 22)); header.RowStyles.Add(new(SizeType.Absolute, 32));
        header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "TEXTURES  /  IMPORT", ForeColor = Theme.Textures,
            Font = Theme.Eyebrow, Dock = DockStyle.Fill }, 0, 0);
        header.Controls.Add(new Label { Text = "Batch import textures", Font = Theme.Title, Dock = DockStyle.Fill }, 0, 1);
        header.Controls.Add(new Label { Text = $"Into {suitName} · Set each texture's type and profile. Import closes this window and queues them under Your Character.",
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        tools.Controls.AddRange([
            MakeButton("+ Add PNGs…", (_, _) => ChooseFiles()),
            MakeButton("Select all", (_, _) => SelectAll()),
            MakeButton("Remove selected", (_, _) => RemoveSelected()),
            MakeButton("Apply active settings to selected", (_, _) => ApplySelectedSettings()), _count]);
        _tips.SetToolTip(tools.Controls[3], "Click a texture to make it active, check the destination rows, then copy its type and cook profile to them.");

        var profileCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0, 8, 0, 8),
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.Radius };
        profileCard.Controls.Add(_detail);
        var cancel = MakeButton("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
        footer.Controls.AddRange([_import, cancel, _experimental]);
        Theme.StyleGoldButton(_import); _import.Padding = new Padding(16, 5, 16, 5);
        _import.Click += (_, _) => Submit();
        CancelButton = cancel;
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(tools, 0, 1); layout.Controls.Add(_files, 0, 2);
        layout.Controls.Add(profileCard, 0, 3); layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
        _files.Controls.Add(_fileCards);
        _files.ClientSizeChanged += (_, _) => SizeCards();
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
        Shown += (_, _) => { Theme.UseDarkTitleBar(this); SizeCards(); };
        AddFiles(sources);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 8, 0), Font = Theme.Caption };
        Theme.StyleDarkButton(button); button.Click += click; return button;
    }

    private void ChooseFiles()
    {
        using var picker = new OpenFileDialog { Title = "Add textures", Filter = "PNG images (*.png)|*.png", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) AddFiles(picker.FileNames);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        _files.SuspendLayout();
        _fileCards.SuspendLayout();
        try
        {
            foreach (var path in paths)
            {
                if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                var fullPath = Path.GetFullPath(path);
                if (_rows.Any(r => r.Job.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                var stem = _safeName(Path.GetFileNameWithoutExtension(path));
                var name = stem; var i = 2;
                while (_occupied.Contains(name) || _rows.Any(r => r.Job.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{stem}_{i++}";
                var kind = MainForm.GuessTextureImportKind(stem);
                if (!_kinds.Contains(kind)) kind = _kinds[0];
                var job = new TextureImportJob { SourcePath = fullPath, Name = name, Kind = kind };
                var row = CreateRow(job);
                _rows.Add(row); _fileCards.Controls.Add(row.Card);
                LoadProfiles(row);
                if (_active is null) Activate(row);
            }
        }
        finally { _fileCards.ResumeLayout(); _files.ResumeLayout(); }
        SizeCards(); UpdateCount(); ShowSelectedDetail();
    }

    private TextureRow CreateRow(TextureImportJob job)
    {
        var card = new RoundedPanel { Height = 126, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 10),
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.Radius };
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, BackColor = Color.Transparent };
        columns.ColumnStyles.Add(new(SizeType.Absolute, 28)); columns.ColumnStyles.Add(new(SizeType.Absolute, 80));
        columns.ColumnStyles.Add(new(SizeType.Percent, 100));
        var selected = new CheckBox { Dock = DockStyle.Fill, AutoSize = false, Text = "", Margin = Padding.Empty,
            AccessibleName = $"Select {Path.GetFileName(job.SourcePath)}", Cursor = Cursors.Hand };
        var preview = new PictureBox { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 12, 8),
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SlateDark };
        var metadata = LoadPreview(job.SourcePath, preview);
        preview.Disposed += (_, _) => preview.Image?.Dispose();
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        content.RowStyles.Add(new(SizeType.Absolute, 30)); content.RowStyles.Add(new(SizeType.Percent, 100));
        var fileHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        fileHeader.ColumnStyles.Add(new(SizeType.Percent, 66)); fileHeader.ColumnStyles.Add(new(SizeType.Percent, 34));
        var fileName = new Label { Text = Path.GetFileName(job.SourcePath), Dock = DockStyle.Fill, Font = Theme.BodyStrong,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var info = new Label { Text = metadata, Dock = DockStyle.Fill, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = Padding.Empty };
        fileHeader.Controls.Add(fileName, 0, 0); fileHeader.Controls.Add(info, 1, 0);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        settings.ColumnStyles.Add(new(SizeType.Percent, 30)); settings.ColumnStyles.Add(new(SizeType.Percent, 27));
        settings.ColumnStyles.Add(new(SizeType.Percent, 43));
        settings.RowStyles.Add(new(SizeType.Absolute, 22)); settings.RowStyles.Add(new(SizeType.Absolute, 36));
        foreach (var (label, index) in new[] { ("TEXTURE NAME", 0), ("TEXTURE TYPE", 1), ("COOK PROFILE", 2) })
            settings.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, Font = Theme.Eyebrow,
                ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, index, 0);
        var name = new TextBox { Text = job.Name, BorderStyle = BorderStyle.None, Dock = DockStyle.Top,
            BackColor = Theme.CardHi, ForeColor = Theme.OnDark, Font = Theme.Body, AccessibleName = "Texture name" };
        var nameHost = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.CardHi, BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm, Padding = new Padding(10, 8, 10, 4), Margin = new Padding(0, 0, 10, 0) };
        nameHost.Controls.Add(name);
        var kind = new ThemedDropDown { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), AccessibleName = "Texture type" };
        kind.Items.AddRange(_kinds.Cast<object>()); kind.SelectedItem = job.Kind;
        var profile = new ThemedDropDown { Dock = DockStyle.Fill, Margin = Padding.Empty, Placeholder = "No compatible profile", AccessibleName = "Cook profile" };
        settings.Controls.Add(nameHost, 0, 1); settings.Controls.Add(kind, 1, 1); settings.Controls.Add(profile, 2, 1);
        content.Controls.Add(fileHeader, 0, 0); content.Controls.Add(settings, 0, 1);
        columns.Controls.Add(selected, 0, 0); columns.Controls.Add(preview, 1, 0); columns.Controls.Add(content, 2, 0);
        card.Controls.Add(columns);
        var row = new TextureRow(job, card, selected, name, kind, profile);
        _tips.SetToolTip(fileName, job.SourcePath); _tips.SetToolTip(preview, job.SourcePath);
        _tips.SetToolTip(info, metadata); _tips.SetToolTip(name, "Unique package name: letters, numbers and underscores.");
        name.TextChanged += (_, _) => job.Name = name.Text.Trim();
        kind.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            job.Kind = kind.SelectedItem?.ToString() ?? _kinds[0]; LoadProfiles(row); Activate(row);
        };
        profile.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            job.Profile = profile.SelectedItem as TextureImportProfile; UpdateProfileDetail(row); Activate(row);
        };
        selected.CheckedChanged += (_, _) => { PaintSelection(row); UpdateCount(); };
        foreach (var control in new Control[] { card, fileName, info, preview }) control.Click += (_, _) => Activate(row);
        foreach (var control in new Control[] { name, kind, profile }) control.Enter += (_, _) => Activate(row);
        return row;
    }

    private static string LoadPreview(string path, PictureBox preview)
    {
        var size = "Source unavailable";
        try
        {
            var bytes = new FileInfo(path).Length;
            size = bytes >= 1048576 ? $"{bytes / 1048576d:0.#} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB";
            // Retain only a thumbnail, never a source-sized bitmap or an open PNG handle.
            using var source = Image.FromFile(path);
            var scale = 64d / Math.Max(source.Width, source.Height);
            var thumbnail = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            try
            {
                using var graphics = Graphics.FromImage(thumbnail);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(Point.Empty, thumbnail.Size));
                preview.Image = thumbnail;
            }
            catch { thumbnail.Dispose(); throw; }
            return $"{source.Width} × {source.Height} px · {size}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return $"Preview unavailable · {size}";
        }
    }

    private void SizeCards()
    {
        var width = Math.Max(100, _files.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 3);
        foreach (var row in _rows) if (row.Card.Width != width) row.Card.Width = width;
    }

    private void LoadProfiles(TextureRow row, string? preferredId = null)
    {
        _updating = true;
        try
        {
            row.Profile.SelectedIndex = -1; row.Profile.Items.Clear();
            var profiles = _profiles(row.Job.Kind);
            row.Profile.Items.AddRange(profiles.Cast<object>());
            row.Job.Profile = profiles.FirstOrDefault(p => p.Id == preferredId) ?? profiles.FirstOrDefault();
            row.Profile.SelectedItem = row.Job.Profile; row.Profile.Invalidate();
            UpdateProfileDetail(row);
        }
        finally { _updating = false; }
    }

    private void UpdateProfileDetail(TextureRow row)
    {
        row.Job.Detail = row.Job.Profile?.Note ?? "No compatible donor available. Refresh game assets before importing this type.";
        _tips.SetToolTip(row.Profile, row.Job.Profile is { } p ? $"{p}\n{p.Note}" : row.Job.Detail);
    }

    private void Activate(TextureRow row)
    {
        var previous = _active; _active = row;
        if (previous is not null) PaintSelection(previous);
        PaintSelection(row); ShowSelectedDetail();
    }

    private void PaintSelection(TextureRow row)
    {
        row.Card.BorderColor = ReferenceEquals(_active, row) ? Theme.Textures : row.Selected.Checked ? Theme.SlateLight : Theme.LineSoft;
        row.Card.Invalidate();
    }

    private void SelectAll()
    {
        var check = _rows.Any(r => !r.Selected.Checked);
        foreach (var row in _rows) row.Selected.Checked = check;
    }

    private void RemoveSelected()
    {
        foreach (var row in _rows.Where(r => r.Selected.Checked).ToArray())
        {
            _rows.Remove(row); _fileCards.Controls.Remove(row.Card); row.Card.Dispose();
            if (ReferenceEquals(_active, row)) _active = null;
        }
        if (_active is null && _rows.Count > 0) Activate(_rows[0]);
        UpdateCount(); ShowSelectedDetail();
    }

    private void ApplySelectedSettings()
    {
        if (_active is not { } source) return;
        foreach (var row in _rows.Where(r => r.Selected.Checked && !ReferenceEquals(r, source)))
        {
            _updating = true;
            row.Job.Kind = source.Job.Kind; row.Kind.SelectedItem = row.Job.Kind;
            LoadProfiles(row, source.Job.Profile?.Id);
        }
        ShowSelectedDetail();
    }

    private void UpdateCount()
    {
        var selected = _rows.Count(r => r.Selected.Checked);
        _count.Text = $"{_rows.Count} PNGs" + (selected > 0 ? $" · {selected} selected" : "");
        _import.Text = _rows.Count == 1 ? "Import texture" : $"Import {_rows.Count} textures";
        _import.Enabled = _rows.Count > 0;
    }

    private void ShowSelectedDetail()
    {
        _detail.ForeColor = Theme.OnDarkMuted;
        _detail.Text = _active?.Job is { } job
            ? $"{job.Profile}\n{job.Detail}" : "Add or drop PNGs here. Choose each texture's type and native cook profile before importing.";
    }

    private bool Reject(TextureRow row, string message)
    {
        Activate(row); _files.ScrollControlIntoView(row.Card);
        _detail.ForeColor = Theme.Textures; _detail.Text = message;
        return false;
    }

    private bool Submit()
    {
        var used = new HashSet<string>(_occupied, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var job = row.Job;
            if (string.IsNullOrWhiteSpace(job.Name) || _safeName(job.Name) != job.Name || !used.Add(job.Name))
                return Reject(row, $"Use a unique texture name with letters, numbers and underscores: {job.Name}");
            if (job.Profile is null) return Reject(row, $"{job.Name}: choose an available cook profile first.");
            if (!job.Profile.Verified && !_experimental.Checked)
                return Reject(row, $"{job.Name}: enable experimental profiles to use this profile.");
        }
        if (_rows.Count == 0) return false;
        DialogResult = DialogResult.OK; Close(); return true;
    }

    internal bool SubmitForAudit() => Submit();
    internal bool HasHorizontalScrollForAudit => _files.HorizontalScroll.Visible;
    internal void SetKindForAudit(int index, string kind) => _rows[index].Kind.SelectedItem = kind;
    internal void ApplySettingsForAudit(int source, params int[] targets)
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Selected.Checked = targets.Contains(i);
        Activate(_rows[source]); ApplySelectedSettings();
    }
}
