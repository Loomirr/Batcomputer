namespace Batcomputer;

/// <summary>Describes observed asset roles, without guessing an unknown widget's screen location.</summary>
internal static class EquipmentIconPresentation
{
    internal static bool IsSdf(EquipmentAssetPart part) => part.AssetClass == "Texture2D" &&
        UnrealPathUtil.AssetName(part.Package).EndsWith("_SDF", StringComparison.OrdinalIgnoreCase);
    internal static bool IsBca(EquipmentAssetPart part) => part.AssetClass == "Texture2D" &&
        UnrealPathUtil.AssetName(part.Package).EndsWith("_BCA", StringComparison.OrdinalIgnoreCase);
    internal static string? SuggestedFormat(EquipmentAssetPart? part) => part is null ? null : IsSdf(part) ? "SDF" : IsBca(part) ? "BCA" : null;
    internal static string? Title(EquipmentAssetPart part)
    {
        if (part.AssetClass == "NiagaraSystem" && part.PropertyPath == "ProjectileSuccessfulVFX") return "After-hit / leftover visual effect (view only)";
        if (part.AssetClass != "Texture2D") return null;
        if (part.PropertyPath.Contains("Reticle", StringComparison.OrdinalIgnoreCase)) return "Aiming reticle — texture";
        var name = UnrealPathUtil.AssetName(part.Package);
        var upgrade = part.Package.Contains("/GadgetUpgrades/", StringComparison.OrdinalIgnoreCase);
        var role = upgrade ? "Upgrade icon" : "Equipment icon";
        var artwork = name.Replace("T_UI_Icon", "").Replace("_SDF", "").Replace("_BCA", "");
        if (IsSdf(part)) return $"{(upgrade ? "Upgrade" : "HUD")} SDF / {(part.PropertyPath.StartsWith("TextureParameterValues", StringComparison.Ordinal) ? "material" : "direct")} — {artwork}";
        if (IsBca(part)) return $"{role}: {artwork} — color / alpha (BCA)";
        return null;
    }
    internal static string Help(EquipmentAssetPart part)
    {
        var instructions = IsSdf(part)
            ? "Suggested: SDF\n\nImport a white silhouette on transparency, with optional green (#00FF00) accents, as Equipment SDF icon (white + green PNG). The material draws the outline and determines accent colour.\n\nUse the same cooked SDF for matching direct and material inputs. Do not import a prepared red/blue SDF through the white + green profile."
            : IsBca(part)
                ? "Suggested: BCA\n\nEquipment color icon (BCA) preserves the painted image, including its outline and transparency. It does not replace an SDF HUD icon. No BCA slot means no BCA is needed."
                : "EXPECTED: a compatible cooked texture.\n\nThis is not a recognized SDF/BCA binding. Check its original texture and material; do not assume a suit portrait or equipment profile is compatible.";
        return (IsSdf(part) ? "Use HUD icon material above the parts list to set up all HUD modes together.\n\n" : "") +
            instructions + (IsSdf(part) ? "\n\n" + EquipmentHudIconService.ArtworkHelp : "") +
            "\n\nChoose the cook or type its /Game/… path, click Use this game asset, then Save equipment and Build mod. Cooking alone does not assign it.";
    }
}
