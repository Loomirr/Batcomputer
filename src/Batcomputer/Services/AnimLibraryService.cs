using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// The cooked-animation LIBRARY. Registers, inspects, imports, and persists
/// <see cref="AnimLibraryEntry"/> records so the user can pick a named animation when building
/// an override instead of hand-typing a /Game path. The tool never cooks anims - it only
/// catalogues assets the modder already cooked in Unreal. Pure data/service layer with no UI,
/// so it survives the incoming UI redesign.
/// </summary>
public sealed class AnimLibraryService
{
    private readonly string _projectRoot;
    private readonly string? _mappingsPath;

    public AnimLibraryService(string projectRoot, string? mappingsPath = null)
    {
        _projectRoot = projectRoot;
        _mappingsPath = mappingsPath;
    }

    public string LibraryRoot => Path.Combine(AppSettings.GeneratedRootFor(_projectRoot), "AnimationLibrary");
    public string IndexPath => Path.Combine(LibraryRoot, "library.json");
    public string CacheRoot => Path.Combine(LibraryRoot, "Cache");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AnimLibrary Load()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                return JsonSerializer.Deserialize<AnimLibrary>(File.ReadAllText(IndexPath), JsonOptions)
                       ?? new AnimLibrary();
            }
        }
        catch { /* corrupt index → start fresh, don't lose the app */ }
        return new AnimLibrary();
    }

    public void Save(AnimLibrary library)
    {
        Directory.CreateDirectory(LibraryRoot);
        AtomicFileUtil.WriteAllText(IndexPath, JsonSerializer.Serialize(library, JsonOptions));
    }

    /// <summary>
    /// Registers an already-cooked animation by name + /Game package path (the "Create animation"
    /// flow). No files are copied - the asset lives in the modder's own pak (external) or the base
    /// game. Inspection is attempted best-effort if the bytes are resolvable on disk.
    /// </summary>
    public AnimLibraryEntry RegisterByPackagePath(AnimLibrary library, string name, string packagePath,
        string sourceMode = "external", string category = "")
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);

        // Same package path already catalogued → bump version + refresh instead of duplicating.
        var existing = library.Entries.FirstOrDefault(e =>
            e.PackagePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        var entry = existing ?? new AnimLibraryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            AddedUtc = now
        };
        if (existing is not null)
        {
            entry.Version++;
        }

        entry.Name = string.IsNullOrWhiteSpace(name) ? LeafOf(normalized) : name.Trim();
        entry.Category = category ?? "";
        entry.SourceMode = string.IsNullOrWhiteSpace(sourceMode) ? "external" : sourceMode;
        entry.PackagePath = normalized;
        entry.UpdatedUtc = now;

        var resolved = ResolveOnDisk(normalized);
        if (resolved is not null)
        {
            Inspect(entry, resolved);
        }
        else
        {
            entry.Inspected = false;
            entry.Notes = "Asset bytes not on disk (lives in the modder's pak / not extracted) — inspection skipped.";
        }

        if (existing is null)
        {
            library.Entries.Add(entry);
        }
        Save(library);
        return entry;
    }

    /// <summary>
    /// Imports a cooked .uasset (with any .uexp/.ubulk sidecars) into the managed library cache
    /// and inspects it (item 3). Use for anims the user wants the library to own a copy of.
    /// </summary>
    public AnimLibraryEntry ImportCookedFile(AnimLibrary library, string name, string uassetPath,
        string packagePath, string sourceMode = "preserve-path", string category = "")
    {
        if (!File.Exists(uassetPath))
        {
            throw new FileNotFoundException($"Cooked animation .uasset not found: {uassetPath}");
        }

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var entry = new AnimLibraryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(uassetPath) : name.Trim(),
            Category = category ?? "",
            SourceMode = string.IsNullOrWhiteSpace(sourceMode) ? "preserve-path" : sourceMode,
            PackagePath = UnrealPathUtil.NormalizePackagePath(packagePath),
            AddedUtc = now,
            UpdatedUtc = now
        };

        var destDir = Path.Combine(CacheRoot, entry.Id);
        Directory.CreateDirectory(destDir);
        var baseNoExt = Path.Combine(Path.GetDirectoryName(uassetPath)!, Path.GetFileNameWithoutExtension(uassetPath));
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var src = baseNoExt + ext;
            if (File.Exists(src))
            {
                var destName = Path.GetFileName(src);
                File.Copy(src, Path.Combine(destDir, destName), overwrite: true);
                entry.CachedFiles.Add(Path.Combine("Cache", entry.Id, destName).Replace('\\', '/'));
            }
        }

        var cachedUasset = Path.Combine(destDir, Path.GetFileName(uassetPath));
        Inspect(entry, cachedUasset);

        library.Entries.Add(entry);
        Save(library);
        return entry;
    }

    public bool Remove(AnimLibrary library, string id)
    {
        var entry = library.Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }
        library.Entries.Remove(entry);
        try
        {
            var dir = Path.Combine(CacheRoot, entry.Id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* leave orphaned cache rather than fail the removal */ }
        Save(library);
        return true;
    }

    /// <summary>
    /// Reads an anim asset's class / skeleton / root-motion / additive mode / dependencies
    /// (item 4). Best-effort: any read failure leaves Inspected=false with a note.
    /// </summary>
    public void Inspect(AnimLibraryEntry entry, string uassetPath)
    {
        try
        {
            var mappings = string.IsNullOrWhiteSpace(_mappingsPath) || !File.Exists(_mappingsPath)
                ? null
                : MappingsCache.Load(_mappingsPath);

            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            // Main export = the one whose name matches the package leaf, else the first NormalExport.
            var leaf = LeafOf(entry.PackagePath.Length > 0 ? entry.PackagePath : Path.GetFileNameWithoutExtension(uassetPath));
            var main = asset.Exports.OfType<NormalExport>()
                           .FirstOrDefault(e => e.ObjectName.ToString().Equals(leaf, StringComparison.OrdinalIgnoreCase))
                       ?? asset.Exports.OfType<NormalExport>().FirstOrDefault();

            if (main is not null)
            {
                entry.AssetClass = main.GetExportClassType().Value?.ToString() ?? "";

                var skeleton = main.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals("Skeleton", StringComparison.OrdinalIgnoreCase));
                if (skeleton is not null && !skeleton.Value.IsNull() && skeleton.Value.IsImport())
                {
                    entry.Skeleton = skeleton.Value.ToImport(asset).ObjectName.ToString();
                }

                entry.RootMotion = main.Data.OfType<BoolPropertyData>()
                    .Any(p => p.Name.ToString().Equals("bEnableRootMotion", StringComparison.OrdinalIgnoreCase) && p.Value);

                var additive = main.Data.OfType<EnumPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals("AdditiveAnimType", StringComparison.OrdinalIgnoreCase));
                if (additive is not null)
                {
                    entry.AdditiveMode = additive.Value.Value?.ToString() ?? "";
                }
            }

            // Dependencies: distinct game or installed Game Feature object imports the asset references.
            entry.Dependencies = asset.Imports
                .Select(i => i.ObjectName.ToString())
                .Where(ExtractedPackagePathService.IsContentPackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            entry.Inspected = true;
            if (entry.Notes.StartsWith("Asset bytes not on disk", StringComparison.OrdinalIgnoreCase))
            {
                entry.Notes = "";
            }
        }
        catch (Exception ex)
        {
            entry.Inspected = false;
            entry.Notes = $"Inspection failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Item 7: the library entries a suit REFERENCES that the tool must SHIP inside the pak -
    /// entries whose PackagePath is referenced by the project AND that own cached cooked files
    /// (preserve-path / proven-clone / imported). <c>external</c> anims live in the modder's own
    /// pak and <c>base-game</c> anims are already in the game, so neither is shipped by us.
    /// </summary>
    public IReadOnlyList<AnimLibraryEntry> ReferencedShippable(AnimLibrary library, IEnumerable<string> referencedPackagePaths)
    {
        var refs = referencedPackagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return library.Entries
            .Where(e => e.CachedFiles.Count > 0
                        && !e.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase)
                        && !e.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase)
                        && refs.Contains(UnrealPathUtil.NormalizePackagePath(e.PackagePath)))
            .ToList();
    }

    /// <summary>
    /// Copies an entry's cached cooked files into <paramref name="contentRoot"/> at the entry's
    /// /Game package path so they ship in the pak. Returns the number of files staged.
    /// </summary>
    public int StageInto(AnimLibraryEntry entry, string contentRoot)
    {
        var norm = UnrealPathUtil.NormalizePackagePath(entry.PackagePath);
        if (!norm.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        var relDir = Path.GetDirectoryName(norm["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var destDir = Path.Combine(contentRoot, relDir);
        Directory.CreateDirectory(destDir);

        var staged = 0;
        foreach (var cachedRel in entry.CachedFiles)
        {
            var src = Path.Combine(LibraryRoot, cachedRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src))
            {
                continue;
            }
            File.Copy(src, Path.Combine(destDir, Path.GetFileName(src)), overwrite: true);
            staged++;
        }
        return staged;
    }

    /// <summary>Outcome of importing a whole unpacked anim pak folder.</summary>
    public sealed class PakImportReport
    {
        public List<AnimLibraryEntry> Imported { get; } = new();
        public List<string> RejectedNonAnim { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Imports every AnimSequence found under a retoc <c>to-legacy</c> output folder into the
    /// library (sourceMode <c>preserve-path</c>, so they SHIP when a suit references them).
    /// Anything that is NOT an AnimSequence is rejected - this is what guarantees a suit pak only
    /// ever gains custom ANIMATIONS, never a stray mesh/material/BP. Best-effort per asset.
    /// </summary>
    public PakImportReport ImportAnimationPakFolder(AnimLibrary library, string unpackedContentRoot)
    {
        var report = new PakImportReport();
        if (!Directory.Exists(unpackedContentRoot))
        {
            report.Warnings.Add($"Unpacked folder not found: {unpackedContentRoot}");
            return report;
        }

        foreach (var uasset in Directory.EnumerateFiles(unpackedContentRoot, "*.uasset", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var pkg = ToGamePackagePathFromDisk(uasset);
            if (pkg is null)
            {
                continue; // not under a /Content/ root — skip (script objects, stray files)
            }

            // Inspect first (no copy) so a non-anim is rejected before we cache anything.
            var probe = new AnimLibraryEntry { PackagePath = pkg };
            Inspect(probe, uasset);
            var leaf = LeafOf(pkg);
            if (!IsAnimSequenceClass(probe.AssetClass))
            {
                report.RejectedNonAnim.Add($"{leaf} ({(string.IsNullOrWhiteSpace(probe.AssetClass) ? "unreadable" : probe.AssetClass)})");
                continue;
            }

            var entry = ImportCookedFile(library, leaf, uasset, pkg, sourceMode: "preserve-path", category: "Imported");
            if (!string.IsNullOrWhiteSpace(probe.Skeleton) &&
                probe.Skeleton.IndexOf("SKEL_LEGOfig", StringComparison.OrdinalIgnoreCase) < 0)
            {
                report.Warnings.Add($"{entry.Name}: skeleton is '{probe.Skeleton}', not SKEL_LEGOfig — it may not retarget onto minifig bodies in-game.");
            }
            report.Imported.Add(entry);
        }
        return report;
    }

    /// <summary>True if a cooked export class looks like a UAnimSequence (accepts the raw name or /Script path).</summary>
    private static bool IsAnimSequenceClass(string? assetClass) =>
        !string.IsNullOrWhiteSpace(assetClass) &&
        assetClass.IndexOf("AnimSequence", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Converts a retoc <c>to-legacy</c> on-disk path (…/&lt;Mount&gt;/Content/Foo/Bar.uasset) to its
    /// <c>/Game/Foo/Bar</c> package path. The suit re-pack mounts at /Game, so this is the path the
    /// override points at and the path StageInto writes to.
    /// </summary>
    private static string? ToGamePackagePathFromDisk(string uassetPath)
    {
        var norm = uassetPath.Replace('\\', '/');
        const string marker = "/Content/";
        var idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rel = norm[(idx + marker.Length)..];
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[..^".uasset".Length];
        }
        return "/Game/" + rel;
    }

    /// <summary>Resolves a game, Game Feature, or exported /Game package to a cooked .uasset on disk.</summary>
    private static string? ResolveOnDisk(string packagePath)
    {
        var norm = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!ExtractedPackagePathService.IsContentPackagePath(norm))
        {
            return null;
        }

        var extracted = ExtractedPackagePathService.ResolvePackageUasset(
            AppSettings.Current.EffectiveExtractedContentRoot(),
            norm);
        if (!string.IsNullOrWhiteSpace(extracted) && File.Exists(extracted))
        {
            return extracted;
        }

        if (!norm.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var exportRoot = AppSettings.Current.EffectiveExportContentRoot();
        var relative = norm["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var exported = string.IsNullOrWhiteSpace(exportRoot)
            ? ""
            : Path.Combine(exportRoot, relative) + ".uasset";
        return File.Exists(exported) ? exported : null;
    }

    private static string LeafOf(string packagePath)
    {
        var norm = packagePath.Contains('.') ? packagePath[..packagePath.IndexOf('.')] : packagePath;
        var slash = norm.LastIndexOf('/');
        return slash >= 0 ? norm[(slash + 1)..] : norm;
    }
}
