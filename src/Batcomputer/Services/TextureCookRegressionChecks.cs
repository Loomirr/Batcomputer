using System.Drawing.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Batcomputer;

/// <summary>Asset-free regression guards for PNG channel preservation and texture codecs.</summary>
internal static class TextureCookRegressionChecks
{
    public static void Run(List<string> failures, TextWriter output)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-texture-channel-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var channelSource = Path.Combine(root, "straight-rgba.png");
            var quadrantColors = new[]
            {
                Color.FromArgb(0, 200, 100, 50),
                Color.FromArgb(1, 10, 220, 30),
                Color.FromArgb(128, 40, 60, 240),
                Color.FromArgb(255, 250, 180, 90),
            };
            using (var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        bitmap.SetPixel(x, y, quadrantColors[(y / 2) * 2 + x / 2]);
                    }
                }
                bitmap.Save(channelSource, ImageFormat.Png);
            }

            var exact = TextureCookService.ReadStraightRgbaForRegression(channelSource, 4, 4);
            var half = TextureCookService.ReadStraightRgbaForRegression(channelSource, 2, 2);
            Check(
                exact.Length == 16 &&
                PixelEquals(exact[0], quadrantColors[0]) &&
                PixelEquals(exact[2], quadrantColors[1]) &&
                PixelEquals(exact[8], quadrantColors[2]) &&
                PixelEquals(exact[10], quadrantColors[3]) &&
                half.Length == 4 &&
                PixelEquals(half[0], quadrantColors[0]) &&
                PixelEquals(half[1], quadrantColors[1]) &&
                PixelEquals(half[2], quadrantColors[2]) &&
                PixelEquals(half[3], quadrantColors[3]),
                "texture source and filtered mips preserve straight RGB beneath zero/low alpha instead of premultiplying it to black",
                failures,
                output);

            var indexedSource = Path.Combine(root, "indexed-alpha.png");
            var indexedColors = new[]
            {
                Color.FromArgb(0, 77, 88, 99),
                Color.FromArgb(63, 210, 45, 160),
            };
            using (var bitmap = new Bitmap(2, 1, PixelFormat.Format8bppIndexed))
            {
                var palette = bitmap.Palette;
                palette.Entries[0] = indexedColors[0];
                palette.Entries[1] = indexedColors[1];
                bitmap.Palette = palette;
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(new byte[] { 0, 1 }, 0, data.Scan0, 2);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                bitmap.Save(indexedSource, ImageFormat.Png);
            }
            var indexed = TextureCookService.ReadStraightRgbaForRegression(indexedSource, 2, 1);
            Check(
                indexed.Length == 2 &&
                PixelEquals(indexed[0], indexedColors[0]) &&
                PixelEquals(indexed[1], indexedColors[1]),
                "indexed PNG imports retain palette RGB and alpha without a destructive ARGB clone",
                failures,
                output);

            var bgra = TextureCookService.EncodeSourceMipForRegression(
                channelSource,
                4,
                4,
                "PF_B8G8R8A8");
            var bgraExact = bgra.Length == 64;
            for (var i = 0; i < exact.Length && bgraExact; i++)
            {
                var offset = i * 4;
                bgraExact = bgra[offset] == exact[i].B &&
                            bgra[offset + 1] == exact[i].G &&
                            bgra[offset + 2] == exact[i].R &&
                            bgra[offset + 3] == exact[i].A;
            }
            Check(
                bgraExact,
                "native BGRA8 character/color cooks retain all four PNG channels byte-for-byte",
                failures,
                output);

            var rgbaBlockSource = Path.Combine(root, "rgba-block.png");
            WriteSolidPng(rgbaBlockSource, Color.FromArgb(91, 25, 150, 230));
            var bc3 = DecodeFirst(
                TextureCookService.EncodeSourceMipForRegression(rgbaBlockSource, 4, 4, "PF_DXT5"),
                CompressionFormat.Bc3);
            var bc7 = DecodeFirst(
                TextureCookService.EncodeSourceMipForRegression(rgbaBlockSource, 4, 4, "PF_BC7"),
                CompressionFormat.Bc7);
            Check(
                PixelClose(bc3, 25, 150, 230, 91, rgbTolerance: 10, alphaTolerance: 2) &&
                PixelClose(bc7, 25, 150, 230, 91, rgbTolerance: 10, alphaTolerance: 10),
                "DXT5/BC3 and BC7 color cooks retain RGB and alpha as independent compressed channels",
                failures,
                output);

            var hiddenRgbSource = Path.Combine(root, "hidden-rgb-block.png");
            WriteSolidPng(hiddenRgbSource, Color.FromArgb(0, 200, 100, 50));
            var bc1 = DecodeFirst(
                TextureCookService.EncodeSourceMipForRegression(hiddenRgbSource, 4, 4, "PF_DXT1"),
                CompressionFormat.Bc1);
            var normalSource = Path.Combine(root, "normal-block.png");
            WriteSolidPng(normalSource, Color.FromArgb(7, 128, 128, 255));
            var bc5 = DecodeFirst(
                TextureCookService.EncodeSourceMipForRegression(normalSource, 4, 4, "PF_BC5"),
                CompressionFormat.Bc5);
            Check(
                Close(bc1.r, 200, 10) &&
                Close(bc1.g, 100, 10) &&
                Close(bc1.b, 50, 10) &&
                bc1.a == 255 &&
                Close(bc5.r, 128, 3) &&
                Close(bc5.g, 128, 3),
                "DXT1 keeps packed RGB while remaining opaque, and BC5 keeps the two normal channels without inventing alpha support",
                failures,
                output);
        }
        catch (Exception ex)
        {
            Check(false, $"texture channel regression fixture completed ({ex.Message})", failures, output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    private static void WriteSolidPng(string path, Color color)
    {
        using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static ColorRgba32 DecodeFirst(byte[] encoded, CompressionFormat format) =>
        new BcDecoder().DecodeRaw(encoded, 4, 4, format)[0];

    private static bool PixelEquals((byte R, byte G, byte B, byte A) pixel, Color expected) =>
        pixel.R == expected.R &&
        pixel.G == expected.G &&
        pixel.B == expected.B &&
        pixel.A == expected.A;

    private static bool PixelClose(
        ColorRgba32 pixel,
        int red,
        int green,
        int blue,
        int alpha,
        int rgbTolerance,
        int alphaTolerance) =>
        Close(pixel.r, red, rgbTolerance) &&
        Close(pixel.g, green, rgbTolerance) &&
        Close(pixel.b, blue, rgbTolerance) &&
        Close(pixel.a, alpha, alphaTolerance);

    private static bool Close(byte actual, int expected, int tolerance) =>
        Math.Abs(actual - expected) <= tolerance;

    private static void Check(bool condition, string name, List<string> failures, TextWriter output)
    {
        if (condition)
        {
            output.WriteLine($"PASS: {name}");
            return;
        }

        failures.Add(name);
        output.WriteLine($"FAIL: {name}");
    }
}
