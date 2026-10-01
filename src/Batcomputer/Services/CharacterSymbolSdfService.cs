using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Batcomputer;

/// <summary>Alpha silhouette fitted without stretching to the native roster-emblem canvas.</summary>
internal static class CharacterSymbolSdfService
{
    internal static byte[] Generate(string path)
    {
        using var stream = File.OpenRead(path);
        using var source = Image.FromStream(stream);
        return Generate(source);
    }
    internal static byte[] Generate(Image source)
    {
        const int width = 128, height = 64;
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle((width-w)/2, (height-h)/2, w, h), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        var inside = new bool[width * height];
        for (var y=0;y<height;y++) for (var x=0;x<width;x++) inside[y*width+x] = canvas.GetPixel(x,y).A >= 128;
        if (!inside.Any(v=>v) || inside.All(v=>v)) throw new InvalidDataException("Use a visible silhouette on transparency, not an empty or opaque image.");
        // Check the source too: fitting a solid square must not disguise an opaque background.
        using var original = new Bitmap(source);
        if (Enumerable.Range(0, original.Width).Any(x => original.GetPixel(x,0).A>=128 || original.GetPixel(x,original.Height-1).A>=128) ||
            Enumerable.Range(0, original.Height).Any(y => original.GetPixel(0,y).A>=128 || original.GetPixel(original.Width-1,y).A>=128))
            throw new InvalidDataException("Leave a transparent margin on every edge. About 10–15% is recommended.");
        var field = new byte[inside.Length];
        for (var y=0;y<height;y++) for (var x=0;x<width;x++)
        {
            // Distances are measured from pixel centres, then shifted half a pixel to
            // the silhouette edge. Cap *after* that shift: a cap of 8 beforehand
            // left every empty texel at 9 instead of native G8's saturated zero.
            const double range = 8;
            var index = y*width+x; var distanceSquared = (range + .5) * (range + .5);
            for (var dy=-9;dy<=9;dy++) for (var dx=-9;dx<=9;dx++)
            {
                var xx=x+dx; var yy=y+dy;
                if (xx<0 || xx>=width || yy<0 || yy>=height || inside[yy*width+xx]==inside[index]) continue;
                distanceSquared=Math.Min(distanceSquared,dx*dx+dy*dy);
            }
            var signed=(Math.Sqrt(distanceSquared)-.5)*(inside[index]?1:-1);
            field[index]=(byte)Math.Clamp(Math.Round(127.5+signed*127.5/range),0,255);
        }
        return field;
    }
    internal static Bitmap Preview(Image source, float border)
    {
        var sdf = Generate(source);
        var image = new Bitmap(128,64,PixelFormat.Format32bppArgb);
        // Shape/outline approximation, not a replacement for the native UI shader.
        // M_UI_SDFIcon defaults: Colour=(1,1,1,1), Border=(0,0,0,.8).
        var threshold = 128 - (int)Math.Round(Math.Clamp(border,0,1)*127);
        for (var y=0;y<64;y++) for (var x=0;x<128;x++)
        {
            var value=sdf[y*128+x];
            image.SetPixel(x,y,value>=128?Color.White:value>0 && value>=threshold?Color.FromArgb(204,0,0,0):Color.Transparent);
        }
        return image;
    }
}
