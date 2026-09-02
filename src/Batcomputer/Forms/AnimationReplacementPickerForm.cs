namespace Batcomputer;

/// <summary>Target-aware picker for base-game and imported animation assets.</summary>
public sealed class AnimationReplacementPickerForm : AdaptiveForm
{
    private readonly CharacterAnimationTargetSnapshot _target;
    private readonly IReadOnlyList<AnimationReplacementCandidate> _all;
    private readonly TextBox _search = new();
    private readonly ComboBox _source = new();
    private readonly ListView _list = new();
    private readonly Label _count = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly RichTextBox _details = new();
    private readonly Button _use = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 160 };

    public AnimationReplacementCandidate? SelectedCandidate { get; private set; }

    public AnimationReplacementPickerForm(
        CharacterAnimationTargetSnapshot target,
        AnimLibrary library)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _all = AnimationReplacementCatalogService.Build(target, library);

        Text = "Choose animation replacement";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1040, 690);
        MinimumSize = new Size(780, 520);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;

        BuildLayout();
        WireEvents();
        RefreshRows();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMain(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
    }

    private Control BuildHeader()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardHi,
            BorderColor = Theme.FrameLine,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(18, 9, 16, 8),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "REPLACE CHARACTER ANIMATION",
            Font = Theme.Eyebrow,
            ForeColor = Theme.Animations,
            TextAlign = ContentAlignment.BottomLeft,
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = $"Choose a compatible {AnimationReplacementCatalogService.AcceptedClass(_target)}. All tool-wide imports stay visible; incompatible ones explain what must be fixed.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
        }, 0, 1);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(12, 3, 0, 0),
        };
        filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _source.Dock = DockStyle.Fill;
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.Items.AddRange(["All sources", "Base game", "Imported"]);
        _source.SelectedIndex = 0;
        Theme.StyleDarkCombo(_source);
        filters.Controls.Add(_source, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search character or animation…";
        Theme.StyleDarkInput(_search);
        filters.Controls.Add(_search, 0, 1);
        layout.Controls.Add(filters, 1, 0);
        layout.SetRowSpan(filters, 2);
        return card;
    }

    private Control BuildMain()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        layout.Controls.Add(BuildListCard(), 0, 0);
        layout.Controls.Add(BuildDetailsCard(), 1, 0);
        return layout;
    }

    private Control BuildListCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 5, 0),
            Padding = new Padding(10),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        card.Controls.Add(layout);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.SlateDark;
        _list.ForeColor = Theme.OnDark;
        _list.Font = Theme.Body;
        _list.Columns.Add("Animation", 190);
        _list.Columns.Add("Source", 78);
        _list.Columns.Add("Status", 92);
        _list.Columns.Add("Character / rig", 150);
        layout.Controls.Add(_list, 0, 0);

        _count.Dock = DockStyle.Fill;
        _count.Font = Theme.Caption;
        _count.ForeColor = Theme.OnDarkMuted;
        _count.TextAlign = ContentAlignment.BottomLeft;
        layout.Controls.Add(_count, 0, 1);
        return card;
    }

    private Control BuildDetailsCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = new Padding(5, 0, 0, 0),
            Padding = new Padding(14, 10, 14, 10),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "SELECTION",
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _title.Dock = DockStyle.Fill;
        _title.Font = Theme.Title;
        _title.ForeColor = Theme.OnDark;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.AutoEllipsis = true;
        layout.Controls.Add(_title, 0, 1);
        _subtitle.Dock = DockStyle.Fill;
        _subtitle.Font = Theme.Caption;
        _subtitle.ForeColor = Theme.OnDarkMuted;
        _subtitle.AutoEllipsis = true;
        layout.Controls.Add(_subtitle, 0, 2);
        _details.Dock = DockStyle.Fill;
        _details.ReadOnly = true;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Theme.SlateDark;
        _details.ForeColor = Theme.OnDarkMuted;
        _details.Font = Theme.Mono;
        _details.DetectUrls = false;
        layout.Controls.Add(_details, 0, 3);
        ShowCandidate(null);
        return card;
    }

    private Control BuildFooter()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Theme.WindowBg,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "The target's original class is enforced. Rig compatibility is checked again before saving.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        }, 0, 0);
        var cancel = new Button
        {
            Text = "Cancel",
            Width = 96,
            Height = 34,
            Margin = new Padding(8, 0, 6, 0),
            DialogResult = DialogResult.Cancel,
        };
        Theme.StyleDarkButton(cancel);
        layout.Controls.Add(cancel, 1, 0);
        _use.Text = "Use animation";
        _use.Width = 132;
        _use.Height = 34;
        _use.Enabled = false;
        Theme.StyleGoldButton(_use);
        layout.Controls.Add(_use, 2, 0);
        CancelButton = cancel;
        AcceptButton = _use;
        return layout;
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshRows();
        };
        _source.SelectedIndexChanged += (_, _) => RefreshRows();
        _list.SelectedIndexChanged += (_, _) =>
            ShowCandidate(_list.SelectedItems.Count == 1
                ? _list.SelectedItems[0].Tag as AnimationReplacementCandidate
                : null);
        _list.DoubleClick += (_, _) => AcceptSelection();
        _use.Click += (_, _) => AcceptSelection();
        FormClosed += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Dispose();
        };
    }

    private void RefreshRows()
    {
        var query = _search.Text.Trim();
        var source = _source.SelectedItem?.ToString() ?? "All sources";
        var rows = _all.Where(candidate =>
                (source == "All sources" ||
                 source == "Base game" && !candidate.Source.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                 source == "Imported" && candidate.Source.Equals("Imported", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(query) ||
                 candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 candidate.PackagePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var candidate in rows)
            {
                var item = new ListViewItem(candidate.Name) { Tag = candidate };
                item.SubItems.Add(candidate.Source);
                item.SubItems.Add(candidate.CanSelect ? "Ready" : "Unavailable");
                item.SubItems.Add(candidate.Detail);
                if (candidate.PackagePath.Equals(_target.EffectivePackage, StringComparison.OrdinalIgnoreCase))
                {
                    item.ForeColor = Theme.Good;
                }
                else if (!candidate.CanSelect)
                {
                    item.ForeColor = Theme.Warn;
                }
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        var compatible = rows.Count(candidate => candidate.CanSelect);
        _count.Text = $"{rows.Count} animation{(rows.Count == 1 ? "" : "s")} shown • {compatible} compatible";
        ShowCandidate(null);
    }

    private void ShowCandidate(AnimationReplacementCandidate? candidate)
    {
        SelectedCandidate = candidate;
        _use.Enabled = candidate?.CanSelect == true;
        if (candidate is null)
        {
            _title.Text = "Choose an animation";
            _subtitle.Text = "Select a row to inspect its package and source.";
            _details.Text =
                $"CURRENT TARGET\n{_target.EffectivePackage}\n\n" +
                $"REQUIRED CLASS\n{AnimationReplacementCatalogService.AcceptedClass(_target)}";
            return;
        }
        _title.Text = candidate.Name;
        _subtitle.Text = $"{candidate.Source} • {candidate.AssetClass}";
        _details.Text =
            $"PACKAGE\n{candidate.PackagePath}\n\n" +
            $"SOURCE\n{candidate.Source}\n\n" +
            $"STATUS\n{(candidate.CanSelect ? "Ready for this slot" : candidate.IncompatibilityReason)}\n\n" +
            $"DETAIL\n{candidate.Detail}" +
            (candidate.LibraryEntry is null
                ? ""
                : $"\n\nRIG / SKELETON\n{candidate.LibraryEntry.Skeleton}\n\nROOT MOTION\n{(candidate.LibraryEntry.RootMotion ? "Enabled" : "Disabled")}");
    }

    private void AcceptSelection()
    {
        if (SelectedCandidate is null || !SelectedCandidate.CanSelect)
        {
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
