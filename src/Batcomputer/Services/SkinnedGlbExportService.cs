using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace Batcomputer;

/// <summary>Rest-pose correction for tool-owned, non-animated CUE4Parse GLB exports.</summary>
internal static class SkinnedGlbExportService
{
    internal const string Revision = "native-rest-pose-v3";
    internal sealed record Bone(string Name, int Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

    internal static Bone[] Bones(FReferenceSkeleton skeleton) => skeleton.FinalRefBoneInfo.Select((info, i) =>
    {
        var pose = skeleton.FinalRefBonePose[i];
        return new Bone(info.Name.Text, info.ParentIndex,
            new(pose.Translation.X, pose.Translation.Y, pose.Translation.Z),
            new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
            new(pose.Scale3D.X, pose.Scale3D.Y, pose.Scale3D.Z));
    }).ToArray();

    internal static AffineTransform ToGltf(Bone bone)
    {
        // Swapping Y/Z changes handedness. A quaternion is an axial vector: its XYZ
        // part must also change sign. Swapping quaternion components alone inverts
        // the rotation and displaces every child of a rotated bone.
        var t = bone.Translation; var q = bone.Rotation; var s = bone.Scale;
        return new AffineTransform(new Vector3(s.X, s.Z, s.Y),
            Quaternion.Normalize(new(-q.X, -q.Z, -q.Y, q.W)), new Vector3(t.X, t.Z, t.Y) * .01f);
    }

    internal static void CorrectRestPose(ModelRoot model, IReadOnlyList<Bone> bones)
    {
        if (model.LogicalAnimations.Count != 0)
            throw new InvalidDataException("Rest-pose export correction cannot be applied to an animated GLB.");
        if (bones.Count == 0 || bones.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != bones.Count)
            throw new InvalidDataException("The native rig has missing or duplicate bone names.");
        var joints = model.LogicalSkins.SelectMany(s => Enumerable.Range(0, s.JointsCount).Select(i => s.GetJoint(i).Item1)).Distinct().ToArray();
        if (joints.Length != bones.Count || joints.Select(j => j.Name).Distinct(StringComparer.Ordinal).Count() != joints.Length)
            throw new InvalidDataException("The GLB did not retain the complete native joint hierarchy.");
        var byName = joints.ToDictionary(j => j.Name, StringComparer.Ordinal);
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (!byName.TryGetValue(bone.Name, out var joint) || bone.Parent >= i || bone.Parent < -1 ||
                (bone.Parent >= 0 && joint.VisualParent != byName[bones[bone.Parent].Name]) ||
                (bone.Parent < 0 && joint.VisualParent?.IsSkinJoint == true))
                throw new InvalidDataException("The exported hierarchy differs at native bone '" + bone.Name + "'.");
            if (!float.IsFinite(bone.Translation.LengthSquared() + bone.Scale.LengthSquared() + bone.Rotation.LengthSquared()) ||
                bone.Rotation.LengthSquared() < .000001f || bone.Scale.X == 0 || bone.Scale.Y == 0 || bone.Scale.Z == 0)
                throw new InvalidDataException("The native rest transform is invalid at '" + bone.Name + "'.");
        }
        // Capture bind space before editing. Preserve non-identity mesh bind transforms
        // rather than assuming all exporters attach their mesh at the scene origin.
        var bindings = model.LogicalSkins.Select(s =>
        {
            var values = Enumerable.Range(0, s.JointsCount).Select(i => s.GetJoint(i)).ToArray();
            return (Skin: s, Joints: values.Select(v => v.Item1).ToArray(),
                BindSpaces: values.Select(v => v.Item2 * v.Item1.WorldMatrix).ToArray());
        }).ToArray();
        foreach (var bone in bones) byName[bone.Name].LocalTransform = ToGltf(bone);
        foreach (var binding in bindings)
        {
            var corrected = binding.Joints.Select((joint, i) =>
            {
                if (!Matrix4x4.Invert(joint.WorldMatrix, out var inverse))
                    throw new InvalidDataException("The corrected GLB has a singular joint transform: " + joint.Name);
                return (joint, AffineMatrix(binding.BindSpaces[i] * inverse));
            }).ToArray();
            binding.Skin.BindJoints(corrected);
        }
    }

    internal static Matrix4x4 AffineMatrix(Matrix4x4 matrix)
    {
        // Inverting rotated chains can leave M44 at 0.9999999. GLB inverse-bind
        // matrices require the exact affine column, even though the transform is
        // already affine mathematically. Only remove numerical roundoff.
        if (!float.IsFinite(matrix.M14 + matrix.M24 + matrix.M34 + matrix.M44) ||
            Math.Abs(matrix.M14) > .00001f || Math.Abs(matrix.M24) > .00001f ||
            Math.Abs(matrix.M34) > .00001f || Math.Abs(matrix.M44 - 1) > .00001f)
            throw new InvalidDataException("The corrected inverse-bind matrix is not affine.");
        matrix.M14 = matrix.M24 = matrix.M34 = 0; matrix.M44 = 1;
        return matrix;
    }

    internal static void CorrectFile(string file, USkeletalMesh mesh)
    {
        var model = WithExportFileRetry(() => ModelRoot.Load(file));
        CorrectRestPose(model, Bones(mesh.ReferenceSkeleton));
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".glb";
        try
        {
            model.SaveGLB(temporary);
            // Read the completed file before replacing the exporter result.
            _ = WithExportFileRetry(() => ModelRoot.Load(temporary));
            WithExportFileRetry(() => { File.Move(temporary, file, overwrite: true); return true; });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A newly exported GLB can briefly be held by a scanner or sync client. Do not
    // retry malformed geometry, missing paths or permissions failures.
    internal static T WithExportFileRetry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException ex) when (FileLockUtil.IsTransient(ex) && attempt < 5)
            { Thread.Sleep(50 << attempt); }
        }
    }
}
