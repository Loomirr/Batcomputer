using System.Drawing.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace Batcomputer;

/// <summary>
/// Decodes a cooked UTexture2D to a PNG using BCnEncoder.Net - the managed codec this project already
/// ships. CUE4Parse's own texture path P/Invokes a native Detex.dll that isn't in the NuGet package;
/// decoding here keeps the tool portable and self-contained, with no native binary to bundle.
/// </summary>
internal static class TextureDecodeService
{
    /// <summary>Maps UE's pixel format to the block codec. Null = not a format we decode.</summary>
    private static CompressionFormat? Codec(EPixelFormat format) => format switch
    {
        EPixelFormat.PF_DXT1 => CompressionFormat.Bc1,
        EPixelFormat.PF_DXT3 => CompressionFormat.Bc2,
        EPixelFormat.PF_DXT5 => CompressionFormat.Bc3,
        EPixelFormat.PF_BC4 => CompressionFormat.Bc4,
        EPixelFormat.PF_BC5 => CompressionFormat.Bc5,
        EPixelFormat.PF_BC7 => CompressionFormat.Bc7,
        EPixelFormat.PF_B8G8R8A8 => CompressionFormat.Bgra,
        EPixelFormat.PF_R8G8B8A8 => CompressionFormat.Rgba,
        _ => null,
    };

    /// <summary>Decoded pixels plus dimensions.</summary>
    internal sealed record Decoded(ColorRgba32[] Pixels, int Width, int Height);

    /// <summary>Decodes a texture to raw RGBA, or null if the format isn't supported.</summary>
    internal static Decoded? TryDecode(UTexture2D texture)
    {
        try
        {
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data is not { Length: > 0 } data || Codec(texture.Format) is not { } codec)
            {
                return null;
            }
            var pixels = new BcDecoder().DecodeRaw(data, mip.SizeX, mip.SizeY, codec);
            return pixels.Length < mip.SizeX * mip.SizeY ? null : new Decoded(pixels, mip.SizeX, mip.SizeY);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Composites the printed decal sheet over a flat plastic colour and writes the result.
    ///
    /// LEGO base-colour textures are NOT full albedo: they are mostly transparent sheets carrying only
    /// the printed detail (logo, belt, muscle sculpt). The plastic colour underneath comes from the
    /// material's colour slots. Applying the sheet alone renders every transparent texel black, which
    /// is what buried the figure.
    /// </summary>
    public static bool TryExportComposited(UTexture2D? decal, Color baseColour, string destPath)
    {
        var d = decal is null ? null : TryDecode(decal);
        var w = d?.Width ?? 16;
        var h = d?.Height ?? 16;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        byte r = baseColour.R, g = baseColour.G, b = baseColour.B;
                        if (d is not null)
                        {
                            var c = d.Pixels[y * d.Width + x];
                            // Standard source-over: decal alpha decides how much print shows.
                            var a = c.a / 255f;
                            r = (byte)(c.r * a + r * (1 - a));
                            g = (byte)(c.g * a + g * (1 - a));
                            b = (byte)(c.b * a + b * (1 - a));
                        }
                        var o = x * 4;
                        row[o + 0] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            bmp.Save(destPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    composite failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Bakes the LEGO shader's UV indirection into a plain texture in the mesh's own UV space.
    ///
    /// The minifig shader does not sample the character's decal atlas directly. It first samples
    /// T_LEGOFIG_CTUV - a lookup texture whose R/G channels ENCODE atlas coordinates - and uses that
    /// result as the UV to read the decal sheet. That indirection happens per pixel in the shader, so
    /// no vertex UV channel maps to the decals and no amount of channel-switching can reproduce it.
    ///
    /// Baking it offline gives an ordinary albedo we can hand to any renderer:
    ///     out[u,v] = decals[ ctuv[u,v].rg ] composited over the plastic colour.
    /// </summary>
    public static bool TryBakeCtuv(UTexture2D ctuv, UTexture2D? decals, Color plastic, string destPath)
    {
        var lut = TryDecode(ctuv);
        if (lut is null)
        {
            return false;
        }
        var sheet = decals is null ? null : TryDecode(decals);

        try
        {
            int w = lut.Width, h = lut.Height;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var l = lut.Pixels[y * w + x];
                        byte r = plastic.R, g = plastic.G, b = plastic.B;

                        // R/G are the atlas coordinates. Near-black means "no mapping here".
                        if (sheet is not null && (l.r > 2 || l.g > 2))
                        {
                            var su = Math.Clamp((int)(l.r / 255f * (sheet.Width - 1)), 0, sheet.Width - 1);
                            var sv = Math.Clamp((int)(l.g / 255f * (sheet.Height - 1)), 0, sheet.Height - 1);
                            var c = sheet.Pixels[sv * sheet.Width + su];
                            var a = c.a / 255f;
                            r = (byte)(c.r * a + r * (1 - a));
                            g = (byte)(c.g * a + g * (1 - a));
                            b = (byte)(c.b * a + b * (1 - a));
                        }

                        var o = x * 4;
                        row[o + 0] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            bmp.Save(destPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    CTUV bake failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="texture"/> to <paramref name="destPath"/> as PNG. Returns false when the
    /// format isn't one we decode or the texture has no usable mip.
    /// </summary>
    public static bool TryExportPng(UTexture2D texture, string destPath, bool reconstructNormalZ = false)
    {
        try
        {
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data is not { Length: > 0 } data)
            {
                return false;
            }
            if (Codec(texture.Format) is not { } codec)
            {
                Console.WriteLine($"    unsupported texture format {texture.Format} for {texture.Name}");
                return false;
            }

            var pixels = new BcDecoder().DecodeRaw(data, mip.SizeX, mip.SizeY, codec);
            if (pixels.Length < mip.SizeX * mip.SizeY)
            {
                return false;
            }

            using var bmp = new Bitmap(mip.SizeX, mip.SizeY, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, mip.SizeX, mip.SizeY);
            var bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // BCnEncoder gives RGBA; GDI+ 32bppArgb wants BGRA in memory.
                var row = new byte[mip.SizeX * 4];
                for (var y = 0; y < mip.SizeY; y++)
                {
                    for (var x = 0; x < mip.SizeX; x++)
                    {
                        var c = pixels[y * mip.SizeX + x];
                        var o = x * 4;
                        var bb = c.b;
                        if (reconstructNormalZ)
                        {
                            // BC5 stores only X/Y. Without rebuilding Z every normal points sideways
                            // and the surface lights up flat/blown-out.
                            var nx = c.r / 127.5f - 1f;
                            var ny = c.g / 127.5f - 1f;
                            var nz = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                            bb = (byte)Math.Clamp((nz + 1f) * 127.5f, 0, 255);
                        }
                        row[o + 0] = bb;
                        row[o + 1] = c.g;
                        row[o + 2] = c.r;
                        // Force opaque: LEGO packs masks in alpha, it is not opacity.
                        row[o + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(
                        row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            bmp.Save(destPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    texture decode failed for {texture.Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }
}
