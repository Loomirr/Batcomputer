namespace Batcomputer;

public sealed partial class MainForm
{
    private async Task ChooseCustomMeshImportAsync()
    {
        using var dialog = new AdaptiveDialogForm { Text = "Import custom model", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(510, 260), MinimumSize = new Size(430, 250), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, Font = Theme.Body, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 36)); layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Percent, 50)); layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.Controls.Add(new Label { Text = "What kind of model are you importing?", Dock = DockStyle.Fill, Font = Theme.BodyStrong }, 0, 0);
        bool skeletal = false;
        var rigid = new Button { Text = "Static · OBJ\nSocket, position, rotation and scale", Dock = DockStyle.Fill };
        var skinned = new Button { Text = "Skeletal · weighted FBX\nChoose the native rig used in Blender", Dock = DockStyle.Fill };
        Theme.StyleDarkButton(rigid); Theme.StyleDarkButton(skinned);
        rigid.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        skinned.Click += (_, _) => { skeletal = true; dialog.DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        Theme.StyleSmallDarkButton(cancel); dialog.CancelButton = cancel;
        layout.Controls.Add(rigid, 0, 1); layout.Controls.Add(skinned, 0, 2); layout.Controls.Add(cancel, 0, 3); dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (skeletal) await OpenSkinnedMeshWorkshopAsync(null);
        else await OpenCustomStaticMeshDialogAsync(null);
    }
}
