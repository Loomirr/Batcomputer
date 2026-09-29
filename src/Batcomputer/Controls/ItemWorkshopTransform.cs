using System.Text.Json;

namespace Batcomputer;

/// <summary>Bounded, preview-only edits. The owning editor still validates and accepts the recipe.</summary>
internal sealed record ItemWorkshopTransform(int Revision, int Index, bool Effect, float Scale, float[] Offset, float[] Rotation)
{
    internal static object FromRecipe(WeaponModelRecipe r) => new { scale = r.Scale, offset = new[] { r.X, r.Y, r.Z }, rotation = new[] { r.Pitch, r.Yaw, r.Roll } };

    internal static bool TryParse(string json, out ItemWorkshopTransform change)
    {
        change = null!;
        if (json.Length > 4096) return false;
        try
        {
            using var d = JsonDocument.Parse(json); var root = d.RootElement;
            if (root.GetProperty("type").GetString() != "item-workshop-transform") return false;
            var target = root.GetProperty("target").GetString(); if (target is not ("custom" or "effect")) return false;
            var effect = target == "effect"; var revision = root.GetProperty("revision").GetInt32();
            var index = root.GetProperty("index").GetInt32(); var t = root.GetProperty("transform");
            var scale = t.GetProperty("scale").GetSingle();
            var offset = t.GetProperty("offset").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var rotation = t.GetProperty("rotation").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (revision < 0 || (effect ? index is < 0 or > 2 : index != -1) || !float.IsFinite(scale) ||
                scale < (effect ? .01f : .001f) || scale > (effect ? 10 : 1000) || offset.Length != 3 || rotation.Length != 3 ||
                offset.Any(v => !float.IsFinite(v) || Math.Abs(v) > (effect ? 1000 : 10000)) || rotation.Any(v => !float.IsFinite(v) || Math.Abs(v) > 360)) return false;
            change = new(revision, index, effect, scale, offset, rotation); return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { return false; }
    }

    internal static void Bind(IReadOnlyList<NumericUpDown> numbers, IEnumerable<float> values)
    {
        var i = 0;
        foreach (var v in values)
        {
            var n = numbers[i++]; n.Value = Math.Clamp(Math.Round((decimal)v, n.DecimalPlaces), n.Minimum, n.Maximum);
        }
    }
}
