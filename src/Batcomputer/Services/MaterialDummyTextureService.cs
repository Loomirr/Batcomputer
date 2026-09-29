namespace Batcomputer;

/// <summary>Conservative, map-typed choices from shipped EoM/LEGOface defaults, never a null reference.</summary>
internal static class MaterialDummyTextureService
{
    private const string Eom = "/Game/Characters/Textures/Shared/EoM/";
    internal sealed record Choice(string ObjectPath, string Explanation);
    internal static bool IsFlatNormal(string path)
    {
        var package = UnrealPathUtil.NormalizePackagePath(path);
        return package.Equals(Eom + "T_Dummy_Norm", StringComparison.OrdinalIgnoreCase) ||
            package.Equals("/Game/Characters/Textures/Shared/T_Dummy_NML", StringComparison.OrdinalIgnoreCase);
    }
    internal static Choice? Resolve(string parameter, MaterialGenService.MaterialTemplateInfo? material)
    {
        var key = new string(parameter.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var parent = material?.ParentMaterialPath ?? "";
        var face = parent.Contains("LEGOface", StringComparison.OrdinalIgnoreCase);
        var eom = !face && (parent.Contains("EoM", StringComparison.OrdinalIgnoreCase) ||
            (material?.TextureParams.Any(t => t.Name.Equals("T_Dummy_CTUV", StringComparison.OrdinalIgnoreCase) &&
                t.ObjectPath.StartsWith(Eom, StringComparison.OrdinalIgnoreCase)) ?? false));
        Choice Map(string name, string explanation) => new(Eom + name + "." + name, explanation);
        // A named dummy binding is already a typed choice from this material, so preserve it exactly.
        var inherited = material?.TextureParams.FirstOrDefault(t => t.Name.Equals(parameter, StringComparison.OrdinalIgnoreCase));
        if (inherited?.ObjectPath.StartsWith("/Game/Characters/Textures/Shared/", StringComparison.OrdinalIgnoreCase) == true &&
            UnrealPathUtil.AssetName(inherited.ObjectPath).StartsWith("T_Dummy_", StringComparison.OrdinalIgnoreCase))
            return new(inherited.ObjectPath, "Reuses this parameter's shipped placeholder; switches and other layers are unchanged.");
        var normal = key.Contains("normal") || key.EndsWith("nrm") || key.EndsWith("nml");
        if (face)
        {
            if (normal) return new("/Game/Characters/Textures/Shared/T_Dummy_NML.T_Dummy_NML", "Flat LEGOface normal input; face visibility is unchanged.");
            if (key.EndsWith("mmr")) return Map("T_Dummy_MMR", "The native LEGOface packed surface placeholder.");
            // Face atlases have shared channels/regions. A blank lash atlas can also remove eyes.
            return null;
        }
        if (!eom) return null; // Do not guess ORM, legacy cape, UI or arbitrary custom shader packing.
        return key switch
        {
            "ct" or "ctuv" => Map("T_Dummy_CTUV", "Native constant CT control data; removes the donor's geometry-specific CT map, not a visibility toggle."),
            "rao" => Map("T_Dummy_RAO", "Constant structural roughness and unoccluded AO; removes donor-shaped roughness/AO detail."),
            "mmr" or "mmrpristine" => Map("T_Dummy_MMR", "Native nonmetal packed surface default with constant roughness; not an all-zero map."),
            "colourmask" or "colormask" or "swapcolourid" => Map("T_Dummy_Black_BC", "Blank colour-ID input, matching the custom-accessory templates; not a transparency mask."),
            "bc" or "bcpristine" or "basecolour" or "basecolor" => Map("T_Dummy_White_BC", "White base-colour input; removes texture artwork but keeps the shader's tinting and other layers."),
            "e" or "pmemissive" or "emissive" => Map("T_Dummy_E", "Black emissive input; other emissive switches/scalars are unchanged."),
            "micronoise" or "macronoise" => Map("T_Dummy_Norm", "Flat normal/noise input; other normal layers remain active."),
            _ when normal => Map("T_Dummy_Norm", "Flat structural/decal normal input; micro detail and other normal layers remain active."),
            _ => null,
        };
    }
}
