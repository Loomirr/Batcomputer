namespace Batcomputer;

public sealed record AnimationReplacementCandidate(
    string Name,
    string PackagePath,
    string AssetClass,
    string Source,
    string Detail,
    AnimLibraryEntry? LibraryEntry = null,
    bool CanSelect = true,
    string IncompatibilityReason = "");

/// <summary>
/// Produces target-compatible animation choices from the shipped base-game catalogue and the
/// workspace import library. Keeping compatibility here prevents a generic picker from offering a
/// sequence where the game expects an AnimBlueprint class, or a montage in a locomotion slot.
/// </summary>
public static class AnimationReplacementCatalogService
{
    public static IReadOnlyList<AnimationReplacementCandidate> Build(
        CharacterAnimationTargetSnapshot target,
        AnimLibrary library)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(library);

        var acceptedClass = AcceptedClass(target);
        if (string.IsNullOrWhiteSpace(acceptedClass))
        {
            return Array.Empty<AnimationReplacementCandidate>();
        }

        var candidates = new Dictionary<string, AnimationReplacementCandidate>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var asset in GameDataService.Instance.AssetsOfClass(acceptedClass))
        {
            var package = UnrealPathUtil.NormalizePackagePath(asset.Path);
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;
            }
            candidates[package] = new AnimationReplacementCandidate(
                FriendlyName(package),
                package,
                acceptedClass,
                "Base game",
                FamilyHint(package, target.OriginalPackage));
        }

        // Always retain the observed donor/effective assets even if the bundled path catalogue is
        // older than the user's active DLC extraction.
        AddObserved(candidates, target.OriginalPackage, target.AssetClass, "Current donor", acceptedClass);
        AddObserved(candidates, target.EffectivePackage, target.EffectiveAssetClass, "Current choice", acceptedClass);

        // Keep every user-library record visible. Compatibility is a property of the target slot,
        // not a reason to hide a tool-wide import. This is especially useful when diagnosing a
        // montage selected for a sequence slot, or a quarantined import which needs re-cooking.
        foreach (var entry in library.Entries
                     .Where(entry => !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase)))
        {
            var package = UnrealPathUtil.NormalizePackagePath(entry.PackagePath);
            var incompatibility = ImportedCompatibilityIssue(entry, acceptedClass);
            // A quarantined legacy import can reuse a base-game package path. Keep both rows: the
            // game-owned asset remains a valid choice while the imported record remains visible
            // with its repair reason.
            var key = "#library:" + (string.IsNullOrWhiteSpace(entry.Id)
                ? package + "|" + entry.Name + "|" + entry.AssetClass
                : entry.Id);
            candidates[key] = new AnimationReplacementCandidate(
                string.IsNullOrWhiteSpace(entry.Name)
                    ? string.IsNullOrWhiteSpace(package) ? "Unnamed imported animation" : FriendlyName(package)
                    : entry.Name,
                package,
                NormalizeClass(entry.AssetClass),
                "Imported",
                string.IsNullOrWhiteSpace(entry.Skeleton)
                    ? "Imported animation • rig not identified"
                    : $"Imported animation • {Leaf(entry.Skeleton)}",
                entry,
                string.IsNullOrWhiteSpace(incompatibility),
                incompatibility);
        }

        return candidates.Values
            .OrderBy(candidate => CandidateOrder(candidate, target))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string AcceptedClass(CharacterAnimationTargetSnapshot target)
    {
        if (target.ReferenceKind == CharacterAnimationReferenceKind.LocomotionSequence)
        {
            return "AnimSequence";
        }
        var value = NormalizeClass(string.IsNullOrWhiteSpace(target.AssetClass)
            ? target.EffectiveAssetClass
            : target.AssetClass);
        return value switch
        {
            "AnimSequence" => "AnimSequence",
            "AnimMontage" => "AnimMontage",
            "AnimBlueprintGeneratedClass" => "AnimBlueprintGeneratedClass",
            _ => "",
        };
    }

    public static bool CanUseImported(AnimLibraryEntry entry, string acceptedClass)
        => string.IsNullOrWhiteSpace(ImportedCompatibilityIssue(entry, acceptedClass));

    public static string ImportedCompatibilityIssue(AnimLibraryEntry entry, string acceptedClass)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var issues = new List<string>();
        var managed = entry.CachedFiles.Count > 0 &&
                      !entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase) &&
                      !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase);
        var actualClass = NormalizeClass(entry.AssetClass);
        if (!actualClass.Equals(acceptedClass, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(string.IsNullOrWhiteSpace(actualClass)
                ? $"Its asset class could not be identified; this slot requires {acceptedClass}."
                : $"This is {actualClass}, but this slot requires {acceptedClass}.");
        }
        if (string.IsNullOrWhiteSpace(entry.PackagePath))
        {
            issues.Add("The imported record has no /Game package path.");
        }
        if (entry.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase) || !entry.IsAvailable)
        {
            var health = entry.HealthIssues.FirstOrDefault(issue => !string.IsNullOrWhiteSpace(issue));
            issues.Add(string.IsNullOrWhiteSpace(health)
                ? "The import is quarantined or unavailable; repair or re-import it before use."
                : "The import is unavailable: " + health);
        }
        if (!managed)
        {
            issues.Add(entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase)
                ? "This external package is catalogued but not owned by Batcomputer, so it cannot be shipped with this suit."
                : "The imported cooked package is not present in the tool-wide animation cache.");
        }
        return string.Join(" ", issues.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void AddObserved(
        IDictionary<string, AnimationReplacementCandidate> destination,
        string packagePath,
        string assetClass,
        string source,
        string acceptedClass)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var normalizedClass = NormalizeClass(assetClass);
        if (string.IsNullOrWhiteSpace(package) || destination.ContainsKey(package) ||
            !normalizedClass.Equals(acceptedClass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        destination[package] = new AnimationReplacementCandidate(
            FriendlyName(package),
            package,
            normalizedClass,
            source,
            source);
    }

    private static int CandidateOrder(
        AnimationReplacementCandidate candidate,
        CharacterAnimationTargetSnapshot target)
    {
        if (candidate.PackagePath.Equals(target.EffectivePackage, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.PackagePath.Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.Source.Equals("Imported", StringComparison.OrdinalIgnoreCase)) return 2;
        return FamilyHint(candidate.PackagePath, target.OriginalPackage)
            .StartsWith("Same character", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    private static string FamilyHint(string packagePath, string originalPackage)
    {
        var originalFamily = LegofigFamily(originalPackage);
        var candidateFamily = LegofigFamily(packagePath);
        return !string.IsNullOrWhiteSpace(originalFamily) &&
               originalFamily.Equals(candidateFamily, StringComparison.OrdinalIgnoreCase)
            ? $"Same character • {candidateFamily}"
            : string.IsNullOrWhiteSpace(candidateFamily)
                ? "Base-game animation"
                : $"Base-game character • {candidateFamily}";
    }

    private static string LegofigFamily(string packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        const string marker = "/Animation/LEGOfig/";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";
        var rest = normalized[(index + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : "";
    }

    private static string FriendlyName(string packagePath) =>
        Leaf(packagePath)
            .Replace("ABP_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AM_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("A_", "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ')
            .Trim();

    private static string Leaf(string value)
    {
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
    }

    private static string NormalizeClass(string? value)
    {
        var normalized = value?.Trim() ?? "";
        var split = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('.'));
        return split >= 0 && split + 1 < normalized.Length ? normalized[(split + 1)..] : normalized;
    }
}
