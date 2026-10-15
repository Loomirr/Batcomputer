using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Experimental exact-Quest material-reference replacer. Never installs or edits source assets.</summary>
internal static class NpcAppearanceTestService
{
    internal sealed record Recipe(string Target, string Material, string Replacement);

    internal static void Validate(Recipe recipe)
    {
        foreach (var path in new[] { recipe.Target, recipe.Material, recipe.Replacement })
            if (!path.StartsWith("/Game/", StringComparison.Ordinal) || path.Contains("..") ||
                path.Any(c => !(char.IsLetterOrDigit(c) || c is '/' or '_')))
                throw new InvalidDataException("Use exact base-game package paths without extensions or object suffixes.");
        if (!BaseEligibilityService.IsVisualCharacterPackage(recipe.Target) ||
            !recipe.Target.EndsWith("_Quest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This prototype supports concrete Quest character Blueprints only.");
        if (!Path.GetFileName(recipe.Material).StartsWith("MI_", StringComparison.Ordinal) ||
            !Path.GetFileName(recipe.Replacement).StartsWith("MI_", StringComparison.Ordinal) || recipe.Material == recipe.Replacement)
            throw new InvalidDataException("Choose two different native material-instance packages.");
    }

    internal static string Build(string recipeFile, string content, string mappings, string unrealPak, string output)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(recipeFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing recipe.");
        Validate(recipe);
        content = Path.GetFullPath(content); output = Path.GetFullPath(output);
        if (FileSystemPathUtil.IsWithinDirectory(output, content) || FileSystemPathUtil.IsWithinDirectory(content, output))
            throw new InvalidDataException("Output must be separate from extracted source assets.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new output directory; existing tests are never overwritten.");
        if (!File.Exists(unrealPak)) throw new FileNotFoundException("UnrealPak is required.", unrealPak);
        string Source(string package) => Path.Combine(content, package[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        foreach (var path in new[] { recipe.Target, recipe.Material, recipe.Replacement })
            if (!File.Exists(Source(path))) throw new FileNotFoundException("Extract this package first.", path);
        var maps = new Usmap(mappings);
        const CustomSerializationFlags flags = CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading;
        UAsset Read(string path) => new(path, EngineVersion.VER_UE5_6, maps, flags);
        var original = Read(Source(recipe.Target));
        if (!original.VerifyBinaryEquality()) throw new InvalidDataException("The source does not round-trip exactly with these mappings.");
        var names = original.GetNameMapIndexList().Select(n => n.ToString()).ToArray();
        if (!names.Contains(recipe.Material) || !names.Contains(Path.GetFileName(recipe.Material)))
            throw new InvalidDataException("The target does not directly reference that material. Inherited materials are not changed by this prototype.");
        if (original.Exports.Any(e => e is not RawExport)) throw new InvalidDataException("Expected opaque exports for an appearance-only edit.");
        var payloads = original.Exports.Cast<RawExport>().Select(e => e.Data.ToArray()).ToArray();
        var sourceFiles = new[] { ".uasset", ".uexp", ".ubulk" }.Select(ext => Path.ChangeExtension(Source(recipe.Target), ext)).Where(File.Exists).ToArray();
        var sourceHashes = sourceFiles.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        Directory.CreateDirectory(output);
        var staged = Path.Combine(output, "Stage", "LEGOBatmanLotDK", "Content", recipe.Target[6..] + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        foreach (var file in sourceFiles) File.Copy(file, Path.ChangeExtension(staged, Path.GetExtension(file)));
        string Replace(string name) => name == recipe.Material ? recipe.Replacement
            : name == Path.GetFileName(recipe.Material) ? Path.GetFileName(recipe.Replacement) : name;
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
        // UnrealPak extracts relative to the archive mount point; compare each staged
        // basename and bytes, and reject extra/missing payload files.
        var unpacked = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories);
        if (unpacked.Length != files.Length || files.Any(file => !unpacked.Any(read =>
            Path.GetFileName(read) == Path.GetFileName(file) && File.ReadAllBytes(read).SequenceEqual(File.ReadAllBytes(file)))))
            throw new InvalidDataException("Packaged asset readback differs from the certified stage.");
        File.WriteAllText(Path.Combine(output, "recipe.json"), JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new {
            recipe.Target, recipe.Material, recipe.Replacement, ExportPayloadsUnchanged = true, SourceFilesUnchanged = true,
            PakSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pak))), InGameTested = false,
            Scope = "All instances of this exact NPC Blueprint; all references to this material inside that Blueprint. Conflicts with other replacers of the same package."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return pak;
    }
}
