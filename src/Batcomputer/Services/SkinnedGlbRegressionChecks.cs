using System.Numerics;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace Batcomputer;

internal static class SkinnedGlbRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool pass, string name) => results.Add((pass, name));
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        SkinnedGlbExportService.Bone[] bones = [new("Root", -1, new(10,20,30), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f), Vector3.One),
            new("Wheel", 0, new(80,40,30), Quaternion.CreateFromYawPitchRoll(.2f,.4f,.6f), Vector3.One)];
        var model = ModelRoot.CreateModel(); var scene = model.UseScene(0); var root = scene.CreateNode("Root"); var wheel = root.CreateNode("Wheel");
        root.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, Vector3.Zero);
        wheel.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, Vector3.One);
        var bindSpace = Matrix4x4.CreateTranslation(2,3,4); var skin = model.CreateSkin();
        (Node, Matrix4x4) Bind(Node n) { Matrix4x4.Invert(n.WorldMatrix, out var inverse); return (n, bindSpace * inverse); }
        skin.BindJoints([Bind(root), Bind(wheel)]);
        var meshNode = scene.CreateNode("RegressionSkinnedBody");
        meshNode.Mesh = model.CreateMesh("RegressionSkinnedBody");
        meshNode.Skin = skin;
        SkinnedGlbExportService.CorrectRestPose(model, bones);
        Check(meshNode.LocalMatrix == Matrix4x4.Identity, "skeletal correction keeps skinned mesh node transforms legal (identity), preventing missing viewer meshes");
        var native = Matrix4x4.CreateFromQuaternion(bones[1].Rotation) * Matrix4x4.CreateTranslation(bones[1].Translation) *
            Matrix4x4.CreateFromQuaternion(bones[0].Rotation) * Matrix4x4.CreateTranslation(bones[0].Translation);
        var expected = new Vector3(native.M41, native.M43, native.M42) * .01f;
        Check(Vector3.Distance(wheel.WorldMatrix.Translation, expected) < .000001f, "GLB child joints follow reflected native rotations, not swapped quaternion components");
        var expectedQ = Quaternion.Normalize(new(-bones[0].Rotation.X, -bones[0].Rotation.Z, -bones[0].Rotation.Y, bones[0].Rotation.W));
        Check(Math.Abs(Quaternion.Dot(root.LocalTransform.Rotation, expectedQ)) > .999999f, "GLB quaternion reflection uses the correct handedness");
        bool BindPreserved() => Enumerable.Range(0, skin.JointsCount).All(i => {
            var (joint, inverse) = skin.GetJoint(i); var matrix = inverse * joint.WorldMatrix;
            return Vector3.Distance(Vector3.Transform(new(2,5,7), matrix), Vector3.Transform(new(2,5,7), bindSpace)) < .00001f;
        });
        Check(BindPreserved(), "GLB correction preserves non-identity bind space and rest geometry");
        var prior = wheel.WorldMatrix; SkinnedGlbExportService.CorrectRestPose(model, bones);
        Check(prior == wheel.WorldMatrix && BindPreserved(), "GLB rest correction can be repeated without moving joints or changing skin bind space");
        Check(Reject(() => SkinnedGlbExportService.CorrectRestPose(model, [bones[0], bones[1] with { Parent = -1 }])), "GLB correction rejects a mismatched native hierarchy");
        Check(Reject(() => SkinnedGlbExportService.CorrectRestPose(model, [bones[0], bones[1] with { Scale = Vector3.Zero }])), "GLB correction rejects singular native transforms");
        var rounded = Matrix4x4.CreateTranslation(3, 4, 5); rounded.M44 = .9999999f;
        var affine = SkinnedGlbExportService.AffineMatrix(rounded);
        Check(affine.M44 == 1 && affine.Translation == rounded.Translation, "GLB inverse-bind matrices remove affine roundoff without moving geometry");
        rounded.M44 = .8f;
        Check(Reject(() => SkinnedGlbExportService.AffineMatrix(rounded)), "GLB affine correction rejects real non-affine transforms");
        Check(SkinnedRigComparisonService.Compare(bones, bones).Passed, "rig comparison accepts unchanged native transforms");
        Check(!SkinnedRigComparisonService.Compare(bones, [bones[0] with { Scale = bones[0].Scale * 100 }, bones[1]]).Passed,
            "cooked rig comparison rejects a 100x root scale even when child local poses match");
        Check(!SkinnedRigComparisonService.Compare(bones, [bones[0], bones[1] with { Scale = bones[1].Scale * 100 }]).Passed,
            "rig comparison rejects centimetre scale on a deforming child bone");
        var report = SkinnedRigComparisonService.Compare(bones, [bones[0], bones[1] with { Translation = bones[1].Translation + Vector3.UnitX, Parent = -1 }]);
        Check(!report.Passed && report.Bones[1].TranslationCm == 1 && report.Bones[1].ExpectedParent == "Root" && report.Bones[1].ActualParent == "", "rig diagnostic identifies the moved bone and wrong parent");
        Check(!SkinnedRigComparisonService.Compare(bones, [bones[0]]).Passed && !SkinnedRigComparisonService.Compare(bones, [bones[0], bones[0]]).Passed,
            "rig comparison rejects missing and duplicate bones");
        Check(!SkinnedRigComparisonService.Compare(bones, [bones[0], bones[1] with { Rotation = new(float.NaN,0,0,1) }]).Passed, "rig comparison rejects non-finite rotations");
        SkinnedGlbExportService.Bone[] vehicleRig = [
            new("Root", -1, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f), Vector3.One),
            new("ChassisAttach_FL", 0, new(20, 10, 5), Quaternion.Identity, Vector3.One),
            new("Wheel_FL", 1, new(0, 0, -5), Quaternion.Identity, Vector3.One)];
        var localRaise = Vector3.Transform(Vector3.UnitZ * 25, Quaternion.Inverse(vehicleRig[0].Rotation));
        var raised = vehicleRig.ToArray(); raised[1] = raised[1] with { Translation = raised[1].Translation + localRaise };
        Check(!SkinnedRigComparisonService.Compare(vehicleRig, raised).Passed && SkinnedRigComparisonService.Compare(vehicleRig, raised, allowVehicleWheelRaise: true).Passed,
            "vehicle-only wheel mounts may rise in component-space Z while the wheel child stays native");
        raised[1] = raised[1] with { Translation = vehicleRig[1].Translation + Vector3.UnitX * 25 };
        Check(!SkinnedRigComparisonService.Compare(vehicleRig, raised, allowVehicleWheelRaise: true).Passed,
            "sideways wheel-mount movement is not treated as a larger tire");
        raised[1] = raised[1] with { Translation = vehicleRig[1].Translation + Vector3.Transform(Vector3.UnitZ * 101, Quaternion.Inverse(vehicleRig[0].Rotation)) };
        Check(!SkinnedRigComparisonService.Compare(vehicleRig, raised, allowVehicleWheelRaise: true).Passed,
            "wheel-mount raise remains bounded to one meter");
        raised = vehicleRig.ToArray(); raised[2] = raised[2] with { Translation = raised[2].Translation + Vector3.UnitZ };
        Check(!SkinnedRigComparisonService.Compare(vehicleRig, raised, allowVehicleWheelRaise: true).Passed,
            "wheel and other animation bones must retain their native local pose");
        return results;
    }
}
