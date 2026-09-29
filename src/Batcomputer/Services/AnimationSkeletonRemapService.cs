using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

namespace Batcomputer;

/// <summary>
/// Rebinds cooked track indices by bone name. An FBX mesh's reference order is not
/// necessarily the game's USkeleton order; repathing the skeleton alone is unsafe.
/// </summary>
internal static class AnimationSkeletonRemapService
{
    internal const string Revision = "native-skeleton-track-map-v4";
    internal sealed record Bone(string Name, string Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);
    internal sealed record Track(int TrackIndex, string Bone, int SourceIndex, int TargetIndex);
    internal sealed record Result(string DataFile, int TableOffset, int ChangedTracks, Track[] Tracks,
        string PayloadOutsideTableSha256, float MaxRestTranslationCm, float MaxRestRotationDegrees, float MaxRestScaleDifference,
        bool NormalizedImportedRootUnits = false);

    internal static Bone[] Bones(FReferenceSkeleton skeleton) => skeleton.FinalRefBoneInfo.Select((info, i) =>
    {
        var pose = skeleton.FinalRefBonePose[i];
        return new Bone(info.Name.Text, info.ParentIndex >= 0 ? skeleton.FinalRefBoneInfo[info.ParentIndex].Name.Text : "",
            new(pose.Translation.X, pose.Translation.Y, pose.Translation.Z),
            new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
            new(pose.Scale3D.X, pose.Scale3D.Y, pose.Scale3D.Z));
    }).ToArray();

    internal static Result RemapCookedTrackTable(string uassetPath, UAnimSequence sequence,
        USkeleton importedSkeleton, USkeleton nativeSkeleton) =>
        RemapCookedTrackTable(uassetPath, sequence.CompressedTrackToSkeletonMapTable.Select(t => t.BoneTreeIndex).ToArray(),
            Bones(importedSkeleton.ReferenceSkeleton), Bones(nativeSkeleton.ReferenceSkeleton));

    internal static Result RemapCookedTrackTable(string uassetPath, UAnimSequence sequence,
        USkeleton importedSkeleton, USkeleton nativeSkeleton, USkeletalMesh normalizedAuthorMesh)
    {
        var native = Bones(nativeSkeleton.ReferenceSkeleton);
        var source = NormalizeAuthoredSourceSkeleton(Bones(importedSkeleton.ReferenceSkeleton), native,
            Bones(normalizedAuthorMesh.ReferenceSkeleton), out var normalized);
        return RemapCookedTrackTable(uassetPath, sequence.CompressedTrackToSkeletonMapTable.Select(t => t.BoneTreeIndex).ToArray(),
            source, native) with { NormalizedImportedRootUnits = normalized };
    }

    internal static Bone[] NormalizeAuthoredSourceSkeleton(IReadOnlyList<Bone> sourceBones, IReadOnlyList<Bone> nativeBones,
        IReadOnlyList<Bone> normalizedMeshBones, out bool normalized)
    {
        // The strict editor importer proves the mesh rest pose. Recheck its ACTUAL
        // cooked reference pose against the runtime USkeleton, not its mesh order.
        if (normalizedMeshBones.Count != nativeBones.Count ||
            !normalizedMeshBones.Select(b => b.Name).Order(StringComparer.Ordinal).SequenceEqual(nativeBones.Select(b => b.Name).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("The authoring mesh does not contain the complete native skeleton bone set.");
        _ = Plan(Enumerable.Range(0, normalizedMeshBones.Count).ToArray(), normalizedMeshBones, nativeBones);
        if (sourceBones.Count != normalizedMeshBones.Count ||
            !sourceBones.Select(b => b.Name).Order(StringComparer.Ordinal).SequenceEqual(normalizedMeshBones.Select(b => b.Name).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Imported USkeleton and normalized author mesh have different bone sets.");
        var source = sourceBones.ToArray(); normalized = false;
        if (source.Count(b => string.IsNullOrEmpty(b.Parent)) == 1 &&
            source.First(b => string.IsNullOrEmpty(b.Parent)) is { Name: "Root" } root)
        {
            var target = nativeBones.Single(b => b.Name == "Root");
            // Only the already diagnosed uniform FBX unit representation is allowed.
            // Position, rotation, all children and mesh Root must still pass Plan.
            if (Vector3.Distance(root.Scale, new(100)) < .0001f && Vector3.Distance(target.Scale, Vector3.One) < .00001f)
            {
                var index = Array.FindIndex(source, b => b.Name == "Root");
                source[index] = root with { Scale = Vector3.One }; normalized = true;
            }
        }
        _ = Plan(Enumerable.Range(0, source.Length).ToArray(), source, nativeBones);
        return source;
    }

    internal static Result RemapCookedTrackTable(string uassetPath, int[] sourceTrackIndices,
        IReadOnlyList<Bone> sourceBones, IReadOnlyList<Bone> targetBones)
    {
        var (tracks, translation, rotation, scale) = Plan(sourceTrackIndices, sourceBones, targetBones);
        var dataFile = File.Exists(Path.ChangeExtension(uassetPath, ".uexp")) ? Path.ChangeExtension(uassetPath, ".uexp") : uassetPath;
        var bytes = File.ReadAllBytes(dataFile);
        var patched = RemapBytes(bytes, sourceTrackIndices, tracks.Select(t => t.TargetIndex).ToArray(), out var offset);
        var outside = HashOutsideTable(bytes, offset, sourceTrackIndices.Length);
        if (outside != HashOutsideTable(patched, offset, sourceTrackIndices.Length))
            throw new InvalidDataException("Skeleton remap changed compressed animation data outside its bone-index table.");
        // All checks precede the write. Length, track order and compressed payload remain unchanged.
        if (!bytes.AsSpan().SequenceEqual(patched)) File.WriteAllBytes(dataFile, patched);
        return new Result(dataFile, offset, tracks.Count(t => t.SourceIndex != t.TargetIndex), tracks, outside,
            translation, rotation, scale);
    }

    internal static (Track[] Tracks, float TranslationCm, float RotationDegrees, float ScaleDifference) Plan(
        int[] sourceTrackIndices, IReadOnlyList<Bone> sourceBones, IReadOnlyList<Bone> targetBones)
    {
        static Dictionary<string, int> Index(IReadOnlyList<Bone> bones)
        {
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < bones.Count; i++)
                if (string.IsNullOrWhiteSpace(bones[i].Name) || !names.TryAdd(bones[i].Name, i))
                    throw new InvalidDataException("Cannot remap a skeleton with missing or duplicate bone names.");
            if (bones.Count == 0) throw new InvalidDataException("Cannot remap an empty skeleton.");
            return names;
        }
        var sourceIndex = Index(sourceBones); var targetIndex = Index(targetBones);
        float maxTranslation = 0, maxRotation = 0, maxScale = 0;
        foreach (var source in sourceBones)
        {
            if (!targetIndex.TryGetValue(source.Name, out var target))
                throw new InvalidDataException("The native skeleton has no bone named '" + source.Name + "'.");
            var destination = targetBones[target];
            if (source.Parent != destination.Parent || (!string.IsNullOrEmpty(source.Parent) && !sourceIndex.ContainsKey(source.Parent)))
                throw new InvalidDataException("The native skeleton parent differs at '" + source.Name + "'.");
            var translation = Vector3.Distance(source.Translation, destination.Translation);
            var dot = Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(source.Rotation), Quaternion.Normalize(destination.Rotation))), 0, 1);
            var rotation = 2 * MathF.Acos(dot) * 180 / MathF.PI;
            var scale = Vector3.Max(Vector3.Abs(source.Scale - destination.Scale), Vector3.Zero);
            var scaleDifference = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
            if (!float.IsFinite(translation + rotation + scaleDifference) || translation > .01f || rotation > .1f || scaleDifference > .0001f)
                throw new InvalidDataException($"Native rest transform differs at '{source.Name}' (position {translation:G6} cm, rotation {rotation:G6} degrees, scale {scaleDifference:G6}).");
            maxTranslation = Math.Max(maxTranslation, translation); maxRotation = Math.Max(maxRotation, rotation); maxScale = Math.Max(maxScale, scaleDifference);
        }
        if (sourceTrackIndices.Length == 0 || sourceTrackIndices.Distinct().Count() != sourceTrackIndices.Length)
            throw new InvalidDataException("The cooked animation contains no tracks or repeated skeleton indices.");
        var tracks = sourceTrackIndices.Select((index, track) =>
        {
            if (index < 0 || index >= sourceBones.Count) throw new InvalidDataException("The cooked animation contains an out-of-range skeleton index.");
            var bone = sourceBones[index].Name;
            return new Track(track, bone, index, targetIndex[bone]);
        }).ToArray();
        return (tracks, maxTranslation, maxRotation, maxScale);
    }

    internal static byte[] RemapBytes(byte[] bytes, int[] sourceIndices, int[] targetIndices, out int tableOffset)
    {
        if (sourceIndices.Length == 0 || sourceIndices.Length != targetIndices.Length || sourceIndices.Any(i => i < 0) || targetIndices.Any(i => i < 0))
            throw new InvalidDataException("The cooked track mapping is empty or invalid.");
        var pattern = new byte[checked((sourceIndices.Length + 1) * sizeof(int))];
        BinaryPrimitives.WriteInt32LittleEndian(pattern, sourceIndices.Length);
        for (var i = 0; i < sourceIndices.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan((i + 1) * 4), sourceIndices[i]);
        tableOffset = -1;
        for (var i = 0; i <= bytes.Length - pattern.Length; i++)
        {
            if (!bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) continue;
            if (tableOffset >= 0) throw new InvalidDataException("The serialized cooked bone-index table is ambiguous; animation was not changed.");
            tableOffset = i;
        }
        if (tableOffset < 0) throw new InvalidDataException("The serialized cooked bone-index table was not found; animation was not changed.");
        var result = (byte[])bytes.Clone();
        for (var i = 0; i < targetIndices.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tableOffset + (i + 1) * 4), targetIndices[i]);
        return result;
    }

    private static string HashOutsideTable(byte[] bytes, int offset, int count)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, offset));
        hash.AppendData(bytes.AsSpan(offset + (count + 1) * 4));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
