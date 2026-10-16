using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Experimental exact-character appearance replacer. Never installs or edits source assets.</summary>
internal static class NpcAppearanceTestService
{
    internal sealed record Change(string Original, string Replacement);
    internal sealed record Recipe(string Target, string Material = "", string Replacement = "", Change[]? Changes = null, string[]? VisualContentRoots = null);

    internal static Change[] ChangesFor(Recipe recipe) => recipe.Changes ??
        (string.IsNullOrWhiteSpace(recipe.Material) && string.IsNullOrWhiteSpace(recipe.Replacement)
            ? [] : [new(recipe.Material, recipe.Replacement)]);

    internal static void Validate(Recipe recipe)
    {
        var changes = ChangesFor(recipe);
        foreach (var path in changes.SelectMany(c => new[] { c.Original, c.Replacement }).Prepend(recipe.Target))
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/Game/", StringComparison.Ordinal) || path.Contains("..") ||
                path.Any(c => !(char.IsLetterOrDigit(c) || c is '/' or '_')))
                throw new InvalidDataException("Use exact base-game package paths without extensions or object suffixes.");
        if (!BaseEligibilityService.IsVisualCharacterPackage(recipe.Target))
            throw new InvalidDataException("Choose a concrete character Blueprint, not a master, prop or gameplay asset.");
        if (changes.Select(c => c.Original).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new InvalidDataException("Each original asset can only be replaced once.");
        foreach (var change in changes)
        {
            if (change.Original.Equals(change.Replacement, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a different replacement asset.");
            if (change.Replacement.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) && recipe.VisualContentRoots is not { Length: > 0 })
                throw new InvalidDataException("Select the cooked Content folder containing the custom visual asset and its dependencies.");
        }
    }

    internal static string Build(string recipeFile, string content, string mappings, string unrealPak, string output, bool stageOnly = false)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(recipeFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing recipe.");
        Validate(recipe);
        var changes = ChangesFor(recipe);
        content = Path.GetFullPath(content); output = Path.GetFullPath(output);
        if (changes.Length == 0 && !stageOnly) throw new InvalidDataException("No visual changes to build. Choose a material or mesh replacement first.");
        if (FileSystemPathUtil.IsWithinDirectory(output, content) || FileSystemPathUtil.IsWithinDirectory(content, output))
            throw new InvalidDataException("Output must be separate from extracted source assets.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new output directory; existing tests are never overwritten.");
        if (!stageOnly && !File.Exists(unrealPak)) throw new FileNotFoundException("UnrealPak is required.", unrealPak);
        var visualRoots = recipe.VisualContentRoots ?? [];
        foreach (var root in visualRoots)
            if (FileSystemPathUtil.IsWithinDirectory(output, root) || FileSystemPathUtil.IsWithinDirectory(root, output))
                throw new InvalidDataException("Output must be separate from custom visual source folders.");
        string Source(string package) => CharacterReplacementService.ResolveAsset(package, content, visualRoots);
        foreach (var path in changes.SelectMany(c => new[] { c.Original, c.Replacement }).Prepend(recipe.Target))
            if (!File.Exists(Source(path))) throw new FileNotFoundException("Extract this package first.", path);
        var maps = new Usmap(mappings);
        const CustomSerializationFlags flags = CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading;
        UAsset Read(string path) => new(path, EngineVersion.VER_UE5_6, maps, flags);
        var original = Read(Source(recipe.Target));
        if (!original.VerifyBinaryEquality()) throw new InvalidDataException("The source does not round-trip exactly with these mappings.");
        var names = original.GetNameMapIndexList().Select(n => n.ToString()).ToArray();
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            CharacterReplacementService.ValidateAssets(original, change, content, maps, visualRoots);
            foreach (var pair in new[] { (change.Original, change.Replacement), (Path.GetFileName(change.Original), Path.GetFileName(change.Replacement)) })
            {
                if (!names.Contains(pair.Item1)) throw new InvalidDataException("The target does not directly reference " + change.Original);
                if (substitutions.TryGetValue(pair.Item1, out var previous) && previous != pair.Item2)
                    throw new InvalidDataException("Conflicting replacements share an asset name. Split this edit into unambiguous references.");
                // Renaming shared name entries is unsafe if they also identify unrelated imports or exports.
                if (pair.Item1 == Path.GetFileName(change.Original) &&
                    (original.Exports.Any(e => e.ObjectName.ToString() == pair.Item1) ||
                     original.Imports.Any(i => i.ObjectName.ToString() == pair.Item1 &&
                         (!i.OuterIndex.IsImport() || i.OuterIndex.ToImport(original).ObjectName.ToString() != change.Original))))
                    throw new InvalidDataException("This asset name is shared with another object; reference-specific editing is required.");
                substitutions[pair.Item1] = pair.Item2;
            }
        }
        if (original.Exports.Any(e => e is not RawExport)) throw new InvalidDataException("Expected opaque exports for an appearance-only edit.");
        var payloads = original.Exports.Cast<RawExport>().Select(e => e.Data.ToArray()).ToArray();
        var sourceFiles = new[] { ".uasset", ".uexp", ".ubulk" }.Select(ext => Path.ChangeExtension(Source(recipe.Target), ext)).Where(File.Exists).ToArray();
        var sourceHashes = sourceFiles.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        Directory.CreateDirectory(output);
        var staged = Path.Combine(output, "Stage", "LEGOBatmanLotDK", "Content", recipe.Target[6..] + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        foreach (var file in sourceFiles) File.Copy(file, Path.ChangeExtension(staged, Path.GetExtension(file)));
        string Replace(string name) => substitutions.GetValueOrDefault(name, name);
        if (names.Select(Replace).Distinct(StringComparer.Ordinal).Count() != names.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("The replacements would merge name-map entries. Choose different assets.");
        for (var i = 0; i < names.Length; i++)
            if (Replace(names[i]) != names[i]) original.SetNameReference(i, new FString(Replace(names[i])));
        original.Write(staged);
        var readback = Read(staged);
        if (!readback.GetNameMapIndexList().Select(n => n.ToString()).SequenceEqual(names.Select(Replace)) ||
            readback.Exports.Count != payloads.Length || readback.Imports.Count != original.Imports.Count ||
            readback.FolderName.ToString() != original.FolderName.ToString() ||
            readback.Exports.Where((e, i) => e is not RawExport raw || !raw.Data.SequenceEqual(payloads[i])).Any())
            throw new InvalidDataException("Appearance-only readback validation failed; no PAK created.");
        foreach (var pair in sourceHashes)
            if (pair.Value != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))) throw new IOException("Source changed during build.");
        var stageContent = Path.Combine(output, "Stage", "LEGOBatmanLotDK", "Content");
        CharacterReplacementService.CopyVisualDependencies(changes.Select(c => c.Replacement), content, visualRoots, stageContent, maps);
        if (stageOnly) return stageContent;
        var stageRoot = Path.Combine(output, "Stage");
        var files = Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories);
        var response = Path.Combine(output, "response.txt");
        File.WriteAllLines(response, files.Select(f => $"\"{f}\" \"../../../{Path.GetRelativePath(stageRoot, f).Replace('\\', '/')}\""));
        var pak = Path.Combine(output, "NPC-" + Path.GetFileName(recipe.Target) + "_P.pak");
        void Run(params string[] args)
        {
            var info = new ProcessStartInfo(unrealPak) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); Task.WaitAll(stdout, stderr);
            File.AppendAllText(Path.Combine(output, "packaging.log"), stdout.Result + stderr.Result);
            if (process.ExitCode != 0) throw new IOException("UnrealPak failed; inspect packaging.log.");
        }
        Run(pak, "-Create=" + response); Run(pak, "-Test");
        var extracted = Path.Combine(output, "PakReadback");
        Directory.CreateDirectory(extracted);
        Run(pak, "-Extract", extracted);
        // UnrealPak extracts relative to the common directory (the archive mount).
        // Compare complete relative paths, not basenames: custom dependencies can share names.
        var unpacked = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories);
        var common = Path.GetDirectoryName(files[0])!;
        while (files.Any(file => !FileSystemPathUtil.IsWithinDirectory(file, common))) common = Path.GetDirectoryName(common)!;
        if (unpacked.Length != files.Length || files.Any(file =>
        {
            var read = Path.Combine(extracted, Path.GetRelativePath(common, file));
            return !File.Exists(read) || !File.ReadAllBytes(read).SequenceEqual(File.ReadAllBytes(file));
        }))
            throw new InvalidDataException("Packaged asset readback differs from the certified stage.");
        File.WriteAllText(Path.Combine(output, "recipe.json"), JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new {
            recipe.Target, Changes = changes, ExportPayloadsUnchanged = true, SourceFilesUnchanged = true,
            PakSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pak))), InGameTested = false,
            Scope = "Visual override for all instances of this exact Blueprint. Original game files are not modified. Conflicts with other replacers of the same package."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return pak;
    }
}
