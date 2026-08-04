using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Batcomputer;

/// <summary>Loads the bundled D-DIN faces without relying on a system install.</summary>
internal static class AppFonts
{
    private static readonly PrivateFontCollection Collection = new();
    private static readonly List<GCHandle> PinnedFontData = new();

    static AppFonts()
    {
        Add("Font/D-DIN.otf");
        Add("Font/D-DIN-Bold.otf");
        Add("Font/D-DINCondensed.otf");
        Add("Font/D-DINCondensed-Bold.otf");
    }

    private static void Add(string assetPath)
    {
        var bytes = EmbeddedAssets.ReadBytes(assetPath);
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Collection.AddMemoryFont(handle.AddrOfPinnedObject(), bytes.Length);
            PinnedFontData.Add(handle);
        }
        catch
        {
            handle.Free();
        }
    }

    private static FontFamily? Find(string name) => Collection.Families
        .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Font Create(FontFamily? family, float size, FontStyle style, string fallback)
    {
        if (family is not null)
        {
            var resolvedStyle = family.IsStyleAvailable(style) ? style : FontStyle.Regular;
            return new Font(family, size, resolvedStyle, GraphicsUnit.Point);
        }

        return new Font(fallback, size, style, GraphicsUnit.Point);
    }

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) =>
        Create(Find("D-DIN"), size, style, "Segoe UI");

    public static Font Condensed(float size, FontStyle style = FontStyle.Regular) =>
        Create(Find("D-DIN Condensed"), size, style, "Segoe UI");
}
