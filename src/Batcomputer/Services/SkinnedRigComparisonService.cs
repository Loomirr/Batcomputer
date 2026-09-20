using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Batcomputer;

internal static class SkinnedRigComparisonService
{
    internal sealed record BoneResult(string Name, string ExpectedParent, string ActualParent,
        double TranslationCm, double RotationDegrees, double RotationMetric, double ScaleDifference,
        float[] ExpectedPose, float[] ActualPose, bool Passed);
    internal sealed record Report(int ExpectedBoneCount, int ActualBoneCount, string[] Errors, BoneResult[] Bones)
    {
        public bool Passed => Errors.Length == 0;
    }

    internal static Report Compare(IReadOnlyList<SkinnedGlbExportService.Bone> expected, IReadOnlyList<SkinnedGlbExportService.Bone> actual)
    {
        var errors = new List<string>(); var rows = new List<BoneResult>();
        if (actual.Count != expected.Count) errors.Add($"Rig has {actual.Count} bones; donor requires {expected.Count}. Remove exporter helper/leaf bones; keep the complete native rig.");
        if (expected.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != expected.Count ||
            actual.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            errors.Add("The rig contains duplicate bone names.");
        var byName = expected.GroupBy(b => b.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        static string Parent(IReadOnlyList<SkinnedGlbExportService.Bone> rig, int i) => i == -1 ? "" : i < -1 || i >= rig.Count ? "<invalid>" : rig[i].Name;
        static float[] Values(SkinnedGlbExportService.Bone b) => [b.Translation.X, b.Translation.Y, b.Translation.Z,
            b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W, b.Scale.X, b.Scale.Y, b.Scale.Z];
        foreach (var a in actual)
        {
            if (!byName.TryGetValue(a.Name, out var b)) { errors.Add("Unexpected bone: " + a.Name); continue; }
            var parentA = Parent(actual, a.Parent); var parentB = Parent(expected, b.Parent);
            double translation = Vector3.Distance(a.Translation, b.Translation);
            double dot = (double)a.Rotation.X * b.Rotation.X + (double)a.Rotation.Y * b.Rotation.Y +
                         (double)a.Rotation.Z * b.Rotation.Z + (double)a.Rotation.W * b.Rotation.W;
            double rotation = Math.Max(0, 1 - Math.Abs(dot));
            double length = Math.Sqrt((double)a.Rotation.LengthSquared() * b.Rotation.LengthSquared());
            double degrees = length > 0 ? 2 * Math.Acos(Math.Clamp(Math.Abs(dot) / length, 0, 1)) * 180 / Math.PI : double.NaN;
            double scale = new[] { Math.Abs(a.Scale.X - b.Scale.X), Math.Abs(a.Scale.Y - b.Scale.Y), Math.Abs(a.Scale.Z - b.Scale.Z) }.Max();
            bool hierarchy = parentA != "<invalid>" && parentA == parentB;
            bool pose = double.IsFinite(translation + rotation + scale + degrees) && translation <= .001 && rotation <= .00001 && scale <= .00001;
            rows.Add(new(a.Name, parentB, parentA, translation, degrees, rotation, scale, Values(b), Values(a), hierarchy && pose));
            if (!hierarchy) errors.Add($"Bone hierarchy differs at '{a.Name}': expected parent '{parentB}', found '{parentA}'.");
            if (!pose) errors.Add($"Rest pose/scale mismatch at '{a.Name}': translation {translation:G5} cm, rotation {degrees:G5} degrees, scale difference {scale:G5}. Preserve the native rest pose; automatic retargeting is not supported.");
        }
        foreach (var missing in expected.Where(e => !actual.Any(a => a.Name == e.Name))) errors.Add("Missing native bone: " + missing.Name);
        return new(expected.Count, actual.Count, errors.ToArray(), rows.ToArray());
    }

    internal static void Write(string path, Report report) => AtomicFileUtil.WriteAllText(path,
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
}
