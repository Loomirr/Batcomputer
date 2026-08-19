using System.Text.Json;

namespace Batcomputer;

internal static class TextureCookTemplateService
{
    // The native UI donor keeps a 16-byte serialized separator between its
    // inline BC7 mip payloads. Its OffsetInFile values point at each
    // FByteBulkData record; the BC7 bytes begin 0x11 bytes later. The cooker
    // applies that shared prefix while preserving the inter-record separators.
    //
    // UAssetGUI's full legacy package contains an editor-only user-data export
    // and property, so its first record is 0x7F. reTOC's clean one-click
    // conversion omits those 27 bytes and starts the equivalent record at
    // 0x64. Both carry the same native BC7 mip stream and are verified below.
    private const int InlineMipInterRecordBytes = 0x10;
    public const string NativeSuitIconTemplateFolder = "TextureStandaloneTemplate_SuitIconUI_BC7";
    private const string NativeSuitIconAssetName = "T_SuitIcon_NULL_BCA";
    private const int NativeSuitIconFullUassetBytes = 1616;
    private const int NativeSuitIconFullUexpBytes = 87708;
    private const int NativeSuitIconRetocUassetBytes = 1133;
    private const int NativeSuitIconRetocUexpBytes = 87681;
    private const int NativeSuitIconFullFirstMipOffset = 0x7F;
    private const int NativeSuitIconRetocFirstMipOffset = 0x64;

    private enum NativeSuitIconLayout
    {
        None,
        FullLegacy,
        RetocLegacy,
    }

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
        // Suit-selector tiles use the game's native compact UMG texture layout,
        // not the 2K world/decal donors above. The verified donor has a 256px
        // BC7 top mip and nine inline mips in its .uexp.
        new(
            NativeSuitIconTemplateFolder,
            "T_SuitIcon_NULL_BCA.json",
            "UI/Icons/Suits/T_SuitIcon_NULL_BCA",
            "/Game/UI/Icons/Suits/T_SuitIcon_NULL_BCA",
            256, 256, "PF_BC7", 1, 9,
            FirstMipWidth: 256,
            FirstMipHeight: 256,
            FirstMipOffset: NativeSuitIconFullFirstMipOffset,
            InlineMips: true),
    };

    public static IReadOnlyList<string> RetocFilters { get; } = Definitions
        .Select(definition => "Content/" + definition.ContentRelativePath)
        .ToArray();

    /// <summary>
    /// Returns true when the general world-texture donors are ready. The native
    /// suit-icon donor is intentionally optional so a missing UI-only asset
    /// never blocks body/material texture authoring.
    /// </summary>
    public static bool HasCoreTemplates(string projectRoot) => Definitions
        .Where(definition => !IsNativeSuitIconDefinition(definition))
        .All(definition => IsTemplateReady(TemplateJsonPath(projectRoot, definition.Folder)));

    public static bool HasNativeSuitIconTemplate(string projectRoot)
    {
        var definition = NativeSuitIconDefinition();
        var templateJson = TemplateJsonPath(projectRoot, definition.Folder);
        var assetBase = Path.Combine(Path.GetDirectoryName(templateJson)!, NativeSuitIconAssetName);
        return IsTemplateReady(templateJson) &&
            DetectNativeSuitIconLayout(assetBase + ".uasset", assetBase + ".uexp") != NativeSuitIconLayout.None;
    }

    /// <summary>
    /// Restores the canonical 256px BC7 recipe when a verified native donor is
    /// available. This also upgrades workspaces left by the retired Red Brick
    /// authoring prototype without reintroducing Red Brick tooling.
    /// </summary>
    public static bool NormalizeNativeSuitIconTemplate(string projectRoot)
    {
        var definition = NativeSuitIconDefinition();
        var generatedRoot = AppSettings.GeneratedRootFor(projectRoot);
        var destination = Path.Combine(generatedRoot, definition.Folder);
        var destinationBase = Path.Combine(destination, NativeSuitIconAssetName);

        if (DetectNativeSuitIconLayout(destinationBase + ".uasset", destinationBase + ".uexp") == NativeSuitIconLayout.None)
        {
            var candidates = new List<string>
            {
                Path.Combine(generatedRoot, "TextureStandaloneTemplate_RedBrickIconUI_DXT5", NativeSuitIconAssetName),
                Path.Combine(AppSettings.Current.EffectiveExtractedContentRoot(), "UI", "Icons", "Suits", NativeSuitIconAssetName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UAssetGUI", "Extracted", "LEGOBatmanLotDK", "Content", "UI", "Icons", "Suits", NativeSuitIconAssetName),
            };

            var sourceBase = candidates.FirstOrDefault(candidate =>
                DetectNativeSuitIconLayout(candidate + ".uasset", candidate + ".uexp") != NativeSuitIconLayout.None);
            if (!string.IsNullOrWhiteSpace(sourceBase))
            {
                Directory.CreateDirectory(destination);
                File.Copy(sourceBase + ".uasset", destinationBase + ".uasset", overwrite: true);
                File.Copy(sourceBase + ".uexp", destinationBase + ".uexp", overwrite: true);
            }
        }

        var layout = DetectNativeSuitIconLayout(destinationBase + ".uasset", destinationBase + ".uexp");
        if (layout == NativeSuitIconLayout.None)
        {
            return false;
        }

        Directory.CreateDirectory(destination);
        WriteCanonicalTemplateJson(DefinitionForNativeLayout(definition, layout), Path.Combine(destination, definition.JsonFile));
        return true;
    }

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
            if (IsNativeSuitIconDefinition(definition) && NormalizeNativeSuitIconTemplate(projectRoot))
            {
                result.Prepared++;
                result.Logs.Add($"Texture template ready: {definition.Folder} (native UI, {definition.PixelFormat} {definition.Width}x{definition.Height})");
                continue;
            }

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

            var nativeLayout = IsNativeSuitIconDefinition(definition)
                ? DetectNativeSuitIconLayout(sourceBase + ".uasset", sourceBase + ".uexp")
                : NativeSuitIconLayout.None;
            if (IsNativeSuitIconDefinition(definition) && nativeLayout == NativeSuitIconLayout.None)
            {
                result.Warnings.Add(
                    "Native suit-icon donor did not match either verified 256px BC7 layout and was not installed. " +
                    "Refresh game assets after updating Batcomputer or select another icon cook profile.");
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

            var recipeDefinition = IsNativeSuitIconDefinition(definition)
                ? DefinitionForNativeLayout(definition, nativeLayout)
                : definition;
            WriteCanonicalTemplateJson(recipeDefinition, Path.Combine(destination, definition.JsonFile));
            result.Prepared++;
            var layoutDetail = IsNativeSuitIconDefinition(definition)
                ? $", {nativeLayout}"
                : "";
            result.Logs.Add($"Texture template ready: {definition.Folder} ({definition.PixelFormat} {definition.Width}x{definition.Height}{layoutDetail})");
        }

        return result;
    }

    private static Definition NativeSuitIconDefinition() => Definitions.Single(IsNativeSuitIconDefinition);

    private static bool IsNativeSuitIconDefinition(Definition definition) =>
        definition.Folder.Equals(NativeSuitIconTemplateFolder, StringComparison.Ordinal);

    private static NativeSuitIconLayout DetectNativeSuitIconLayout(string uassetPath, string uexpPath)
    {
        if (!File.Exists(uassetPath) || !File.Exists(uexpPath))
        {
            return NativeSuitIconLayout.None;
        }

        var uassetBytes = new FileInfo(uassetPath).Length;
        var uexpBytes = new FileInfo(uexpPath).Length;
        if (uassetBytes == NativeSuitIconFullUassetBytes && uexpBytes == NativeSuitIconFullUexpBytes)
        {
            return NativeSuitIconLayout.FullLegacy;
        }

        if (uassetBytes == NativeSuitIconRetocUassetBytes && uexpBytes == NativeSuitIconRetocUexpBytes)
        {
            return NativeSuitIconLayout.RetocLegacy;
        }

        return NativeSuitIconLayout.None;
    }

    private static Definition DefinitionForNativeLayout(Definition definition, NativeSuitIconLayout layout) =>
        definition with
        {
            FirstMipOffset = layout == NativeSuitIconLayout.RetocLegacy
                ? NativeSuitIconRetocFirstMipOffset
                : NativeSuitIconFullFirstMipOffset,
        };

    private static void WriteCanonicalTemplateJson(Definition definition, string destination)
    {
        var json = new TemplateDocument(
            Path.GetFileName(definition.ContentRelativePath),
            definition.PackagePath,
            definition.Width,
            definition.Height,
            definition.PixelFormat,
            BuildMips(definition));
        File.WriteAllText(destination, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
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
            // The native UI donor keeps its platform-data records inline in the
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
