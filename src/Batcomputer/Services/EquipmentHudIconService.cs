using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Uses the cooked native HUD shader and preserves its existing import/preload layout.</summary>
internal static class EquipmentHudIconService
{
    internal const string Donor = "/Game/UI/Icons/Gadgets/MI_UI_IconGadgetBatarang";
    internal const string Parent = "/Game/UI/Global/M_UI_SDFIconGadget";
    internal const string DonorTexture = "/Game/UI/Icons/Gadgets/T_UI_IconBatarang_SDF";
    internal const string ArtworkHelp = "256 × 256 transparent PNG · about 32 px padding. White silhouette, optional green accents (#00FF00) at least 12–16 px wide. No painted outline needed. SDF output: 64 × 64.";
    internal static string MaterialPackage(string equipmentRoot) => equipmentRoot + "/UI/MI_EquipmentIcon_HUD";
    internal static bool IsMaterialBinding(EquipmentAssetPart part) => part.PropertyPath.Split('.').Last() == "HudIconMtl" && part.AssetClass == "MaterialInstanceConstant";
    internal static bool HasBindings(EquipmentAssetProfile? profile) => profile?.Parts.Any(IsMaterialBinding) == true;
    internal static bool Manages(EquipmentAssetProfile profile, EquipmentAssetPart part) => IsMaterialBinding(part) ||
        (part.AssetClass == "Texture2D" && (part.PropertyPath.Split('.').Last() == "HudIcon" ||
        (part.PropertyPath.StartsWith("TextureParameterValues", StringComparison.Ordinal) &&
         profile.Parts.Any(p => IsMaterialBinding(p) && p.Package.Equals(part.OwnerPackage, StringComparison.OrdinalIgnoreCase)))));

    internal static IEnumerable<EquipmentPartEdit> ActiveParts(CustomEquipmentRecipe recipe, EquipmentAssetProfile profile) =>
        recipe.Parts.Where(edit => recipe.HudIcon is null ||
            !profile.Parts.Any(part => part.Key == edit.Key && Manages(profile, part)));

    internal static IEnumerable<EquipmentPartEdit> ActiveCopyParts(CustomEquipmentRecipe recipe)
    {
        if (recipe.HudIcon is null || recipe.Parts.Count == 0) return recipe.Parts;
        var profile = EquipmentAssetService.Inspect(AppSettings.Current.EffectiveExtractedContentRoot(),
            AppSettings.Current.EffectiveUsmapPath() ?? throw new InvalidDataException("Mappings are required to copy equipment."), recipe.DonorEtaPackage);
        return ActiveParts(recipe, profile);
    }

    internal static void Validate(EquipmentHudIconRecipe recipe)
    {
        if (!HeldItemService.ValidPackage(recipe.SdfPackage))
            throw new InvalidDataException("HUD icon material needs a cooked SDF /Game/… package. Import the PNG in Textures, then choose its SDF cook in Equipment → HUD icon material.");
        if (recipe.SdfPackage.EndsWith("_BCA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HUD icon material expects SDF, not BCA. Choose the SDF cook of this icon.");
        if (!float.IsFinite(recipe.GlowPower) || recipe.GlowPower < 0 || recipe.GlowPower > 4)
            throw new InvalidDataException("HUD icon glow strength must be between 0 and 4 (native default 0.5).");
        Range(recipe.FillOpacity, 0, 1, "Fill opacity"); Range(recipe.OutlineOpacity, 0, 1, "Outline opacity");
        Range(recipe.OutlineWidth, 0, 1, "Outline width"); Range(recipe.Grain, 0, 1, "Texture grain");
        Range(recipe.Sharpness, 1, 32, "Edge sharpness");
        _ = ReadColor(recipe.FillColor); _ = ReadColor(recipe.OutlineColor);
    }

    private static void Range(float value, float min, float max, string label)
    {
        if (!float.IsFinite(value) || value < min || value > max) throw new InvalidDataException($"{label} must be between {min} and {max}.");
    }
    internal static Color ReadColor(string hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            throw new InvalidDataException("Icon colours must use #RRGGBB, for example #FFFFFF.");
        return Color.FromArgb(255, rgb >> 16, (rgb >> 8) & 255, rgb & 255);
    }
    private static FLinearColor LinearColor(string hex, float opacity)
    {
        var color = ReadColor(hex);
        static float Linear(byte value) { var v = value / 255f; return v <= .04045f ? v / 12.92f : MathF.Pow((v + .055f) / 1.055f, 2.4f); }
        return new FLinearColor(Linear(color.R), Linear(color.G), Linear(color.B), opacity);
    }
    private static IEnumerable<(string Name, float Value)> Scalars(EquipmentHudIconRecipe recipe)
    {
        if (recipe.GlowPower != .5f) yield return ("GlowPower", recipe.GlowPower);
        if (recipe.OutlineWidth != .2f) yield return ("BorderThickness", recipe.OutlineWidth);
        if (recipe.Grain != .5f) yield return ("NoiseAmount", recipe.Grain);
        if (recipe.Sharpness != 8f) yield return ("Sharpness", recipe.Sharpness);
    }
    private static IEnumerable<(string Name, FLinearColor Value)> Vectors(EquipmentHudIconRecipe recipe)
    {
        if (!recipe.FillColor.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) || recipe.FillOpacity != 1f)
            yield return ("Colour", LinearColor(recipe.FillColor, recipe.FillOpacity));
        if (!recipe.OutlineColor.Equals("#000000", StringComparison.OrdinalIgnoreCase) || recipe.OutlineOpacity != .8f)
            yield return ("Border", LinearColor(recipe.OutlineColor, recipe.OutlineOpacity));
    }

    internal static void Generate(EquipmentHudIconRecipe recipe, string equipmentRoot, string extracted, string contentRoot,
        Usmap mappings, EquipmentAssetProfile profile, IReadOnlyDictionary<string, UAsset> assets, Action<string> log)
    {
        Validate(recipe);
        if (!HasBindings(profile)) throw new InvalidDataException("This equipment no longer exposes a supported HUD material. Reopen its workshop and reset HUD icon material.");
        var staged = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, recipe.SdfPackage);
        if (recipe.SdfPackage.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) && (staged is null || !File.Exists(staged)))
            new ToolMaterialLibraryService(AppSettings.Current.EffectiveProjectRoot()).CopyMaterialClosureToContentRoot(recipe.SdfPackage, contentRoot);
        staged = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, recipe.SdfPackage);
        var texture = EquipmentAssetService.Read(staged is not null && File.Exists(staged) ? contentRoot : extracted, recipe.SdfPackage, mappings);
        if (!texture.Exports.Any(e => e.GetExportClassType()?.ToString() == "Texture2D"))
            throw new InvalidDataException("HUD icon material's SDF input must be a cooked Texture2D.");
        WriteMaterial(recipe, equipmentRoot, extracted, contentRoot, mappings);
        var material = MaterialPackage(equipmentRoot);
        var bindings = 0;
        foreach (var (source, asset) in assets)
        foreach (var export in asset.Exports.OfType<NormalExport>())
        foreach (var (_, property) in EquipmentAssetService.Properties(export.Data))
        {
            if (property is not SoftObjectPropertyData) continue;
            if (property.Name.ToString() == "HudIconMtl")
            {
                CustomEquipmentService.ReplaceReference(asset, export, property, material, "MaterialInstanceConstant");
                bindings++;
            }
            else if (property.Name.ToString() == "HudIcon")
                CustomEquipmentService.ReplaceReference(asset, export, property, recipe.SdfPackage, "Texture2D");
        }
        if (bindings != profile.Parts.Count(IsMaterialBinding))
            throw new InvalidDataException("HUD material binding count changed; packaging stopped before producing a partial icon setup.");
        log($"HUD icon material: {bindings} equipment/mode bindings → {material}; SDF {recipe.SdfPackage}. Gameplay upgrades unchanged; HUD mode artwork is shared.");
    }

    internal static void WriteMaterial(EquipmentHudIconRecipe recipe, string equipmentRoot, string extracted, string contentRoot, Usmap mappings)
    {
        Validate(recipe);
        var parsed = EquipmentAssetService.Read(extracted, Donor, mappings);
        var donorExport = parsed.Exports.OfType<NormalExport>().Single();
        var parent = EquipmentAssetService.Reference(parsed, donorExport.Data.Single(p => p.Name.ToString() == "Parent"));
        if (parent?.Package != Parent || !parsed.Imports.Any(i => i.ObjectName.ToString() == DonorTexture))
            throw new InvalidDataException("Native equipment HUD material template changed. Refresh game files before building.");
        var package = MaterialPackage(equipmentRoot);
        if (!package.StartsWith("/Game/Mods/", StringComparison.Ordinal) || !HeldItemService.ValidPackage(package))
            throw new InvalidDataException("HUD material output must stay inside a custom equipment /Game/Mods namespace.");
        // Keep the cooked shader, texture import index and all preload dependencies intact.
        // The non-numeric object suffix also avoids ambiguous numbered FName identities.
        var asset = EquipmentAssetService.ReadRaw(extracted, Donor, mappings, CustomSerializationFlags.SkipParsingExports);
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal) {
            [Donor] = package, [UnrealPathUtil.AssetName(Donor)] = UnrealPathUtil.AssetName(package),
            [DonorTexture] = recipe.SdfPackage, [UnrealPathUtil.AssetName(DonorTexture)] = UnrealPathUtil.AssetName(recipe.SdfPackage)
        };
        var names = asset.GetNameMapIndexList();
        for (var i = 0; i < names.Count; i++)
            if (redirects.TryGetValue(names[i].ToString(), out var target)) asset.SetNameReference(i, new FString(target));
        asset.FolderName = new FString(package);
        var file = Path.Combine(contentRoot, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        asset.Write(file);
        var check = EquipmentAssetService.Read(contentRoot, package, mappings);
        if (Scalars(recipe).Any() || Vectors(recipe).Any())
        {
            ApplyAppearance(check, recipe);
            check.Write(file);
            check = EquipmentAssetService.Read(contentRoot, package, mappings);
        }
        var iconRefs = check.Exports.OfType<NormalExport>().SelectMany(e => EquipmentAssetService.Properties(e.Data))
            .Where(p => p.Path.StartsWith("TextureParameterValues", StringComparison.Ordinal))
            .Select(p => EquipmentAssetService.Reference(check, p.Property)).Where(r => r?.Class == "Texture2D").ToArray();
        if (iconRefs.Length != 1 || iconRefs[0]?.Package != recipe.SdfPackage || Glow(check).Value != recipe.GlowPower ||
            check.Exports.Single().ObjectName.ToString() != UnrealPathUtil.AssetName(package))
            throw new InvalidDataException("Equipment HUD material failed its cooked roundtrip check.");
        foreach (var (name, value) in Scalars(recipe))
            if (FindParameter(check, "ScalarParameterValues", name)?.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "ParameterValue").Value != value)
                throw new InvalidDataException("HUD scalar failed cooked roundtrip: " + name);
        foreach (var (name, value) in Vectors(recipe))
        {
            var entry = FindParameter(check, "VectorParameterValues", name);
            var actual = entry?.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ParameterValue").Value.OfType<LinearColorPropertyData>().Single().Value;
            if (actual is not { } color || color.R != value.R || color.G != value.G || color.B != value.B || color.A != value.A)
                throw new InvalidDataException("HUD colour failed cooked roundtrip: " + name);
        }
    }

    private static StructPropertyData? FindParameter(UAsset asset, string array, string name) => asset.Exports.OfType<NormalExport>().Single().Data
        .OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == array)?.Value.OfType<StructPropertyData>()
        .SingleOrDefault(p => EquipmentAssetService.Properties(p.Value).Any(v => v.Property is NamePropertyData n && n.Value.ToString() == name));

    private static void ApplyAppearance(UAsset asset, EquipmentHudIconRecipe recipe)
    {
        var export = asset.Exports.OfType<NormalExport>().Single();
        var prototype = FindParameter(asset, "ScalarParameterValues", "GlowPower")!;
        StructPropertyData Parameter(string arrayName, string name, bool vector)
        {
            if (FindParameter(asset, arrayName, name) is { } existing) return existing;
            var array = export.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == arrayName);
            if (array is null)
            {
                array = new ArrayPropertyData(new FName(asset, arrayName)) { ArrayType = new FName(asset, "StructProperty"), Value = [] };
                export.Data.Add(array);
            }
            var entry = (StructPropertyData)PartGraftService.DeepClonePropertiesRebased([prototype], asset).Single();
            entry.Name = new FName(asset, array.Value.Length.ToString());
            entry.StructType = new FName(asset, vector ? "VectorParameterValue" : "ScalarParameterValue");
            var info = entry.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ParameterInfo");
            info.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "Name").Value = new FName(asset, name);
            // This is a new runtime override, not the GlowPower editor expression it was based on.
            var guid = entry.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ExpressionGUID");
            guid.Value.OfType<GuidPropertyData>().Single().Value = Guid.Empty;
            if (vector)
            {
                var index = entry.Value.FindIndex(p => p.Name.ToString() == "ParameterValue");
                entry.Value[index] = new StructPropertyData(new FName(asset, "ParameterValue")) {
                    StructType = new FName(asset, "LinearColor"),
                    Value = [new LinearColorPropertyData(new FName(asset, "ParameterValue")) { Value = new FLinearColor(1, 1, 1, 1) }]
                };
            }
            array.Value = array.Value.Append(entry).ToArray();
            return entry;
        }
        foreach (var (name, value) in Scalars(recipe))
            Parameter("ScalarParameterValues", name, false).Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "ParameterValue").Value = value;
        foreach (var (name, value) in Vectors(recipe))
            Parameter("VectorParameterValues", name, true).Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ParameterValue")
                .Value.OfType<LinearColorPropertyData>().Single().Value = value;
    }

    private static FloatPropertyData Glow(UAsset asset)
    {
        var parameters = asset.Exports.OfType<NormalExport>().Single().Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ScalarParameterValues");
        var glow = parameters.Value.OfType<StructPropertyData>().Single(p => EquipmentAssetService.Properties(p.Value)
            .Any(v => v.Property is NamePropertyData name && name.Value.ToString() == "GlowPower"));
        return glow.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "ParameterValue");
    }
}
