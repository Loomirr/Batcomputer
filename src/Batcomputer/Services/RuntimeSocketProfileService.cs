using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Reads socket transforms captured from CharacterMesh0 at runtime.</summary>
public static class RuntimeSocketProfileService
{
    public sealed record SocketTransform(
        Vector3 TranslationUe,
        Quaternion RotationUe,
        Vector3 ScaleUe,
        string BoneName,
        string ProfileName);

    public sealed class ProfileSet
    {
        private readonly Dictionary<string, Profile> _byBodyPath;

        internal ProfileSet(Dictionary<string, Profile> byBodyPath) => _byBodyPath = byBodyPath;

        public int Count => _byBodyPath.Count;

        public bool TryGet(string? bodyMeshPath, string? socketName, out SocketTransform transform)
        {
            transform = null!;
            if (string.IsNullOrWhiteSpace(bodyMeshPath) || string.IsNullOrWhiteSpace(socketName))
            {
                return false;
            }

            if (!_byBodyPath.TryGetValue(NormalizeAssetPath(bodyMeshPath), out var profile))
            {
                return false;
            }

            if (!profile.Sockets.TryGetValue(socketName.Trim(), out var socket))
            {
                return false;
            }

            transform = socket with { ProfileName = profile.Name };
            return true;
        }
    }

    internal sealed record Profile(string Name, Dictionary<string, SocketTransform> Sockets);

    private sealed class CaptureProfile
    {
        [JsonPropertyName("rig_hash")]
        public string RigHash { get; set; } = "";

        [JsonPropertyName("body")]
        public CaptureBody? Body { get; set; }

        [JsonPropertyName("sockets")]
        public List<CaptureSocket>? Sockets { get; set; }
    }

    private sealed class CaptureBody
    {
        [JsonPropertyName("skeletal_mesh")]
        public string SkeletalMesh { get; set; } = "";
    }

    private sealed class CaptureSocket
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("exists")]
        public bool Exists { get; set; }

        [JsonPropertyName("bone_name")]
        public string BoneName { get; set; } = "";

        [JsonPropertyName("authored_socket_local_transform_status")]
        public string AuthoredTransformStatus { get; set; } = "";

        [JsonPropertyName("authored_socket_local_transform_ue")]
        public string AuthoredTransform { get; set; } = "";
    }

    private const string Number = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?";
    private static readonly Regex TransformPattern = new(
        @"Rotation=\(X=(?<rx>" + Number + @"),Y=(?<ry>" + Number + @"),Z=(?<rz>" + Number + @"),W=(?<rw>" + Number + @")\)," +
        @"Translation=\(X=(?<tx>" + Number + @"),Y=(?<ty>" + Number + @"),Z=(?<tz>" + Number + @")\)," +
        @"Scale3D=\(X=(?<sx>" + Number + @"),Y=(?<sy>" + Number + @"),Z=(?<sz>" + Number + @")\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProfileSet Load()
    {
        var profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "socket-profile_*.json", SearchOption.TopDirectoryOnly))
            {
                TryLoad(file, profiles);
            }
        }
        return new ProfileSet(profiles);
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "gamedata", "RuntimeSocketProfiles");
    }

    private static void TryLoad(string file, IDictionary<string, Profile> profiles)
    {
        try
        {
            var capture = JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(file));
            var bodyPath = NormalizeAssetPath(capture?.Body?.SkeletalMesh);
            if (string.IsNullOrWhiteSpace(bodyPath) || capture?.Sockets is null)
            {
                return;
            }

            var sockets = new Dictionary<string, SocketTransform>(StringComparer.OrdinalIgnoreCase);
            foreach (var socket in capture.Sockets)
            {
                if (!socket.Exists || !socket.AuthoredTransformStatus.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(socket.Name) || string.IsNullOrWhiteSpace(socket.BoneName) ||
                    !TryParse(socket.AuthoredTransform, socket.BoneName, out var transform))
                {
                    continue;
                }
                sockets[socket.Name] = transform;
            }

            if (sockets.Count == 0)
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(capture.RigHash) ? Path.GetFileNameWithoutExtension(file) : capture.RigHash;
            profiles[bodyPath] = new Profile(name, sockets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Console.WriteLine($"  runtime socket profile skipped '{Path.GetFileName(file)}': {ex.Message.Split('\n')[0]}");
        }
    }

    private static bool TryParse(string value, string boneName, out SocketTransform transform)
    {
        transform = null!;
        var match = TransformPattern.Match(value ?? "");
        if (!match.Success)
        {
            return false;
        }

        static float NumberValue(Group group) => float.Parse(group.Value, CultureInfo.InvariantCulture);
        var rotation = new Quaternion(
            NumberValue(match.Groups["rx"]), NumberValue(match.Groups["ry"]),
            NumberValue(match.Groups["rz"]), NumberValue(match.Groups["rw"]));
        if (rotation.LengthSquared() < 0.000001f)
        {
            return false;
        }

        transform = new SocketTransform(
            new Vector3(NumberValue(match.Groups["tx"]), NumberValue(match.Groups["ty"]), NumberValue(match.Groups["tz"])),
            Quaternion.Normalize(rotation),
            new Vector3(NumberValue(match.Groups["sx"]), NumberValue(match.Groups["sy"]), NumberValue(match.Groups["sz"])),
            boneName,
            "");
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
