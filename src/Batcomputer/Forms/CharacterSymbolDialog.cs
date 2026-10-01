namespace Batcomputer;

internal sealed class CharacterSymbolDialog : AdaptiveForm
{
    internal string SymbolPngBase64 { get; private set; }
    internal bool ResetRequested { get; private set; }
    internal string CookProfile { get; private set; }
    internal float? BorderThickness { get; private set; }
    private readonly ComboBox _profile = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Name = "symbol-cook-profile" };
    private readonly NumericUpDown _border = new() { Minimum = 0, Maximum = 1, DecimalPlaces = 2, Increment = .05m, Width = 90, Name = "symbol-outline" };
    private readonly Label _details = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ToolTip _tips = new();
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 38, 43) };
    internal CharacterSymbolDialog(string name, string encoded, string profile = "", float? border = null)
    {
        SymbolPngBase64 = encoded;
        CookProfile = profile.Length == 0 && encoded.Length == 0 ? CharacterSymbolService.NativeProfile : profile;
        BorderThickness = border;
        Text = "Batcomputer — " + name + " symbol";
        ClientSize = new Size(650, 590); MinimumSize = new Size(560, 530);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false;
        var root = CharacterMenuStyle.Grid(1, 5); root.Padding = new Padding(16);
        root.RowStyles.Add(new(SizeType.Absolute, 76)); root.RowStyles.Add(new(SizeType.Absolute, 36));
        root.RowStyles.Add(new(SizeType.Absolute, 38)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 52));
        _details.Text = "CHARACTER EMBLEM\n512×256 PNG · transparent background and cutouts · 10–15% clear margins\nAlpha defines the shape; source RGB is ignored. White fill / black outline in-game.";
        root.Controls.Add(_details, 0, 0);
        _profile.Items.AddRange(["Native character emblem · 128×64 grayscale SDF · needs in-game check", "Legacy symbol · 64×64 equipment SDF"]);
        _profile.SelectedIndex = CookProfile == CharacterSymbolService.NativeProfile ? 0 : 1;
        Theme.StyleDarkCombo(_profile);
        root.Controls.Add(_profile,0,1);
        var outline = new FlowLayoutPanel { Dock = DockStyle.Fill };
        outline.Controls.Add(new Label { Text = "Menu border · native 0.30", AutoSize = true, Padding = new Padding(0,6,6,0) });
        _border.Value = (decimal)Math.Clamp(border ?? .3f,0,1); outline.Controls.Add(_border);
        _tips.SetToolTip(_border, "Character-menu material override, not a pixel width. The native HUD's SetEmblem route changes only its Icon texture and can retain its own border styling. Preview is approximate.");
        root.Controls.Add(outline,0,2);
        root.Controls.Add(_preview, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var import = new Button { Text = "Import PNG…", Width = 145, Height = 38, Name = "import-symbol" };
        var reset = new Button { Text = "Use default symbol", Width = 175, Height = 38, Name = "reset-symbol" };
        Theme.StyleDarkButton(import); Theme.StyleDarkButton(reset); actions.Controls.AddRange([import, reset]); root.Controls.Add(actions, 0, 4);
        import.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "PNG image|*.png", Title = "Import character symbol", CheckFileExists = true };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try { var next = CharacterSymbolService.ReadPng(file.FileName, CookProfile); SymbolPngBase64 = next; ResetRequested = false; RefreshPreview(); }
            catch (Exception ex) { Dialog.Error(this, "Symbol was not imported", ex.Message); }
        };
        reset.Click += (_, _) => { SymbolPngBase64 = ""; BorderThickness = null; ResetRequested = true; RefreshPreview(); };
        _profile.SelectedIndexChanged += (_, _) =>
        {
            var next = _profile.SelectedIndex == 0 ? CharacterSymbolService.NativeProfile : CharacterSymbolService.LegacyProfile;
            try { if (SymbolPngBase64.Length > 0) CharacterSymbolService.Validate(SymbolPngBase64,next); CookProfile=next; RefreshPreview(); }
            catch (Exception ex) { _profile.SelectedIndex = CookProfile == CharacterSymbolService.NativeProfile ? 0 : 1; Dialog.Error(this,"Profile was not changed",ex.Message); }
        };
        _border.ValueChanged += (_, _) => { BorderThickness=(float)_border.Value; RefreshPreview(); };
        var save = new Button { Text = "Save symbol", Width = 140, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        Theme.StyleGoldButton(save); Theme.StyleDarkButton(cancel);
        Controls.Add(root); Controls.Add(DialogActionFooter.Create(save, cancel)); AcceptButton = save; CancelButton = cancel;
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        Image? image = null;
        if (!string.IsNullOrWhiteSpace(SymbolPngBase64))
        {
            using var stream = new MemoryStream(CharacterSymbolService.Validate(SymbolPngBase64,CookProfile));
            using var source = Image.FromStream(stream);
            image = CookProfile == CharacterSymbolService.NativeProfile ? CharacterSymbolSdfService.Preview(source,BorderThickness ?? .3f) : new Bitmap(source);
        }
        var old = _preview.Image; _preview.Image = image; old?.Dispose();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _preview.Image?.Dispose(); _preview.Image = null; _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
