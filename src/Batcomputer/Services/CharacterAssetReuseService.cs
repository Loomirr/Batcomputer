using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Conservative, aggregate-stage-only sharing of unchanged character-family and vehicle assets.
/// Authoring recipes and their independent editable sources are never modified.</summary>
internal static class CharacterAssetReuseService
{
    internal sealed record Result(int Packages, long Bytes, IReadOnlyDictionary<string, string> Aliases);
    private static readonly string[] Extensions = [".uasset", ".uexp", ".ubulk", ".uptnl"];
    private static readonly HashSet<string> Shareable = new(StringComparer.Ordinal)
    { "StaticMesh", "SkeletalMesh", "Texture2D", "MaterialInstanceConstant", "AnimSequence", "AnimMontage", "Skeleton", "PhysicsAsset",
      "BlendSpace", "BlendSpace1D", "TTAnimSet", "TTLayerSet" };
    private sealed record Candidate(string Package, string File, string Class);

    internal static bool SupportsSharedPayload(string type) => Shareable.Contains(type);
    internal static string Policy(string type) => Shareable.Contains(type)
        ? "Share only byte-equivalent cooked payloads with equivalent dependencies; unique edits stay separate"
        : type.StartsWith("Wub", StringComparison.Ordinal)
            ? "Profile-owned routing; inherited profiles already merge at the same path; encoded media reused separately"
            : "Reuse immutable native inputs only; keep runtime/package identity separate";

    internal static object[] Inventory(string content, Usmap mappings) => Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories)
        .Select(file =>
        {
            var asset = new UAsset(file, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
            var leaf = Path.GetFileNameWithoutExtension(file);
            var type = asset.Exports.FirstOrDefault(e => e.ObjectName.ToString() == leaf)?.GetExportClassType()?.ToString()
                ?? (asset.Exports.Any(e => e.GetExportClassType()?.ToString() == "BlueprintGeneratedClass") ? "BlueprintGeneratedClass" : "Other");
            return (Type: type, Bytes: Extensions.Sum(ext => File.Exists(Path.ChangeExtension(file, ext)) ? new FileInfo(Path.ChangeExtension(file, ext)).Length : 0L));
        }).GroupBy(p => p.Type).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (object)new { Class = g.Key, Packages = g.Count(), Bytes = g.Sum(p => p.Bytes),
            Policy = Policy(g.Key) }).ToArray();

    // Refine equivalence groups rather than comparing everything only with the definition.
    // A split dependency can split its consumers on the next round; groups never merge back.
    internal static Dictionary<string, string> Refine(IEnumerable<string[]> initial,
        Func<string, string, IReadOnlyDictionary<string, string>, string> fingerprint)
    {
        var groups = initial.Where(g => g.Length > 1).ToArray();
        while (true)
        {
            var aliases = groups.SelectMany(g => g.Skip(1).Select(p => (p, g[0])))
                .ToDictionary(p => p.p, p => p.Item2, StringComparer.Ordinal);
            var next = groups.SelectMany(g => g.GroupBy(p => fingerprint(p, g[0], aliases), StringComparer.Ordinal)
                .Select(part => part.ToArray())).Where(g => g.Length > 1).ToArray();
            if (next.Sum(g => g.Length - 1) == aliases.Count) return aliases;
            groups = next;
        }
    }

    private static string Fingerprint(string source, string normalized)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var extension in Extensions)
        {
            var file = Path.ChangeExtension(extension is ".uasset" or ".uexp" ? normalized : source, extension);
            hash.AppendData(Encoding.UTF8.GetBytes(extension));
            hash.AppendData([File.Exists(file) ? (byte)1 : (byte)0]);
            if (!File.Exists(file)) continue;
            using var stream = File.OpenRead(file);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            var buffer = new byte[65536];
            int count;
            while ((count = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static Result Apply(string content, IList<NativeSuitProject> projects, Usmap mappings, string work, string workspace, Action<string> log,
        IReadOnlyList<VehicleProject>? vehicles = null)
    {
        content = Path.GetFullPath(content);
        work = Path.GetFullPath(work);
        if (FileSystemPathUtil.IsWithinDirectory(work, content)) throw new InvalidDataException("Asset-reuse scratch must be outside Content.");
        Directory.CreateDirectory(work);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        UAsset Read(string file) => new(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        string FileFor(string package) => Path.Combine(content, package[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var immutable = projects.SelectMany(p => p.GameplayAnimationGraphs).SelectMany(g => GameplayAnimationGraphService.Load(workspace, g).Packages)
            .Select(p => p.Package).ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var groups = new List<string[]>();
        var scopes = projects.Where(p => p.CustomCharacter is not null).GroupBy(p => p.CustomCharacter!.DefinitionSlotId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1 && g.Any(p => p.CustomCharacter!.IsDefinition))
            .Select(g => g.OrderByDescending(p => p.CustomCharacter!.IsDefinition).ThenBy(p => p.SlotId, StringComparer.Ordinal)
                .SelectMany(DeclaredRoots).Distinct(StringComparer.Ordinal).ToArray()).ToList();
        // Only enabled vehicles supplied by this aggregate build. No scanning unrelated projects,
        // no character-definition requirement, and no merging of primary/runtime owner assets.
        var vehicleRoots = (vehicles ?? []).OrderBy(v => v.Id, StringComparer.Ordinal)
            .Select(VehicleProjectService.ContentRoot).Distinct(StringComparer.Ordinal).ToArray();
        if (vehicleRoots.Length > 0) scopes.Add(vehicleRoots);
        foreach (var roots in scopes)
        {
            var primary = new Dictionary<(string Class, string Leaf), List<string>>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            // Mesh tooling and immutable libraries can own a different mod root
            // from the receiving Blueprint. Include declared roots only.
            foreach (var root in roots)
            {
                var folder = Path.Combine(content, root[6..]);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.uasset", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    if (!visited.Add(file)) continue;
                    // No Blueprint, ability set, input/tag state or primary-asset identity is eligible.
                    var asset = Read(file);
                    var leaf = Path.GetFileNameWithoutExtension(file);
                    var main = asset.Exports.Where(e => e.ObjectName.ToString() == leaf).ToArray();
                    var assetClass = main.Length == 1 ? main[0].GetExportClassType()?.ToString() : null;
                    if (assetClass is null || !Shareable.Contains(assetClass) ||
                        asset.Exports.Any(e => e.GetExportClassType()?.ToString() is "BlueprintGeneratedClass" or "AnimBlueprintGeneratedClass")) continue;
                    var package = "/Game/" + Path.GetRelativePath(content, file)[..^7].Replace('\\', '/');
                    if (immutable.Contains(package) || candidates.ContainsKey(package)) continue;
                    var candidate = new Candidate(package, file, assetClass);
                    candidates[package] = candidate;
                    var key = (candidate.Class, Path.GetFileNameWithoutExtension(file));
                    if (!primary.TryGetValue(key, out var peers)) primary.Add(key, peers = []);
                    peers.Add(package);
                }
            }
            groups.AddRange(primary.Values.Where(g => g.Count > 1).Select(g => g.ToArray()));
        }
        var protectedPackages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in immutable)
            foreach (var extension in Extensions.Take(2))
            {
                var file = Path.ChangeExtension(FileFor(package), extension);
                if (!File.Exists(file)) continue;
                var data = File.ReadAllBytes(file);
                foreach (var old in candidates.Keys.Where(old => ContainsPath(data, old))) protectedPackages.Add(old);
            }
        // A material is shareable only if its texture bindings also remain equivalent.
        // Start with all proposed aliases, then reject differences to a fixed point.
        var compare = Path.Combine(work, "Compare", "Normalized.uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
        aliases = Refine(groups.Select(g => g.Where(p => !protectedPackages.Contains(p)).ToArray()), (package, canonical, proposed) =>
        {
            var source = candidates[package].File;
            var asset = Read(source);
            Rewrite(asset, proposed);
            asset.FolderName = new FString(canonical);
            foreach (var ext in Extensions) File.Delete(Path.ChangeExtension(compare, ext));
            asset.Write(compare);
            return Fingerprint(source, compare);
        });
        if (aliases.Count == 0)
        {
            File.WriteAllText(Path.Combine(work, "reuse-report.json"), JsonSerializer.Serialize(new { Packages = 0, VehiclePackages = 0, Bytes = 0, Aliases = aliases,
                AssetTypes = Inventory(content, mappings) }, new JsonSerializerOptions { WriteIndented = true }));
            return new(0, 0, aliases);
        }

        // Build all replacements before changing any staged package. The surrounding
        // release transaction discards this disposable stage if a write/validation fails.
        var replacements = new List<(string Source, string Staged)>();
        foreach (var file in Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories))
        {
            var package = "/Game/" + Path.GetRelativePath(content, file)[..^7].Replace('\\', '/');
            if (aliases.ContainsKey(package)) continue;
            var header = File.ReadAllBytes(file);
            if (!aliases.Keys.Any(old => ContainsPath(header, old))) continue;
            // Object imports use indexed FNames. Keep opaque Blueprint exports and
            // bytecode byte-for-byte rather than requiring their reflected schemas.
            var asset = new UAsset(file, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
            if (!Rewrite(asset, aliases)) continue;
            var staged = Path.Combine(work, "Rewritten", Path.GetRelativePath(content, file));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!); asset.Write(staged);
            _ = new UAsset(staged, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
            replacements.Add((file, staged));
        }
        foreach (var (source, staged) in replacements)
            foreach (var extension in Extensions.Take(2))
            {
                var output = Path.ChangeExtension(staged, extension);
                if (File.Exists(output)) File.Copy(output, Path.ChangeExtension(source, extension), overwrite: true);
            }
        foreach (var file in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories).Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            var package = "/Game/" + Path.GetRelativePath(content, file)[..^Path.GetExtension(file).Length].Replace('\\', '/');
            if (aliases.ContainsKey(package)) continue;
            var data = File.ReadAllBytes(file);
            foreach (var old in aliases.Keys)
                if (ContainsPath(data, old)) throw new InvalidDataException("Unrewritten shared-asset reference in " + file + ": " + old);
        }
        long bytes = 0;
        foreach (var old in aliases.Keys)
            foreach (var extension in Extensions)
            {
                var file = Path.ChangeExtension(FileFor(old), extension);
                if (!FileSystemPathUtil.IsWithinDirectory(file, content)) throw new InvalidDataException("Shared asset escaped disposable Content.");
                if (File.Exists(file)) { bytes += new FileInfo(file).Length; File.Delete(file); }
            }
        for (int i = 0; i < projects.Count; i++)
        {
            var node = JsonSerializer.SerializeToNode(projects[i])!;
            RewriteRecipe(node, aliases);
            projects[i] = node.Deserialize<NativeSuitProject>()!;
            projects[i].ReleaseAssetAliases = aliases;
        }
        var vehiclePackages = aliases.Keys.Count(package => vehicleRoots.Any(root => package.StartsWith(root + "/", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(work, "reuse-report.json"), JsonSerializer.Serialize(new { Packages = aliases.Count, VehiclePackages = vehiclePackages, Bytes = bytes, Aliases = aliases,
            SharedTypes = aliases.Keys.GroupBy(p => candidates[p].Class).Select(g => new { Class = g.Key, Packages = g.Count() }),
            AssetTypes = Inventory(content, mappings) }, new JsonSerializerOptions { WriteIndented = true }));
        log($"Shared character/vehicle assets: reused {aliases.Count} unchanged packages ({bytes:N0} staged bytes). Unique edits and runtime owners remain separate.");
        log($"Vehicle asset sharing: {vehicleRoots.Length} enabled vehicle root(s), {vehiclePackages} duplicate payload package(s) removed; vehicle identities and settings remain independent.");
        return new(aliases.Count, bytes, aliases);
    }

    internal static string Redirect(string text, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.TryGetValue(text, out var exact)) return exact;
        foreach (var pair in aliases)
        {
            if (text.StartsWith(pair.Key + ".", StringComparison.Ordinal)) return pair.Value + text[pair.Key.Length..];
            var quoted = "'" + pair.Key;
            var index = text.IndexOf(quoted, StringComparison.Ordinal);
            if (index >= 0 && text.Length > index + quoted.Length && text[index + quoted.Length] is '.' or '\'')
                return text[..(index + 1)] + pair.Value + text[(index + quoted.Length)..];
        }
        return text;
    }
    internal static string Resolve(string package, IReadOnlyDictionary<string, string>? aliases)
    {
        if (aliases is null) return package;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(package, out var next))
        {
            if (!seen.Add(package)) throw new InvalidDataException("Cyclic shared asset aliases.");
            package = next;
        }
        return package;
    }
    internal static bool Rewrite(UAsset asset, IReadOnlyDictionary<string, string> aliases)
    {
        bool changed = false;
        for (int i = 0; i < asset.GetNameMapIndexList().Count; i++)
        {
            var text = asset.GetNameReference(i).ToString(); var next = Redirect(text, aliases);
            if (text != next) { asset.SetNameReference(i, new FString(next)); changed = true; }
        }
        void Properties(IEnumerable<PropertyData> rows)
        {
            foreach (var row in rows)
            {
                if (row is StrPropertyData str && str.Value is { } value)
                {
                    var text = value.ToString(); var next = Redirect(text, aliases);
                    if (text != next) { str.Value = new FString(next); changed = true; }
                }
                if (row is StructPropertyData structure) Properties(structure.Value);
                if (row is ArrayPropertyData array) Properties(array.Value);
            }
        }
        foreach (var export in asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>()) Properties(export.Data);
        return changed;
    }
    internal static bool EquivalentFamily(string source, string target, string normalizedSource, string normalizedTarget)
    {
        foreach (var extension in Extensions)
        {
            var left = Path.ChangeExtension(extension is ".uasset" or ".uexp" ? normalizedSource : source, extension);
            var right = Path.ChangeExtension(extension is ".uasset" or ".uexp" ? normalizedTarget : target, extension);
            if (File.Exists(left) != File.Exists(right)) return false;
            if (File.Exists(left) && !File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right))) return false;
        }
        return true;
    }
    private static bool ContainsPath(byte[] data, string package) => new[] { ".", "\0", "'" }.Any(suffix =>
        data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(package + suffix)) >= 0 || data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(package + suffix)) >= 0);
    internal static string[] DeclaredRoots(NativeSuitProject project)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, "^/Game/Mods/[A-Za-z0-9_]+(?=/|$)");
                if (match.Success) roots.Add(match.Value);
            }
            else if (node is JsonObject obj) foreach (var pair in obj) { if (pair.Value is {} child) Visit(child); }
            else if (node is JsonArray array) foreach (var child in array) { if (child is not null) Visit(child); }
        }
        Visit(JsonSerializer.SerializeToNode(project)!);
        return roots.Order(StringComparer.Ordinal).ToArray();
    }
    internal static T RedirectRecipe<T>(T project, IReadOnlyDictionary<string, string> aliases)
    {
        var node = JsonSerializer.SerializeToNode(project)!;
        RewriteRecipe(node, aliases);
        return node.Deserialize<T>()!;
    }
    private static void RewriteRecipe(JsonNode node, IReadOnlyDictionary<string, string> aliases)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) obj[pair.Key] = Redirect(text, aliases);
                else if (pair.Value is not null) RewriteRecipe(pair.Value, aliases);
        if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Redirect(text, aliases);
                else if (array[i] is {} child) RewriteRecipe(child, aliases);
    }
}
