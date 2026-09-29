using System.Buffers.Binary;
using System.Numerics;

namespace Batcomputer;

internal static class AnimationSkeletonRemapRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        static AnimationSkeletonRemapService.Bone B(string name, string parent = "") =>
            new(name, parent, Vector3.Zero, Quaternion.Identity, Vector3.One);
        AnimationSkeletonRemapService.Bone[] source = [B("Root"), B("Head", "Root"), B("HeadAttach", "Head"), B("Arm", "Root")];
        AnimationSkeletonRemapService.Bone[] target = [source[0], source[1], source[3], source[2]];
        var plan = AnimationSkeletonRemapService.Plan([2, 3, 1], source, target);
        Check(plan.Tracks.Select(t => t.TargetIndex).SequenceEqual([3, 2, 1]) &&
              plan.Tracks.Select(t => t.Bone).SequenceEqual(["HeadAttach", "Arm", "Head"]),
            "cooked track remap retains track order and resolves a reordered head attachment by name", failures, output);
        var bytes = Enumerable.Repeat((byte)0xAD, 40).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(7), 3);
        for (var i = 0; i < 3; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(11 + i * 4), new[] { 2, 3, 1 }[i]);
        var patched = AnimationSkeletonRemapService.RemapBytes(bytes, [2, 3, 1], [3, 2, 1], out var offset);
        Check(offset == 7 && patched.Length == bytes.Length && patched.AsSpan(0, 11).SequenceEqual(bytes.AsSpan(0, 11)) &&
              patched.AsSpan(23).SequenceEqual(bytes.AsSpan(23)) && BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(11)) == 3,
            "cooked track remap changes only index integers, preserving header, length and compressed payload", failures, output);
        Check(Reject(() => AnimationSkeletonRemapService.RemapBytes([.. bytes, .. bytes], [2, 3, 1], [3, 2, 1], out _)) &&
              Reject(() => AnimationSkeletonRemapService.RemapBytes(bytes[..20], [2, 3, 1], [3, 2, 1], out _)),
            "cooked track remap refuses ambiguous and truncated serialized tables", failures, output);
        Check(Reject(() => AnimationSkeletonRemapService.Plan([2], source, target[..3])) &&
              Reject(() => AnimationSkeletonRemapService.Plan([2], [source[0], source[1], source[1]], target)) &&
              Reject(() => AnimationSkeletonRemapService.Plan([2, 2], source, target)) &&
              Reject(() => AnimationSkeletonRemapService.Plan([99], source, target)),
            "cooked track remap rejects missing or duplicate names and invalid or repeated indices", failures, output);
        var wrongParent = (AnimationSkeletonRemapService.Bone[])target.Clone(); wrongParent[3] = wrongParent[3] with { Parent = "Arm" };
        var wrongPose = (AnimationSkeletonRemapService.Bone[])target.Clone(); wrongPose[3] = wrongPose[3] with { Translation = new(0, 1, 0) };
        Check(Reject(() => AnimationSkeletonRemapService.Plan([2], source, wrongParent)) &&
              Reject(() => AnimationSkeletonRemapService.Plan([2], source, wrongPose)),
            "cooked track remap refuses incompatible parent and native local rest pose", failures, output);
        var unchanged = AnimationSkeletonRemapService.RemapBytes(bytes, [2, 3, 1], [2, 3, 1], out _);
        Check(unchanged.AsSpan().SequenceEqual(bytes), "matching cooked track order is a byte-for-byte no-op", failures, output);
        var unitSource = (AnimationSkeletonRemapService.Bone[])source.Clone(); unitSource[0] = source[0] with { Scale = new(100) };
        var normalized = AnimationSkeletonRemapService.NormalizeAuthoredSourceSkeleton(unitSource, target, source, out var rootUnits);
        Check(rootUnits && normalized[0].Scale == Vector3.One && unitSource[0].Scale == new Vector3(100) &&
              normalized.Skip(1).SequenceEqual(unitSource.Skip(1)),
            "authoring remap recognizes only the verified imported Root x100 metadata without modifying source skeleton data", failures, output);
        var wrongUnits = (AnimationSkeletonRemapService.Bone[])unitSource.Clone(); wrongUnits[0] = wrongUnits[0] with { Scale = new(99) };
        var wrongChild = (AnimationSkeletonRemapService.Bone[])unitSource.Clone(); wrongChild[1] = wrongChild[1] with { Translation = new(1, 0, 0) };
        Check(Reject(() => AnimationSkeletonRemapService.NormalizeAuthoredSourceSkeleton(wrongUnits, target, source, out _)) &&
              Reject(() => AnimationSkeletonRemapService.NormalizeAuthoredSourceSkeleton(wrongChild, target, source, out _)) &&
              Reject(() => AnimationSkeletonRemapService.NormalizeAuthoredSourceSkeleton(unitSource, target, unitSource, out _)) &&
              Reject(() => AnimationSkeletonRemapService.NormalizeAuthoredSourceSkeleton(unitSource[..3], target, source[..3], out _)),
            "authoring remap refuses arbitrary root scale, child rest mismatch, unnormalized or incomplete author mesh", failures, output);
    }

    private static bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
    private static void Check(bool passed, string description, List<string> failures, TextWriter output)
    {
        output.WriteLine((passed ? "PASS" : "FAIL") + "  " + description);
        if (!passed) failures.Add(description);
    }
}
