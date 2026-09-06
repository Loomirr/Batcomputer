namespace Batcomputer;

/// <summary>Shared presentation for character identity, libraries and donor selection.</summary>
internal static class CharacterMenuStyle
{
    internal static TableLayoutPanel Grid(int columns, int rows) => new()
    { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
    internal static RoundedPanel Card() => new()
    { Dock = DockStyle.Fill, BackColor = Theme.CardBg, BorderColor = Theme.LineSoft,
        CornerRadius = Theme.Radius, Padding = new Padding(16), Margin = Padding.Empty };
    internal static Label Label(string text, Font? font = null, Color? color = null) => new()
    { Text = text, Font = font ?? Theme.Body, ForeColor = color ?? Theme.OnDarkMuted,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    internal static Control Input(TextBox box)
    {
        box.BackColor = Theme.PanelBg; box.ForeColor = Theme.OnDark; box.BorderStyle = BorderStyle.None;
        box.Dock = DockStyle.Fill;
        var wrap = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg, BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 2, 0, 6) };
        wrap.Controls.Add(box); return wrap;
    }
    internal static Control Header(string title, string subtitle)
    {
        var card = Card(); card.Margin = new Padding(0, 0, 0, 12);
        var grid = Grid(1, 2); grid.RowStyles.Add(new(SizeType.Absolute, 30)); grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.Controls.Add(Label(title, Theme.Title, Theme.Materials), 0, 0);
        grid.Controls.Add(Label(subtitle, Theme.Caption), 0, 1); card.Controls.Add(grid); return card;
    }
    internal static TextBox DetailText() => new()
    { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = Theme.CardBg, ForeColor = Theme.OnDarkMuted, Font = Theme.Body };
}
