namespace Batcomputer;

/// <summary>Readable review list with persistent selection, status filtering and explicit edit actions.</summary>
internal sealed class ChangeReviewControl : UserControl
{
    private readonly DataGridView _list = new();
    private readonly TextBox _detail = new();
    private readonly Label _summary = new();
    private readonly ComboBox _status = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Button _remove = new() { Text = "Remove selected edit", AutoSize = true };
    private readonly Button _copy = new() { Text = "Copy selected detail", AutoSize = true };
    private IReadOnlyList<SavedChange> _changes = Array.Empty<SavedChange>();
    private Func<SavedChange, Task>? _removeAction;
    private string _name = "";

    internal ChangeReviewControl()
    {
        Dock = DockStyle.Fill; BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 62)); root.RowStyles.Add(new(SizeType.Percent, 62));
        root.RowStyles.Add(new(SizeType.Percent, 38)); root.RowStyles.Add(new(SizeType.Absolute, 42));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100)); heading.ColumnStyles.Add(new(SizeType.Absolute, 162));
        _summary.Dock = DockStyle.Fill; _summary.Font = Theme.BodyStrong; _summary.ForeColor = Theme.OnDark;
        _status.Items.AddRange(["All statuses", "applied", "staged", "Other"]); _status.SelectedIndex = 0;
        _status.BackColor = Theme.Slate; _status.ForeColor = Theme.OnDark; _status.FlatStyle = FlatStyle.Flat;
        _status.AccessibleName = "Filter review by recorded status";
        _status.SelectedIndexChanged += (_, _) => Refill();
        heading.Controls.Add(_summary, 0, 0); heading.Controls.Add(_status, 1, 0); root.Controls.Add(heading, 0, 0);
        _list.Dock = DockStyle.Fill; _list.ReadOnly = true; _list.AllowUserToAddRows = false; _list.AllowUserToDeleteRows = false;
        _list.AllowUserToResizeRows = false; _list.RowHeadersVisible = false; _list.MultiSelect = false;
        _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _list.AutoGenerateColumns = false;
        _list.BackgroundColor = Theme.SlateDark; _list.BorderStyle = BorderStyle.FixedSingle; _list.GridColor = Theme.LineSoft;
        _list.EnableHeadersVisualStyles = false;
        _list.ColumnHeadersDefaultCellStyle = new() { BackColor = Theme.Slate, ForeColor = Theme.OnDark, Font = Theme.BodyStrong };
        _list.DefaultCellStyle = new() { BackColor = Theme.SlateDark, ForeColor = Theme.OnDark, SelectionBackColor = Theme.SlateLight, SelectionForeColor = Theme.OnDark, Font = Theme.Body };
        _list.AlternatingRowsDefaultCellStyle.BackColor = Theme.WindowBg;
        foreach (var (name, weight) in new[] { ("Category", 20), ("Target", 30), ("Status", 15), ("Recorded", 20) })
            _list.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        _list.SelectionChanged += (_, _) => ShowSelection(); root.Controls.Add(_list, 0, 1);
        _detail.Dock = DockStyle.Fill; _detail.Multiline = true; _detail.ReadOnly = true; _detail.ScrollBars = ScrollBars.Vertical;
        _detail.BackColor = Theme.Slate; _detail.ForeColor = Theme.OnDark; _detail.Font = Theme.Body;
        _detail.BorderStyle = BorderStyle.FixedSingle; _detail.AccessibleName = "Selected change details";
        root.Controls.Add(_detail, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        Theme.StyleDarkButton(_copy); Theme.StyleDarkButton(_remove);
        _copy.Click += (_, _) => { if (_detail.Text.Length > 0) try { Clipboard.SetText(_detail.Text); } catch (Exception ex) { Dialog.Error(FindForm(), "Copy failed", ex.Message); } };
        _remove.Click += async (_, _) =>
        {
            if (Selected is not { } change || _removeAction is null) return;
            _remove.Enabled = false;
            try { await _removeAction(change); }
            catch (Exception ex) { Dialog.Error(FindForm(), "Could not remove edit", ex.Message); }
            finally { if (!IsDisposed) ShowSelection(); }
        };
        actions.Controls.Add(_copy); actions.Controls.Add(_remove); root.Controls.Add(actions, 0, 3); Controls.Add(root);
    }

    private SavedChange? Selected => _list.CurrentRow?.Tag as SavedChange;
    internal void ShowChanges(string name, IReadOnlyList<SavedChange> changes, Func<SavedChange, Task> remove)
    { _name = name; _changes = changes; _removeAction = remove; Refill(); }

    private void Refill()
    {
        var previous = Selected;
        var filter = _status.SelectedItem?.ToString() ?? "All statuses";
        var rows = _changes.Where(c => filter == "All statuses" || (filter == "Other" ? c.Status is not "applied" and not "staged" : c.Status.Equals(filter, StringComparison.OrdinalIgnoreCase)));
        if (AppSettings.Current.ReviewGroupByCategory) rows = rows.OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase);
        _list.Rows.Clear();
        _list.Columns["Recorded"]!.Visible = AppSettings.Current.ReviewShowTimestamps;
        foreach (var change in rows)
        {
            var index = _list.Rows.Add(change.Category, change.Target, change.Status, FormatWhen(change.When));
            _list.Rows[index].Tag = change;
            if (ReferenceEquals(change, previous)) _list.CurrentCell = _list.Rows[index].Cells[0];
        }
        _summary.Text = _name + $" — {_list.Rows.Count} edits shown\nRecorded intent, not a build or in-game test certificate. Search above to narrow results.";
        ShowSelection();
    }

    private void ShowSelection()
    {
        _copy.Enabled = _remove.Enabled = Selected is not null;
        _detail.Text = Selected is { } c ? $"{c.Category} · {c.Target}\r\n\r\n{c.Detail}\r\n\r\nRecorded status: {c.Status}" +
            (AppSettings.Current.ReviewShowTimestamps ? $"\r\nRecorded: {FormatWhen(c.When)}" : "") +
            "\r\n\r\nRemoving an edit requires confirmation and may rebuild the saved stage. Run Build check before packaging."
            : "No matching edits. Change the category/status/search filters, or select an edit to inspect its full details.";
    }
    private static string FormatWhen(string when) => DateTimeOffset.TryParse(when, out var date) ? date.ToLocalTime().ToString("g") : when;
}
