using System.Numerics;
using System.Buffers.Binary;
using System.Text.Json;

namespace Batcomputer;

internal static class AnimationCookedPoseValidationRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        const string linear = """[{"frame":0,"p":[0,0,0],"q":[0,0,0,1],"s":[1,1,1]},{"frame":10,"p":[2,4,6],"q":[0,0,1,0],"s":[3,3,3]}]""";
        static (Vector3 P, Quaternion Q, Vector3 S) Sample(string json, int frame)
        {
            using var doc = JsonDocument.Parse(json);
            return AnimationCookedPoseValidationService.Sample(doc.RootElement, frame);
        }
        var midpoint = Sample(linear, 5);
        Check(Vector3.Distance(midpoint.P, new(1, 2, 3)) < .00001f && Vector3.Distance(midpoint.S, new(2, 2, 2)) < .00001f &&
              Math.Abs(Math.Abs(midpoint.Q.Z) - MathF.Sqrt(.5f)) < .00001f,
            "cooked pose validator reconstructs sparse position/scale and shortest-path quaternion interpolation", failures, output);
        var held = Sample(linear.Replace("\"frame\":0", "\"frame\":0,\"interpolation\":\"hold\""), 9);
        Check(held.P == Vector3.Zero && held.S == Vector3.One && held.Q == Quaternion.Identity,
            "cooked pose validator respects held keys", failures, output);
        var smooth = Sample(linear.Replace("\"frame\":0", "\"frame\":0,\"interpolation\":\"smooth\""), 2);
        Check(Math.Abs(smooth.P.X - .208f) < .00001f,
            "cooked pose validator matches authoring smoothstep timing", failures, output);
        var legacy = Sample("""[{"frame":10,"p":[2,0,0],"q":[0,0,0,-1]}]""", 5);
        var after = Sample(linear, 20);
        Check(legacy.P.X == 1 && legacy.S == Vector3.One && Math.Abs(legacy.Q.W) == 1 && after.P == new Vector3(2, 4, 6),
            "cooked pose validator matches implicit neutral start, legacy unit scale, antipodal rotation and final-key hold", failures, output);
        var acl = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(8), 0xAC11AC11);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(12), 10);
        BinaryPrimitives.WriteInt32LittleEndian(acl.AsSpan(20), 30);
        Check(AnimationCookedPoseValidationService.AuthoredAclFrameCount(acl, 30) == 30,
            "authored ACL retains an exact non-optimized sample count", failures, output);
        static bool Rejects(byte[] stream, int samples) { try { AnimationCookedPoseValidationService.AuthoredAclFrameCount(stream, samples); return false; } catch (InvalidDataException) { return true; } }
        Check(Rejects(acl, 31), "authored ACL rejects an unexplained missing endpoint", failures, output);
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(28), 1u << 30);
        Check(AnimationCookedPoseValidationService.AuthoredAclFrameCount(acl, 31) == 31 && Rejects(acl, 32),
            "authored ACL accepts only its explicitly wrap-optimized duplicate endpoint", failures, output);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(12), 9);
        Check(Rejects(acl, 31), "authored ACL does not guess wrap flags on an unverified header version", failures, output);
    }

    private static void Check(bool passed, string description, List<string> failures, TextWriter output)
    {
        output.WriteLine((passed ? "PASS" : "FAIL") + "  " + description);
        if (!passed) failures.Add(description);
    }
}
