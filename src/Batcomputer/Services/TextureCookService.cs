using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    // Version 10 adds a mandatory source-image size/hash to the cook report.
    public const int CurrentEncoderVersion = 10;

    private const CustomSerializationFlags NameMapOnlyPatchFlags =
        CustomSerializationFlags.SkipParsingExports |
        CustomSerializationFlags.SkipPreloadDependencyLoading;

    public sealed class Request
    {
        public string SourceImagePath { get; set; } = "";
        public string TemplateJsonPath { get; set; } = "";
        public string OutputContentRoot { get; set; } = "";
        public string OutputPackagePath { get; set; } = "";
        public bool NearestNeighborMips { get; set; } = false;
        public bool BleedTransparentRgb { get; set; } = false;
        public bool WriteInlineMips { get; set; } = true;
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
        public int InlinePayloadOffsetBias { get; set; }
        public string RecipeFingerprint { get; set; } = "";
        // Bind the cooked payload to the exact source image bytes used for this
        // attempt.  Output hashes prove the package is internally complete; the
        // source hash additionally proves that an edited saved image has actually
        // been recooked rather than being hidden behind an otherwise-valid report.
        public long SourceImageBytes { get; set; }
        public string SourceImageSha256 { get; set; } = "";
        public long OutputUassetBytes { get; set; }
        public string OutputUassetSha256 { get; set; } = "";
        public long OutputUexpBytes { get; set; }
        public string OutputUexpSha256 { get; set; } = "";
        public long OutputUbulkBytes { get; set; }
        public string OutputUbulkSha256 { get; set; } = "";
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
        int InlinePayloadOffsetBias,
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
        string? attemptBase = null;
        try
        {
            if (!File.Exists(request.SourceImagePath))
            {
                return Fail(result, $"Source image not found: {request.SourceImagePath}");
            }

            if (!TextureCookTemplateService.IsTemplateReady(request.TemplateJsonPath))
            {
                return Fail(result, $"Verified Texture2D template is incomplete or has an unrecognized layout: {request.TemplateJsonPath}");
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
            var donorPixelFormat = NormalizePixelFormat(template.PixelFormat);
            if (!string.IsNullOrWhiteSpace(request.ForcePixelFormat))
            {
                var forcedPixelFormat = NormalizePixelFormat(request.ForcePixelFormat);
                if (!IsSupportedPixelFormat(forcedPixelFormat))
                {
                    return Fail(result, $"Unsupported forced Texture2D pixel format: {request.ForcePixelFormat}.");
                }

                // A format override must also rewrite the donor package's format
                // name. The only verified conversion is the native BC7 UI shell
                // to same-block-size BC3/DXT5. Treat an identity force as a no-op
                // and reject every other header/payload mismatch.
                if (!forcedPixelFormat.Equals(donorPixelFormat, StringComparison.OrdinalIgnoreCase) &&
                    !(donorPixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase) &&
                      forcedPixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase)))
                {
                    return Fail(result,
                        $"Unsafe forced Texture2D pixel-format conversion {donorPixelFormat} -> {forcedPixelFormat} was blocked. " +
                        "Only the verified PF_BC7 -> PF_DXT5 UI-shell conversion is supported.");
                }

                if (!forcedPixelFormat.Equals(donorPixelFormat, StringComparison.OrdinalIgnoreCase))
                {
                    result.Log.Add($"forced pixel format: {donorPixelFormat} -> {forcedPixelFormat}");
                    template = template with { PixelFormat = forcedPixelFormat };
                }
            }
            result.RecipeFingerprint = RecipeFingerprintFor(request.TemplateJsonPath, template.PixelFormat);
            ValidateMipRecipe(template);
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
            result.InlinePayloadOffsetBias = template.InlinePayloadOffsetBias;
            if (result.InlineMipCount > 0 && !request.WriteInlineMips)
            {
                return Fail(result,
                    "This Texture2D recipe contains inline lower mips. Preserving the donor tail would make texture quality settings select unrelated pixels, so the cook was blocked.");
            }

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

            var sourceIntegrityBefore = FileIntegrity(request.SourceImagePath);

            var outputBase = PackagePathToBasePath(request.OutputContentRoot, outputPackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
            attemptBase = outputBase + ".texture-cook-attempt-" + Guid.NewGuid().ToString("N");
            var attemptUasset = attemptBase + ".uasset";
            var attemptUexp = attemptBase + ".uexp";
            var attemptUbulk = attemptBase + ".ubulk";
            result.OutputUasset = outputBase + ".uasset";
            result.OutputUexp = outputBase + ".uexp";
            result.OutputUbulk = outputBase + ".ubulk";

            var uassetBytes = File.ReadAllBytes(templateUasset);
            byte[]? uexpBytes = File.Exists(templateUexp)
                ? File.ReadAllBytes(templateUexp)
                : null;
            var encodedMips = EncodeAllMips(
                request.SourceImagePath,
                template.Mips,
                request.NearestNeighborMips,
                request.BleedTransparentRgb,
                template.PixelFormat,
                request.Bc7InputLayout,
                request.Bc7Quality);
            var sourceIntegrityAfter = FileIntegrity(request.SourceImagePath);
            if (sourceIntegrityAfter != sourceIntegrityBefore)
            {
                throw new IOException(
                    "The source image changed while it was being cooked. No generated texture files were replaced; save the image and retry.");
            }
            (result.SourceImageBytes, result.SourceImageSha256) = sourceIntegrityAfter;
            result.Log.Add($"source image SHA-256: {result.SourceImageSha256}");
            if (template.PixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase))
            {
                result.Log.Add($"BC7 input layout: {NormalizeBc7InputLayout(request.Bc7InputLayout)} quality={NormalizeBc7Quality(request.Bc7Quality)}");
            }
            result.Log.Add(request.NearestNeighborMips
                ? "mip filter: nearest-neighbor"
                : request.BleedTransparentRgb
                    ? "mip filter: high-quality, alpha-safe edge bleed"
                    : "mip filter: high-quality");
            if (template.Mips.Any(m => m.IsExternal))
            {
                WriteExternalMips(template.Mips, encodedMips, templateUbulk, attemptUbulk, result);
            }
            else
            {
                result.OutputUbulk = "";
                result.Log.Add("template has no external .ubulk mips");
            }

            var inlineMips = template.Mips.Where(m => m.IsInline).ToList();
            if (inlineMips.Count > 0)
            {
                if (uexpBytes is not null)
                {
                    WriteInlineMips(template.Mips, encodedMips, uexpBytes, ".uexp", template.InlinePayloadOffsetBias, result);
                }
                else
                {
                    WriteInlineMips(template.Mips, encodedMips, uassetBytes, ".uasset", template.InlinePayloadOffsetBias, result);
                }
            }

            File.WriteAllBytes(attemptUasset, uassetBytes);
            if (uexpBytes is not null)
            {
                File.WriteAllBytes(attemptUexp, uexpBytes);
                result.Log.Add("prepared complete split export payload");
            }
            if (template.Mips.Count > 0 &&
                template.Mips.All(m => m.IsInline) &&
                CanSameLengthIdentityPatch(template, outputPackagePath) &&
                !RequiresNameMapTextureFormatRewrite(template))
            {
                PatchSameLengthIdentity(template, outputPackagePath, uassetBytes, result);
                File.WriteAllBytes(attemptUasset, uassetBytes);
                result.Log.Add("same-length inline Texture2D identity patch complete; UAssetAPI rewrite skipped to preserve split-export offsets");
            }
            else
            {
                try
                {
                    RewriteTextureIdentityWithUAssetApi(attemptUasset, template, outputPackagePath, result);
                    // A name-map rewrite may grow the .uasset header. UAssetAPI
                    // needs the paired export loaded to update that header, but
                    // this deliberately raw Texture2D path keeps the cooked
                    // inline mip bytes authored above. Restore those exact bytes
                    // after the header write instead of accepting a serializer-
                    // generated split payload.
                    if (uexpBytes is not null)
                    {
                        File.WriteAllBytes(attemptUexp, uexpBytes);
                        result.Log.Add("restored cooked split export payload after UAssetAPI header rewrite");
                    }
                }
                catch (Exception ex) when (CanSameLengthIdentityPatch(template, outputPackagePath) && !RequiresNameMapTextureFormatRewrite(template))
                {
                    result.Warnings.Add($"UAssetAPI identity rewrite was not available for this template ({ex.Message.Split('\n')[0]}). Used same-length binary identity patch fallback.");
                    PatchSameLengthIdentity(template, outputPackagePath, uassetBytes, result);
                    File.WriteAllBytes(attemptUasset, uassetBytes);
                }
            }
            result.Status = "created";
            result.Log.Add($"wrote {result.OutputUasset}");
            if (!string.IsNullOrWhiteSpace(result.OutputUbulk))
            {
                result.Log.Add($"wrote {result.OutputUbulk}");
            }
            PopulateOutputIntegrity(
                result,
                attemptBase,
                includeUexp: uexpBytes is not null,
                includeUbulk: template.Mips.Any(mip => mip.IsExternal));
            WriteReport(result, attemptBase + ".texture-cook-report.json");
            CommitCookAttempt(
                attemptBase,
                outputBase,
                includeUexp: uexpBytes is not null,
                includeUbulk: template.Mips.Any(mip => mip.IsExternal));
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(attemptBase))
            {
                DeleteCookAttemptFiles(attemptBase);
            }
        }
    }

    private static Result Fail(Result result, string error)
    {
        result.Status = "error";
        result.Error = error;
        return result;
    }

    internal static string RecipeFingerprintFor(string templateJsonPath, string? effectivePixelFormat = null)
    {
        var templateBase = Path.Combine(
            Path.GetDirectoryName(templateJsonPath)!,
            Path.GetFileNameWithoutExtension(templateJsonPath));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintFile(hash, "recipe", templateJsonPath);
        AppendFingerprintFile(hash, "uasset", templateBase + ".uasset");
        AppendFingerprintFile(hash, "uexp", templateBase + ".uexp");
        var ubulk = templateBase + ".ubulk";
        if (File.Exists(ubulk))
        {
            AppendFingerprintFile(hash, "ubulk", ubulk);
        }
        else
        {
            hash.AppendData(Encoding.UTF8.GetBytes("ubulk:none"));
        }
        effectivePixelFormat = string.IsNullOrWhiteSpace(effectivePixelFormat)
            ? ReadTemplate(templateJsonPath).PixelFormat
            : effectivePixelFormat;
        hash.AppendData(Encoding.UTF8.GetBytes(
            "effective-pixel-format:" + NormalizePixelFormat(effectivePixelFormat)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendFingerprintFile(IncrementalHash hash, string role, string path)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(role + ":"));
        hash.AppendData(File.ReadAllBytes(path));
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
        var inlinePayloadOffsetBias = root.TryGetProperty("InlinePayloadOffsetBias", out var bias) && bias.TryGetInt32(out var parsedBias)
            ? parsedBias
            : 0;
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

        return new TextureTemplate(name, package, sizeX, sizeY, pixelFormat, inlinePayloadOffsetBias, mips);
    }

    private static bool IsSupportedPixelFormat(string pixelFormat) =>
        pixelFormat.Equals("PF_DXT1", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("PF_B8G8R8A8", StringComparison.OrdinalIgnoreCase);

    private static void ValidateMipRecipe(TextureTemplate template)
    {
        if (template.SizeX <= 0 || template.SizeY <= 0 || template.Mips.Count == 0)
        {
            throw new InvalidOperationException("Texture2D recipe has invalid dimensions or no cooked mips.");
        }
        if (template.InlinePayloadOffsetBias < 0)
        {
            throw new InvalidOperationException("Texture2D inline payload offset bias cannot be negative.");
        }

        var expectedWidth = template.SizeX;
        var expectedHeight = template.SizeY;
        var sawInline = false;
        MipTemplate? priorInline = null;
        long expectedExternalOffset = 0;
        foreach (var (mip, index) in template.Mips.Select((mip, index) => (mip, index)))
        {
            if (mip.SizeX != expectedWidth || mip.SizeY != expectedHeight)
            {
                throw new InvalidOperationException(
                    $"Texture2D mip {index} is {mip.SizeX}x{mip.SizeY}; expected {expectedWidth}x{expectedHeight}.");
            }

            var expectedBytes = ExpectedMipBytes(template.PixelFormat, mip.SizeX, mip.SizeY);
            if (mip.SizeOnDisk != expectedBytes || mip.ElementCount != expectedBytes)
            {
                throw new InvalidOperationException(
                    $"Texture2D mip {index} ({mip.SizeX}x{mip.SizeY}) declares {mip.SizeOnDisk}/{mip.ElementCount} bytes; expected {expectedBytes} for {template.PixelFormat}.");
            }

            if (mip.IsExternal == mip.IsInline)
            {
                throw new InvalidOperationException(
                    $"Texture2D mip {index} must declare exactly one supported storage location; flags were '{mip.BulkDataFlags}'.");
            }

            if (mip.IsInline)
            {
                sawInline = true;
                if (priorInline is not null)
                {
                    var expectedOffset = checked(priorInline.OffsetInFile + priorInline.SizeOnDisk + 0x10L);
                    if (mip.OffsetInFile != expectedOffset)
                    {
                        throw new InvalidOperationException(
                            $"Texture2D inline mip {index} offset is {mip.OffsetInFile}; expected {expectedOffset} after the prior inline payload.");
                    }
                }
                priorInline = mip;
            }
            else if (mip.IsExternal)
            {
                if (sawInline)
                {
                    throw new InvalidOperationException("Texture2D recipe moves back to external bulk data after its inline mip tail began.");
                }
                if (mip.OffsetInFile != expectedExternalOffset)
                {
                    throw new InvalidOperationException(
                        $"Texture2D external mip {index} offset is {mip.OffsetInFile}; expected contiguous offset {expectedExternalOffset}.");
                }
                expectedExternalOffset = checked(expectedExternalOffset + mip.SizeOnDisk);
            }
            else
            {
                throw new InvalidOperationException($"Texture2D mip {index} has unsupported bulk-data flags '{mip.BulkDataFlags}'.");
            }

            expectedWidth = Math.Max(1, expectedWidth / 2);
            expectedHeight = Math.Max(1, expectedHeight / 2);
        }

        var last = template.Mips[^1];
        if (last.SizeX != 1 || last.SizeY != 1)
        {
            throw new InvalidOperationException(
                $"Texture2D recipe stops at {last.SizeX}x{last.SizeY}. A complete mip chain through 1x1 is required so every texture-quality setting resolves authored pixels.");
        }
    }

    internal static byte[] RewriteInlineMipsForRegression(string templateJsonPath, byte[] donorPayload)
    {
        var template = ReadTemplate(templateJsonPath);
        ValidateMipRecipe(template);
        var encoded = template.Mips
            .Select((mip, index) => (Mip: mip, Fill: (byte)((index % 251) + 1)))
            .Where(entry => entry.Mip.IsInline)
            .ToDictionary(
                entry => entry.Mip,
                entry => Enumerable.Repeat(entry.Fill, entry.Mip.SizeOnDisk).ToArray());
        var rewritten = donorPayload.ToArray();
        WriteInlineMips(
            template.Mips,
            encoded,
            rewritten,
            ".uexp",
            template.InlinePayloadOffsetBias,
            new Result());
        return rewritten;
    }

    private static int ExpectedMipBytes(string pixelFormat, int width, int height)
    {
        if (pixelFormat.Equals("PF_B8G8R8A8", StringComparison.OrdinalIgnoreCase))
        {
            return checked(width * height * 4);
        }

        var blockBytes = pixelFormat.Equals("PF_DXT1", StringComparison.OrdinalIgnoreCase) ? 8 : 16;
        return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes);
    }

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

    private static Dictionary<MipTemplate, byte[]> EncodeAllMips(
        string sourceImagePath,
        List<MipTemplate> mips,
        bool nearest,
        bool bleedTransparentRgb,
        string pixelFormat,
        string bc7InputLayout,
        string bc7Quality)
    {
        using var source = new Bitmap(sourceImagePath);
        // Read the PNG once, before any GDI+ draw/copy operation. GDI+ treats
        // alpha as compositing opacity and converts through premultiplied color
        // when an image is copied or resized. That silently turns RGB under
        // alpha=0 black and badly distorts RGB under very small alpha values.
        // In LEGO character textures alpha is shader data (including the
        // plastic-vs-print detail selector), so every channel must be sampled
        // independently as straight RGBA.
        var sourcePixels = ReadRgba(source);
        var output = new Dictionary<MipTemplate, byte[]>();
        Rgba[]? previousMip = null;
        var previousWidth = 0;
        var previousHeight = 0;
        foreach (var mip in mips)
        {
            // Build ordinary half-size mip tails from the preceding straight-
            // RGBA level. If the source was smaller than the template's top
            // level, prefer the original once the requested mip fits it so an
            // upsample is never immediately downsampled again.
            var canContinueMipChain = previousMip is not null &&
                                      previousWidth == mip.SizeX * 2 &&
                                      previousHeight == mip.SizeY * 2 &&
                                      previousWidth <= source.Width &&
                                      previousHeight <= source.Height;
            var resizedPixels = canContinueMipChain
                ? ResizeRgba(previousMip!, previousWidth, previousHeight, mip.SizeX, mip.SizeY, nearest)
                : ResizeRgba(sourcePixels, source.Width, source.Height, mip.SizeX, mip.SizeY, nearest);
            previousMip = resizedPixels;
            previousWidth = mip.SizeX;
            previousHeight = mip.SizeY;

            // Edge bleed and BC5 normal renormalization intentionally mutate a
            // level for encoding. Keep the unmodified straight-RGBA level as
            // the input to the next mip so one format-specific transform cannot
            // leak into the general resampling path.
            var pixels = bleedTransparentRgb || pixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase)
                ? resizedPixels.ToArray()
                : resizedPixels;
            if (bleedTransparentRgb)
            {
                BleedTransparentRgb(pixels, mip.SizeX, mip.SizeY);
            }
            if (pixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase))
            {
                RenormalizeNormalMap(pixels);
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

    /// <summary>
    /// Focused release-regression hook for the exact straight-RGBA source and
    /// mip path used by real texture cooks.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A)[] ReadStraightRgbaForRegression(
        string sourceImagePath,
        int width,
        int height,
        bool nearest = false)
    {
        using var source = new Bitmap(sourceImagePath);
        return ResizeRgba(ReadRgba(source), source.Width, source.Height, width, height, nearest)
            .Select(pixel => (pixel.R, pixel.G, pixel.B, pixel.A))
            .ToArray();
    }

    /// <summary>Encodes one regression mip through the same channel paths as a real cook.</summary>
    internal static byte[] EncodeSourceMipForRegression(
        string sourceImagePath,
        int width,
        int height,
        string pixelFormat)
    {
        using var source = new Bitmap(sourceImagePath);
        var pixels = ResizeRgba(ReadRgba(source), source.Width, source.Height, width, height, nearest: false);
        pixelFormat = NormalizePixelFormat(pixelFormat);
        if (pixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase))
        {
            RenormalizeNormalMap(pixels);
        }

        return pixelFormat switch
        {
            "PF_DXT1" => Bc1Encode(pixels, width, height),
            "PF_DXT5" => Bc3Encode(pixels, width, height),
            "PF_BC5" => Bc5Encode(pixels, width, height),
            "PF_BC7" => Bc7Encode(pixels, width, height, "rgba", "best"),
            "PF_B8G8R8A8" => Bgra8Encode(pixels, width, height),
            _ => throw new InvalidOperationException($"Unsupported regression Texture2D pixel format: {pixelFormat}"),
        };
    }

    private static void RenormalizeNormalMap(Rgba[] pixels)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            var x = pixels[i].R / 127.5f - 1f;
            var y = pixels[i].G / 127.5f - 1f;
            var z = pixels[i].B / 127.5f - 1f;
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length < 0.0001f)
            {
                pixels[i] = new Rgba(128, 128, 255, pixels[i].A);
                continue;
            }

            x /= length;
            y /= length;
            pixels[i] = new Rgba(
                (byte)Math.Clamp((int)MathF.Round((x * 0.5f + 0.5f) * 255f), 0, 255),
                (byte)Math.Clamp((int)MathF.Round((y * 0.5f + 0.5f) * 255f), 0, 255),
                pixels[i].B,
                pixels[i].A);
        }
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

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private static Rgba[] ResizeRgba(
        Rgba[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        bool nearest)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            source.Length != checked(sourceWidth * sourceHeight))
        {
            throw new InvalidOperationException("Texture source pixels do not match their declared dimensions.");
        }
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Texture mip dimensions must be positive. Got {width}x{height}.");
        }
        if (sourceWidth == width && sourceHeight == height)
        {
            return source.ToArray();
        }

        return nearest
            ? ResizeRgbaNearest(source, sourceWidth, sourceHeight, width, height)
            : width <= sourceWidth && height <= sourceHeight
                ? ResizeRgbaArea(source, sourceWidth, sourceHeight, width, height)
                : ResizeRgbaBilinear(source, sourceWidth, sourceHeight, width, height);
    }

    private static Rgba[] ResizeRgbaNearest(
        Rgba[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        var output = new Rgba[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min((int)(((long)(y * 2 + 1) * sourceHeight) / (height * 2L)), sourceHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min((int)(((long)(x * 2 + 1) * sourceWidth) / (width * 2L)), sourceWidth - 1);
                output[y * width + x] = source[sourceY * sourceWidth + sourceX];
            }
        }
        return output;
    }

    /// <summary>
    /// Box-filters each straight channel independently. Alpha is not multiplied
    /// into RGB: these textures use alpha as shader input, not just opacity.
    /// Fractional source-pixel coverage also handles non-power-of-two imports.
    /// </summary>
    private static Rgba[] ResizeRgbaArea(
        Rgba[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        var output = new Rgba[checked(width * height)];
        var scaleX = sourceWidth / (double)width;
        var scaleY = sourceHeight / (double)height;
        for (var y = 0; y < height; y++)
        {
            var top = y * scaleY;
            var bottom = (y + 1) * scaleY;
            var firstY = Math.Max(0, (int)Math.Floor(top));
            var lastY = Math.Min(sourceHeight - 1, (int)Math.Ceiling(bottom) - 1);
            for (var x = 0; x < width; x++)
            {
                var left = x * scaleX;
                var right = (x + 1) * scaleX;
                var firstX = Math.Max(0, (int)Math.Floor(left));
                var lastX = Math.Min(sourceWidth - 1, (int)Math.Ceiling(right) - 1);
                double red = 0;
                double green = 0;
                double blue = 0;
                double alpha = 0;
                double totalWeight = 0;
                for (var sourceY = firstY; sourceY <= lastY; sourceY++)
                {
                    var yWeight = Math.Min(bottom, sourceY + 1d) - Math.Max(top, sourceY);
                    if (yWeight <= 0)
                    {
                        continue;
                    }
                    for (var sourceX = firstX; sourceX <= lastX; sourceX++)
                    {
                        var xWeight = Math.Min(right, sourceX + 1d) - Math.Max(left, sourceX);
                        var weight = xWeight * yWeight;
                        if (weight <= 0)
                        {
                            continue;
                        }

                        var pixel = source[sourceY * sourceWidth + sourceX];
                        red += pixel.R * weight;
                        green += pixel.G * weight;
                        blue += pixel.B * weight;
                        alpha += pixel.A * weight;
                        totalWeight += weight;
                    }
                }

                output[y * width + x] = totalWeight > 0
                    ? new Rgba(
                        RoundedByte(red / totalWeight),
                        RoundedByte(green / totalWeight),
                        RoundedByte(blue / totalWeight),
                        RoundedByte(alpha / totalWeight))
                    : source[Math.Min(firstY, sourceHeight - 1) * sourceWidth + Math.Min(firstX, sourceWidth - 1)];
            }
        }
        return output;
    }

    private static Rgba[] ResizeRgbaBilinear(
        Rgba[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        var output = new Rgba[checked(width * height)];
        var scaleX = sourceWidth / (double)width;
        var scaleY = sourceHeight / (double)height;
        for (var y = 0; y < height; y++)
        {
            var sampleY = Math.Clamp((y + 0.5d) * scaleY - 0.5d, 0d, sourceHeight - 1d);
            var y0 = (int)Math.Floor(sampleY);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var fy = sampleY - y0;
            for (var x = 0; x < width; x++)
            {
                var sampleX = Math.Clamp((x + 0.5d) * scaleX - 0.5d, 0d, sourceWidth - 1d);
                var x0 = (int)Math.Floor(sampleX);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var fx = sampleX - x0;
                var topLeft = source[y0 * sourceWidth + x0];
                var topRight = source[y0 * sourceWidth + x1];
                var bottomLeft = source[y1 * sourceWidth + x0];
                var bottomRight = source[y1 * sourceWidth + x1];
                output[y * width + x] = new Rgba(
                    BilinearChannel(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R, fx, fy),
                    BilinearChannel(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G, fx, fy),
                    BilinearChannel(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B, fx, fy),
                    BilinearChannel(topLeft.A, topRight.A, bottomLeft.A, bottomRight.A, fx, fy));
            }
        }
        return output;
    }

    private static byte BilinearChannel(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, double x, double y)
    {
        var top = topLeft + (topRight - topLeft) * x;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * x;
        return RoundedByte(top + (bottom - top) * y);
    }

    private static byte RoundedByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static Rgba[] ReadRgba(Bitmap bitmap)
    {
        // Do not clone before reading. new Bitmap(bitmap) draws through GDI+'s
        // premultiplied-alpha path, destroying RGB values stored beneath
        // transparent and nearly-transparent alpha. Read native PNG layouts
        // directly; the uncommon fallback favors correctness over speed.
        try
        {
            return bitmap.PixelFormat switch
            {
                DrawingPixelFormat.Format32bppArgb => ReadDirectRgba(bitmap, bytesPerPixel: 4, hasAlpha: true),
                DrawingPixelFormat.Format32bppRgb => ReadDirectRgba(bitmap, bytesPerPixel: 4, hasAlpha: false),
                DrawingPixelFormat.Format24bppRgb => ReadDirectRgba(bitmap, bytesPerPixel: 3, hasAlpha: false),
                DrawingPixelFormat.Format8bppIndexed => ReadIndexedRgba(bitmap, bitsPerPixel: 8),
                DrawingPixelFormat.Format4bppIndexed => ReadIndexedRgba(bitmap, bitsPerPixel: 4),
                DrawingPixelFormat.Format1bppIndexed => ReadIndexedRgba(bitmap, bitsPerPixel: 1),
                _ => ReadRgbaWithGetPixel(bitmap),
            };
        }
        catch (ArgumentException)
        {
            return ReadRgbaWithGetPixel(bitmap);
        }
        catch (ExternalException)
        {
            return ReadRgbaWithGetPixel(bitmap);
        }
    }

    private static Rgba[] ReadDirectRgba(Bitmap bitmap, int bytesPerPixel, bool hasAlpha)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            var pixels = new Rgba[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, checked(y * data.Stride)),
                    row,
                    0,
                    row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = x * bytesPerPixel;
                    pixels[y * bitmap.Width + x] = new Rgba(
                        row[offset + 2],
                        row[offset + 1],
                        row[offset],
                        hasAlpha ? row[offset + 3] : (byte)255);
                }
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Rgba[] ReadIndexedRgba(Bitmap bitmap, int bitsPerPixel)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var palette = bitmap.Palette.Entries;
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            var pixels = new Rgba[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, checked(y * data.Stride)),
                    row,
                    0,
                    row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var paletteIndex = bitsPerPixel switch
                    {
                        8 => row[x],
                        4 => (row[x >> 1] >> ((x & 1) == 0 ? 4 : 0)) & 0x0F,
                        1 => (row[x >> 3] >> (7 - (x & 7))) & 0x01,
                        _ => throw new InvalidOperationException($"Unsupported indexed PNG depth: {bitsPerPixel}."),
                    };
                    if (paletteIndex >= palette.Length)
                    {
                        throw new InvalidOperationException("Indexed PNG referenced a color outside its palette.");
                    }

                    var color = palette[paletteIndex];
                    pixels[y * bitmap.Width + x] = new Rgba(color.R, color.G, color.B, color.A);
                }
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Rgba[] ReadRgbaWithGetPixel(Bitmap bitmap)
    {
        var pixels = new Rgba[checked(bitmap.Width * bitmap.Height)];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                pixels[y * bitmap.Width + x] = new Rgba(color.R, color.G, color.B, color.A);
            }
        }
        return pixels;
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
        int inlinePayloadOffsetBias,
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
        // UAssetGUI/CUE4Parse recipes normally record the actual .uexp payload
        // byte. The native inline-only UI recipe instead records the serialized
        // FByteBulkData marker and explicitly carries a +0x11 payload bias.
        // Keep that distinction in the recipe; applying the UI bias to mixed
        // world textures crosses into the next mip's metadata.
        var inlineBase = payloadLabel.Equals(".uexp", StringComparison.OrdinalIgnoreCase)
            ? inlinePayloadOffsetBias
            : uassetBytes.Length - (last.OffsetInFile + last.SizeOnDisk);
        if (inlineBase < 0)
        {
            throw new InvalidOperationException("Could not derive inline mip base from template offsets.");
        }

        if (payloadLabel.Equals(".uexp", StringComparison.OrdinalIgnoreCase))
        {
            if (footerLength != 4)
            {
                throw new InvalidOperationException("Split Texture2D export is missing its C1 83 2A 9E package footer.");
            }

            // Known UE5.6 split Texture2D layouts leave a 24-byte cooked tail
            // between the final inline mip and the 4-byte package footer.
            var finalPayloadEnd = checked(inlineBase + last.OffsetInFile + last.SizeOnDisk);
            var expectedFinalPayloadEnd = uassetBytes.Length - 28L;
            if (finalPayloadEnd != expectedFinalPayloadEnd)
            {
                throw new InvalidOperationException(
                    $"Inline Texture2D payload layout mismatch: final mip ends at 0x{finalPayloadEnd:X}, expected 0x{expectedFinalPayloadEnd:X}. Refresh the verified cook template before retrying.");
            }
        }

        foreach (var mip in inline)
        {
            var absolute = inlineBase + mip.OffsetInFile;
            if (absolute < 0 || absolute + mip.SizeOnDisk > writableLength)
            {
                throw new InvalidOperationException($"Inline mip {mip.SizeX}x{mip.SizeY} is outside the {payloadLabel} bounds.");
            }

            Buffer.BlockCopy(encodedMips[mip], 0, uassetBytes, checked((int)absolute), mip.SizeOnDisk);
            result.Log.Add($"inline mip {mip.SizeX}x{mip.SizeY}: {payloadLabel}+0x{absolute:X}");
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

    private static void PopulateOutputIntegrity(
        Result result,
        string attemptBase,
        bool includeUexp,
        bool includeUbulk)
    {
        (result.OutputUassetBytes, result.OutputUassetSha256) = FileIntegrity(attemptBase + ".uasset");
        if (includeUexp)
        {
            (result.OutputUexpBytes, result.OutputUexpSha256) = FileIntegrity(attemptBase + ".uexp");
        }
        if (includeUbulk)
        {
            (result.OutputUbulkBytes, result.OutputUbulkSha256) = FileIntegrity(attemptBase + ".ubulk");
        }
    }

    private static (long Bytes, string Sha256) FileIntegrity(string path)
    {
        using var stream = File.OpenRead(path);
        return (stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void CommitCookAttempt(
        string attemptBase,
        string outputBase,
        bool includeUexp,
        bool includeUbulk)
    {
        var reportExtension = ".texture-cook-report.json";
        var extensions = new[] { ".uasset", ".uexp", ".ubulk", reportExtension };
        // The report is the completeness marker and contains hashes for the
        // payload trio, so it must be installed last. A process interruption at
        // any earlier move leaves no acceptable report beside mixed files.
        var desired = new List<string> { ".uasset" };
        if (includeUexp) desired.Add(".uexp");
        if (includeUbulk) desired.Add(".ubulk");
        desired.Add(reportExtension);

        foreach (var extension in desired)
        {
            if (!File.Exists(attemptBase + extension))
            {
                throw new InvalidOperationException($"Texture cook attempt is incomplete: missing {extension}.");
            }
        }

        var backupBase = outputBase + ".texture-cook-backup-" + Guid.NewGuid().ToString("N");
        var backups = new List<(string Final, string Backup)>();
        var installed = new List<string>();
        try
        {
            foreach (var extension in extensions)
            {
                var final = outputBase + extension;
                if (!File.Exists(final))
                {
                    continue;
                }
                var backup = backupBase + extension;
                File.Move(final, backup);
                backups.Add((final, backup));
            }

            foreach (var extension in desired)
            {
                var final = outputBase + extension;
                File.Move(attemptBase + extension, final);
                installed.Add(final);
            }
        }
        catch
        {
            foreach (var final in installed)
            {
                try
                {
                    if (File.Exists(final)) File.Delete(final);
                }
                catch
                {
                    // Continue restoring every recoverable prior file.
                }
            }
            foreach (var (final, backup) in backups.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup)) File.Move(backup, final, overwrite: true);
                }
                catch
                {
                    // Preserve the backup file for manual recovery if a lock
                    // also prevents rollback.
                }
            }
            throw;
        }

        foreach (var (_, backup) in backups)
        {
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch
            {
                // A successful cook remains complete; an obsolete backup can
                // be cleaned on the next workspace maintenance pass.
            }
        }
    }

    private static void DeleteCookAttemptFiles(string attemptBase)
    {
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" })
        {
            try
            {
                var path = attemptBase + extension;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup only; never hide the real cook result.
            }
        }
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
