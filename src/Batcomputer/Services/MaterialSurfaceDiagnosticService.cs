using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Batcomputer;

/// <summary>
/// Small, non-blocking authoring checks for the game's MMR surface maps. The verified TPAGE/body
/// and native-plastic cowl conventions pack metalness into red, leave green unused, and pack
/// roughness into blue. Some specialized game-material families use green for their own mask, so
/// callers opt into the unused-green diagnostic only when their selected donor convention proves it.
/// These checks deliberately inspect the author's source PNG instead of trying to reinterpret the
/// cooked BC1 payload in the UI.
/// </summary>
internal static class MaterialSurfaceDiagnosticService
{
    internal sealed record MmrStats(
        int Samples,
        byte MedianMetalness,
        byte MedianRoughness,
        byte RoughnessP10,
        byte RoughnessP90,
        double FullyMetalPercent,
        double NonzeroGreenPercent,
        double VeryGlossyPercent);

    internal static MmrStats? TryAnalyzeMmrSource(string? sourcePng)
    {
        if (string.IsNullOrWhiteSpace(sourcePng) || !File.Exists(sourcePng))
        {
            return null;
        }

        try
        {
            using var source = new Bitmap(sourcePng);
            if (source.Width <= 0 || source.Height <= 0)
            {
                return null;
            }

            // Normalize every supported source image to one predictable byte layout. Sampling at
            // most roughly 65k pixels keeps the check instant even for a 4k authoring texture.
            using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            var targetSamples = 65_536d;
            var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(
                (bitmap.Width * (double)bitmap.Height) / targetSamples)));
            var metalHistogram = new int[256];
            var roughHistogram = new int[256];
            var samples = 0;
            var fullyMetal = 0;
            var nonzeroGreen = 0;
            var veryGlossy = 0;
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < bitmap.Height; y += step)
                {
                    var sourceRow = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                    var row = sourceRow * stride;
                    for (var x = 0; x < bitmap.Width; x += step)
                    {
                        var offset = row + x * 4;
                        var blueRoughness = bytes[offset + 0];
                        var greenUnused = bytes[offset + 1];
                        var redMetalness = bytes[offset + 2];
                        metalHistogram[redMetalness]++;
                        roughHistogram[blueRoughness]++;
                        samples++;
                        if (redMetalness >= 240) fullyMetal++;
                        if (greenUnused > 8) nonzeroGreen++;
                        if (blueRoughness <= 32) veryGlossy++;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            if (samples == 0)
            {
                return null;
            }

            return new MmrStats(
                samples,
                Percentile(metalHistogram, samples, 0.5),
                Percentile(roughHistogram, samples, 0.5),
                Percentile(roughHistogram, samples, 0.1),
                Percentile(roughHistogram, samples, 0.9),
                fullyMetal * 100d / samples,
                nonzeroGreen * 100d / samples,
                veryGlossy * 100d / samples);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Describe(MmrStats stats) =>
        $"MMR: R metal {stats.FullyMetalPercent:0.#}% · B roughness median {stats.MedianRoughness}/255 " +
        $"(10–90%: {stats.RoughnessP10}–{stats.RoughnessP90})";

    internal static IReadOnlyList<string> RiskMessages(MmrStats stats, bool expectUnusedGreen = false)
    {
        var warnings = new List<string>();
        if (expectUnusedGreen && stats.NonzeroGreenPercent >= 1d)
        {
            warnings.Add(
                $"{stats.NonzeroGreenPercent:0.#}% of sampled pixels use the green channel. This material template " +
                "expects its MMR green channel to stay unused; an ORM-style map may be packed incorrectly " +
                "(use R=metalness, G=0, B=roughness for this template). Specialized game donors may use G differently.");
        }
        if (stats.FullyMetalPercent >= 25d)
        {
            warnings.Add(
                $"{stats.FullyMetalPercent:0.#}% of sampled pixels are fully metallic. LEGO plastic should normally use R=0; " +
                "reserve R=255 for actual metal regions.");
        }
        if (stats.VeryGlossyPercent >= 25d)
        {
            warnings.Add(
                $"{stats.VeryGlossyPercent:0.#}% of sampled pixels have B roughness at or below 32/255. " +
                "For calmer black plastic, try B=64–89 before inherited shader offsets.");
        }
        return warnings;
    }

    private static byte Percentile(int[] histogram, int total, double percentile)
    {
        var target = Math.Clamp((int)Math.Ceiling(total * percentile), 1, total);
        var cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return (byte)value;
            }
        }
        return byte.MaxValue;
    }
}
