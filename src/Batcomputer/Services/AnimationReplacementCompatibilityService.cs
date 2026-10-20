using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion.Animations;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

/// <summary>Checks pose semantics before a sequence is inserted into a donor's existing graph.</summary>
internal static class AnimationReplacementCompatibilityService
{
    internal sealed record Pair(string Donor, string Replacement, string Role);
    internal sealed record Motion(string Skeleton, string AdditiveMode,
        IReadOnlyList<AnimationSkeletonRemapService.Bone>? Bones = null,
        string? VerifiedExportRoot = null);
    internal sealed record Metadata(string Kind, IReadOnlyList<Motion> Motions,
        IReadOnlyDictionary<string, Metadata>? Slots = null);

    internal static IReadOnlyList<Pair> Pairs(NativeSuitProject project, string owner)
    {
        var pairs = project.LocomotionOverrides.Select(o => new Pair(o.DonorSequencePackage, o.ReplacementPackage, o.DonorSequence))
            .Concat(project.AnimationSlotOverrides.Where(o => IsMotion(o.DonorClass) && IsMotion(o.ReplacementClass))
                .Select(o => new Pair(o.DonorPackage, o.ReplacementPackage, o.ActionTag))).ToList();
        if (project.AbilityLoadout?.HeldItemToggle is { Enabled: true } toggle)
        {
            var redirects = HeldItemToggleService.Redirects(owner, toggle);
            string Staged(string package) => redirects.GetValueOrDefault(package, package);
            pairs.AddRange(toggle.SheathedAnimationReplacements.Select(o => new Pair(Staged(o.Key), Staged(o.Value), "Held-item state")));
        }
        return pairs.Select(p => p with { Donor = CharacterAssetReuseService.Resolve(p.Donor, project.ReleaseAssetAliases),
            Replacement = CharacterAssetReuseService.Resolve(p.Replacement, project.ReleaseAssetAliases) })
            .DistinctBy(p => (Canonical(p.Donor), Canonical(p.Replacement))).ToArray();
    }

    internal static IReadOnlyList<string> Validate(NativeSuitProject project, string contentRoot)
    {
        var owner = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable).Split('/').Skip(3).FirstOrDefault() ?? "";
        var pairs = Pairs(project, owner);
        if (pairs.Count == 0) return [];
        var mappings = AppSettings.Current.EffectiveUsmapPath()
            ?? throw new InvalidDataException("Configure game mappings before checking animation compatibility.");
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), mappings, [contentRoot]);
        var cache = new Dictionary<string, Metadata>(StringComparer.OrdinalIgnoreCase);
        var skeletons = new Dictionary<string, AnimationSkeletonRemapService.Bone[]>(StringComparer.OrdinalIgnoreCase);
        var loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Metadata Read(string package)
        {
            package = Canonical(package);
            if (cache.TryGetValue(package, out var previous)) return previous;
            if (!loading.Add(package)) throw new InvalidDataException("Cyclic animation reference: " + package);
            try
            {
                var exports = provider.LoadPackage(package).GetExports();
                var sequence = exports.OfType<UAnimSequence>().FirstOrDefault();
                Metadata result;
                if (sequence is not null)
                {
                    var skeleton = sequence.Skeleton?.Load<USkeleton>()
                        ?? throw new InvalidDataException("Sequence has no readable skeleton: " + package);
                    var skeletonPath = Canonical(skeleton.GetPathName());
                    if (!skeletons.TryGetValue(skeletonPath, out var bones))
                        skeletons[skeletonPath] = bones = AnimationSkeletonRemapService.Bones(skeleton.ReferenceSkeleton);
                    result = new("AnimSequence", [new(skeletonPath, sequence.AdditiveAnimType.ToString(), bones,
                        VerifyExportRoot(sequence, skeleton, bones, contentRoot, package))]);
                }
                else
                {
                    var data = JArray.FromObject(exports);
                    var cdo = data.FirstOrDefault(e => e["Name"]?.ToString().StartsWith("Default__", StringComparison.Ordinal) == true);
                    var kind = data.Select(e => e["Type"]?.ToString()).FirstOrDefault(IsMotion)
                        ?? (cdo?["Properties"] is JObject properties && properties.Properties().Any(p => p.Name.StartsWith("AnimGraphNode_", StringComparison.Ordinal))
                            ? "AnimBlueprintGeneratedClass" : null);
                    if (kind is null) throw new InvalidDataException("Not a supported motion asset: " + package);
                    if (kind is "AnimBlueprintGeneratedClass" or "BlendSpace" or "BlendSpace1D")
                    {
                        // Validate each named layer input/sample, not only the union of modes.
                        // Otherwise an absolute idle can hide among valid absolute movement
                        // samples and reach a native ApplyAdditive node unchecked.
                        var scope = kind == "AnimBlueprintGeneratedClass" ? cdo?["Properties"] : data[0]?["Properties"];
                        if (scope is null) throw new InvalidDataException("Motion graph has no readable defaults: " + package);
                        var slots = scope.SelectTokens("$..ObjectPath").Where(v =>
                            v.Parent?.Parent?["ObjectName"]?.ToString().Split('\'')[0] is "AnimSequence" or "BlendSpace" or "BlendSpace1D" or "AnimMontage" or "AnimComposite")
                            .ToDictionary(v => v.Path[(scope.Path.Length + 1)..], v => Read(Canonical(v.ToString())), StringComparer.Ordinal);
                        result = new(kind, slots.Values.SelectMany(m => m.Motions).Distinct().ToArray(), slots);
                    }
                    else
                    {
                        var motions = data.SelectTokens("$..AnimReference.ObjectPath").Select(v => Canonical(v.ToString()))
                            .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(p => Read(p).Motions).Distinct().ToArray();
                        result = new(kind, motions);
                    }
                }
                cache[package] = result;
                return result;
            }
            finally { loading.Remove(package); }
        }
        return ValidatePairs(pairs, Read);
    }

    internal static IReadOnlyList<string> ValidatePairs(IEnumerable<Pair> pairs, Func<string, Metadata> read)
    {
        var issues = new List<string>();
        void Check(Metadata donor, Metadata replacement, string slot)
        {
            if (donor.Kind == "AnimSequence" && replacement.Kind != "AnimSequence")
                throw new InvalidDataException("A sequence slot cannot use a montage or composite.");
            if (donor.Slots is { } expectedSlots)
            {
                if (replacement.Kind != donor.Kind || replacement.Slots is not { } actualSlots
                    || !expectedSlots.Keys.Order(StringComparer.Ordinal).SequenceEqual(actualSlots.Keys.Order(StringComparer.Ordinal)))
                    throw new InvalidDataException("Replacement layer/blend-space inputs cannot be matched to the donor's pose slots.");
                foreach (var entry in expectedSlots) Check(entry.Value, actualSlots[entry.Key], slot + " / " + entry.Key);
                return;
            }
            // Empty native placeholder montages have no pose semantics to compare.
            if (donor.Motions.Count == 0 || replacement.Motions.Count == 0) return;
            // Cooked UE motions can use separately named copies of the same rig. Compare the
            // actual hierarchy/rest pose instead of treating asset-path inequality as incompatibility.
            // This is validation only: never relabel a skeleton or rewrite compressed track indices.
            bool Matches(Motion a, Motion b) => SkeletonIssue(a, b) == null;
            void RejectRig(Motion expected, Motion actual) => throw new InvalidDataException(
                $"Replacement skeleton '{Canonical(actual.Skeleton)}' does not match donor '{Canonical(expected.Skeleton)}': " +
                SkeletonIssue(expected, actual) + " Cook/retarget the clip for the donor's rig; renaming the Skeleton asset alone is not a repair.");
            foreach (var actual in replacement.Motions)
                if (!donor.Motions.Any(expected => Matches(expected, actual))) RejectRig(donor.Motions[0], actual);
            foreach (var expected in donor.Motions)
            {
                if (!replacement.Motions.Any(actual => Matches(expected, actual))) RejectRig(expected, replacement.Motions[0]);
                string[] Modes(Metadata metadata) => metadata.Motions.Where(m => Matches(expected, m))
                    .Select(m => NormalizeMode(m.AdditiveMode)).Distinct().Order().ToArray();
                var before = Modes(donor); var after = Modes(replacement);
                if (!before.SequenceEqual(after))
                    throw new InvalidDataException($"{slot}: additive mode mismatch: slot expects {string.Join(", ", before)}, replacement supplies {string.Join(", ", after)}. " +
                        "This can enlarge or distort the character. Use a matching native motion or recook with the correct additive base; changing only the flag is not sufficient.");
            }
        }
        foreach (var pair in pairs)
        {
            try
            {
                var donor = read(Canonical(pair.Donor));
                var replacement = read(Canonical(pair.Replacement));
                Check(donor, replacement, pair.Role);
            }
            catch (Exception ex) { issues.Add($"Animation '{pair.Role}' ({pair.Replacement}) cannot safely replace {pair.Donor}: {ex.Message}"); }
        }
        return issues;
    }

    internal static string? SkeletonIssue(Motion donor, Motion replacement)
    {
        if (Canonical(donor.Skeleton).Equals(Canonical(replacement.Skeleton), StringComparison.OrdinalIgnoreCase)) return null;
        if (donor.Bones == null || replacement.Bones == null)
            return "The skeleton paths differ and their bone hierarchies could not be verified.";
        try
        {
            // The runtime maps skeleton bones by name, so different serialization order is OK.
            // An imported skeleton may also contain peripheral helper/attachment bones. Require
            // the COMPLETE donor hierarchy and rest pose, not equality of the total bone count.
            // Check the full source against itself first so extra branches cannot hide malformed
            // names, transforms, dangling parents or cycles. No compressed tracks are rewritten.
            ValidateHierarchy(donor.Bones);
            ValidateHierarchy(replacement.Bones);
            var actualBones = replacement.Bones;
            var root = donor.Bones.Single(b => string.IsNullOrEmpty(b.Parent));
            var wrapper = replacement.VerifiedExportRoot;
            if (wrapper is not null && !donor.Bones.Any(b => b.Name == wrapper) &&
                replacement.Bones.Any(b => b.Name == root.Name && b.Parent == wrapper) &&
                replacement.Bones.Any(b => b.Name == wrapper && string.IsNullOrEmpty(b.Parent) && IsIdentity(b)))
                actualBones = replacement.Bones.Select(b => b.Name == root.Name ? b with { Parent = "" } : b).ToArray();
            _ = AnimationSkeletonRemapService.Plan(Enumerable.Range(0, donor.Bones.Count).ToArray(),
                donor.Bones, actualBones);
            return null;
        }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    // UE 5.6 SkeletonRemapping maps by name; AnimationDecompression handles track 0
    // first, then the remaining named tracks. An exporter wrapper at track 0 is
    // overwritten by the actual Root track. Prove this ordering, a unit-scale Root,
    // and a fixed exporter unit scale; root motion/locking/additive clips are excluded.
    // No bytes, skeleton names, or compressed indices are changed.
    private static string? VerifyExportRoot(UAnimSequence sequence, USkeleton skeleton,
        IReadOnlyList<AnimationSkeletonRemapService.Bone> bones, string contentRoot, string package)
    {
        var index = Enumerable.Range(0, bones.Count).SingleOrDefault(i => string.IsNullOrEmpty(bones[i].Parent), -1);
        if (index < 0 || bones[index].Name == "Root" || !IsIdentity(bones[index]) ||
            !bones.Any(b => b.Name == "Root" && b.Parent == bones[index].Name)) return null;
        try
        {
            var rootIndex = Enumerable.Range(0, bones.Count).Single(i => bones[i].Name == "Root");
            var map = sequence.CompressedTrackToSkeletonMapTable;
            var properties = JObject.FromObject(sequence)["Properties"];
            if (index != 0 || map.Length < 2 || map[0].BoneTreeIndex != 0 || map[1].BoneTreeIndex != rootIndex ||
                map.Any(t => t.BoneTreeIndex < 0 || t.BoneTreeIndex >= bones.Count) ||
                map.Select(t => t.BoneTreeIndex).Distinct().Count() != map.Length ||
                properties?["bEnableRootMotion"]?.Value<bool>() == true || properties?["bForceRootLock"]?.Value<bool>() == true ||
                NormalizeMode(sequence.AdditiveAnimType.ToString()) != "AAT_None") return null;
            if (sequence.CompressedDataStructure is null)
            {
                var file = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, package);
                if (file is null) return null;
                AnimationCookedPoseValidationService.EnsureAuthoredAclDecoded(file, sequence);
            }
            var decoded = skeleton.ConvertAnims(sequence).Sequences.Single();
            if (decoded.NumFrames < 1 || decoded.Tracks.Count != bones.Count) return null;
            Vector3? wrapperScale = null;
            for (var frame = 0; frame < decoded.NumFrames; frame++)
            {
                var pose = skeleton.ReferenceSkeleton.FinalRefBonePose[index];
                var p = pose.Translation; var q = pose.Rotation; var s = pose.Scale3D;
                decoded.Tracks[index].GetBoneTransform(frame, decoded.NumFrames, ref q, ref p, ref s);
                var scale = new Vector3(s.X, s.Y, s.Z);
                if (!IsIdentity(new(bones[index].Name, "", new(p.X, p.Y, p.Z), new(q.X, q.Y, q.Z, q.W), Vector3.One)) ||
                    (Vector3.Distance(scale, Vector3.One) > .0001f && Vector3.Distance(scale, new Vector3(100)) > .001f) ||
                    (wrapperScale is { } previous && Vector3.Distance(scale, previous) > .0001f)) return null;
                wrapperScale = scale;
                pose = skeleton.ReferenceSkeleton.FinalRefBonePose[rootIndex];
                p = pose.Translation; q = pose.Rotation; s = pose.Scale3D;
                decoded.Tracks[rootIndex].GetBoneTransform(frame, decoded.NumFrames, ref q, ref p, ref s);
                // Root may carry intentional visual movement. Its named track is retained;
                // only the extra export object is ignored by the compatibility comparison.
                var rootRotation = new Quaternion(q.X, q.Y, q.Z, q.W);
                if (!float.IsFinite(new Vector3(p.X, p.Y, p.Z).LengthSquared()) ||
                    !float.IsFinite(rootRotation.LengthSquared()) || Math.Abs(rootRotation.LengthSquared() - 1) > .01f ||
                    !(Vector3.Distance(new(s.X, s.Y, s.Z), Vector3.One) < .0001f)) return null;
            }
            return bones[index].Name;
        }
        catch { return null; } // Unknown compression cannot prove the wrapper layout.
    }

    private static bool IsIdentity(AnimationSkeletonRemapService.Bone bone) =>
        Vector3.Distance(bone.Translation, Vector3.Zero) < .0001f &&
        Vector3.Distance(bone.Scale, Vector3.One) < .00001f &&
        float.IsFinite(bone.Rotation.LengthSquared()) && bone.Rotation.LengthSquared() > .99f &&
        Math.Abs(Quaternion.Dot(Quaternion.Normalize(bone.Rotation), Quaternion.Identity)) > .999999f;

    private static void ValidateHierarchy(IReadOnlyList<AnimationSkeletonRemapService.Bone> bones)
    {
        _ = AnimationSkeletonRemapService.Plan(Enumerable.Range(0, bones.Count).ToArray(), bones, bones);
        var parents = bones.ToDictionary(b => b.Name, b => b.Parent, StringComparer.Ordinal);
        if (bones.Count(b => string.IsNullOrEmpty(b.Parent)) != 1)
            throw new InvalidDataException("The skeleton must contain exactly one root bone.");
        foreach (var bone in bones)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var name = bone.Name; !string.IsNullOrEmpty(name); name = parents[name])
                if (!visited.Add(name))
                    throw new InvalidDataException($"The skeleton has a parent cycle at '{name}'.");
        }
    }

    private static bool IsMotion(string? kind) => kind is "AnimSequence" or "AnimMontage" or "AnimComposite" or "AnimBlueprintGeneratedClass" or "BlendSpace" or "BlendSpace1D";
    private static string Canonical(string? package) => UnrealPathUtil.NormalizePackagePath((package ?? "").Replace("LEGOBatmanLotDK/Content/", "/Game/")).Split('.')[0];
    private static string NormalizeMode(string mode) => string.IsNullOrWhiteSpace(mode) ? "AAT_None" : mode.Split("::")[^1];
}
