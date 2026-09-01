using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>
/// Pure fixture for the semantic half of character-animation set parsing. It uses the same raw
/// records produced by the UAsset adapter, so it can verify stable slot identities, contextual
/// duplicates, weight indices, direct AnimFile references, and LayerAnimArray expansion without
/// requiring copyrighted cooked assets or a writable fixture package.
/// </summary>
public static class CharacterAnimationGraphRegressionChecks
{
    public static void Run(List<string> failures, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (!Run(output))
        {
            failures.Add("character animation graph discovery and exact-slot overlay");
        }
    }

    public static bool Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var failures = new List<string>();

        var mergedAnimationCatalog = ExtractedAnimationCatalogService.MergeForRegression(
            new[]
            {
                new GameDataAsset
                {
                    Path = "/Game/Animation/LEGOfig/Batman/A_Idle_Batman",
                    Class = "AnimSequence",
                },
            },
            new[]
            {
                new GameDataAsset
                {
                    Path = "/Game/Animation/LEGOfig/Batman/A_Idle_Batman",
                    Class = "AnimSequence",
                },
                new GameDataAsset
                {
                    Path = "/Game/AdditionalContent/Beyond/Animation/A_Idle_BatmanBeyond",
                    Class = "AnimSequence",
                },
                new GameDataAsset
                {
                    Path = "/Game/AdditionalContent/Beyond/Animation/AM_Jump_BatmanBeyond",
                    Class = "AnimMontage",
                },
            },
            "AnimSequence");
        Check(
            mergedAnimationCatalog.Select(asset => asset.Path).SequenceEqual(new[]
            {
                "/Game/AdditionalContent/Beyond/Animation/A_Idle_BatmanBeyond",
                "/Game/Animation/LEGOfig/Batman/A_Idle_Batman",
            }) && mergedAnimationCatalog.All(asset => asset.Class == "AnimSequence"),
            "animation choices merge per-user extracted DLC with the shipped fallback without cross-class entries",
            failures,
            output);

        var sharedSmallItemMontage = new RawAnimationReference(
            "/Game/Animation/LEGOfig/Batman/Item/AM_Jump_SmallItem_Batman",
            "AM_Jump_SmallItem_Batman",
            "AnimMontage");
        var montageEntries = new List<RawAnimationSetEntry>
        {
            new(
                0,
                "Animation.Action.Jump",
                Array.Empty<string>(),
                1,
                new[]
                {
                    new RawAnimationWeightEntry(
                        0,
                        1,
                        new RawAnimationReference(
                            "/Game/Animation/LEGOfig/Batman/Movement/AM_Jump_Batman",
                            "AM_Jump_Batman",
                            "AnimMontage"),
                        Array.Empty<RawLayerAnimationReference>()),
                }),
            new(
                1,
                "Animation.Action.Jump",
                new[] { "Animation.Equipment.SmallItem" },
                2,
                new[]
                {
                    new RawAnimationWeightEntry(
                        0,
                        1,
                        sharedSmallItemMontage,
                        Array.Empty<RawLayerAnimationReference>()),
                }),
            new(
                2,
                "Animation.Action.Jump",
                new[] { "Animation.Context.Misc.Moving", "Animation.Equipment.SmallItem" },
                3,
                new[]
                {
                    new RawAnimationWeightEntry(
                        2,
                        7,
                        sharedSmallItemMontage,
                        Array.Empty<RawLayerAnimationReference>()),
                }),
            new(
                3,
                "Animation.Action.Land",
                new[] { "Animation.Status.Alert", "Animation.Status.Moving" },
                4,
                new[]
                {
                    new RawAnimationWeightEntry(
                        0,
                        1,
                        new RawAnimationReference(
                            "/Game/Animation/LEGOfig/Batman/Movement/A_Land_Move_Batman",
                            "A_Land_Move_Batman",
                            "AnimSequence"),
                        Array.Empty<RawLayerAnimationReference>()),
                }),
        };

        var montageSlots = CharacterAnimationGraphService.MaterializeSlotsForTest(
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
            CharacterAnimationSetKind.Montage,
            montageEntries);
        Check(
            montageSlots.Count == 4 && montageSlots.All(slot => slot.Targets.Count == 1),
            "materializes every logical TTAnimSet entry",
            failures,
            output);
        Check(
            montageSlots[1].Targets[0].OriginalPackage == montageSlots[2].Targets[0].OriginalPackage &&
            montageSlots[1].Targets[0].TargetId != montageSlots[2].Targets[0].TargetId,
            "keeps reused animation assets as distinct ActionTag/context targets",
            failures,
            output);
        Check(
            montageSlots[2].Targets[0] is
            {
                WeightIndex: 2,
                Weight: 7,
                ReferenceKind: CharacterAnimationReferenceKind.AnimFile,
                AssetClass: "AnimMontage",
                EffectiveAssetClass: "AnimMontage",
            },
            "retains AnimAndWeightsArray position and authored weight",
            failures,
            output);
        Check(
            montageSlots[3].Targets[0].AssetClass == "AnimSequence" &&
            montageSlots[3].ContextTags.SequenceEqual(
                new[] { "Animation.Status.Alert", "Animation.Status.Moving" }),
            "accepts a direct AnimSequence AnimFile and preserves contexts",
            failures,
            output);

        var overrideDiagnostics = new List<CharacterAnimationDiagnostic>();
        var overriddenSlots = new CharacterAnimationGraphService().ApplyExactSlotOverridesForTest(
            new NativeSuitProject
            {
                AnimationSlotOverrides =
                {
                    new AnimationSlotOverride
                    {
                        Kind = "Montage",
                        OwnerSetPackage = montageSlots[2].SetPackage,
                        ActionTag = montageSlots[2].ActionTag,
                        ContextTags = montageSlots[2].ContextTags.Reverse().ToList(),
                        EntryIndex = 2,
                        VariantIndex = 2,
                        ReferenceKind = "AnimFile",
                        ReferenceIndex = 0,
                        DonorPackage = sharedSmallItemMontage.PackagePath,
                        DonorClass = "AnimMontage",
                        ReplacementPackage = "/Game/Animation/Custom/AM_Jump_Custom",
                        ReplacementClass = "AnimMontage",
                    },
                },
            },
            montageSlots,
            overrideDiagnostics);
        Check(
            overriddenSlots[2].Targets[0] is
            {
                IsOverridden: true,
                OverrideKind: "exact-slot",
                EffectivePackage: "/Game/Animation/Custom/AM_Jump_Custom",
                EffectiveObjectName: "AM_Jump_Custom",
                EffectiveAssetClass: "AnimMontage",
            } &&
            overrideDiagnostics.Count == 0,
            "applies a persisted exact-slot override by semantic identity and raw indices",
            failures,
            output);

        var shiftedSavedOverride = new AnimationSlotOverride
        {
            Kind = "Montage",
            OwnerSetPackage = montageSlots[2].SetPackage,
            ActionTag = montageSlots[2].ActionTag,
            ContextTags = montageSlots[2].ContextTags.ToList(),
            EntryIndex = 99,
            VariantIndex = 99,
            ReferenceKind = "AnimFile",
            ReferenceIndex = 0,
            DonorPackage = montageSlots[2].Targets[0].OriginalPackage,
            DonorClass = "AnimMontage",
            ReplacementPackage = "/Game/Animation/Custom/AM_Jump_Custom",
            ReplacementClass = "AnimMontage",
        };
        var shiftedIndex = CharacterAnimationGraphService.SelectPersistedSlotOverrideIndex(
            new[] { shiftedSavedOverride },
            montageSlots[2],
            montageSlots[2].Targets[0],
            out var shiftedAmbiguous);
        Check(
            shiftedIndex == 0 && !shiftedAmbiguous,
            "selects one unique semantic override after refreshed animation data moves its raw row",
            failures,
            output);
        var ambiguousIndex = CharacterAnimationGraphService.SelectPersistedSlotOverrideIndex(
            new[]
            {
                shiftedSavedOverride,
                new AnimationSlotOverride
                {
                    Kind = shiftedSavedOverride.Kind,
                    OwnerSetPackage = shiftedSavedOverride.OwnerSetPackage,
                    ActionTag = shiftedSavedOverride.ActionTag,
                    ContextTags = shiftedSavedOverride.ContextTags.ToList(),
                    EntryIndex = 98,
                    VariantIndex = shiftedSavedOverride.VariantIndex,
                    ReferenceKind = shiftedSavedOverride.ReferenceKind,
                    ReferenceIndex = shiftedSavedOverride.ReferenceIndex,
                    DonorPackage = shiftedSavedOverride.DonorPackage,
                    DonorClass = shiftedSavedOverride.DonorClass,
                    ReplacementPackage = shiftedSavedOverride.ReplacementPackage,
                    ReplacementClass = shiftedSavedOverride.ReplacementClass,
                },
            },
            montageSlots[2],
            montageSlots[2].Targets[0],
            out var duplicateAmbiguous);
        Check(
            ambiguousIndex == -1 && duplicateAmbiguous && !montageSlots[2].Targets[0].IsOverridden,
            "reports multiple shifted saved overrides as ambiguous even when the visible graph target is not marked overridden",
            failures,
            output);

        var defaultLayerOwner = "/Game/Animation/LayerAnimSets/Default/LAS_Default_Batman";
        Check(
            !string.IsNullOrWhiteSpace(AnimArchetypeGraftService.LocomotionCompositionConflict(
                "Batman",
                Array.Empty<AnimSetOverride>(),
                new[]
                {
                    new AnimationSlotOverride
                    {
                        Kind = "Layer",
                        OwnerSetPackage = defaultLayerOwner,
                    },
                })) &&
            !string.IsNullOrWhiteSpace(AnimArchetypeGraftService.LocomotionCompositionConflict(
                "Batman",
                new[]
                {
                    new AnimSetOverride
                    {
                        Category = "Locomotion (idle/walk/run)",
                        Kind = "Layer",
                    },
                },
                Array.Empty<AnimationSlotOverride>())) &&
            string.IsNullOrWhiteSpace(AnimArchetypeGraftService.LocomotionCompositionConflict(
                "Batman",
                new[]
                {
                    new AnimSetOverride
                    {
                        Category = "Traversal",
                        Kind = "Layer",
                    },
                },
                Array.Empty<AnimationSlotOverride>())),
            "blocks competing LAS_Default owners while allowing unrelated layer edits",
            failures,
            output);

        var reversedContextEntry = montageEntries[2] with
        {
            ContextTags = new[] { "Animation.Equipment.SmallItem", "Animation.Context.Misc.Moving" },
        };
        var reversedContextSlot = CharacterAnimationGraphService.MaterializeSlotsForTest(
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
            CharacterAnimationSetKind.Montage,
            new[] { reversedContextEntry })[0];
        Check(
            montageSlots[2].Targets[0].TargetId == reversedContextSlot.Targets[0].TargetId,
            "normalizes context-tag order inside stable target identities",
            failures,
            output);

        var layerEntries = new List<RawAnimationSetEntry>
        {
            new(
                0,
                "Animation.Layer.Default",
                Array.Empty<string>(),
                1,
                new[]
                {
                    new RawAnimationWeightEntry(
                        0,
                        1,
                        null,
                        new[]
                        {
                            new RawLayerAnimationReference(
                                0,
                                new RawAnimationReference(
                                    "/Game/Animation/LEGOfig/Batman/ABP_Core_Batman",
                                    "ABP_Core_Batman_C",
                                    "AnimBlueprintGeneratedClass")),
                            new RawLayerAnimationReference(
                                2,
                                new RawAnimationReference(
                                    "/Game/Animation/LEGOfig/Batman/ABP_Movement_Batman",
                                    "ABP_Movement_Batman_C",
                                    "AnimBlueprintGeneratedClass")),
                        }),
                }),
        };
        var layerSlots = CharacterAnimationGraphService.MaterializeSlotsForTest(
            "/Game/Animation/LayerAnimSets/Default/LAS_Default_Batman",
            CharacterAnimationSetKind.Layer,
            layerEntries);
        Check(
            layerSlots.Count == 1 &&
            layerSlots[0].Targets.Count == 2 &&
            layerSlots[0].Targets[0].LayerIndex == 0 &&
            layerSlots[0].Targets[1].LayerIndex == 2 &&
            layerSlots[0].Targets.All(target =>
                target.ReferenceKind == CharacterAnimationReferenceKind.LayerAnimation &&
                target.AssetClass == "AnimBlueprintGeneratedClass"),
            "expands LayerAnimArray references without renumbering malformed gaps",
            failures,
            output);

        RunPropertyTreeParserFixture(failures, output);

        if (failures.Count == 0)
        {
            output.WriteLine("PASS: character animation graph pure parser fixture");
            return true;
        }

        output.WriteLine($"FAIL: character animation graph pure parser fixture ({failures.Count} failure(s))");
        foreach (var failure in failures)
        {
            output.WriteLine("  - " + failure);
        }
        return false;
    }

    private static void RunPropertyTreeParserFixture(
        ICollection<string> failures,
        TextWriter output)
    {
        try
        {
            var asset = new UAsset();
            asset.ClearNameIndexList();
            asset.Imports = new List<Import>();

            FName Name(string value) => FName.FromString(asset, value);
            FPackageIndex AddObject(string package, string objectName, string className)
            {
                var packageIndex = asset.AddImport(new Import(
                    "/Script/CoreUObject",
                    "Package",
                    FPackageIndex.FromRawIndex(0),
                    package,
                    false,
                    asset));
                return asset.AddImport(new Import(
                    "/Script/Engine",
                    className,
                    packageIndex,
                    objectName,
                    false,
                    asset));
            }

            var coreClass = AddObject(
                "/Game/Animation/LEGOfig/Batman/ABP_Core_Batman",
                "ABP_Core_Batman_C",
                "AnimBlueprintGeneratedClass");
            var movementClass = AddObject(
                "/Game/Animation/LEGOfig/Batman/ABP_Movement_Batman",
                "ABP_Movement_Batman_C",
                "AnimBlueprintGeneratedClass");
            var replacementLayer = AddObject(
                "/Game/Animation/LEGOfig/Nightwing/ABP_Movement_Nightwing",
                "ABP_Movement_Nightwing_C",
                "AnimBlueprintGeneratedClass");
            var directSequence = AddObject(
                "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
                "A_Idle_Batman",
                "AnimSequence");

            var actionTag = new StructPropertyData(Name("ActionTag"))
            {
                Value = new List<PropertyData>
                {
                    new NamePropertyData(Name("TagName"))
                    {
                        Value = Name("Animation.Layer.Base"),
                    },
                },
            };
            var contextTags = new StructPropertyData(Name("ContextTags"))
            {
                Value = new List<PropertyData>
                {
                    new GameplayTagContainerPropertyData(Name("GameplayTags"))
                    {
                        Value = new[]
                        {
                            Name("Animation.Status.Moving"),
                            Name("Animation.Equipment.SmallItem"),
                        },
                    },
                },
            };
            var layerVariant = new StructPropertyData(Name("0"))
            {
                Value = new List<PropertyData>
                {
                    new BytePropertyData(Name("Weight")) { Value = 3 },
                    new ObjectPropertyData(Name("AnimFile"))
                    {
                        Value = FPackageIndex.FromRawIndex(0),
                    },
                    new ArrayPropertyData(Name("LayerAnimArray"))
                    {
                        Value = new PropertyData[]
                        {
                            new ObjectPropertyData(Name("0")) { Value = coreClass },
                            new NamePropertyData(Name("1")) { Value = Name("MalformedGap") },
                            new ObjectPropertyData(Name("2")) { Value = movementClass },
                        },
                    },
                },
            };
            var sequenceVariant = new StructPropertyData(Name("2"))
            {
                Value = new List<PropertyData>
                {
                    new BytePropertyData(Name("Weight")) { Value = 9 },
                    new ObjectPropertyData(Name("AnimFile")) { Value = directSequence },
                    new ArrayPropertyData(Name("LayerAnimArray"))
                    {
                        Value = Array.Empty<PropertyData>(),
                    },
                },
            };
            var entry = new StructPropertyData(Name("0"))
            {
                Value = new List<PropertyData>
                {
                    actionTag,
                    contextTags,
                    new IntPropertyData(Name("ActionLink")) { Value = 17 },
                    new ArrayPropertyData(Name("AnimAndWeightsArray"))
                    {
                        Value = new PropertyData[]
                        {
                            layerVariant,
                            new NamePropertyData(Name("1")) { Value = Name("MalformedVariantGap") },
                            sequenceVariant,
                        },
                    },
                },
            };
            var entriesProperty = new ArrayPropertyData(Name("AnimSetEntryArray"))
            {
                Value = new PropertyData[] { entry },
            };
            var diagnostics = new List<CharacterAnimationDiagnostic>();
            var slots = CharacterAnimationGraphService.ParseSetExportDataForTest(
                asset,
                "/Game/Animation/LayerAnimSets/Default/LAS_Default_Batman",
                CharacterAnimationSetKind.Layer,
                new PropertyData[] { entriesProperty },
                diagnostics);

            Check(
                slots.Count == 1 &&
                slots[0].ActionTag == "Animation.Layer.Base" &&
                slots[0].ActionLink == 17 &&
                slots[0].ContextTags.Count == 2 &&
                slots[0].Targets.Count == 3 &&
                slots[0].Targets[0] is
                {
                    WeightIndex: 0,
                    LayerIndex: 0,
                    OriginalObjectName: "ABP_Core_Batman_C",
                    AssetClass: "AnimBlueprintGeneratedClass",
                } &&
                slots[0].Targets[1] is
                {
                    WeightIndex: 0,
                    LayerIndex: 2,
                    OriginalPackage: "/Game/Animation/LEGOfig/Batman/ABP_Movement_Batman",
                } &&
                slots[0].Targets[2] is
                {
                    WeightIndex: 2,
                    Weight: 9,
                    ReferenceKind: CharacterAnimationReferenceKind.AnimFile,
                    OriginalPackage: "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
                } &&
                diagnostics.Count == 2 &&
                diagnostics.Select(item => item.Code).SequenceEqual(new[] { "layer-shape", "weight-shape" }),
                "parses a synthetic UAssetAPI property tree and preserves every raw nested index",
                failures,
                output);

            var writerTarget = new AnimationSlotOverride
            {
                Kind = "Layer",
                OwnerSetPackage = "/Game/Animation/LayerAnimSets/Default/LAS_Default_Batman",
                ActionTag = "Animation.Layer.Base",
                ContextTags = ["Animation.Equipment.SmallItem", "Animation.Status.Moving"],
                EntryIndex = 0,
                VariantIndex = 0,
                ReferenceKind = "LayerAnim",
                ReferenceIndex = 2,
                DonorPackage = "/Game/Animation/LEGOfig/Batman/ABP_Movement_Batman",
                DonorClass = "AnimBlueprintGeneratedClass",
                ReplacementPackage = "/Game/Animation/LEGOfig/Nightwing/ABP_Movement_Nightwing",
                ReplacementClass = "AnimBlueprintGeneratedClass",
            };
            var writerMatches = AnimGraftService.ReplaceAnimationSlotReferenceForTest(
                asset,
                entriesProperty,
                writerTarget,
                replacementLayer);
            var patchedLayers = layerVariant.Value.OfType<ArrayPropertyData>()
                .Single(property => property.Name.ToString().Equals("LayerAnimArray", StringComparison.OrdinalIgnoreCase));
            var patchedReference = patchedLayers.Value[2] as ObjectPropertyData;
            Check(
                writerMatches == 1 && patchedReference?.Value.Index == replacementLayer.Index,
                "patches only the exact semantic action/context/variant/layer reference used by packaging",
                failures,
                output);
        }
        catch (Exception ex)
        {
            failures.Add("synthetic UAssetAPI property-tree parser fixture");
            output.WriteLine($"  ERR synthetic UAssetAPI property-tree parser fixture: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(
        bool condition,
        string description,
        ICollection<string> failures,
        TextWriter output)
    {
        if (condition)
        {
            output.WriteLine("  ok  " + description);
            return;
        }
        failures.Add(description);
        output.WriteLine("  ERR " + description);
    }
}
