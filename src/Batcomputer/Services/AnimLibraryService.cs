using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal sealed record AnimationImportSupportNode(
    string PackagePath,
    string AssetClass,
    IReadOnlyList<string> Dependencies,
    bool IsProvidedByGame = false);

/// <summary>
/// The workspace-wide cooked-animation library. Registers, inspects, imports, and persists
/// <see cref="AnimLibraryEntry"/> records so the user can pick a named animation when building
/// an override instead of hand-typing a /Game path. The tool never cooks anims - it only
/// catalogues assets the modder already cooked in Unreal. The library belongs to the workspace,
/// never to an individual suit; suits reference only the packages they use. Pure data/service
/// layer with no UI, so it survives the incoming UI redesign.
/// </summary>
public sealed class AnimLibraryService
{
    private const string NativeLegofigSkeletonPackage = "/Game/Characters/LEGOfig/SKEL_LEGOfig";
    private static readonly string[] CookedAssetExtensions = [".uasset", ".uexp", ".ubulk", ".m.ubulk", ".uptnl"];

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
                var library = JsonSerializer.Deserialize<AnimLibrary>(File.ReadAllText(IndexPath), JsonOptions)
                              ?? new AnimLibrary();
                NormalizeLibrary(library);
                return library;
            }
        }
        catch { /* corrupt index → start fresh, don't lose the app */ }
        return new AnimLibrary();
    }

    public void Save(AnimLibrary library)
    {
        NormalizeLibrary(library);
        library.SchemaVersion = Math.Max(library.SchemaVersion, 2);
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
        NormalizeLibrary(library);
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);

        // Same package path already catalogued → deterministically reuse one stable entry instead
        // of making stage order decide which duplicate wins.
        var existing = FindCanonicalEntry(library, normalized);
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

        AssessEntryHealth(entry, legacyWhenUngrouped: true);

        if (existing is null)
        {
            library.Entries.Add(entry);
        }
        RemoveDuplicateEntries(library, entry);
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

        NormalizeLibrary(library);
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        var existing = FindCanonicalEntry(library, normalized);
        var originalEntries = library.Entries.ToList();
        var candidateCacheCreated = false;
        var replacementCommitted = false;
        var entry = new AnimLibraryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            AddedUtc = existing?.AddedUtc ?? now
        };

        entry.Version = existing is null ? 1 : Math.Max(1, existing.Version + 1);
        entry.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(uassetPath) : name.Trim();
        entry.Category = category ?? "";
        entry.SourceMode = string.IsNullOrWhiteSpace(sourceMode) ? "preserve-path" : sourceMode;
        entry.PackagePath = normalized;
        entry.UpdatedUtc = now;
        ResetInspection(entry);
        Inspect(entry, uassetPath);

        var issues = InspectionIssues(entry, requireSupportedAnimation: true);
        if (ResolveInGameData(normalized) is not null)
        {
            issues.Add(
                $"The imported animation path already exists in the configured game data: {normalized}. " +
                "Use a unique /Game/Mods/... path.");
        }
        if (issues.Count == 0)
        {
            try
            {
                CacheEntryPackages(entry, uassetPath, []);
                candidateCacheCreated = true;
                entry.HealthStatus = "legacy";
                AssessEntryHealth(entry, legacyWhenUngrouped: true);
                if (entry.IsAvailable)
                {
                    ReplaceLibraryEntry(library, existing, entry);
                    replacementCommitted = true;
                }
                else
                {
                    DeleteCacheBestEffort(entry.Id);
                    candidateCacheCreated = false;
                    if (existing is null || !existing.IsAvailable)
                    {
                        ReplaceLibraryEntry(library, existing, entry);
                        replacementCommitted = true;
                    }
                }
            }
            catch (Exception ex)
            {
                DeleteCacheBestEffort(entry.Id);
                Quarantine(entry, [$"Cache failed: {ex.Message}"]);
                if (existing is null || !existing.IsAvailable)
                {
                    ReplaceLibraryEntry(library, existing, entry);
                    replacementCommitted = true;
                }
            }
        }
        else
        {
            Quarantine(entry, issues);
            if (existing is null || !existing.IsAvailable)
            {
                ReplaceLibraryEntry(library, existing, entry);
                replacementCommitted = true;
            }
        }

        try
        {
            Save(library);
        }
        catch
        {
            // The on-disk index still references the previous entry and cache. Restore the caller's
            // in-memory view and discard only the uncommitted candidate, leaving the healthy copy
            // fully usable even when library.json cannot be replaced.
            library.Entries = originalEntries;
            if (candidateCacheCreated)
            {
                DeleteCacheBestEffort(entry.Id);
            }
            throw;
        }

        if (replacementCommitted && existing is not null &&
            !existing.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) &&
            !library.Entries.Any(active => active.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)))
        {
            DeleteCacheBestEffort(existing.Id);
        }
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
        ResetInspection(entry);
        try
        {
            var mappings = string.IsNullOrWhiteSpace(_mappingsPath) || !File.Exists(_mappingsPath)
                ? null
                : MappingsCache.Load(_mappingsPath);

            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            // Main export = the one whose name matches the package leaf, else the first export.
            // SkeletalMeshExport and several other cooked support types are not NormalExport;
            // their import table is still readable and is essential for dependency closure.
            var leaf = LeafOf(entry.PackagePath.Length > 0 ? entry.PackagePath : Path.GetFileNameWithoutExtension(uassetPath));
            var primary = asset.Exports
                              .FirstOrDefault(e => e.ObjectName.ToString().Equals(leaf, StringComparison.OrdinalIgnoreCase))
                          ?? asset.Exports.FirstOrDefault();

            if (primary is null)
            {
                entry.Notes = "Inspection failed: package contains no export.";
                return;
            }

            entry.AssetClass = primary.GetExportClassType().Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(entry.AssetClass) && primary.ClassIndex.IsImport())
            {
                entry.AssetClass = primary.ClassIndex.ToImport(asset).ObjectName.ToString();
            }

            if (primary is NormalExport main)
            {
                var skeleton = main.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals("Skeleton", StringComparison.OrdinalIgnoreCase));
                if (skeleton is not null && !skeleton.Value.IsNull() && skeleton.Value.IsImport())
                {
                    var skeletonImport = skeleton.Value.ToImport(asset);
                    var skeletonPackage = ResolveImportPackage(asset, skeletonImport.OuterIndex);
                    entry.Skeleton = string.IsNullOrWhiteSpace(skeletonPackage)
                        ? skeletonImport.ObjectName.ToString()
                        : UnrealPathUtil.NormalizePackagePath(skeletonPackage);
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
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            entry.UnresolvedImports = asset.Imports
                .SelectMany(i => new[]
                {
                    i.ObjectName.ToString(),
                    i.ClassName.ToString(),
                    i.ClassPackage.ToString()
                })
                .Where(ContainsUnknownMarker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ContainsUnknownMarker(entry.AssetClass))
            {
                entry.UnresolvedImports.Add(entry.AssetClass);
            }
            if (ContainsUnknownMarker(entry.Skeleton))
            {
                entry.UnresolvedImports.Add(entry.Skeleton);
            }
            entry.UnresolvedImports = entry.UnresolvedImports
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            entry.Inspected = true;
            entry.Notes = "";
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
        NormalizeLibrary(library);
        var refs = referencedPackagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return library.Entries
            .Where(e => e.IsAvailable
                        && e.CachedFiles.Count > 0
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
        NormalizeEntry(entry);
        AssessEntryHealth(entry, legacyWhenUngrouped: true);
        if (!entry.IsAvailable)
        {
            return 0;
        }

        var staged = StagePackageFiles(entry.PackagePath, entry.CachedFiles, contentRoot);
        foreach (var support in entry.SupportPackages
                     .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            if (support.PackagePath.Equals(entry.PackagePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            staged += StagePackageFiles(support.PackagePath, support.CachedFiles, contentRoot);
        }
        return staged;
    }

    /// <summary>
    /// Checks the complete package set before staging multiple managed animations. Two animations
    /// may share a custom rig package only when every cooked sidecar is byte-identical; otherwise
    /// stage order would silently decide which incompatible package ships in the suit.
    /// </summary>
    public IReadOnlyList<string> ValidateStagingSet(IEnumerable<AnimLibraryEntry> entries)
    {
        var owned = entries
            .SelectMany(entry => OwnedPackages(entry).Select(package => new
            {
                Entry = entry.Name,
                package.PackagePath,
                package.CachedFiles,
                Signature = PackageSignature(package.CachedFiles)
            }))
            .GroupBy(package => package.PackagePath, StringComparer.OrdinalIgnoreCase);

        var issues = new List<string>();
        foreach (var group in owned)
        {
            var variants = group
                .GroupBy(package => package.Signature, StringComparer.Ordinal)
                .ToList();
            if (variants.Count <= 1)
            {
                continue;
            }

            var names = group.Select(package => package.Entry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            issues.Add(
                $"Animations {string.Join(", ", names.Select(name => $"'{name}'"))} provide different cooked files for shared package '{group.Key}'. " +
                "Import them with one compatible shared rig package before building.");
        }
        return issues;
    }

    private sealed record OwnedPackage(
        string PackagePath,
        IReadOnlyList<string> CachedFiles);

    private static IEnumerable<OwnedPackage> OwnedPackages(AnimLibraryEntry entry)
    {
        yield return new OwnedPackage(
            UnrealPathUtil.NormalizePackagePath(entry.PackagePath),
            entry.CachedFiles);
        foreach (var support in entry.SupportPackages)
        {
            yield return new OwnedPackage(
                UnrealPathUtil.NormalizePackagePath(support.PackagePath),
                support.CachedFiles);
        }
    }

    private string PackageSignature(IReadOnlyList<string> cachedFiles)
    {
        var pieces = new List<string>();
        foreach (var cached in cachedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveLibraryFile(cached, out var file))
            {
                pieces.Add($"missing:{cached}");
                continue;
            }
            using var stream = File.OpenRead(file);
            pieces.Add($"{Path.GetExtension(file).ToLowerInvariant()}:{Convert.ToHexString(SHA256.HashData(stream))}");
        }
        return string.Join("|", pieces);
    }

    /// <summary>Outcome of importing a whole unpacked anim pak folder.</summary>
    public sealed class PakImportReport
    {
        public List<AnimLibraryEntry> Imported { get; } = new();
        public List<AnimLibraryEntry> Quarantined { get; } = new();
        public List<string> RejectedNonAnim { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Imports every AnimSequence and AnimMontage found under a retoc <c>to-legacy</c> output folder together with
    /// its connected source-container support packages. This is intentionally broader than a
    /// directed dependency walk: UE Skeletons do not import their SkeletalMesh, while the mesh is
    /// what points back to the Skeleton and PhysicsAsset. The connected non-sequence support set
    /// preserves that relationship without pulling unrelated sibling primary animations into an entry.
    /// Unreadable packages and UnknownPackage/UnknownExport references are quarantined fail-closed.
    /// </summary>
    public PakImportReport ImportAnimationPakFolder(
        AnimLibrary library,
        string unpackedContentRoot,
        IReadOnlyCollection<string>? allowedPackagePaths = null)
    {
        var report = new PakImportReport();
        if (!Directory.Exists(unpackedContentRoot))
        {
            report.Warnings.Add($"Unpacked folder not found: {unpackedContentRoot}");
            return report;
        }

        NormalizeLibrary(library);
        var originalEntries = library.Entries.ToList();
        var candidateCacheIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retiredCacheIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = allowedPackagePaths is null
            ? null
            : allowedPackagePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(UnrealPathUtil.NormalizePackagePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var probes = new List<SourcePackageProbe>();
        foreach (var uasset in Directory.EnumerateFiles(unpackedContentRoot, "*.uasset", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var pkg = ToGamePackagePathFromDisk(uasset);
            if (pkg is null)
            {
                continue; // not under a /Content/ root — skip (script objects, stray files)
            }
            if (allowed is not null && !allowed.Contains(UnrealPathUtil.NormalizePackagePath(pkg)))
            {
                continue; // another mounted base/DLC container matched a broad retoc filter
            }

            var probe = new AnimLibraryEntry { PackagePath = pkg };
            Inspect(probe, uasset);
            probes.Add(new SourcePackageProbe(uasset, pkg, probe));
        }

        var primaryAnimations = probes
            .Where(p => IsSupportedAnimationClass(p.Metadata.AssetClass))
            .GroupBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(p => p.UassetPath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var usedSupportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var animation in primaryAnimations)
        {
            var support = BuildSupportSet(animation, probes);
            if (string.IsNullOrWhiteSpace(animation.Metadata.Skeleton))
            {
                var skeletonSupport = support.FirstOrDefault(package =>
                    IsSkeletonClass(package.Metadata.AssetClass));
                if (skeletonSupport is not null)
                {
                    // UAssetAPI can read an animation without mappings but may not deserialize its
                    // Skeleton property. The connected cooked Skeleton package remains an
                    // exact identity, so retain it for diagnostics without guessing or retargeting.
                    animation.Metadata.Skeleton = UnrealPathUtil.NormalizePackagePath(skeletonSupport.PackagePath);
                }
            }
            foreach (var package in support)
            {
                usedSupportPaths.Add(package.PackagePath);
            }

            var expectedClass = NormalizeSupportedAnimationClass(animation.Metadata.AssetClass);
            var issues = InspectionIssues(animation.Metadata, expectedAnimationClass: expectedClass);
            if (ResolveInGameData(animation.PackagePath) is not null)
            {
                issues.Add(
                    $"The imported animation path already exists in the configured game data: {animation.PackagePath}. " +
                    "Cook custom animations under a unique /Game/Mods/... package path so the suit cannot overwrite a base-game asset.");
            }
            foreach (var package in support)
            {
                issues.AddRange(InspectionIssues(package.Metadata)
                    .Select(issue => $"{package.PackagePath}: {issue}"));
            }
            issues.AddRange(MissingDependencyIssues(animation, support, probes));
            issues = issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var existing = FindCanonicalEntry(library, animation.PackagePath);
            AnimLibraryEntry entry;
            if (issues.Count > 0)
            {
                entry = CreateEntryFromProbe(animation, existing);
                Quarantine(entry, issues);
                if (existing is null || !existing.IsAvailable)
                {
                    ReplaceLibraryEntry(library, existing, entry);
                    if (existing is not null)
                    {
                        retiredCacheIds.Add(existing.Id);
                    }
                }
                else
                {
                    report.Warnings.Add(
                        $"{entry.Name}: rejected re-import; the previous healthy library copy was kept unchanged.");
                }
                report.Quarantined.Add(entry);
                report.Warnings.Add($"{entry.Name}: quarantined — {string.Join("; ", issues)}");
                continue;
            }

            entry = CreateEntryFromProbe(animation, existing);
            try
            {
                CacheEntryPackages(entry, animation.UassetPath, support);
                candidateCacheIds.Add(entry.Id);
                entry.HealthStatus = "healthy";
                AssessEntryHealth(entry, legacyWhenUngrouped: false);
                if (!entry.IsAvailable)
                {
                    DeleteCacheBestEffort(entry.Id);
                    candidateCacheIds.Remove(entry.Id);
                    report.Quarantined.Add(entry);
                    report.Warnings.Add($"{entry.Name}: quarantined — {string.Join("; ", entry.HealthIssues)}");
                    continue;
                }

                ReplaceLibraryEntry(library, existing, entry);
                if (existing is not null)
                {
                    retiredCacheIds.Add(existing.Id);
                }
            }
            catch (Exception ex)
            {
                DeleteCacheBestEffort(entry.Id);
                candidateCacheIds.Remove(entry.Id);
                Quarantine(entry, [$"Cache failed: {ex.Message}"]);
                report.Quarantined.Add(entry);
                report.Warnings.Add($"{entry.Name}: quarantined — cache failed: {ex.Message}");
                if (existing is null || !existing.IsAvailable)
                {
                    ReplaceLibraryEntry(library, existing, entry);
                    if (existing is not null)
                    {
                        retiredCacheIds.Add(existing.Id);
                    }
                }
                else
                {
                    report.Warnings.Add(
                        $"{entry.Name}: failed re-import; the previous healthy library copy was kept unchanged.");
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.Skeleton) &&
                !entry.Skeleton.Equals(NativeLegofigSkeletonPackage, StringComparison.OrdinalIgnoreCase))
            {
                report.Warnings.Add(
                    $"{entry.Name}: packaged custom rig '{entry.Skeleton}' with its connected support assets. " +
                    "Batcomputer kept the authored rig intact instead of silently substituting the native minifig skeleton.");
            }
            report.Imported.Add(entry);
        }

        foreach (var package in probes
                     .Where(p => !IsSupportedAnimationClass(p.Metadata.AssetClass) && !usedSupportPaths.Contains(p.PackagePath))
                     .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            report.RejectedNonAnim.Add($"{LeafOf(package.PackagePath)} ({(string.IsNullOrWhiteSpace(package.Metadata.AssetClass) ? "unreadable" : package.Metadata.AssetClass)})");
        }

        if (primaryAnimations.Count == 0 && probes.Count > 0)
        {
            report.Warnings.Add("No readable AnimSequence or AnimMontage package was found in the converted source container.");
        }

        try
        {
            Save(library);
        }
        catch
        {
            library.Entries = originalEntries;
            foreach (var cacheId in candidateCacheIds)
            {
                DeleteCacheBestEffort(cacheId);
            }
            throw;
        }

        // Only retire old bytes after the atomically-written index points at every new cache ID.
        // If saving failed above, the old index and all old cache directories remain untouched.
        foreach (var cacheId in retiredCacheIds)
        {
            if (!library.Entries.Any(entry => entry.Id.Equals(cacheId, StringComparison.OrdinalIgnoreCase)))
            {
                DeleteCacheBestEffort(cacheId);
            }
        }
        return report;
    }

    private sealed record SourcePackageProbe(
        string UassetPath,
        string PackagePath,
        AnimLibraryEntry Metadata);

    private static List<SourcePackageProbe> BuildSupportSet(
        SourcePackageProbe animation,
        IReadOnlyList<SourcePackageProbe> sourcePackages)
    {
        var byPath = sourcePackages
            .GroupBy(p => UnrealPathUtil.NormalizePackagePath(p.PackagePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(p => p.UassetPath, StringComparer.OrdinalIgnoreCase).First(),
                StringComparer.OrdinalIgnoreCase);
        var included = SelectSupportPackagePaths(
            animation.PackagePath,
            byPath.Values.Select(package => new AnimationImportSupportNode(
                    package.PackagePath,
                    package.Metadata.AssetClass,
                    package.Metadata.Dependencies,
                    ResolveInGameData(package.PackagePath) is not null))
                .ToList());
        return included
            .Where(byPath.ContainsKey)
            .Select(path => byPath[path])
            .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Selects the connected support component for one primary animation. Directed dependencies
    /// are always retained (so a montage can bring along the sequences it actually uses), while
    /// the reverse walk deliberately ignores other animation primaries. That keeps a shared rig
    /// from dragging every sibling sequence or montage in the source container into each entry.
    /// </summary>
    internal static IReadOnlySet<string> SelectSupportPackagePaths(
        string primaryPackagePath,
        IReadOnlyList<AnimationImportSupportNode> sourcePackages)
    {
        var byPath = sourcePackages
            .Where(package => !string.IsNullOrWhiteSpace(package.PackagePath))
            .GroupBy(
                package => UnrealPathUtil.NormalizePackagePath(package.PackagePath),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var primary = UnrealPathUtil.NormalizePackagePath(primaryPackagePath);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };

        var changed = true;
        while (changed)
        {
            changed = false;

            // Ordinary directed dependency closure. A montage's referenced AnimSequences are
            // genuine support, even though those same sequences are also useful library entries.
            foreach (var includedPath in included.ToList())
            {
                if (!byPath.TryGetValue(includedPath, out var package))
                {
                    continue;
                }
                foreach (var dependency in package.Dependencies.Select(UnrealPathUtil.NormalizePackagePath))
                {
                    if (byPath.TryGetValue(dependency, out var dependencyPackage) &&
                        !dependencyPackage.IsProvidedByGame &&
                        included.Add(dependency))
                    {
                        changed = true;
                    }
                }
            }

            // UE Skeletons do not import their SkeletalMesh, while that mesh commonly points back
            // to the Skeleton and PhysicsAsset. Pull reverse-connected support packages, but never
            // pull an unrelated primary animation merely because it references the same rig/clip.
            foreach (var candidate in byPath)
            {
                if (included.Contains(candidate.Key) ||
                    candidate.Value.IsProvidedByGame ||
                    IsSupportedAnimationClass(candidate.Value.AssetClass))
                {
                    continue;
                }
                if (candidate.Value.Dependencies
                    .Select(UnrealPathUtil.NormalizePackagePath)
                    .Any(included.Contains))
                {
                    included.Add(candidate.Key);
                    changed = true;
                }
            }
        }

        included.Remove(primary);
        return included;
    }

    private static IEnumerable<string> MissingDependencyIssues(
        SourcePackageProbe animation,
        IReadOnlyList<SourcePackageProbe> support,
        IReadOnlyList<SourcePackageProbe> sourcePackages)
    {
        var sourcePaths = sourcePackages
            .Select(package => UnrealPathUtil.NormalizePackagePath(package.PackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedPaths = support
            .Select(package => UnrealPathUtil.NormalizePackagePath(package.PackagePath))
            .Append(UnrealPathUtil.NormalizePackagePath(animation.PackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var package in support.Prepend(animation))
        {
            foreach (var dependency in package.Metadata.Dependencies
                         .Select(UnrealPathUtil.NormalizePackagePath)
                         .Where(path => path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)))
            {
                if (ResolveInGameData(dependency) is not null)
                {
                    continue;
                }
                if (sourcePaths.Contains(dependency) && !ownedPaths.Contains(dependency))
                {
                    yield return $"{package.PackagePath}: source dependency was not included: {dependency}";
                }
                else if (!sourcePaths.Contains(dependency) && ResolveOnDisk(dependency) is null)
                {
                    yield return $"{package.PackagePath}: dependency is absent from the source container and configured game data: {dependency}";
                }
            }
        }
    }

    private static AnimLibraryEntry CreateEntryFromProbe(
        SourcePackageProbe source,
        AnimLibraryEntry? existing)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var normalized = UnrealPathUtil.NormalizePackagePath(source.PackagePath);
        var entry = new AnimLibraryEntry
        {
            // Re-imports deliberately receive a new cache ID. The old ID is retired only after
            // library.json has been atomically updated, making replacement transactional.
            Id = Guid.NewGuid().ToString("N"),
            AddedUtc = existing?.AddedUtc ?? now
        };

        entry.Version = existing is null ? 1 : Math.Max(1, existing.Version + 1);
        entry.Name = LeafOf(normalized);
        entry.Category = "Imported";
        entry.SourceMode = "preserve-path";
        entry.PackagePath = normalized;
        entry.AssetClass = source.Metadata.AssetClass;
        entry.Skeleton = source.Metadata.Skeleton;
        entry.RootMotion = source.Metadata.RootMotion;
        entry.AdditiveMode = source.Metadata.AdditiveMode;
        entry.Dependencies = source.Metadata.Dependencies.ToList();
        entry.UnresolvedImports = source.Metadata.UnresolvedImports.ToList();
        entry.Inspected = source.Metadata.Inspected;
        entry.Notes = source.Metadata.Notes;
        entry.CachedFiles = new List<string>();
        entry.SupportPackages = new List<AnimLibraryCachedPackage>();
        entry.HealthStatus = "";
        entry.HealthIssues = new List<string>();
        entry.IsAvailable = true;
        entry.UpdatedUtc = now;

        return entry;
    }

    private static void ReplaceLibraryEntry(
        AnimLibrary library,
        AnimLibraryEntry? existing,
        AnimLibraryEntry replacement)
    {
        if (existing is not null)
        {
            library.Entries.Remove(existing);
        }
        library.Entries.RemoveAll(entry =>
            UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(UnrealPathUtil.NormalizePackagePath(replacement.PackagePath), StringComparison.OrdinalIgnoreCase));
        library.Entries.Add(replacement);
    }

    private void DeleteCacheBestEffort(string entryId)
    {
        try
        {
            var cache = Path.Combine(CacheRoot, entryId);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
        catch { /* a rejected import must not take down the app */ }
    }

    private void CacheEntryPackages(
        AnimLibraryEntry entry,
        string primaryUassetPath,
        IReadOnlyList<SourcePackageProbe> supportPackages)
    {
        Directory.CreateDirectory(CacheRoot);
        var finalDir = Path.Combine(CacheRoot, entry.Id);
        var incomingDir = Path.Combine(CacheRoot, $".{entry.Id}.incoming-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(CacheRoot, $".{entry.Id}.previous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(incomingDir);

        var primaryFiles = new List<string>();
        var cachedSupport = new List<AnimLibraryCachedPackage>();
        var movedOldCache = false;
        try
        {
            primaryFiles.AddRange(CopyCookedPackageFiles(
                primaryUassetPath,
                incomingDir,
                relativeDestinationDirectory: "",
                entry.Id));

            foreach (var support in supportPackages
                         .Where(p => !p.PackagePath.Equals(entry.PackagePath, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                var normalized = UnrealPathUtil.NormalizePackagePath(support.PackagePath);
                if (!TryGamePackageRelativePath(normalized, out var packageRelative))
                {
                    throw new InvalidOperationException($"Support package is not a safe /Game path: {normalized}");
                }

                var packageDirectory = Path.GetDirectoryName(packageRelative) ?? "";
                var cachedFiles = CopyCookedPackageFiles(
                    support.UassetPath,
                    incomingDir,
                    Path.Combine("Packages", packageDirectory),
                    entry.Id);
                cachedSupport.Add(new AnimLibraryCachedPackage
                {
                    PackagePath = normalized,
                    AssetClass = support.Metadata.AssetClass,
                    Dependencies = support.Metadata.Dependencies.ToList(),
                    UnresolvedImports = support.Metadata.UnresolvedImports.ToList(),
                    Inspected = support.Metadata.Inspected,
                    CachedFiles = cachedFiles,
                    Notes = support.Metadata.Notes
                });
            }

            if (Directory.Exists(finalDir))
            {
                Directory.Move(finalDir, backupDir);
                movedOldCache = true;
            }
            Directory.Move(incomingDir, finalDir);
            entry.CachedFiles = primaryFiles;
            entry.SupportPackages = cachedSupport;

            if (movedOldCache)
            {
                try { Directory.Delete(backupDir, recursive: true); }
                catch { /* harmless orphan; the index points only at the replacement */ }
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(incomingDir))
                {
                    Directory.Delete(incomingDir, recursive: true);
                }
                if (movedOldCache && !Directory.Exists(finalDir) && Directory.Exists(backupDir))
                {
                    Directory.Move(backupDir, finalDir);
                }
            }
            catch { /* preserve the original exception */ }
            throw;
        }
    }

    private static List<string> CopyCookedPackageFiles(
        string uassetPath,
        string incomingRoot,
        string relativeDestinationDirectory,
        string entryId)
    {
        var destinationDirectory = Path.Combine(incomingRoot, relativeDestinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var sourceBase = Path.Combine(
            Path.GetDirectoryName(uassetPath) ?? "",
            Path.GetFileNameWithoutExtension(uassetPath));
        var cached = new List<string>();
        foreach (var extension in CookedAssetExtensions)
        {
            var source = sourceBase + extension;
            if (!File.Exists(source))
            {
                continue;
            }
            var fileName = Path.GetFileName(source);
            File.Copy(source, Path.Combine(destinationDirectory, fileName), overwrite: true);
            cached.Add(Path.Combine("Cache", entryId, relativeDestinationDirectory, fileName).Replace('\\', '/'));
        }

        if (!cached.Any(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Cooked package has no .uasset: {uassetPath}");
        }
        return cached;
    }

    private int StagePackageFiles(string packagePath, IReadOnlyList<string> cachedFiles, string contentRoot)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!TryGamePackageRelativePath(normalized, out var packageRelative))
        {
            return 0;
        }

        var relativeDirectory = Path.GetDirectoryName(packageRelative) ?? "";
        var rootFull = Path.GetFullPath(contentRoot);
        var destinationDirectory = Path.GetFullPath(Path.Combine(rootFull, relativeDirectory));
        if (!IsWithinRoot(rootFull, destinationDirectory))
        {
            return 0;
        }
        Directory.CreateDirectory(destinationDirectory);

        var staged = 0;
        foreach (var cachedRelative in cachedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveLibraryFile(cachedRelative, out var source))
            {
                continue;
            }
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (File.Exists(destination) && !FilesAreIdentical(source, destination))
            {
                throw new InvalidOperationException(
                    $"Animation package collision at '{normalized}': '{destination}' already contains different cooked bytes. " +
                    "Batcomputer stopped instead of letting stage order choose a crash-prone asset.");
            }
            File.Copy(source, destination, overwrite: true);
            staged++;
        }
        return staged;
    }

    private static bool FilesAreIdentical(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
    }

    private void NormalizeLibrary(AnimLibrary library)
    {
        library.Entries ??= new List<AnimLibraryEntry>();
        foreach (var entry in library.Entries)
        {
            NormalizeEntry(entry);
            AssessEntryHealth(entry, legacyWhenUngrouped: true);
        }

        // Older versions allowed duplicate package records. Resolve them by availability, version,
        // update timestamp, then ID so the same library always chooses the same winner.
        library.Entries = library.Entries
            .GroupBy(
                entry => string.IsNullOrWhiteSpace(entry.PackagePath)
                    ? $"#id:{entry.Id}"
                    : UnrealPathUtil.NormalizePackagePath(entry.PackagePath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => OrderCanonical(group).First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        library.SchemaVersion = Math.Max(library.SchemaVersion, 2);
    }

    private static void NormalizeEntry(AnimLibraryEntry entry)
    {
        entry.Id ??= "";
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }
        entry.Name ??= "";
        entry.Category ??= "";
        entry.SourceMode ??= "external";
        entry.PackagePath = UnrealPathUtil.NormalizePackagePath(entry.PackagePath ?? "");
        entry.AssetClass ??= "";
        entry.Skeleton ??= "";
        entry.AdditiveMode ??= "";
        entry.Dependencies = (entry.Dependencies ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entry.Skeleton.Equals("SKEL_LEGOfig", StringComparison.OrdinalIgnoreCase) &&
            entry.Dependencies.Contains(NativeLegofigSkeletonPackage, StringComparer.OrdinalIgnoreCase))
        {
            // Schema-v1 stored only the imported object leaf. Promote it only when the import list
            // proves the exact package; never guess from a lookalike name.
            entry.Skeleton = NativeLegofigSkeletonPackage;
        }
        entry.UnresolvedImports = (entry.UnresolvedImports ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        entry.CachedFiles = (entry.CachedFiles ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        entry.SupportPackages ??= new List<AnimLibraryCachedPackage>();
        foreach (var support in entry.SupportPackages)
        {
            support.PackagePath = UnrealPathUtil.NormalizePackagePath(support.PackagePath ?? "");
            support.AssetClass ??= "";
            support.Dependencies = (support.Dependencies ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            support.UnresolvedImports = (support.UnresolvedImports ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            support.CachedFiles = (support.CachedFiles ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            support.Notes ??= "";
        }
        entry.SupportPackages = entry.SupportPackages
            .Where(support => !string.IsNullOrWhiteSpace(support.PackagePath))
            .GroupBy(support => support.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(support => support.Inspected)
                .ThenByDescending(support => support.CachedFiles.Count)
                .ThenBy(support => support.PackagePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(support => support.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        entry.HealthStatus ??= "";
        entry.HealthIssues ??= new List<string>();
        entry.Notes ??= "";
        entry.AddedUtc ??= "";
        entry.UpdatedUtc ??= "";
    }

    private void AssessEntryHealth(AnimLibraryEntry entry, bool legacyWhenUngrouped)
    {
        var owned = entry.CachedFiles.Count > 0 ||
                    (!entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase) &&
                     !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase));
        var issues = !owned && !entry.Inspected
            ? new List<string>()
            : InspectionIssues(entry, requireSupportedAnimation: owned);
        if (entry.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(entry.HealthIssues);
        }

        if (owned)
        {
            if (ResolveInGameData(entry.PackagePath) is not null)
            {
                issues.Add(
                    $"Managed animation package collides with configured base-game data: {entry.PackagePath}. Re-import it from a unique /Game/Mods/... path.");
            }
            if (entry.CachedFiles.Count == 0)
            {
                issues.Add("Primary cooked package is not cached.");
            }
            else
            {
                foreach (var cached in entry.CachedFiles)
                {
                    if (!TryResolveLibraryFile(cached, out _))
                    {
                        issues.Add($"Cached file is missing or outside the library: {cached}");
                    }
                }
            }

            foreach (var support in entry.SupportPackages)
            {
                if (ResolveInGameData(support.PackagePath) is not null)
                {
                    issues.Add(
                        $"{support.PackagePath}: cached support collides with configured base-game data and must be supplied by the game instead.");
                }
                if (!support.Inspected)
                {
                    issues.Add($"{support.PackagePath}: {FallbackInspectionNote(support.Notes)}");
                }
                if (support.UnresolvedImports.Any(ContainsUnknownMarker) ||
                    support.Dependencies.Any(ContainsUnknownMarker) ||
                    ContainsUnknownMarker(support.AssetClass))
                {
                    issues.Add($"{support.PackagePath}: contains an unresolved UnknownPackage/UnknownExport import.");
                }
                if (support.CachedFiles.Count == 0)
                {
                    issues.Add($"{support.PackagePath}: cooked package is not cached.");
                }
                foreach (var cached in support.CachedFiles)
                {
                    if (!TryResolveLibraryFile(cached, out _))
                    {
                        issues.Add($"{support.PackagePath}: cached file is missing or outside the library: {cached}");
                    }
                }
            }

            if (legacyWhenUngrouped && entry.SupportPackages.Count == 0)
            {
                if (!entry.Skeleton.Equals(NativeLegofigSkeletonPackage, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(
                        $"Legacy single-package cache does not prove the native skeleton {NativeLegofigSkeletonPackage}; re-import its source container.");
                }
                foreach (var dependency in entry.Dependencies
                             .Where(path => path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (ResolveOnDisk(dependency) is null)
                    {
                        issues.Add($"Legacy single-package cache has an unresolved /Game dependency: {dependency}");
                    }
                }
            }
        }

        issues = issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(issue => issue, StringComparer.OrdinalIgnoreCase)
            .ToList();
        entry.HealthIssues = issues;
        if (issues.Count > 0)
        {
            entry.HealthStatus = "quarantined";
            entry.IsAvailable = false;
        }
        else if (!owned)
        {
            entry.HealthStatus = "external";
            entry.IsAvailable = true;
        }
        else
        {
            entry.HealthStatus = legacyWhenUngrouped && entry.SupportPackages.Count == 0
                ? "legacy"
                : "healthy";
            entry.IsAvailable = true;
        }
    }

    private static List<string> InspectionIssues(
        AnimLibraryEntry entry,
        string? expectedAnimationClass = null,
        bool requireSupportedAnimation = false)
    {
        var issues = new List<string>();
        if (!entry.Inspected)
        {
            issues.Add(FallbackInspectionNote(entry.Notes));
        }
        var actualAnimationClass = NormalizeSupportedAnimationClass(entry.AssetClass);
        if (requireSupportedAnimation && string.IsNullOrWhiteSpace(actualAnimationClass))
        {
            issues.Add(
                $"Asset class is '{(string.IsNullOrWhiteSpace(entry.AssetClass) ? "unreadable" : entry.AssetClass)}', " +
                "not AnimSequence or AnimMontage.");
        }
        if (!string.IsNullOrWhiteSpace(expectedAnimationClass) &&
            !actualAnimationClass.Equals(
                NormalizeSupportedAnimationClass(expectedAnimationClass),
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(
                $"Asset class is '{(string.IsNullOrWhiteSpace(entry.AssetClass) ? "unreadable" : entry.AssetClass)}', " +
                $"not the expected {NormalizeSupportedAnimationClass(expectedAnimationClass)} class.");
        }
        if (entry.UnresolvedImports.Any(ContainsUnknownMarker) ||
            entry.Dependencies.Any(ContainsUnknownMarker) ||
            ContainsUnknownMarker(entry.AssetClass) ||
            ContainsUnknownMarker(entry.Skeleton))
        {
            issues.Add("Contains an unresolved UnknownPackage/UnknownExport import.");
        }
        return issues;
    }

    private static string FallbackInspectionNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "Package could not be inspected." : note;

    private static void Quarantine(AnimLibraryEntry entry, IEnumerable<string> issues)
    {
        entry.HealthStatus = "quarantined";
        entry.IsAvailable = false;
        entry.HealthIssues = issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(issue => issue, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ResetInspection(AnimLibraryEntry entry)
    {
        entry.AssetClass = "";
        entry.Skeleton = "";
        entry.RootMotion = false;
        entry.AdditiveMode = "";
        entry.Dependencies = new List<string>();
        entry.UnresolvedImports = new List<string>();
        entry.Inspected = false;
        entry.Notes = "";
        entry.HealthStatus = "";
        entry.HealthIssues = new List<string>();
        entry.IsAvailable = true;
    }

    private static AnimLibraryEntry? FindCanonicalEntry(AnimLibrary library, string packagePath) =>
        OrderCanonical(library.Entries.Where(entry =>
            UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(UnrealPathUtil.NormalizePackagePath(packagePath), StringComparison.OrdinalIgnoreCase)))
        .FirstOrDefault();

    private static IOrderedEnumerable<AnimLibraryEntry> OrderCanonical(IEnumerable<AnimLibraryEntry> entries) =>
        entries
            .OrderByDescending(entry => entry.IsAvailable)
            .ThenByDescending(entry => entry.HealthStatus.Equals("healthy", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Version)
            .ThenByDescending(entry => entry.UpdatedUtc, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

    private static void RemoveDuplicateEntries(AnimLibrary library, AnimLibraryEntry canonical)
    {
        var packagePath = UnrealPathUtil.NormalizePackagePath(canonical.PackagePath);
        library.Entries.RemoveAll(entry =>
            !ReferenceEquals(entry, canonical) &&
            UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(packagePath, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveLibraryFile(string cachedRelative, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(cachedRelative) || Path.IsPathRooted(cachedRelative))
        {
            return false;
        }
        try
        {
            var root = Path.GetFullPath(LibraryRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, cachedRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithinRoot(root, candidate) || !File.Exists(candidate))
            {
                return false;
            }
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGamePackageRelativePath(string packagePath, out string relativePath)
    {
        relativePath = "";
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!normalized.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var candidate = normalized["/Game/".Length..];
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Split('/').Any(part => part is "" or "." or ".."))
        {
            return false;
        }
        relativePath = candidate.Replace('/', Path.DirectorySeparatorChar);
        return true;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUnknownMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.IndexOf("UnknownPackage", StringComparison.OrdinalIgnoreCase) >= 0 ||
         value.IndexOf("UnknownExport", StringComparison.OrdinalIgnoreCase) >= 0);

    private static string ResolveImportPackage(UAsset asset, FPackageIndex index)
    {
        var visited = new HashSet<int>();
        while (!index.IsNull() && index.IsImport() && visited.Add(index.Index))
        {
            var import = index.ToImport(asset);
            if (import.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                return import.ObjectName.ToString();
            }
            index = import.OuterIndex;
        }
        return "";
    }

    /// <summary>True only for primary animation classes the managed library can safely own.</summary>
    private static bool IsSupportedAnimationClass(string? assetClass) =>
        !string.IsNullOrWhiteSpace(NormalizeSupportedAnimationClass(assetClass));

    private static string NormalizeSupportedAnimationClass(string? assetClass)
    {
        var value = assetClass?.Trim().Trim('\'', '"') ?? "";
        var split = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('.'));
        if (split >= 0 && split + 1 < value.Length)
        {
            value = value[(split + 1)..];
        }
        value = value.Trim().Trim('\'', '"');
        if (value.Equals("AnimSequence", StringComparison.OrdinalIgnoreCase)) return "AnimSequence";
        if (value.Equals("AnimMontage", StringComparison.OrdinalIgnoreCase)) return "AnimMontage";
        return "";
    }

    private static bool IsSkeletonClass(string? assetClass) =>
        !string.IsNullOrWhiteSpace(assetClass) &&
        (assetClass.Equals("Skeleton", StringComparison.OrdinalIgnoreCase) ||
         assetClass.EndsWith(".Skeleton", StringComparison.OrdinalIgnoreCase) ||
         assetClass.EndsWith("/Skeleton", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Resolves only the active base-game + installed-DLC extract.</summary>
    private static string? ResolveInGameData(string packagePath)
    {
        var norm = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!ExtractedPackagePathService.IsContentPackagePath(norm))
        {
            return null;
        }
        var extracted = ExtractedPackagePathService.ResolvePackageUasset(
            AppSettings.Current.EffectiveExtractedContentRoot(),
            norm);
        return !string.IsNullOrWhiteSpace(extracted) && File.Exists(extracted)
            ? extracted
            : null;
    }

    /// <summary>Resolves a game, Game Feature, or exported /Game package to a cooked .uasset on disk.</summary>
    private static string? ResolveOnDisk(string packagePath)
    {
        var norm = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!ExtractedPackagePathService.IsContentPackagePath(norm))
        {
            return null;
        }

        var extracted = ResolveInGameData(norm);
        if (!string.IsNullOrWhiteSpace(extracted))
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
