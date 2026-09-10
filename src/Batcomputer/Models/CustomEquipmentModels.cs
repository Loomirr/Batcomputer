using System.Text.Json;

namespace Batcomputer;

/// <summary>Suit-local derivative. Native behavior/animation/upgrade tags are intentionally retained.</summary>
public sealed class CustomEquipmentRecipe
{
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "Custom equipment";
    public string DonorEtaPackage { get; set; } = "";
    public List<EquipmentPartEdit> Parts { get; set; } = [];
    public EquipmentHudIconRecipe? HudIcon { get; set; }
    /// <summary>Stable upgrade IDs excluded from this item's functionality chain. Empty preserves all native upgrades.</summary>
    public List<string> DisabledUpgrades { get; set; } = [];
    public CustomEquipmentRecipe Clone() => JsonSerializer.Deserialize<CustomEquipmentRecipe>(JsonSerializer.Serialize(this))!;
}

/// <summary>One SDF material shared by the equipment's HUD states; gameplay upgrades stay native.</summary>
public sealed class EquipmentHudIconRecipe
{
    public string SdfPackage { get; set; } = "";
    public float GlowPower { get; set; } = 0.5f;
    public string FillColor { get; set; } = "#FFFFFF";
    public float FillOpacity { get; set; } = 1f;
    public string OutlineColor { get; set; } = "#000000";
    public float OutlineOpacity { get; set; } = 0.8f;
    public float OutlineWidth { get; set; } = 0.2f;
    public float Grain { get; set; } = 0.5f;
    public float Sharpness { get; set; } = 8f;
}

public sealed class EquipmentPartEdit
{
    public string OwnerPackage { get; set; } = "";
    public string ExportName { get; set; } = "";
    public string PropertyPath { get; set; } = "";
    public string OriginalPackage { get; set; } = "";
    public string ReplacementPackage { get; set; } = "";
    public WeaponModelRecipe? Model { get; set; }
    public SkinnedMeshImport? SkinnedModel { get; set; }
    public string Key => OwnerPackage + "|" + ExportName + "|" + PropertyPath;
}

public sealed class EquipmentAssetPart
{
    public string OwnerPackage { get; init; } = "";
    public string ExportName { get; init; } = "";
    public string PropertyPath { get; init; } = "";
    public string Package { get; init; } = "";
    public string AssetClass { get; init; } = "";
    public string Category { get; init; } = "";
    public string Key => OwnerPackage + "|" + ExportName + "|" + PropertyPath;
    public bool CanEdit => AssetClass is "StaticMesh" or "MaterialInstanceConstant" or "Material" or "Texture2D" || EquipmentSkinnedModelService.Supports(this) || EquipmentImpactMeshService.Supports(this);
}

public sealed class EquipmentAssetProfile
{
    public string EtaPackage { get; init; } = "";
    public string DefinitionPackage { get; set; } = "";
    public List<string> OwnedGraph { get; } = [];
    public List<EquipmentAssetPart> Parts { get; } = [];
    public List<string> Warnings { get; } = [];
}
