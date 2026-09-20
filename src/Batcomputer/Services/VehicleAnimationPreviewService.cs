using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion.Animations;

namespace Batcomputer;

/// <summary>
/// Samples a donor's native mechanics animations (canopy, launcher covers) for the workshop preview.
/// Read-only: nothing here is staged or changes how the game animates the vehicle.
/// </summary>
internal static class VehicleAnimationPreviewService
{
    // Transforms use the workshop glTF convention: metres, (X, Z, Y) with the handedness change, as [px, py, pz, qx, qy, qz, qw, sx, sy, sz].
    internal sealed record Track(string Bone, float[] Reference, float[][] Frames);
    // Evaluate on the native hierarchy; the viewer applies model-space deltas to the preview skeleton.
    internal sealed record RigBone(string Name, int Parent, float[] Reference);
    internal sealed record Clip(string Id, string Label, string Package, float FramesPerSecond, int FrameCount, IReadOnlyList<Track> Tracks);

    internal const int MaxFrames = 600;

    internal static IReadOnlyList<Clip> Read(IFileProvider provider, VehicleDonorService.Donor donor, ICollection<string> warnings, CancellationToken cancellation = default)
    {
        var clips = new List<Clip>();
        foreach (var animation in donor.Animations)
        {
            cancellation.ThrowIfCancellationRequested();
            try { clips.Add(Sample(provider, donor, animation)); }
            catch (Exception ex) { warnings.Add("Animation preview " + animation.Label + ": " + ex.Message); }
        }
        return clips;
    }

    private static Clip Sample(IFileProvider provider, VehicleDonorService.Donor donor, VehicleAnimation animation)
    {
        var sequence = provider.LoadPackageObject<UAnimSequence>(animation.Package);
        var skeleton = sequence.Skeleton?.Load<USkeleton>() ?? throw new InvalidDataException("the animation has no skeleton.");
        var skeletonPackage = skeleton.GetPathName().Split('.')[0];
        if (!string.Equals(skeletonPackage, donor.Skeleton, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("it targets " + skeletonPackage + ", not this driving base.");
        var converted = AnimConverter.ConvertAnims(skeleton, sequence).Sequences.FirstOrDefault() ?? throw new InvalidDataException("the animation has no sampled sequence.");
        var bones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        VehicleAssetService.Require(converted.Tracks.Count == bones.Length, "the animation track map does not match its skeleton.");
        var frames = Math.Clamp(converted.NumFrames, 1, MaxFrames);
        // Sample so the first and last samples land on the first and last keys.
        float KeyAt(int sample) => frames > 1 ? sample * (converted.NumFrames - 1f) / (frames - 1) : 0;
        var tracks = new List<Track>();
        for (int i = 0; i < bones.Length; i++)
        {
            var reference = skeleton.ReferenceSkeleton.FinalRefBonePose[i];
            var referenceTransform = Transform(reference.Translation, reference.Rotation, reference.Scale3D);
            var sampled = new float[frames][];
            var moves = false;
            for (int frame = 0; frame < frames; frame++)
            {
                var q = reference.Rotation; var p = reference.Translation; var scale = reference.Scale3D;
                converted.Tracks[i].GetBoneTransform(KeyAt(frame), converted.NumFrames, ref q, ref p, ref scale);
                sampled[frame] = Transform(p, q, scale);
                moves |= Differs(sampled[frame], referenceTransform);
            }
            if (moves) tracks.Add(new(bones[i].Name.Text, referenceTransform, sampled));
        }
        if (tracks.Count == 0) throw new InvalidDataException("no bone of this rig moves in it.");
        // CUE4Parse reports NumFrames / play length; the keys span NumFrames - 1 intervals.
        var length = converted.FramesPerSecond > 0 ? converted.NumFrames / converted.FramesPerSecond : frames / 30f;
        var rate = frames > 1 ? (frames - 1) / length : 30;
        return new(UnrealPathUtil.AssetName(animation.Package), animation.Label, animation.Package, rate, frames, tracks);
    }

    internal static IReadOnlyList<RigBone> Rig(CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh mesh)
    {
        var info = mesh.ReferenceSkeleton.FinalRefBoneInfo; var pose = mesh.ReferenceSkeleton.FinalRefBonePose;
        return Enumerable.Range(0, info.Length).Select(i => new RigBone(info[i].Name.Text, info[i].ParentIndex, Transform(pose[i].Translation, pose[i].Rotation, pose[i].Scale3D))).ToArray();
    }

    internal static float[] Transform(CUE4Parse.UE4.Objects.Core.Math.FVector p, CUE4Parse.UE4.Objects.Core.Math.FQuat q, CUE4Parse.UE4.Objects.Core.Math.FVector scale)
    {
        var length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (length < 1e-6f) length = 1;
        return [p.X / 100, p.Z / 100, p.Y / 100, -q.X / length, -q.Z / length, -q.Y / length, q.W / length, scale.X, scale.Z, scale.Y];
    }

    private static bool Differs(float[] a, float[] b)
    {
        for (int i = 0; i < 3; i++) if (MathF.Abs(a[i] - b[i]) > 1e-4f || MathF.Abs(a[7 + i] - b[7 + i]) > 1e-4f) return true;
        // q and -q are the same rotation.
        var dot = MathF.Abs(a[3] * b[3] + a[4] * b[4] + a[5] * b[5] + a[6] * b[6]);
        return dot < 0.99999f;
    }
}
