namespace Batcomputer;

internal sealed class CharacterSymbolDialog : AdaptiveForm
{
    internal string SymbolPngBase64 { get; private set; }
    internal bool ResetRequested { get; private set; }
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 38, 43) };
    internal CharacterSymbolDialog(string name, string encoded)
    {
        SymbolPngBase64 = encoded;
        Text = "Batcomputer — " + name + " symbol";
        ClientSize = new Size(540, 500); MinimumSize = new Size(460, 420);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false;
        var root = CharacterMenuStyle.Grid(1, 3); root.Padding = new Padding(16);
        root.RowStyles.Add(new(SizeType.Absolute, 72)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 52));
        root.Controls.Add(CharacterMenuStyle.Label("CHARACTER SYMBOL\nSquare PNG · transparent background · leave clear margins\nUses the silhouette, not its colors. Shared by every suit."), 0, 0);
        root.Controls.Add(_preview, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var import = new Button { Text = "Import PNG…", Width = 145, Height = 38, Name = "import-symbol" };
        var reset = new Button { Text = "Use default symbol", Width = 175, Height = 38, Name = "reset-symbol" };
        Theme.StyleDarkButton(import); Theme.StyleDarkButton(reset); actions.Controls.AddRange([import, reset]); root.Controls.Add(actions, 0, 2);
        import.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "PNG image|*.png", Title = "Import character symbol", CheckFileExists = true };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try { var next = CharacterSymbolService.ReadPng(file.FileName); SymbolPngBase64 = next; ResetRequested = false; RefreshPreview(); }
            catch (Exception ex) { Dialog.Error(this, "Symbol was not imported", ex.Message); }
        };
        reset.Click += (_, _) => { SymbolPngBase64 = ""; ResetRequested = true; RefreshPreview(); };
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
            using var stream = new MemoryStream(CharacterSymbolService.Validate(SymbolPngBase64));
            using var source = Image.FromStream(stream); image = new Bitmap(source);
        }
        var old = _preview.Image; _preview.Image = image; old?.Dispose();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _preview.Image?.Dispose(); _preview.Image = null; }
        base.Dispose(disposing);
    }
}
