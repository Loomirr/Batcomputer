using System.Buffers.Binary;

namespace Batcomputer;

/// <summary>
/// Exercises the exact native suit-selector cook without adding a texture recipe to a project,
/// changing UIMD icon paths, packaging a mod, or writing to the game installation.
/// </summary>
internal static class SuitIconDryRunService
{
    internal sealed record Result(string Folder, string SourcePng, TextureCookService.Result Cook);

    internal static byte[] DecodePngDataUrl(string dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith(prefix, StringComparison.Ordinal) ||
            dataUrl.Length > 1_500_000)
            throw new InvalidDataException("The viewer did not send a supported PNG image.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataUrl[prefix.Length..]); }
        catch (FormatException ex) { throw new InvalidDataException("The icon PNG data is invalid.", ex); }
        ValidatePngHeader(bytes);
        return bytes;
    }

    internal static void ValidatePngHeader(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length is < 45 or > 1_000_000 || !bytes[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(8, 4)) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)) != 256 ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)) != 256)
            throw new InvalidDataException("A suit selector test requires a valid-looking 256 × 256 PNG.");
    }

    internal static Result Cook(string projectRoot, byte[] pngBytes, string outputFolder)
        => CookToPackage(projectRoot, pngBytes, outputFolder,
            "/Game/Mods/BatcomputerIconTest/Textures/T_SuitIcon_DryRun");

    internal static Result CookToPackage(string projectRoot, byte[] pngBytes, string outputFolder, string packagePath)
    {
        ValidatePngHeader(pngBytes);
        if (!packagePath.StartsWith("/Game/Mods/", StringComparison.Ordinal) ||
            !UnrealPathUtil.NormalizePackagePath(packagePath).Equals(packagePath, StringComparison.Ordinal))
            throw new InvalidDataException("The suit icon needs a safe /Game/Mods texture path.");
        if (Directory.Exists(outputFolder) && Directory.EnumerateFileSystemEntries(outputFolder).Any())
            throw new IOException("The icon output folder is not empty. Choose a new folder so nothing is overwritten.");

        var templateJson = TextureCookTemplateService.TemplateJsonPath(
            projectRoot, TextureCookTemplateService.NativeSuitIconTemplateFolder);
        if (!TextureCookTemplateService.IsTemplateReady(templateJson))
            throw new InvalidOperationException("The verified native 256px BC7 suit-icon template is not ready. Refresh game assets first.");

        Directory.CreateDirectory(outputFolder);
        var sourcePng = Path.Combine(outputFolder, "Suit-icon-256.png");
        using (var source = new FileStream(sourcePng, FileMode.CreateNew, FileAccess.Write))
            source.Write(pngBytes);

        var cookedRoot = Path.Combine(outputFolder, "Cooked", "LEGOBatmanLotDK", "Content");
        var cook = new TextureCookService(projectRoot).Cook(new TextureCookService.Request
        {
            SourceImagePath = sourcePng,
            TemplateJsonPath = templateJson,
            OutputContentRoot = cookedRoot,
            OutputPackagePath = packagePath,
            NearestNeighborMips = false,
            BleedTransparentRgb = true,
            WriteInlineMips = true,
            Bc7InputLayout = "rgba",
            Bc7Quality = "best",
        });
        if (!cook.Status.Equals("created", StringComparison.OrdinalIgnoreCase) ||
            cook.Width != 256 || cook.Height != 256 ||
            !cook.PixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase) ||
            cook.MipCount != 9 || !File.Exists(cook.OutputUasset) || !File.Exists(cook.OutputUexp))
            throw new InvalidOperationException(cook.Error ??
                $"Suit icon cook did not produce the verified 256px BC7 / nine-mip layout (status: {cook.Status}).");
        return new Result(outputFolder, sourcePng, cook);
    }

    internal static int RunCli(string projectRoot, string sourcePng, string outputFolder, TextWriter log)
    {
        try
        {
            var result = Cook(projectRoot, File.ReadAllBytes(sourcePng), outputFolder);
            log.WriteLine($"suit icon dry run: PASS ({result.Cook.Width}x{result.Cook.Height}, {result.Cook.PixelFormat}, {result.Cook.MipCount} mips)");
            log.WriteLine(result.Folder);
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("suit icon dry run: FAIL: " + ex.Message);
            return 1;
        }
    }
}
