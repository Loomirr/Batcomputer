using System.Drawing.Imaging;

namespace Batcomputer;

/// <summary>Character-owned roster emblem; independent of suit portraits and equipment HUD icons.</summary>
internal static class CharacterSymbolService
{
    internal const string MaterialDonor = "/Game/UI/Icons/Characters/Emblems/MI_UI_EmblemBatman";
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const string NativeProfile = "ui-character-emblem-128x64-g8";
    internal const string LegacyProfile = "ui-character-emblem-legacy-64-bgra8";

    internal static string ReadPng(string path, string profile = "")
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Use a PNG smaller than 4 MB.");
        var encoded = Convert.ToBase64String(File.ReadAllBytes(path));
        Validate(encoded, profile);
        return encoded;
    }

    internal static byte[] Validate(string encoded, string profile = "")
    {
        if (encoded.Length > (MaxBytes + 2) / 3 * 4) throw new InvalidDataException("Symbol PNG exceeds 4 MB.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Choose a nonempty PNG smaller than 4 MB.");
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);
        if (profile == NativeProfile)
        {
            if (source.RawFormat.Guid != ImageFormat.Png.Guid || source.Width < 64 || source.Height < 64 || source.Width > 2048 || source.Height > 2048)
                throw new InvalidDataException("Use a transparent PNG, 64–2048px per side. Recommended: 512×256 with 10–15% clear margins. Square artwork is fitted without stretching.");
            _ = CharacterSymbolSdfService.Generate(source);
            return bytes;
        }
        if (profile.Length > 0 && profile != LegacyProfile) throw new InvalidDataException("Unsupported character-symbol cook profile.");
        if (source.RawFormat.Guid != ImageFormat.Png.Guid || source.Width != source.Height || source.Width < 64 || source.Width > 2048)
            throw new InvalidDataException("Use a square PNG from 64 to 2048 pixels with a transparent background. The silhouette is cooked to a 64px SDF.");
        using var small = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small)) graphics.DrawImage(source, 0, 0, 64, 64);
        var alpha = new byte[64 * 64];
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) alpha[y * 64 + x] = small.GetPixel(x, y).A;
        // Same encoder constraints as the actual cook: nonempty silhouette and clear margins.
        _ = TextureCookService.EquipmentSdfAlphaForRegression(alpha);
        return bytes;
    }

    internal static string MaterialPackage(string mod, string character) => Root(mod, character) + "/MI_CharacterSymbol";
    internal static string TexturePackage(string mod, string character) => Root(mod, character) + "/T_CharacterSymbol_SDF";
    private static string Root(string mod, string character)
    {
        if (!UnrealPathUtil.IsValidIdentifier(mod) || !CustomCharacterProjectService.IsIdentifier(character))
            throw new InvalidDataException("Invalid character symbol owner.");
        return $"/Game/Mods/{mod}/CharacterSymbols/{character}";
    }

    internal static string Stage(string content, string nativeContent, string mod, CustomCharacterIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.SymbolPngBase64)) return identity.SymbolPackage;
        if (!identity.IsDefinition) throw new InvalidDataException("Symbols belong to the character definition, not individual suits.");
        var bytes = Validate(identity.SymbolPngBase64, identity.SymbolCookProfile);
        if (identity.SymbolBorderThickness is { } border && (!float.IsFinite(border) || border < 0 || border > 1))
            throw new InvalidDataException("Symbol outline thickness must be between 0 and 1.");
        var donor = Path.Combine(nativeContent, MaterialDonor[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var projectRoot = AppSettings.Current.ProjectRoot ?? throw new InvalidDataException("Set the Batcomputer project folder before importing symbols.");
        var materialService = new MaterialGenService(projectRoot);
        var info = materialService.ReadTemplate(donor);
        if (info.Status != "ok" || info.TextureParams.Count(t => t.Name == "Icon") != 1 ||
            UnrealPathUtil.NormalizePackagePath(info.ParentMaterialPath) != "/Game/UI/Global/M_UI_SDFIcon")
            throw new InvalidDataException("The native character emblem material is missing or changed. Run Full character extraction, then rebuild. " + info.Error);
        var template = TextureCookTemplateService.TemplateJsonPath(projectRoot, identity.SymbolCookProfile == NativeProfile
            ? TextureCookTemplateService.CharacterSymbolTemplateFolder : TextureCookTemplateService.EquipmentAlphaTemplateFolder);
        if (!TextureCookTemplateService.IsTemplateReady(template)) TextureCookTemplateService.PrepareFromContentRoot(projectRoot, nativeContent);
        if (!TextureCookTemplateService.IsTemplateReady(template)) throw new InvalidDataException("Missing SDF icon cook template. Run Full character extraction.");
        var material = MaterialPackage(mod, identity.CharacterId);
        var texture = TexturePackage(mod, identity.CharacterId);
        foreach (var package in new[] { material, texture })
            if (File.Exists(Path.Combine(content, package[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset")))
                throw new InvalidDataException("Character symbol output collides with another asset: " + package);
        // Source is temporary, outside staged Content, and removed even when the cook fails.
        var source = Path.Combine(Path.GetTempPath(), "BatcomputerSymbol-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(source, bytes);
            var result = new TextureCookService(projectRoot).Cook(new() { SourceImagePath = source, TemplateJsonPath = template,
                OutputContentRoot = content, OutputPackagePath = texture, WriteInlineMips = true });
            if (result.Status != "created") throw new InvalidDataException("Symbol texture cook failed: " + result.Error);
            var generated = materialService.Generate(new() { BaseUassetPath = donor, OutputContentRoot = content,
                OutputPackagePath = material, ParamToTexture = new(StringComparer.OrdinalIgnoreCase) { ["Icon"] = texture },
                ParamToScalar = identity.SymbolBorderThickness is { } thickness
                    ? new(StringComparer.OrdinalIgnoreCase) { ["BorderThickness"] = thickness } : new(StringComparer.OrdinalIgnoreCase) });
            if (generated.Status != "created") throw new InvalidDataException("Symbol material generation failed: " + generated.Error);
            var check = materialService.ReadTemplate(generated.OutputUasset);
            if (check.TextureParams.Count(t => t.Name == "Icon" && UnrealPathUtil.NormalizePackagePath(t.ObjectPath) == texture) != 1)
                throw new InvalidDataException("Symbol material failed its written texture check.");
            if (identity.SymbolBorderThickness is { } expected && !check.ScalarParams.Any(p => p.Name == "BorderThickness" && Math.Abs(p.Value - expected) < .0001f))
                throw new InvalidDataException("Symbol outline failed its written scalar check.");
            return material;
        }
        finally { if (File.Exists(source)) File.Delete(source); }
    }
}
