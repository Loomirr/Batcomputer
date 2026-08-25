using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

internal static class TextureCookTemplateService
{
    // Cooked Texture2D donors keep a 16-byte serialized separator between
    // inline mip payloads. Mixed world-texture recipes use actual payload-byte
    // offsets. The native inline-only UI recipe instead records each
    // FByteBulkData marker and carries an explicit +0x11 payload bias. Keep the
    // bias in the recipe while preserving the shared inter-record separators.
    //
    // UAssetGUI's full legacy package contains an editor-only user-data export
    // and property, so its first record is 0x7F. reTOC's clean one-click
    // conversion omits those 27 bytes and starts the equivalent record at
    // 0x64. Both carry the same native BC7 mip stream and are verified below.
    private const int InlineMipInterRecordBytes = 0x10;
    public const string NativeSuitIconTemplateFolder = "TextureStandaloneTemplate_SuitIconUI_BC7";
    public const string NativeMmrTemplateFolder = "TextureStandaloneTemplate_EoMMMR_DXT1";
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
        int ExternalMipCount,
        int FirstInlineMipOffset,
        int InlinePayloadOffsetBias,
        long ExpectedUassetBytes,
        long ExpectedUexpBytes,
        long ExpectedUbulkBytes,
        string ExpectedUassetSha256,
        string ExpectedUexpSha256,
        string ExpectedUbulkSha256);

    private sealed record TemplateDocument(
        string Name,
        string Package,
        int SizeX,
        int SizeY,
        string PixelFormat,
        int InlinePayloadOffsetBias,
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
            2048, 2048, "PF_B8G8R8A8", 4, 12, 5, 197, 0,
            1348, 22165, 22347776,
            "A99ECF4264E360A4BC62CAB11BB54D67ECBAF311A63E07E6DB7B87EE17A3CAB2",
            "5DE572DFF87F99228D9ED00CF76911213DBA10CAC2026756807CBB84202FBD11",
            "DCE0C6C23B7755B878E62B7DB2AEBAAB4385DDC5DD9F087990BEEBBA33F5EFDA"),
        new(
            "TextureStandaloneTemplate_BatarangBC5",
            "T_Batarang_N.json",
            "Models/Gadgets/GA_Batarang/T_Batarang_N",
            "/Game/Models/Gadgets/GA_Batarang/T_Batarang_N",
            2048, 2048, "PF_BC5", 1, 12, 5, 198, 0,
            1271, 5810, 5586944,
            "3CF020C9EC27AC4DB945B5AF45C323A518C91647399CA231FF170D9A2253F4A5",
            "695EE70B19D6B2A8BBC8735D35A9D9B964B02A8D1FC63DFCD6981416EFEDF7DA",
            "B04F7A86D3C85200F0C5EC7758479634ED6AF7E294AA19121D10784FB01E9363"),
        new(
            "TextureStandaloneTemplate_BatclawLogo_DXT5",
            "T_DECAL_BatclawLogo.json",
            "Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            "/Game/Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            2048, 2048, "PF_DXT5", 1, 12, 5, 193, 0,
            1291, 5805, 5586944,
            "9C8A1D21318793975559BAC55605789258BE07C92EDE1EB4A8686B37CE310726",
            "7A9BD7A4DFE39254CC7E16E4EE694624D77527402EE091C6258BD0BF1CB471C2",
            "072FED93C003021B5F7226ECCE066433F55D4E722BD7F22A5624F7914D7CD296"),
        // Batman '89's body MMR has the desired native PF_DXT1/sRGB=false
        // metadata, but its 2048px mip is stored in an optional .uptnl stream
        // that this narrow cooker intentionally does not synthesize. This EoM
        // MMR uses the same native surface-map metadata and keeps its complete
        // 2048px..1px chain inline in the split export, a layout the cooker can
        // replace and verify atomically without inventing optional-bulk data.
        new(
            NativeMmrTemplateFolder,
            "T_TPAGE_OswaldCobblepot_DIST_MMR.json",
            "Characters/Textures/EoM/T_TPAGE_OswaldCobblepot_DIST_MMR",
            "/Game/Characters/Textures/EoM/T_TPAGE_OswaldCobblepot_DIST_MMR",
            2048, 2048, "PF_DXT1", 1, 12, 0, 119, 0,
            1326, 2796539, 0,
            "A1B560A288D03488BD66FF087359921EE05E14EF9453A475D00EBDE0BDFC263D",
            "A6F040A5A999F6AD2529CD8629200F81AA99A1AFAB9FD32F7A12E73F0D89C665",
            ""),
        // Suit-selector tiles use the game's native compact UMG texture layout,
        // not the 2K world/decal donors above. The verified donor has a 256px
        // BC7 top mip and nine inline mips in its .uexp.
        new(
            NativeSuitIconTemplateFolder,
            "T_SuitIcon_NULL_BCA.json",
            "UI/Icons/Suits/T_SuitIcon_NULL_BCA",
            "/Game/UI/Icons/Suits/T_SuitIcon_NULL_BCA",
            256, 256, "PF_BC7", 1, 9, 0, NativeSuitIconFullFirstMipOffset, 0x11,
            0, 0, 0, "", "", ""),
    };

    public static IReadOnlyList<string> RetocFilters { get; } = Definitions
        .Select(definition => "Content/" + definition.ContentRelativePath)
        .ToArray();

    /// <summary>
    /// Returns true when the general world-texture donors are ready. The native
    /// suit-icon donor is intentionally optional so a missing UI-only asset
    /// never blocks body/material texture authoring.
    /// </summary>
    public static bool HasCoreTemplates(string projectRoot)
    {
        // Older workspaces have the correct donor packages but a five-mip JSON
        // recipe that omits the donor's inline 64px..1px tail. Refresh the
        // internal recipe in place so saved suits are repaired on their next
        // encoder-version recook without asking the user to extract again.
        NormalizeCoreTemplates(projectRoot);
        return Definitions
            .Where(definition => !IsNativeSuitIconDefinition(definition))
            .All(definition => IsCoreTemplateReady(projectRoot, definition));
    }

    public static int NormalizeCoreTemplates(string projectRoot)
    {
        var normalized = 0;
        foreach (var definition in Definitions.Where(definition => !IsNativeSuitIconDefinition(definition)))
        {
            var templateJson = TemplateJsonPath(projectRoot, definition.Folder);
            var assetBase = Path.Combine(Path.GetDirectoryName(templateJson)!, Path.GetFileNameWithoutExtension(templateJson));
            if (!IsKnownCoreLayout(definition, assetBase))
            {
                continue;
            }

            WriteCanonicalTemplateJson(definition, templateJson);
            normalized++;
        }
        return normalized;
    }

    internal static bool WriteCanonicalTemplateForRegression(string folder, string destination)
    {
        var definition = Definitions.FirstOrDefault(candidate =>
            candidate.Folder.Equals(folder, StringComparison.Ordinal));
        if (definition is null)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        WriteCanonicalTemplateJson(definition, destination);
        return true;
    }

    internal static IReadOnlyList<string> RequiredPackageExtensionsForRegression(string folder)
    {
        var definition = Definitions.Single(candidate =>
            candidate.Folder.Equals(folder, StringComparison.Ordinal));
        return RequiredPackageExtensions(definition);
    }

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

    /// <summary>
    /// Installs the exact no-optional-bulk EoM MMR donor from an already
    /// configured Content extract. This keeps legacy saved MMRs repairable from
    /// Change cook profile without silently rewriting their project recipe.
    /// </summary>
    public static bool NormalizeNativeMmrTemplate(string projectRoot, string? contentRoot)
    {
        var definition = Definitions.Single(candidate =>
            candidate.Folder.Equals(NativeMmrTemplateFolder, StringComparison.Ordinal));
        var destination = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), definition.Folder);
        var destinationBase = Path.Combine(destination, Path.GetFileName(definition.ContentRelativePath));

        if (!IsKnownCoreLayout(definition, destinationBase) && !string.IsNullOrWhiteSpace(contentRoot))
        {
            var sourceBase = Path.Combine(
                contentRoot,
                definition.ContentRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (IsKnownCoreLayout(definition, sourceBase))
            {
                Directory.CreateDirectory(destination);
                File.Copy(sourceBase + ".uasset", destinationBase + ".uasset", overwrite: true);
                File.Copy(sourceBase + ".uexp", destinationBase + ".uexp", overwrite: true);
            }
        }

        if (!IsKnownCoreLayout(definition, destinationBase))
        {
            return false;
        }

        Directory.CreateDirectory(destination);
        WriteCanonicalTemplateJson(definition, Path.Combine(destination, definition.JsonFile));
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
        if (!File.Exists(assetBase + ".uasset") || !File.Exists(assetBase + ".uexp"))
        {
            return false;
        }

        var managedFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(templateJsonPath)));
        var managedDefinition = Definitions.FirstOrDefault(definition =>
            definition.Folder.Equals(managedFolder, StringComparison.OrdinalIgnoreCase));
        if (managedDefinition is not null)
        {
            var recognizedLayout = IsNativeSuitIconDefinition(managedDefinition)
                ? DetectNativeSuitIconLayout(assetBase + ".uasset", assetBase + ".uexp") != NativeSuitIconLayout.None
                : IsKnownCoreLayout(managedDefinition, assetBase);
            if (!recognizedLayout)
            {
                return false;
            }
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(templateJsonPath));
            var root = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().First(element =>
                    element.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                : document.RootElement;
            var hasExternalMips = root.GetProperty("Mips").EnumerateArray().Any(mip =>
                mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString()?
                    .Contains("PayloadInSep", StringComparison.OrdinalIgnoreCase) == true);
            return !hasExternalMips || File.Exists(assetBase + ".ubulk");
        }
        catch
        {
            return false;
        }
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
            var required = RequiredPackageExtensions(definition);
            var missing = required
                .Select(extension => sourceBase + extension)
                .Where(path => !File.Exists(path))
                .ToList();
            if (missing.Count > 0)
            {
                result.Warnings.Add($"Texture template donor is missing: {definition.ContentRelativePath} ({string.Join(", ", missing.Select(Path.GetFileName))})");
                continue;
            }

            if (!IsNativeSuitIconDefinition(definition) && !IsKnownCoreLayout(definition, sourceBase))
            {
                result.Warnings.Add(
                    $"Texture template donor has an unrecognized cooked mip layout and was not installed: {definition.ContentRelativePath}. " +
                    "Refresh game assets with the current Batcomputer build before cooking textures.");
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
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
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

    private static IReadOnlyList<string> RequiredPackageExtensions(Definition definition) =>
        definition.ExternalMipCount > 0
            ? new[] { ".uasset", ".uexp", ".ubulk" }
            : new[] { ".uasset", ".uexp" };

    private static bool IsCoreTemplateReady(string projectRoot, Definition definition)
    {
        var templateJson = TemplateJsonPath(projectRoot, definition.Folder);
        var assetBase = Path.Combine(Path.GetDirectoryName(templateJson)!, Path.GetFileNameWithoutExtension(templateJson));
        return IsTemplateReady(templateJson) && IsKnownCoreLayout(definition, assetBase);
    }

    private static bool IsKnownCoreLayout(Definition definition, string assetBase)
    {
        if (IsNativeSuitIconDefinition(definition))
        {
            return false;
        }

        var uasset = assetBase + ".uasset";
        var uexp = assetBase + ".uexp";
        var ubulk = assetBase + ".ubulk";
        var requiresUbulk = definition.ExternalMipCount > 0;
        return File.Exists(uasset) && File.Exists(uexp) && (!requiresUbulk || File.Exists(ubulk)) &&
               new FileInfo(uasset).Length == definition.ExpectedUassetBytes &&
               new FileInfo(uexp).Length == definition.ExpectedUexpBytes &&
               (!requiresUbulk || new FileInfo(ubulk).Length == definition.ExpectedUbulkBytes) &&
               HasSplitExportFooter(uexp) &&
               FileSha256Equals(uasset, definition.ExpectedUassetSha256) &&
               FileSha256Equals(uexp, definition.ExpectedUexpSha256) &&
               (!requiresUbulk || FileSha256Equals(ubulk, definition.ExpectedUbulkSha256));
    }

    private static bool FileSha256Equals(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSplitExportFooter(string uexpPath)
    {
        using var stream = File.OpenRead(uexpPath);
        if (stream.Length < 4)
        {
            return false;
        }
        stream.Seek(-4, SeekOrigin.End);
        return stream.ReadByte() == 0xC1 &&
               stream.ReadByte() == 0x83 &&
               stream.ReadByte() == 0x2A &&
               stream.ReadByte() == 0x9E;
    }

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
        if (!HasSplitExportFooter(uexpPath))
        {
            return NativeSuitIconLayout.None;
        }
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
            FirstInlineMipOffset = layout == NativeSuitIconLayout.RetocLegacy
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
            definition.InlinePayloadOffsetBias,
            BuildMips(definition));
        var serialized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(destination) && File.ReadAllText(destination).Equals(serialized, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(destination, serialized);
    }

    private static IReadOnlyList<TemplateMip> BuildMips(Definition definition)
    {
        var mips = new List<TemplateMip>(definition.MipCount);
        var width = definition.Width;
        var height = definition.Height;
        var externalOffset = 0;
        var inlineOffset = definition.FirstInlineMipOffset;
        for (var i = 0; i < definition.MipCount; i++)
        {
            var size = CalculateMipSize(definition, width, height);
            var isInline = i >= definition.ExternalMipCount;
            mips.Add(new TemplateMip(
                width,
                height,
                new TemplateBulkData(
                    size,
                    size,
                    isInline ? inlineOffset : externalOffset,
                    isInline
                        ? "BULKDATA_SingleUse | BULKDATA_ForceInlinePayload"
                        : "PayloadInSeperateFile")));
            // Inline platform-data records live in .uexp and each following
            // record begins after the prior payload plus a native 16-byte gap.
            // External .ubulk payloads remain contiguous from offset zero.
            if (isInline)
            {
                inlineOffset = checked(inlineOffset + size + InlineMipInterRecordBytes);
            }
            else
            {
                externalOffset = checked(externalOffset + size);
            }
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return mips;
    }

    private static int CalculateMipSize(Definition definition, int width, int height)
    {
        if (definition.PixelFormat.Equals("PF_DXT1", StringComparison.OrdinalIgnoreCase))
        {
            return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8);
        }

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
