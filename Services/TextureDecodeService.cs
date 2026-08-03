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
            if (mip?.BulkData?.Data is not { Length: > 0 } data)
            {
                return null;
            }
            // PF_G8 is plain uncompressed 8-bit grayscale (the cape fabric height/noise maps use it) -
            // not a block format, so expand it here rather than through BCnEncoder.
            if (texture.Format == EPixelFormat.PF_G8)
            {
                if (data.Length < mip.SizeX * mip.SizeY)
                {
                    return null;
                }
                var gray = new ColorRgba32[mip.SizeX * mip.SizeY];
                for (var i = 0; i < gray.Length; i++)
                {
                    var v = data[i];
                    gray[i] = new ColorRgba32(v, v, v, 255);
                }
                return new Decoded(gray, mip.SizeX, mip.SizeY);
            }
            if (Codec(texture.Format) is not { } codec)
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
    /// Repacks the LEGO "MMR" map into three.js's ORM channel order.
    ///
    /// Confirmed by decoding T_TPAGE_Batman_89_DIST_MMR: the game packs
    ///   R = metalness (the belt/buckle metal, spikes to 255; ~0 on plastic)
    ///   G = unused    (measured max 0 across the whole texture)
    ///   B = roughness (the muscle/logo/plastic detail field, ~0.2 avg = glossy plastic)
    /// three.js MeshStandardMaterial reads roughness from the GREEN channel and metalness from BLUE, so
    /// binding MMR straight makes it sample the empty green and render the whole body mirror-glossy.
    /// Rewrite to R = AO(255), G = roughness (src B), B = metalness (src R) so both read correctly from
    /// one texture bound as roughnessMap + metalnessMap.
    /// </summary>
    public static bool TryExportMmrAsOrm(UTexture2D mmr, string destPath)
    {
        var d = TryDecode(mmr);
        if (d is null)
        {
            return false;
        }
        try
        {
            using var bmp = new Bitmap(d.Width, d.Height, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, d.Width, d.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // 32bppArgb is BGRA in memory: [0]=Blue [1]=Green [2]=Red [3]=Alpha.
                var row = new byte[d.Width * 4];
                for (var y = 0; y < d.Height; y++)
                {
                    for (var x = 0; x < d.Width; x++)
                    {
                        var c = d.Pixels[y * d.Width + x];
                        var o = x * 4;
                        // Roughness floor 0.146 from the recreated M_TPAGE graph's colour ramp
                        // (0 -> 0.146, 1 -> 1): even the shiniest plastic keeps a slight matte.
                        var rough = (byte)Math.Clamp(37 + c.b * (255 - 37) / 255, 0, 255);
                        row[o + 0] = c.r;   // Blue  <- metalness (three.js metalnessMap reads .b)
                        row[o + 1] = rough; // Green <- roughness (three.js roughnessMap reads .g)
                        row[o + 2] = 255;   // Red   <- AO none
                        row[o + 3] = 255;
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
            Console.WriteLine($"    MMR repack failed for {mmr.Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Exports the green channel of the game's RAO map as a conventional grayscale AO texture.
    /// The recovered EoM material wiring uses RAO.G for ambient-occlusion mixing; three.js samples
    /// an aoMap from its red channel, so copy the value into RGB rather than binding RAO directly.
    /// </summary>
    public static bool TryExportRaoGreenAsAo(UTexture2D rao, string destPath)
    {
        var d = TryDecode(rao);
        if (d is null)
        {
            return false;
        }
        try
        {
            using var bmp = new Bitmap(d.Width, d.Height, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, d.Width, d.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[d.Width * 4];
                for (var y = 0; y < d.Height; y++)
                {
                    for (var x = 0; x < d.Width; x++)
                    {
                        var ao = d.Pixels[y * d.Width + x].g;
                        var o = x * 4;
                        row[o + 0] = ao;
                        row[o + 1] = ao;
                        row[o + 2] = ao;
                        row[o + 3] = 255;
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
            Console.WriteLine($"    RAO AO export failed for {rao.Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>Repacks a source PNG using the same MMR-to-ORM mapping as cooked textures.</summary>
    public static bool TryConvertMmrPngToOrm(string sourcePath, string destPath)
    {
        try
        {
            using var source = new Bitmap(sourcePath);
            using var input = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(input))
            {
                g.DrawImageUnscaled(source, 0, 0);
            }
            using var output = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, input.Width, input.Height);
            var inputBits = input.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var outputBits = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var inputRow = new byte[input.Width * 4];
                var outputRow = new byte[output.Width * 4];
                for (var y = 0; y < input.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(inputBits.Scan0 + y * inputBits.Stride, inputRow, 0, inputRow.Length);
                    for (var x = 0; x < input.Width; x++)
                    {
                        var o = x * 4;
                        var metalness = inputRow[o + 2];
                        var roughness = inputRow[o];
                        outputRow[o] = metalness;
                        outputRow[o + 1] = (byte)Math.Clamp(37 + roughness * (255 - 37) / 255, 0, 255);
                        outputRow[o + 2] = 255;
                        outputRow[o + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(outputRow, 0, outputBits.Scan0 + y * outputBits.Stride, outputRow.Length);
                }
            }
            finally
            {
                input.UnlockBits(inputBits);
                output.UnlockBits(outputBits);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            output.Save(destPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    source MMR repack failed for {Path.GetFileName(sourcePath)}: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Bakes the shared LEGO mouth sheet into a drawable mouth.
    ///
    /// T_LEGOface_Mouth_BC is pure white RGB with the shape entirely in ALPHA: the opaque region is
    /// the TEETH, the transparent interior is the mouth cavity. Rendered with an alpha cutout you get
    /// a floating white ring ("the O"); what the game shows is a dark opening with white teeth in it.
    /// So resolve alpha into colour - white where the teeth are, near-black inside - and return a
    /// fully opaque texture.
    /// </summary>
    public static bool TryExportMouthSheet(UTexture2D sheet, string destPath)
    {
        var d = TryDecode(sheet);
        if (d is null)
        {
            return false;
        }
        try
        {
            using var bmp = new Bitmap(d.Width, d.Height, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, d.Width, d.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[d.Width * 4];
                for (var y = 0; y < d.Height; y++)
                {
                    for (var x = 0; x < d.Width; x++)
                    {
                        // Opaque region = the mouth OPENING (dark); the transparent interior is
                        // what the teeth show through as. Reads as a dark mouth with white teeth.
                        var opening = d.Pixels[y * d.Width + x].a >= 128;
                        var v = (byte)(opening ? 16 : 232);
                        var o = x * 4;
                        row[o + 0] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 255;
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
            Console.WriteLine($"    mouth sheet bake failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Bakes the cape's woven-fabric shading maps. The cooked M_Cape_EoM graph is stripped, but a
    /// community Blender recreation (near-exact to in-game) revealed the wiring, and all four source
    /// textures ship in the paks (Characters/Textures/Attachments/Cape/Batman_EOM/T_PongeeFabric_*):
    ///   roughness = ramp(0.2136->0.307, 0.2932->1.0) of height*(1-fuzz)   [weave valleys glossier]
    ///   normal    = overlay(NRM, Scratches_NRM)                            [weave + wear]
    ///   alpha     = ramp(0->0, 0.1864->1) of height                        [deep weave holes see-through]
    /// Writes an ORM png (G=roughness, B=0 metal), a normal png, and a separate grayscale alpha png -
    /// separate because three.js reads BOTH alphaMap and roughnessMap from the green channel, so the
    /// two cannot share one texture.
    /// </summary>
    public static bool TryBakeCapeFabric(
        UTexture2D height, UTexture2D? fuzz, UTexture2D nrm, UTexture2D? scratches,
        string ormDest, string nrmDest, string alphaDest)
    {
        var h = TryDecode(height);
        if (h is null)
        {
            return false;
        }
        var f = fuzz is null ? null : TryDecode(fuzz);
        var n1 = TryDecode(nrm);
        var n2 = scratches is null ? null : TryDecode(scratches);

        // The weave ramp: below lo the fabric floor, above hi fully rough. Values from the Blender
        // recreation's colour ramps.
        static float RampRough(float x) => x <= 0.2136f ? 0.3073f
            : x >= 0.2932f ? 1f
            : 0.3073f + (x - 0.2136f) / (0.2932f - 0.2136f) * (1f - 0.3073f);
        static float RampAlpha(float x) => x >= 0.1864f ? 1f : Math.Max(0f, x / 0.1864f);
        static ColorRgba32 SampleN(Decoded d, float u, float v)
        {
            var x = Math.Clamp((int)(u * (d.Width - 1) + 0.5f), 0, d.Width - 1);
            var y = Math.Clamp((int)(v * (d.Height - 1) + 0.5f), 0, d.Height - 1);
            return d.Pixels[y * d.Width + x];
        }
        static float Overlay(float a, float b) => a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);

        try
        {
            int w = h.Width, ht = h.Height;
            using var orm = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
            using var alp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
            var ob = orm.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var ab = alp.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var orow = new byte[w * 4];
                var arow = new byte[w * 4];
                for (var y = 0; y < ht; y++)
                {
                    var v = ht <= 1 ? 0f : y / (float)(ht - 1);
                    for (var x = 0; x < w; x++)
                    {
                        var u = w <= 1 ? 0f : x / (float)(w - 1);
                        var hv = h.Pixels[y * w + x].r / 255f;
                        var fv = f is null ? 0f : SampleN(f, u, v).r / 255f;
                        var rough = (byte)Math.Clamp(RampRough(hv * (1f - fv)) * 255f, 0, 255);
                        var a = (byte)Math.Clamp(RampAlpha(hv) * 255f, 0, 255);
                        var o = x * 4;
                        orow[o + 0] = 0;     // B: metalness 0 - cloth
                        orow[o + 1] = rough; // G: roughness
                        orow[o + 2] = 255;   // R: AO none
                        orow[o + 3] = 255;
                        arow[o + 0] = a; arow[o + 1] = a; arow[o + 2] = a; arow[o + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(orow, 0, ob.Scan0 + y * ob.Stride, orow.Length);
                    System.Runtime.InteropServices.Marshal.Copy(arow, 0, ab.Scan0 + y * ab.Stride, arow.Length);
                }
            }
            finally
            {
                orm.UnlockBits(ob);
                alp.UnlockBits(ab);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ormDest)!);
            orm.Save(ormDest, ImageFormat.Png);
            alp.Save(alphaDest, ImageFormat.Png);

            // Fabric normal: overlay-blend the weave and scratch normals in X/Y, rebuild Z (the
            // sources are BC5, so blue is empty on both).
            if (n1 is not null)
            {
                int nw = n1.Width, nh = n1.Height;
                using var bmp = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                var bits = bmp.LockBits(new Rectangle(0, 0, nw, nh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[nw * 4];
                    for (var y = 0; y < nh; y++)
                    {
                        var v = nh <= 1 ? 0f : y / (float)(nh - 1);
                        for (var x = 0; x < nw; x++)
                        {
                            var u = nw <= 1 ? 0f : x / (float)(nw - 1);
                            var c1 = n1.Pixels[y * nw + x];
                            float rx = c1.r / 255f, gy = c1.g / 255f;
                            if (n2 is not null)
                            {
                                var c2 = SampleN(n2, u, v);
                                rx = Overlay(rx, c2.r / 255f);
                                gy = Overlay(gy, c2.g / 255f);
                            }
                            var nx = rx * 2f - 1f;
                            var ny = gy * 2f - 1f;
                            var nz = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                            var o = x * 4;
                            row[o + 0] = (byte)Math.Clamp((nz + 1f) * 127.5f, 0, 255);
                            row[o + 1] = (byte)Math.Clamp(gy * 255f, 0, 255);
                            row[o + 2] = (byte)Math.Clamp(rx * 255f, 0, 255);
                            row[o + 3] = 255;
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bits);
                }
                bmp.Save(nrmDest, ImageFormat.Png);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    cape fabric bake failed: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Bakes a normal map with the game's micro-surface noise overlaid, Z rebuilt. The recreated
    /// shader graphs overlay T_Noise_Norm_SEB_N (tiled <paramref name="tile"/>x, 6.9 in M_TPAGE) over
    /// the part's base normal - the subtle injection-moulded plastic texture. Both live in the same
    /// UV space, so the overlay can be baked into one texture offline.
    /// </summary>
    public static bool TryBakeNoisedNormal(UTexture2D baseNrm, UTexture2D? noise, float tile, string destPath)
    {
        var b = TryDecode(baseNrm);
        if (b is null)
        {
            return false;
        }
        var n = noise is null ? null : TryDecode(noise);

        static float Overlay(float a, float x) => a < 0.5f ? 2f * a * x : 1f - 2f * (1f - a) * (1f - x);

        try
        {
            int w = b.Width, h = b.Height;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var c = b.Pixels[y * w + x];
                        float rx = c.r / 255f, gy = c.g / 255f;
                        if (n is not null)
                        {
                            // Tile the noise across the base map's UV space.
                            var nu = x / (float)w * tile % 1f;
                            var nv = y / (float)h * tile % 1f;
                            var np = n.Pixels[Math.Clamp((int)(nv * n.Height), 0, n.Height - 1) * n.Width
                                              + Math.Clamp((int)(nu * n.Width), 0, n.Width - 1)];
                            rx = Overlay(rx, np.r / 255f);
                            gy = Overlay(gy, np.g / 255f);
                        }
                        var nx = rx * 2f - 1f;
                        var ny = gy * 2f - 1f;
                        var nz = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                        var o = x * 4;
                        row[o + 0] = (byte)Math.Clamp((nz + 1f) * 127.5f, 0, 255);
                        row[o + 1] = (byte)Math.Clamp(gy * 255f, 0, 255);
                        row[o + 2] = (byte)Math.Clamp(rx * 255f, 0, 255);
                        row[o + 3] = 255;
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
            Console.WriteLine($"    noised normal bake failed for {baseNrm.Name}: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="texture"/> to <paramref name="destPath"/> as PNG. Returns false when the
    /// format isn't one we decode or the texture has no usable mip.
    /// </summary>
    /// Bakes a face print for MULTIPLY compositing over the head. The zone textures are white masks
    /// with the shape in ALPHA; the colour is the material's "<feature> Tint". Drawing them as a lit
    /// overlay double-lights the ink and washes it out to bright orange. Baking
    /// rgb = lerp(white, tint, alpha) with alpha forced opaque lets the viewer multiply the sheet
    /// over the head: untouched where the mask is empty, tinted where the print is, and shaded by
    /// the head underneath rather than by its own light.
    public static bool TryBakeFacePrint(UTexture2D texture, string destPath, Color tint)
    {
        try
        {
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data is not { Length: > 0 } data || Codec(texture.Format) is not { } codec)
            {
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
                var row = new byte[mip.SizeX * 4];
                for (var y = 0; y < mip.SizeY; y++)
                {
                    for (var x = 0; x < mip.SizeX; x++)
                    {
                        var px = pixels[(y * mip.SizeX) + x];
                        var a = px.a / 255f;
                        row[(x * 4) + 0] = (byte)(255 + ((tint.B - 255) * a));
                        row[(x * 4) + 1] = (byte)(255 + ((tint.G - 255) * a));
                        row[(x * 4) + 2] = (byte)(255 + ((tint.R - 255) * a));
                        row[(x * 4) + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(
                        row, 0, bits.Scan0 + (y * bits.Stride), row.Length);
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
        catch
        {
            return false;
        }
    }

    public static bool TryExportPng(UTexture2D texture, string destPath, bool reconstructNormalZ = false,
        bool keepAlpha = false)
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
                        // Force opaque by default: LEGO packs masks in alpha, it is not opacity.
                        // Faces are the exception - their print alpha IS opacity (keepAlpha).
                        row[o + 3] = keepAlpha ? c.a : (byte)255;
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
