using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Conservative, aggregate-stage-only sharing of unchanged character-family assets.
/// Authoring recipes and their independent editable sources are never modified.</summary>
internal static class CharacterAssetReuseService
{
    internal sealed record Result(int Packages, long Bytes, IReadOnlyDictionary<string, string> Aliases);
    private static readonly string[] Extensions = [".uasset", ".uexp", ".ubulk", ".uptnl"];
    private static readonly HashSet<string> Shareable = new(StringComparer.Ordinal)
    { "StaticMesh", "SkeletalMesh", "Texture2D", "MaterialInstanceConstant", "AnimSequence", "AnimMontage", "Skeleton", "PhysicsAsset" };
    private sealed record Candidate(string Package, string File, string Class);

    internal static Result Apply(string content, IList<NativeSuitProject> projects, Usmap mappings, string work, string workspace, Action<string> log)
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
        foreach (var family in projects.Where(p => p.CustomCharacter is not null).GroupBy(p => p.CustomCharacter!.DefinitionSlotId, StringComparer.Ordinal))
        {
            var members = family.OrderByDescending(p => p.CustomCharacter!.IsDefinition).ThenBy(p => p.SlotId, StringComparer.Ordinal).ToArray();
            if (members.Length < 2 || !members.Any(p => p.CustomCharacter!.IsDefinition)) continue;
            var primary = new Dictionary<(string Class, string Leaf), Candidate>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                // Mesh tooling and immutable libraries can own a different mod root
                // from the receiving character Blueprint. Include declared roots only.
                foreach (var root in DeclaredRoots(member))
                {
                var folder = Path.Combine(content, root[6..]);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.uasset", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    if (!visited.Add(file)) continue;
                    // No Blueprint, ability set, input/tag state or primary-asset identity is eligible.
                    var asset = Read(file);
                    var classes = asset.Exports.Select(e => e.GetExportClassType()?.ToString()).Where(c => c is not null && Shareable.Contains(c)).Distinct().ToArray();
                    if (classes.Length != 1) continue;
                    var package = "/Game/" + Path.GetRelativePath(content, file)[..^7].Replace('\\', '/');
                    if (immutable.Contains(package)) continue;
                    var candidate = new Candidate(package, file, classes[0]!);
                    candidates[package] = candidate;
                    var key = (candidate.Class, Path.GetFileNameWithoutExtension(file));
                    if (primary.TryGetValue(key, out var parent)) aliases[package] = parent.Package;
                    else primary.Add(key, candidate);
                }
                }
            }
        }
        foreach (var package in immutable)
            foreach (var extension in Extensions.Take(2))
            {
                var file = Path.ChangeExtension(FileFor(package), extension);
                if (!File.Exists(file)) continue;
                var data = File.ReadAllBytes(file);
                foreach (var old in aliases.Keys.Where(old => ContainsPath(data, old)).ToArray()) aliases.Remove(old);
            }
        // A material is shareable only if its texture bindings also remain equivalent.
        // Start with all proposed aliases, then reject differences to a fixed point.
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in aliases.ToArray())
            {
                var left = Read(candidates[pair.Key].File);
                var right = Read(candidates[pair.Value].File);
                Rewrite(left, aliases); Rewrite(right, aliases);
                left.FolderName = new FString(pair.Value); right.FolderName = new FString(pair.Value);
                var leftFile = Path.Combine(work, "Compare", "Child.uasset");
                var rightFile = Path.Combine(work, "Compare", "Parent.uasset");
                Directory.CreateDirectory(Path.GetDirectoryName(leftFile)!);
                left.Write(leftFile); right.Write(rightFile);
                if (!EquivalentFamily(candidates[pair.Key].File, candidates[pair.Value].File, leftFile, rightFile))
                { aliases.Remove(pair.Key); changed = true; }
            }
        } while (changed);
        if (aliases.Count == 0) return new(0, 0, aliases);

        // Build all replacements before changing any staged package. The surrounding
        // release transaction discards this disposable stage if a write/validation fails.
        var replacements = new List<(string Source, string Staged)>();
        foreach (var file in Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories))
        {
            var package = "/Game/" + Path.GetRelativePath(content, file)[..^7].Replace('\\', '/');
            if (aliases.ContainsKey(package)) continue;
            if (!aliases.Keys.Any(old => ContainsPath(File.ReadAllBytes(file), old))) continue;
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
        File.WriteAllText(Path.Combine(work, "reuse-report.json"), JsonSerializer.Serialize(new { Packages = aliases.Count, Bytes = bytes, Aliases = aliases }, new JsonSerializerOptions { WriteIndented = true }));
        log($"Shared character assets: reused {aliases.Count} unchanged packages ({bytes:N0} staged bytes). Suit-specific edits and runtime owners remain separate.");
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
    private static bool Rewrite(UAsset asset, IReadOnlyDictionary<string, string> aliases)
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
