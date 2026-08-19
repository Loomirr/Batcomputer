namespace Batcomputer;

/// <summary>
/// Creates the standard Batcomputer dialog footer without relying on pre-scale pixel
/// coordinates. WinForms can resize a fixed dialog while applying per-monitor DPI; a
/// right-to-left flow keeps every action visible after that scaling pass.
/// </summary>
internal static class DialogActionFooter
{
    public const int StandardHeight = 54;

    public static Panel Create(params Button[] buttons)
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = StandardHeight,
            BackColor = Theme.SlateDark,
            Margin = Padding.Empty,
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.ClientSize.Width, 0);
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(18, 11, 18, 10),
            Margin = Padding.Empty,
            BackColor = Theme.SlateDark,
        };

        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.Height = 32;
            button.Margin = index == 0 ? Padding.Empty : new Padding(8, 0, 0, 0);
            actions.Controls.Add(button);
        }

        footer.Controls.Add(actions);
        return footer;
    }
}
