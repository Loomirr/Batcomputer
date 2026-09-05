using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Reads and edits the two cooked assets that define a character's runtime ability loadout:
/// a <c>DinnerPawnRuntimeData</c> (ordered <c>AbilitySets</c>) and a
/// <c>TtAbilitySet</c> (granted abilities/effects/attributes/data/cues).
///
/// Mutation methods deliberately reject assets under the active extracted-content root. Callers
/// must clone a donor into their staged mod tree first; base-game and DLC assets remain read-only.
/// Imports are never deleted or renumbered. Removed references merely lose their preload edge,
/// which is the safe Unreal cooked-asset pattern used by the rest of Batcomputer.
/// </summary>
public sealed class AbilityAssetMutationService
{
    private const string AbilitySetClassPackage = "/Script/TtGameplayAbilities";
    private const string AbilitySetClassName = "TtAbilitySet";

    public sealed class MutationResult
    {
        public bool Success { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public List<string> Changes { get; init; } = new();

        internal static MutationResult Ok(IEnumerable<string>? changes = null) => new()
        {
            Success = true,
            Status = "ok",
            Changes = changes?.ToList() ?? new List<string>(),
        };

        internal static MutationResult Fail(string status, string error) => new()
        {
            Success = false,
            Status = status,
            Error = error,
        };
    }

    public sealed class DprdAbilitySetInspection
    {
        public bool Success { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public List<DprdAbilitySetReference> AbilitySets { get; init; } = new();
    }

    public sealed class DprdAbilitySetReference
    {
        public int Index { get; init; }
        public string PackagePath { get; init; } = "";
        public string ObjectName { get; init; } = "";
    }

    public sealed class DprdEquipmentInspection
    {
        public bool Success { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        /// <summary>
        /// Ordered runtime Equipment entries. Null slots are retained with an empty package path so
        /// callers never shift a later gadget into a different slot while comparing loadouts.
        /// </summary>
        public List<DprdEquipmentReference> Equipment { get; init; } = new();
    }

    public sealed class DprdEquipmentReference
    {
        public int Index { get; init; }
        public string PackagePath { get; init; } = "";
        public string ObjectName { get; init; } = "";
        public bool IsNull { get; init; }
    }

    public sealed class AbilitySetInspection
    {
        public bool Success { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public List<AbilityGrantReference> GameplayAbilities { get; init; } = new();
        public List<AbilityGrantReference> GameplayEffects { get; init; } = new();
        public List<AbilityGrantReference> Attributes { get; init; } = new();
        public List<AbilityGrantReference> GameplayData { get; init; } = new();
        public List<AbilityGrantReference> ActorGameplayCues { get; init; } = new();
        public List<AbilityGrantReference> StaticGameplayCues { get; init; } = new();
        public AbilityGrantReference? AccessoryAnimGraphClass { get; init; }
    }

    public sealed class AbilityGrantReference
    {
        public int Index { get; init; }
        public string Kind { get; init; } = "";
        public string PackagePath { get; init; } = "";
        public string ObjectName { get; init; } = "";
        public string ClassName { get; init; } = "";
        public int? AbilityLevel { get; init; }
        public float? EffectLevel { get; init; }
        public string InputTag { get; init; } = "";
        public bool IsNativeClass { get; init; }
    }

    public enum GameplayAbilityEditKind
    {
        Add,
        Remove,
        Replace,
    }

    /// <summary>
    /// One exact change to <c>GrantedGameplayAbilities</c>. Package paths identify the Blueprint
    /// package (without <c>_C</c>). Null level/tag overrides preserve metadata from the replaced
    /// entry, selected source entry, or local template in that order. An empty non-null InputTag
    /// explicitly writes an empty GameplayTag.
    /// </summary>
    public sealed class GameplayAbilityEdit
    {
        public GameplayAbilityEditKind Kind { get; init; }
        public string TargetPackagePath { get; init; } = "";
        public string ReplacementPackagePath { get; init; } = "";
        public int? AbilityLevelOverride { get; init; }
        public string? InputTagOverride { get; init; }
        public int? InsertIndex { get; init; }

        // Optional real donor entry whose level/InputTag/struct shape should seed an Add.
        public string SourceAbilitySetUassetPath { get; init; } = "";
        public string SourceAbilityPackagePath { get; init; } = "";
    }

    /// <summary>
    /// Adds one exact class to <c>GrantedGameplayEffects</c>. A source AbilitySet is required only
    /// when the target has no existing effect entry whose cooked struct metadata can be reused.
    /// </summary>
    public sealed class GameplayEffectAddition
    {
        public string PackagePath { get; init; } = "";
        public float? EffectLevelOverride { get; init; }
        public string SourceAbilitySetUassetPath { get; init; } = "";
        public string SourceEffectPackagePath { get; init; } = "";
    }

    /// <summary>Lists the DPRD's ordered, exact AbilitySet package references.</summary>
    public DprdAbilitySetInspection InspectDprdAbilitySets(string dprdUassetPath)
    {
        try
        {
            var asset = LoadAsset(dprdUassetPath);
            var (_, array) = FindDprdAbilitySets(asset);
            var entries = new List<DprdAbilitySetReference>();
            for (var index = 0; index < array.Value.Length; index++)
            {
                if (array.Value[index] is not ObjectPropertyData property ||
                    property.Value.IsNull() ||
                    !property.Value.IsImport())
                {
                    return new DprdAbilitySetInspection
                    {
                        Status = "invalid-ability-set-entry",
                        Error = $"AbilitySets[{index}] is not an imported TtAbilitySet reference.",
                    };
                }

                var resolved = ResolveObjectReference(asset, property.Value);
                entries.Add(new DprdAbilitySetReference
                {
                    Index = index,
                    PackagePath = resolved.PackagePath,
                    ObjectName = resolved.ObjectName,
                });
            }

            return new DprdAbilitySetInspection
            {
                Success = true,
                Status = "ok",
                AbilitySets = entries,
            };
        }
        catch (Exception ex)
        {
            return new DprdAbilitySetInspection { Status = "error", Error = ex.ToString() };
        }
    }

    /// <summary>
    /// Reads the DPRD's authoritative runtime Equipment array. This deliberately does not inspect
    /// the DCMD EquipmentList, which is menu metadata and can disagree with the pawn loadout.
    /// </summary>
    public DprdEquipmentInspection InspectDprdEquipment(string dprdUassetPath)
    {
        try
        {
            var asset = LoadAsset(dprdUassetPath);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(candidate => candidate.Data.Any(property =>
                    PropertyNamed(property, "Equipment")));
            if (export is null)
            {
                // An omitted property is an authored empty runtime loadout.
                return new DprdEquipmentInspection
                {
                    Success = true,
                    Status = "ok",
                };
            }

            var array = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "Equipment"));
            if (array is null)
            {
                return new DprdEquipmentInspection
                {
                    Status = "invalid-equipment-property",
                    Error = "The DPRD Equipment property is not an object array.",
                };
            }

            var entries = new List<DprdEquipmentReference>();
            for (var index = 0; index < array.Value.Length; index++)
            {
                if (array.Value[index] is not ObjectPropertyData property)
                {
                    return new DprdEquipmentInspection
                    {
                        Status = "invalid-equipment-entry",
                        Error = $"Equipment[{index}] is not an object reference.",
                    };
                }
                if (property.Value.IsNull())
                {
                    entries.Add(new DprdEquipmentReference { Index = index, IsNull = true });
                    continue;
                }
                if (!property.Value.IsImport())
                {
                    return new DprdEquipmentInspection
                    {
                        Status = "invalid-equipment-entry",
                        Error = $"Equipment[{index}] is not an imported equipment-definition class.",
                    };
                }

                var resolved = ResolveObjectReference(asset, property.Value);
                entries.Add(new DprdEquipmentReference
                {
                    Index = index,
                    PackagePath = resolved.PackagePath,
                    ObjectName = resolved.ObjectName,
                });
            }
            return new DprdEquipmentInspection
            {
                Success = true,
                Status = "ok",
                Equipment = entries,
            };
        }
        catch (Exception ex)
        {
            return new DprdEquipmentInspection { Status = "error", Error = ex.ToString() };
        }
    }

    /// <summary>Atomically replaces the DPRD's complete ordered AbilitySets list.</summary>
    public MutationResult SetDprdAbilitySets(
        string stagedDprdUassetPath,
        IReadOnlyList<string> orderedAbilitySetPackages)
    {
        var writable = EnsureStagedWritableAsset(stagedDprdUassetPath);
        if (writable is not null)
        {
            return writable;
        }

        try
        {
            var packages = NormalizeDistinctPackages(orderedAbilitySetPackages, out var packageError);
            if (packageError is not null)
            {
                return MutationResult.Fail("invalid-packages", packageError);
            }

            var asset = LoadAsset(stagedDprdUassetPath);
            var (export, array) = FindDprdAbilitySets(asset);
            var oldImports = array.Value
                .OfType<ObjectPropertyData>()
                .Where(property => !property.Value.IsNull() && property.Value.IsImport())
                .Select(property => property.Value)
                .ToList();
            var newImports = packages
                .Select(package => EnsureObjectImport(
                    asset,
                    package,
                    UnrealPathUtil.AssetName(package),
                    AbilitySetClassPackage,
                    AbilitySetClassName))
                .ToList();

            array.Value = newImports
                .Select((import, index) => (PropertyData)new ObjectPropertyData(MakeName(asset, index.ToString()))
                {
                    Value = import,
                })
                .ToArray();
            SyncPreloadDependencies(asset, export, oldImports, newImports);
            asset.Write(stagedDprdUassetPath);

            var verify = InspectDprdAbilitySets(stagedDprdUassetPath);
            if (!verify.Success ||
                !verify.AbilitySets.Select(reference => reference.PackagePath)
                    .SequenceEqual(packages, StringComparer.OrdinalIgnoreCase))
            {
                return MutationResult.Fail(
                    "verification-failed",
                    verify.Error ?? "The written DPRD did not reload with the requested ordered AbilitySets list.");
            }

            return MutationResult.Ok(new[]
            {
                $"AbilitySets = [{string.Join(", ", packages)}]",
            });
        }
        catch (Exception ex)
        {
            return MutationResult.Fail("error", ex.ToString());
        }
    }

    public MutationResult AddDprdAbilitySet(
        string stagedDprdUassetPath,
        string abilitySetPackage,
        int? insertIndex = null)
    {
        var inspection = InspectDprdAbilitySets(stagedDprdUassetPath);
        if (!inspection.Success)
        {
            return MutationResult.Fail(inspection.Status, inspection.Error ?? "Unable to inspect DPRD.");
        }

        var package = UnrealPathUtil.NormalizePackagePath(abilitySetPackage);
        if (inspection.AbilitySets.Any(reference => PackageEquals(reference.PackagePath, package)))
        {
            return MutationResult.Ok(new[] { $"{package} already present" });
        }

        var ordered = inspection.AbilitySets.Select(reference => reference.PackagePath).ToList();
        var index = Math.Clamp(insertIndex ?? ordered.Count, 0, ordered.Count);
        ordered.Insert(index, package);
        return SetDprdAbilitySets(stagedDprdUassetPath, ordered);
    }

    public MutationResult RemoveDprdAbilitySet(string stagedDprdUassetPath, string abilitySetPackage)
    {
        var inspection = InspectDprdAbilitySets(stagedDprdUassetPath);
        if (!inspection.Success)
        {
            return MutationResult.Fail(inspection.Status, inspection.Error ?? "Unable to inspect DPRD.");
        }

        var package = UnrealPathUtil.NormalizePackagePath(abilitySetPackage);
        var ordered = inspection.AbilitySets
            .Where(reference => !PackageEquals(reference.PackagePath, package))
            .Select(reference => reference.PackagePath)
            .ToList();
        if (ordered.Count == inspection.AbilitySets.Count)
        {
            return MutationResult.Ok(new[] { $"{package} was not present" });
        }
        return SetDprdAbilitySets(stagedDprdUassetPath, ordered);
    }

    public MutationResult ReplaceDprdAbilitySet(
        string stagedDprdUassetPath,
        string targetAbilitySetPackage,
        string replacementAbilitySetPackage)
    {
        var inspection = InspectDprdAbilitySets(stagedDprdUassetPath);
        if (!inspection.Success)
        {
            return MutationResult.Fail(inspection.Status, inspection.Error ?? "Unable to inspect DPRD.");
        }

        var target = UnrealPathUtil.NormalizePackagePath(targetAbilitySetPackage);
        var replacement = UnrealPathUtil.NormalizePackagePath(replacementAbilitySetPackage);
        var matches = inspection.AbilitySets.Where(reference => PackageEquals(reference.PackagePath, target)).ToList();
        if (matches.Count == 0)
        {
            return MutationResult.Fail("target-not-found", $"DPRD does not reference '{target}'.");
        }
        if (matches.Count > 1)
        {
            return MutationResult.Fail("ambiguous-target", $"DPRD references '{target}' more than once.");
        }
        if (!PackageEquals(target, replacement) &&
            inspection.AbilitySets.Any(reference => PackageEquals(reference.PackagePath, replacement)))
        {
            return MutationResult.Fail("duplicate-replacement", $"DPRD already references '{replacement}'.");
        }

        var ordered = inspection.AbilitySets.Select(reference => reference.PackagePath).ToList();
        ordered[matches[0].Index] = replacement;
        return SetDprdAbilitySets(stagedDprdUassetPath, ordered);
    }

    /// <summary>Reads every supported grant category from a TtAbilitySet.</summary>
    public AbilitySetInspection InspectAbilitySet(string abilitySetUassetPath)
    {
        try
        {
            var asset = LoadAsset(abilitySetUassetPath);
            var export = FindAbilitySetExport(asset);
            return new AbilitySetInspection
            {
                Success = true,
                Status = "ok",
                GameplayAbilities = ReadGrantArray(
                    asset, export, "GrantedGameplayAbilities", "Ability", "Gameplay ability"),
                GameplayEffects = ReadGrantArray(
                    asset, export, "GrantedGameplayEffects", "GameplayEffect", "Gameplay effect"),
                Attributes = ReadGrantArray(
                    asset, export, "GrantedAttributes", "AttributeSet", "Attribute set"),
                GameplayData = ReadGrantArray(
                    asset, export, "GrantedGameplayData", "GameplayDataSet", "Gameplay data"),
                ActorGameplayCues = ReadGrantArray(
                    asset, export, "GrantedGameplayCueNotifyActorData", "GameplayCueNotifyActor", "Actor gameplay cue"),
                StaticGameplayCues = ReadGrantArray(
                    asset, export, "GrantedGameplayCueNotifyStaticData", "GameplayCueNotifyStatic", "Static gameplay cue"),
                AccessoryAnimGraphClass = ReadAccessoryAnimGraph(asset, export),
            };
        }
        catch (Exception ex)
        {
            return new AbilitySetInspection { Status = "error", Error = ex.ToString() };
        }
    }

    /// <summary>
    /// Applies an ordered batch of exact add/remove/replace edits to GrantedGameplayAbilities and
    /// writes once. Existing/replaced metadata is preserved unless an override is supplied.
    /// </summary>
    public MutationResult ApplyGameplayAbilityEdits(
        string stagedAbilitySetUassetPath,
        IReadOnlyList<GameplayAbilityEdit> edits)
    {
        var writable = EnsureStagedWritableAsset(stagedAbilitySetUassetPath);
        if (writable is not null)
        {
            return writable;
        }

        try
        {
            var asset = LoadAsset(stagedAbilitySetUassetPath);
            var export = FindAbilitySetExport(asset);
            var array = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "GrantedGameplayAbilities"));
            if (array is null)
            {
                array = new ArrayPropertyData(MakeName(asset, "GrantedGameplayAbilities"))
                {
                    ArrayType = MakeName(asset, "StructProperty"),
                    Value = Array.Empty<PropertyData>(),
                };
                export.Data.Add(array);
            }

            var items = array.Value.ToList();
            if (items.Any(item => item is not StructPropertyData))
            {
                return MutationResult.Fail(
                    "invalid-grant-array",
                    "GrantedGameplayAbilities contains a non-struct entry and was not modified.");
            }

            var originalImports = AbilityImports(asset, items).ToList();
            var changes = new List<string>();
            foreach (var edit in edits)
            {
                var targetPackage = UnrealPathUtil.NormalizePackagePath(edit.TargetPackagePath);
                var replacementPackage = UnrealPathUtil.NormalizePackagePath(edit.ReplacementPackagePath);
                if (edit.Kind == GameplayAbilityEditKind.Add)
                {
                    var addedPackage = !string.IsNullOrWhiteSpace(replacementPackage)
                        ? replacementPackage
                        : targetPackage;
                    if (!IsGamePackage(addedPackage))
                    {
                        return MutationResult.Fail("invalid-ability-package", $"'{addedPackage}' is not a supported game or DLC content package.");
                    }
                    if (FindAbilityEntries(asset, items, addedPackage).Count > 0)
                    {
                        changes.Add($"{addedPackage} already granted");
                        continue;
                    }

                    var metadata = ReadSelectedTemplateMetadata(edit) ??
                                   ReadTemplateMetadata(asset, items.OfType<StructPropertyData>().FirstOrDefault());
                    if (metadata is null)
                    {
                        return MutationResult.Fail(
                            "no-template",
                            "Adding the first gameplay ability requires SourceAbilitySetUassetPath and SourceAbilityPackagePath.");
                    }

                    var import = EnsureBlueprintClassImport(asset, addedPackage);
                    var template = items.OfType<StructPropertyData>().FirstOrDefault();
                    var entry = CreateGameplayAbilityEntry(
                        asset,
                        template,
                        metadata,
                        import,
                        edit.AbilityLevelOverride ?? metadata.AbilityLevel,
                        edit.InputTagOverride ?? metadata.InputTag);
                    var insertAt = Math.Clamp(edit.InsertIndex ?? items.Count, 0, items.Count);
                    items.Insert(insertAt, entry);
                    changes.Add($"added {addedPackage} at {insertAt}");
                    continue;
                }

                var matches = FindAbilityEntries(asset, items, targetPackage);
                if (matches.Count == 0)
                {
                    if (edit.Kind == GameplayAbilityEditKind.Remove)
                    {
                        changes.Add($"{targetPackage} was not granted");
                        continue;
                    }
                    return MutationResult.Fail("target-not-found", $"AbilitySet does not grant '{targetPackage}'.");
                }
                if (matches.Count > 1)
                {
                    return MutationResult.Fail("ambiguous-target", $"AbilitySet grants '{targetPackage}' more than once.");
                }

                var match = matches[0];
                if (edit.Kind == GameplayAbilityEditKind.Remove)
                {
                    items.RemoveAt(match.Index);
                    changes.Add($"removed {targetPackage}");
                    continue;
                }

                if (!IsGamePackage(replacementPackage))
                {
                    return MutationResult.Fail(
                        "invalid-ability-package",
                        $"'{replacementPackage}' is not a supported game or DLC content package.");
                }
                if (!PackageEquals(targetPackage, replacementPackage) &&
                    FindAbilityEntries(asset, items, replacementPackage).Count > 0)
                {
                    return MutationResult.Fail(
                        "duplicate-replacement",
                        $"AbilitySet already grants '{replacementPackage}'.");
                }

                var existingMetadata = ReadTemplateMetadata(asset, match.Entry)!;
                var replacementImport = EnsureBlueprintClassImport(asset, replacementPackage);
                SetAbilityReference(match.Entry, replacementImport);
                SetAbilityLevel(match.Entry, edit.AbilityLevelOverride ?? existingMetadata.AbilityLevel);
                SetInputTag(asset, match.Entry, edit.InputTagOverride ?? existingMetadata.InputTag);
                changes.Add($"replaced {targetPackage} with {replacementPackage}");
            }

            Renumber(items, asset);
            array.Value = items.ToArray();
            var currentImports = AbilityImports(asset, items).ToList();
            SyncPreloadDependencies(asset, export, originalImports, currentImports);
            asset.Write(stagedAbilitySetUassetPath);

            var verify = InspectAbilitySet(stagedAbilitySetUassetPath);
            if (!verify.Success)
            {
                return MutationResult.Fail(
                    "verification-failed",
                    verify.Error ?? "The written AbilitySet could not be reloaded.");
            }
            return MutationResult.Ok(changes);
        }
        catch (Exception ex)
        {
            return MutationResult.Fail("error", ex.ToString());
        }
    }

    /// <summary>
    /// Adds combat-type or other GameplayEffects without replacing the character's complete
    /// AbilitySet. This is the suit-local bridge used by coordinated fighting-style presets.
    /// </summary>
    public MutationResult AddGameplayEffects(
        string stagedAbilitySetUassetPath,
        IReadOnlyList<GameplayEffectAddition> additions)
    {
        var writable = EnsureStagedWritableAsset(stagedAbilitySetUassetPath);
        if (writable is not null) return writable;

        try
        {
            var asset = LoadAsset(stagedAbilitySetUassetPath);
            var export = FindAbilitySetExport(asset);
            var array = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "GrantedGameplayEffects"));
            if (array is null)
            {
                array = new ArrayPropertyData(MakeName(asset, "GrantedGameplayEffects"))
                {
                    ArrayType = MakeName(asset, "StructProperty"),
                    Value = Array.Empty<PropertyData>(),
                };
                export.Data.Add(array);
            }

            var items = array.Value.ToList();
            if (items.Any(item => item is not StructPropertyData))
            {
                return MutationResult.Fail(
                    "invalid-effect-array",
                    "GrantedGameplayEffects contains a non-struct entry and was not modified.");
            }

            var oldImports = GrantImports(asset, items, "GameplayEffect").ToList();
            var changes = new List<string>();
            foreach (var addition in additions)
            {
                var package = UnrealPathUtil.NormalizePackagePath(addition.PackagePath);
                if (!IsGamePackage(package))
                {
                    return MutationResult.Fail(
                        "invalid-effect-package",
                        $"'{addition.PackagePath}' is not a supported game or DLC content package.");
                }
                if (FindGrantEntries(asset, items, package, "GameplayEffect").Count > 0)
                {
                    changes.Add($"{package} already granted");
                    continue;
                }

                var localTemplate = items.OfType<StructPropertyData>().FirstOrDefault();
                var metadata = localTemplate is not null
                    ? ReadGameplayEffectTemplateMetadata(localTemplate)
                    : ReadSelectedEffectTemplateMetadata(addition);
                if (metadata is null)
                {
                    return MutationResult.Fail(
                        "no-effect-template",
                        "Adding the first gameplay effect requires its source AbilitySet and effect package.");
                }

                var effectImport = EnsureBlueprintClassImport(asset, package);
                items.Add(CreateGameplayEffectEntry(
                    asset,
                    localTemplate,
                    metadata,
                    effectImport,
                    addition.EffectLevelOverride ?? metadata.EffectLevel));
                changes.Add($"added {package}");
            }

            Renumber(items, asset);
            array.Value = items.ToArray();
            SyncPreloadDependencies(asset, export, oldImports, GrantImports(asset, items, "GameplayEffect").ToList());
            asset.Write(stagedAbilitySetUassetPath);

            var verify = InspectAbilitySet(stagedAbilitySetUassetPath);
            if (!verify.Success)
            {
                return MutationResult.Fail(
                    "verification-failed",
                    verify.Error ?? "The written AbilitySet could not be reloaded.");
            }
            var missing = additions.Select(addition => UnrealPathUtil.NormalizePackagePath(addition.PackagePath))
                .Where(package => !verify.GameplayEffects.Any(effect => PackageEquals(effect.PackagePath, package)))
                .ToList();
            return missing.Count == 0
                ? MutationResult.Ok(changes)
                : MutationResult.Fail(
                    "verification-failed",
                    "The written AbilitySet is missing effect(s): " + string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            return MutationResult.Fail("error", ex.ToString());
        }
    }

    /// <summary>
    /// Makes one combat-type GameplayEffect authoritative while preserving every unrelated effect.
    /// Combat styles are mutually exclusive in shipped playable loadouts; appending a second
    /// GE_CombatType_* grant leaves competing tags and is therefore never allowed here.
    /// </summary>
    public MutationResult SetExclusiveCombatTypeEffect(
        string stagedAbilitySetUassetPath,
        GameplayEffectAddition? selected)
    {
        var writable = EnsureStagedWritableAsset(stagedAbilitySetUassetPath);
        if (writable is not null) return writable;

        // Null explicitly means the shipped style has no combat-type effect (e.g. Lucius).
        // It must remove the outgoing style's tag, not leave it active on the new melee set.
        var selectedPackage = UnrealPathUtil.NormalizePackagePath(selected?.PackagePath ?? "");
        if (selected is not null && !IsCombatTypeEffect(selectedPackage))
        {
            return MutationResult.Fail(
                "invalid-combat-effect",
                $"'{selected.PackagePath}' is not a GE_CombatType_* GameplayEffect.");
        }

        try
        {
            var asset = LoadAsset(stagedAbilitySetUassetPath);
            var export = FindAbilitySetExport(asset);
            var array = export.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "GrantedGameplayEffects"));
            if (array is null)
            {
                if (selected is null) return MutationResult.Ok(new[] { "no combat-type effect to remove" });
                array = new ArrayPropertyData(MakeName(asset, "GrantedGameplayEffects"))
                {
                    ArrayType = MakeName(asset, "StructProperty"),
                    Value = Array.Empty<PropertyData>(),
                };
                export.Data.Add(array);
            }
            var items = array.Value.ToList();
            if (items.Any(item => item is not StructPropertyData))
            {
                return MutationResult.Fail(
                    "invalid-effect-array",
                    "GrantedGameplayEffects contains a non-struct entry and was not modified.");
            }

            var originalImports = GrantImports(asset, items, "GameplayEffect").ToList();
            var changes = new List<string>();
            var selectedFound = false;
            for (var index = items.Count - 1; index >= 0; index--)
            {
                var entry = (StructPropertyData)items[index];
                var reference = entry.Value.OfType<ObjectPropertyData>()
                    .FirstOrDefault(property => PropertyNamed(property, "GameplayEffect"));
                if (reference is null || reference.Value.IsNull() || !reference.Value.IsImport()) continue;
                var package = ResolveObjectReference(asset, reference.Value).PackagePath;
                if (!IsCombatTypeEffect(package)) continue;

                if (PackageEquals(package, selectedPackage) && !selectedFound)
                {
                    selectedFound = true;
                    continue;
                }
                items.RemoveAt(index);
                changes.Add($"removed {package}");
            }

            if (!selectedFound && selected is not null)
            {
                var localTemplate = items.OfType<StructPropertyData>().FirstOrDefault();
                var metadata = localTemplate is not null
                    ? ReadGameplayEffectTemplateMetadata(localTemplate)
                    : ReadSelectedEffectTemplateMetadata(selected);
                if (metadata is null)
                {
                    return MutationResult.Fail(
                        "no-effect-template",
                        "Setting the first combat effect requires its source AbilitySet and effect package.");
                }
                var effectImport = EnsureBlueprintClassImport(asset, selectedPackage);
                items.Add(CreateGameplayEffectEntry(
                    asset,
                    localTemplate,
                    metadata,
                    effectImport,
                    selected.EffectLevelOverride ?? metadata.EffectLevel));
                changes.Add($"added {selectedPackage}");
            }
            else if (selectedFound)
            {
                changes.Add($"kept exclusive {selectedPackage}");
            }

            Renumber(items, asset);
            array.Value = items.ToArray();
            SyncPreloadDependencies(
                asset,
                export,
                originalImports,
                GrantImports(asset, items, "GameplayEffect").ToList());
            asset.Write(stagedAbilitySetUassetPath);

            var verify = InspectAbilitySet(stagedAbilitySetUassetPath);
            if (!verify.Success)
            {
                return MutationResult.Fail(
                    "verification-failed",
                    verify.Error ?? "The written AbilitySet could not be reloaded.");
            }
            var combatEffects = verify.GameplayEffects
                .Where(effect => IsCombatTypeEffect(effect.PackagePath))
                .ToList();
            if (selected is null ? combatEffects.Count != 0 :
                combatEffects.Count != 1 || !PackageEquals(combatEffects[0].PackagePath, selectedPackage))
            {
                return MutationResult.Fail(
                    "verification-failed",
                    $"Expected {(selected is null ? "no combat effect" : $"exactly one combat effect ({selectedPackage})")}; found " +
                    (combatEffects.Count == 0
                        ? "none."
                        : string.Join(", ", combatEffects.Select(effect => effect.PackagePath))));
            }
            return MutationResult.Ok(changes);
        }
        catch (Exception ex)
        {
            return MutationResult.Fail("error", ex.ToString());
        }
    }

    private static List<AbilityGrantReference> ReadGrantArray(
        UAsset asset,
        NormalExport export,
        string arrayName,
        string referenceField,
        string kind)
    {
        var array = export.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, arrayName));
        if (array is null)
        {
            return new List<AbilityGrantReference>();
        }

        var result = new List<AbilityGrantReference>();
        for (var index = 0; index < array.Value.Length; index++)
        {
            if (array.Value[index] is not StructPropertyData entry)
            {
                continue;
            }
            var reference = entry.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, referenceField));
            if (reference is null || reference.Value.IsNull() || !reference.Value.IsImport())
            {
                continue;
            }
            var resolved = ResolveObjectReference(asset, reference.Value);
            result.Add(new AbilityGrantReference
            {
                Index = index,
                Kind = kind,
                PackagePath = resolved.PackagePath,
                ObjectName = resolved.ObjectName,
                ClassName = resolved.ClassName,
                AbilityLevel = entry.Value.OfType<IntPropertyData>()
                    .FirstOrDefault(property => PropertyNamed(property, "AbilityLevel"))?.Value,
                EffectLevel = entry.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(property => PropertyNamed(property, "EffectLevel"))?.Value,
                InputTag = ReadInputTag(entry),
                IsNativeClass = resolved.PackagePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase),
            });
        }
        return result;
    }

    private static AbilityGrantReference? ReadAccessoryAnimGraph(UAsset asset, NormalExport export)
    {
        var property = export.Data.OfType<ObjectPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "AccessoryAnimGraphClass"));
        if (property is null || property.Value.IsNull() || !property.Value.IsImport())
        {
            return null;
        }
        var resolved = ResolveObjectReference(asset, property.Value);
        return new AbilityGrantReference
        {
            Index = 0,
            Kind = "Accessory animation graph",
            PackagePath = resolved.PackagePath,
            ObjectName = resolved.ObjectName,
            ClassName = resolved.ClassName,
            IsNativeClass = resolved.PackagePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static (NormalExport Export, ArrayPropertyData Array) FindDprdAbilitySets(UAsset asset)
    {
        var export = asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(candidate => candidate.Data.Any(property => PropertyNamed(property, "AbilitySets")))
            ?? throw new InvalidDataException("DPRD has no export containing AbilitySets.");
        var array = export.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "AbilitySets"))
            ?? throw new InvalidDataException("DPRD AbilitySets is not an array property.");
        return (export, array);
    }

    private static NormalExport FindAbilitySetExport(UAsset asset) =>
        asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(candidate =>
                candidate.GetExportClassType()?.Value.Value.Equals(
                    AbilitySetClassName,
                    StringComparison.OrdinalIgnoreCase) == true ||
                candidate.Data.Any(property =>
                    property.Name.ToString().StartsWith("Granted", StringComparison.OrdinalIgnoreCase)))
        ?? throw new InvalidDataException("Asset has no TtAbilitySet export.");

    private static List<(int Index, StructPropertyData Entry)> FindAbilityEntries(
        UAsset asset,
        IReadOnlyList<PropertyData> items,
        string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var result = new List<(int, StructPropertyData)>();
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not StructPropertyData entry)
            {
                continue;
            }
            var ability = entry.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "Ability"));
            if (ability is null || ability.Value.IsNull() || !ability.Value.IsImport())
            {
                continue;
            }
            if (PackageEquals(ResolveObjectReference(asset, ability.Value).PackagePath, package))
            {
                result.Add((index, entry));
            }
        }
        return result;
    }

    private static List<(int Index, StructPropertyData Entry)> FindGrantEntries(
        UAsset asset,
        IReadOnlyList<PropertyData> items,
        string packagePath,
        string referenceField)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var result = new List<(int, StructPropertyData)>();
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not StructPropertyData entry) continue;
            var reference = entry.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, referenceField));
            if (reference is not null && !reference.Value.IsNull() && reference.Value.IsImport() &&
                PackageEquals(ResolveObjectReference(asset, reference.Value).PackagePath, package))
            {
                result.Add((index, entry));
            }
        }
        return result;
    }

    internal static bool IsCombatTypeEffect(string? packagePath) =>
        UnrealPathUtil.NormalizePackagePath(packagePath ?? "").Contains(
            "/GameplayEffects/Combat/CombatTypes/GE_CombatType_",
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<FPackageIndex> AbilityImports(UAsset asset, IEnumerable<PropertyData> items)
    {
        foreach (var entry in items.OfType<StructPropertyData>())
        {
            var ability = entry.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "Ability"));
            if (ability is not null && !ability.Value.IsNull() && ability.Value.IsImport())
            {
                yield return ability.Value;
            }
        }
    }

    private static IEnumerable<FPackageIndex> GrantImports(
        UAsset asset,
        IEnumerable<PropertyData> items,
        string referenceField)
    {
        foreach (var entry in items.OfType<StructPropertyData>())
        {
            var reference = entry.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, referenceField));
            if (reference is not null && !reference.Value.IsNull() && reference.Value.IsImport())
            {
                yield return reference.Value;
            }
        }
    }

    private sealed class GameplayAbilityTemplateMetadata
    {
        public int AbilityLevel { get; init; } = 1;
        public string InputTag { get; init; } = "";
        public Guid EntryGuid { get; init; }
        public bool EntrySerializeNone { get; init; }
        public Guid TagGuid { get; init; }
        public bool TagSerializeNone { get; init; }
    }

    private sealed class GameplayEffectTemplateMetadata
    {
        public float EffectLevel { get; init; } = 1.0f;
        public Guid EntryGuid { get; init; }
        public bool EntrySerializeNone { get; init; }
    }

    private GameplayEffectTemplateMetadata? ReadSelectedEffectTemplateMetadata(GameplayEffectAddition addition)
    {
        if (string.IsNullOrWhiteSpace(addition.SourceAbilitySetUassetPath) ||
            string.IsNullOrWhiteSpace(addition.SourceEffectPackagePath) ||
            !File.Exists(addition.SourceAbilitySetUassetPath))
        {
            return null;
        }
        var source = LoadAsset(addition.SourceAbilitySetUassetPath);
        var export = FindAbilitySetExport(source);
        var array = export.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "GrantedGameplayEffects"));
        if (array is null) return null;
        var matches = FindGrantEntries(source, array.Value, addition.SourceEffectPackagePath, "GameplayEffect");
        return matches.Count == 1 ? ReadGameplayEffectTemplateMetadata(matches[0].Entry) : null;
    }

    private static GameplayEffectTemplateMetadata ReadGameplayEffectTemplateMetadata(StructPropertyData entry) => new()
    {
        EffectLevel = entry.Value.OfType<FloatPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "EffectLevel"))?.Value ?? 1.0f,
        EntryGuid = entry.StructGUID,
        EntrySerializeNone = entry.SerializeNone,
    };

    private static StructPropertyData CreateGameplayEffectEntry(
        UAsset asset,
        StructPropertyData? localTemplate,
        GameplayEffectTemplateMetadata metadata,
        FPackageIndex effectImport,
        float level)
    {
        if (localTemplate is not null)
        {
            var clone = (StructPropertyData)localTemplate.Clone();
            SetGameplayEffectReference(clone, effectImport);
            SetGameplayEffectLevel(clone, level);
            return clone;
        }
        return new StructPropertyData(MakeName(asset, "0"))
        {
            StructType = MakeName(asset, "TtAbilitySet_GameplayEffect"),
            StructGUID = metadata.EntryGuid,
            SerializeNone = metadata.EntrySerializeNone,
            Value = new List<PropertyData>
            {
                new ObjectPropertyData(MakeName(asset, "GameplayEffect")) { Value = effectImport },
                new FloatPropertyData(MakeName(asset, "EffectLevel")) { Value = level },
            },
        };
    }

    private static void SetGameplayEffectReference(StructPropertyData entry, FPackageIndex import)
    {
        var property = entry.Value.OfType<ObjectPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "GameplayEffect"))
            ?? throw new InvalidDataException("Gameplay effect struct has no GameplayEffect field.");
        property.Value = import;
    }

    private static void SetGameplayEffectLevel(StructPropertyData entry, float level)
    {
        var property = entry.Value.OfType<FloatPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "EffectLevel"))
            ?? throw new InvalidDataException("Gameplay effect struct has no EffectLevel field.");
        property.Value = level;
    }

    private GameplayAbilityTemplateMetadata? ReadSelectedTemplateMetadata(GameplayAbilityEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.SourceAbilitySetUassetPath) ||
            string.IsNullOrWhiteSpace(edit.SourceAbilityPackagePath) ||
            !File.Exists(edit.SourceAbilitySetUassetPath))
        {
            return null;
        }
        var source = LoadAsset(edit.SourceAbilitySetUassetPath);
        var export = FindAbilitySetExport(source);
        var array = export.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "GrantedGameplayAbilities"));
        if (array is null)
        {
            return null;
        }
        var matches = FindAbilityEntries(source, array.Value, edit.SourceAbilityPackagePath);
        return matches.Count == 1 ? ReadTemplateMetadata(source, matches[0].Entry) : null;
    }

    private static GameplayAbilityTemplateMetadata? ReadTemplateMetadata(
        UAsset asset,
        StructPropertyData? entry)
    {
        if (entry is null)
        {
            return null;
        }
        var tag = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "InputTag"));
        return new GameplayAbilityTemplateMetadata
        {
            AbilityLevel = entry.Value.OfType<IntPropertyData>()
                .FirstOrDefault(property => PropertyNamed(property, "AbilityLevel"))?.Value ?? 1,
            InputTag = ReadInputTag(entry),
            EntryGuid = entry.StructGUID,
            EntrySerializeNone = entry.SerializeNone,
            TagGuid = tag?.StructGUID ?? Guid.Empty,
            TagSerializeNone = tag?.SerializeNone ?? false,
        };
    }

    private static StructPropertyData CreateGameplayAbilityEntry(
        UAsset asset,
        StructPropertyData? localTemplate,
        GameplayAbilityTemplateMetadata metadata,
        FPackageIndex abilityImport,
        int level,
        string inputTag)
    {
        StructPropertyData entry;
        if (localTemplate is not null)
        {
            entry = (StructPropertyData)localTemplate.Clone();
            SetAbilityReference(entry, abilityImport);
            SetAbilityLevel(entry, level);
            SetInputTag(asset, entry, inputTag);
            return entry;
        }

        entry = new StructPropertyData(MakeName(asset, "0"))
        {
            StructType = MakeName(asset, "TtAbilitySet_GameplayAbility"),
            StructGUID = metadata.EntryGuid,
            SerializeNone = metadata.EntrySerializeNone,
            Value = new List<PropertyData>
            {
                new ObjectPropertyData(MakeName(asset, "Ability")) { Value = abilityImport },
                new IntPropertyData(MakeName(asset, "AbilityLevel")) { Value = level },
                CreateGameplayTag(asset, inputTag, metadata.TagGuid, metadata.TagSerializeNone),
            },
        };
        return entry;
    }

    private static StructPropertyData CreateGameplayTag(
        UAsset asset,
        string inputTag,
        Guid guid,
        bool serializeNone) =>
        new(MakeName(asset, "InputTag"))
        {
            StructType = MakeName(asset, "GameplayTag"),
            StructGUID = guid,
            SerializeNone = serializeNone,
            Value = new List<PropertyData>
            {
                new NamePropertyData(MakeName(asset, "TagName"))
                {
                    Value = string.IsNullOrWhiteSpace(inputTag)
                        ? MakeName(asset, "None")
                        : MakeName(asset, inputTag.Trim()),
                },
            },
        };

    private static void SetAbilityReference(StructPropertyData entry, FPackageIndex import)
    {
        var property = entry.Value.OfType<ObjectPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "Ability"));
        if (property is null)
        {
            throw new InvalidDataException("Gameplay ability struct has no Ability field.");
        }
        property.Value = import;
    }

    private static void SetAbilityLevel(StructPropertyData entry, int level)
    {
        var property = entry.Value.OfType<IntPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "AbilityLevel"));
        if (property is null)
        {
            throw new InvalidDataException("Gameplay ability struct has no AbilityLevel field.");
        }
        property.Value = level;
    }

    private static void SetInputTag(UAsset asset, StructPropertyData entry, string inputTag)
    {
        var tag = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "InputTag"));
        if (tag is null)
        {
            throw new InvalidDataException("Gameplay ability struct has no InputTag field.");
        }
        var tagName = tag.Value.OfType<NamePropertyData>()
            .FirstOrDefault(item => PropertyNamed(item, "TagName"));
        if (tagName is null)
        {
            throw new InvalidDataException("Gameplay ability InputTag has no TagName field.");
        }
        tagName.Value = string.IsNullOrWhiteSpace(inputTag)
            ? MakeName(asset, "None")
            : MakeName(asset, inputTag.Trim());
    }

    private static string ReadInputTag(StructPropertyData entry)
    {
        var tag = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "InputTag"));
        var tagName = tag?.Value.OfType<NamePropertyData>()
            .FirstOrDefault(property => PropertyNamed(property, "TagName"));
        var value = tagName?.Value?.ToString() ?? "";
        return value.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private static void Renumber(IList<PropertyData> items, UAsset asset)
    {
        for (var index = 0; index < items.Count; index++)
        {
            items[index].Name = MakeName(asset, index.ToString());
        }
    }

    private static void SyncPreloadDependencies(
        UAsset asset,
        NormalExport export,
        IReadOnlyCollection<FPackageIndex> oldImports,
        IReadOnlyCollection<FPackageIndex> currentImports)
    {
        var dependencies = export.CreateBeforeSerializationDependencies
            ?? throw new InvalidDataException("Export has no CreateBeforeSerializationDependencies collection.");
        var current = currentImports.Select(index => index.Index).ToHashSet();
        foreach (var old in oldImports)
        {
            if (!current.Contains(old.Index) && !ExportReferencesIndex(export, old.Index))
            {
                dependencies.RemoveAll(index => index.Index == old.Index);
            }
        }
        foreach (var import in currentImports)
        {
            if (dependencies.All(index => index.Index != import.Index))
            {
                dependencies.Add(import);
            }
        }
    }

    private static bool ExportReferencesIndex(NormalExport export, int rawIndex) =>
        export.Data.Any(property => PropertyReferencesIndex(property, rawIndex));

    private static bool PropertyReferencesIndex(PropertyData property, int rawIndex)
    {
        if (property is ObjectPropertyData objectProperty)
        {
            return objectProperty.Value.Index == rawIndex;
        }
        if (property is StructPropertyData structure)
        {
            return structure.Value.Any(child => PropertyReferencesIndex(child, rawIndex));
        }
        if (property is ArrayPropertyData array)
        {
            return array.Value.Any(child => PropertyReferencesIndex(child, rawIndex));
        }
        return false;
    }

    private static FPackageIndex EnsureBlueprintClassImport(UAsset asset, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var className = UnrealPathUtil.AssetName(package) + "_C";
        var classImport = EnsureObjectImport(
            asset,
            package,
            className,
            "/Script/Engine",
            "BlueprintGeneratedClass");
        EnsureObjectImport(asset, package, "Default__" + className, package, className);
        return classImport;
    }

    private static FPackageIndex EnsureObjectImport(
        UAsset asset,
        string packagePath,
        string objectName,
        string classPackage,
        string className)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        var packageImport = EnsurePackageImport(asset, package);
        for (var index = 0; index < asset.Imports.Count; index++)
        {
            var import = asset.Imports[index];
            if (import.OuterIndex.Index == packageImport.Index &&
                import.ObjectName.ToString().Equals(objectName, StringComparison.Ordinal) &&
                import.ClassPackage.ToString().Equals(classPackage, StringComparison.Ordinal) &&
                import.ClassName.ToString().Equals(className, StringComparison.Ordinal))
            {
                return FPackageIndex.FromImport(index);
            }
        }
        return asset.AddImport(new Import(
            classPackage,
            className,
            packageImport,
            objectName,
            false,
            asset));
    }

    private static FPackageIndex EnsurePackageImport(UAsset asset, string packagePath)
    {
        for (var index = 0; index < asset.Imports.Count; index++)
        {
            var import = asset.Imports[index];
            if (import.ClassName.ToString().Equals("Package", StringComparison.Ordinal) &&
                import.ObjectName.ToString().Equals(packagePath, StringComparison.OrdinalIgnoreCase))
            {
                return FPackageIndex.FromImport(index);
            }
        }
        return asset.AddImport(new Import(
            "/Script/CoreUObject",
            "Package",
            FPackageIndex.FromRawIndex(0),
            packagePath,
            false,
            asset));
    }

    private sealed record ResolvedObjectReference(
        string PackagePath,
        string ObjectName,
        string ClassName);

    private static ResolvedObjectReference ResolveObjectReference(UAsset asset, FPackageIndex index)
    {
        if (index.IsNull() || !index.IsImport())
        {
            throw new InvalidDataException("Expected an imported object reference.");
        }
        var import = index.ToImport(asset);
        var outer = import.OuterIndex;
        var seen = new HashSet<int>();
        while (!outer.IsNull() && outer.IsImport() && seen.Add(outer.Index))
        {
            var candidate = outer.ToImport(asset);
            if (candidate.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedObjectReference(
                    UnrealPathUtil.NormalizePackagePath(candidate.ObjectName.ToString()),
                    import.ObjectName.ToString(),
                    import.ClassName.ToString());
            }
            outer = candidate.OuterIndex;
        }
        throw new InvalidDataException($"Could not resolve package for import '{import.ObjectName}'.");
    }

    private static List<string> NormalizeDistinctPackages(
        IEnumerable<string> packages,
        out string? error)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in packages)
        {
            var package = UnrealPathUtil.NormalizePackagePath(raw);
            if (string.IsNullOrWhiteSpace(package) ||
                !IsGamePackage(package))
            {
                error = $"'{raw}' is not a supported game or DLC package path.";
                return result;
            }
            if (!seen.Add(package))
            {
                error = $"AbilitySets cannot contain duplicate package '{package}'.";
                return result;
            }
            result.Add(package);
        }
        error = null;
        return result;
    }

    private static MutationResult? EnsureStagedWritableAsset(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
        {
            return MutationResult.Fail("missing", $"Asset not found: {assetPath}");
        }
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var extractedMounts = ExtractedPackagePathService.EnumerateMounts(extractedRoot);
        if (extractedMounts.Any(mount => IsUnderRoot(assetPath, mount.ContentRoot)))
        {
            return MutationResult.Fail(
                "base-asset-read-only",
                "Ability assets under the active game extract are read-only. Clone the donor into the suit's staged mod tree first.");
        }
        return null;
    }

    internal static bool IsUnderRootForTest(string path, string root) => IsUnderRoot(path, root);

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static UAsset LoadAsset(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Cooked asset not found.", path);
        }
        var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
        if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
        {
            throw new FileNotFoundException("A UE 5.6 .usmap is required to inspect ability assets.", mappingsPath);
        }
        var mappings = MappingsCache.Load(mappingsPath);
        try
        {
            return new UAsset(path, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
        }
        catch (UnauthorizedAccessException)
        {
            // UAssetAPI opens split export streams read/write while parsing. Refresh outputs can be
            // deliberately read-only, so inspect a private ephemeral copy instead of weakening the
            // extract's protection. Mutation entry points already reject extract-owned targets.
            var temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Batcomputer-ability-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                var sourceBase = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path));
                var targetBase = Path.Combine(temporaryRoot, Path.GetFileNameWithoutExtension(path));
                foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
                {
                    var source = sourceBase + extension;
                    if (File.Exists(source)) File.Copy(source, targetBase + extension, overwrite: true);
                }
                return new UAsset(
                    targetBase + ".uasset",
                    EngineVersion.VER_UE5_6,
                    mappings,
                    CustomSerializationFlags.None);
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* the OS will reclaim this best-effort inspection copy */ }
            }
        }
    }

    private static bool PropertyNamed(PropertyData property, string name) =>
        property.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool PackageEquals(string left, string right) =>
        UnrealPathUtil.NormalizePackagePath(left).Equals(
            UnrealPathUtil.NormalizePackagePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGamePackage(string packagePath) =>
        ExtractedPackagePathService.IsContentPackagePath(packagePath) &&
        packagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2 &&
        !packagePath.EndsWith('/');

    private static FName MakeName(UAsset asset, string value) => FName.FromString(asset, value);
}
