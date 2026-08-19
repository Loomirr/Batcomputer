using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using BcnPixelFormat = BCnEncoder.Encoder.PixelFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Batcomputer;

/// <summary>
/// Clones a known-good cooked Texture2D template and replaces its
/// mip payloads. This is deliberately narrow and template-driven; it is not a
/// general Unreal texture cooker yet.
/// </summary>
public sealed class TextureCookService
{
    public const int CurrentEncoderVersion = 7;

    private const CustomSerializationFlags NameMapOnlyPatchFlags =
        CustomSerializationFlags.SkipParsingExports |
        CustomSerializationFlags.SkipPreloadDependencyLoading;

    public sealed class Request
    {
        public string SourceImagePath { get; set; } = "";
        public string TemplateJsonPath { get; set; } = "";
        public string OutputContentRoot { get; set; } = "";
        public string OutputPackagePath { get; set; } = "";
        public bool NearestNeighborMips { get; set; } = true;
        public bool WriteInlineMips { get; set; } = false;
        public string Bc7InputLayout { get; set; } = "rgba";
        public string Bc7Quality { get; set; } = "balanced";
        public string ForcePixelFormat { get; set; } = "";
    }

    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string TemplatePackagePath { get; set; } = "";
        public string OutputPackagePath { get; set; } = "";
        public string OutputUasset { get; set; } = "";
        public string OutputUexp { get; set; } = "";
        public string OutputUbulk { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelFormat { get; set; } = "";
        public int MipCount { get; set; }
        public int ExternalMipCount { get; set; }
        public int InlineMipCount { get; set; }
        public int EncoderVersion { get; set; } = CurrentEncoderVersion;
        public List<string> Warnings { get; set; } = new();
        public List<string> Log { get; set; } = new();
    }

    private sealed record TextureTemplate(
        string Name,
        string Package,
        int SizeX,
        int SizeY,
        string PixelFormat,
        List<MipTemplate> Mips);

    private sealed record MipTemplate(
        int SizeX,
        int SizeY,
        int ElementCount,
        int SizeOnDisk,
        long OffsetInFile,
        string BulkDataFlags)
    {
        public bool IsExternal =>
            BulkDataFlags.Contains("PayloadInSeperateFile", StringComparison.OrdinalIgnoreCase) ||
            BulkDataFlags.Contains("PayloadInSeparateFile", StringComparison.OrdinalIgnoreCase);

        public bool IsInline =>
            BulkDataFlags.Contains("ForceInlinePayload", StringComparison.OrdinalIgnoreCase);
    }

    private readonly string _projectRoot;

    public TextureCookService(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public Result Cook(Request request)
    {
        var result = new Result();
        try
        {
            if (!File.Exists(request.SourceImagePath))
            {
                return Fail(result, $"Source image not found: {request.SourceImagePath}");
            }

            if (!File.Exists(request.TemplateJsonPath))
            {
                return Fail(result, $"Template JSON not found: {request.TemplateJsonPath}");
            }

            var templateBase = Path.Combine(
                Path.GetDirectoryName(request.TemplateJsonPath)!,
                Path.GetFileNameWithoutExtension(request.TemplateJsonPath));
            var templateUasset = templateBase + ".uasset";
            var templateUexp = templateBase + ".uexp";
            var templateUbulk = templateBase + ".ubulk";
            if (!File.Exists(templateUasset))
            {
                return Fail(result, $"Template .uasset not found beside JSON: {templateUasset}");
            }

            var template = ReadTemplate(request.TemplateJsonPath);
            if (!string.IsNullOrWhiteSpace(request.ForcePixelFormat))
            {
                var forcedPixelFormat = NormalizePixelFormat(request.ForcePixelFormat);
                if (!IsSupportedPixelFormat(forcedPixelFormat))
                {
                    return Fail(result, $"Unsupported forced Texture2D pixel format: {request.ForcePixelFormat}.");
                }

                result.Log.Add($"forced pixel format: {template.PixelFormat} -> {forcedPixelFormat}");
                template = template with { PixelFormat = forcedPixelFormat };
            }
            if (template.Mips.Any(m => m.IsExternal) && !File.Exists(templateUbulk))
            {
                return Fail(result, $"Template has external mip payloads but .ubulk was not found beside JSON: {templateUbulk}");
            }

            result.TemplatePackagePath = template.Package;
            result.Width = template.SizeX;
            result.Height = template.SizeY;
            result.PixelFormat = template.PixelFormat;
            result.MipCount = template.Mips.Count;
            result.ExternalMipCount = template.Mips.Count(m => m.IsExternal);
            result.InlineMipCount = template.Mips.Count(m => m.IsInline);

            if (!IsSupportedPixelFormat(template.PixelFormat))
            {
                return Fail(result, $"Unsupported Texture2D template pixel format: {template.PixelFormat}. Supported: PF_BC7, PF_B8G8R8A8, PF_BC5, PF_DXT5/BC3, PF_DXT1/BC1.");
            }

            var outputPackagePath = string.IsNullOrWhiteSpace(request.OutputPackagePath)
                ? template.Package
                : UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath);
            if (!outputPackagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(result, $"Output package path must start with /Game/. Got: {request.OutputPackagePath}");
            }
            result.OutputPackagePath = outputPackagePath;

            var outputBase = PackagePathToBasePath(request.OutputContentRoot, outputPackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
            result.OutputUasset = outputBase + ".uasset";
            result.OutputUexp = outputBase + ".uexp";
            result.OutputUbulk = outputBase + ".ubulk";

            var uassetBytes = File.ReadAllBytes(templateUasset);
            byte[]? uexpBytes = File.Exists(templateUexp)
                ? File.ReadAllBytes(templateUexp)
                : null;
            var encodedMips = EncodeAllMips(request.SourceImagePath, template.Mips, request.NearestNeighborMips, template.PixelFormat, request.Bc7InputLayout, request.Bc7Quality);
            if (template.PixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase))
            {
                result.Log.Add($"BC7 input layout: {NormalizeBc7InputLayout(request.Bc7InputLayout)} quality={NormalizeBc7Quality(request.Bc7Quality)}");
            }
            result.Log.Add(request.NearestNeighborMips
                ? "mip filter: nearest-neighbor"
                : "mip filter: high-quality alpha-safe");
            if (template.Mips.Any(m => m.IsExternal))
            {
                WriteExternalMips(template.Mips, encodedMips, templateUbulk, result.OutputUbulk, result);
            }
            else
            {
                result.OutputUbulk = "";
                if (File.Exists(outputBase + ".ubulk"))
                {
                    File.Delete(outputBase + ".ubulk");
                    result.Log.Add($"deleted stale external payload {outputBase}.ubulk");
                }
                result.Log.Add("template has no external .ubulk mips");
            }

            if (request.WriteInlineMips || template.Mips.All(m => m.IsInline))
            {
                if (uexpBytes is not null)
                {
                    WriteInlineMips(template.Mips, encodedMips, uexpBytes, ".uexp", result);
                }
                else
                {
                    WriteInlineMips(template.Mips, encodedMips, uassetBytes, ".uasset", result);
                }
            }
            else
            {
                var inlineMips = template.Mips.Where(m => m.IsInline).ToList();
                if (inlineMips.Count > 0)
                {
                    result.Warnings.Add("Inline .uasset mips were preserved from the template. This spike only replaces external .ubulk mips because inline payload boundaries need more research.");
                    foreach (var mip in inlineMips)
                    {
                        result.Log.Add($"preserved inline mip {mip.SizeX}x{mip.SizeY}");
                    }
                }
            }

            File.WriteAllBytes(result.OutputUasset, uassetBytes);
            if (uexpBytes is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result.OutputUexp)!);
                File.WriteAllBytes(result.OutputUexp, uexpBytes);
                result.Log.Add($"wrote split export payload {result.OutputUexp}");
            }
            if (template.Mips.Count > 0 &&
                template.Mips.All(m => m.IsInline) &&
                CanSameLengthIdentityPatch(template, outputPackagePath) &&
                !RequiresNameMapTextureFormatRewrite(template))
            {
                PatchSameLengthIdentity(template, outputPackagePath, uassetBytes, result);
                File.WriteAllBytes(result.OutputUasset, uassetBytes);
                result.Log.Add("same-length inline Texture2D identity patch complete; UAssetAPI rewrite skipped to preserve split-export offsets");
            }
            else
            {
                try
                {
                    RewriteTextureIdentityWithUAssetApi(result.OutputUasset, template, outputPackagePath, result);
                    // A name-map rewrite may grow the .uasset header. UAssetAPI
                    // needs the paired export loaded to update that header, but
                    // this deliberately raw Texture2D path keeps the cooked
                    // inline mip bytes authored above. Restore those exact bytes
                    // after the header write instead of accepting a serializer-
                    // generated split payload.
                    if (uexpBytes is not null)
                    {
                        File.WriteAllBytes(result.OutputUexp, uexpBytes);
                        result.Log.Add("restored cooked split export payload after UAssetAPI header rewrite");
                    }
                }
                catch (Exception ex) when (CanSameLengthIdentityPatch(template, outputPackagePath) && !RequiresNameMapTextureFormatRewrite(template))
                {
                    result.Warnings.Add($"UAssetAPI identity rewrite was not available for this template ({ex.Message.Split('\n')[0]}). Used same-length binary identity patch fallback.");
                    PatchSameLengthIdentity(template, outputPackagePath, uassetBytes, result);
                    File.WriteAllBytes(result.OutputUasset, uassetBytes);
                }
            }
            result.Status = "created";
            result.Log.Add($"wrote {result.OutputUasset}");
            if (!string.IsNullOrWhiteSpace(result.OutputUbulk))
            {
                result.Log.Add($"wrote {result.OutputUbulk}");
            }
            WriteReport(result, outputBase + ".texture-cook-report.json");
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    private static Result Fail(Result result, string error)
    {
        result.Status = "error";
        result.Error = error;
        return result;
    }

    private static TextureTemplate ReadTemplate(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().FirstOrDefault(e =>
                e.TryGetProperty("Type", out var type) &&
                type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
            : doc.RootElement;
        if (root.ValueKind == JsonValueKind.Undefined && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            root = doc.RootElement.EnumerateArray().First();
        }

        var name = root.GetProperty("Name").GetString() ?? "";
        var package = UnrealPathUtil.NormalizePackagePath(root.GetProperty("Package").GetString());
        var sizeX = root.GetProperty("SizeX").GetInt32();
        var sizeY = root.GetProperty("SizeY").GetInt32();
        var pixelFormat = root.GetProperty("PixelFormat").GetString() ?? "";
        var mips = new List<MipTemplate>();
        foreach (var mip in root.GetProperty("Mips").EnumerateArray())
        {
            var bulk = mip.GetProperty("BulkData");
            mips.Add(new MipTemplate(
                mip.GetProperty("SizeX").GetInt32(),
                mip.GetProperty("SizeY").GetInt32(),
                bulk.GetProperty("ElementCount").GetInt32(),
                bulk.GetProperty("SizeOnDisk").GetInt32(),
                ParseOffset(bulk.GetProperty("OffsetInFile")),
                bulk.GetProperty("BulkDataFlags").GetString() ?? ""));
        }

        return new TextureTemplate(name, package, sizeX, sizeY, pixelFormat, mips);
    }

    private static bool IsSupportedPixelFormat(string pixelFormat) =>
        pixelFormat.Equals("PF_DXT1", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_B8G8R8A8", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePixelFormat(string? pixelFormat)
    {
        var value = (pixelFormat ?? "").Trim().ToUpperInvariant();
        return value switch
        {
            "DXT1" or "BC1" or "PF_BC1" => "PF_DXT1",
            "DXT5" or "BC3" or "PF_BC3" => "PF_DXT5",
            "BC5" => "PF_BC5",
            "BC7" => "PF_BC7",
            "B8G8R8A8" => "PF_B8G8R8A8",
            _ => value
        };
    }

    private static bool RequiresNameMapTextureFormatRewrite(TextureTemplate template) =>
        template.PixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase);

    private static long ParseOffset(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        var text = element.GetString() ?? "0";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(text[2..], 16);
        }
        return long.Parse(text);
    }

    private static void PatchSameLengthIdentity(TextureTemplate template, string outputPackagePath, byte[] uassetBytes, Result result)
    {
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        if (outputName.Length != template.Name.Length || outputPackagePath.Length != template.Package.Length)
        {
            throw new InvalidOperationException(
                "This legacy Texture2D template can only be renamed when the output asset name and package path are the same length as the donor. " +
                $"Donor '{template.Package}' ({template.Package.Length}) / '{template.Name}' ({template.Name.Length}); " +
                $"output '{outputPackagePath}' ({outputPackagePath.Length}) / '{outputName}' ({outputName.Length}). " +
                "Preserve the donor path or choose a same-length compatibility path, then migrate to a standalone template when available.");
        }

        var packageHits = ReplaceAscii(uassetBytes, template.Package, outputPackagePath);
        var nameHits = ReplaceAscii(uassetBytes, template.Name, outputName);
        result.Log.Add($"renamed package path hits={packageHits}, asset name hits={nameHits}");
    }

    private static bool CanSameLengthIdentityPatch(TextureTemplate template, string outputPackagePath)
    {
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        return outputName.Length == template.Name.Length && outputPackagePath.Length == template.Package.Length;
    }

    private static void RewriteTextureIdentityWithUAssetApi(string uassetPath, TextureTemplate template, string outputPackagePath, Result result)
    {
        var outputName = UnrealPathUtil.AssetName(outputPackagePath);
        var replacements = new Dictionary<string, string>
        {
            [template.Package] = outputPackagePath,
            [template.Name] = outputName
        };
        if (template.PixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase))
        {
            // The real native suit-icon donor is PF_BC7, but BC7 and BC3/DXT5 have
            // the same block size. Until we add a true BC7 encoder, use the native
            // UI icon shell and patch its cooked pixel-format name to PF_DXT5.
            replacements["PF_BC7"] = "PF_DXT5";
            replacements["TC_BC7"] = "TC_Default";
        }

        replacements = replacements
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderByDescending(pair => pair.Key.Length)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var mappings = LoadDefaultMappings();
        // This asset has its Texture2D export in a companion .uexp. Loading only
        // the header lets UAssetAPI grow the name map without rebasing the split
        // export table, which makes FModel discard platform data as PF_Unknown.
        // Load the pair so the rewritten header remains consistent with the
        // separate export; the caller restores its intentionally raw cooked
        // mip payload after this narrow name-map operation.
        var asset = new UAsset(uassetPath, true, EngineVersion.VER_UE5_6, mappings, NameMapOnlyPatchFlags);
        var beforeFolder = asset.FolderName.ToString();
        if (!beforeFolder.Equals(outputPackagePath, StringComparison.Ordinal))
        {
            asset.FolderName = new FString(outputPackagePath);
            result.Log.Add($"uassetapi folderName: {beforeFolder} -> {outputPackagePath}");
        }
        else
        {
            result.Log.Add($"uassetapi folderName already: {outputPackagePath}");
        }

        var nameMap = asset.GetNameMapIndexList();
        var replacementsApplied = 0;
        for (var i = 0; i < nameMap.Count; i++)
        {
            var original = nameMap[i].ToString();
            var patched = original;
            foreach (var pair in replacements)
            {
                patched = patched.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            }

            if (patched == original)
            {
                continue;
            }

            asset.SetNameReference(i, new FString(patched));
            replacementsApplied++;
            result.Log.Add($"uassetapi nameMap[{i}]: {original} -> {patched}");
        }

        asset.Write(uassetPath);
        result.Log.Add($"uassetapi identity rewrite complete: nameMap replacements={replacementsApplied}");
    }

    private static Usmap? LoadDefaultMappings()
    {
        try
        {
            var configured = AppSettings.Current.EffectiveUsmapPath();
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
                ? MappingsCache.Load(configured)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int ReplaceAscii(byte[] data, string before, string after)
    {
        if (before.Length != after.Length)
        {
            throw new InvalidOperationException("ASCII replacement length mismatch.");
        }

        var find = Encoding.ASCII.GetBytes(before);
        var replacement = Encoding.ASCII.GetBytes(after);
        var hits = 0;
        for (var i = 0; i <= data.Length - find.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < find.Length; j++)
            {
                if (data[i + j] != find[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            Buffer.BlockCopy(replacement, 0, data, i, replacement.Length);
            hits++;
            i += find.Length - 1;
        }
        return hits;
    }

    private static Dictionary<MipTemplate, byte[]> EncodeAllMips(string sourceImagePath, List<MipTemplate> mips, bool nearest, string pixelFormat, string bc7InputLayout, string bc7Quality)
    {
        using var source = new Bitmap(sourceImagePath);
        var output = new Dictionary<MipTemplate, byte[]>();
        foreach (var mip in mips)
        {
            using var resized = ResizeBitmap(source, mip.SizeX, mip.SizeY, nearest);
            var pixels = ReadRgba(resized);
            if (!nearest)
            {
                BleedTransparentRgb(pixels, mip.SizeX, mip.SizeY);
            }
            var encoded = pixelFormat.ToUpperInvariant() switch
            {
                "PF_DXT1" => Bc1Encode(pixels, mip.SizeX, mip.SizeY),
                "PF_DXT5" => Bc3Encode(pixels, mip.SizeX, mip.SizeY),
                "PF_BC5" => Bc5Encode(pixels, mip.SizeX, mip.SizeY),
                "PF_BC7" => Bc7Encode(pixels, mip.SizeX, mip.SizeY, bc7InputLayout, bc7Quality),
                "PF_B8G8R8A8" => Bgra8Encode(pixels, mip.SizeX, mip.SizeY),
                _ => throw new InvalidOperationException($"Unsupported Texture2D pixel format: {pixelFormat}")
            };
            if (encoded.Length != mip.SizeOnDisk || encoded.Length != mip.ElementCount)
            {
                throw new InvalidOperationException(
                    $"Encoded mip {mip.SizeX}x{mip.SizeY} length {encoded.Length} did not match template size {mip.SizeOnDisk}.");
            }
            output[mip] = encoded;
        }
        return output;
    }

    private static byte[] Bc7Encode(Rgba[] pixels, int width, int height, string inputLayout, string quality)
    {
        var layout = NormalizeBc7InputLayout(inputLayout);
        var rgbaBytes = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var offset = i * 4;
            switch (layout)
            {
                case "bgra":
                    rgbaBytes[offset + 0] = pixels[i].B;
                    rgbaBytes[offset + 1] = pixels[i].G;
                    rgbaBytes[offset + 2] = pixels[i].R;
                    rgbaBytes[offset + 3] = pixels[i].A;
                    break;
                case "argb":
                    rgbaBytes[offset + 0] = pixels[i].A;
                    rgbaBytes[offset + 1] = pixels[i].R;
                    rgbaBytes[offset + 2] = pixels[i].G;
                    rgbaBytes[offset + 3] = pixels[i].B;
                    break;
                case "bgra-as-rgba":
                    rgbaBytes[offset + 0] = pixels[i].B;
                    rgbaBytes[offset + 1] = pixels[i].G;
                    rgbaBytes[offset + 2] = pixels[i].R;
                    rgbaBytes[offset + 3] = pixels[i].A;
                    break;
                default:
                    rgbaBytes[offset + 0] = pixels[i].R;
                    rgbaBytes[offset + 1] = pixels[i].G;
                    rgbaBytes[offset + 2] = pixels[i].B;
                    rgbaBytes[offset + 3] = pixels[i].A;
                    break;
            }
        }

        var encoder = new BcEncoder(CompressionFormat.Bc7);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = NormalizeBc7Quality(quality) switch
        {
            "fast" => CompressionQuality.Fast,
            "best" => CompressionQuality.BestQuality,
            _ => CompressionQuality.Balanced
        };
        var pixelFormat = layout switch
        {
            "bgra" => BcnPixelFormat.Bgra32,
            "argb" => BcnPixelFormat.Argb32,
            _ => BcnPixelFormat.Rgba32
        };
        return encoder.EncodeToRawBytes(rgbaBytes, width, height, pixelFormat)[0];
    }

    private static string NormalizeBc7InputLayout(string? inputLayout)
    {
        var value = (inputLayout ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "bgra" => "bgra",
            "argb" => "argb",
            "bgra-as-rgba" => "bgra-as-rgba",
            _ => "rgba"
        };
    }

    private static string NormalizeBc7Quality(string? quality)
    {
        var value = (quality ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "fast" => "fast",
            "best" or "bestquality" or "best-quality" => "best",
            _ => "balanced"
        };
    }

    /// <summary>
    /// UI icon mips are viewed very small in-game. If transparent pixels keep a
    /// hard black/white/empty RGB fringe, bilinear filtering can pull that fringe
    /// into visible edges even though FModel's full-res preview looks fine. Keep
    /// alpha untouched, but copy neighboring visible RGB into fully-transparent
    /// pixels so small mips behave like normal Unreal texture imports.
    /// </summary>
    private static void BleedTransparentRgb(Rgba[] pixels, int width, int height)
    {
        if (pixels.Length == 0 || pixels.All(p => p.A != 0))
        {
            return;
        }

        var current = pixels.ToArray();
        var next = new Rgba[pixels.Length];
        var anyChanged = false;
        for (var pass = 0; pass < 12; pass++)
        {
            var changed = false;
            Array.Copy(current, next, current.Length);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (current[index].A != 0)
                    {
                        continue;
                    }

                    var count = 0;
                    var r = 0;
                    var g = 0;
                    var b = 0;
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        var ny = y + oy;
                        if (ny < 0 || ny >= height)
                        {
                            continue;
                        }

                        for (var ox = -1; ox <= 1; ox++)
                        {
                            var nx = x + ox;
                            if ((ox == 0 && oy == 0) || nx < 0 || nx >= width)
                            {
                                continue;
                            }

                            var neighbor = current[ny * width + nx];
                            if (neighbor.A == 0)
                            {
                                continue;
                            }

                            r += neighbor.R;
                            g += neighbor.G;
                            b += neighbor.B;
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    next[index] = new Rgba((byte)(r / count), (byte)(g / count), (byte)(b / count), 0);
                    changed = true;
                    anyChanged = true;
                }
            }

            if (!changed)
            {
                break;
            }

            (current, next) = (next, current);
        }

        if (anyChanged)
        {
            Array.Copy(current, pixels, current.Length);
        }
    }

    private static Bitmap ResizeBitmap(Bitmap source, int width, int height, bool nearest)
    {
        if (source.Width == width && source.Height == height)
        {
            return new Bitmap(source);
        }

        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = nearest ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = nearest ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return bitmap;
    }

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private static Rgba[] ReadRgba(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var clone = bitmap.PixelFormat == DrawingPixelFormat.Format32bppArgb
            ? new Bitmap(bitmap)
            : bitmap.Clone(rect, DrawingPixelFormat.Format32bppArgb);
        var data = clone.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * clone.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var pixels = new Rgba[clone.Width * clone.Height];
            for (var y = 0; y < clone.Height; y++)
            {
                var row = stride >= 0 ? y * stride : (clone.Height - 1 - y) * -stride;
                for (var x = 0; x < clone.Width; x++)
                {
                    var i = row + x * 4;
                    pixels[y * clone.Width + x] = new Rgba(bytes[i + 2], bytes[i + 1], bytes[i], bytes[i + 3]);
                }
            }
            return pixels;
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }

    private static byte[] Bc1Encode(Rgba[] pixels, int width, int height)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var output = new byte[blocksX * blocksY * 8];
        var pos = 0;
        Span<Rgba> block = stackalloc Rgba[16];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var py = 0; py < 4; py++)
                {
                    var sy = Math.Min(by * 4 + py, height - 1);
                    for (var px = 0; px < 4; px++)
                    {
                        var sx = Math.Min(bx * 4 + px, width - 1);
                        block[py * 4 + px] = pixels[sy * width + sx];
                    }
                }

                WriteColorBlock(block, output.AsSpan(pos, 8));
                pos += 8;
            }
        }
        return output;
    }

    private static byte[] Bc3Encode(Rgba[] pixels, int width, int height)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var output = new byte[blocksX * blocksY * 16];
        var pos = 0;
        Span<Rgba> block = stackalloc Rgba[16];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var py = 0; py < 4; py++)
                {
                    var sy = Math.Min(by * 4 + py, height - 1);
                    for (var px = 0; px < 4; px++)
                    {
                        var sx = Math.Min(bx * 4 + px, width - 1);
                        block[py * 4 + px] = pixels[sy * width + sx];
                    }
                }

                WriteChannelBlock(block, output.AsSpan(pos, 8), Channel.Alpha);
                WriteColorBlock(block, output.AsSpan(pos + 8, 8));
                pos += 16;
            }
        }
        return output;
    }

    private enum Channel
    {
        Red,
        Green,
        Alpha
    }

    private static byte[] Bc5Encode(Rgba[] pixels, int width, int height)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var output = new byte[blocksX * blocksY * 16];
        var pos = 0;
        Span<Rgba> block = stackalloc Rgba[16];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var py = 0; py < 4; py++)
                {
                    var sy = Math.Min(by * 4 + py, height - 1);
                    for (var px = 0; px < 4; px++)
                    {
                        var sx = Math.Min(bx * 4 + px, width - 1);
                        block[py * 4 + px] = pixels[sy * width + sx];
                    }
                }

                WriteChannelBlock(block, output.AsSpan(pos, 8), Channel.Red);
                WriteChannelBlock(block, output.AsSpan(pos + 8, 8), Channel.Green);
                pos += 16;
            }
        }
        return output;
    }

    private static byte[] Bgra8Encode(Rgba[] pixels, int width, int height)
    {
        var output = new byte[checked(width * height * 4)];
        var pos = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            output[pos++] = pixels[i].B;
            output[pos++] = pixels[i].G;
            output[pos++] = pixels[i].R;
            output[pos++] = pixels[i].A;
        }
        return output;
    }

    private static void WriteChannelBlock(ReadOnlySpan<Rgba> block, Span<byte> output, Channel channel)
    {
        byte min = 255;
        byte max = 0;
        foreach (var px in block)
        {
            var value = ChannelValue(px, channel);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        // BC4/BC5/DXT5 channel blocks have two endpoint modes. Large runs of
        // equal endpoints (0,0 or 255,255) proved to make CUE4Parse/FModel drop
        // the cooked Texture2D platform data for generated UI icons, even though
        // tiny isolated replacements parse. Nudge constant blocks into the
        // normal endpoint0 > endpoint1 mode while keeping the reconstructed value
        // visually indistinguishable for icon-sized textures.
        if (max == min)
        {
            if (max == 0)
            {
                max = 1;
                min = 0;
            }
            else
            {
                min = (byte)(max - 1);
            }
        }

        output[0] = max;
        output[1] = min;
        Span<int> palette = stackalloc int[8];
        palette[0] = max;
        palette[1] = min;
        for (var i = 1; i <= 6; i++)
        {
            palette[i + 1] = ((7 - i) * max + i * min + 3) / 7;
        }

        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestErr = int.MaxValue;
            var value = ChannelValue(block[i], channel);
            for (var p = 0; p < 8; p++)
            {
                var err = Math.Abs(value - palette[p]);
                if (err < bestErr)
                {
                    bestErr = err;
                    best = p;
                }
            }
            bits |= ((ulong)best & 0x7UL) << (3 * i);
        }

        for (var i = 0; i < 6; i++)
        {
            output[2 + i] = (byte)((bits >> (8 * i)) & 0xFF);
        }
    }

    private static byte ChannelValue(Rgba px, Channel channel) => channel switch
    {
        Channel.Red => px.R,
        Channel.Green => px.G,
        Channel.Alpha => px.A,
        _ => px.A
    };

    private static void WriteColorBlock(ReadOnlySpan<Rgba> block, Span<byte> output)
    {
        var unique = block.ToArray()
            .Select(p => new Rgba(p.R, p.G, p.B, 255))
            .Distinct()
            .ToArray();

        ushort bestC0 = 0;
        ushort bestC1 = 0;
        uint bestBits = 0;
        long bestError = long.MaxValue;

        foreach (var a in unique)
        {
            foreach (var b in unique)
            {
                var c0 = ToRgb565(a);
                var c1 = ToRgb565(b);
                if (c0 < c1)
                {
                    (c0, c1) = (c1, c0);
                }

                EvaluateColorPair(block, c0, c1, out var bits, out var error);
                if (error < bestError)
                {
                    bestError = error;
                    bestC0 = c0;
                    bestC1 = c1;
                    bestBits = bits;
                }
            }
        }

        output[0] = (byte)(bestC0 & 0xFF);
        output[1] = (byte)(bestC0 >> 8);
        output[2] = (byte)(bestC1 & 0xFF);
        output[3] = (byte)(bestC1 >> 8);
        output[4] = (byte)(bestBits & 0xFF);
        output[5] = (byte)((bestBits >> 8) & 0xFF);
        output[6] = (byte)((bestBits >> 16) & 0xFF);
        output[7] = (byte)((bestBits >> 24) & 0xFF);
    }

    private static void EvaluateColorPair(ReadOnlySpan<Rgba> block, ushort c0, ushort c1, out uint bits, out long error)
    {
        Span<Rgba> palette = stackalloc Rgba[4];
        palette[0] = FromRgb565(c0);
        palette[1] = FromRgb565(c1);
        palette[2] = Lerp(palette[0], palette[1], 2, 1, 3);
        palette[3] = Lerp(palette[0], palette[1], 1, 2, 3);

        bits = 0;
        error = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestErr = long.MaxValue;
            for (var p = 0; p < 4; p++)
            {
                var e = ColorDistance(block[i], palette[p]);
                if (e < bestErr)
                {
                    bestErr = e;
                    best = p;
                }
            }
            error += bestErr;
            bits |= (uint)(best & 0x3) << (2 * i);
        }
    }

    private static long ColorDistance(Rgba a, Rgba b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private static Rgba Lerp(Rgba a, Rgba b, int aw, int bw, int div) => new(
        (byte)((a.R * aw + b.R * bw + div / 2) / div),
        (byte)((a.G * aw + b.G * bw + div / 2) / div),
        (byte)((a.B * aw + b.B * bw + div / 2) / div),
        255);

    private static ushort ToRgb565(Rgba px)
    {
        // RGB565 gives green one extra bit. If we independently quantize a near-neutral
        // dark color like (4,4,4), it expands back as roughly (0,4,0), which shows up as
        // a faint green tint on "black" LEGO material backgrounds. Treat near-neutral
        // colors as neutral before packing so black/grey stays visually neutral.
        var min = Math.Min(px.R, Math.Min(px.G, px.B));
        var max = Math.Max(px.R, Math.Max(px.G, px.B));
        if (max - min <= 3)
        {
            var gray = (px.R + px.G + px.B + 1) / 3;
            if (gray <= 5)
            {
                return 0;
            }

            var r5 = Math.Clamp((gray + 4) / 8, 0, 31);
            var g6 = r5 >= 31 ? 63 : Math.Clamp(r5 * 2, 0, 63);
            return (ushort)((r5 << 11) | (g6 << 5) | r5);
        }

        var r = px.R >> 3;
        var g = px.G >> 2;
        var b = px.B >> 3;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Rgba FromRgb565(ushort value)
    {
        var r5 = (value >> 11) & 0x1F;
        var g6 = (value >> 5) & 0x3F;
        var b5 = value & 0x1F;
        return new Rgba(
            (byte)((r5 << 3) | (r5 >> 2)),
            (byte)((g6 << 2) | (g6 >> 4)),
            (byte)((b5 << 3) | (b5 >> 2)),
            255);
    }

    private static void WriteExternalMips(
        List<MipTemplate> mips,
        IReadOnlyDictionary<MipTemplate, byte[]> encodedMips,
        string templateUbulk,
        string outputUbulk,
        Result result)
    {
        var bytes = File.ReadAllBytes(templateUbulk);
        foreach (var mip in mips.Where(m => m.IsExternal))
        {
            if (mip.OffsetInFile < 0 || mip.OffsetInFile + mip.SizeOnDisk > bytes.Length)
            {
                throw new InvalidOperationException($"External mip {mip.SizeX}x{mip.SizeY} is outside the .ubulk bounds.");
            }
            Buffer.BlockCopy(encodedMips[mip], 0, bytes, checked((int)mip.OffsetInFile), mip.SizeOnDisk);
            result.Log.Add($"external mip {mip.SizeX}x{mip.SizeY}: .ubulk+0x{mip.OffsetInFile:X}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputUbulk)!);
        File.WriteAllBytes(outputUbulk, bytes);
    }

    private static void WriteInlineMips(
        List<MipTemplate> mips,
        IReadOnlyDictionary<MipTemplate, byte[]> encodedMips,
        byte[] uassetBytes,
        string payloadLabel,
        Result result)
    {
        var inline = mips.Where(m => m.IsInline).OrderBy(m => m.OffsetInFile).ToList();
        if (inline.Count == 0)
        {
            return;
        }

        var last = inline[^1];
        var footerLength = SplitExportFooterLength(uassetBytes, payloadLabel);
        var writableLength = uassetBytes.Length - footerLength;
        // For this game's split .uexp UI donor, OffsetInFile points at the
        // serialized FByteBulkData record, not the first byte of the BC7
        // payload. The first record's 0x7F value is followed by its 0x11-byte
        // header, so its pixels begin at 0x90. Writing at 0x7F overwrites the
        // serialized PF_BC7/platform-data fields and FModel consequently
        // reports PF_Unknown. Every inline UI mip uses that same +0x11 base;
        // the native inter-record bytes remain untouched between payloads.
        // A non-split .uasset still derives its local base from the final mip.
        var inlineBase = payloadLabel.Equals(".uexp", StringComparison.OrdinalIgnoreCase)
            ? 0x11
            : uassetBytes.Length - (last.OffsetInFile + last.SizeOnDisk);
        if (inlineBase < 0)
        {
            throw new InvalidOperationException("Could not derive inline mip base from template offsets.");
        }

        foreach (var mip in inline)
        {
            var absolute = inlineBase + mip.OffsetInFile;
            if (absolute < 0 || absolute + mip.SizeOnDisk > uassetBytes.Length)
            {
                throw new InvalidOperationException($"Inline mip {mip.SizeX}x{mip.SizeY} is outside the {payloadLabel} bounds.");
            }

            var writeLength = mip.SizeOnDisk;
            if (footerLength > 0 && absolute + writeLength > writableLength)
            {
                writeLength = checked((int)(writableLength - absolute));
                if (writeLength <= 0)
                {
                    throw new InvalidOperationException($"Inline mip {mip.SizeX}x{mip.SizeY} overlaps only the {payloadLabel} footer.");
                }
            }

            Buffer.BlockCopy(encodedMips[mip], 0, uassetBytes, checked((int)absolute), writeLength);
            result.Log.Add(writeLength == mip.SizeOnDisk
                ? $"inline mip {mip.SizeX}x{mip.SizeY}: {payloadLabel}+0x{absolute:X}"
                : $"inline mip {mip.SizeX}x{mip.SizeY}: {payloadLabel}+0x{absolute:X} wrote={writeLength}/{mip.SizeOnDisk} preserved-footer-tail={mip.SizeOnDisk - writeLength}");
        }

        if (footerLength > 0)
        {
            result.Log.Add($"preserved {payloadLabel} footer bytes={footerLength}");
        }
    }

    private static int SplitExportFooterLength(byte[] bytes, string payloadLabel)
    {
        // Split .uexp files end with the Unreal package tag (C1 83 2A 9E). The
        // inline Texture2D donor's last mip sits immediately before that footer.
        // The first UI-icon implementation accidentally wrote the final tiny mip
        // all the way to EOF, corrupting this tag; FModel then dropped the cooked
        // platform data to PF_Unknown. Preserve the footer for split payloads.
        if (!payloadLabel.Equals(".uexp", StringComparison.OrdinalIgnoreCase) || bytes.Length < 4)
        {
            return 0;
        }

        return bytes[^4] == 0xC1 &&
               bytes[^3] == 0x83 &&
               bytes[^2] == 0x2A &&
               bytes[^1] == 0x9E
            ? 4
            : 0;
    }

    private static void WriteReport(Result result, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only /Game package paths are supported. Got: {packagePath}");
        }

        return Path.Combine(contentRoot, packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
