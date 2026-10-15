using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Build-local shared work, independent of the opt-in persistent preparation cache.</summary>
internal sealed class BuildSharedAssets : IDisposable
{
    private static readonly AsyncLocal<BuildSharedAssets?> Active = new();
    internal static BuildSharedAssets? Current => Active.Value;
    private readonly BuildSharedAssets? previous;
    private readonly Dictionary<string, string> meshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> nativeSources = new(StringComparer.Ordinal);
    private sealed record NativeBytes(byte[] Data, bool Split, long HeaderLength, long ExportLength, DateTime HeaderTime, DateTime ExportTime);
    private readonly Dictionary<string, NativeBytes> nativeBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharacterVoiceCatalogService.Snapshot> voiceCatalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Newtonsoft.Json.Linq.JArray> voiceExports = new(StringComparer.Ordinal);
    private CUE4Parse.FileProvider.DefaultFileProvider? voiceProvider;
    private int voiceExportReuses;

    // Shipped containers only. Mutable authoring/verification overlays must use fresh providers.
    internal CUE4Parse.FileProvider.DefaultFileProvider NativeVoiceProvider()
    {
        if (voiceProvider is null)
        {
            voiceProvider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), mappings);
            voiceProvider.ReadScriptData = true;
        }
        return voiceProvider;
    }

    internal Newtonsoft.Json.Linq.JArray VoiceExports(string package, Func<Newtonsoft.Json.Linq.JArray> inspect)
    {
        if (package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) return inspect();
        if (voiceExports.TryGetValue(package, out var saved))
        {
            voiceExportReuses++;
            return (Newtonsoft.Json.Linq.JArray)saved.DeepClone();
        }
        var result = inspect();
        var size = Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None));
        if (bytes + size <= MaximumBytes)
        {
            voiceExports.Add(package, (Newtonsoft.Json.Linq.JArray)result.DeepClone());
            bytes += size;
        }
        return result;
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "Batcomputer-shared-build-" + Guid.NewGuid().ToString("N"));
    private readonly string extracted, mappings;
    private readonly Action<string> log;
    private int reused;
    private int nativeReads, nativeReuses;
    private int sourceReads, sourceReuses;
    private int voiceCatalogReuses;
    private long bytes;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly string[] Extensions = [".uasset", ".uexp", ".ubulk", ".uptnl"];

    internal CharacterVoiceCatalogService.Snapshot VoiceCatalog(string donor, Func<CharacterVoiceCatalogService.Snapshot> inspect)
    {
        if (!voiceCatalogs.TryGetValue(donor, out var snapshot))
        {
            snapshot = inspect();
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot));
            // Do not memoize incomplete discovery or suit-local donors.
            if (snapshot.Warnings.Length == 0 && !donor.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) && bytes + size <= MaximumBytes)
            { voiceCatalogs.Add(donor, snapshot); bytes += size; }
        }
        else voiceCatalogReuses++;
        return snapshot with { Lines = snapshot.Lines.ToArray(), Warnings = snapshot.Warnings.ToArray() };
    }

    // Class-independent reuse of immutable extracted inputs. Never accept a mutable suit stage.
    // The caller parses its own UAsset from a read-only stream, not a shared object graph.
    internal bool TryReadNativeBytes(string file, out byte[] data, out bool split)
    {
        data = []; split = false;
        if (!Path.IsPathFullyQualified(extracted) || !FileSystemPathUtil.IsWithinDirectory(file, extracted)) return false;
        file = Path.GetFullPath(file);
        var header = new FileInfo(file);
        var exports = new FileInfo(Path.ChangeExtension(file, ".uexp"));
        if (!header.Exists) return false;
        split = exports.Exists;
        var exportLength = split ? exports.Length : 0;
        var exportTime = split ? exports.LastWriteTimeUtc : default;
        if (nativeBytes.TryGetValue(file, out var saved))
        {
            if (saved.HeaderLength != header.Length || saved.ExportLength != exportLength || saved.Split != split ||
                saved.HeaderTime != header.LastWriteTimeUtc || saved.ExportTime != exportTime)
                throw new IOException("Native extracted source changed during this build; rebuild with stable inputs: " + file);
            sourceReuses++; data = saved.Data; return true;
        }
        var size = header.Length + exportLength;
        if (bytes + size > MaximumBytes) return false;
        using var stream = new MemoryStream((int)size);
        using (var source = File.OpenRead(file)) source.CopyTo(stream);
        if (split) { using var source = File.OpenRead(exports.FullName); source.CopyTo(stream); }
        data = stream.ToArray();
        nativeBytes.Add(file, new(data, split, header.Length, exportLength, header.LastWriteTimeUtc, exportTime));
        bytes += size; sourceReads++;
        return true;
    }

    internal BuildSharedAssets(string extracted, string mappings, Action<string> log)
    {
        this.extracted = extracted; this.mappings = mappings; this.log = log;
        previous = Active.Value; Active.Value = this;
    }

    // Geometry and slot layout participate. Bindings are applied to independent outputs below.
    // Preserve the equality pattern of material references so shared imports stay shared, too.
    internal static string Key(WeaponModelRecipe recipe) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GeometryRecipe(recipe)))));

    private static WeaponModelRecipe GeometryRecipe(WeaponModelRecipe recipe)
    {
        var copy = recipe.Clone();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var material in copy.Materials)
        {
            if (!paths.TryGetValue(material.MaterialPath, out var placeholder))
                paths.Add(material.MaterialPath, placeholder = "/Game/Mods/BuildShared/BCSharedMaterial_" + paths.Count);
            material.MaterialPath = placeholder;
        }
        return copy;
    }

    internal static IEnumerable<WeaponModelRecipe> Models(NativeSuitProject project)
    {
        var p = project.AbilityLoadout;
        if (p is null) yield break;
        if (SwordCombatService.Enabled(p) && p.SwordCombat?.CustomModel is { } sword) yield return sword;
        if (HeldItemService.Independent(p))
            foreach (var item in p.HeldItems ?? [])
                if (item.CustomModel is { } model) yield return model;
        foreach (var item in p.NativeHeldItems)
            if (!item.Hide && item.CustomModel is { } model) yield return model;
    }

    internal void Prepare(IEnumerable<NativeSuitProject> projects)
        => PrepareModels(projects.SelectMany(Models));

    internal void PrepareModels(IEnumerable<WeaponModelRecipe> recipes)
    {
        var groups = recipes.GroupBy(Key).Where(g => g.Count() > 1).ToArray();
        log($"Shared-asset plan: {groups.Length} repeated custom mesh recipe(s); suit materials, gameplay and removals stay independent. Persistent cache is {(AppSettings.Current.EnableSuitPreparationCache ? "enabled" : "off")}.");
        foreach (var group in groups)
        {
            if (bytes >= MaximumBytes) break;
            var package = "/Game/Mods/BuildShared/SM_" + group.Key;
            WeaponModelService.BakeUnshared(GeometryRecipe(group.First()), extracted, mappings, root, package);
            var size = Extensions.Sum(ext => File.Exists(PathFor(root, package) + ext)
                ? new FileInfo(PathFor(root, package) + ext).Length : 0L);
            if (bytes + size > MaximumBytes)
            {
                foreach (var ext in Extensions) File.Delete(PathFor(root, package) + ext);
                continue;
            }
            bytes += size;
            meshes.Add(group.Key, package);
        }
        log($"Shared-asset preparation: cooked {meshes.Count} shared mesh(es) once; {bytes / 1048576d:F1} MiB temporary data, removed after this build.");
    }

    private static string PathFor(string content, string package)
    {
        if (!package.StartsWith("/Game/Mods/", StringComparison.Ordinal) || !HeldItemService.ValidPackage(package))
            throw new InvalidDataException("Shared mesh output must be a private mod package.");
        var path = Path.GetFullPath(Path.Combine(content, package[6..].Replace('/', Path.DirectorySeparatorChar)));
        if (!FileSystemPathUtil.IsWithinDirectory(path, content)) throw new InvalidDataException("Shared mesh escaped build staging.");
        return path;
    }

    // Resolve only immutable shipped packages. Suit-local stages are always checked by
    // the caller first and may never populate this pool. Each consumer still parses its own UAsset.
    internal string? NativeAnimationParent(string package, Func<string?> extract)
        => NativeSourceAsync(package, () => Task.FromResult(extract())).GetAwaiter().GetResult();

    internal bool HasNativeSource(string package) => nativeSources.TryGetValue(package, out var file) && file is not null;
    internal string VoiceExtractionDirectory(string package) => Path.Combine(root, "Voice", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package))));

    internal async Task<string?> NativeSourceAsync(string package, Func<Task<string?>> extract)
    {
        if (!HeldItemService.ValidPackage(package) || package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            return await extract().ConfigureAwait(false);
        if (nativeSources.TryGetValue(package, out var saved)) { nativeReuses++; return saved; }
        nativeReads++;
        var source = await extract().ConfigureAwait(false);
        if (source is null) { nativeSources.Add(package, null); return null; }
        var size = Extensions.Sum(ext => File.Exists(Path.ChangeExtension(source, ext)) ? new FileInfo(Path.ChangeExtension(source, ext)).Length : 0L);
        if (bytes + size > MaximumBytes) return source;
        var target = Path.Combine(root, "NativeSources", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package))), Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var ext in Extensions)
            if (File.Exists(Path.ChangeExtension(source, ext))) File.Copy(Path.ChangeExtension(source, ext), Path.ChangeExtension(target, ext));
        bytes += size;
        nativeSources.Add(package, target);
        return target;
    }

    internal bool TryStage(WeaponModelRecipe recipe, string sourceRoot, string map, string content, string package)
    {
        if (sourceRoot != extracted || map != mappings || !meshes.TryGetValue(Key(recipe), out var sourcePackage)) return false;
        var source = PathFor(root, sourcePackage);
        var target = PathFor(content, package);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var ext in Extensions)
        {
            if (File.Exists(source + ext)) File.Copy(source + ext, target + ext, overwrite: true);
            else if (File.Exists(target + ext)) File.Delete(target + ext);
        }
        // Independent files and UAsset objects: subsequent per-suit edits cannot alter the shared source.
        var flags = CustomSerializationFlags.SkipPreloadDependencyLoading;
        var asset = new UAsset(target + ".uasset", EngineVersion.VER_UE5_6, MappingsCache.Load(map), flags);
        var names = asset.GetNameMapIndexList();
        var sourceName = UnrealPathUtil.AssetName(sourcePackage);
        var targetName = UnrealPathUtil.AssetName(package);
        var neutral = GeometryRecipe(recipe);
        var materialNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < recipe.Materials.Count; i++)
        {
            materialNames[neutral.Materials[i].MaterialPath] = recipe.Materials[i].MaterialPath;
            materialNames[UnrealPathUtil.AssetName(neutral.Materials[i].MaterialPath)] = UnrealPathUtil.AssetName(recipe.Materials[i].MaterialPath);
        }
        asset.FolderName = new FString(package);
        for (var i = 0; i < names.Count; i++)
        {
            var old = names[i].ToString();
            var renamed = materialNames.TryGetValue(old, out var binding) ? binding :
                old.Replace(sourcePackage, package, StringComparison.Ordinal).Replace(sourceName, targetName, StringComparison.Ordinal);
            if (old != renamed) asset.SetNameReference(i, new FString(renamed));
        }
        asset.Write(target + ".uasset");
        var verified = new UAsset(target + ".uasset", EngineVersion.VER_UE5_6, MappingsCache.Load(map), flags);
        if (verified.FolderName.ToString() != package ||
            !verified.Exports.Any(e => e.GetExportClassType()?.ToString() == "StaticMesh" && e.ObjectName.ToString() == targetName) ||
            verified.GetNameMapIndexList().Any(n => n.ToString().StartsWith("/Game/Mods/BuildShared/", StringComparison.Ordinal)))
            throw new InvalidDataException("Shared mesh identity did not persist: " + package);
        reused++;
        return true;
    }

    public void Dispose()
    {
        Active.Value = previous;
        voiceProvider?.Dispose();
        voiceExports.Clear();
        log($"Shared voice routing: {voiceExportReuses} repeated native export decodes avoided; private routing and verification remain independent.");
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException ex) { log("Could not remove temporary shared-build files: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { log("Could not remove temporary shared-build files: " + ex.Message); }
        log($"Shared-asset result: {reused} mesh placements from {meshes.Count} shared cook(s). No persistent cache was created by this optimization.");
        log($"Shared native sources: {nativeReads} extraction request(s), {nativeReuses} repeated request(s) reused; total temporary shared data {bytes / 1048576d:F1} MiB.");
        log($"Shared native reads: {sourceReads} source package(s), {sourceReuses} repeated reads avoided across all asset classes; parsed and edited objects remain independent.");
        nativeBytes.Clear();
        voiceCatalogs.Clear();
        log($"Shared native voice discovery: {voiceCatalogReuses} repeated donor scan(s) avoided; each suit's assignments are still validated.");
    }
}
