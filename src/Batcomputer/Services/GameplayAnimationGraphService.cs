using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

/// <summary>Immutable cooked animation closures plus replayable, suit-local ability wiring.</summary>
internal static class GameplayAnimationGraphService
{
    internal sealed record CachedFile(string Path, string Sha256);
    internal sealed record CachedPackage(string Package, string[] Dependencies, CachedFile[] Files);
    internal sealed record Manifest(int Schema, string Id, string Name, CachedPackage[] Packages);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] Extensions = [".uasset", ".uexp", ".ubulk", ".uptnl"];
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    internal static string CacheRoot(string workspace) => Path.Combine(AppSettings.GeneratedRootFor(workspace), "GameplayAnimationGraphs");
    internal static bool ValidPrivatePackage(string package) => HeldItemService.ValidPackage(package) &&
        package.StartsWith("/Game/Mods/", StringComparison.Ordinal) && package.Split('/').Length >= 6;
    internal static bool SupportedPayloadClass(string className) => className is
        "AnimSequence" or "AnimMontage" or "GameplayTagStateMachineDataAsset" or
        "BlueprintGeneratedClass" or "WidgetBlueprintGeneratedClass";
    private static string FileFor(string root, string package)
    {
        if (!ValidPrivatePackage(package)) throw new InvalidDataException("Animation graph outputs must be private /Game/Mods packages.");
        var file = Path.GetFullPath(Path.Combine(root, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset");
        if (!FileSystemPathUtil.IsWithinDirectory(file, root)) throw new InvalidDataException("Animation graph output escaped staging.");
        return file;
    }
    internal static void ValidateSelection(GameplayAnimationGraphSelection selection)
    {
        if (!Regex.IsMatch(selection.Id, "^[a-f0-9]{32}$") || !ValidPrivatePackage(selection.OwnerDprdPackage) ||
            (selection.Abilities.Count == 0 && selection.SupportAbilityPackages.Count == 0) || selection.Abilities.Select(b => b.OriginalPackage).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Abilities.Count ||
            selection.Abilities.Select(b => b.ReplacementPackage).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Abilities.Count ||
            selection.Abilities.Any(b => !HeldItemService.ValidPackage(b.OriginalPackage) || !ValidPrivatePackage(b.ReplacementPackage) || b.OriginalPackage == b.ReplacementPackage) ||
            selection.SupportAbilityPackages.Count > 32 || selection.SupportAbilityPackages.Any(p => !ValidPrivatePackage(p)) ||
            selection.SupportAbilityPackages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.SupportAbilityPackages.Count)
            throw new InvalidDataException("Invalid or conflicting gameplay animation graph bindings.");
    }
    internal static Manifest Import(string workspace, string name, string sourceContent, IEnumerable<string> packagePaths)
    {
        var paths = packagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0 || paths.Any(p => !ValidPrivatePackage(p))) throw new InvalidDataException("Only explicit private animation graph packages may be cached.");
        using var native = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        var maps = new Usmap(AppSettings.Current.EffectiveUsmapPath());
        var id = Guid.NewGuid().ToString("N");var folder = Path.Combine(CacheRoot(workspace), id);Directory.CreateDirectory(folder);
        var packages = new List<CachedPackage>();
        foreach (var package in paths)
        {
            if (native.Files.ContainsKey("LEGOBatmanLotDK/Content/" + package[6..] + ".uasset")) throw new InvalidDataException("Animation graph import would replace native content.");
            var source = FileFor(sourceContent, package);
            if (!File.Exists(source) || !File.Exists(Path.ChangeExtension(source, ".uexp"))) throw new FileNotFoundException("Incomplete cooked animation graph: " + package);
            // Parse the cache copy, never open the user's original with UAssetAPI's writable reader.
            var cached = FileFor(folder, package);Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var files = new List<CachedFile>();
            foreach (var ext in Extensions)
            {
                var from = Path.ChangeExtension(source, ext);if (!File.Exists(from)) continue;
                var to = Path.ChangeExtension(cached, ext);File.Copy(from, to);
                files.Add(new(Path.GetRelativePath(folder, to).Replace('\\', '/'), Hash(to)));
            }
            var asset = new UAsset(cached, EngineVersion.VER_UE5_6, maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
            var classes = asset.Exports.Select(e => e.GetExportClassType()?.ToString() ?? "").ToArray();
            if (!classes.Any(SupportedPayloadClass))
                throw new InvalidDataException("Unsupported animation graph payload: " + package);
            if (asset.Imports.Any(i => i.ObjectName.ToString() is "UnknownPackage" or "UnknownExport")) throw new InvalidDataException("Unresolved cooked graph import: " + package);
            var dependencies = asset.Imports.Where(i => i.OuterIndex.IsNull()).Select(i => i.ObjectName.ToString()).Where(p => p.StartsWith('/')).Distinct().ToArray();
            packages.Add(new(package, dependencies, files.ToArray()));
        }
        foreach (var dependency in packages.SelectMany(p => p.Dependencies).Where(p => p.StartsWith("/Game/Mods/", StringComparison.Ordinal)))
            if (!paths.Contains(dependency, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Incomplete private animation graph closure: " + dependency);
        var manifest = new Manifest(1, id, name, packages.ToArray());
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }
    internal static Manifest Load(string workspace, GameplayAnimationGraphSelection selection)
    {
        ValidateSelection(selection);
        var folder = Path.Combine(CacheRoot(workspace), selection.Id);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(folder, "manifest.json")), Json)
            ?? throw new InvalidDataException("Animation graph cache is missing.");
        if (manifest.Schema != 1 || manifest.Id != selection.Id || manifest.Packages.Length == 0 ||
            manifest.Packages.Any(p => !ValidPrivatePackage(p.Package)) || manifest.Packages.Select(p => p.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Packages.Length)
            throw new InvalidDataException("Invalid cached animation graph manifest.");
        foreach (var binding in selection.Abilities)
            if (!manifest.Packages.Any(p => p.Package == binding.ReplacementPackage)) throw new InvalidDataException("Replacement ability is absent from its saved animation graph.");
        foreach (var support in selection.SupportAbilityPackages)
            if (!manifest.Packages.Any(p => p.Package == support)) throw new InvalidDataException("Support ability is absent from its saved animation graph.");
        foreach (var dependency in manifest.Packages.SelectMany(p => p.Dependencies).Where(p => p.StartsWith("/Game/Mods/", StringComparison.Ordinal)))
            if (!manifest.Packages.Any(p => p.Package.Equals(dependency, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Cached animation graph is missing a private dependency: " + dependency);
        foreach (var package in manifest.Packages)
        {
            if (!package.Files.Any(f => f.Path.EndsWith(".uasset", StringComparison.Ordinal)) || !package.Files.Any(f => f.Path.EndsWith(".uexp", StringComparison.Ordinal)) ||
                package.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Files.Length)
                throw new InvalidDataException("Incomplete cached animation graph package.");
            foreach (var file in package.Files)
            {
                var source = Path.GetFullPath(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                var expected = package.Package[6..] + Path.GetExtension(file.Path);
                if (!FileSystemPathUtil.IsWithinDirectory(source, folder) || file.Path != expected || !Extensions.Contains(Path.GetExtension(file.Path)) ||
                    !Regex.IsMatch(file.Sha256, "^[A-Fa-f0-9]{64}$") || !File.Exists(source) || Hash(source) != file.Sha256.ToUpperInvariant())
                    throw new InvalidDataException("Saved animation graph cache is missing or changed: " + package.Package);
            }
        }
        return manifest;
    }
    internal static void Stage(NativeSuitProject project, string workspace, string contentRoot, Action<string> log)
    {
        ValidateSelections(project);
        foreach (var selection in project.GameplayAnimationGraphs)
        {
            var manifest = Load(workspace, selection);var folder = Path.Combine(CacheRoot(workspace), selection.Id);
            foreach (var package in manifest.Packages)
            foreach (var file in package.Files)
            {
                var target = Path.ChangeExtension(FileFor(contentRoot, package.Package), Path.GetExtension(file.Path));
                if (File.Exists(target) && Hash(target) != file.Sha256.ToUpperInvariant()) throw new InvalidDataException("Conflicting staged animation graph package: " + package.Package);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            log($"Animation graph '{selection.Name}': staged {manifest.Packages.Length} verified private packages.");
        }
    }
    internal static void Apply(NativeSuitProject project, string contentRoot, Action<string> log)
    {
        if (project.GameplayAnimationGraphs.Count == 0) return;
        ValidateSelections(project);
        var mod = project.TargetPackages.Playable.Split('/').ElementAtOrDefault(3) ?? "";
        var owned = "/Game/Mods/" + mod + "/";
        var maps = new Usmap(AppSettings.Current.EffectiveUsmapPath());
        var mutation = new AbilityAssetMutationService();
        using var context = new SwordCombatService.Context(AppSettings.Current.EffectiveExtractedContentRoot(), contentRoot, mod, maps, owned.TrimEnd('/') + "/AnimationGraphs");
        foreach (var group in project.GameplayAnimationGraphs.GroupBy(g => g.OwnerDprdPackage, StringComparer.OrdinalIgnoreCase))
        {
            var bindings = group.SelectMany(g => g.Abilities).ToArray();
            if (!group.Key.StartsWith(owned, StringComparison.Ordinal)) throw new InvalidDataException("Animation graph wiring must target this suit's own DPRD.");
            foreach (var binding in bindings)
                if (!File.Exists(FileFor(contentRoot, binding.ReplacementPackage))) throw new InvalidDataException("Animation graph replacement was not staged.");
            var dprdFile = FileFor(contentRoot, group.Key);
            var before = mutation.InspectDprdAbilitySets(dprdFile);
            if (!before.Success) throw new InvalidDataException(before.Error);
            var sets = before.AbilitySets.Select(s => s.PackagePath).ToList();var changed = 0;
            for (int index = 0; index < sets.Count; index++)
            {
                var source = context.Read(sets[index]);
                var originalGrants = ReadGrants(source);
                var edits = bindings.Where(b => originalGrants.Contains(b.OriginalPackage, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (edits.Length == 0) continue; // an explicitly removed native ability stays removed
                var target = DerivedSet(mod, sets[index]);
                context.Clone(sets[index], target);
                var result = mutation.ApplyGameplayAbilityEdits(context.PathFor(target), edits.Select(b => new AbilityAssetMutationService.GameplayAbilityEdit
                { Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Replace, TargetPackagePath = b.OriginalPackage, ReplacementPackagePath = b.ReplacementPackage }).ToArray());
                if (!result.Success) throw new InvalidDataException(result.Error);
                var verified = mutation.InspectAbilitySet(context.PathFor(target));
                var expected = originalGrants.Select(p => edits.FirstOrDefault(b => b.OriginalPackage == p)?.ReplacementPackage ?? p);
                if (!verified.Success || !verified.GameplayAbilities.Select(g => g.PackagePath).SequenceEqual(expected)) throw new InvalidDataException("Animation graph changed unrelated ability grants or their order.");
                sets[index] = target;changed += edits.Length;
            }
            if (changed > 0)
            {
                var result = mutation.SetDprdAbilitySets(dprdFile, sets);
                if (!result.Success || !mutation.InspectDprdAbilitySets(dprdFile).AbilitySets.Select(s => s.PackagePath).SequenceEqual(sets)) throw new InvalidDataException("Animation graph DPRD wiring failed verification.");
            }
            log($"Animation graph '{string.Join(", ", group.Select(g => g.Name))}': connected {changed} ability grant(s); removed or already-connected grants left unchanged.");
        }
    }
    internal static string DerivedSet(string mod, string source) => "/Game/Mods/" + mod + "/AnimationGraphs/AS_Graph_" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..12];
    // Certification may run before or after graph replay. A generated set is
    // accepted only when every decoded field equals its resolved source except
    // for the declared ability substitutions and the private clone identity.
    internal static bool CertifySets(NativeSuitProject project, string contentRoot, IReadOnlyList<string> expected, IReadOnlyList<string> actual, out string error)
    {
        error = "";
        if (expected.Count != actual.Count) { error = "Animation graph changed DPRD set count.";return false; }
        var mod = project.TargetPackages.Playable.Split('/').ElementAtOrDefault(3) ?? "";
        try
        {
            using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!, [contentRoot]);
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i].Equals(actual[i],StringComparison.OrdinalIgnoreCase)) continue;
                if (project.GameplayAnimationGraphs.Count == 0 || actual[i] != DerivedSet(mod,expected[i])) throw new InvalidDataException("Unexpected generated animation AbilitySet.");
                var wanted = CharacterVoiceBuildService.CanonicalPaths(JArray.FromObject(provider.LoadPackage(expected[i]).GetExports()));
                var observed = CharacterVoiceBuildService.CanonicalPaths(JArray.FromObject(provider.LoadPackage(actual[i]).GetExports()));
                var names = new Dictionary<string,string>();
                void Map(string from,string to)
                {
                    names[from]=to;var old=UnrealPathUtil.AssetName(from);var next=UnrealPathUtil.AssetName(to);
                    names[old]=next;names[old+"_C"]=next+"_C";names["Default__"+old+"_C"]="Default__"+next+"_C";
                }
                Map(expected[i],actual[i]);
                foreach(var binding in project.GameplayAnimationGraphs.SelectMany(g=>g.Abilities))Map(binding.OriginalPackage,binding.ReplacementPackage);
                foreach(var value in wanted.Descendants().OfType<JValue>().Where(v=>v.Type==JTokenType.String))value.Value=CharacterVoiceBuildService.RedirectText(value.Value<string>()!,names);
                if(!JToken.DeepEquals(wanted,observed))throw new InvalidDataException("Animation graph changed undeclared ability fields, levels, input tags, effects or ordering.");
            }
            return true;
        }
        catch(Exception ex) { error=ex.Message;return false; }
    }
    internal static void VerifyStaged(NativeSuitProject project, string workspace, string contentRoot)
    {
        if (project.GameplayAnimationGraphs.Count == 0) return;
        ValidateSelections(project);
        var mod = project.TargetPackages.Playable.Split('/').ElementAtOrDefault(3) ?? "";
        using var context = new SwordCombatService.Context(AppSettings.Current.EffectiveExtractedContentRoot(), contentRoot, mod,
            new Usmap(AppSettings.Current.EffectiveUsmapPath()), $"/Game/Mods/{mod}/AnimationGraphs");
        var mutation = new AbilityAssetMutationService();
        foreach(var selection in project.GameplayAnimationGraphs)
        {
            var manifest=Load(workspace,selection);
            foreach(var package in manifest.Packages)
            foreach(var file in package.Files)
                if(Hash(Path.ChangeExtension(FileFor(contentRoot,package.Package),Path.GetExtension(file.Path)))!=file.Sha256.ToUpperInvariant())
                    throw new InvalidDataException("Staged gameplay animation graph differs from its saved cache.");
            var sets=mutation.InspectDprdAbilitySets(FileFor(contentRoot,selection.OwnerDprdPackage));
            if(!sets.Success)throw new InvalidDataException(sets.Error);
            var allGrants = new List<string>();
            foreach(var set in sets.AbilitySets)
            {
                var grants = ReadGrants(context.Read(set.PackagePath));
                allGrants.AddRange(grants);
                if(grants.Any(g=>selection.Abilities.Any(b=>b.OriginalPackage==g)))
                    throw new InvalidDataException("A saved gameplay animation graph was staged but its original ability is still active.");
            }
            foreach (var support in selection.SupportAbilityPackages)
                if (allGrants.Count(g => g.Equals(support, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidDataException("A saved gameplay graph support ability must be granted exactly once in its staged owner DPRD.");
        }
    }
    internal static void ValidateSelections(NativeSuitProject project)
    {
        foreach (var selection in project.GameplayAnimationGraphs) ValidateSelection(selection);
        var added = project.AbilityLoadout?.AbilitySets.Where(s => s.Enabled).SelectMany(s => s.AddedGameplayAbilities).Select(g => g.PackagePath).ToArray() ?? [];
        foreach (var support in project.GameplayAnimationGraphs.SelectMany(g => g.SupportAbilityPackages).Distinct(StringComparer.OrdinalIgnoreCase))
            if (added.Count(p => p.Equals(support, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("The saved gameplay graph requires exactly one enabled support ability grant: " + support);
        var bindings = project.GameplayAnimationGraphs.SelectMany(g => g.Abilities.Select(b => (g.OwnerDprdPackage, Binding: b))).ToArray();
        if (bindings.GroupBy(b => b.OwnerDprdPackage + "|" + b.Binding.OriginalPackage, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) ||
            bindings.Any(b => bindings.Any(other => b.OwnerDprdPackage.Equals(other.OwnerDprdPackage, StringComparison.OrdinalIgnoreCase) &&
                b.Binding.ReplacementPackage.Equals(other.Binding.OriginalPackage, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Saved animation graphs contain conflicting or chained ability replacements.");
    }
    private static string[] ReadGrants(UAsset asset) => asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>().SelectMany(e => e.Data)
        .OfType<UAssetAPI.PropertyTypes.Objects.ArrayPropertyData>().Where(p => p.Name.ToString() == "GrantedGameplayAbilities")
        .SelectMany(p => p.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        .SelectMany(p => p.Value.OfType<UAssetAPI.PropertyTypes.Objects.ObjectPropertyData>())
        .Where(p => p.Name.ToString() == "Ability").Select(p => SwordCombatService.Package(asset, p.Value)).ToArray();
}
