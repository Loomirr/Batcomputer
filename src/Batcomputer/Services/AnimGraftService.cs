using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Grafts equipment animations into a suit's cloned character anim sets. A
/// suit's MAS_Char/LAS_Char is a TTAnimSet/TTLayerSet whose <c>ParentSetsArray</c>
/// composes categorized building blocks. When a foreign gadget is added, its
/// MAS_Equipment_&lt;X&gt; / LAS_Equipment_&lt;X&gt; block must be appended to that
/// array so the gadget's actions have animations. This only works when the suit
/// owns its anim sets (custom archetype), so callers run it on the mod-local
/// clones and repoint the archetype at them.
/// </summary>
public sealed class AnimGraftService
{
    private const string AnimClassPackage = "/Script/TTAnim";

    public sealed class GraftResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public List<string> Added { get; } = new();
        public List<string> Skipped { get; } = new();
    }

    /// <summary>
    /// Appends parent anim-set references to <paramref name="charSetUassetPath"/>'s
    /// ParentSetsArray. <paramref name="className"/> is "TTLayerSet" for LAS sets or
    /// "TTAnimSet" for MAS sets. Idempotent by package path.
    /// </summary>
    public GraftResult InjectParentSets(string charSetUassetPath, string className, IReadOnlyList<string> parentSetPackages)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null)
            {
                result.Status = "no-mappings";
                result.Error = "A .usmap mappings file is required to edit ParentSetsArray.";
                return result;
            }
            if (!File.Exists(charSetUassetPath))
            {
                result.Status = "missing";
                result.Error = $"Anim set not found: {charSetUassetPath}";
                return result;
            }

            var asset = new UAsset(charSetUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "ParentSetsArray"));
            if (export is null)
            {
                result.Status = "no-parentsets";
                result.Error = "Anim set has no ParentSetsArray property.";
                return result;
            }

            var array = export.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "ParentSetsArray");
            var items = array.Value.ToList();
            var newItems = new List<(PropertyData Item, string ObjectName)>();

            foreach (var raw in parentSetPackages)
            {
                var pkg = UnrealPathUtil.NormalizePackagePath(raw);
                if (string.IsNullOrWhiteSpace(pkg))
                {
                    continue;
                }
                var objName = UnrealPathUtil.AssetName(pkg);

                if (ArrayContainsImport(asset, items, pkg) ||
                    newItems.Any(item => item.ObjectName.Equals(objName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Skipped.Add($"{objName} (already present)");
                    continue;
                }

                var importIndex = EnsureObjectImport(asset, pkg, objName, AnimClassPackage, className);
                newItems.Add((
                    new ObjectPropertyData(MakeName(asset, "0")) { Value = importIndex },
                    objName));
                result.Added.Add(objName);
            }

            if (newItems.Count > 0)
            {
                // PRIORITY: a certified glide/traversal block must precede any native block in
                // the same category, regardless of where a particular cooked build places its
                // *_Playable default container. Other injected sets retain the established rule
                // of sitting before *_Playable. Iterate in reverse so additions sharing an anchor
                // preserve caller order.
                foreach (var addition in newItems.AsEnumerable().Reverse())
                {
                    var existingNames = items.Select(item =>
                            item is ObjectPropertyData objectProperty &&
                            !objectProperty.Value.IsNull() &&
                            objectProperty.Value.IsImport()
                                ? objectProperty.Value.ToImport(asset).ObjectName.ToString()
                                : "")
                        .ToList();
                    var anchor = ParentSetInsertionIndex(existingNames, addition.ObjectName);
                    items.Insert(anchor, addition.Item);
                }

                // Keep the positional element-name labels sequential after the insert.
                for (var i = 0; i < items.Count; i++)
                {
                    items[i].Name = MakeName(asset, i.ToString());
                }

                array.Value = items.ToArray();
                asset.Write(charSetUassetPath);
            }
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    private static int ParentSetInsertionIndex(
        IReadOnlyList<string> existingObjectNames,
        string incomingObjectName)
    {
        var categoryPrefix = incomingObjectName.StartsWith("MAS_Glide_", StringComparison.OrdinalIgnoreCase)
            ? "MAS_Glide_"
            : incomingObjectName.StartsWith("LAS_Traversal_", StringComparison.OrdinalIgnoreCase)
                ? "LAS_Traversal_"
                : "";
        var categoryIndex = -1;
        if (!string.IsNullOrWhiteSpace(categoryPrefix))
        {
            for (var index = 0; index < existingObjectNames.Count; index++)
            {
                if (existingObjectNames[index].StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    categoryIndex = index;
                    break;
                }
            }
        }

        var playableIndex = -1;
        for (var index = 0; index < existingObjectNames.Count; index++)
        {
            if (existingObjectNames[index].EndsWith("_Playable", StringComparison.OrdinalIgnoreCase))
            {
                playableIndex = index;
                break;
            }
        }
        if (categoryIndex >= 0 && playableIndex >= 0)
        {
            return Math.Min(categoryIndex, playableIndex);
        }
        if (categoryIndex >= 0)
        {
            return categoryIndex;
        }
        if (playableIndex >= 0)
        {
            return playableIndex;
        }
        return existingObjectNames.Count;
    }

    internal static int ParentSetInsertionIndexForTest(
        IReadOnlyList<string> existingObjectNames,
        string incomingObjectName) =>
        ParentSetInsertionIndex(existingObjectNames, incomingObjectName);

    /// <summary>
    /// Replaces (or appends) the equipment-definition class at 0-based
    /// <paramref name="slot"/> in a DinnerPawnRuntimeData's <c>Equipment</c> array.
    /// This is the pawn's real runtime loadout - the DCMD only drives the menu.
    /// <paramref name="edPackage"/> is /Game/…/BP_&lt;Gadget&gt;_ED; the class is that
    /// leaf + "_C".
    /// </summary>
    public GraftResult SetEquipmentSlot(string dprdUassetPath, int slot, string edPackage)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null) { result.Status = "no-mappings"; result.Error = "usmap required."; return result; }
            if (!File.Exists(dprdUassetPath)) { result.Status = "missing"; result.Error = $"DPRD not found: {dprdUassetPath}"; return result; }

            var asset = new UAsset(dprdUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "Equipment"));

            ArrayPropertyData array;
            if (export is null)
            {
                // Some families (e.g. ThomasWayne) have the equipment components on the
                // pawn (EquipmentContainer/EquipmentManager) but their DPRD instance
                // never serialized an Equipment array. The DinnerPawnRuntimeData class
                // schema defines Equipment (Batman's DPRD has it), so we can create the
                // array on the main data export and it round-trips; the pawn's equipment
                // components then read the loadout from it.
                export = asset.Exports.OfType<NormalExport>()
                    .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "AbilitySets"))
                    ?? asset.Exports.OfType<NormalExport>().FirstOrDefault();
                if (export is null) { result.Status = "no-export"; result.Error = "DPRD has no usable export."; return result; }

                array = new ArrayPropertyData(MakeName(asset, "Equipment"))
                {
                    ArrayType = MakeName(asset, "ObjectProperty"),
                    Value = Array.Empty<PropertyData>()
                };
                export.Data.Add(array);
                result.Added.Add("created Equipment array (base family had none)");
            }
            else
            {
                array = export.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "Equipment");
            }
            var pkg = UnrealPathUtil.NormalizePackagePath(edPackage);
            var edClass = UnrealPathUtil.AssetName(pkg) + "_C";
            var importIndex = EnsureObjectImport(asset, pkg, edClass, "/Script/Engine", "BlueprintGeneratedClass");

            // A class reference needs its CDO (Default__<Class>) imported too - every
            // native ED reference has one, and instantiating the equipment crashes
            // without it. Its class is the ED class itself (lives in the ED package).
            EnsureObjectImport(asset, pkg, "Default__" + edClass, pkg, edClass);

            var items = array.Value.ToList();

            // Capture the class the old slot pointed at so we can also update the
            // export's preload-dependency list (which references the ED class by
            // FPackageIndex, not the array). Missing this crashes on load.
            FPackageIndex? oldClass = (slot >= 0 && slot < items.Count && items[slot] is ObjectPropertyData oldOp)
                ? oldOp.Value
                : null;

            var entry = new ObjectPropertyData(MakeName(asset, Math.Max(slot, 0).ToString())) { Value = importIndex };
            if (slot >= 0 && slot < items.Count)
            {
                items[slot] = entry;
            }
            else
            {
                items.Add(entry);
            }
            array.Value = items.ToArray();

            // Keep CreateBeforeSerializationDependencies in sync: the new ED class
            // must be create-before-serialized just like the donor gadget's was.
            var deps = export.CreateBeforeSerializationDependencies;
            if (deps is not null)
            {
                var replaced = false;
                if (oldClass is not null && !oldClass.IsNull())
                {
                    for (var i = 0; i < deps.Count; i++)
                    {
                        if (deps[i].Index == oldClass.Index)
                        {
                            deps[i] = importIndex;
                            replaced = true;
                            break;
                        }
                    }
                }
                if (!replaced && deps.All(d => d.Index != importIndex.Index))
                {
                    deps.Add(importIndex);
                }
            }

            asset.Write(dprdUassetPath);
            result.Added.Add($"{edClass}@slot{slot}");
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>Adds a native TtAbilitySet reference to a cloned DPRD.</summary>
    public GraftResult AddAbilitySet(string dprdUassetPath, string abilitySetPackage)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null) { result.Status = "no-mappings"; result.Error = "usmap required."; return result; }
            if (!File.Exists(dprdUassetPath)) { result.Status = "missing"; result.Error = $"DPRD not found: {dprdUassetPath}"; return result; }

            var asset = new UAsset(dprdUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "AbilitySets"));
            if (export is null)
            {
                result.Status = "no-ability-sets";
                result.Error = "DPRD has no AbilitySets array.";
                return result;
            }

            var array = export.Data.OfType<ArrayPropertyData>()
                .First(p => p.Name.ToString() == "AbilitySets");
            var package = UnrealPathUtil.NormalizePackagePath(abilitySetPackage);
            var objectName = UnrealPathUtil.AssetName(package);
            var items = array.Value.ToList();
            var alreadyPresent = items
                .OfType<ObjectPropertyData>()
                .Any(item =>
                    !item.Value.IsNull() &&
                    item.Value.IsImport() &&
                    item.Value.ToImport(asset).ObjectName.ToString()
                        .Equals(objectName, StringComparison.OrdinalIgnoreCase));
            if (alreadyPresent)
            {
                result.Status = "ok";
                result.Skipped.Add($"{objectName} (already present)");
                return result;
            }

            var import = EnsureObjectImport(
                asset,
                package,
                objectName,
                "/Script/TtGameplayAbilities",
                "TtAbilitySet");
            items.Add(new ObjectPropertyData(MakeName(asset, items.Count.ToString()))
            {
                Value = import
            });
            array.Value = items.ToArray();

            var dependencies = export.CreateBeforeSerializationDependencies;
            if (dependencies is not null && dependencies.All(item => item.Index != import.Index))
            {
                dependencies.Add(import);
            }

            asset.Write(dprdUassetPath);
            result.Status = "ok";
            result.Added.Add(objectName);
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>
    /// Replaces a parent set in a composite's ParentSetsArray by object name
    /// (e.g. LAS_Default_Batman → LAS_Default_Catwoman) so the suit uses another
    /// family's animations for that category. Parent sets are object refs, so no
    /// CDO needed. If the donor set isn't present, the replacement is appended unless
    /// <paramref name="requireExisting"/> is true. Certified bridges use the strict mode so a
    /// second category controller can never be added beside an unresolved native controller.
    /// </summary>
    public GraftResult ReplaceParentSet(
        string charSetUassetPath,
        string className,
        string donorSetName,
        string replacementPackage,
        bool requireExisting = false)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null) { result.Status = "no-mappings"; result.Error = "usmap required."; return result; }
            if (!File.Exists(charSetUassetPath)) { result.Status = "missing"; result.Error = $"Set not found: {charSetUassetPath}"; return result; }

            var asset = new UAsset(charSetUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "ParentSetsArray"));
            if (export is null) { result.Status = "no-parentsets"; result.Error = "No ParentSetsArray."; return result; }

            var array = export.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "ParentSetsArray");
            var pkg = UnrealPathUtil.NormalizePackagePath(replacementPackage);
            var newName = UnrealPathUtil.AssetName(pkg);
            var newImport = EnsureObjectImport(asset, pkg, newName, AnimClassPackage, className);

            var items = array.Value.ToList();
            var replaced = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is ObjectPropertyData op && !op.Value.IsNull() && op.Value.IsImport() &&
                    op.Value.ToImport(asset).ObjectName.ToString().Equals(donorSetName, StringComparison.OrdinalIgnoreCase))
                {
                    items[i] = new ObjectPropertyData(op.Name) { Value = newImport };
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                if (requireExisting)
                {
                    result.Status = "missing-donor-parent";
                    result.Error = $"Required parent set '{donorSetName}' is absent; no replacement was written.";
                    return result;
                }
                items.Add(new ObjectPropertyData(MakeName(asset, items.Count.ToString())) { Value = newImport });
            }
            array.Value = items.ToArray();
            asset.Write(charSetUassetPath);
            result.Added.Add($"{donorSetName}→{newName}");
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>
    /// Replaces one semantically-identified reference inside a TTAnimSet/TTLayerSet
    /// <c>AnimSetEntryArray</c>. The saved numeric indices are only a fast path: action tag,
    /// context tags, reference kind, and original package must still match. If a game update makes
    /// the target ambiguous or removes it, this fails closed instead of patching another action.
    /// </summary>
    public GraftResult ReplaceAnimationSlot(string ownerSetUassetPath, AnimationSlotOverride change)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null)
            {
                result.Status = "no-mappings";
                result.Error = "A .usmap mappings file is required to edit an animation slot.";
                return result;
            }
            if (!File.Exists(ownerSetUassetPath))
            {
                result.Status = "missing";
                result.Error = $"Animation set not found: {ownerSetUassetPath}";
                return result;
            }

            var asset = new UAsset(
                ownerSetUassetPath,
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(candidate => candidate.Data.OfType<ArrayPropertyData>()
                    .Any(property => property.Name.ToString().Equals(
                        "AnimSetEntryArray",
                        StringComparison.OrdinalIgnoreCase)));
            var entries = export?.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => property.Name.ToString().Equals(
                    "AnimSetEntryArray",
                    StringComparison.OrdinalIgnoreCase));
            if (export is null || entries is null)
            {
                result.Status = "no-animation-slots";
                result.Error = "The selected animation set has no readable AnimSetEntryArray.";
                return result;
            }

            var matches = FindAnimationSlotReferences(asset, entries, change).ToList();
            if (matches.Count != 1)
            {
                result.Status = matches.Count == 0 ? "slot-not-found" : "ambiguous-slot";
                result.Error = matches.Count == 0
                    ? $"The saved target {DescribeAnimationTarget(change)} no longer matches the active extracted set. Reopen Animation Explorer and choose the slot again."
                    : $"The saved target {DescribeAnimationTarget(change)} matched {matches.Count} references. Batcomputer refused to guess.";
                return result;
            }

            var match = matches[0];
            var replacementPackage = UnrealPathUtil.NormalizePackagePath(change.ReplacementPackage);
            if (string.IsNullOrWhiteSpace(replacementPackage) ||
                !replacementPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "invalid-replacement";
                result.Error = "The replacement animation must have a valid /Game package path.";
                return result;
            }

            var replacementClass = NormalizeAnimationAssetClass(change.ReplacementClass);
            if (string.IsNullOrWhiteSpace(replacementClass))
            {
                result.Status = "invalid-replacement-class";
                result.Error = "The replacement animation class was not identified.";
                return result;
            }
            var replacementObject = UnrealPathUtil.AssetName(replacementPackage);
            if (replacementClass.EndsWith("GeneratedClass", StringComparison.OrdinalIgnoreCase) &&
                !replacementObject.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
            {
                replacementObject += "_C";
            }

            var newImport = EnsureObjectImport(
                asset,
                replacementPackage,
                replacementObject,
                "/Script/Engine",
                replacementClass);
            match.Replace(newImport);

            // Keep the old dependency because another context row may still use it. The new asset
            // must be create-before-serialization just like the donor reference.
            export.CreateBeforeSerializationDependencies ??= new List<FPackageIndex>();
            if (export.CreateBeforeSerializationDependencies.All(dependency => dependency.Index != newImport.Index))
            {
                export.CreateBeforeSerializationDependencies.Add(newImport);
            }

            asset.Write(ownerSetUassetPath);
            result.Status = "ok";
            result.Added.Add(
                $"{DescribeAnimationTarget(change)}: {UnrealPathUtil.AssetName(change.DonorPackage)}→{replacementObject}");
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    private sealed record AnimationSlotReference(
        int EntryIndex,
        int VariantIndex,
        int ReferenceIndex,
        FPackageIndex Current,
        Action<FPackageIndex> Replace);

    private static IEnumerable<AnimationSlotReference> FindAnimationSlotReferences(
        UAsset asset,
        ArrayPropertyData entries,
        AnimationSlotOverride target)
    {
        var expectedPackage = UnrealPathUtil.NormalizePackagePath(target.DonorPackage);
        var expectedContexts = NormalizeTags(target.ContextTags);
        var candidates = new List<AnimationSlotReference>();

        foreach (var (entryProperty, entryIndex) in entries.Value.Select((value, index) => (value, index)))
        {
            if (entryProperty is not StructPropertyData entry ||
                !AnimationEntryMatches(entry, target.ActionTag, expectedContexts))
            {
                continue;
            }

            var variants = entry.Value.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => property.Name.ToString().Equals(
                    "AnimAndWeightsArray",
                    StringComparison.OrdinalIgnoreCase));
            if (variants is null)
            {
                continue;
            }

            foreach (var (variantProperty, variantIndex) in variants.Value.Select((value, index) => (value, index)))
            {
                if (variantProperty is not StructPropertyData variant)
                {
                    continue;
                }

                if (target.ReferenceKind.Equals("AnimFile", StringComparison.OrdinalIgnoreCase))
                {
                    var animation = variant.Value.OfType<ObjectPropertyData>()
                        .FirstOrDefault(property => property.Name.ToString().Equals(
                            "AnimFile",
                            StringComparison.OrdinalIgnoreCase));
                    if (animation is not null &&
                        ObjectPackage(asset, animation.Value).Equals(expectedPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(new AnimationSlotReference(
                            entryIndex,
                            variantIndex,
                            0,
                            animation.Value,
                            replacement => animation.Value = replacement));
                    }
                    continue;
                }

                if (!target.ReferenceKind.Equals("LayerAnim", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var layers = variant.Value.OfType<ArrayPropertyData>()
                    .FirstOrDefault(property => property.Name.ToString().Equals(
                        "LayerAnimArray",
                        StringComparison.OrdinalIgnoreCase));
                if (layers is null)
                {
                    continue;
                }
                foreach (var (layerProperty, referenceIndex) in layers.Value.Select((value, index) => (value, index)))
                {
                    if (layerProperty is not ObjectPropertyData layer ||
                        !ObjectPackage(asset, layer.Value).Equals(expectedPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var capturedIndex = referenceIndex;
                    candidates.Add(new AnimationSlotReference(
                        entryIndex,
                        variantIndex,
                        capturedIndex,
                        layer.Value,
                        replacement =>
                        {
                            var values = layers.Value.ToArray();
                            values[capturedIndex] = new ObjectPropertyData(layer.Name) { Value = replacement };
                            layers.Value = values;
                        }));
                }
            }
        }

        // Prefer the exact observed location, but only after the semantic key and donor package
        // above have been validated. Fall back to one unique semantic match after a data refresh.
        var exact = candidates.Where(candidate =>
                candidate.EntryIndex == target.EntryIndex &&
                candidate.VariantIndex == target.VariantIndex &&
                candidate.ReferenceIndex == Math.Max(0, target.ReferenceIndex))
            .ToList();
        return exact.Count == 1 ? exact : candidates;
    }

    internal static int ReplaceAnimationSlotReferenceForTest(
        UAsset asset,
        ArrayPropertyData entries,
        AnimationSlotOverride target,
        FPackageIndex replacement)
    {
        var matches = FindAnimationSlotReferences(asset, entries, target).ToList();
        if (matches.Count == 1)
        {
            matches[0].Replace(replacement);
        }
        return matches.Count;
    }

    private static bool AnimationEntryMatches(
        StructPropertyData entry,
        string expectedAction,
        IReadOnlyList<string> expectedContexts)
    {
        var action = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString().Equals(
                "ActionTag",
                StringComparison.OrdinalIgnoreCase))?
            .Value.OfType<NamePropertyData>()
            .FirstOrDefault(property => property.Name.ToString().Equals(
                "TagName",
                StringComparison.OrdinalIgnoreCase))?
            .Value.ToString() ?? "";
        if (!action.Equals(expectedAction, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var contexts = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString().Equals(
                "ContextTags",
                StringComparison.OrdinalIgnoreCase))?
            .Value.OfType<GameplayTagContainerPropertyData>()
            .FirstOrDefault()?
            .Value.Select(tag => tag.ToString())
            .ToList() ?? [];
        return NormalizeTags(contexts).SequenceEqual(expectedContexts, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeTags(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string ObjectPackage(UAsset asset, FPackageIndex index)
    {
        var seen = new HashSet<int>();
        while (!index.IsNull() && index.IsImport() && seen.Add(index.Index))
        {
            var import = index.ToImport(asset);
            if (import.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                return UnrealPathUtil.NormalizePackagePath(import.ObjectName.ToString());
            }
            index = import.OuterIndex;
        }
        return "";
    }

    private static string NormalizeAnimationAssetClass(string? value)
    {
        var normalized = value?.Trim() ?? "";
        var slash = normalized.LastIndexOf('/');
        var dot = normalized.LastIndexOf('.');
        var split = Math.Max(slash, dot);
        return split >= 0 && split + 1 < normalized.Length ? normalized[(split + 1)..] : normalized;
    }

    private static string DescribeAnimationTarget(AnimationSlotOverride target)
    {
        var contexts = NormalizeTags(target.ContextTags);
        return contexts.Count == 0
            ? $"'{target.ActionTag}'"
            : $"'{target.ActionTag}' [{string.Join(", ", contexts)}]";
    }

    /// <summary>
    /// Grants extra gameplay abilities in a TtAbilitySet by appending entries to
    /// GrantedGameplayAbilities. Each entry is an FTtAbilitySet_GameplayAbility
    /// struct {Ability(class), AbilityLevel, InputTag}; we clone an existing
    /// GA_Item template entry and swap its Ability class, so the LAM/level/tag
    /// metadata matches. Used to grant a foreign gadget's GA_Item_* visual
    /// abilities so its held/carried mesh appears. Class refs get CDO + preload dep.
    /// </summary>
    public GraftResult AddGrantedAbilities(string abilitySetUassetPath, IReadOnlyList<string> abilityPackages)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null) { result.Status = "no-mappings"; result.Error = "usmap required."; return result; }
            if (!File.Exists(abilitySetUassetPath)) { result.Status = "missing"; result.Error = $"AbilitySet not found: {abilitySetUassetPath}"; return result; }

            var asset = new UAsset(abilitySetUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "GrantedGameplayAbilities"));
            if (export is null) { result.Status = "no-abilities"; result.Error = "AbilitySet has no GrantedGameplayAbilities."; return result; }

            var array = export.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "GrantedGameplayAbilities");
            var items = array.Value.ToList();

            // Template: an existing entry (prefer a GA_Item one for matching level/tag).
            var template = items.OfType<StructPropertyData>()
                .FirstOrDefault(s => AbilityClassName(asset, s)?.Contains("GA_Item_", StringComparison.OrdinalIgnoreCase) == true)
                ?? items.OfType<StructPropertyData>().FirstOrDefault();
            if (template is null) { result.Status = "no-template"; result.Error = "No struct entry to clone."; return result; }

            var deps = export.CreateBeforeSerializationDependencies;
            foreach (var raw in abilityPackages)
            {
                var pkg = UnrealPathUtil.NormalizePackagePath(raw);
                var gaClass = UnrealPathUtil.AssetName(pkg) + "_C";
                if (items.OfType<StructPropertyData>().Any(s => string.Equals(AbilityClassName(asset, s), gaClass, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Skipped.Add($"{gaClass} (already granted)");
                    continue;
                }

                var gaImport = EnsureObjectImport(asset, pkg, gaClass, "/Script/Engine", "BlueprintGeneratedClass");
                EnsureObjectImport(asset, pkg, "Default__" + gaClass, pkg, gaClass); // CDO
                if (deps is not null && deps.All(d => d.Index != gaImport.Index))
                {
                    deps.Add(gaImport);
                }

                // Clone the template struct, swap only its Ability field.
                var fields = new List<PropertyData>();
                foreach (var f in template.Value)
                {
                    if (f.Name.ToString() == "Ability")
                    {
                        fields.Add(new ObjectPropertyData(f.Name) { Value = gaImport });
                    }
                    else
                    {
                        fields.Add(f);
                    }
                }
                items.Add(new StructPropertyData(template.Name)
                {
                    StructType = template.StructType,
                    SerializeNone = template.SerializeNone,
                    StructGUID = template.StructGUID,
                    Value = fields,
                });
                result.Added.Add(gaClass);
            }

            if (result.Added.Count > 0)
            {
                array.Value = items.ToArray();
                asset.Write(abilitySetUassetPath);
            }
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>Equipment adapter (Stage 1): give a boss/NPC gadget the PLAYER "draw" ability it
    /// lacks. Boss gadgets (FreezeGun/MachineGun/RocketLauncher) ship a TtEquipmentDefinition
    /// (BP_&lt;Gadget&gt;_ED) with QuickFire/AimAndFire abilities but NO <c>GetGadgetOutAbility</c> -
    /// so slotting one on a hero never "draws" it and nothing happens. This sets GetGadgetOutAbility
    /// on the (already-cloned, mod-local) ED's CDO to the generic <c>GA_GetGadgetOut</c> (the base
    /// class every hero gadget's own GetGadgetOut derives from; it draws whatever the ED's
    /// InstanceType/ActorsToSpawn specify - the gadget already ships BP_&lt;Gadget&gt;_Inst/_Weapon).
    /// Mirrors SetEquipmentSlot's import + object-property + preload-dependency pattern.</summary>
    public GraftResult SetGadgetDrawScaffolding(string edUassetPath, string drawAbilityPackage)
    {
        var result = new GraftResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null) { result.Status = "no-mappings"; result.Error = "usmap required."; return result; }
            if (!File.Exists(edUassetPath)) { result.Status = "missing"; result.Error = $"ED not found: {edUassetPath}"; return result; }

            var asset = new UAsset(edUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);

            // The ED's CDO carries the ability slots (Default__BP_<Gadget>_ED_C). Find it by the
            // AimAndFireAbility property that every gadget ED defines.
            var cdo = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "AimAndFireAbility"
                                                 || p.Name.ToString() == "QuickFireAbility"));
            if (cdo is null) { result.Status = "no-cdo"; result.Error = "ED has no equipment-definition CDO (AimAndFire/QuickFire)."; return result; }

            var existing = cdo.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name.ToString() == "GetGadgetOutAbility");
            if (existing is not null && !existing.Value.IsNull())
            {
                result.Skipped.Add("GetGadgetOutAbility already set");
                result.Status = "ok";
                return result;
            }

            var pkg = UnrealPathUtil.NormalizePackagePath(drawAbilityPackage);
            var gaClass = UnrealPathUtil.AssetName(pkg) + "_C";
            var gaImport = EnsureObjectImport(asset, pkg, gaClass, "/Script/Engine", "BlueprintGeneratedClass");
            EnsureObjectImport(asset, pkg, "Default__" + gaClass, pkg, gaClass); // CDO

            if (existing is not null)
            {
                existing.Value = gaImport;
            }
            else
            {
                cdo.Data.Add(new ObjectPropertyData(MakeName(asset, "GetGadgetOutAbility")) { Value = gaImport });
            }

            // The draw-ability class must be create-before-serialized like the other ability refs.
            var deps = cdo.CreateBeforeSerializationDependencies;
            if (deps is not null && deps.All(d => d.Index != gaImport.Index))
            {
                deps.Add(gaImport);
            }

            asset.Write(edUassetPath);
            result.Added.Add($"GetGadgetOutAbility -> {gaClass}");
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>True if a gadget's base TtEquipmentDefinition (BP_&lt;Gadget&gt;_ED) has NO
    /// GetGadgetOutAbility - i.e. it's a boss/NPC gadget that needs the draw-ability adapter to be
    /// usable by a player. Returns false (no adapter needed) for hero gadgets that already draw.</summary>
    public bool EquipmentNeedsDrawAdapter(string edUassetPath)
    {
        try
        {
            if (!File.Exists(edUassetPath)) return false;
            var mappings = LoadMappings();
            var asset = new UAsset(edUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var cdo = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "AimAndFireAbility"
                                                 || p.Name.ToString() == "QuickFireAbility"));
            if (cdo is null) return false;
            var draw = cdo.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name.ToString() == "GetGadgetOutAbility");
            return draw is null || draw.Value.IsNull();
        }
        catch
        {
            return false;
        }
    }

    private static string? AbilityClassName(UAsset asset, StructPropertyData entry)
    {
        var ability = entry.Value.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Ability");
        if (ability is null || ability.Value.IsNull() || !ability.Value.IsImport())
        {
            return null;
        }
        return ability.Value.ToImport(asset).ObjectName.ToString();
    }

    private static bool ArrayContainsImport(UAsset asset, List<PropertyData> items, string packagePath)
    {
        foreach (var item in items)
        {
            if (item is ObjectPropertyData op && !op.Value.IsNull() && op.Value.IsImport())
            {
                var import = op.Value.ToImport(asset);
                var pkgIndex = import.OuterIndex;
                if (pkgIndex.IsImport())
                {
                    var pkg = pkgIndex.ToImport(asset).ObjectName.ToString();
                    if (pkg.Equals(packagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // --- Import helpers (mirrors MaterialReplaceService's proven pattern) ---

    private static FPackageIndex EnsureObjectImport(UAsset asset, string packagePath, string objectName, string classPackage, string className)
    {
        var packageImport = EnsurePackageImport(asset, packagePath);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString() == objectName &&
                import.OuterIndex.Index == packageImport.Index &&
                import.ClassName.ToString() == className)
            {
                return FromImportNumber(i + 1);
            }
        }
        return asset.AddImport(new Import(classPackage, className, packageImport, objectName, false, asset));
    }

    private static FPackageIndex EnsurePackageImport(UAsset asset, string packagePath)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ClassName.ToString() == "Package" && import.ObjectName.ToString() == packagePath)
            {
                return FromImportNumber(i + 1);
            }
        }
        return asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), packagePath, false, asset));
    }

    private static FPackageIndex FromImportNumber(int importNumber) =>
        importNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromImport(importNumber - 1);

    private static FName MakeName(UAsset asset, string value) => FName.FromString(asset, value);

    private static Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }
}
