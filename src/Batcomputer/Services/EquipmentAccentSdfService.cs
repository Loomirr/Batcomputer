namespace Batcomputer;

/// <summary>Converts white/black icon artwork and green accent markers into independent distance fields.</summary>
internal static class EquipmentAccentSdfService
{
    internal const int Size = 64;
    private const int Samples = 4;
    private const double DistanceRange = 8;

    // Straight RGBA input/output. Alpha defines the entire icon, including marked regions.
    // Green dominance defines accent coverage; hidden RGB and white/black artwork never mark accents.
    internal static byte[] Generate(byte[] rgba, int width, int height)
    {
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || rgba.Length != checked(width * height * 4))
            throw new InvalidDataException("Equipment SDF source must be a PNG no larger than 4096×4096.");
        var alpha = new byte[width * height];
        var accent = new byte[alpha.Length];
        var hasTransparent = false;
        var marked = false;
        for (var i = 0; i < alpha.Length; i++)
        {
            var offset = i * 4;
            var a = rgba[offset + 3];
            alpha[i] = a;
            hasTransparent |= a == 0;
            if (a == 0) continue;
            var r = (int)rgba[offset]; var g = (int)rgba[offset + 1]; var b = (int)rgba[offset + 2];
            var excess = g - Math.Max(r, b);
            // Green-to-black/grey antialiasing can be opaque but darker than 96.
            // Classify by green dominance, not absolute brightness; keep the excess
            // as fractional coverage instead of turning dark edge pixels fully green.
            if (excess >= 32)
            {
                accent[i] = (byte)((a * excess + 127) / 255);
                marked |= accent[i] != 0;
            }
            else if (a >= 128 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 32)
            {
                throw new InvalidDataException("Equipment SDF expects a white silhouette with optional pure-green (#00FF00) accent areas on transparency. No painted outline needed. Use the source PNG, not a prepared red/blue SDF or coloured BCA image.");
            }
        }
        if (!hasTransparent) throw new InvalidDataException("Equipment SDF needs a transparent background, not an opaque black background. Leave transparent margins around the icon.");
        var shape = Rasterize(alpha, width, height);
        if (!shape.Any(a => a >= 0.5f)) throw new InvalidDataException("The equipment icon is empty or too small at 64×64.");
        for (var i = 0; i < Size; i++)
            if (shape[i] >= 0.5f || shape[(Size - 1) * Size + i] >= 0.5f || shape[i * Size] >= 0.5f || shape[i * Size + Size - 1] >= 0.5f)
                throw new InvalidDataException("Leave transparent margins around the equipment icon; its silhouette touches the 64×64 output edge.");
        var red = DistanceField(shape);
        var green = new byte[Size * Size];
        if (marked)
        {
            var coverage = Rasterize(accent, width, height);
            if (!coverage.Any(a => a >= 0.5f))
                throw new InvalidDataException("The green accent becomes too small or faint at 64×64. Paint a larger area using pure green (#00FF00), or remove the green for an icon without accents.");
            green = DistanceField(coverage);
        }
        var result = new byte[Size * Size * 4];
        for (var i = 0; i < red.Length; i++)
        {
            result[i * 4] = red[i]; result[i * 4 + 1] = green[i];
            result[i * 4 + 2] = 77; result[i * 4 + 3] = 255;
        }
        return result;
    }

    private static float[] Rasterize(byte[] source, int width, int height)
    {
        var result = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var sum = 0d;
            for (var sy = 0; sy < Samples; sy++)
            for (var sx = 0; sx < Samples; sx++)
            {
                var xx = (x + (sx + 0.5d) / Samples) * width / Size - 0.5d;
                var yy = (y + (sy + 0.5d) / Samples) * height / Size - 0.5d;
                var x0 = (int)Math.Floor(xx); var y0 = (int)Math.Floor(yy);
                var tx = xx - x0; var ty = yy - y0;
                double At(int ax, int ay) => ax < 0 || ax >= width || ay < 0 || ay >= height ? 0 : source[ay * width + ax];
                sum += (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) +
                       (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty;
            }
            result[y * Size + x] = (float)(sum / (Samples * Samples * 255d));
        }
        return result;
    }

    private static byte[] DistanceField(float[] coverage)
    {
        var inside = coverage.Select(a => a >= 0.5f).ToArray();
        var result = new byte[inside.Length];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var i = y * Size + x;
            // Search past the half-pixel boundary so distant pixels really saturate to 0/255.
            var nearestSquared = 81;
            for (var dy = -9; dy <= 9; dy++)
            for (var dx = -9; dx <= 9; dx++)
            {
                var xx = x + dx; var yy = y + dy;
                if (xx < 0 || xx >= Size || yy < 0 || yy >= Size || inside[yy * Size + xx] == inside[i]) continue;
                nearestSquared = Math.Min(nearestSquared, dx * dx + dy * dy);
            }
            var signed = (Math.Sqrt(nearestSquared) - 0.5d) * (inside[i] ? 1 : -1);
            signed += coverage[i] - (inside[i] ? 1d : 0d);
            result[i] = (byte)Math.Clamp(Math.Round(127.5d + Math.Clamp(signed / DistanceRange, -1d, 1d) * 127.5d,
                MidpointRounding.AwayFromZero), 0, 255);
        }
        return result;
    }
}
