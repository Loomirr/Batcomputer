using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Private recipe editor; cancellation never changes the suit.</summary>
public sealed class EquipmentWorkshopForm : AdaptiveForm
{
    private sealed record Donor(string Package, GameDataEquipment? Catalog, bool HasPlayableOwner)
    {
        public string? InspectedStatus { get; set; }
        public override string ToString() => (Catalog?.Name ?? UnrealPathUtil.AssetName(Package).Replace("DA_ETA_", "")) +
            " · " + (InspectedStatus ?? (HasPlayableOwner ? "playable equipment" : "view only"));
    }
    private readonly ThemedDropDown _donors = new() { Dock = DockStyle.Fill }, _category = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new(), _search = new(), _replacement = new();
    private readonly ListBox _parts = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label _accessLabel = new(), _selectionTitle = new(), _help = new(), _status = new();
    private readonly CheckBox _technical = new() { Text = "Show technical paths", AutoSize = true };
    private readonly TextBox _paths = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Button _model = new(), _apply = new(), _reset = new(), _save = new();
    private readonly ToolTip _tips = new();
    private readonly TableLayoutPanel _detailLayout = new();
    private CustomEquipmentRecipe _working;
    private EquipmentAssetProfile? _profile;
    private EquipmentWorkshopPolicy.Access _access = new(false, "Choose equipment to inspect its support.", []);
    private bool _busy;
    private int _loadVersion;
    public CustomEquipmentRecipe? Result { get; private set; }
    public GameDataEquipment? SelectedEquipment => (_donors.SelectedItem as Donor)?.Catalog;

    public EquipmentWorkshopForm(CustomEquipmentRecipe? existing = null)
    {
        _working = existing?.Clone() ?? new();
        Text = "Batcomputer — Equipment workshop"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1260, 860); MinimumSize = new Size(1040, 780); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body; Theme.StyleTooltip(_tips);
        var root = Grid(1, 3); root.Padding = new Padding(16);
        root.RowStyles.Add(new(SizeType.Absolute, 220)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 68)); Controls.Add(root);
        var header = Card(); header.Margin = new Padding(0, 0, 0, 12); root.Controls.Add(header, 0, 0);
        var head = Grid(2, 5); head.ColumnStyles.Add(new(SizeType.Percent, 58)); head.ColumnStyles.Add(new(SizeType.Percent, 42));
        foreach (var height in new[] { 30, 26, 22, 38, 58 }) head.RowStyles.Add(new(SizeType.Absolute, height)); header.Controls.Add(head);
        AddAcross(head, TextLabel("Equipment workshop", Theme.Title, Theme.Equipment), 0);
        AddAcross(head, TextLabel("1  Choose equipment     →     2  Customize its parts     →     3  Save, then Build mod", Theme.Body, Theme.OnDarkMuted), 1);
        head.Controls.Add(TextLabel("BASE EQUIPMENT", Theme.Eyebrow, Theme.OnDarkMuted), 0, 2);
        head.Controls.Add(TextLabel("YOUR NAME IN BATCOMPUTER", Theme.Eyebrow, Theme.OnDarkMuted), 1, 2);
        head.Controls.Add(_donors, 0, 3); head.Controls.Add(Input(_name), 1, 3);
        _accessLabel.Padding = new Padding(0, 7, 0, 0); AddAcross(head, _accessLabel, 4);

        var body = Grid(2, 1); body.ColumnStyles.Add(new(SizeType.Percent, 55)); body.ColumnStyles.Add(new(SizeType.Percent, 45)); root.Controls.Add(body, 0, 1);
        var browser = Card(); browser.Margin = new Padding(0, 0, 10, 0); body.Controls.Add(browser, 0, 0);
        var left = Grid(1, 3); left.RowStyles.Add(new(SizeType.Absolute, 30)); left.RowStyles.Add(new(SizeType.Absolute, 44)); left.RowStyles.Add(new(SizeType.Percent, 100)); browser.Controls.Add(left);
        left.Controls.Add(TextLabel("PARTS TO CUSTOMIZE", Theme.Heading, Theme.OnDark), 0, 0);
        var filters = Grid(2, 1); filters.ColumnStyles.Add(new(SizeType.Percent, 49)); filters.ColumnStyles.Add(new(SizeType.Percent, 51)); left.Controls.Add(filters, 0, 1);
        foreach (var item in new[] { "Models & icons", "Held models", "Projectile models", "Icons", "Materials", "All references" }) _category.Items.Add(item);
        _category.SelectedIndex = 0; filters.Controls.Add(_category, 0, 0); _search.PlaceholderText = "Search parts…"; filters.Controls.Add(Input(_search), 1, 0);
        _parts.BackColor = Theme.CardBg; _parts.ForeColor = Theme.OnDark; _parts.ItemHeight = 88; left.Controls.Add(_parts, 0, 2); _parts.DrawItem += DrawPart;
        _parts.MouseMove += (_, e) => { var index = _parts.IndexFromPoint(e.Location); _tips.SetToolTip(_parts, index < 0 ? "" : ((EquipmentAssetPart)_parts.Items[index]).Package); };

        var detail = Card(); body.Controls.Add(detail, 1, 0);
        var detailScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; detail.Controls.Add(detailScroll);
        _detailLayout.Dock = DockStyle.Top; _detailLayout.ColumnCount = 1; _detailLayout.RowCount = 9;
        _detailLayout.RowStyles.Add(new(SizeType.Absolute, 48)); _detailLayout.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 28, 0, 26, 40, 48, 46, 38 }) _detailLayout.RowStyles.Add(new(SizeType.Absolute, height)); detailScroll.Controls.Add(_detailLayout);
        _selectionTitle.Font = Theme.Heading; _selectionTitle.ForeColor = Theme.Equipment; _selectionTitle.Dock = DockStyle.Fill;
        _help.Dock = DockStyle.Fill; _help.Padding = new Padding(0, 6, 0, 6); _help.ForeColor = Theme.OnDarkMuted;
        _detailLayout.Controls.Add(_selectionTitle, 0, 0); _detailLayout.Controls.Add(_help, 0, 1); _detailLayout.Controls.Add(_technical, 0, 2);
        _paths.Dock = DockStyle.Fill; _paths.Visible = false; _paths.BackColor = Theme.PanelBg; _paths.ForeColor = Theme.OnDarkMuted; _paths.BorderStyle = BorderStyle.None; _paths.Font = Theme.Mono;
        _detailLayout.Controls.Add(_paths, 0, 3); _detailLayout.Controls.Add(TextLabel("EXISTING GAME ASSET (optional)", Theme.Eyebrow, Theme.OnDarkMuted), 0, 4);
        _replacement.PlaceholderText = "/Game/… — a cooked asset, not a PNG file"; _detailLayout.Controls.Add(Input(_replacement), 0, 5);
        ConfigureButton(_model, "Import / align a 3D model…", true); _detailLayout.Controls.Add(_model, 0, 6);
        ConfigureButton(_apply, "Use this game asset"); _detailLayout.Controls.Add(_apply, 0, 7); ConfigureButton(_reset, "Reset this part"); _detailLayout.Controls.Add(_reset, 0, 8);

        var footer = Grid(3, 1); footer.Padding = new Padding(0, 12, 0, 0); footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.Absolute, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 172)); root.Controls.Add(footer, 0, 2);
        _status.Dock = DockStyle.Fill; _status.ForeColor = Theme.OnDarkMuted; footer.Controls.Add(_status, 0, 0);
        var cancel = new Button { DialogResult = DialogResult.Cancel }; ConfigureButton(cancel, "Cancel"); ConfigureButton(_save, "Save equipment", true);
        footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(_save, 2, 0); CancelButton = cancel;

        detailScroll.Resize += (_, _) => FitDetails();
        _technical.CheckedChanged += (_, _) => { _paths.Visible = _technical.Checked; _detailLayout.RowStyles[3].Height = _technical.Checked ? 100 * DeviceDpi / 96f : 0; FitDetails(); };
        _category.SelectedIndexChanged += (_, _) => RefreshParts(); _search.TextChanged += (_, _) => RefreshParts();
        _parts.SelectedIndexChanged += (_, _) => ShowPart(); _parts.DoubleClick += (_, _) => EditModel();
        _parts.Resize += (_, _) => { _parts.ItemHeight = 88 * DeviceDpi / 96; _parts.Invalidate(); };
        _donors.SelectedIndexChanged += async (_, _) => await LoadDonorAsync();
        _model.Click += (_, _) => EditModel(); _apply.Click += (_, _) => ApplyReplacement(); _reset.Click += (_, _) => ResetPart(); _save.Click += (_, _) => Save();
        Shown += async (_, _) => await DiscoverAsync(); FormClosing += (_, _) => ++_loadVersion; FormClosed += (_, _) => _tips.Dispose(); UpdateAccess(); ShowPart();
    }

    private static TableLayoutPanel Grid(int columns, int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        // AutoSize rows use the rounded input wrapper's default preferred height (100),
        // clipping a 44px filter row. Single-row bars must fill their allotted height.
        if (rows == 1) grid.RowStyles.Add(new(SizeType.Percent, 100));
        return grid;
    }
    private void FitDetails()
    {
        if (_detailLayout.Parent is not { } parent) return;
        var minimum = (_apply.Enabled ? 450 : 280) + (_technical.Checked ? 100 : 0);
        _detailLayout.Height = Math.Max(parent.ClientSize.Height, minimum * DeviceDpi / 96);
    }
    private static RoundedPanel Card() => new() { Dock = DockStyle.Fill, BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.Radius, Padding = new Padding(14), Margin = Padding.Empty };
    private static Label TextLabel(string text, Font font, Color color) => new() { Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private static void AddAcross(TableLayoutPanel layout, Control control, int row) { control.Dock = DockStyle.Fill; layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2); }
    private static Control Input(TextBox box)
    {
        box.BorderStyle = BorderStyle.None; box.BackColor = Theme.PanelBg; box.ForeColor = Theme.OnDark; box.Dock = DockStyle.Fill;
        var wrap = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm,
            Padding = new Padding(10, 9, 10, 7), Margin = new Padding(5, 2, 0, 2) }; wrap.Controls.Add(box); return wrap;
    }
    private static void ConfigureButton(Button button, string text, bool primary = false)
    {
        button.Text = text; button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 3, 0, 3);
        if (primary) Theme.StyleGoldButton(button); else Theme.StyleDarkButton(button);
    }
    private EquipmentAssetPart? Part => _parts.SelectedItem as EquipmentAssetPart;
    private bool CanEdit => !_busy && _profile is not null && _access.CanCustomize;
    private async Task DiscoverAsync()
    {
        _busy = true; UpdateAccess(); _status.Text = "Finding equipment in your extraction…";
        try
        {
            var db = GameDataService.Instance.Db;
            var donors = await Task.Run(() => EquipmentAssetService.Discover(AppSettings.Current.EffectiveExtractedContentRoot()).Select(p => {
                var equipment = db.Equipment.FirstOrDefault(e => e.EtaPackage.Equals(p, StringComparison.OrdinalIgnoreCase));
                return new Donor(p, equipment, EquipmentWorkshopPolicy.PlayableOwners(equipment, db).Count > 0);
            }).OrderByDescending(d => d.HasPlayableOwner).ThenBy(d => d.ToString()).ToArray());
            if (IsDisposed) return; _donors.Items.AddRange(donors); _busy = false;
            _donors.SelectedItem = donors.FirstOrDefault(d => d.Package == _working.DonorEtaPackage) ?? donors.FirstOrDefault(d => d.Catalog?.Name == "Batarang") ?? donors.FirstOrDefault();
            if (donors.Length == 0) _status.Text = "No equipment found. Run Full refresh in Settings.";
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { if (!IsDisposed && _donors.SelectedItem is null) { _busy = false; UpdateAccess(); } }
    }
    private async Task LoadDonorAsync()
    {
        if (_busy || _donors.SelectedItem is not Donor donor) return;
        var previous = _working.DonorEtaPackage;
        if (previous.Length > 0 && previous != donor.Package && (_working.Parts.Count > 0 || _name.Text != _working.Name) &&
            !Dialog.Confirm(this, "Change base equipment?", "Changing the donor starts a fresh recipe. Discard the current workshop edits?", confirmText: "Change equipment"))
        { _busy = true; _donors.SelectedItem = _donors.Items.Cast<Donor>().FirstOrDefault(d => d.Package == previous); _busy = false; return; }
        var version = ++_loadVersion; _busy = true; _donors.Enabled = false; _profile = null; RefreshParts(); UpdateAccess(); _status.Text = "Checking models, projectiles and icons…";
        try
        {
            var profile = await Task.Run(() => EquipmentAssetService.Inspect(AppSettings.Current.EffectiveExtractedContentRoot(), AppSettings.Current.EffectiveUsmapPath()!, donor.Package));
            if (IsDisposed || version != _loadVersion) return;
            if (_working.DonorEtaPackage != donor.Package) _working = new() { DonorEtaPackage = donor.Package, Name = (donor.Catalog?.Name ?? UnrealPathUtil.AssetName(donor.Package)) + " custom" };
            _name.Text = _working.Name; _profile = profile;
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Cannot inspect this equipment: " + ex.Message; }
        finally { if (!IsDisposed && version == _loadVersion) { _busy = false; _donors.Enabled = true; UpdateAccess(); RefreshParts(); } }
    }
    private void UpdateAccess()
    {
        _access = EquipmentWorkshopPolicy.Evaluate(SelectedEquipment, _profile, GameDataService.Instance.Db);
        _accessLabel.ForeColor = _access.CanCustomize ? Theme.Good : Theme.Warn;
        _accessLabel.Text = (_busy ? "CHECKING SUPPORT…" : _access.CanCustomize ? "CUSTOMIZATION AVAILABLE" : "VIEW ONLY") + "  ·  " + _access.Reason;
        if (_profile is not null && _donors.SelectedItem is Donor donor) { donor.InspectedStatus = _access.CanCustomize ? "editable" : "view only"; _donors.Invalidate(); }
        _save.Enabled = CanEdit; _save.Visible = CanEdit; _name.ReadOnly = !CanEdit;
        if (_save.Parent is TableLayoutPanel footer) footer.ColumnStyles[2].Width = CanEdit ? 172 * DeviceDpi / 96f : 0;
        ShowPart();
    }
    internal static bool IsProjectile(EquipmentAssetPart part) => part.OwnerPackage.Contains("Projectile", StringComparison.OrdinalIgnoreCase) || UnrealPathUtil.AssetName(part.OwnerPackage).EndsWith("_Proj", StringComparison.OrdinalIgnoreCase);
    internal static string Group(EquipmentAssetPart part) => part.AssetClass switch { "StaticMesh" or "SkeletalMesh" => IsProjectile(part) ? "Projectile models" : "Held models", "Texture2D" => "Icons", "Material" or "MaterialInstanceConstant" => "Materials", _ => "View-only references" };
    private static string Words(string value) => Regex.Replace(value.Replace("_GEN_VARIABLE", "").Replace("_", " "), "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
    internal static string PartTitle(EquipmentAssetPart part)
    {
        if (part.AssetClass == "SkeletalMesh" && part.ExportName == "WeaponMesh") return "Equipment body (skeletal · view only)";
        if (part.AssetClass is "StaticMesh" or "SkeletalMesh") return IsProjectile(part) ? Words(UnrealPathUtil.AssetName(part.OwnerPackage).Replace("BP_", "")) + " model" : part.ExportName == "WeaponMesh" ? "Held equipment model" : Words(part.ExportName);
        if (part.AssetClass == "Texture2D") return part.PropertyPath.Contains("Reticle", StringComparison.OrdinalIgnoreCase) ? "Aiming reticle" : Words(UnrealPathUtil.AssetName(part.Package).Replace("T_UI_Icon", "").Replace("_SDF", "")) + " icon texture";
        return Words(UnrealPathUtil.AssetName(part.Package).Replace("MI_UI_Icon", "").Replace("MI_", "").Replace("M_UI_", ""));
    }
    private void RefreshParts()
    {
        var key = Part?.Key; _parts.BeginUpdate(); _parts.Items.Clear(); var category = _category.SelectedItem?.ToString() ?? "Models & icons"; var search = _search.Text.Trim();
        foreach (var part in (_profile?.Parts ?? []).GroupBy(p => p.AssetClass == "SkeletalMesh" ? p.OwnerPackage + "|" + p.ExportName + "|" + p.Package : p.Key).Select(g => g.First())
            .Where(p => category == "All references" || (category == "Models & icons" ? p.AssetClass is "StaticMesh" or "SkeletalMesh" or "Texture2D" : Group(p) == category))
            .Where(p => search.Length == 0 || (PartTitle(p) + p.Key + p.Package).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Group(p) switch { "Held models" => 0, "Projectile models" => 1, "Icons" => 2, "Materials" => 3, _ => 4 }).ThenBy(p => p.ExportName == "WeaponMesh" ? 0 : 1).ThenBy(PartTitle)) _parts.Items.Add(part);
        _parts.EndUpdate(); _parts.SelectedItem = _parts.Items.Cast<EquipmentAssetPart>().FirstOrDefault(p => p.Key == key) ?? _parts.Items.Cast<EquipmentAssetPart>().FirstOrDefault();
        if (_profile is not null) _status.Text = $"{_parts.Items.Count} parts shown  ·  {_working.Parts.Count} customized" + (CanEdit ? "\nSave equipment, then Build mod to apply your changes." : "\nInspection only — this equipment cannot be edited or built."); ShowPart();
    }
    private void DrawPart(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return; var part = (EquipmentAssetPart)_parts.Items[e.Index]; var selected = (e.State & DrawItemState.Selected) != 0; var scale = DeviceDpi / 96f; var rect = e.Bounds;
        using var fill = new SolidBrush(selected ? Theme.CardHi : Theme.CardBg); e.Graphics.FillRectangle(fill, rect);
        using var line = new Pen(selected ? Theme.Equipment : Theme.LineSoft, selected ? 3 : 1); e.Graphics.DrawLine(line, rect.Left, rect.Top + 6, rect.Left, rect.Bottom - 6);
        var edit = _working.Parts.FirstOrDefault(p => p.Key == part.Key); var state = !CanEdit || !part.CanEdit ? "View only" : edit?.Model is not null ? "Custom model" : edit is not null ? "Replaced" : "Original";
        var x = rect.Left + (int)(12 * scale); var width = Math.Max(1, rect.Width - (int)(25 * scale));
        TextRenderer.DrawText(e.Graphics, PartTitle(part), Theme.Heading, new Rectangle(x, rect.Top + (int)(8 * scale), width, (int)(26 * scale)), Theme.OnDark, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, Group(part) + "  ·  " + state, Theme.Caption, new Rectangle(x, rect.Top + (int)(36 * scale), width, (int)(22 * scale)), selected ? Theme.Equipment : Theme.OnDarkMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, Words(part.ExportName) + " · " + Words(part.PropertyPath), Theme.Caption, new Rectangle(x, rect.Top + (int)(60 * scale), width, (int)(22 * scale)), Theme.OnDarkMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    private void ShowPart()
    {
        var part = Part; _model.Enabled = CanEdit && part?.AssetClass == "StaticMesh"; _apply.Enabled = CanEdit && part?.CanEdit == true;
        _reset.Enabled = CanEdit && part is not null && _working.Parts.Any(p => p.Key == part.Key); _replacement.ReadOnly = !_apply.Enabled; _selectionTitle.Text = part is null ? "Choose a part" : PartTitle(part);
        var actionHeights = new[] { 26, 40, 48, 46, 38 };
        for (int row = 4; row <= 8; row++)
        {
            var show = _apply.Enabled && (row != 6 || _model.Enabled);
            _detailLayout.GetControlFromPosition(0, row)!.Visible = show;
            _detailLayout.RowStyles[row].Height = show ? actionHeights[row - 4] * DeviceDpi / 96f : 0;
        }
        FitDetails();
        _replacement.Text = part is null ? "" : _working.Parts.FirstOrDefault(p => p.Key == part.Key)?.ReplacementPackage ?? "";
        _paths.Text = part is null ? "" : $"Type: {part.AssetClass}\r\nOwner: {part.OwnerPackage}\r\nComponent: {part.ExportName}\r\nProperty: {part.PropertyPath}\r\nOriginal: {part.Package}";
        _help.Text = !CanEdit ? _access.Reason + "\n\nYou can browse every reference and show technical paths. No changes can be saved." : part is null ?
            "Choose a part from the left. Models and icons appear first; materials and other references are in the filter." : part.AssetClass == "StaticMesh" ?
            "Import an OBJ and line it up with this original in the 3D editor. Assign materials there, then save.\n\nEach held or projectile model is separate. This changes its look, not firing behavior or damage." : part.AssetClass == "Texture2D" ?
            "Replace this image with a cooked texture from the Textures category. Paste its /Game/… asset path below.\n\nGadget HUD icons usually need SDF textures, not ordinary suit-icon PNGs. Upgrade icons are separate." : part.CanEdit ?
            "Use a compatible cooked material path below. For a custom OBJ, set its material slots inside the 3D editor.\n\nOnly this reference changes; the game's original asset remains untouched." :
            "This reference is view-only. Skeletal models, effects and audio need their own support. Their native behavior is retained.";
    }
    private EquipmentPartEdit EditFor(EquipmentAssetPart part) => new() { OwnerPackage = part.OwnerPackage, ExportName = part.ExportName, PropertyPath = part.PropertyPath, OriginalPackage = part.Package };
    private void Store(EquipmentPartEdit edit) { _working.Parts.RemoveAll(p => p.Key == edit.Key); _working.Parts.Add(edit); RefreshParts(); }
    private void EditModel()
    {
        if (!CanEdit || Part is not { AssetClass: "StaticMesh" } part) return;
        using var editor = new WeaponModelEditorForm(part.Package, _working.Parts.FirstOrDefault(p => p.Key == part.Key)?.Model, equipment: true);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is null) return;
        _working.Parts.RemoveAll(p => p.OwnerPackage == part.OwnerPackage && p.ExportName == part.ExportName && p.PropertyPath.StartsWith("OverrideMaterials", StringComparison.Ordinal));
        var edit = EditFor(part); edit.Model = editor.Result.Clone(); Store(edit);
    }
    private void ApplyReplacement()
    {
        if (!CanEdit || Part is not { CanEdit: true } part) return;
        if (part.PropertyPath.StartsWith("OverrideMaterials", StringComparison.Ordinal) && _working.Parts.Any(p => p.OwnerPackage == part.OwnerPackage && p.ExportName == part.ExportName && p.Model is not null))
        { Dialog.Info(this, "Custom model materials", "Change this component's materials inside its 3D model editor so the baked mesh slots and component overrides stay in sync."); return; }
        var path = UnrealPathUtil.NormalizePackagePath(_replacement.Text);
        if (!HeldItemService.ValidPackage(path)) { Dialog.Info(this, "Invalid asset", "Enter a cooked /Game or DLC package path. Import PNG textures in the Textures category first."); return; }
        var edit = EditFor(part); edit.ReplacementPackage = path; Store(edit);
    }
    private void ResetPart() { if (CanEdit && Part is { } part) { _working.Parts.RemoveAll(p => p.Key == part.Key); RefreshParts(); } }
    private void Save()
    {
        if (!CanEdit || _profile is null) return; EquipmentWorkshopPolicy.RequireEditable(SelectedEquipment, _profile, GameDataService.Instance.Db);
        if (string.IsNullOrWhiteSpace(_name.Text)) { _status.Text = "Give your custom equipment a name."; return; }
        _working.Name = _name.Text.Trim(); Result = _working.Clone(); DialogResult = DialogResult.OK; Close();
    }
}
