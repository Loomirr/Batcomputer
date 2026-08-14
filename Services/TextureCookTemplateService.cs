using System.Text.Json;

namespace Batcomputer;

internal static class TextureCookTemplateService
{
    // The native UI donor keeps a 16-byte serialized separator between its
    // inline BC7 mip payloads. Its OffsetInFile values point at each
    // FByteBulkData record; the BC7 bytes begin 0x11 bytes later. The cooker
    // applies that shared prefix while preserving the inter-record separators.
    private const int InlineMipInterRecordBytes = 0x10;

    private sealed record Definition(
        string Folder,
        string JsonFile,
        string ContentRelativePath,
        string PackagePath,
        int Width,
        int Height,
        string PixelFormat,
        int BytesPerPixel,
        int MipCount,
        int FirstMipWidth = 0,
        int FirstMipHeight = 0,
        int FirstMipOffset = 0,
        bool InlineMips = false);

    private sealed record TemplateDocument(
        string Name,
        string Package,
        int SizeX,
        int SizeY,
        string PixelFormat,
        IReadOnlyList<TemplateMip> Mips);

    private sealed record TemplateMip(int SizeX, int SizeY, TemplateBulkData BulkData);

    private sealed record TemplateBulkData(
        int ElementCount,
        int SizeOnDisk,
        int OffsetInFile,
        string BulkDataFlags);

    internal sealed class Result
    {
        public int Prepared { get; set; }
        public List<string> Logs { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private static readonly Definition[] Definitions =
    {
        new(
            "TextureStandaloneTemplate_DroneControlBGRA8",
            "T_GA_DroneControl_BatGirl_AO.json",
            "Models/Gadgets/GA_DroneControl_BatGirl/T_GA_DroneControl_BatGirl_AO",
            "/Game/Models/Gadgets/GA_DroneControl_BatGirl/T_GA_DroneControl_BatGirl_AO",
            2048, 2048, "PF_B8G8R8A8", 4, 5),
        new(
            "TextureStandaloneTemplate_BatarangBC5",
            "T_Batarang_N.json",
            "Models/Gadgets/GA_Batarang/T_Batarang_N",
            "/Game/Models/Gadgets/GA_Batarang/T_Batarang_N",
            2048, 2048, "PF_BC5", 1, 5),
        new(
            "TextureStandaloneTemplate_BatclawLogo_DXT5",
            "T_DECAL_BatclawLogo.json",
            "Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            "/Game/Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            2048, 2048, "PF_DXT5", 1, 5),
    };

    public static IReadOnlyList<string> RetocFilters { get; } = Definitions
        .Select(definition => "Content/" + definition.ContentRelativePath)
        .ToArray();

    /// <summary>Returns true only when every template required by the supported texture workflows is present.</summary>
    public static bool HasCoreTemplates(string projectRoot) => Definitions
        .All(definition => IsTemplateReady(TemplateJsonPath(projectRoot, definition.Folder)));

    public static string TemplateJsonPath(string projectRoot, string folder) =>
        Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            folder,
            Definitions.First(definition => definition.Folder.Equals(folder, StringComparison.Ordinal)).JsonFile);

    public static bool IsTemplateReady(string templateJsonPath)
    {
        if (!File.Exists(templateJsonPath))
        {
            return false;
        }

        var assetBase = Path.Combine(
            Path.GetDirectoryName(templateJsonPath)!,
            Path.GetFileNameWithoutExtension(templateJsonPath));
        // TextureCookService determines from the JSON whether this particular
        // template needs an external .ubulk. UI texture donors keep their mips
        // inline, so requiring a .ubulk here would incorrectly reject them.
        return File.Exists(assetBase + ".uasset") &&
            File.Exists(assetBase + ".uexp");
    }

    public static Result PrepareFromContentRoot(string projectRoot, string contentRoot)
    {
        var result = new Result();
        foreach (var definition in Definitions)
        {
            var sourceBase = Path.Combine(
                contentRoot,
                definition.ContentRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var required = new[] { ".uasset", ".uexp" };
            var missing = required
                .Select(extension => sourceBase + extension)
                .Where(path => !File.Exists(path))
                .ToList();
            if (missing.Count > 0)
            {
                result.Warnings.Add($"Texture template donor is missing: {definition.ContentRelativePath} ({string.Join(", ", missing.Select(Path.GetFileName))})");
                continue;
            }

            var destination = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), definition.Folder);
            Directory.CreateDirectory(destination);
            foreach (var extension in required.Append(".ubulk"))
            {
                var source = sourceBase + extension;
                if (File.Exists(source))
                {
                    // The source already ends with the extension. Appending it a
                    // second time would create unusable files such as *.uasset.uasset.
                    var target = Path.Combine(destination, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: true);

                    // Clean up the short-lived bad names left by the original
                    // implementation.  This is safe: the correctly named copy
                    // above is the only file the cook pipeline can consume.
                    var legacyDuplicateExtension = target + extension;
                    try
                    {
                        if (File.Exists(legacyDuplicateExtension))
                        {
                            File.Delete(legacyDuplicateExtension);
                        }
                    }
                    catch (IOException)
                    {
                        // The correct file has already been written. A locked,
                        // obsolete duplicate can be cleaned on the next refresh.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The correct file has already been written. A locked,
                        // obsolete duplicate can be cleaned on the next refresh.
                    }
                }
            }

            var json = new TemplateDocument(
                Path.GetFileName(definition.ContentRelativePath),
                definition.PackagePath,
                definition.Width,
                definition.Height,
                definition.PixelFormat,
                BuildMips(definition));
            File.WriteAllText(
                Path.Combine(destination, definition.JsonFile),
                JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            result.Prepared++;
            result.Logs.Add($"Texture template ready: {definition.Folder} ({definition.PixelFormat} {definition.Width}x{definition.Height})");
        }

        return result;
    }

    private static IReadOnlyList<TemplateMip> BuildMips(Definition definition)
    {
        var mips = new List<TemplateMip>(definition.MipCount);
        var width = definition.FirstMipWidth > 0 ? definition.FirstMipWidth : definition.Width;
        var height = definition.FirstMipHeight > 0 ? definition.FirstMipHeight : definition.Height;
        var offset = definition.FirstMipOffset;
        for (var i = 0; i < definition.MipCount; i++)
        {
            var size = CalculateMipSize(definition, width, height);
            mips.Add(new TemplateMip(
                width,
                height,
                new TemplateBulkData(
                    size,
                    size,
                    offset,
                    definition.InlineMips
                        ? "BULKDATA_SingleUse | BULKDATA_ForceInlinePayload"
                        : "PayloadInSeperateFile")));
            // The 512px UI donor keeps its platform-data records inline in the
            // .uexp. Each following bulk-data record begins after the previous
            // compressed payload plus a native 16-byte gap. Treating the
            // payloads as one contiguous stream overwrites those records,
            // causing FModel to report PF_Unknown and the game to crash when
            // UMG resolves the texture.
            offset = checked(offset + size + (definition.InlineMips ? InlineMipInterRecordBytes : 0));
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return mips;
    }

    private static int CalculateMipSize(Definition definition, int width, int height)
    {
        if (definition.PixelFormat.Equals("PF_DXT5", StringComparison.OrdinalIgnoreCase) ||
            definition.PixelFormat.Equals("PF_BC7", StringComparison.OrdinalIgnoreCase))
        {
            return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16);
        }

        if (definition.PixelFormat.Equals("PF_BC5", StringComparison.OrdinalIgnoreCase))
        {
            return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16);
        }

        return checked(width * height * definition.BytesPerPixel);
    }
}
