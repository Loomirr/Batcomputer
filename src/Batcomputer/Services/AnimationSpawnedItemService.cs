using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Exact notify visual edits with a private, reverse-reachable animation closure.</summary>
internal static class AnimationSpawnedItemService
{
    internal sealed record Binding(AnimationSpawnedItemEdit Identity, string Slot);
    internal sealed record Inspection(IReadOnlyList<Binding> Items, IReadOnlyList<string> Warnings);
    private static readonly HashSet<string> AnimationClasses = new(StringComparer.Ordinal)
    { "AnimSequence", "AnimMontage", "AnimComposite", "BlendSpace", "BlendSpace1D", "AnimBlueprintGeneratedClass", "TTAnimSet", "TTLayerSet", "TTCompositeAnimSet", "TTCompositeLayerSet", "GameplayTagStateMachine" };
    internal static string Root(string mod) => $"/Game/Mods/{mod}/AnimationItems";
    internal static string Alias(string mod, string source) => Root(mod) + "/" + UnrealPathUtil.AssetName(source) + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    private static string Fingerprint(string file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
            if (File.Exists(Path.ChangeExtension(file, ext))) hash.AppendData(File.ReadAllBytes(Path.ChangeExtension(file, ext)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static UAsset Read(string file, Usmap mappings) => new(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
    internal static string? Existing(string root, string package)
    {
        var file = ExtractedPackagePathService.ResolvePackageUasset(root, package);
        return file is not null && File.Exists(file) ? file : null;
    }
    private sealed class NativeParents : IDisposable
    {
        internal readonly string Output = Path.Combine(Path.GetTempPath(), "BatcomputerAnimationItems-" + Guid.NewGuid().ToString("N"));
        internal string Content => Path.Combine(Output, "LEGOBatmanLotDK", "Content");
        private string? _mount;
        internal string? Resolve(string package)
        {
            var file = Existing(Content, package);
            if (file is not null) return file;
            // Shared base controllers are often omitted by character-only extraction. Read them
            // on demand from shipped containers, excluding all installed author mods.
            if (!UnrealPathUtil.AssetName(package).StartsWith("ABP_", StringComparison.Ordinal)) return null;
            var slash = package.IndexOf('/', 1);
            if (slash < 0) return null;
            Directory.CreateDirectory(Output);
            var paks = AppSettings.Current.EffectiveGamePaksRoot();
            _mount ??= GameAssetRefreshService.CreateCombinedContainerMount(paks, GameAssetRefreshService.DlcRootForPaksRoot(paks), Output);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var filter = package.StartsWith("/Game/", StringComparison.Ordinal) ? "Content/" + package[6..]
                : "GameFeatures/" + package[1..slash] + "/Content/" + package[(slash + 1)..];
            var result = GameAssetRefreshService.RunRetocAsync(AppSettings.Current.EffectiveRetocExePath(), _mount, Output, filter, timeout.Token).GetAwaiter().GetResult();
            if (result.ExitCode != 0) throw new InvalidDataException("Cannot read shared animation controller: " + package);
            return Existing(Content, package);
        }
        public void Dispose()
        {
            GameAssetRefreshService.TryDeleteCombinedContainerMount(_mount);
            // Keep the small extracted files for diagnostics; no native file is ever written.
        }
    }
    private static IEnumerable<PropertyData> Walk(IEnumerable<PropertyData> properties)
    {
        foreach (var property in properties) {
            yield return property;
            var children = property switch {
                StructPropertyData s => s.Value.AsEnumerable(),
                ArrayPropertyData a => a.Value.AsEnumerable(),
                MapPropertyData m => m.Value.SelectMany(p => new[] { p.Key, p.Value }),
                _ => []
            };
            foreach (var child in Walk(children)) yield return child;
        }
    }
    private static IEnumerable<string> Children(UAsset asset)
    {
        // Import tables retain unused donor references. Follow serialized object references,
        // including class ancestry, rather than treating every old import as active wiring.
        var references = asset.Exports.SelectMany(e => new[] { e.ClassIndex, e.SuperIndex, e.TemplateIndex })
            .Concat(asset.Exports.OfType<NormalExport>().SelectMany(e => Walk(e.Data)).OfType<ObjectPropertyData>().Select(p => p.Value));
        return references.Where(i => i.IsImport() && AnimationClasses.Contains(i.ToImport(asset).ClassName.ToString()))
            .Select(i => SwordCombatService.Package(asset, i)).Where(HeldItemService.ValidPackage).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    internal static IReadOnlyList<Binding> InspectAsset(UAsset asset, string package, string fingerprint)
    {
        var items = new List<Binding>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is not NormalExport export || export.GetExportClassType()?.ToString() != "AnimNotfiy_SpawnActor_DataObj") continue;
            var mesh = export.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "StaticMeshToSpawn");
            if (mesh?.Value.IsImport() != true) continue;
            var original = SwordCombatService.Package(asset, mesh.Value);
            var slot = "Unknown / inherited";
            var request = export.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "AttachmentRequestData");
            if (request?.Value.IsExport() == true && request.Value.ToExport(asset) is NormalExport attachment)
            {
                var primary = attachment.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "PrimarySlotData");
                if (primary?.Value.IsExport() == true && primary.Value.ToExport(asset) is NormalExport location)
                    slot = location.Data.OfType<StructPropertyData>().Where(p => p.Name.ToString() == "SlotTag").SelectMany(p => p.Value.OfType<NamePropertyData>()).FirstOrDefault()?.Value.ToString() ?? slot;
            }
            items.Add(new(new() { AnimationPackage = package, ExportIndex = i, OriginalMeshPackage = original, SourceFingerprint = fingerprint }, slot));
        }
        return items;
    }
    internal static Inspection Inspect(NativeSuitProject project)
    {
        var graph = new CharacterAnimationGraphService().Build(project);
        var warnings = graph.Diagnostics.Select(d => d.Message).ToList();
        var roots = graph.Sets.Select(s => s.EffectivePackage).Concat(graph.Sets.SelectMany(s => s.Slots).SelectMany(s => s.Targets).Select(t => t.EffectivePackage))
            .Concat(graph.LocomotionSequences.Select(t => t.EffectivePackage));
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        using var parents = new NativeParents();
        var queue = new Queue<string>(roots.Where(HeldItemService.ValidPackage)); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var items = new List<Binding>();
        while (queue.TryDequeue(out var package))
        {
            if (!seen.Add(package)) continue;
            if (seen.Count > 12000) { warnings.Add("Animation discovery limit reached; not all dynamic items are covered."); break; }
            try {
                var file = Existing(extracted, package) ?? parents.Resolve(package) ?? throw new FileNotFoundException(package);
                var asset = Read(file, mappings); items.AddRange(InspectAsset(asset, package, Fingerprint(file)));
                foreach (var child in Children(asset)) queue.Enqueue(child);
            } catch (Exception ex) { warnings.Add(UnrealPathUtil.AssetName(package) + ": " + ex.Message); }
        }
        return new(items.DistinctBy(b => b.Identity.Key).ToArray(), warnings);
    }
    internal static IReadOnlyList<string> Validate(AbilityLoadoutProfile profile)
    {
        var errors = new List<string>();
        if (profile.AnimationSpawnedItems.GroupBy(e => e.Key).Any(g => g.Count() > 1)) errors.Add("An animation spawn binding is edited twice.");
        foreach (var edit in profile.AnimationSpawnedItems)
        {
            if (!HeldItemService.ValidPackage(edit.AnimationPackage) || !HeldItemService.ValidPackage(edit.OriginalMeshPackage) || edit.ExportIndex < 0 || edit.SourceFingerprint.Length != 64 || !edit.SourceFingerprint.All(Uri.IsHexDigit)) errors.Add("Invalid animation item source binding.");
            if (edit.Hide) continue;
            if (edit.HeldItemId.Length > 0)
            {
                if (edit.ReplacementMeshPackage.Length > 0 || HeldItemService.Resolve(profile).Count(i => i.Id == edit.HeldItemId) != 1) errors.Add("Choose one existing extra prop, or restore its animation-item references before removing it.");
            }
            else if (!HeldItemService.ValidPackage(edit.ReplacementMeshPackage)) errors.Add("Choose an extra prop or a cooked static mesh for the animation item.");
        }
        return errors;
    }
    internal static void ReplaceMesh(UAsset asset, AnimationSpawnedItemEdit edit, string? mesh)
    {
        if (edit.ExportIndex >= asset.Exports.Count || asset.Exports[edit.ExportIndex] is not NormalExport export || export.GetExportClassType()?.ToString() != "AnimNotfiy_SpawnActor_DataObj") throw new InvalidDataException("The animation spawn export changed.");
        var property = export.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMeshToSpawn");
        if (!property.Value.IsImport() || SwordCombatService.Package(asset, property.Value) != edit.OriginalMeshPackage) throw new InvalidDataException("The original animation item changed.");
        property.Value = mesh is null ? new FPackageIndex(0) : SwordCombatService.Obj(asset, mesh, UnrealPathUtil.AssetName(mesh), "/Script/Engine", "StaticMesh");
        if (mesh is not null && !export.CreateBeforeSerializationDependencies.Contains(property.Value)) export.CreateBeforeSerializationDependencies.Add(property.Value);
    }
    internal static HashSet<string> Impacted(IEnumerable<string> edited, IReadOnlyDictionary<string, string[]> edges)
    {
        var result = edited.ToHashSet(StringComparer.OrdinalIgnoreCase); bool changed;
        do { changed = false; foreach (var (parent, children) in edges) if (children.Any(result.Contains)) changed |= result.Add(parent); } while (changed);
        return result;
    }
    private static bool Rewrite(UAsset asset, IReadOnlyDictionary<string, string> redirects)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in redirects) {
            names[from] = to; var old = UnrealPathUtil.AssetName(from); var replacement = UnrealPathUtil.AssetName(to);
            foreach (var suffix in new[] { "", "_C" }) {
                if (names.TryGetValue(old + suffix, out var other) && other != replacement + suffix) throw new InvalidDataException("Ambiguous animation asset names; refusing a guessed redirect.");
                names[old + suffix] = replacement + suffix;
            }
            names["Default__" + old + "_C"] = "Default__" + replacement + "_C";
        }
        var changed = false;
        for (int i = 0; i < asset.GetNameMapIndexList().Count; i++) if (names.TryGetValue(asset.GetNameReference(i).ToString(), out var replacement)) { asset.SetNameReference(i, new FString(replacement)); changed = true; }
        return changed;
    }
    internal static void ApplyToPackagedRoot(NativeSuitProject project, string staged, Action<string> log)
    {
        var profile = project.AbilityLoadout ?? new();
        var errors = Validate(profile); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var owner = project.TargetPackages.Playable.Split('/');
        if (owner.Length < 5 || owner[1] != "Game" || owner[2] != "Mods") {
            if (profile.AnimationSpawnedItems.Count == 0) return;
            throw new InvalidDataException("Animation item changes require a private suit namespace.");
        }
        var mod = owner[3]; var ownerRoot = $"/Game/Mods/{mod}/";
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!); var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        using var parents = new NativeParents();
        var reportPath = Path.Combine(Path.GetDirectoryName(staged)!, "AnimationItemsBuild", mod + ".json");
        if (profile.AnimationSpawnedItems.Count == 0 && !File.Exists(reportPath)) return;
        var prior = File.Exists(reportPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(reportPath))! : new();
        if (prior.Any(p => !HeldItemService.ValidPackage(p.Key) || !p.Value.StartsWith(Root(mod) + "/", StringComparison.Ordinal))) throw new InvalidDataException("Invalid animation item staging history.");
        var reverse = prior.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
        var roots = Directory.GetFiles(Path.Combine(staged, "Mods", mod), "*.uasset", SearchOption.AllDirectories).Select(f => "/Game/" + Path.GetRelativePath(staged, f).Replace('\\', '/')[..^7]).Where(p => !p.StartsWith(Root(mod) + "/", StringComparison.Ordinal)).ToArray();
        var assets = new Dictionary<string, UAsset>(StringComparer.OrdinalIgnoreCase); var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var edges = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var missing = new List<string>();
        var restoredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var package)) {
            package = reverse.GetValueOrDefault(package, package); if (!visited.Add(package)) continue;
            if (visited.Count > 12000) throw new InvalidDataException("Animation dependency graph exceeded the safe staging limit.");
            var file = Existing(staged, package) ?? Existing(extracted, package) ?? parents.Resolve(package);
            if (file is null || !File.Exists(file)) {
                if (roots.Contains(package) || profile.AnimationSpawnedItems.Any(e => e.AnimationPackage == package)) throw new FileNotFoundException("Edited animation dependency is missing: " + package);
                missing.Add(package); continue; // Unextracted shared parents remain native; never guess their graph or clone them.
            }
            var asset = Read(file, mappings);
            if (reverse.Count > 0 && Rewrite(asset, reverse) && roots.Contains(package)) restoredRoots.Add(package);
            assets.Add(package, asset); files.Add(package, file); edges.Add(package, Children(asset).ToArray());
            foreach (var child in edges[package]) queue.Enqueue(child);
        }
        if (missing.Count > 0) log($"Animation items: {missing.Count} unextracted shared dependencies retained unchanged. Only discovered, reachable spawn bindings can be edited.");
        foreach (var edit in profile.AnimationSpawnedItems) {
            if (!assets.ContainsKey(edit.AnimationPackage)) throw new InvalidDataException("Edited animation is no longer reachable from this suit: " + edit.AnimationPackage + ". Restore the stale item edit before building.");
            if (Fingerprint(files[edit.AnimationPackage]) != edit.SourceFingerprint) throw new InvalidDataException("Animation item donor changed; rescan it before building: " + edit.AnimationPackage);
        }
        var impacted = Impacted(profile.AnimationSpawnedItems.Select(e => e.AnimationPackage), edges);
        var redirects = impacted.Where(p => !p.StartsWith(ownerRoot, StringComparison.Ordinal)).ToDictionary(p => p, p => Alias(mod, p), StringComparer.OrdinalIgnoreCase);
        foreach (var package in restoredRoots.Where(p => !impacted.Contains(p))) assets[package].Write(files[package]);
        // Supply the clone writer a read-only source pool containing exactly the needed originals.
        foreach (var package in redirects.Keys) {
            var slash = package.IndexOf('/', 1);
            var target = package.StartsWith("/Game/", StringComparison.Ordinal) ? Path.Combine(parents.Content, package[6..] + ".uasset")
                : Path.Combine(parents.Output, "LEGOBatmanLotDK", "Plugins", "GameFeatures", package[1..slash], "Content", package[(slash + 1)..] + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" }) {
                var from = Path.ChangeExtension(files[package], ext); var to = Path.ChangeExtension(target, ext);
                if (File.Exists(from) && !Path.GetFullPath(from).Equals(Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) File.Copy(from, to, true);
            }
        }
        using var context = new SwordCombatService.Context(parents.Content, staged, mod, mappings, Root(mod));
        foreach (var package in impacted) {
            var target = redirects.GetValueOrDefault(package, package); var asset = redirects.ContainsKey(package) ? context.Clone(package, target, redirects) : assets[package];
            NativeBlueprintSchemaService.EnsureParents(asset); Rewrite(asset, redirects);
            foreach (var edit in profile.AnimationSpawnedItems.Where(e => e.AnimationPackage == package)) {
                string? mesh = null;
                if (!edit.Hide) {
                    if (edit.HeldItemId.Length > 0) {
                        var item = HeldItemService.Resolve(profile).Single(i => i.Id == edit.HeldItemId);
                        var actorFile = Path.Combine(staged, $"Mods/{mod}/HeldItems/{item.Id}/BP_HeldItem_{item.TemplateId.Replace('-', '_')}.uasset");
                        var actor = Read(actorFile, mappings);
                        var component = actor.Exports.OfType<NormalExport>().Single(e => e.Data.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "StaticMesh"));
                        mesh = SwordCombatService.Package(actor, component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value);
                        // The notify spawns a mesh, not our actor. Component-only material overrides must be baked into a private mesh.
                        var materialOverrides = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
                        if (materialOverrides is { Value.Length: > 0 }) mesh = BakeMaterialMesh(context, mesh, actor, materialOverrides, Root(mod) + "/SM_NotifyProp_" + item.Id);
                    } else mesh = edit.ReplacementMeshPackage;
                    var meshFile = Existing(staged, mesh) ?? Existing(extracted, mesh) ?? throw new FileNotFoundException(mesh);
                    if (!Read(meshFile, mappings).Exports.Any(e => e.GetExportClassType()?.ToString() == "StaticMesh")) throw new InvalidDataException("Animation items require a static mesh.");
                }
                ReplaceMesh(asset, edit, mesh);
            }
            var destination = Path.Combine(staged, target[6..] + ".uasset"); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); asset.Write(destination);
            var after = Read(destination, mappings); if (after.Imports.Any(i => i.ObjectName.ToString() is "UnknownPackage" or "UnknownExport")) throw new InvalidDataException("Unresolved animation item import.");
            CustomEquipmentService.RequirePropertyLayoutRoundtrip(asset, after, target);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!); File.WriteAllText(reportPath, JsonSerializer.Serialize(redirects));
        log($"Animation items: {profile.AnimationSpawnedItems.Count} exact visual bindings; {redirects.Count} private animation dependencies. Native assets and attack timings unchanged.");
    }
    private static string BakeMaterialMesh(SwordCombatService.Context context, string mesh, UAsset actor, ArrayPropertyData overrides, string target)
    {
        var asset = context.Clone(mesh, target);
        var export = asset.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "StaticMesh");
        var materials = export.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "StaticMaterials");
        if (overrides.Value.Length > materials.Value.Length) throw new InvalidDataException("Animation prop material override exceeds its mesh slots.");
        for (int i = 0; i < overrides.Value.Length; i++) {
            if (overrides.Value[i] is not ObjectPropertyData material || !material.Value.IsImport()) continue;
            var package = SwordCombatService.Package(actor, material.Value);
            var import = material.Value.ToImport(actor);
            if (import.ClassName.ToString() is not ("Material" or "MaterialInstanceConstant")) throw new InvalidDataException("Animation prop override is not a material.");
            if (materials.Value[i] is not StructPropertyData entry) throw new InvalidDataException("Unsupported static material layout.");
            var field = entry.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "MaterialInterface");
            field.Value = SwordCombatService.Obj(asset, package, UnrealPathUtil.AssetName(package), "/Script/Engine", import.ClassName.ToString());
            if (!export.CreateBeforeSerializationDependencies.Contains(field.Value)) export.CreateBeforeSerializationDependencies.Add(field.Value);
        }
        context.Write(asset, target);
        return target;
    }
}
