using System.Security.Cryptography;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Reads the active user's DPRD/AbilitySet assets for the Abilities editor. The shipped game-data
/// file supplies a fast offline library while the active extraction is merged in so DLC and newer
/// game builds are not hidden. This service never writes cooked assets.
/// </summary>
public sealed class AbilityCatalogService : IAbilityCatalogSource
{
    private const CustomSerializationFlags NameMapOnly = CustomSerializationFlags.SkipParsingExports;
    public static AbilityCatalogService Instance { get; } = new();

    private readonly Dictionary<string, (DateTime Stamp, AbilitySetInfo? Info)> _setCache =
        new(StringComparer.OrdinalIgnoreCase);

    public sealed class Snapshot
    {
        public string DonorDprdPackage { get; init; } = "";
        public string DonorFingerprint { get; init; } = "";
        public IReadOnlyList<string> OrderedDonorSets { get; init; } = Array.Empty<string>();
        public IReadOnlyList<AbilitySetInfo> DonorSets { get; init; } = Array.Empty<AbilitySetInfo>();
        public IReadOnlyList<AbilitySetInfo> LibrarySets { get; init; } = Array.Empty<AbilitySetInfo>();
        public IReadOnlyList<string> GameplayAbilityPackages { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
        public bool IsReady => string.IsNullOrWhiteSpace(Error) && !string.IsNullOrWhiteSpace(DonorDprdPackage);
    }

    public sealed class AbilitySetInfo
    {
        public string PackagePath { get; init; } = "";
        public string Name => UnrealPathUtil.AssetName(PackagePath);
        public string Category { get; init; } = "Other";
        public bool IsProtectedCore { get; init; }
        public bool IsReadable { get; init; }
        public IReadOnlyList<GrantInfo> GameplayAbilities { get; init; } = Array.Empty<GrantInfo>();
        public IReadOnlyList<GrantInfo> GameplayEffects { get; init; } = Array.Empty<GrantInfo>();
        public IReadOnlyList<GrantInfo> Attributes { get; init; } = Array.Empty<GrantInfo>();
        public IReadOnlyList<GrantInfo> GameplayData { get; init; } = Array.Empty<GrantInfo>();
        public IReadOnlyList<GrantInfo> ActorCues { get; init; } = Array.Empty<GrantInfo>();
        public IReadOnlyList<GrantInfo> StaticCues { get; init; } = Array.Empty<GrantInfo>();

        public int GrantCount => GameplayAbilities.Count + GameplayEffects.Count + Attributes.Count +
                                 GameplayData.Count + ActorCues.Count + StaticCues.Count;
    }

    public sealed class GrantInfo
    {
        public string ArrayName { get; init; } = "";
        public string PackagePath { get; init; } = "";
        public string ObjectName { get; init; } = "";
        public int? Level { get; init; }
        public string InputTag { get; init; } = "";
        public string DisplayName => !string.IsNullOrWhiteSpace(ObjectName)
            ? ObjectName.EndsWith("_C", StringComparison.OrdinalIgnoreCase) ? ObjectName[..^2] : ObjectName
            : UnrealPathUtil.AssetName(PackagePath);
    }

    public Snapshot BuildSnapshot(NativeSuitProject project)
    {
        try
        {
            var mappings = LoadMappings();
            if (mappings is null)
            {
                return new Snapshot { Error = "The configured .usmap is required to read ability loadouts." };
            }

            var donor = AnimArchetypeGraftService.DetectDonorForProject(project, "", mappings);
            var dprdPackage = donor?.DprdPackage ?? project.AbilityLoadout?.DonorDprdPackage ?? "";
            if (string.IsNullOrWhiteSpace(dprdPackage))
            {
                return new Snapshot { Error = "The selected gameplay donor does not expose a DPRD." };
            }

            var root = AppSettings.Current.EffectiveExtractedContentRoot();
            var dprdFile = ExtractedPackagePathService.ResolvePackageUasset(root, dprdPackage) ?? "";
            if (!File.Exists(dprdFile))
            {
                return new Snapshot
                {
                    DonorDprdPackage = dprdPackage,
                    Error = $"The donor DPRD is not present in the active extraction: {dprdPackage}"
                };
            }

            var ordered = ReadDprdAbilitySets(dprdFile, mappings);
            var donorInfos = ordered.Select(package => InspectAbilitySet(package, mappings) ?? Placeholder(package))
                .ToList();
            var shippedPackages = ShippedAbilitySetPackages();
            var library = AbilitySetPackages(shippedPackages)
                .Select(package =>
                {
                    var inspected = InspectAbilitySet(package, mappings);
                    return KeepAbilitySetCandidate(
                        shippedPackages.Contains(package),
                        inspected is not null)
                        ? inspected ?? Placeholder(package)
                        : null;
                })
                .Where(info => info is not null)
                .Select(info => info!)
                .OrderBy(info => info.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new Snapshot
            {
                DonorDprdPackage = dprdPackage,
                DonorFingerprint = AbilityLoadoutService.Fingerprint(ordered),
                OrderedDonorSets = ordered,
                DonorSets = donorInfos,
                LibrarySets = library,
                GameplayAbilityPackages = GameplayAbilityPackages(),
            };
        }
        catch (Exception ex)
        {
            return new Snapshot { Error = ex.Message };
        }
    }

    public AbilityEditorCatalog BuildForProject(NativeSuitProject project)
    {
        var snapshot = BuildSnapshot(project);
        AbilitySetCatalogEntry Convert(AbilitySetInfo info) => new()
        {
            PackagePath = info.PackagePath,
            DisplayName = info.Name,
            Category = info.Category,
            Source = SourceFor(info.PackagePath),
            IsCore = info.IsProtectedCore,
            IsAvailable = info.IsReadable,
            GameplayAbilities = info.GameplayAbilities.Select(grant => new GameplayAbilityCatalogEntry
            {
                PackagePath = grant.PackagePath,
                SourceAbilitySetPackage = info.PackagePath,
                AbilityLevel = grant.Level ?? 1,
                InputTag = grant.InputTag,
            }).ToList(),
        };

        var available = snapshot.LibrarySets.Select(Convert).ToList();
        var grants = available.SelectMany(set => set.GameplayAbilities).ToList();
        var knownGrantPaths = grants.Select(grant => grant.PackagePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        grants.AddRange(snapshot.GameplayAbilityPackages
            .Where(package => !knownGrantPaths.Contains(package))
            .Select(package => new GameplayAbilityCatalogEntry { PackagePath = package }));

        var catalog = new AbilityEditorCatalog
        {
            DonorDprdPackage = snapshot.DonorDprdPackage,
            DonorAbilitySetFingerprint = snapshot.DonorFingerprint,
            SavedLoadoutNeedsRemap = project.AbilityLoadout is not null && snapshot.IsReady &&
                !AbilityLoadoutService.DonorMatches(
                    project.AbilityLoadout,
                    snapshot.DonorDprdPackage,
                    snapshot.OrderedDonorSets),
            InheritedAbilitySets = snapshot.DonorSets.Select(Convert).ToList(),
            AvailableAbilitySets = available,
            GameplayAbilities = grants,
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            catalog.Warnings.Add(snapshot.Error);
        }
        if (catalog.SavedLoadoutNeedsRemap)
        {
            catalog.Warnings.Add(
                "The saved ability loadout belongs to a different gameplay donor or donor revision and was not reused.");
        }
        var unreadable = snapshot.LibrarySets.Count(set => !set.IsReadable);
        if (unreadable > 0)
        {
            catalog.Warnings.Add($"{unreadable} shipped AbilitySet asset(s) are unavailable or unreadable in the active extraction.");
        }
        return catalog;
    }

    public AbilitySetInfo? InspectAbilitySet(string packagePath) => InspectAbilitySet(packagePath, LoadMappings());

    public void Invalidate() => _setCache.Clear();

    private AbilitySetInfo? InspectAbilitySet(string packagePath, Usmap? mappings)
    {
        if (mappings is null || string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        var file = ExtractedPackagePathService.ResolvePackageUasset(root, packagePath) ?? "";
        if (!File.Exists(file))
        {
            return null;
        }

        var stamp = File.GetLastWriteTimeUtc(file);
        if (_setCache.TryGetValue(packagePath, out var cached) && cached.Stamp == stamp)
        {
            return cached.Info;
        }

        AbilitySetInfo? info = null;
        try
        {
            var asset = new UAsset(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(candidate => candidate.GetExportClassType()?.Value.Value
                    .Equals("TtAbilitySet", StringComparison.OrdinalIgnoreCase) == true)
                ?? asset.Exports.OfType<NormalExport>().FirstOrDefault(candidate =>
                    candidate.Data.Any(property => property.Name.ToString().StartsWith("Granted", StringComparison.Ordinal)));
            if (export is not null)
            {
                IReadOnlyList<GrantInfo> Grants(string arrayName) => export.Data
                    .OfType<ArrayPropertyData>()
                    .Where(array => array.Name.ToString().Equals(arrayName, StringComparison.Ordinal))
                    .SelectMany(array => array.Value.OfType<StructPropertyData>())
                    .Select(entry => ReadGrant(asset, arrayName, entry))
                    .Where(grant => grant is not null)
                    .Cast<GrantInfo>()
                    .ToList();

                info = new AbilitySetInfo
                {
                    PackagePath = packagePath,
                    Category = CategoryFor(packagePath),
                    IsProtectedCore = AbilityLoadoutService.IsProtectedCoreSet(packagePath),
                    IsReadable = true,
                    GameplayAbilities = Grants("GrantedGameplayAbilities"),
                    GameplayEffects = Grants("GrantedGameplayEffects"),
                    Attributes = Grants("GrantedAttributes"),
                    GameplayData = Grants("GrantedGameplayData"),
                    ActorCues = Grants("GrantedGameplayCueNotifyActorData"),
                    StaticCues = Grants("GrantedGameplayCueNotifyStaticData"),
                };
            }
        }
        catch
        {
            // A stale/incomplete extraction remains visible as a library placeholder. The editor
            // can explain that the bytes must be refreshed instead of silently hiding the entry.
        }

        _setCache[packagePath] = (stamp, info);
        return info;
    }

    private static GrantInfo? ReadGrant(UAsset asset, string arrayName, StructPropertyData entry)
    {
        var objectField = entry.Value.OfType<ObjectPropertyData>().FirstOrDefault();
        if (objectField is null || objectField.Value.IsNull())
        {
            return null;
        }

        var objectName = "";
        var package = "";
        if (objectField.Value.IsImport())
        {
            var import = objectField.Value.ToImport(asset);
            objectName = import.ObjectName.ToString();
            package = ImportPackage(asset, import);
        }
        else if (objectField.Value.IsExport())
        {
            objectName = objectField.Value.ToExport(asset).ObjectName.ToString();
        }

        var level = entry.Value.FirstOrDefault(field =>
            field.Name.ToString().Contains("Level", StringComparison.OrdinalIgnoreCase))?.RawValue;
        int? parsedLevel = level switch
        {
            int value => value,
            byte value => value,
            _ when int.TryParse(level?.ToString(), out var value) => value,
            _ => null,
        };
        var inputTag = entry.Value.FirstOrDefault(field =>
            field.Name.ToString().Equals("InputTag", StringComparison.OrdinalIgnoreCase));

        return new GrantInfo
        {
            ArrayName = arrayName,
            PackagePath = package,
            ObjectName = objectName,
            Level = parsedLevel,
            InputTag = DescribeTag(inputTag),
        };
    }

    private static string DescribeTag(UAssetAPI.PropertyTypes.Objects.PropertyData? property)
    {
        if (property is not StructPropertyData tag)
        {
            return property?.RawValue?.ToString() ?? "";
        }

        foreach (var field in tag.Value)
        {
            var raw = field.RawValue?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(raw) && !raw.StartsWith("System.", StringComparison.Ordinal))
            {
                return raw;
            }
        }
        return "";
    }

    private static string ImportPackage(UAsset asset, Import import)
    {
        var outer = import.OuterIndex;
        var guard = 0;
        while (outer.IsImport() && guard++ < 16)
        {
            var candidate = outer.ToImport(asset);
            var name = candidate.ObjectName.ToString();
            if (candidate.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("/", StringComparison.Ordinal))
            {
                return name;
            }
            outer = candidate.OuterIndex;
        }
        return "";
    }

    private static List<string> ReadDprdAbilitySets(string dprdFile, Usmap mappings)
    {
        var asset = new UAsset(dprdFile, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
        var array = asset.Exports.OfType<NormalExport>()
            .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
            .FirstOrDefault(property => property.Name.ToString().Equals("AbilitySets", StringComparison.Ordinal));
        if (array is null)
        {
            return new List<string>();
        }

        return array.Value.OfType<ObjectPropertyData>()
            .Where(item => !item.Value.IsNull() && item.Value.IsImport())
            .Select(item => ImportPackage(asset, item.Value.ToImport(asset)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .ToList();
    }

    private static HashSet<string> ShippedAbilitySetPackages() =>
        GameDataService.Instance.AssetsOfClass("TtAbilitySet")
            .Select(asset => UnrealPathUtil.NormalizePackagePath(asset.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> AbilitySetPackages(IReadOnlyCollection<string>? shippedPackages = null)
    {
        var paths = shippedPackages is null
            ? ShippedAbilitySetPackages()
            : new HashSet<string>(shippedPackages, StringComparer.OrdinalIgnoreCase);
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        foreach (var mount in ExtractedPackagePathService.EnumerateMounts(root))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(mount.ContentRoot, "AS_*.uasset", SearchOption.AllDirectories))
                {
                    var package = ExtractedPackagePathService.PackagePathFromFile(root, file);
                    if (!string.IsNullOrWhiteSpace(package))
                    {
                        paths.Add(UnrealPathUtil.NormalizePackagePath(package));
                    }
                }
            }
            catch
            {
                // One unavailable DLC mount must not hide the base-game catalog.
            }
        }
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Filename discovery is deliberately broader than the UAsset class check so installed DLC
    /// can contribute sets absent from the bundled index. Only a verified TtAbilitySet survives
    /// that broad scan; shipped index entries remain visible as unavailable when their files are
    /// genuinely missing or unreadable.
    /// </summary>
    internal static bool KeepAbilitySetCandidate(bool isShippedAbilitySet, bool inspectedAsAbilitySet) =>
        isShippedAbilitySet || inspectedAsAbilitySet;

    private static IReadOnlyList<string> GameplayAbilityPackages()
    {
        var paths = new HashSet<string>(
            GameDataService.Instance.Db.Assets
                .Select(asset => UnrealPathUtil.NormalizePackagePath(asset.Path))
                .Where(path => UnrealPathUtil.AssetName(path).StartsWith("GA_", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        foreach (var mount in ExtractedPackagePathService.EnumerateMounts(root))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(mount.ContentRoot, "GA_*.uasset", SearchOption.AllDirectories))
                {
                    var package = ExtractedPackagePathService.PackagePathFromFile(root, file);
                    if (!string.IsNullOrWhiteSpace(package))
                    {
                        paths.Add(UnrealPathUtil.NormalizePackagePath(package));
                    }
                }
            }
            catch
            {
                // One unavailable DLC mount must not hide the base-game gameplay-ability catalog.
            }
        }
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AbilitySetInfo Placeholder(string packagePath) => new()
    {
        PackagePath = UnrealPathUtil.NormalizePackagePath(packagePath),
        Category = CategoryFor(packagePath),
        IsProtectedCore = AbilityLoadoutService.IsProtectedCoreSet(packagePath),
        IsReadable = false,
    };

    public static string CategoryFor(string packagePath)
    {
        if (packagePath.Contains("/Equipment/", StringComparison.OrdinalIgnoreCase)) return "Equipment";
        if (packagePath.Contains("/MeleeAbilities/", StringComparison.OrdinalIgnoreCase)) return "Combat";
        if (packagePath.Contains("/Gliding/", StringComparison.OrdinalIgnoreCase) ||
            packagePath.Contains("/Grappling/", StringComparison.OrdinalIgnoreCase) ||
            packagePath.Contains("/LedgeGrab/", StringComparison.OrdinalIgnoreCase) ||
            packagePath.Contains("/Sprinting/", StringComparison.OrdinalIgnoreCase) ||
            packagePath.Contains("/VentTraversal/", StringComparison.OrdinalIgnoreCase)) return "Traversal";
        if (packagePath.Contains("/Stealth", StringComparison.OrdinalIgnoreCase)) return "Stealth";
        if (packagePath.Contains("/Minifig/", StringComparison.OrdinalIgnoreCase)) return "Character";
        if (packagePath.Contains("/CoreAbilities/", StringComparison.OrdinalIgnoreCase)) return "Core";
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return "DLC / plugin";
        return "Other";
    }

    private static string SourceFor(string packagePath) =>
        packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? "Base game"
            : "Installed DLC / plugin";

    private static Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? MappingsCache.Load(configured)
            : null;
    }
}

/// <summary>Pure loadout resolution shared by the UI, generator, validation and regressions.</summary>
public static class AbilityLoadoutService
{
    private static readonly HashSet<string> ProtectedCoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AS_CharacterCoreAbilitySet",
        "AS_PlayableCoreAbilitySet",
        "AS_PlayableStartingStats",
        "AS_InputBuffering",
    };

    public static bool IsProtectedCoreSet(string packagePath) =>
        ProtectedCoreNames.Contains(UnrealPathUtil.AssetName(UnrealPathUtil.NormalizePackagePath(packagePath)));

    public static bool HasCustomizations(NativeSuitProject? project) =>
        project?.AbilityLoadout is { } profile && (profile.AbilitySets.Count > 0 || HeldItemService.Resolve(profile).Count > 0 || !string.IsNullOrWhiteSpace(profile.FightingStyleId));

    public static string Fingerprint(IEnumerable<string> orderedPackages)
    {
        var normalized = string.Join("\n", orderedPackages.Select(UnrealPathUtil.NormalizePackagePath));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string ConfigurationFingerprint(AbilityLoadoutProfile? profile)
    {
        if (profile is null)
        {
            return "donor-abilities";
        }
        var lines = new List<string>
        {
            profile.SchemaVersion.ToString(),
            UnrealPathUtil.NormalizePackagePath(profile.DonorDprdPackage),
            profile.DonorAbilitySetFingerprint,
            profile.FightingStyleId ?? "",
            SwordCombatService.Enabled(profile)
                ? System.Text.Json.JsonSerializer.Serialize(profile.SwordCombat ?? PlayerMeleeAdapterService.Defaults(profile.FightingStyleId)) : "",
            profile.AllowUnsafeCoreEdits ? "unsafe-core" : "protected-core",
            profile.HeldItems is null ? "legacy-held-items" : System.Text.Json.JsonSerializer.Serialize(profile.HeldItems),
            profile.HeldItems is { Count: > 0 } ? "held-actor-templates-v3-effects" : "",
            SwordCombatService.Enabled(profile) && MeleeStatusEffectService.Enabled(profile.SwordCombat?.HitStatus) ? "hit-status-v1" : "",
        };
        lines.AddRange((profile.DonorAbilitySetPackages ?? new List<string>())
            .Select(package => "donor-set|" + UnrealPathUtil.NormalizePackagePath(package)));
        foreach (var set in profile.AbilitySets.OrderBy(selection => selection.Order))
        {
            lines.Add($"set|{set.Order}|{set.Enabled}|{UnrealPathUtil.NormalizePackagePath(set.PackagePath)}");
            lines.AddRange(set.RemovedGameplayAbilities
                .Select(package => "remove|" + UnrealPathUtil.NormalizePackagePath(package)));
            lines.AddRange(set.AddedGameplayAbilities.Select(grant =>
                $"add|{UnrealPathUtil.NormalizePackagePath(grant.PackagePath)}|{grant.AbilityLevel}|{grant.InputTag}"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))
            .ToLowerInvariant();
    }

    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string> donorSets,
        AbilityLoadoutProfile? profile,
        IEnumerable<string>? requiredSets = null)
    {
        var donor = donorSets.Select(UnrealPathUtil.NormalizePackagePath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        var resolved = profile is null || profile.AbilitySets.Count == 0
            ? donor.ToList()
            : profile.AbilitySets
                .Select((selection, index) => (selection, index))
                .Where(item => item.selection.Enabled)
                .OrderBy(item => item.selection.Order)
                .ThenBy(item => item.index)
                .Select(item => UnrealPathUtil.NormalizePackagePath(item.selection.PackagePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

        if (profile?.AllowUnsafeCoreEdits != true)
        {
            foreach (var core in donor.Where(IsProtectedCoreSet))
            {
                if (!resolved.Contains(core, StringComparer.OrdinalIgnoreCase))
                {
                    var donorIndex = donor.IndexOf(core);
                    var insertion = Math.Clamp(donorIndex, 0, resolved.Count);
                    resolved.Insert(insertion, core);
                }
            }
        }

        if (requiredSets is not null)
        {
            foreach (var required in requiredSets.Select(UnrealPathUtil.NormalizePackagePath))
            {
                if (!string.IsNullOrWhiteSpace(required) &&
                    !resolved.Contains(required, StringComparer.OrdinalIgnoreCase))
                {
                    resolved.Add(required);
                }
            }
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool DonorMatches(AbilityLoadoutProfile? profile, string dprdPackage, IReadOnlyList<string> donorSets) =>
        profile is null ||
        (!string.IsNullOrWhiteSpace(profile.DonorAbilitySetFingerprint) &&
         UnrealPathUtil.NormalizePackagePath(profile.DonorDprdPackage)
             .Equals(UnrealPathUtil.NormalizePackagePath(dprdPackage), StringComparison.OrdinalIgnoreCase) &&
         profile.DonorAbilitySetFingerprint.Equals(Fingerprint(donorSets), StringComparison.OrdinalIgnoreCase));
}
