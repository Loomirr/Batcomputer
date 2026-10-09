using CUE4Parse.UE4.Assets.Exports.Animation;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

/// <summary>Checks pose semantics before a sequence is inserted into a donor's existing graph.</summary>
internal static class AnimationReplacementCompatibilityService
{
    internal sealed record Pair(string Donor, string Replacement, string Role);
    internal sealed record Motion(string Skeleton, string AdditiveMode,
        IReadOnlyList<AnimationSkeletonRemapService.Bone>? Bones = null);
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
                    result = new("AnimSequence", [new(skeletonPath, sequence.AdditiveAnimType.ToString(), bones)]);
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
        if (donor.Bones.Count != replacement.Bones.Count)
            return $"Bone counts differ ({donor.Bones.Count} expected, {replacement.Bones.Count} supplied).";
        try
        {
            // The runtime maps skeleton bones by name, so different serialization order is OK.
            // Plan checks unique names, parents and finite matching reference transforms (including
            // root scale). Requiring equal counts also prevents silently accepting a subset rig.
            _ = AnimationSkeletonRemapService.Plan(Enumerable.Range(0, replacement.Bones.Count).ToArray(),
                replacement.Bones, donor.Bones);
            return null;
        }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    private static bool IsMotion(string? kind) => kind is "AnimSequence" or "AnimMontage" or "AnimComposite" or "AnimBlueprintGeneratedClass" or "BlendSpace" or "BlendSpace1D";
    private static string Canonical(string? package) => UnrealPathUtil.NormalizePackagePath((package ?? "").Replace("LEGOBatmanLotDK/Content/", "/Game/")).Split('.')[0];
    private static string NormalizeMode(string mode) => string.IsNullOrWhiteSpace(mode) ? "AAT_None" : mode.Split("::")[^1];
}
