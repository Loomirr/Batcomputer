namespace Batcomputer;

internal static class AnimationReplacementCompatibilityRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        const string rig = "/Game/Characters/LEGOfig/SKEL_LEGOfig";
        const string donor = "/Game/Animation/LEGOfig/_Shared/Movement/A_RunStop_Minifig";
        const string replacement = "/Game/Mods/Example/A_Stop";
        var pair = new AnimationReplacementCompatibilityService.Pair(donor, replacement, "Run stop");
        AnimationReplacementCompatibilityService.Metadata Meta(string mode, string skeleton = rig, string kind = "AnimSequence") => new(kind, [new(skeleton, mode)]);
        IReadOnlyList<string> Check(string a, string b, string skeleton = rig, string kind = "AnimSequence") =>
            AnimationReplacementCompatibilityService.ValidatePairs([pair], p => p == donor ? Meta(a) : Meta(b, skeleton, kind));
        var results = new List<(bool, string)>
        {
            (Check("AAT_RotationOffsetMeshSpace", "AAT_None").Count == 1, "absolute run-stop is rejected in a mesh-space additive slot"),
            (Check("AAT_None", "AAT_LocalSpaceBase").Count == 1, "additive motion is rejected in a normal full-body slot"),
            (Check("AAT_LocalSpaceBase", "AAT_RotationOffsetMeshSpace").Count == 1, "local-space and mesh-space additive modes are not interchangeable"),
            (Check("EAdditiveAnimationType::AAT_RotationOffsetMeshSpace", "AAT_RotationOffsetMeshSpace").Count == 0, "matching additive motions accept native serialized enum names"),
            (Check("", "AAT_None").Count == 0, "omitted legacy mode retains Unreal's normal-pose default"),
            (Check("AAT_None", "AAT_None", "/Game/Other/Rig").Count == 1, "different replacement skeleton fails closed"),
            (Check("AAT_None", "AAT_None", kind: "AnimMontage").Count == 1, "a sequence slot cannot be filled with a montage"),
            (AnimationReplacementCompatibilityService.ValidatePairs([pair], _ => throw new FileNotFoundException("missing")).Count == 1, "missing native replacement is reported before packaging"),
        };
        var project = new NativeSuitProject { LocomotionOverrides = [new() { DonorSequencePackage = donor, ReplacementPackage = replacement, DonorSequence = "Stop" }] };
        project.AnimationSlotOverrides.Add(new() { DonorPackage = donor, ReplacementPackage = replacement, DonorClass = "AnimSequence", ReplacementClass = "AnimSequence" });
        results.Add((AnimationReplacementCompatibilityService.Pairs(project, "Example").Count == 1, "duplicated locomotion and exact-slot assignments are inspected once"));
        var montagePair = pair with { Role = "Attack" };
        var mixed = AnimationReplacementCompatibilityService.ValidatePairs([montagePair], p => new("AnimMontage",
            p == donor ? [new(rig, "AAT_None")] : [new(rig, "AAT_None"), new(rig, "AAT_LocalSpaceBase")]));
        results.Add((mixed.Count == 1, "montage segment mode mixtures cannot bypass pose compatibility checks"));
        AnimationReplacementCompatibilityService.Metadata Layer(bool swapped) => new("AnimBlueprintGeneratedClass",
            [new(rig, "AAT_None"), new(rig, "AAT_LocalSpaceBase")], new Dictionary<string, AnimationReplacementCompatibilityService.Metadata>
            { ["Idle"] = Meta(swapped ? "AAT_None" : "AAT_LocalSpaceBase"), ["Move"] = Meta(swapped ? "AAT_LocalSpaceBase" : "AAT_None") });
        results.Add((AnimationReplacementCompatibilityService.ValidatePairs([pair], p => Layer(p != donor)).Count == 1,
            "layer inputs are checked individually even when their combined additive modes match"));
        results.Add((AnimationReplacementCompatibilityService.ValidatePairs([pair], _ => Layer(false)).Count == 0,
            "matching mixed absolute/additive layer inputs remain valid"));
        project.AnimationSlotOverrides.Add(new() { DonorPackage = "/Game/Animation/ABP_Climb", ReplacementPackage = "/Game/Mods/Example/ABP_Climb",
            DonorClass = "AnimBlueprintGeneratedClass", ReplacementClass = "AnimBlueprintGeneratedClass", ActionTag = "Animation.Layer.Ability.Climb" });
        results.Add((AnimationReplacementCompatibilityService.Pairs(project, "Example").Any(p => p.Role == "Animation.Layer.Ability.Climb"),
            "animation-layer overrides cannot bypass compatibility validation"));
        project.AbilityLoadout = new() { HeldItemToggle = new() { ItemId = "item", Assets = [new() { Package = replacement }],
            SheathedAnimationReplacements = new(StringComparer.Ordinal) { [donor] = replacement } } };
        var state = AnimationReplacementCompatibilityService.Pairs(project, "Example").Single(p => p.Role == "Held-item state");
        results.Add((state.Replacement == "/Game/Mods/Example/HeldItemToggles/item/Assets/A_Stop",
            "held-item state motion checks resolve the actual staged portable bundle path"));
        return results;
    }
}
