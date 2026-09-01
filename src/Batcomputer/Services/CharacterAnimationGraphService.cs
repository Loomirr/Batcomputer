using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Read-only description of the animation assets selected by a suit's gameplay donor. The graph
/// deliberately contains no staging instructions: authoring and packaging can consume the stable
/// target identities later without making this discovery surface capable of mutating cooked data.
/// </summary>
public sealed record CharacterAnimationSnapshot(
    string SuitId,
    string SuitName,
    string GameplayFamily,
    string MontageCompositePackage,
    string LayerCompositePackage,
    IReadOnlyList<CharacterAnimationSetSnapshot> Sets,
    IReadOnlyList<CharacterAnimationTargetSnapshot> LocomotionSequences,
    IReadOnlyList<CharacterAnimationDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(item => item.Severity == CharacterAnimationDiagnosticSeverity.Error);
}

public sealed record CharacterAnimationSetSnapshot(
    string SetId,
    int ParentIndex,
    CharacterAnimationSetKind Kind,
    string Category,
    string DonorPackage,
    string EffectivePackage,
    bool IsOverridden,
    string OverrideCategory,
    IReadOnlyList<CharacterAnimationSlotSnapshot> Slots);

public sealed record CharacterAnimationSlotSnapshot(
    string SlotId,
    string SetPackage,
    CharacterAnimationSetKind SetKind,
    int EntryIndex,
    string ActionTag,
    IReadOnlyList<string> ContextTags,
    int ActionLink,
    IReadOnlyList<CharacterAnimationTargetSnapshot> Targets);

public sealed record CharacterAnimationTargetSnapshot(
    string TargetId,
    CharacterAnimationReferenceKind ReferenceKind,
    string OwnerPackage,
    string OwnerClass,
    string OriginalPackage,
    string EffectivePackage,
    string OriginalObjectName,
    string EffectiveObjectName,
    string AssetClass,
    string EffectiveAssetClass,
    int EntryIndex,
    int WeightIndex,
    int LayerIndex,
    int Weight,
    bool IsOverridden,
    string OverrideKind);

public sealed record CharacterAnimationDiagnostic(
    CharacterAnimationDiagnosticSeverity Severity,
    string Code,
    string Message,
    string PackagePath = "");

public enum CharacterAnimationSetKind
{
    Montage,
    Layer,
}

public enum CharacterAnimationReferenceKind
{
    AnimFile,
    LayerAnimation,
    LocomotionSequence,
}

public enum CharacterAnimationDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Optional bridge for callers that need to layer a newer exact-slot representation over the
/// persisted <see cref="AnimationSlotOverride"/> model. Persisted overrides are always applied
/// first; the callback can add non-project overlays without coupling discovery to their schema.
/// </summary>
public delegate CharacterAnimationTargetSnapshot CharacterAnimationExactSlotOverlay(
    NativeSuitProject project,
    CharacterAnimationTargetSnapshot target);

/// <summary>
/// Builds the effective, read-only MAS/LAS animation graph for a selected suit. Discovery starts at
/// the gameplay donor's MAS_Char/LAS_Char, reads their real ParentSetsArray values from the active
/// extract, then expands every TTAnimSet/TTLayerSet AnimSetEntryArray entry. Failures are retained as
/// diagnostics so one missing or unreadable package never takes down the animation browser.
/// </summary>
public sealed class CharacterAnimationGraphService
{
    private const CustomSerializationFlags ReadFlags =
        CustomSerializationFlags.SkipPreloadDependencyLoading;

    private readonly CharacterAnimationExactSlotOverlay? _exactSlotOverlay;

    public CharacterAnimationGraphService(CharacterAnimationExactSlotOverlay? exactSlotOverlay = null)
    {
        _exactSlotOverlay = exactSlotOverlay;
    }

    public CharacterAnimationSnapshot Build(NativeSuitProject? project)
    {
        var diagnostics = new List<CharacterAnimationDiagnostic>();
        if (project is null)
        {
            diagnostics.Add(Error("no-project", "Open or create a suit before browsing its character animations."));
            return EmptySnapshot(diagnostics);
        }

        string extractedRoot;
        Usmap? mappings = null;
        try
        {
            extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        }
        catch (Exception ex)
        {
            diagnostics.Add(Error("extract-root", "The active extracted Content folder could not be resolved: " + ex.Message));
            return EmptySnapshot(diagnostics, project);
        }

        try
        {
            var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
            if (!string.IsNullOrWhiteSpace(mappingsPath) && File.Exists(mappingsPath))
            {
                mappings = MappingsCache.Load(mappingsPath);
            }
            else
            {
                diagnostics.Add(Warning(
                    "mappings-missing",
                    "The configured .usmap is unavailable. Parent imports may still be visible, but unversioned animation-set entries may not parse."));
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(Warning("mappings-load", "The configured .usmap could not be loaded: " + ex.Message));
        }

        try
        {
            return BuildCore(project, extractedRoot, mappings, diagnostics);
        }
        catch (Exception ex)
        {
            // The individual readers below are already fail-soft. Keep this final boundary as a
            // last-resort guarantee that a malformed project or unexpected package shape becomes a
            // visible diagnostic instead of an unhandled UI exception.
            diagnostics.Add(Error("graph-build", $"Animation graph discovery stopped safely: {ex.GetType().Name}: {ex.Message}"));
            return EmptySnapshot(diagnostics, project);
        }
    }

    private CharacterAnimationSnapshot BuildCore(
        NativeSuitProject project,
        string extractedRoot,
        Usmap? mappings,
        List<CharacterAnimationDiagnostic> diagnostics)
    {
        var donor = ResolveGameplayDonor(project, extractedRoot, mappings, diagnostics);
        var family = donor?.Family ?? ResolveFallbackFamily(project);
        var montageComposite = donor?.MasCharPackage ?? "";
        var layerComposite = donor?.LasCharPackage ?? "";

        if (string.IsNullOrWhiteSpace(family))
        {
            diagnostics.Add(Error(
                "gameplay-family",
                "Batcomputer could not determine the suit's gameplay animation family from its playable base or machinery donor."));
        }

        if (string.IsNullOrWhiteSpace(montageComposite) || string.IsNullOrWhiteSpace(layerComposite))
        {
            var fallback = string.IsNullOrWhiteSpace(family)
                ? null
                : GameDataService.Instance.FindFamily(family);
            montageComposite = ValueOr(
                montageComposite,
                fallback?.MontageAnimSet ?? "",
                string.IsNullOrWhiteSpace(family)
                    ? ""
                    : $"/Game/Animation/MontageAnimSets/Character/MAS_Char_{family}");
            layerComposite = ValueOr(
                layerComposite,
                fallback?.LayerAnimSet ?? "",
                string.IsNullOrWhiteSpace(family)
                    ? ""
                    : $"/Game/Animation/LayerAnimSets/Character/LAS_Char_{family}");
        }

        var sets = new List<CharacterAnimationSetSnapshot>();
        sets.AddRange(ReadCompositeSets(
            project,
            extractedRoot,
            mappings,
            montageComposite,
            CharacterAnimationSetKind.Montage,
            family,
            diagnostics));
        sets.AddRange(ReadCompositeSets(
            project,
            extractedRoot,
            mappings,
            layerComposite,
            CharacterAnimationSetKind.Layer,
            family,
            diagnostics));

        var locomotion = ReadLocomotionSequences(project, family, extractedRoot, mappings, diagnostics);
        return new CharacterAnimationSnapshot(
            project.SlotId ?? "",
            ValueOr(project.DisplayName ?? "", project.SlotId ?? "", "Selected suit"),
            family,
            UnrealPathUtil.NormalizePackagePath(montageComposite),
            UnrealPathUtil.NormalizePackagePath(layerComposite),
            Freeze(sets),
            Freeze(locomotion),
            Freeze(diagnostics));
    }

    private static DonorInfo? ResolveGameplayDonor(
        NativeSuitProject project,
        string extractedRoot,
        Usmap? mappings,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        try
        {
            var donor = AnimArchetypeGraftService.DetectDonorForProject(project, extractedRoot, mappings);
            if (donor is not null && donor.Valid)
            {
                return donor;
            }
            diagnostics.Add(Warning(
                "donor-detect",
                "The playable package did not expose a complete gameplay donor. Shipped family metadata will be used where possible."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(Warning("donor-detect", "Gameplay donor inspection failed: " + ex.Message));
        }
        return null;
    }

    private static string ResolveFallbackFamily(NativeSuitProject project)
    {
        var configured = project.BaseProfile?.GameplayFamily ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            project.BaseProfile?.GameplayDonorPackage,
            project.MachineryDonorPlayable,
            project.PlayableTemplate?.PackagePath,
        };
        foreach (var candidate in candidates)
        {
            var family = GameDataService.Instance.FamilyForBasePath(candidate)?.Name;
            if (!string.IsNullOrWhiteSpace(family))
            {
                return family;
            }
        }
        return "";
    }

    private IReadOnlyList<CharacterAnimationSetSnapshot> ReadCompositeSets(
        NativeSuitProject project,
        string extractedRoot,
        Usmap? mappings,
        string compositePackage,
        CharacterAnimationSetKind kind,
        string family,
        List<CharacterAnimationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(compositePackage))
        {
            diagnostics.Add(Error(
                kind == CharacterAnimationSetKind.Montage ? "mas-composite" : "las-composite",
                $"The gameplay donor does not identify a {(kind == CharacterAnimationSetKind.Montage ? "MAS_Char" : "LAS_Char")} composite."));
            return Array.Empty<CharacterAnimationSetSnapshot>();
        }

        var parentPackages = ReadParentSetPackages(
            extractedRoot,
            mappings,
            compositePackage,
            kind,
            diagnostics);
        var result = new List<CharacterAnimationSetSnapshot>();
        foreach (var parent in parentPackages)
        {
            var donorPackage = parent.PackagePath;
            var category = CategoryForSet(donorPackage);
            var setOverride = FindSetOverride(project, kind, donorPackage, category, family);
            var effectivePackage = !string.IsNullOrWhiteSpace(setOverride?.ReplacementPackage)
                ? UnrealPathUtil.NormalizePackagePath(setOverride!.ReplacementPackage)
                : donorPackage;
            var isOverridden = !effectivePackage.Equals(donorPackage, StringComparison.OrdinalIgnoreCase);

            var slots = ReadSetSlots(
                extractedRoot,
                mappings,
                effectivePackage,
                kind,
                diagnostics);
            if (slots.Count > 0)
            {
                slots = ApplyExactSlotOverrides(project, slots, diagnostics);
            }

            result.Add(new CharacterAnimationSetSnapshot(
                BuildSetId(compositePackage, donorPackage, kind, parent.ParentIndex),
                parent.ParentIndex,
                kind,
                category,
                donorPackage,
                effectivePackage,
                isOverridden,
                setOverride?.Category ?? "",
                slots));
        }
        return Freeze(result);
    }

    private static IReadOnlyList<RawParentSetReference> ReadParentSetPackages(
        string extractedRoot,
        Usmap? mappings,
        string compositePackage,
        CharacterAnimationSetKind kind,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(compositePackage);
        var uassetPath = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, normalized);
        if (string.IsNullOrWhiteSpace(uassetPath) || !File.Exists(uassetPath))
        {
            diagnostics.Add(Error("composite-missing", "Animation composite is not present in the active extract.", normalized));
            return Array.Empty<RawParentSetReference>();
        }

        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, ReadFlags);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(candidate => candidate.Data.OfType<ArrayPropertyData>()
                    .Any(property => PropertyName(property) == "ParentSetsArray"));
            var parentArray = export?.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyName(property) == "ParentSetsArray");
            if (parentArray is null)
            {
                diagnostics.Add(Error("parent-array", "The character composite has no readable ParentSetsArray.", normalized));
                return ParentSetImportFallback(asset, normalized, kind, diagnostics);
            }

            var packages = new List<RawParentSetReference>();
            for (var parentIndex = 0; parentIndex < parentArray.Value.Length; parentIndex++)
            {
                if (parentArray.Value[parentIndex] is not ObjectPropertyData property || property.Value.IsNull())
                {
                    diagnostics.Add(Warning(
                        "parent-shape",
                        $"ParentSetsArray entry {parentIndex} is not a non-null object reference and was skipped.",
                        normalized));
                    continue;
                }

                string package;
                try
                {
                    package = UnrealPathUtil.NormalizePackagePath(
                        ResolveObjectReference(asset, property.Value).PackagePath);
                }
                catch (Exception ex)
                {
                    diagnostics.Add(Warning(
                        "parent-reference",
                        $"ParentSetsArray entry {parentIndex} could not be resolved and was skipped: {ex.Message}",
                        normalized));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(package) ||
                    package.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Warning(
                        "parent-reference",
                        $"ParentSetsArray entry {parentIndex} did not resolve to a distinct content package and was skipped.",
                        normalized));
                    continue;
                }
                packages.Add(new RawParentSetReference(parentIndex, package));
            }
            return Freeze(packages);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Error(
                "composite-parse",
                $"The character composite could not be parsed: {ex.GetType().Name}: {ex.Message}",
                normalized));
            return Array.Empty<RawParentSetReference>();
        }
    }

    private static IReadOnlyList<RawParentSetReference> ParentSetImportFallback(
        UAsset asset,
        string compositePackage,
        CharacterAnimationSetKind kind,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var prefix = kind == CharacterAnimationSetKind.Montage ? "MAS_" : "LAS_";
        var recoveredPackages = asset.Imports
            .Where(import => import.ClassName.ToString() == "Package")
            .Select(import => UnrealPathUtil.NormalizePackagePath(import.ObjectName.ToString()))
            .Where(package => ExtractedPackagePathService.IsContentPackagePath(package))
            .Where(package => UnrealPathUtil.AssetName(package).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(package => !package.Equals(compositePackage, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var packages = recoveredPackages
            .Select((package, index) => new RawParentSetReference(index, package))
            .ToList();
        if (packages.Count > 0)
        {
            diagnostics.Add(Warning(
                "parent-fallback",
                "Parent sets were recovered from the import table because ParentSetsArray was unreadable; their authored order may be unavailable.",
                compositePackage));
        }
        return Freeze(packages);
    }

    private static AnimSetOverride? FindSetOverride(
        NativeSuitProject project,
        CharacterAnimationSetKind kind,
        string donorPackage,
        string category,
        string family)
    {
        var overrides = project.AnimationOverrides ?? new List<AnimSetOverride>();
        var kindName = kind == CharacterAnimationSetKind.Montage ? "Montage" : "Layer";
        var donorName = UnrealPathUtil.AssetName(donorPackage);

        var exact = overrides.LastOrDefault(item =>
            (string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Equals(kindName, StringComparison.OrdinalIgnoreCase)) &&
            (item.DonorSet.Equals(donorName, StringComparison.OrdinalIgnoreCase) ||
             UnrealPathUtil.NormalizePackagePath(item.DonorSet).Equals(donorPackage, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null)
        {
            return exact;
        }

        // Legacy family-swap records store a Batman-shaped donor name while packaging resolves the
        // actual family-relative donor from Category. Mirror that behavior for the read-only view.
        foreach (var map in GameDataService.AnimCategoryMap)
        {
            if (!map.Kind.Equals(kindName, StringComparison.OrdinalIgnoreCase) ||
                !donorName.Equals(map.SetPrefix + family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var byCategory = overrides.LastOrDefault(item =>
                item.Category.Equals(map.Category, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Equals(kindName, StringComparison.OrdinalIgnoreCase)));
            if (byCategory is not null)
            {
                return byCategory;
            }
        }

        return overrides.LastOrDefault(item =>
            !string.IsNullOrWhiteSpace(category) &&
            item.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Equals(kindName, StringComparison.OrdinalIgnoreCase)) &&
            item.DonorSet.Equals(donorName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CharacterAnimationSlotSnapshot> ReadSetSlots(
        string extractedRoot,
        Usmap? mappings,
        string setPackage,
        CharacterAnimationSetKind kind,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(setPackage);
        var uassetPath = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, normalized);
        if (string.IsNullOrWhiteSpace(uassetPath) || !File.Exists(uassetPath))
        {
            diagnostics.Add(Error("set-missing", "Animation parent set is not present in the active extract.", normalized));
            return Array.Empty<CharacterAnimationSlotSnapshot>();
        }

        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, ReadFlags);
            var raw = ReadRawSetEntries(asset, normalized, diagnostics);
            return MaterializeSlots(normalized, kind, raw);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Error(
                "set-parse",
                $"Animation parent set could not be parsed: {ex.GetType().Name}: {ex.Message}",
                normalized));
            return Array.Empty<CharacterAnimationSlotSnapshot>();
        }
    }

    private static IReadOnlyList<RawAnimationSetEntry> ReadRawSetEntries(
        UAsset asset,
        string setPackage,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var export = asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(candidate => candidate.Data.OfType<ArrayPropertyData>()
                .Any(property => PropertyName(property) == "AnimSetEntryArray"));
        var entries = export?.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyName(property) == "AnimSetEntryArray");
        if (entries is null)
        {
            diagnostics.Add(Error("entry-array", "Animation set has no readable AnimSetEntryArray.", setPackage));
            return Array.Empty<RawAnimationSetEntry>();
        }

        return ReadRawSetEntries(asset, setPackage, entries, diagnostics);
    }

    private static IReadOnlyList<RawAnimationSetEntry> ReadRawSetEntries(
        UAsset asset,
        string setPackage,
        ArrayPropertyData entries,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {

        var result = new List<RawAnimationSetEntry>();
        for (var entryIndex = 0; entryIndex < entries.Value.Length; entryIndex++)
        {
            if (entries.Value[entryIndex] is not StructPropertyData entry)
            {
                diagnostics.Add(Warning("entry-shape", $"Animation entry {entryIndex} is not a reflected struct and was skipped.", setPackage));
                continue;
            }

            var actionTag = ReadGameplayTag(entry.Value, "ActionTag");
            var contextTags = ReadGameplayTagContainer(entry.Value, "ContextTags");
            var actionLink = entry.Value.OfType<IntPropertyData>()
                .FirstOrDefault(property => PropertyName(property) == "ActionLink")?.Value ?? -1;
            var weights = new List<RawAnimationWeightEntry>();
            var weightsArray = entry.Value.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyName(property) == "AnimAndWeightsArray");
            if (weightsArray is not null)
            {
                for (var weightIndex = 0; weightIndex < weightsArray.Value.Length; weightIndex++)
                {
                    if (weightsArray.Value[weightIndex] is not StructPropertyData weightEntry)
                    {
                        diagnostics.Add(Warning(
                            "weight-shape",
                            $"Animation entry {entryIndex}, weight {weightIndex} is not a reflected struct and was skipped.",
                            setPackage));
                        continue;
                    }

                    var weight = weightEntry.Value.OfType<BytePropertyData>()
                        .FirstOrDefault(property => PropertyName(property) == "Weight")?.Value ?? 0;
                    RawAnimationReference? animFile = null;
                    var animProperty = weightEntry.Value.OfType<ObjectPropertyData>()
                        .FirstOrDefault(property => PropertyName(property) == "AnimFile");
                    if (animProperty is not null && !animProperty.Value.IsNull())
                    {
                        try
                        {
                            animFile = ResolveObjectReference(asset, animProperty.Value);
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add(Warning(
                                "anim-reference",
                                $"Animation entry {entryIndex}, weight {weightIndex} AnimFile could not be resolved and was skipped: {ex.Message}",
                                setPackage));
                        }
                    }

                    var layerReferences = new List<RawLayerAnimationReference>();
                    var layerArray = weightEntry.Value.OfType<ArrayPropertyData>()
                        .FirstOrDefault(property => PropertyName(property) == "LayerAnimArray");
                    if (layerArray is not null)
                    {
                        for (var layerIndex = 0; layerIndex < layerArray.Value.Length; layerIndex++)
                        {
                            if (layerArray.Value[layerIndex] is not ObjectPropertyData layerProperty)
                            {
                                diagnostics.Add(Warning(
                                    "layer-shape",
                                    $"Animation entry {entryIndex}, weight {weightIndex}, layer {layerIndex} is not an object reference and was skipped.",
                                    setPackage));
                                continue;
                            }
                            if (layerProperty.Value.IsNull())
                            {
                                continue;
                            }
                            RawAnimationReference reference;
                            try
                            {
                                reference = ResolveObjectReference(asset, layerProperty.Value);
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add(Warning(
                                    "layer-reference",
                                    $"Animation entry {entryIndex}, weight {weightIndex}, layer {layerIndex} could not be resolved and was skipped: {ex.Message}",
                                    setPackage));
                                continue;
                            }
                            if (!string.IsNullOrWhiteSpace(reference.PackagePath))
                            {
                                layerReferences.Add(new RawLayerAnimationReference(layerIndex, reference));
                            }
                        }
                    }
                    weights.Add(new RawAnimationWeightEntry(
                        weightIndex,
                        Convert.ToInt32(weight),
                        animFile,
                        Freeze(layerReferences)));
                }
            }

            result.Add(new RawAnimationSetEntry(
                entryIndex,
                actionTag,
                Freeze(contextTags),
                actionLink,
                Freeze(weights)));
        }
        return Freeze(result);
    }

    private IReadOnlyList<CharacterAnimationSlotSnapshot> ApplyExactSlotOverrides(
        NativeSuitProject project,
        IReadOnlyList<CharacterAnimationSlotSnapshot> slots,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var result = slots
            .Select(slot => slot with { Targets = Freeze(slot.Targets) })
            .ToList();

        foreach (var change in project.AnimationSlotOverrides ?? new List<AnimationSlotOverride>())
        {
            var candidates = new List<(int SlotIndex, int TargetIndex)>();
            for (var slotIndex = 0; slotIndex < result.Count; slotIndex++)
            {
                var slot = result[slotIndex];
                for (var targetIndex = 0; targetIndex < slot.Targets.Count; targetIndex++)
                {
                    if (PersistedSlotSemanticallyMatches(change, slot, slot.Targets[targetIndex]))
                    {
                        candidates.Add((slotIndex, targetIndex));
                    }
                }
            }

            var exact = candidates.Where(location =>
            {
                var target = result[location.SlotIndex].Targets[location.TargetIndex];
                var referenceIndex = target.ReferenceKind == CharacterAnimationReferenceKind.AnimFile
                    ? 0
                    : target.LayerIndex;
                return target.EntryIndex == change.EntryIndex &&
                       target.WeightIndex == change.VariantIndex &&
                       referenceIndex == Math.Max(0, change.ReferenceIndex);
            }).ToList();
            var chosen = exact.Count == 1
                ? exact[0]
                : exact.Count == 0 && candidates.Count == 1
                    ? candidates[0]
                    : ((int SlotIndex, int TargetIndex)?)null;
            if (chosen is null)
            {
                diagnostics.Add(Warning(
                    candidates.Count == 0 ? "exact-slot-missing" : "exact-slot-ambiguous",
                    candidates.Count == 0
                        ? $"Saved exact-slot override '{change.ActionTag}' no longer matches the active animation graph."
                        : $"Saved exact-slot override '{change.ActionTag}' matches {candidates.Count} graph targets and was not guessed.",
                    change.OwnerSetPackage));
                continue;
            }

            var replacementPackage = UnrealPathUtil.NormalizePackagePath(change.ReplacementPackage ?? "");
            if (string.IsNullOrWhiteSpace(replacementPackage) ||
                !ExtractedPackagePathService.IsContentPackagePath(replacementPackage))
            {
                diagnostics.Add(Warning(
                    "exact-slot-replacement",
                    $"Saved exact-slot override '{change.ActionTag}' has no valid /Game replacement package.",
                    change.OwnerSetPackage));
                continue;
            }

            var location = chosen.Value;
            var selectedSlot = result[location.SlotIndex];
            var selectedTarget = selectedSlot.Targets[location.TargetIndex];
            var effectiveClass = NormalizeClassName(ValueOr(change.ReplacementClass ?? "", selectedTarget.AssetClass));
            var effectiveObject = UnrealPathUtil.AssetName(replacementPackage);
            if (effectiveClass.EndsWith("GeneratedClass", StringComparison.OrdinalIgnoreCase) &&
                !effectiveObject.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
            {
                effectiveObject += "_C";
            }
            var changedTarget = selectedTarget with
            {
                EffectivePackage = replacementPackage,
                EffectiveObjectName = effectiveObject,
                EffectiveAssetClass = effectiveClass,
                IsOverridden = true,
                OverrideKind = "exact-slot",
            };
            var changedTargets = selectedSlot.Targets.ToArray();
            changedTargets[location.TargetIndex] = changedTarget;
            result[location.SlotIndex] = selectedSlot with { Targets = Array.AsReadOnly(changedTargets) };
        }

        if (_exactSlotOverlay is not null)
        {
            for (var slotIndex = 0; slotIndex < result.Count; slotIndex++)
            {
                var slot = result[slotIndex];
                var targets = new List<CharacterAnimationTargetSnapshot>(slot.Targets.Count);
                foreach (var target in slot.Targets)
                {
                    try
                    {
                        targets.Add(_exactSlotOverlay(project, target) ?? target);
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(Warning(
                            "exact-overlay",
                            $"An exact-slot override overlay failed and the persisted target was retained: {ex.Message}",
                            target.OwnerPackage));
                        targets.Add(target);
                    }
                }
                result[slotIndex] = slot with { Targets = Freeze(targets) };
            }
        }
        return Freeze(result);
    }

    private static bool PersistedSlotSemanticallyMatches(
        AnimationSlotOverride change,
        CharacterAnimationSlotSnapshot slot,
        CharacterAnimationTargetSnapshot target)
    {
        var expectedKind = slot.SetKind == CharacterAnimationSetKind.Montage ? "Montage" : "Layer";
        var expectedReference = target.ReferenceKind == CharacterAnimationReferenceKind.AnimFile
            ? "AnimFile"
            : "LayerAnim";
        return (string.IsNullOrWhiteSpace(change.Kind) ||
                change.Kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase)) &&
               UnrealPathUtil.NormalizePackagePath(change.OwnerSetPackage ?? "")
                   .Equals(target.OwnerPackage, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(change.ActionTag, slot.ActionTag, StringComparison.OrdinalIgnoreCase) &&
               StableContexts(change.ContextTags).Equals(
                   StableContexts(slot.ContextTags),
                   StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(change.ReferenceKind, expectedReference, StringComparison.OrdinalIgnoreCase) ||
                target.ReferenceKind == CharacterAnimationReferenceKind.LayerAnimation &&
                (string.Equals(change.ReferenceKind, "LayerAnimation", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(change.ReferenceKind, "LayerAnimArray", StringComparison.OrdinalIgnoreCase))) &&
               UnrealPathUtil.NormalizePackagePath(change.DonorPackage ?? "")
                   .Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<CharacterAnimationTargetSnapshot> ReadLocomotionSequences(
        NativeSuitProject project,
        string family,
        string extractedRoot,
        Usmap? mappings,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return Array.Empty<CharacterAnimationTargetSnapshot>();
        }

        try
        {
            var graph = AnimArchetypeGraftService.DetectLocomotionGraph(family, mappings);
            if (graph.Sequences.Count == 0)
            {
                var lasPath = ExtractedPackagePathService.ResolvePackageUasset(
                    extractedRoot,
                    graph.LasDefaultPackage);
                diagnostics.Add(Warning(
                    string.IsNullOrWhiteSpace(lasPath) || !File.Exists(lasPath)
                        ? "locomotion-source-missing"
                        : "locomotion-empty",
                    string.IsNullOrWhiteSpace(lasPath) || !File.Exists(lasPath)
                        ? "The family's LAS_Default package is not present in the active extract, so locomotion sequences could not be enumerated."
                        : "No locomotion AnimSequence references were detected below the family's LAS_Default graph.",
                    graph.LasDefaultPackage));
            }
            var overrides = project.LocomotionOverrides ?? new List<AnimSequenceOverride>();
            var result = new List<CharacterAnimationTargetSnapshot>();
            foreach (var owner in graph.Sequences.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var sequence in owner.Value.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var originalPackage = UnrealPathUtil.NormalizePackagePath(sequence.Package);
                    var sequenceOverride = overrides.LastOrDefault(item =>
                        UnrealPathUtil.NormalizePackagePath(item.DonorSequencePackage)
                            .Equals(originalPackage, StringComparison.OrdinalIgnoreCase) ||
                        item.DonorSequence.Equals(sequence.Name, StringComparison.OrdinalIgnoreCase));
                    var effectivePackage = !string.IsNullOrWhiteSpace(sequenceOverride?.ReplacementPackage)
                        ? UnrealPathUtil.NormalizePackagePath(sequenceOverride!.ReplacementPackage)
                        : originalPackage;
                    var effectiveObject = ValueOr(
                        sequenceOverride?.ReplacementSequence ?? "",
                        UnrealPathUtil.AssetName(effectivePackage));
                    var target = new CharacterAnimationTargetSnapshot(
                        BuildLocomotionTargetId(owner.Key, originalPackage),
                        CharacterAnimationReferenceKind.LocomotionSequence,
                        UnrealPathUtil.NormalizePackagePath(owner.Key),
                        OwnerClassFromPackage(owner.Key),
                        originalPackage,
                        effectivePackage,
                        sequence.Name,
                        effectiveObject,
                        "AnimSequence",
                        "AnimSequence",
                        -1,
                        -1,
                        -1,
                        0,
                        !effectivePackage.Equals(originalPackage, StringComparison.OrdinalIgnoreCase),
                        sequenceOverride is null ? "" : "sequence");
                    if (_exactSlotOverlay is not null)
                    {
                        try
                        {
                            target = _exactSlotOverlay(project, target) ?? target;
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add(Warning(
                                "exact-overlay",
                                $"An exact-slot override overlay failed and the donor locomotion target was retained: {ex.Message}",
                                owner.Key));
                        }
                    }
                    result.Add(target);
                }
            }
            return Freeze(result);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Warning(
                "locomotion-graph",
                $"Locomotion sequences could not be expanded: {ex.GetType().Name}: {ex.Message}"));
            return Array.Empty<CharacterAnimationTargetSnapshot>();
        }
    }

    internal static IReadOnlyList<CharacterAnimationSlotSnapshot> MaterializeSlotsForTest(
        string setPackage,
        CharacterAnimationSetKind kind,
        IReadOnlyList<RawAnimationSetEntry> entries) =>
        MaterializeSlots(setPackage, kind, entries);

    internal static IReadOnlyList<CharacterAnimationSlotSnapshot> ParseSetExportDataForTest(
        UAsset asset,
        string setPackage,
        CharacterAnimationSetKind kind,
        IReadOnlyList<PropertyData> exportData,
        ICollection<CharacterAnimationDiagnostic> diagnostics)
    {
        var entries = exportData.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyName(property) == "AnimSetEntryArray");
        if (entries is null)
        {
            diagnostics.Add(Error("entry-array", "Animation set has no readable AnimSetEntryArray.", setPackage));
            return Array.Empty<CharacterAnimationSlotSnapshot>();
        }
        return MaterializeSlots(
            setPackage,
            kind,
            ReadRawSetEntries(asset, setPackage, entries, diagnostics));
    }

    internal IReadOnlyList<CharacterAnimationSlotSnapshot> ApplyExactSlotOverridesForTest(
        NativeSuitProject project,
        IReadOnlyList<CharacterAnimationSlotSnapshot> slots,
        ICollection<CharacterAnimationDiagnostic> diagnostics) =>
        ApplyExactSlotOverrides(project, slots, diagnostics);

    /// <summary>
    /// Finds the saved override represented by an Explorer target. Numeric positions are preferred,
    /// but a single semantic match is accepted after a refreshed dump moves a row. This mirrors the
    /// overlay/packaging policy so Reset and Replace cannot strand a stale record.
    /// </summary>
    internal static int SelectPersistedSlotOverrideIndex(
        IReadOnlyList<AnimationSlotOverride> changes,
        CharacterAnimationSlotSnapshot slot,
        CharacterAnimationTargetSnapshot target,
        out bool ambiguous)
    {
        var semantic = changes
            .Select((change, index) => new { Change = change, Index = index })
            .Where(candidate => PersistedSlotSemanticallyMatches(candidate.Change, slot, target))
            .ToList();
        var referenceIndex = target.ReferenceKind == CharacterAnimationReferenceKind.AnimFile
            ? 0
            : Math.Max(0, target.LayerIndex);
        var exact = semantic.Where(candidate =>
                candidate.Change.EntryIndex == target.EntryIndex &&
                candidate.Change.VariantIndex == target.WeightIndex &&
                Math.Max(0, candidate.Change.ReferenceIndex) == referenceIndex)
            .ToList();

        if (exact.Count == 1)
        {
            ambiguous = false;
            return exact[0].Index;
        }
        if (exact.Count == 0 && semantic.Count == 1)
        {
            ambiguous = false;
            return semantic[0].Index;
        }

        ambiguous = exact.Count > 1 || semantic.Count > 1;
        return -1;
    }

    private static IReadOnlyList<CharacterAnimationSlotSnapshot> MaterializeSlots(
        string setPackage,
        CharacterAnimationSetKind kind,
        IReadOnlyList<RawAnimationSetEntry> entries)
    {
        var slots = new List<CharacterAnimationSlotSnapshot>(entries.Count);
        foreach (var entry in entries)
        {
            var targets = new List<CharacterAnimationTargetSnapshot>();
            foreach (var weight in entry.Weights)
            {
                if (weight.AnimFile is { } animFile && !string.IsNullOrWhiteSpace(animFile.PackagePath))
                {
                    targets.Add(MaterializeTarget(
                        setPackage,
                        kind,
                        entry,
                        weight,
                        animFile,
                        CharacterAnimationReferenceKind.AnimFile,
                        -1));
                }
                foreach (var layerAnimation in weight.LayerAnimations)
                {
                    targets.Add(MaterializeTarget(
                        setPackage,
                        kind,
                        entry,
                        weight,
                        layerAnimation.Reference,
                        CharacterAnimationReferenceKind.LayerAnimation,
                        layerAnimation.LayerIndex));
                }
            }

            slots.Add(new CharacterAnimationSlotSnapshot(
                BuildSlotId(setPackage, kind, entry.EntryIndex, entry.ActionTag, entry.ContextTags),
                UnrealPathUtil.NormalizePackagePath(setPackage),
                kind,
                entry.EntryIndex,
                entry.ActionTag,
                Freeze(entry.ContextTags),
                entry.ActionLink,
                Freeze(targets)));
        }
        return Freeze(slots);
    }

    private static CharacterAnimationTargetSnapshot MaterializeTarget(
        string setPackage,
        CharacterAnimationSetKind kind,
        RawAnimationSetEntry entry,
        RawAnimationWeightEntry weight,
        RawAnimationReference reference,
        CharacterAnimationReferenceKind referenceKind,
        int layerIndex)
    {
        var package = UnrealPathUtil.NormalizePackagePath(reference.PackagePath);
        return new CharacterAnimationTargetSnapshot(
            BuildDirectTargetId(
                setPackage,
                entry.ActionTag,
                entry.ContextTags,
                entry.EntryIndex,
                weight.WeightIndex,
                referenceKind,
                layerIndex,
                package),
            referenceKind,
            UnrealPathUtil.NormalizePackagePath(setPackage),
            kind == CharacterAnimationSetKind.Montage ? "TTAnimSet" : "TTLayerSet",
            package,
            package,
            reference.ObjectName,
            reference.ObjectName,
            reference.AssetClass,
            reference.AssetClass,
            entry.EntryIndex,
            weight.WeightIndex,
            layerIndex,
            weight.Weight,
            false,
            "");
    }

    private static RawAnimationReference ResolveObjectReference(UAsset asset, FPackageIndex index)
    {
        if (index.IsNull())
        {
            return new RawAnimationReference("", "", "");
        }
        if (index.IsImport())
        {
            var import = index.ToImport(asset);
            var package = ResolveImportPackage(asset, import.OuterIndex);
            if (string.IsNullOrWhiteSpace(package) &&
                ExtractedPackagePathService.IsContentPackagePath(import.ObjectName.ToString()))
            {
                package = import.ObjectName.ToString();
            }
            return new RawAnimationReference(
                UnrealPathUtil.NormalizePackagePath(package),
                import.ObjectName.ToString(),
                NormalizeAssetClass(import.ClassName.ToString(), import.ObjectName.ToString()));
        }
        if (index.IsExport())
        {
            var export = index.ToExport(asset);
            return new RawAnimationReference(
                UnrealPathUtil.NormalizePackagePath(asset.FolderName?.ToString() ?? ""),
                export.ObjectName.ToString(),
                export.GetExportClassType().Value?.ToString() ?? "");
        }
        return new RawAnimationReference("", "", "");
    }

    private static string ResolveImportPackage(UAsset asset, FPackageIndex index)
    {
        var visited = new HashSet<int>();
        while (!index.IsNull() && index.IsImport() && visited.Add(index.Index))
        {
            var import = index.ToImport(asset);
            var objectName = import.ObjectName.ToString();
            if (import.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase) &&
                ExtractedPackagePathService.IsContentPackagePath(objectName))
            {
                return UnrealPathUtil.NormalizePackagePath(objectName);
            }
            index = import.OuterIndex;
        }
        return "";
    }

    private static string NormalizeAssetClass(string className, string objectName)
    {
        if (className.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase) ||
            objectName.EndsWith("_C", StringComparison.OrdinalIgnoreCase) &&
            className.Contains("ABP_", StringComparison.OrdinalIgnoreCase))
        {
            return "AnimBlueprintGeneratedClass";
        }
        return className;
    }

    private static string NormalizeClassName(string value)
    {
        var normalized = value.Trim();
        var split = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('.'));
        return split >= 0 && split + 1 < normalized.Length
            ? normalized[(split + 1)..]
            : normalized;
    }

    private static string ReadGameplayTag(IReadOnlyList<PropertyData> data, string propertyName)
    {
        var structure = data.OfType<StructPropertyData>()
            .FirstOrDefault(property => PropertyName(property) == propertyName);
        return structure?.Value.OfType<NamePropertyData>()
                   .FirstOrDefault(property => PropertyName(property) == "TagName")?.Value.ToString()
               ?? "";
    }

    private static List<string> ReadGameplayTagContainer(IReadOnlyList<PropertyData> data, string propertyName)
    {
        var structure = data.OfType<StructPropertyData>()
            .FirstOrDefault(property => PropertyName(property) == propertyName);
        return structure?.Value.OfType<GameplayTagContainerPropertyData>()
                   .FirstOrDefault()?.Value
                   .Select(value => value.ToString())
                   .Where(value => !string.IsNullOrWhiteSpace(value))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList()
               ?? new List<string>();
    }

    private static string CategoryForSet(string package)
    {
        var catalog = GameDataService.Instance.FindAnimSet(UnrealPathUtil.AssetName(package));
        if (!string.IsNullOrWhiteSpace(catalog?.Category))
        {
            return catalog.Category;
        }
        var normalized = UnrealPathUtil.NormalizePackagePath(package);
        var slash = normalized.LastIndexOf('/');
        if (slash <= 0)
        {
            return "";
        }
        var parent = normalized[..slash];
        var parentSlash = parent.LastIndexOf('/');
        return parentSlash >= 0 ? parent[(parentSlash + 1)..] : parent;
    }

    private static string OwnerClassFromPackage(string package)
    {
        var leaf = UnrealPathUtil.AssetName(package);
        if (leaf.StartsWith("ABP_", StringComparison.OrdinalIgnoreCase)) return "AnimBlueprintGeneratedClass";
        if (leaf.StartsWith("BS_", StringComparison.OrdinalIgnoreCase)) return "BlendSpace";
        return "AnimationAsset";
    }

    private static string PropertyName(PropertyData property) => property.Name.ToString();

    private static string BuildSetId(
        string compositePackage,
        string donorPackage,
        CharacterAnimationSetKind kind,
        int parentIndex) =>
        $"{kind}|{UnrealPathUtil.NormalizePackagePath(compositePackage)}|{parentIndex}|{UnrealPathUtil.NormalizePackagePath(donorPackage)}";

    private static string BuildSlotId(
        string setPackage,
        CharacterAnimationSetKind kind,
        int entryIndex,
        string actionTag,
        IReadOnlyList<string> contextTags) =>
        $"{kind}|{UnrealPathUtil.NormalizePackagePath(setPackage)}|{entryIndex}|{actionTag}|{StableContexts(contextTags)}";

    private static string BuildDirectTargetId(
        string ownerPackage,
        string actionTag,
        IReadOnlyList<string> contextTags,
        int entryIndex,
        int weightIndex,
        CharacterAnimationReferenceKind referenceKind,
        int layerIndex,
        string originalPackage) =>
        $"{referenceKind}|{UnrealPathUtil.NormalizePackagePath(ownerPackage)}|{actionTag}|{StableContexts(contextTags)}|entry={entryIndex}|weight={weightIndex}|layer={layerIndex}|{UnrealPathUtil.NormalizePackagePath(originalPackage)}";

    private static string BuildLocomotionTargetId(string ownerPackage, string originalPackage) =>
        $"LocomotionSequence|{UnrealPathUtil.NormalizePackagePath(ownerPackage)}|{UnrealPathUtil.NormalizePackagePath(originalPackage)}";

    private static string StableContexts(IEnumerable<string>? contexts) =>
        string.Join(",", (contexts ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static CharacterAnimationSnapshot EmptySnapshot(
        List<CharacterAnimationDiagnostic> diagnostics,
        NativeSuitProject? project = null) =>
        new(
            project?.SlotId ?? "",
            ValueOr(project?.DisplayName ?? "", project?.SlotId ?? "", "No suit open"),
            project?.BaseProfile?.GameplayFamily ?? "",
            "",
            "",
            Array.Empty<CharacterAnimationSetSnapshot>(),
            Array.Empty<CharacterAnimationTargetSnapshot>(),
            Freeze(diagnostics));

    private static CharacterAnimationDiagnostic Error(string code, string message, string package = "") =>
        new(CharacterAnimationDiagnosticSeverity.Error, code, message, package);

    private static CharacterAnimationDiagnostic Warning(string code, string message, string package = "") =>
        new(CharacterAnimationDiagnosticSeverity.Warning, code, message, package);

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static string ValueOr(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

internal sealed record RawAnimationSetEntry(
    int EntryIndex,
    string ActionTag,
    IReadOnlyList<string> ContextTags,
    int ActionLink,
    IReadOnlyList<RawAnimationWeightEntry> Weights);

internal sealed record RawAnimationWeightEntry(
    int WeightIndex,
    int Weight,
    RawAnimationReference? AnimFile,
    IReadOnlyList<RawLayerAnimationReference> LayerAnimations);

internal sealed record RawLayerAnimationReference(
    int LayerIndex,
    RawAnimationReference Reference);

internal sealed record RawParentSetReference(
    int ParentIndex,
    string PackagePath);

internal sealed record RawAnimationReference(
    string PackagePath,
    string ObjectName,
    string AssetClass);
