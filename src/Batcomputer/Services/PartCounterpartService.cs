namespace Batcomputer;

internal static class PartCounterpartService
{
    // A role counterpart supplies its native component shell, not a different appearance.
    internal static NativeSuitPartRecord? Find(NativeSuitPartRecord selected,
        IEnumerable<NativeSuitPartRecord> parts, string role)
    {
        var mesh = UnrealPathUtil.NormalizePackagePath(selected.MeshPackagePath);
        var match = parts.Where(p => p.HasMesh && p.Context.Equals(role, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(mesh) && SameAppearance(selected, p) &&
                SameComponentKind(p.ComponentClass, selected.ComponentClass))
            .OrderByDescending(p => BaseEligibilityService.IsSameCharacterVariant(selected.SourcePackagePath, p.SourcePackagePath))
            .ThenByDescending(p => p.Slot.Equals(selected.Slot, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.CharacterFolder.Equals(selected.CharacterFolder, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.SourcePackagePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (match is null) return null;
        var result = PartRecipeService.Clone(match);
        result.Materials = PartRecipeService.Clone(selected).Materials;
        result.RecipeKey = PartRecipeService.BuildRecipeKey(result);
        return result;
    }
    private static bool SameAppearance(NativeSuitPartRecord first, NativeSuitPartRecord second)
    {
        if (first.MeshPackagePath.Equals(second.MeshPackagePath, StringComparison.OrdinalIgnoreCase) &&
            first.MeshObjectName.Equals(second.MeshObjectName, StringComparison.OrdinalIgnoreCase)) return true;
        // Native capes have a higher-detail cutscene rig in the same cape family.
        // Only accept the documented Advanced suffix, never a different cape shape.
        if (!first.Slot.Equals("Cape", StringComparison.OrdinalIgnoreCase) || !second.Slot.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
            !first.MeshPackagePath.StartsWith("/Game/Characters/Attachments/Cape/", StringComparison.OrdinalIgnoreCase) ||
            !second.MeshPackagePath.StartsWith("/Game/Characters/Attachments/Cape/", StringComparison.OrdinalIgnoreCase)) return false;
        static string Identity(string value) => value.Replace("_AdvancedForceLOD", "_ForceLOD", StringComparison.OrdinalIgnoreCase)
            .Replace("_Advanced", "", StringComparison.OrdinalIgnoreCase);
        return Identity(first.MeshPackagePath).Equals(Identity(second.MeshPackagePath), StringComparison.OrdinalIgnoreCase) &&
            Identity(first.MeshObjectName).Equals(Identity(second.MeshObjectName), StringComparison.OrdinalIgnoreCase);
    }
    internal static bool SameComponentKind(string first, string second) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        first.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase) && second.Contains("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase) ||
        first.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) && second.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase);
}
