namespace Batcomputer;

/// <summary>Compact two-line catalogue rows, without loading thumbnails or scanning game assets.</summary>
internal sealed class CharacterBrowserList : ListBox
{
    public CharacterBrowserList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.FixedSingle;
        IntegralHeight = false;
        BackColor = Theme.PanelBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        ItemHeight = Math.Max(52, Font.Height + Theme.Caption.Height + 20);
    }

    internal static string DisplayName(string name) => System.Text.RegularExpressions.Regex.Replace(
        name.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var entry = Items[e.Index] as CharacterCatalogService.Entry;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var row = Rectangle.Inflate(e.Bounds, -3, -3);
        using var fill = new SolidBrush(selected ? Theme.CardHi : Theme.PanelBg);
        e.Graphics.FillRectangle(fill, row);
        using var border = new Pen(selected ? Theme.Gold : Color.FromArgb(83, 91, 105));
        e.Graphics.DrawRectangle(border, row.X, row.Y, Math.Max(0, row.Width - 1), Math.Max(0, row.Height - 1));
        if (selected) { using var accent = new SolidBrush(Theme.Gold); e.Graphics.FillRectangle(accent, row.Left, row.Top + 5, 3, row.Height - 10); }
        var name = DisplayName(entry?.Name ?? GetItemText(Items[e.Index]) ?? "");
        var origin = EntryLabel(entry);
        TextRenderer.DrawText(e.Graphics, name, Font, new Rectangle(row.X + 13, row.Y + 5, row.Width - 21, Font.Height + 3), selected ? Theme.Gold : ForeColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, origin, Theme.Caption, new Rectangle(row.X + 13, row.Bottom - Theme.Caption.Height - 6, row.Width - 21, Theme.Caption.Height), Theme.OnDarkMuted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    internal static string EntryLabel(CharacterCatalogService.Entry? entry) => entry?.Origin switch
    {
        CharacterCatalogService.Source.Playable => "GAMEPLAY",
        CharacterCatalogService.Source.Cutscene => "CUTSCENE",
        _ when entry?.IsCharacter == true => "CHARACTER · " + entry.CharacterId,
        _ => "SUIT · " + (entry?.ProjectId ?? ""),
    };
}
