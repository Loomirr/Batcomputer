using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse_Conversion.Animations;

namespace Batcomputer;

/// <summary>Checks actual cooked native-index poses, not an editor draft or a skeleton path alone.</summary>
internal static class AnimationCookedPoseValidationService
{
    internal sealed record BoneError(string Bone, float PositionCm, float RotationDegrees, float ScaleDifference);
    internal sealed record Result(int Frames, int Bones, int Samples, float MaxPositionCm, float MaxRotationDegrees,
        float MaxScaleDifference, BoneError[] BoneErrors);
    internal sealed record PreviewRigBone(string Name, int Parent, float[] Transform);
    internal sealed record PreviewTrack(string Bone, float[][] Frames);
    internal sealed record PreviewSamples(float Fps, int FrameCount, PreviewRigBone[] Rig, PreviewTrack[] Tracks);

    internal static PreviewSamples SamplePreview(string uassetPath, UAnimSequence sequence, USkeleton nativeSkeleton)
    {
        EnsureAuthoredAclDecoded(uassetPath, sequence);
        var converted = nativeSkeleton.ConvertAnims(sequence).Sequences.Single();
        var info = nativeSkeleton.ReferenceSkeleton.FinalRefBoneInfo; var pose = nativeSkeleton.ReferenceSkeleton.FinalRefBonePose;
        if (converted.NumFrames < 2 || converted.NumFrames > 901 || converted.Tracks.Count != info.Length)
            throw new InvalidDataException("Cooked animation does not decode onto the complete native preview rig.");
        static float[] View(CUE4Parse.UE4.Objects.Core.Math.FVector p, CUE4Parse.UE4.Objects.Core.Math.FQuat q, CUE4Parse.UE4.Objects.Core.Math.FVector s)
        {
            var length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (length < 1e-6f) throw new InvalidDataException("Cooked preview contains a zero quaternion.");
            return [p.X / 100, p.Z / 100, p.Y / 100, -q.X / length, -q.Z / length, -q.Y / length, q.W / length, s.X, s.Z, s.Y];
        }
        var rig = info.Select((b, i) => new PreviewRigBone(b.Name.Text, b.ParentIndex, View(pose[i].Translation, pose[i].Rotation, pose[i].Scale3D))).ToArray();
        var tracks = info.Select((b, i) => new PreviewTrack(b.Name.Text, Enumerable.Range(0, converted.NumFrames).Select(frame =>
        {
            var p = pose[i].Translation; var q = pose[i].Rotation; var s = pose[i].Scale3D;
            converted.Tracks[i].GetBoneTransform(frame, converted.NumFrames, ref q, ref p, ref s);
            return View(p, q, s);
        }).ToArray())).ToArray();
        return new((converted.NumFrames - 1) / sequence.SequenceLength, converted.NumFrames, rig, tracks);
    }

    internal static Result Validate(string uassetPath, UAnimSequence sequence, USkeleton nativeSkeleton, JsonElement draft)
    {
        EnsureAuthoredAclDecoded(uassetPath, sequence);
        var frames = draft.GetProperty("durationFrames").GetInt32() + 1;
        if (sequence.NumFrames != frames)
            throw new InvalidDataException("Cooked animation frame count differs from the draft.");
        var bones = AnimationSkeletonRemapService.Bones(nativeSkeleton.ReferenceSkeleton);
        _ = AnimationSkeletonRemapService.Plan(sequence.CompressedTrackToSkeletonMapTable.Select(t => t.BoneTreeIndex).ToArray(), bones, bones);
        var converted = nativeSkeleton.ConvertAnims(sequence).Sequences.Single();
        if (converted.NumFrames != frames || converted.Tracks.Count != bones.Length)
            throw new InvalidDataException("Cooked animation could not be decoded on the complete native skeleton.");
        var tracks = draft.GetProperty("tracks").EnumerateArray().ToDictionary(t => t.GetProperty("bone").GetString()!, StringComparer.Ordinal);
        var errors = new List<BoneError>();
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            float maxPosition = 0, maxRotation = 0, maxScale = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                var expectedPosition = bone.Translation; var expectedRotation = bone.Rotation; var expectedScale = bone.Scale;
                if (tracks.TryGetValue(bone.Name, out var track))
                {
                    var (p, q, s) = Sample(track.GetProperty("keys"), frame);
                    expectedPosition += new Vector3(p.X, p.Z, p.Y) * 100;
                    expectedRotation = Quaternion.Normalize(bone.Rotation * new Quaternion(-q.X, -q.Z, -q.Y, q.W));
                    expectedScale *= new Vector3(s.X, s.Z, s.Y);
                }
                var rest = nativeSkeleton.ReferenceSkeleton.FinalRefBonePose[i];
                var actualQ = rest.Rotation; var actualP = rest.Translation; var actualS = rest.Scale3D;
                converted.Tracks[i].GetBoneTransform(frame, frames, ref actualQ, ref actualP, ref actualS);
                var position = Vector3.Distance(expectedPosition, new(actualP.X, actualP.Y, actualP.Z));
                var dot = Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(expectedRotation),
                    Quaternion.Normalize(new(actualQ.X, actualQ.Y, actualQ.Z, actualQ.W)))), 0, 1);
                var rotation = 2 * MathF.Acos(dot) * 180 / MathF.PI;
                var scale = Vector3.Distance(expectedScale, new(actualS.X, actualS.Y, actualS.Z));
                if (!float.IsFinite(position + rotation + scale) || position > .05f || rotation > 1 || scale > .001f)
                    throw new InvalidDataException($"Cooked native pose differs from draft at '{bone.Name}', frame {frame} (position {position:G6} cm, rotation {rotation:G6} degrees, scale {scale:G6}).");
                maxPosition = Math.Max(maxPosition, position); maxRotation = Math.Max(maxRotation, rotation); maxScale = Math.Max(maxScale, scale);
            }
            errors.Add(new(bone.Name, maxPosition, maxRotation, maxScale));
        }
        return new(frames, bones.Length, frames * bones.Length, errors.Max(e => e.PositionCm),
            errors.Max(e => e.RotationDegrees), errors.Max(e => e.ScaleDifference), errors.ToArray());
    }

    internal static (Vector3 P, Quaternion Q, Vector3 S) Sample(JsonElement keys, int frame)
    {
        static (Vector3, Quaternion, Vector3) Read(JsonElement key)
        {
            var p = key.GetProperty("p"); var q = key.GetProperty("q");
            var s = key.TryGetProperty("s", out var scale) ? new Vector3(scale[0].GetSingle(), scale[1].GetSingle(), scale[2].GetSingle()) : Vector3.One;
            return (new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()),
                Quaternion.Normalize(new(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle())), s);
        }
        var beforeFrame = 0; var before = (Vector3.Zero, Quaternion.Identity, Vector3.One); var transition = "linear";
        foreach (var key in keys.EnumerateArray())
        {
            var keyFrame = key.GetProperty("frame").GetInt32(); var after = Read(key);
            if (keyFrame == frame) return after;
            if (keyFrame > frame)
            {
                var alpha = (float)(frame - beforeFrame) / (keyFrame - beforeFrame);
                alpha = transition switch { "hold" => 0, "smooth" => alpha * alpha * (3 - 2 * alpha), _ => alpha };
                return (Vector3.Lerp(before.Item1, after.Item1, alpha), Quaternion.Normalize(Quaternion.Slerp(before.Item2, after.Item2, alpha)), Vector3.Lerp(before.Item3, after.Item3, alpha));
            }
            beforeFrame = keyFrame; before = after;
            transition = key.TryGetProperty("interpolation", out var value) ? value.GetString()! : "linear";
        }
        return before;
    }

    // Authored drafts use UE 5.6's inline ACL stream. Some game extractions expose
    // the default settings under an engine-plugin mount CUE cannot resolve. Bind
    // that same serialized stream explicitly; never guess a codec or frame count.
    internal static void EnsureAuthoredAclDecoded(string uassetPath, UAnimSequence sequence)
    {
        if (sequence.CompressedDataStructure is not null) return;
        if (sequence.BoneCodecDDCHandle != "AnimBoneCompressionCodec_ACL_0")
            throw new InvalidDataException("Cannot validate the authored animation's compression codec.");
        var file = File.Exists(Path.ChangeExtension(uassetPath, ".uexp")) ? Path.ChangeExtension(uassetPath, ".uexp") : uassetPath;
        var bytes = File.ReadAllBytes(file); var map = sequence.CompressedTrackToSkeletonMapTable.Select(t => t.BoneTreeIndex).ToArray();
        AnimationSkeletonRemapService.RemapBytes(bytes, map, map, out var tableOffset);
        var offset = tableOffset + (map.Length + 1) * 4;
        int ReadInt()
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("Truncated authored compressed animation metadata.");
            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)); offset += 4; return value;
        }
        if (ReadInt() != 0) throw new InvalidDataException("Unexpected material curves in an authored motion-only sequence.");
        var length = ReadInt(); var bulk = ReadInt(); var dataOffset = offset;
        if (bulk != 0 || length < 32 || length > bytes.Length - offset)
            throw new InvalidDataException("Unsupported authored compressed animation stream layout.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8)) != 0xAC11AC11 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 16)) != map.Length)
            throw new InvalidDataException("Authored ACL header does not match the cooked track table.");
        offset += length;
        for (var i = 0; i < 2; i++)
        {
            var characters = ReadInt();
            if (characters <= 0 || characters > bytes.Length - offset) throw new InvalidDataException("Invalid authored ACL codec metadata.");
            offset += characters;
        }
        var curveBytes = ReadInt();
        if (curveBytes != 0) throw new InvalidDataException("Unexpected curves in an authored motion-only sequence.");
        var serializedSamples = ReadInt();
        var frames = AuthoredAclFrameCount(bytes.AsSpan(dataOffset, length), serializedSamples);
        var data = (FACLCompressedAnimData)new UAnimBoneCompressionCodec_ACL().AllocateAnimData();
        data.Bind(bytes.AsSpan(dataOffset, length).ToArray());
        typeof(FACLCompressedAnimData).GetProperty(nameof(FACLCompressedAnimData.CompressedNumberOfFrames), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(data, frames);
        sequence.CompressedDataStructure = data; sequence.NumFrames = frames;
    }

    internal static int AuthoredAclFrameCount(ReadOnlySpan<byte> stream, int serializedSamples)
    {
        if (stream.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(stream[8..]) != 0xAC11AC11)
            throw new InvalidDataException("Invalid authored ACL stream header.");
        var samples = BinaryPrimitives.ReadInt32LittleEndian(stream[20..]);
        // UE 5.6's ACL 2.1 header explicitly records wrap optimization in bit 30.
        // It can remove the duplicate closing sample while preserving the original
        // serialized timeline. The native decoder reconstructs that sample by wrapping.
        var wrapOptimized = BinaryPrimitives.ReadUInt16LittleEndian(stream[12..]) == 10 &&
                            (BinaryPrimitives.ReadUInt32LittleEndian(stream[28..]) & (1u << 30)) != 0;
        if (samples is < 2 or > 901 || serializedSamples is < 2 or > 901 ||
            (samples != serializedSamples && !(wrapOptimized && serializedSamples == samples + 1)))
            throw new InvalidDataException("Authored ACL sample count disagrees with serialized metadata.");
        return serializedSamples;
    }
}
