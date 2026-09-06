using System.Text.Json;

namespace Batcomputer;

/// <summary>Suit-local derivative. Native behavior/animation/upgrade tags are intentionally retained.</summary>
public sealed class CustomEquipmentRecipe
{
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "Custom equipment";
    public string DonorEtaPackage { get; set; } = "";
    public List<EquipmentPartEdit> Parts { get; set; } = [];
    public CustomEquipmentRecipe Clone() => JsonSerializer.Deserialize<CustomEquipmentRecipe>(JsonSerializer.Serialize(this))!;
}

public sealed class EquipmentPartEdit
{
    public string OwnerPackage { get; set; } = "";
    public string ExportName { get; set; } = "";
    public string PropertyPath { get; set; } = "";
    public string OriginalPackage { get; set; } = "";
    public string ReplacementPackage { get; set; } = "";
    public WeaponModelRecipe? Model { get; set; }
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
    public bool CanEdit => AssetClass is "StaticMesh" or "MaterialInstanceConstant" or "Material" or "Texture2D";
}

public sealed class EquipmentAssetProfile
{
    public string EtaPackage { get; init; } = "";
    public string DefinitionPackage { get; set; } = "";
    public List<string> OwnedGraph { get; } = [];
    public List<EquipmentAssetPart> Parts { get; } = [];
    public List<string> Warnings { get; } = [];
}
