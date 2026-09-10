namespace Batcomputer;

/// <summary>Workshop-local snapshot. Never shares mutable model settings or native component identity.</summary>
internal sealed class EquipmentModelClipboard
{
    private CustomEquipmentRecipe? _owner;
    private WeaponModelRecipe? _model;
    internal string SourceTitle { get; private set; } = "";

    internal static bool CanCopy(CustomEquipmentRecipe recipe, EquipmentAssetPart? part) =>
        part is not null && (part.AssetClass == "StaticMesh" || EquipmentImpactMeshService.Supports(part)) && recipe.Parts.Any(edit => edit.Key == part.Key && edit.Model is not null);

    internal void Copy(CustomEquipmentRecipe recipe, EquipmentAssetPart part)
    {
        if (!CanCopy(recipe, part)) throw new InvalidDataException("Choose a part with a custom OBJ model first.");
        var snapshot = recipe.Parts.First(edit => edit.Key == part.Key).Model!.Clone();
        WeaponModelService.Validate(snapshot);
        _owner = recipe; _model = snapshot; SourceTitle = EquipmentWorkshopForm.PartTitle(part);
    }

    internal bool CanPaste(CustomEquipmentRecipe recipe, EquipmentAssetPart? part) =>
        ReferenceEquals(_owner, recipe) && _model is not null && part is not null && (part.AssetClass == "StaticMesh" || EquipmentImpactMeshService.Supports(part));

    internal void Paste(CustomEquipmentRecipe recipe, EquipmentAssetPart part)
    {
        if (!CanPaste(recipe, part)) throw new InvalidDataException("Copy a custom model from this equipment, then choose an editable static-model part.");
        StoreModel(recipe, part, _model!);
    }

    internal static void StoreModel(CustomEquipmentRecipe recipe, EquipmentAssetPart part, WeaponModelRecipe model)
    {
        if (part.AssetClass != "StaticMesh" && !EquipmentImpactMeshService.Supports(part)) throw new InvalidDataException("Custom OBJ models need a static component or supported impact-mesh adapter.");
        var copy = model.Clone();
        WeaponModelService.Validate(copy); // Fail before changing any existing edit.
        var edit = new EquipmentPartEdit { OwnerPackage = part.OwnerPackage, ExportName = part.ExportName,
            PropertyPath = part.PropertyPath, OriginalPackage = part.Package, Model = copy };
        recipe.Parts.RemoveAll(existing => existing.Key == part.Key ||
            (existing.OwnerPackage == part.OwnerPackage && existing.ExportName == part.ExportName &&
             existing.PropertyPath.StartsWith("OverrideMaterials", StringComparison.Ordinal)));
        recipe.Parts.Add(edit);
    }
}
