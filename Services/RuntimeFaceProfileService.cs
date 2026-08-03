using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Reads neutral face material values captured from the running game.</summary>
public static class RuntimeFaceProfileService
{
    public sealed record FaceProfile(
        string RigHash,
        string FaceMeshPath,
        string MaterialPath,
        IReadOnlyDictionary<string, float> Scalars,
        IReadOnlyDictionary<string, Vector3> Vectors)
    {
        public bool TryGetScalar(string name, out float value) => Scalars.TryGetValue(name, out value);
    }

    public sealed class ProfileSet
    {
        private readonly Dictionary<string, FaceProfile> _byMaterialPath;

        internal ProfileSet(Dictionary<string, FaceProfile> byMaterialPath) => _byMaterialPath = byMaterialPath;

        public int Count => _byMaterialPath.Count;

        public bool TryGet(string? materialPath, out FaceProfile profile) =>
            _byMaterialPath.TryGetValue(NormalizeAssetPath(materialPath), out profile!);
    }

    private sealed class CaptureProfile
    {
        [JsonPropertyName("rig_hash")]
        public string RigHash { get; set; } = "";

        [JsonPropertyName("components")]
        public List<CaptureComponent>? Components { get; set; }
    }

    private sealed class CaptureComponent
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("skeletal_mesh")]
        public string SkeletalMesh { get; set; } = "";

        [JsonPropertyName("materials")]
        public List<CaptureMaterialSlot>? Materials { get; set; }
    }

    private sealed class CaptureMaterialSlot
    {
        [JsonPropertyName("material")]
        public CaptureMaterial? Material { get; set; }
    }

    private sealed class CaptureMaterial
    {
        [JsonPropertyName("parent_chain")]
        public List<string>? ParentChain { get; set; }

        [JsonPropertyName("parameter_arrays")]
        public List<CaptureParameterArray>? ParameterArrays { get; set; }
    }

    private sealed class CaptureParameterArray
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("values")]
        public List<string>? Values { get; set; }
    }

    private static readonly Regex ScalarPattern = new(
        "Name=\\\"(?<name>[^\\\"]+)\\\".*?ParameterValue=(?<value>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[Ee][+-]?\\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VectorPattern = new(
        "Name=\\\"(?<name>[^\\\"]+)\\\".*?ParameterValue=\\(R=(?<x>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)),G=(?<y>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)),B=(?<z>[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProfileSet Load()
    {
        var profiles = new Dictionary<string, FaceProfile>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(AppContext.BaseDirectory, "gamedata", "RuntimeFaceProfiles");
        if (!Directory.Exists(directory))
        {
            return new ProfileSet(profiles);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "face-baseline_*.json", SearchOption.TopDirectoryOnly))
        {
            TryLoad(file, profiles);
        }
        return new ProfileSet(profiles);
    }

    private static void TryLoad(string file, IDictionary<string, FaceProfile> profiles)
    {
        try
        {
            var capture = JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(file));
            var face = capture?.Components?.FirstOrDefault(component =>
                component.Name.Equals("Face", StringComparison.OrdinalIgnoreCase));
            var material = face?.Materials?.FirstOrDefault()?.Material;
            var materialPath = material?.ParentChain?
                .Select(NormalizeAssetPath)
                .FirstOrDefault(path => path.Contains("/mi_face_", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(materialPath))
            {
                return;
            }

            var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameterArray in material?.ParameterArrays ?? Enumerable.Empty<CaptureParameterArray>())
            {
                foreach (var value in parameterArray.Values ?? Enumerable.Empty<string>())
                {
                    if (parameterArray.Name.Equals("ScalarParameterValues", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = ScalarPattern.Match(value);
                        if (match.Success && float.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var scalar))
                        {
                            scalars[match.Groups["name"].Value] = scalar;
                        }
                    }
                    else if (parameterArray.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = VectorPattern.Match(value);
                        if (match.Success && TryReadVector(match, out var vector))
                        {
                            vectors[match.Groups["name"].Value] = vector;
                        }
                    }
                }
            }

            if (scalars.Count == 0)
            {
                return;
            }

            profiles[materialPath] = new FaceProfile(
                capture?.RigHash ?? Path.GetFileNameWithoutExtension(file),
                NormalizeAssetPath(face?.SkeletalMesh),
                materialPath,
                scalars,
                vectors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"  runtime face profile skipped '{Path.GetFileName(file)}': {ex.Message.Split('\n')[0]}");
        }
    }

    private static bool TryReadVector(Match match, out Vector3 vector)
    {
        vector = default;
        if (!float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(match.Groups["z"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }
        vector = new Vector3(x, y, z);
        return true;
    }

    private static string NormalizeAssetPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var path = value.Replace('\\', '/');
        var gameIndex = path.IndexOf("/Game/", StringComparison.OrdinalIgnoreCase);
        if (gameIndex >= 0)
        {
            path = path[gameIndex..];
        }
        var separator = path.IndexOfAny(['|', '\'', ' ']);
        if (separator >= 0)
        {
            path = path[..separator];
        }
        var lastSlash = path.LastIndexOf('/');
        var objectSeparator = path.IndexOf('.', lastSlash + 1);
        if (objectSeparator >= 0)
        {
            path = path[..objectSeparator];
        }
        return path.Trim().ToLowerInvariant();
    }
}
