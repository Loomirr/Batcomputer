namespace Batcomputer;

/// <summary>Target-aware picker for the curated donor-backed material recipe catalog.</summary>
internal sealed class MaterialTemplatePicker : Form
{
    private readonly MaterialTemplateCatalogService _catalog = new();
    private readonly MaterialTemplateCatalogService.Target? _target;
    private readonly TextBox _search = new();
    private readonly ComboBox _category = new();
    private readonly CheckBox _advanced = new();
    private readonly ListView _list = new();
    private readonly TextBox _details = new();
    private readonly Button _use = new();
    private readonly Label _targetLabel = new();

    public MaterialTemplateCatalogService.Recipe? SelectedRecipe { get; private set; }

    public MaterialTemplatePicker(MaterialTemplateCatalogService.Target? target)
    {
        _target = target;
        BuildLayout();
        PopulateCategories();
        RefreshRecipes();
    }

    private void BuildLayout()
    {
        Text = "Batcomputer - Material templates";
        ClientSize = new Size(920, 660);
        MinimumSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = true;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Shown += (_, _) => Theme.UseDarkTitleBar(this);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Theme.WindowBg,
        };
        header.Controls.Add(new Panel { Left = 18, Top = 17, Width = 3, Height = 42, BackColor = Theme.Materials });
        header.Controls.Add(new Label
        {
            Left = 33, Top = 13, Width = 500, Height = 18,
            Text = "DONOR-BACKED MATERIALS", Font = Theme.Eyebrow, ForeColor = Theme.Materials,
        });
        header.Controls.Add(new Label
        {
            Left = 33, Top = 32, Width = 520, Height = 25,
            Text = "Choose a proven material template", Font = Theme.Heading, ForeColor = Theme.OnDark,
        });
        _targetLabel.SetBounds(560, 20, 340, 38);
        _targetLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _targetLabel.TextAlign = ContentAlignment.MiddleRight;
        _targetLabel.Font = Theme.Caption;
        _targetLabel.ForeColor = Theme.OnDarkMuted;
        _targetLabel.Text = _target is null
            ? "Target: not selected"
            : $"Target: {_target.DisplayName}\n{_target.Kind} · {UnrealPathUtil.AssetName(_target.MeshPackagePath)}";
        header.Controls.Add(_targetLabel);

        var filters = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(18, 8, 18, 8),
            BackColor = Theme.SlateDark,
        };
        _search.SetBounds(18, 9, 340, 32);
        _search.PlaceholderText = "Search templates, categories, and notes…";
        Theme.StyleDarkInput(_search);
        _search.TextChanged += (_, _) => RefreshRecipes();
        filters.Controls.Add(_search);
        _category.SetBounds(370, 9, 220, 32);
        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        Theme.StyleDarkCombo(_category);
        _category.SelectedIndexChanged += (_, _) => RefreshRecipes();
        filters.Controls.Add(_category);
        _advanced.SetBounds(608, 12, 270, 26);
        _advanced.Text = "Show advanced and unavailable";
        _advanced.ForeColor = Theme.OnDark;
        _advanced.CheckedChanged += (_, _) => RefreshRecipes();
        filters.Controls.Add(_advanced);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(14),
            BackColor = Theme.LineSoft,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var listHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.WindowBg,
            Padding = new Padding(4, 0, 8, 0),
        };
        var detailHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.WindowBg,
            Padding = new Padding(8, 0, 4, 0),
        };

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.BackColor = Theme.CardBg;
        _list.ForeColor = Theme.OnDark;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Columns.Add("Template", 265);
        _list.Columns.Add("Category", 145);
        _list.Columns.Add("Status", 118);
        _list.SelectedIndexChanged += (_, _) => UpdateDetails();
        _list.DoubleClick += (_, _) => AcceptSelected();
        listHost.Controls.Add(_list);

        var detailCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
            Padding = new Padding(14),
        };
        _details.Dock = DockStyle.Fill;
        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Theme.CardBg;
        _details.ForeColor = Theme.OnDark;
        _details.Font = Theme.Body;
        detailCard.Controls.Add(_details);
        detailHost.Controls.Add(detailCard);
        body.Controls.Add(listHost, 0, 0);
        body.Controls.Add(detailHost, 1, 0);

        var cancel = new Button { Text = "Cancel", Width = 92, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        _use.Text = "Use template";
        _use.Width = 126;
        _use.Enabled = false;
        Theme.StyleGoldButton(_use);
        _use.Click += (_, _) => AcceptSelected();
        var footer = DialogActionFooter.Create(_use, cancel);

        Controls.Add(body);
        Controls.Add(filters);
        Controls.Add(header);
        Controls.Add(footer);

        AcceptButton = _use;
        CancelButton = cancel;
    }

    private void PopulateCategories()
    {
        _category.Items.Clear();
        _category.Items.Add("All categories");
        foreach (var category in _catalog.Recipes().Select(recipe => recipe.Category)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            _category.Items.Add(category);
        }
        _category.SelectedIndex = 0;
    }

    private void RefreshRecipes()
    {
        if (!IsHandleCreated && _category.SelectedIndex < 0)
        {
            return;
        }

        var selectedId = (_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null)
            is MaterialTemplateCatalogService.Recipe selected ? selected.Id : "";
        var query = _search.Text.Trim();
        var category = _category.SelectedItem?.ToString() ?? "All categories";
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var recipe in _catalog.Recipes())
        {
            if (!_advanced.Checked && (recipe.Advanced || !recipe.Enabled))
            {
                continue;
            }
            if (!category.Equals("All categories", StringComparison.OrdinalIgnoreCase) &&
                !recipe.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(query) &&
                !$"{recipe.DisplayName} {recipe.Category} {recipe.Summary} {recipe.Guidance}"
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compatibility = _catalog.Evaluate(recipe, _target);
            var item = new ListViewItem(recipe.DisplayName) { Tag = recipe };
            item.SubItems.Add(recipe.Category);
            item.SubItems.Add(compatibility.Status);
            item.ForeColor = compatibility.CanUse ? Theme.OnDark : Theme.OnDarkMuted;
            if (recipe.Advanced)
            {
                item.ToolTipText = "Advanced clone-only or context-limited recipe.";
            }
            _list.Items.Add(item);
            if (recipe.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
            }
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not MaterialTemplateCatalogService.Recipe recipe)
        {
            _details.Text = "Select a template to see its donor, outputs, compatibility, and limitations.";
            _use.Enabled = false;
            return;
        }

        var compatibility = _catalog.Evaluate(recipe, _target);
        var outputs = recipe.Outputs.Count == 0
            ? "No output is available."
            : string.Join(Environment.NewLine, recipe.Outputs.Select(output =>
                $"• {output.Role}: {output.DonorPackagePath}{(string.IsNullOrWhiteSpace(output.NameSuffix) ? "" : $"  → name{output.NameSuffix}")}"));
        var mesh = recipe.CompatibleMeshPackagePaths.Count == 0
            ? "Target mesh: normal target-kind checks"
            : "Required mesh: " + string.Join(", ", recipe.CompatibleMeshPackagePaths.Select(UnrealPathUtil.AssetName));
        _details.Text =
            $"{recipe.DisplayName}\r\n" +
            $"{recipe.Category}{(recipe.Advanced ? " · ADVANCED" : "")}\r\n\r\n" +
            $"{recipe.Summary}\r\n\r\n" +
            $"{recipe.Guidance}\r\n\r\n" +
            $"{mesh}\r\n\r\n" +
            $"Outputs\r\n{outputs}\r\n\r\n" +
            $"{compatibility.Status}\r\n{compatibility.Detail}";
        _use.Enabled = compatibility.CanUse;
    }

    private void AcceptSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not MaterialTemplateCatalogService.Recipe recipe)
        {
            return;
        }
        var compatibility = _catalog.Evaluate(recipe, _target);
        if (!compatibility.CanUse)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        SelectedRecipe = recipe;
        DialogResult = DialogResult.OK;
        Close();
    }
}
