using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Describes why a character-dependency edge is present. A trace never upgrades a filename or
/// folder convention into serialized proof: callers can therefore distinguish a complete native
/// graph from a useful-but-unverified discovery.
/// </summary>
public enum CharacterTraceEvidenceKind
{
    SerializedProperty,
    SerializedOrderedArray,
    SerializedClassParent,
    SerializedImport,
    InheritedSerializedImport,
    InheritedSerializedProperty,
    FilenameConvention,
    SerializedNullArrayEntry,
}

public enum CharacterTraceDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum CharacterTraceClosureDepth
{
    DirectSerializedReferences,
}

public sealed class CharacterTraceReference
{
    public string PackagePath { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string SourcePackage { get; set; } = "";
    public string SourceProperty { get; set; } = "";
    public int Index { get; set; } = -1;
    public CharacterTraceEvidenceKind Evidence { get; set; }
    public bool TargetExists { get; set; }
    public bool IsNull { get; set; }
    public bool IsNativeReference { get; set; }
}

public sealed class CharacterTraceDiagnostic
{
    public CharacterTraceDiagnosticSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string SourcePackage { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class CharacterAbilityGrantTrace
{
    public string Kind { get; set; } = "";
    public CharacterTraceReference Reference { get; set; } = new();
    public int? AbilityLevel { get; set; }
    public float? EffectLevel { get; set; }
    public string InputTag { get; set; } = "";
}

public sealed class CharacterAbilitySetTrace
{
    public string PackagePath { get; set; } = "";
    public bool IsReadable { get; set; }
    public List<CharacterAbilityGrantTrace> Grants { get; set; } = new();
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterEquipmentDefinitionTrace
{
    public string PackagePath { get; set; } = "";
    public bool IsReadable { get; set; }
    public List<CharacterTraceReference> AbilitySetsToGrant { get; set; } = new();
    public List<CharacterTraceReference> GameplayAbilities { get; set; } = new();
    public List<CharacterTraceReference> SpawnedActors { get; set; } = new();
    public List<CharacterTraceReference> OtherReferencedPackages { get; set; } = new();
    public bool HasUntracedNestedPackageGraphs { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterEquipmentTypeTrace
{
    public string PackagePath { get; set; } = "";
    public bool IsReadable { get; set; }
    public string EquipmentTag { get; set; } = "";
    public CharacterTraceReference? EquipmentDefinition { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterUpgradeTrace
{
    public string PackagePath { get; set; } = "";
    public bool IsReadable { get; set; }
    public List<CharacterTraceReference> DirectReferences { get; set; } = new();
    public bool HasUntracedNestedPackageGraphs { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterAnimationCompositeTrace
{
    public string PackagePath { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsReadable { get; set; }
    public bool HasOrderedParentArray { get; set; }
    public List<CharacterTraceReference> OrderedParents { get; set; } = new();
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterDcmdTrace
{
    public string PackagePath { get; set; } = "";
    public string SourceMount { get; set; } = "";
    public bool IsPlayableMetadata { get; set; }
    public bool IsHumanoidPawn { get; set; }
    public bool IsReadable { get; set; }
    public CharacterTraceReference? Pawn { get; set; }
    public CharacterTraceReference? MenuActor { get; set; }
    public CharacterTraceReference? CinematicsActor { get; set; }
    public CharacterTraceReference? UiMetadata { get; set; }
    public string PawnTag { get; set; } = "";
    public string ProgressTag { get; set; } = "";
    public List<CharacterTraceReference> EquipmentTypes { get; set; } = new();
    public List<CharacterTraceReference> UpgradeAssets { get; set; } = new();
    public List<CharacterTraceReference> PawnClassChain { get; set; } = new();
    public List<CharacterTraceReference> MenuActorClassChain { get; set; } = new();
    public List<CharacterTraceReference> CinematicsActorClassChain { get; set; } = new();
    public string GameplayProfileId { get; set; } = "";
    public bool HasUntracedNestedPackageGraphs { get; set; }
    public bool IsDependencyClosureComplete { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterGameplayProfileTrace
{
    public string Id { get; set; } = "";
    public CharacterTraceReference RuntimeData { get; set; } = new();
    public CharacterTraceReference MontageComposite { get; set; } = new();
    public CharacterTraceReference LayerComposite { get; set; } = new();
    public List<CharacterTraceReference> OrderedAbilitySets { get; set; } = new();
    public List<CharacterTraceReference> OrderedEquipmentDefinitions { get; set; } = new();
    public bool EquipmentUsesDefaultEmptyArray { get; set; }
    public List<string> CombatAbilitySetPackages { get; set; } = new();
    public List<string> CombatTypeEffectPackages { get; set; } = new();
    public List<string> HeldItemAbilityPackages { get; set; } = new();
    public List<string> GrappleDataSetPackages { get; set; } = new();
    public bool HasPlayableCore { get; set; }
    public bool IsPlayerProfile { get; set; }
    public bool IsStructurallyCertified { get; set; }
    public bool IsDependencyClosureComplete { get; set; }
    public bool HasUntracedNestedPackageGraphs { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterPlayableVariantTrace
{
    public string PackagePath { get; set; } = "";
    public string SourceMount { get; set; } = "";
    public bool IsHumanoid { get; set; }
    public bool IsEquipmentControlledPawn { get; set; }
    public bool HasSerializedPlayableDcmdEvidence { get; set; }
    public List<CharacterTraceReference> ClassChain { get; set; } = new();
    public List<CharacterTraceReference> Dcmds { get; set; } = new();
    public string GameplayProfileId { get; set; } = "";
    public bool IsStructurallyCertified { get; set; }
    public bool IsDependencyClosureComplete { get; set; }
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class CharacterDependencyTraceCatalog
{
    public int SchemaVersion { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public string ExtractedContentRoot { get; set; } = "";
    public string MappingsPath { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public CharacterTraceClosureDepth ClosureDepth { get; set; } =
        CharacterTraceClosureDepth.DirectSerializedReferences;
    public bool TransitiveBlueprintPackageGraphsTraced { get; set; }
    public List<string> Mounts { get; set; } = new();
    public List<CharacterPlayableVariantTrace> PlayableVariants { get; set; } = new();
    public List<CharacterDcmdTrace> Dcmds { get; set; } = new();
    public List<CharacterDcmdTrace> PlayableDcmds { get; set; } = new();
    public List<CharacterGameplayProfileTrace> GameplayProfiles { get; set; } = new();
    public List<CharacterAbilitySetTrace> AbilitySets { get; set; } = new();
    public List<CharacterEquipmentTypeTrace> EquipmentTypes { get; set; } = new();
    public List<CharacterEquipmentDefinitionTrace> EquipmentDefinitions { get; set; } = new();
    public List<CharacterUpgradeTrace> Upgrades { get; set; } = new();
    public List<CharacterAnimationCompositeTrace> AnimationComposites { get; set; } = new();
    public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();

    [JsonIgnore]
    public int HumanoidPlayableCount => PlayableVariants.Count(variant => variant.IsHumanoid);

    [JsonIgnore]
    public int CertifiedHumanoidPlayableCount => PlayableVariants.Count(variant =>
        variant.IsHumanoid && variant.IsStructurallyCertified);

    [JsonIgnore]
        public int PlayerProfileCount => GameplayProfiles.Count(profile => profile.IsPlayerProfile);

    [JsonIgnore]
    public int CertifiedPlayerProfileCount => GameplayProfiles.Count(profile =>
        profile.IsPlayerProfile && profile.IsStructurallyCertified);
}

/// <summary>
/// Builds a read-only, evidence-bearing dependency graph from the active user's extracted game and
/// Game Feature mounts. The cache is deliberately tied to that extraction and mappings file; it is
/// never a shipped list of characters and therefore follows installed DLC per user.
/// </summary>
public sealed class CharacterDependencyTraceService
{
    public const int CurrentSchemaVersion = 4;
    private const int MaximumClassDepth = 32;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public static CharacterDependencyTraceService Instance { get; } = new();
    public static string CachePath => Path.Combine(AppSettings.CacheRoot, "character-dependency-trace.json");

    public CharacterDependencyTraceCatalog Load(bool forceRebuild = false) => Load(
        AppSettings.Current.EffectiveExtractedContentRoot(),
        AppSettings.Current.EffectiveUsmapPath() ?? "",
        forceRebuild);

    public CharacterDependencyTraceCatalog Load(
        string extractedContentRoot,
        string mappingsPath,
        bool forceRebuild = false)
    {
        lock (_gate)
        {
            var context = BuildContext.Create(extractedContentRoot, mappingsPath);
            if (!forceRebuild && TryReadCache(context.Fingerprint) is { } cached)
            {
                return cached;
            }

            var catalog = Build(context);
            TryWrite(CachePath, catalog);
            return catalog;
        }
    }

    public void WriteReport(CharacterDependencyTraceCatalog catalog, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("A report path is required.", nameof(outputPath));
        }
        TryWrite(Path.GetFullPath(outputPath), catalog, throwOnFailure: true);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(CachePath)) File.Delete(CachePath);
            }
            catch
            {
                // A locked/read-only cache simply fails its fingerprint check on the next load.
            }
        }
    }

    private static CharacterDependencyTraceCatalog Build(BuildContext context)
    {
        var builder = new Builder(context);
        return builder.Build();
    }

    private static CharacterDependencyTraceCatalog? TryReadCache(string fingerprint)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var cached = JsonSerializer.Deserialize<CharacterDependencyTraceCatalog>(
                File.ReadAllText(CachePath),
                Json);
            return CacheIdentityMatches(cached, fingerprint)
                ? cached
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CacheIdentityMatches(
        CharacterDependencyTraceCatalog? cached,
        string fingerprint) =>
        cached is { SchemaVersion: CurrentSchemaVersion } &&
        string.Equals(cached.SourceFingerprint, fingerprint, StringComparison.Ordinal);

    private static IEnumerable<CharacterTraceDiagnostic> EnumerateDiagnostics(
        CharacterDependencyTraceCatalog catalog)
    {
        foreach (var diagnostic in catalog.Diagnostics) yield return diagnostic;
        foreach (var trace in catalog.PlayableVariants)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.Dcmds)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.PlayableDcmds)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.GameplayProfiles)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.AbilitySets)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.EquipmentTypes)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.EquipmentDefinitions)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.Upgrades)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
        foreach (var trace in catalog.AnimationComposites)
        foreach (var diagnostic in trace.Diagnostics)
            yield return diagnostic;
    }

    private static void TryWrite(string path, CharacterDependencyTraceCatalog catalog, bool throwOnFailure = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(catalog, Json));
        }
        catch when (!throwOnFailure)
        {
            // Read-only installs may rebuild in memory each run.
        }
    }

    internal static bool IsHumanoidPlayablePackageForTest(string packagePath) =>
        IsHumanoidPackage(packagePath) && IsConcretePlayableAsset(UnrealPathUtil.AssetName(packagePath));

    internal static bool IsPlayablePawnTagForTest(string pawnTag) => IsPlayablePawnTag(pawnTag);

    internal static bool IsNullSoftReferenceForTest(string packageName, string assetName) =>
        IsNullSoftReference(packageName, assetName);

    internal static bool CacheIdentityMatchesForTest(
        int schemaVersion,
        string sourceFingerprint,
        string expectedFingerprint) =>
        CacheIdentityMatches(
            new CharacterDependencyTraceCatalog
            {
                SchemaVersion = schemaVersion,
                SourceFingerprint = sourceFingerprint,
            },
            expectedFingerprint);

    internal static bool IsCatalogUsableForCli(CharacterDependencyTraceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.CertifiedHumanoidPlayableCount > 0 &&
               catalog.CertifiedPlayerProfileCount > 0 &&
               !EnumerateDiagnostics(catalog).Any(diagnostic =>
                   diagnostic.Severity == CharacterTraceDiagnosticSeverity.Error);
    }

    internal static string SelectNearestDependencyForTest(IEnumerable<IReadOnlyList<string>> classLevels)
        => SelectNearestDependencyForTest(classLevels.Select(level =>
            (Candidates: level, HasExplicitNull: false, HasUnresolvedValue: false)));

    internal static string SelectNearestDependencyForTest(
        IEnumerable<(
            IReadOnlyList<string> Candidates,
            bool HasExplicitNull,
            bool HasUnresolvedValue)> classLevels)
    {
        foreach (var level in classLevels)
        {
            if (BlocksInheritedDependency(level.HasExplicitNull, level.HasUnresolvedValue)) return "";
            var selection = ClassifyDependencyLevel(level.Candidates);
            if (selection.IsAmbiguous) return "";
            if (!string.IsNullOrWhiteSpace(selection.PackagePath)) return selection.PackagePath;
        }
        return "";
    }

    internal static bool ProfileCertificateForTest(CharacterGameplayProfileTrace profile) =>
        profile.IsDependencyClosureComplete && ComputeStructuralCertificate(profile);

    internal static bool VariantCertificateForTest(
        CharacterGameplayProfileTrace profile,
        CharacterPlayableVariantTrace variant) =>
        ComputeVariantCertificate(profile, variant);

    internal static string ComputeSourceFingerprintForTest(string extractedContentRoot, string mappingsPath) =>
        BuildContext.Create(extractedContentRoot, mappingsPath).Fingerprint;

    private sealed class Builder
    {
        private readonly BuildContext _context;
        private readonly Usmap _mappings;
        private readonly Dictionary<string, AssetRecord> _byPackage;
        private readonly Dictionary<string, ClassNode> _classNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterAbilitySetTrace> _abilitySets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterEquipmentTypeTrace> _equipmentTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterEquipmentDefinitionTrace> _equipmentDefinitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterUpgradeTrace> _upgrades = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterAnimationCompositeTrace> _animationComposites = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterGameplayProfileTrace> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterDcmdTrace> _dcmdByPackage = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CharacterTraceDiagnostic> _diagnostics = new();

        public Builder(BuildContext context)
        {
            _context = context;
            _mappings = MappingsCache.Load(context.MappingsPath);
            var assetGroups = context.Assets
                .GroupBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _byPackage = assetGroups
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var duplicate in assetGroups.Where(group => group.Count() > 1))
            {
                _diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "duplicate-package",
                    duplicate.Key,
                    "More than one extracted file resolves to this package path: " +
                    string.Join(", ", duplicate.Select(asset => asset.FilePath))));
            }
        }

        public CharacterDependencyTraceCatalog Build()
        {
            var dcmds = BuildDcmds();
            foreach (var dcmd in dcmds) _dcmdByPackage[Normalize(dcmd.PackagePath)] = dcmd;
            var playableDcmds = dcmds.Where(dcmd =>
                dcmd.IsPlayableMetadata && dcmd.IsHumanoidPawn).ToList();
            var dcmdsByPawn = playableDcmds
                .Where(dcmd => !string.IsNullOrWhiteSpace(dcmd.Pawn?.PackagePath))
                .GroupBy(dcmd => Normalize(dcmd.Pawn!.PackagePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var variantAssets = _context.Assets
                .Where(asset => IsConcretePlayableAsset(UnrealPathUtil.AssetName(asset.PackagePath)))
                .Concat(playableDcmds
                    .Where(dcmd => !string.IsNullOrWhiteSpace(dcmd.Pawn?.PackagePath))
                    .Select(dcmd => _byPackage.GetValueOrDefault(Normalize(dcmd.Pawn!.PackagePath)))
                    .Where(asset => asset is not null)
                    .Cast<AssetRecord>())
                .DistinctBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase);
            var variants = new List<CharacterPlayableVariantTrace>();
            foreach (var asset in variantAssets)
            {
                var variant = BuildPlayableVariant(asset, dcmdsByPawn);
                variants.Add(variant);
            }

            // A DCMD's Pawn soft reference is authoritative discovery evidence even when its name
            // is not *_Playable. This is how shipped quest/reward characters such as Cluemaster
            // remain traceable without a filename allow-list.
            foreach (var dcmd in dcmds.Where(dcmd =>
                         dcmd.IsReadable &&
                         dcmd.IsHumanoidPawn &&
                         !string.IsNullOrWhiteSpace(dcmd.Pawn?.PackagePath)))
            {
                var dependency = ResolveClassDependencies(dcmd.Pawn!.PackagePath);
                dcmd.PawnClassChain = dependency.ClassChain;
                dcmd.Diagnostics.AddRange(dependency.Diagnostics);
                if (!string.IsNullOrWhiteSpace(dependency.RuntimeData?.PackagePath) &&
                    !string.IsNullOrWhiteSpace(dependency.MontageComposite?.PackagePath) &&
                    !string.IsNullOrWhiteSpace(dependency.LayerComposite?.PackagePath))
                {
                    dcmd.GameplayProfileId = EnsureProfile(dependency).Id;
                }
                else
                {
                    dcmd.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Warning,
                        "incomplete-gameplay-anchor",
                        dcmd.PackagePath,
                        "The humanoid pawn class chain did not prove a DPRD, MAS_Char, and LAS_Char triple."));
                }
            }

            // Some unlockable character profiles are authored on a CAT archetype that is not the
            // quest DCMD's visible pawn. Discover those class packages by role, but retain a profile
            // only after its serialized DPRD grants prove that it is player-capable.
            foreach (var asset in _context.Assets
                         .Where(asset => IsCharacterArchetypeCandidate(asset.PackagePath))
                         .OrderBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                var dependency = ResolveClassDependencies(asset.PackagePath);
                if (!string.IsNullOrWhiteSpace(dependency.RuntimeData?.PackagePath) &&
                    !string.IsNullOrWhiteSpace(dependency.MontageComposite?.PackagePath) &&
                    !string.IsNullOrWhiteSpace(dependency.LayerComposite?.PackagePath))
                {
                    EnsureProfile(dependency);
                }
            }

            foreach (var profile in _profiles.Values)
            {
                FinalizeProfile(profile);
            }

            var referencedProfileIds = variants.Select(variant => variant.GameplayProfileId)
                .Concat(dcmds.Select(dcmd => dcmd.GameplayProfileId))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profiles = _profiles.Values
                .Where(profile => referencedProfileIds.Contains(profile.Id) || profile.IsPlayerProfile)
                .OrderBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var retainedProfileIds = profiles.Select(profile => profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (variants.All(variant => !variant.IsHumanoid))
            {
                _diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "no-playable-characters",
                    _context.ContentRoot,
                    "No humanoid playable pawn classes or serialized playable PawnTags were found."));
            }
            if (profiles.All(profile => !profile.IsPlayerProfile))
            {
                _diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "no-player-profiles",
                    _context.ContentRoot,
                    "No DPRD profile proved PlayableCore plus exactly one melee controller."));
            }

            foreach (var dcmd in dcmds)
            {
                dcmd.IsDependencyClosureComplete = ComputeDcmdClosure(dcmd);
            }

            foreach (var variant in variants)
            {
                if (!retainedProfileIds.Contains(variant.GameplayProfileId))
                {
                    variant.IsStructurallyCertified = false;
                    variant.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "profile-not-retained",
                        variant.PackagePath,
                        "The playable did not resolve to a player-capable gameplay profile."));
                    continue;
                }
                variant.IsDependencyClosureComplete = ComputeVariantClosure(variant);
                variant.IsStructurallyCertified = ComputeVariantCertificate(
                    _profiles[variant.GameplayProfileId],
                    variant);
            }

            return new CharacterDependencyTraceCatalog
            {
                SchemaVersion = CurrentSchemaVersion,
                GeneratedUtc = DateTime.UtcNow,
                ExtractedContentRoot = _context.ContentRoot,
                MappingsPath = _context.MappingsPath,
                SourceFingerprint = _context.Fingerprint,
                Mounts = _context.Mounts.Select(mount => mount.PackageRoot).ToList(),
                PlayableVariants = variants,
                Dcmds = dcmds.OrderBy(dcmd => dcmd.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                PlayableDcmds = playableDcmds.OrderBy(dcmd => dcmd.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                GameplayProfiles = profiles,
                AbilitySets = _abilitySets.Values.OrderBy(trace => trace.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                EquipmentTypes = _equipmentTypes.Values.OrderBy(trace => trace.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                EquipmentDefinitions = _equipmentDefinitions.Values.OrderBy(trace => trace.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                Upgrades = _upgrades.Values.OrderBy(trace => trace.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                AnimationComposites = _animationComposites.Values.OrderBy(trace => trace.PackagePath, StringComparer.OrdinalIgnoreCase).ToList(),
                Diagnostics = _diagnostics,
            };
        }

        private List<CharacterDcmdTrace> BuildDcmds()
        {
            var output = new List<CharacterDcmdTrace>();
            foreach (var asset in _context.Assets.Where(asset =>
                         UnrealPathUtil.AssetName(asset.PackagePath).StartsWith("DA_DCMD_", StringComparison.OrdinalIgnoreCase)))
            {
                output.Add(ReadDcmd(asset));
            }
            return output;
        }

        private CharacterDcmdTrace ReadDcmd(AssetRecord record)
        {
            var trace = new CharacterDcmdTrace
            {
                PackagePath = record.PackagePath,
                SourceMount = record.MountRoot,
            };
            try
            {
                var asset = LoadTyped(record.FilePath);
                trace.IsReadable = true;
                trace.Pawn = ReadSoftReference(asset, record.PackagePath, "Pawn");
                trace.IsHumanoidPawn = IsHumanoidPackage(trace.Pawn?.PackagePath ?? "");
                trace.MenuActor = ReadSoftReference(asset, record.PackagePath, "MenuActor");
                trace.CinematicsActor = ReadSoftReference(asset, record.PackagePath, "CinematicsActor");
                trace.PawnTag = ReadGameplayTag(asset, "PawnTag");
                trace.IsPlayableMetadata = IsPlayablePawnTag(trace.PawnTag);
                trace.ProgressTag = ReadGameplayTag(asset, "ProgressTag");
                trace.EquipmentTypes = ReadSoftArray(asset, record.PackagePath, "EquipmentList", trace.Diagnostics);
                trace.UpgradeAssets = ReadSoftArray(asset, record.PackagePath, "UpgradeDataAssets", trace.Diagnostics);
                trace.UiMetadata = UniqueImportedReference(asset, record.PackagePath, "DA_UIMD_", trace.Diagnostics);
                trace.HasUntracedNestedPackageGraphs =
                    trace.MenuActor is not null ||
                    trace.CinematicsActor is not null ||
                    trace.EquipmentTypes.Any(reference => !reference.IsNull) ||
                    trace.UpgradeAssets.Any(reference => !reference.IsNull);

                foreach (var eta in trace.EquipmentTypes.Where(reference => !string.IsNullOrWhiteSpace(reference.PackagePath)))
                {
                    EnsureEquipmentType(eta.PackagePath);
                }
                foreach (var upgrade in trace.UpgradeAssets.Where(reference =>
                             !reference.IsNull && !string.IsNullOrWhiteSpace(reference.PackagePath)))
                {
                    EnsureUpgrade(upgrade.PackagePath);
                }
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "dcmd-unreadable",
                    record.PackagePath,
                    ex.Message));
            }
            return trace;
        }

        private CharacterPlayableVariantTrace BuildPlayableVariant(
            AssetRecord asset,
            IReadOnlyDictionary<string, List<CharacterDcmdTrace>> dcmdsByPawn)
        {
            var result = new CharacterPlayableVariantTrace
            {
                PackagePath = asset.PackagePath,
                SourceMount = asset.MountRoot,
                IsHumanoid = IsHumanoidPackage(asset.PackagePath),
                IsEquipmentControlledPawn = asset.PackagePath.Contains(
                    "/Characters/Creatures/RemoteKitten/",
                    StringComparison.OrdinalIgnoreCase),
            };

            var dependency = ResolveClassDependencies(asset.PackagePath);
            result.ClassChain = dependency.ClassChain;
            result.Diagnostics.AddRange(dependency.Diagnostics);
            if (dcmdsByPawn.TryGetValue(Normalize(asset.PackagePath), out var dcmds))
            {
                result.Dcmds = dcmds.Select(dcmd => MakeReference(
                    dcmd.PackagePath,
                    dcmd.PackagePath,
                    "Pawn",
                    CharacterTraceEvidenceKind.SerializedProperty,
                    -1)).ToList();
            }
            result.HasSerializedPlayableDcmdEvidence = result.Dcmds.Count > 0;
            if (result.IsHumanoid && !result.HasSerializedPlayableDcmdEvidence)
            {
                result.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Warning,
                    "playable-dcmd-evidence-missing",
                    asset.PackagePath,
                    "The *_Playable class has no DCMD whose serialized PawnTag and Pawn reference prove it is selectable."));
            }

            if (!result.IsEquipmentControlledPawn &&
                !string.IsNullOrWhiteSpace(dependency.RuntimeData?.PackagePath) &&
                !string.IsNullOrWhiteSpace(dependency.MontageComposite?.PackagePath) &&
                !string.IsNullOrWhiteSpace(dependency.LayerComposite?.PackagePath))
            {
                var profile = EnsureProfile(dependency);
                result.GameplayProfileId = profile.Id;
            }
            else if (!result.IsEquipmentControlledPawn)
            {
                result.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "incomplete-gameplay-anchor",
                    asset.PackagePath,
                    "The class chain did not prove an effective DPRD, MAS_Char, and LAS_Char."));
            }
            return result;
        }

        private ResolvedClassDependencies ResolveClassDependencies(string startPackage)
        {
            var result = new ResolvedClassDependencies();
            var current = Normalize(startPackage);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < MaximumClassDepth && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!visited.Add(current))
                {
                    result.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "class-cycle",
                        startPackage,
                        $"Class inheritance repeats {current}."));
                    break;
                }
                var node = ReadClassNode(current);
                result.ClassChain.Add(MakeReference(
                    current,
                    depth == 0 ? current : result.ClassChain[^1].PackagePath,
                    "SuperIndex",
                    depth == 0 ? CharacterTraceEvidenceKind.FilenameConvention : CharacterTraceEvidenceKind.SerializedClassParent,
                    depth));
                result.Diagnostics.AddRange(node.Diagnostics);

                PickEffective(
                    node.PackagePath,
                    node.RuntimeDataCandidates,
                    node.RuntimeDataExplicitNull,
                    node.RuntimeDataUnresolved,
                    "DPRD",
                    depth,
                    ref result.RuntimeData,
                    ambiguousRoles,
                    result.Diagnostics);
                PickEffective(
                    node.PackagePath,
                    node.MontageCandidates,
                    node.MontageExplicitNull,
                    node.MontageUnresolved,
                    "MAS_Char",
                    depth,
                    ref result.MontageComposite,
                    ambiguousRoles,
                    result.Diagnostics);
                PickEffective(
                    node.PackagePath,
                    node.LayerCandidates,
                    node.LayerExplicitNull,
                    node.LayerUnresolved,
                    "LAS_Char",
                    depth,
                    ref result.LayerComposite,
                    ambiguousRoles,
                    result.Diagnostics);

                current = node.ParentPackage;
            }
            if (!string.IsNullOrWhiteSpace(current) && visited.Count >= MaximumClassDepth)
            {
                result.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "class-depth",
                    startPackage,
                    $"Class inheritance exceeded {MaximumClassDepth} packages."));
            }
            return result;
        }

        private void PickEffective(
            string sourcePackage,
            IReadOnlyList<string> candidates,
            bool hasExplicitNull,
            bool hasUnresolvedValue,
            string role,
            int depth,
            ref CharacterTraceReference? selected,
            ISet<string> ambiguousRoles,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            if (selected is not null || ambiguousRoles.Contains(role)) return;
            if (BlocksInheritedDependency(hasExplicitNull, hasUnresolvedValue))
            {
                ambiguousRoles.Add(role);
                if (hasExplicitNull)
                {
                    diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Information,
                        "class-anchor-explicit-null",
                        sourcePackage,
                        $"The class explicitly clears {role}; inherited values are not used."));
                }
                return;
            }
            var selection = ClassifyDependencyLevel(candidates);
            if (selection.IsAmbiguous)
            {
                ambiguousRoles.Add(role);
                diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "ambiguous-" + role.ToLowerInvariant(),
                    sourcePackage,
                    $"One class level imports multiple {role} candidates: {string.Join(", ", candidates)}."));
                return;
            }
            if (string.IsNullOrWhiteSpace(selection.PackagePath)) return;
            selected = MakeReference(
                selection.PackagePath,
                sourcePackage,
                role switch
                {
                    "DPRD" => "DinnerPawnRuntimeData",
                    "MAS_Char" => "AnimSet",
                    "LAS_Char" => "LayerSet",
                    _ => role,
                },
                depth == 0
                    ? CharacterTraceEvidenceKind.SerializedProperty
                    : CharacterTraceEvidenceKind.InheritedSerializedProperty,
                depth);
        }

        private ClassNode ReadClassNode(string packagePath)
        {
            packagePath = Normalize(packagePath);
            if (_classNodes.TryGetValue(packagePath, out var cached)) return cached;

            var node = new ClassNode { PackagePath = packagePath };
            _classNodes[packagePath] = node;
            if (!TryAsset(packagePath, out var record))
            {
                node.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Warning,
                    "class-package-missing",
                    packagePath,
                    "The referenced parent class package is not present in this extraction."));
                return node;
            }

            try
            {
                // Class CDO properties are the authority for gameplay anchors. Do not use the
                // preload-skipping mode here: UAssetAPI can otherwise leave some generated CDOs
                // as raw exports even though their typed properties are present on disk.
                var asset = new UAsset(
                    record.FilePath,
                    EngineVersion.VER_UE5_6,
                    _mappings,
                    CustomSerializationFlags.None);
                var className = UnrealPathUtil.AssetName(packagePath) + "_C";
                var generatedClass = asset.Exports.FirstOrDefault(export =>
                    export.ObjectName.ToString().Equals(className, StringComparison.OrdinalIgnoreCase));
                if (generatedClass is null)
                {
                    node.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "generated-class-missing",
                        packagePath,
                        $"The package has no {className} export."));
                }
                else if (generatedClass.SuperIndex.IsImport())
                {
                    node.ParentPackage = ResolveObjectPackage(asset, generatedClass.SuperIndex);
                }
                var cdoName = "Default__" + className;
                var cdoExport = asset.Exports.FirstOrDefault(export =>
                    export.ObjectName.ToString().Equals(cdoName, StringComparison.OrdinalIgnoreCase));
                if (cdoExport is null)
                {
                    node.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Information,
                        "class-cdo-absent",
                        packagePath,
                        $"The package has no {cdoName} export; gameplay anchors inherit from its serialized parent class."));
                }
                else if (cdoExport is not NormalExport cdo)
                {
                    node.RuntimeDataUnresolved = true;
                    node.MontageUnresolved = true;
                    node.LayerUnresolved = true;
                    node.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "class-cdo-untyped",
                        packagePath,
                        $"{cdoName} exists but its properties could not be typed, so local gameplay-anchor overrides are unknown."));
                }
                else
                {
                    var runtime = ReadClassAnchorCandidates(
                        asset,
                        cdo,
                        "DinnerPawnRuntimeData",
                        packagePath,
                        node.Diagnostics);
                    node.RuntimeDataCandidates = runtime.Packages;
                    node.RuntimeDataExplicitNull = runtime.HasExplicitNull;
                    node.RuntimeDataUnresolved = runtime.HasUnresolvedValue;
                    var montage = ReadClassAnchorCandidates(
                        asset,
                        cdo,
                        "AnimSet",
                        packagePath,
                        node.Diagnostics);
                    node.MontageCandidates = montage.Packages;
                    node.MontageExplicitNull = montage.HasExplicitNull;
                    node.MontageUnresolved = montage.HasUnresolvedValue;
                    var layer = ReadClassAnchorCandidates(
                        asset,
                        cdo,
                        "LayerSet",
                        packagePath,
                        node.Diagnostics);
                    node.LayerCandidates = layer.Packages;
                    node.LayerExplicitNull = layer.HasExplicitNull;
                    node.LayerUnresolved = layer.HasUnresolvedValue;
                }
            }
            catch (Exception ex)
            {
                node.RuntimeDataUnresolved = true;
                node.MontageUnresolved = true;
                node.LayerUnresolved = true;
                node.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "class-unreadable",
                    packagePath,
                    ex.Message));
            }
            return node;
        }

        private static ClassAnchorReadResult ReadClassAnchorCandidates(
            UAsset asset,
            NormalExport cdo,
            string propertyName,
            string sourcePackage,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            var properties = cdo.Data.Where(property => property.Name.ToString().Equals(
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (properties.Count == 0) return new ClassAnchorReadResult([], false, false);
            var packages = new List<string>();
            var hasExplicitNull = false;
            var hasUnresolvedValue = false;
            foreach (var property in properties)
            {
                if (property is ObjectPropertyData reference && reference.Value.IsNull())
                {
                    hasExplicitNull = true;
                    continue;
                }
                if (property is ObjectPropertyData imported && imported.Value.IsImport())
                {
                    var package = ResolveObjectPackage(asset, imported.Value);
                    if (!string.IsNullOrWhiteSpace(package))
                    {
                        packages.Add(package);
                    }
                    else
                    {
                        hasUnresolvedValue = true;
                        diagnostics.Add(Diagnostic(
                            CharacterTraceDiagnosticSeverity.Error,
                            "class-anchor-unresolved",
                            sourcePackage,
                            $"{propertyName} is imported, but its package could not be resolved."));
                    }
                    continue;
                }
                hasUnresolvedValue = true;
                diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "class-anchor-unresolved",
                    sourcePackage,
                    $"{propertyName} is not an imported object reference."));
            }
            if (hasExplicitNull && packages.Count > 0)
            {
                diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "class-anchor-ambiguous",
                    sourcePackage,
                    $"{propertyName} contains both a null clear and an imported value."));
            }
            return new ClassAnchorReadResult(packages, hasExplicitNull, hasUnresolvedValue);
        }

        private CharacterGameplayProfileTrace EnsureProfile(ResolvedClassDependencies dependency)
        {
            var dprd = Normalize(dependency.RuntimeData?.PackagePath);
            var key = GameplayProfileKey(
                dprd,
                dependency.MontageComposite?.PackagePath ?? "",
                dependency.LayerComposite?.PackagePath ?? "");
            if (_profiles.TryGetValue(key, out var existing)) return existing;

            var profile = new CharacterGameplayProfileTrace
            {
                Id = key,
                RuntimeData = dependency.RuntimeData!,
                MontageComposite = dependency.MontageComposite!,
                LayerComposite = dependency.LayerComposite!,
            };
            _profiles[key] = profile;
            ReadDprd(profile);
            EnsureAnimationComposite(profile.MontageComposite.PackagePath, "Montage");
            EnsureAnimationComposite(profile.LayerComposite.PackagePath, "Layer");
            return profile;
        }

        private void ReadDprd(CharacterGameplayProfileTrace profile)
        {
            if (!TryAsset(profile.RuntimeData.PackagePath, out var record))
            {
                profile.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "dprd-missing",
                    profile.RuntimeData.PackagePath,
                    "The effective DinnerPawnRuntimeData asset is absent from the extraction."));
                return;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                profile.OrderedAbilitySets = ReadObjectArray(
                    asset,
                    profile.RuntimeData.PackagePath,
                    "AbilitySets",
                    profile.Diagnostics);
                profile.EquipmentUsesDefaultEmptyArray = FindArray(asset, "Equipment") is null;
                profile.OrderedEquipmentDefinitions = ReadObjectArray(
                    asset,
                    profile.RuntimeData.PackagePath,
                    "Equipment",
                    profile.Diagnostics,
                    propertyMayBeAbsent: true);
                if (profile.EquipmentUsesDefaultEmptyArray)
                {
                    profile.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Information,
                        "property-default-empty",
                        profile.RuntimeData.PackagePath,
                        "Equipment is not serialized on this DPRD, so the native class default is the empty array."));
                }
                foreach (var abilitySet in profile.OrderedAbilitySets.Where(reference =>
                             !string.IsNullOrWhiteSpace(reference.PackagePath)))
                {
                    EnsureAbilitySet(abilitySet.PackagePath);
                }
                foreach (var equipment in profile.OrderedEquipmentDefinitions.Where(reference =>
                             !string.IsNullOrWhiteSpace(reference.PackagePath)))
                {
                    EnsureEquipmentDefinition(equipment.PackagePath);
                }
            }
            catch (Exception ex)
            {
                profile.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "dprd-unreadable",
                    profile.RuntimeData.PackagePath,
                    ex.Message));
            }
        }

        private CharacterAbilitySetTrace EnsureAbilitySet(string packagePath)
        {
            packagePath = Normalize(packagePath);
            if (_abilitySets.TryGetValue(packagePath, out var existing)) return existing;
            var trace = new CharacterAbilitySetTrace { PackagePath = packagePath };
            _abilitySets[packagePath] = trace;
            if (!TryAsset(packagePath, out var record))
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Warning,
                    "ability-set-missing",
                    packagePath,
                    "The DPRD references this AbilitySet, but it is absent from the active extraction."));
                return trace;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                var export = asset.Exports.OfType<NormalExport>()
                    .FirstOrDefault(candidate =>
                        candidate.GetExportClassType()?.Value.Value.Equals(
                            "TtAbilitySet",
                            StringComparison.OrdinalIgnoreCase) == true ||
                        candidate.Data.Any(property => property.Name.ToString().StartsWith(
                            "Granted",
                            StringComparison.OrdinalIgnoreCase)))
                    ?? throw new InvalidDataException("Asset has no TtAbilitySet export.");
                trace.IsReadable = true;
                ReadAbilityGrantArray(asset, export, trace, "GrantedGameplayAbilities", "Ability", "Gameplay ability");
                ReadAbilityGrantArray(asset, export, trace, "GrantedGameplayEffects", "GameplayEffect", "Gameplay effect");
                ReadAbilityGrantArray(asset, export, trace, "GrantedAttributes", "AttributeSet", "Attribute set");
                ReadAbilityGrantArray(asset, export, trace, "GrantedGameplayData", "GameplayDataSet", "Gameplay data");
                ReadAbilityGrantArray(
                    asset,
                    export,
                    trace,
                    "GrantedGameplayCueNotifyActorData",
                    "GameplayCueNotifyActor",
                    "Actor gameplay cue");
                ReadAbilityGrantArray(
                    asset,
                    export,
                    trace,
                    "GrantedGameplayCueNotifyStaticData",
                    "GameplayCueNotifyStatic",
                    "Static gameplay cue");
                var accessory = export.Data.OfType<ObjectPropertyData>().FirstOrDefault(property =>
                    property.Name.ToString().Equals("AccessoryAnimGraphClass", StringComparison.OrdinalIgnoreCase));
                if (accessory is not null && !accessory.Value.IsNull())
                {
                    if (accessory.Value.IsImport())
                    {
                        trace.Grants.Add(new CharacterAbilityGrantTrace
                        {
                            Kind = "Accessory animation graph",
                            Reference = MakeReference(
                                ResolveObjectPackage(asset, accessory.Value),
                                trace.PackagePath,
                                "AccessoryAnimGraphClass",
                                CharacterTraceEvidenceKind.SerializedProperty,
                                -1,
                                accessory.Value.ToImport(asset).ObjectName.ToString()),
                        });
                    }
                    else
                    {
                        trace.Diagnostics.Add(Diagnostic(
                            CharacterTraceDiagnosticSeverity.Error,
                            "ability-grant-unresolved",
                            trace.PackagePath,
                            "AccessoryAnimGraphClass is not an imported object reference."));
                    }
                }
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "ability-set-unreadable",
                    packagePath,
                    ex.Message));
            }
            return trace;
        }

        private void ReadAbilityGrantArray(
            UAsset asset,
            NormalExport export,
            CharacterAbilitySetTrace trace,
            string arrayName,
            string referenceField,
            string kind)
        {
            var array = export.Data.OfType<ArrayPropertyData>().FirstOrDefault(property =>
                property.Name.ToString().Equals(arrayName, StringComparison.OrdinalIgnoreCase));
            if (array is null) return;
            for (var index = 0; index < array.Value.Length; index++)
            {
                if (array.Value[index] is not StructPropertyData entry)
                {
                    trace.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "ability-grant-unresolved",
                        trace.PackagePath,
                        $"{arrayName}[{index}] is not a struct entry."));
                    continue;
                }
                var reference = entry.Value.OfType<ObjectPropertyData>().FirstOrDefault(property =>
                    property.Name.ToString().Equals(referenceField, StringComparison.OrdinalIgnoreCase));
                if (reference is null || reference.Value.IsNull() || !reference.Value.IsImport())
                {
                    trace.Diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "ability-grant-unresolved",
                        trace.PackagePath,
                        $"{arrayName}[{index}].{referenceField} is not an imported object reference."));
                    continue;
                }
                var inputTag = entry.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(property => property.Name.ToString().Equals(
                        "InputTag",
                        StringComparison.OrdinalIgnoreCase))?
                    .Value.OfType<NamePropertyData>()
                    .FirstOrDefault(property => property.Name.ToString().Equals(
                        "TagName",
                        StringComparison.OrdinalIgnoreCase))?
                    .Value?.ToString() ?? "";
                trace.Grants.Add(new CharacterAbilityGrantTrace
                {
                    Kind = kind,
                    Reference = MakeReference(
                        ResolveObjectPackage(asset, reference.Value),
                        trace.PackagePath,
                        arrayName,
                        CharacterTraceEvidenceKind.SerializedOrderedArray,
                        index,
                        reference.Value.ToImport(asset).ObjectName.ToString()),
                    AbilityLevel = entry.Value.OfType<IntPropertyData>()
                        .FirstOrDefault(property => property.Name.ToString().Equals(
                            "AbilityLevel",
                            StringComparison.OrdinalIgnoreCase))?.Value,
                    EffectLevel = entry.Value.OfType<FloatPropertyData>()
                        .FirstOrDefault(property => property.Name.ToString().Equals(
                            "EffectLevel",
                            StringComparison.OrdinalIgnoreCase))?.Value,
                    InputTag = inputTag.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : inputTag,
                });
            }
        }

        private CharacterEquipmentDefinitionTrace EnsureEquipmentDefinition(string packagePath)
        {
            packagePath = Normalize(packagePath);
            if (_equipmentDefinitions.TryGetValue(packagePath, out var existing)) return existing;
            var trace = new CharacterEquipmentDefinitionTrace { PackagePath = packagePath };
            _equipmentDefinitions[packagePath] = trace;
            if (!TryAsset(packagePath, out var record))
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "equipment-definition-missing",
                    packagePath,
                    "The DPRD or ETA references an ED that is absent from the extraction."));
                return trace;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                trace.IsReadable = true;
                trace.AbilitySetsToGrant = ReadObjectArray(
                    asset,
                    packagePath,
                    "AbilitySetsToGrant",
                    trace.Diagnostics,
                    propertyMayBeAbsent: true);
                var references = new List<CharacterTraceReference>();
                foreach (var export in asset.Exports.OfType<NormalExport>())
                {
                    CollectPropertyReferences(asset, packagePath, export.Data, "", references);
                }
                var abilitySetPackages = trace.AbilitySetsToGrant.Select(reference => reference.PackagePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                trace.GameplayAbilities = references.Where(reference =>
                        UnrealPathUtil.AssetName(reference.PackagePath).StartsWith("GA_", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(ReferenceKey, StringComparer.OrdinalIgnoreCase).ToList();
                trace.SpawnedActors = references.Where(reference =>
                        UnrealPathUtil.AssetName(reference.PackagePath).StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(ReferenceKey, StringComparer.OrdinalIgnoreCase).ToList();
                trace.OtherReferencedPackages = references.Where(reference =>
                        !abilitySetPackages.Contains(reference.PackagePath) &&
                        !trace.GameplayAbilities.Any(item => SamePackage(item.PackagePath, reference.PackagePath)) &&
                        !trace.SpawnedActors.Any(item => SamePackage(item.PackagePath, reference.PackagePath)))
                    .DistinctBy(ReferenceKey, StringComparer.OrdinalIgnoreCase).ToList();
                trace.HasUntracedNestedPackageGraphs =
                    trace.AbilitySetsToGrant.Count > 0 ||
                    trace.GameplayAbilities.Count > 0 ||
                    trace.SpawnedActors.Count > 0 ||
                    trace.OtherReferencedPackages.Count > 0;
                foreach (var ownedSet in trace.AbilitySetsToGrant.Where(reference =>
                             !string.IsNullOrWhiteSpace(reference.PackagePath)))
                {
                    EnsureAbilitySet(ownedSet.PackagePath);
                }
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "equipment-definition-unreadable",
                    packagePath,
                    ex.Message));
            }
            return trace;
        }

        private CharacterEquipmentTypeTrace EnsureEquipmentType(string packagePath)
        {
            packagePath = Normalize(packagePath);
            if (_equipmentTypes.TryGetValue(packagePath, out var existing)) return existing;
            var trace = new CharacterEquipmentTypeTrace { PackagePath = packagePath };
            _equipmentTypes[packagePath] = trace;
            if (!TryAsset(packagePath, out var record))
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "equipment-type-missing",
                    packagePath,
                    "The DCMD references this ETA, but it is absent from the extraction."));
                return trace;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                trace.IsReadable = true;
                trace.EquipmentDefinition = ReadAnyReference(asset, packagePath, "Equipment", trace.Diagnostics);
                trace.EquipmentTag = ReadGameplayTag(asset, "EquipmentTag");
                if (!string.IsNullOrWhiteSpace(trace.EquipmentDefinition?.PackagePath))
                {
                    EnsureEquipmentDefinition(trace.EquipmentDefinition.PackagePath);
                }
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "equipment-type-unreadable",
                    packagePath,
                    ex.Message));
            }
            return trace;
        }

        private CharacterUpgradeTrace EnsureUpgrade(string packagePath)
        {
            packagePath = Normalize(packagePath);
            if (_upgrades.TryGetValue(packagePath, out var existing)) return existing;
            var trace = new CharacterUpgradeTrace { PackagePath = packagePath };
            _upgrades[packagePath] = trace;
            if (!TryAsset(packagePath, out var record))
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "upgrade-missing",
                    packagePath,
                    "The DCMD references an upgrade asset absent from the extraction."));
                return trace;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                trace.IsReadable = true;
                var references = new List<CharacterTraceReference>();
                foreach (var export in asset.Exports.OfType<NormalExport>())
                {
                    CollectPropertyReferences(asset, packagePath, export.Data, "", references);
                }
                trace.DirectReferences = references
                    .DistinctBy(ReferenceKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                trace.HasUntracedNestedPackageGraphs = trace.DirectReferences.Count > 0;
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "upgrade-unreadable",
                    packagePath,
                    ex.Message));
            }
            return trace;
        }

        private CharacterAnimationCompositeTrace EnsureAnimationComposite(string packagePath, string kind)
        {
            packagePath = Normalize(packagePath);
            if (_animationComposites.TryGetValue(packagePath, out var existing)) return existing;
            var trace = new CharacterAnimationCompositeTrace { PackagePath = packagePath, Kind = kind };
            _animationComposites[packagePath] = trace;
            if (!TryAsset(packagePath, out var record))
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "animation-composite-missing",
                    packagePath,
                    "The effective character animation composite is absent from the extraction."));
                return trace;
            }
            try
            {
                var asset = LoadTyped(record.FilePath);
                trace.IsReadable = true;
                trace.OrderedParents = ReadObjectArray(
                    asset,
                    packagePath,
                    "ParentSetsArray",
                    trace.Diagnostics);
                trace.HasOrderedParentArray = !trace.Diagnostics.Any(diagnostic =>
                    diagnostic.Code is "property-missing" or "invalid-array-entry");
            }
            catch (Exception ex)
            {
                trace.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "animation-composite-unreadable",
                    packagePath,
                    ex.Message));
            }
            return trace;
        }

        private void FinalizeProfile(CharacterGameplayProfileTrace profile)
        {
            profile.HasPlayableCore = profile.OrderedAbilitySets.Any(reference =>
                UnrealPathUtil.AssetName(reference.PackagePath)
                    .Equals("AS_PlayableCoreAbilitySet", StringComparison.OrdinalIgnoreCase));
            var abilityTraces = profile.OrderedAbilitySets
                .Select(reference => _abilitySets.GetValueOrDefault(Normalize(reference.PackagePath)))
                .Where(trace => trace is not null)
                .Cast<CharacterAbilitySetTrace>()
                .ToList();
            profile.CombatAbilitySetPackages = abilityTraces.Where(trace => trace.Grants.Any(grant =>
                {
                    var grantName = UnrealPathUtil.AssetName(grant.Reference.PackagePath);
                    return grantName.StartsWith("GA_MeleeAttack", StringComparison.OrdinalIgnoreCase) &&
                           grantName.EndsWith("GTSM", StringComparison.OrdinalIgnoreCase);
                }))
                .Select(trace => trace.PackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.CombatTypeEffectPackages = abilityTraces.SelectMany(trace => trace.Grants)
                .Where(grant => UnrealPathUtil.AssetName(grant.Reference.PackagePath)
                    .StartsWith("GE_CombatType_", StringComparison.OrdinalIgnoreCase))
                .Select(grant => Normalize(grant.Reference.PackagePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.HeldItemAbilityPackages = abilityTraces.SelectMany(trace => trace.Grants)
                .Where(grant => UnrealPathUtil.AssetName(grant.Reference.PackagePath)
                    .StartsWith("GA_Item_", StringComparison.OrdinalIgnoreCase))
                .Select(grant => Normalize(grant.Reference.PackagePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.GrappleDataSetPackages = profile.OrderedAbilitySets
                .Where(reference => UnrealPathUtil.AssetName(reference.PackagePath)
                    .StartsWith("AS_GrappleData", StringComparison.OrdinalIgnoreCase))
                .Select(reference => Normalize(reference.PackagePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.IsPlayerProfile = profile.HasPlayableCore && profile.CombatAbilitySetPackages.Count == 1;

            if (profile.CombatAbilitySetPackages.Count != 1)
            {
                profile.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "combat-cardinality",
                    profile.Id,
                    $"Expected one melee-controller AbilitySet, traced {profile.CombatAbilitySetPackages.Count}."));
            }
            if (profile.GrappleDataSetPackages.Count > 1)
            {
                profile.Diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "grapple-cardinality",
                    profile.Id,
                    $"Expected at most one grapple-data AbilitySet, traced {profile.GrappleDataSetPackages.Count}."));
            }

            profile.IsDependencyClosureComplete =
                ReferenceResolved(profile.RuntimeData) &&
                AnimationClosure(profile.MontageComposite) &&
                AnimationClosure(profile.LayerComposite) &&
                profile.OrderedAbilitySets.All(reference =>
                    ReferenceResolved(reference) && (reference.IsNull || AbilitySetClosure(reference.PackagePath))) &&
                profile.OrderedEquipmentDefinitions.All(reference =>
                    ReferenceResolved(reference) &&
                    (reference.IsNull || EquipmentDefinitionClosure(reference.PackagePath)));
            profile.HasUntracedNestedPackageGraphs =
                abilityTraces.Any(trace => trace.Grants.Count > 0) ||
                profile.OrderedEquipmentDefinitions.Any(reference => !reference.IsNull) ||
                _animationComposites.GetValueOrDefault(Normalize(profile.MontageComposite.PackagePath))?
                    .OrderedParents.Any(reference => !reference.IsNull) == true ||
                _animationComposites.GetValueOrDefault(Normalize(profile.LayerComposite.PackagePath))?
                    .OrderedParents.Any(reference => !reference.IsNull) == true;
            profile.IsStructurallyCertified = profile.IsDependencyClosureComplete &&
                ComputeStructuralCertificate(profile) &&
                _animationComposites.GetValueOrDefault(Normalize(profile.MontageComposite.PackagePath))
                    is { IsReadable: true, HasOrderedParentArray: true } &&
                _animationComposites.GetValueOrDefault(Normalize(profile.LayerComposite.PackagePath))
                    is { IsReadable: true, HasOrderedParentArray: true };
        }

        private bool ComputeDcmdClosure(CharacterDcmdTrace dcmd)
        {
            if (!dcmd.IsReadable || HasErrors(dcmd.Diagnostics)) return false;
            if (dcmd.Pawn is not null && !ReferenceResolved(dcmd.Pawn)) return false;
            if (dcmd.MenuActor is not null && !ReferenceResolved(dcmd.MenuActor)) return false;
            if (dcmd.CinematicsActor is not null && !ReferenceResolved(dcmd.CinematicsActor)) return false;
            if (dcmd.UiMetadata is not null && !ReferenceResolved(dcmd.UiMetadata)) return false;
            if (dcmd.PawnClassChain.Any(reference => !ReferenceResolved(reference))) return false;
            if (dcmd.UpgradeAssets.Any(reference => !ReferenceResolved(reference))) return false;
            if (dcmd.EquipmentTypes.Any(reference =>
                    !ReferenceResolved(reference) ||
                    (!reference.IsNull && !EquipmentTypeClosure(reference.PackagePath))))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(dcmd.GameplayProfileId)) return !dcmd.IsHumanoidPawn;
            return _profiles.GetValueOrDefault(dcmd.GameplayProfileId)?.IsDependencyClosureComplete == true;
        }

        private bool ComputeVariantClosure(CharacterPlayableVariantTrace variant)
        {
            if (HasErrors(variant.Diagnostics) ||
                !variant.HasSerializedPlayableDcmdEvidence ||
                variant.ClassChain.Any(reference => !ReferenceResolved(reference)))
            {
                return false;
            }
            if (!_profiles.TryGetValue(variant.GameplayProfileId, out var profile) ||
                !profile.IsDependencyClosureComplete)
            {
                return false;
            }
            return variant.Dcmds.All(reference =>
                ReferenceResolved(reference) &&
                _dcmdByPackage.GetValueOrDefault(Normalize(reference.PackagePath))
                    is { IsDependencyClosureComplete: true });
        }

        private bool AbilitySetClosure(string packagePath) =>
            _abilitySets.GetValueOrDefault(Normalize(packagePath)) is { IsReadable: true } trace &&
            !HasErrors(trace.Diagnostics) &&
            trace.Grants.All(grant => ReferenceResolved(grant.Reference));

        private bool EquipmentDefinitionClosure(string packagePath) =>
            _equipmentDefinitions.GetValueOrDefault(Normalize(packagePath)) is { IsReadable: true } trace &&
            !HasErrors(trace.Diagnostics) &&
            trace.AbilitySetsToGrant.All(reference =>
                ReferenceResolved(reference) && (reference.IsNull || AbilitySetClosure(reference.PackagePath))) &&
            trace.GameplayAbilities.All(ReferenceResolved) &&
            trace.SpawnedActors.All(ReferenceResolved) &&
            trace.OtherReferencedPackages.All(ReferenceResolved);

        private bool EquipmentTypeClosure(string packagePath) =>
            _equipmentTypes.GetValueOrDefault(Normalize(packagePath)) is { IsReadable: true } trace &&
            !HasErrors(trace.Diagnostics) &&
            trace.EquipmentDefinition is not null &&
            ReferenceResolved(trace.EquipmentDefinition) &&
            EquipmentDefinitionClosure(trace.EquipmentDefinition.PackagePath);

        private bool AnimationClosure(CharacterTraceReference root)
        {
            if (!ReferenceResolved(root)) return false;
            return _animationComposites.GetValueOrDefault(Normalize(root.PackagePath))
                       is { IsReadable: true, HasOrderedParentArray: true } trace &&
                   !HasErrors(trace.Diagnostics) &&
                   trace.OrderedParents.All(ReferenceResolved);
        }

        private static bool HasErrors(IEnumerable<CharacterTraceDiagnostic> diagnostics) =>
            diagnostics.Any(diagnostic => diagnostic.Severity == CharacterTraceDiagnosticSeverity.Error);

        private static bool ReferenceResolved(CharacterTraceReference reference) =>
            reference.IsNull || reference.IsNativeReference || reference.TargetExists;

        private UAsset LoadTyped(string path) => new(
            path,
            EngineVersion.VER_UE5_6,
            _mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);

        private bool TryAsset(string packagePath, out AssetRecord record) =>
            _byPackage.TryGetValue(Normalize(packagePath), out record!);

        private CharacterTraceReference MakeReference(
            string? packagePath,
            string sourcePackage,
            string sourceProperty,
            CharacterTraceEvidenceKind evidence,
            int index,
            string? objectName = null)
        {
            var package = Normalize(packagePath);
            var nativeReference = package.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase);
            return new CharacterTraceReference
            {
                PackagePath = package,
                ObjectName = string.IsNullOrWhiteSpace(objectName)
                    ? UnrealPathUtil.AssetName(package)
                    : objectName!,
                SourcePackage = Normalize(sourcePackage),
                SourceProperty = sourceProperty,
                Index = index,
                Evidence = evidence,
                TargetExists = nativeReference ||
                               (!string.IsNullOrWhiteSpace(package) && _byPackage.ContainsKey(package)),
                IsNativeReference = nativeReference,
            };
        }

        private CharacterTraceReference? ReadSoftReference(UAsset asset, string sourcePackage, string propertyName)
        {
            var reference = NativeAssetTextPatch.GetSoftReference(asset, propertyName);
            return reference is null || IsNullSoftReference(reference.Value.PackageName, reference.Value.AssetName)
                ? null
                : MakeReference(
                    reference.Value.PackageName,
                    sourcePackage,
                    propertyName,
                    CharacterTraceEvidenceKind.SerializedProperty,
                    -1,
                    reference.Value.AssetName);
        }

        private List<CharacterTraceReference> ReadSoftArray(
            UAsset asset,
            string sourcePackage,
            string propertyName,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            var array = FindArray(asset, propertyName);
            if (array is null) return new List<CharacterTraceReference>();
            var output = new List<CharacterTraceReference>();
            for (var index = 0; index < array.Value.Length; index++)
            {
                if (array.Value[index] is SoftObjectPropertyData soft)
                {
                    var packageName = soft.Value.AssetPath.PackageName.ToString();
                    var assetName = soft.Value.AssetPath.AssetName.ToString();
                    if (IsNullSoftReference(packageName, assetName))
                    {
                        output.Add(new CharacterTraceReference
                        {
                            SourcePackage = sourcePackage,
                            SourceProperty = propertyName,
                            Index = index,
                            Evidence = CharacterTraceEvidenceKind.SerializedNullArrayEntry,
                            IsNull = true,
                        });
                    }
                    else
                    {
                        output.Add(MakeReference(
                            packageName,
                            sourcePackage,
                            propertyName,
                            CharacterTraceEvidenceKind.SerializedOrderedArray,
                            index,
                            assetName));
                    }
                }
                else
                {
                    diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "invalid-array-entry",
                        sourcePackage,
                        $"{propertyName}[{index}] is not a soft object reference."));
                }
            }
            return output;
        }

        private List<CharacterTraceReference> ReadObjectArray(
            UAsset asset,
            string sourcePackage,
            string propertyName,
            ICollection<CharacterTraceDiagnostic> diagnostics,
            bool propertyMayBeAbsent = false)
        {
            var array = FindArray(asset, propertyName);
            if (array is null)
            {
                if (!propertyMayBeAbsent)
                {
                    diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "property-missing",
                        sourcePackage,
                        $"The asset has no readable {propertyName} array."));
                }
                return new List<CharacterTraceReference>();
            }
            var output = new List<CharacterTraceReference>();
            for (var index = 0; index < array.Value.Length; index++)
            {
                if (array.Value[index] is ObjectPropertyData property && property.Value.IsImport())
                {
                    var package = ResolveObjectPackage(asset, property.Value);
                    var objectName = property.Value.ToImport(asset).ObjectName.ToString();
                    output.Add(MakeReference(
                        package,
                        sourcePackage,
                        propertyName,
                        CharacterTraceEvidenceKind.SerializedOrderedArray,
                        index,
                        objectName));
                }
                else if (array.Value[index] is ObjectPropertyData nullProperty && nullProperty.Value.IsNull())
                {
                    output.Add(new CharacterTraceReference
                    {
                        SourcePackage = sourcePackage,
                        SourceProperty = propertyName,
                        Index = index,
                        Evidence = CharacterTraceEvidenceKind.SerializedNullArrayEntry,
                        IsNull = true,
                    });
                }
                else
                {
                    diagnostics.Add(Diagnostic(
                        CharacterTraceDiagnosticSeverity.Error,
                        "invalid-array-entry",
                        sourcePackage,
                        $"{propertyName}[{index}] is not an imported object reference."));
                }
            }
            return output;
        }

        private CharacterTraceReference? ReadAnyReference(
            UAsset asset,
            string sourcePackage,
            string propertyName,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            foreach (var export in asset.Exports.OfType<NormalExport>())
            {
                var property = export.Data.FirstOrDefault(item =>
                    item.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
                switch (property)
                {
                    case ObjectPropertyData objectProperty when objectProperty.Value.IsImport():
                        return MakeReference(
                            ResolveObjectPackage(asset, objectProperty.Value),
                            sourcePackage,
                            propertyName,
                            CharacterTraceEvidenceKind.SerializedProperty,
                            -1,
                            objectProperty.Value.ToImport(asset).ObjectName.ToString());
                    case SoftObjectPropertyData soft when !IsNullSoftReference(
                        soft.Value.AssetPath.PackageName.ToString(),
                        soft.Value.AssetPath.AssetName.ToString()):
                        return MakeReference(
                            soft.Value.AssetPath.PackageName.ToString(),
                            sourcePackage,
                            propertyName,
                            CharacterTraceEvidenceKind.SerializedProperty,
                            -1,
                            soft.Value.AssetPath.AssetName.ToString());
                }
            }
            diagnostics.Add(Diagnostic(
                CharacterTraceDiagnosticSeverity.Error,
                "property-missing",
                sourcePackage,
                $"The asset has no readable {propertyName} reference."));
            return null;
        }

        private CharacterTraceReference? UniqueImportedReference(
            UAsset asset,
            string sourcePackage,
            string assetPrefix,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            var candidates = ImportedPackages(asset).Where(package =>
                    UnrealPathUtil.AssetName(package).StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 1)
            {
                return MakeReference(
                    candidates[0],
                    sourcePackage,
                    "Imports",
                    CharacterTraceEvidenceKind.SerializedImport,
                    -1);
            }
            if (candidates.Count > 1)
            {
                diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Warning,
                    "ambiguous-import",
                    sourcePackage,
                    $"Multiple {assetPrefix} imports are present: {string.Join(", ", candidates)}."));
            }
            return null;
        }

        private void CollectPropertyReferences(
            UAsset asset,
            string sourcePackage,
            IEnumerable<PropertyData> properties,
            string parentPath,
            ICollection<CharacterTraceReference> output)
        {
            foreach (var property in properties)
            {
                var name = property.Name.ToString();
                var path = string.IsNullOrWhiteSpace(parentPath) ? name : parentPath + "." + name;
                switch (property)
                {
                    case ObjectPropertyData objectProperty when objectProperty.Value.IsImport():
                        output.Add(MakeReference(
                            ResolveObjectPackage(asset, objectProperty.Value),
                            sourcePackage,
                            path,
                            CharacterTraceEvidenceKind.SerializedProperty,
                            -1,
                            objectProperty.Value.ToImport(asset).ObjectName.ToString()));
                        break;
                    case SoftObjectPropertyData soft when !IsNullSoftReference(
                        soft.Value.AssetPath.PackageName.ToString(),
                        soft.Value.AssetPath.AssetName.ToString()):
                        output.Add(MakeReference(
                            soft.Value.AssetPath.PackageName.ToString(),
                            sourcePackage,
                            path,
                            CharacterTraceEvidenceKind.SerializedProperty,
                            -1,
                            soft.Value.AssetPath.AssetName.ToString()));
                        break;
                    case StructPropertyData structure:
                        CollectPropertyReferences(asset, sourcePackage, structure.Value, path, output);
                        break;
                    case ArrayPropertyData array:
                        for (var index = 0; index < array.Value.Length; index++)
                        {
                            CollectPropertyReferences(asset, sourcePackage, [array.Value[index]], $"{path}[{index}]", output);
                        }
                        break;
                    case MapPropertyData map:
                        CollectPropertyReferences(asset, sourcePackage, map.Value.Keys, path + ".Key", output);
                        CollectPropertyReferences(asset, sourcePackage, map.Value.Values, path + ".Value", output);
                        break;
                }
            }
        }

        private CharacterTraceReference? UniqueClassDependency(
            string sourcePackage,
            IEnumerable<string> candidates,
            string role,
            CharacterTraceEvidenceKind evidence,
            ICollection<CharacterTraceDiagnostic> diagnostics)
        {
            var distinct = candidates.Select(Normalize).Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 1)
            {
                return MakeReference(distinct[0], sourcePackage, "Imports", evidence, -1);
            }
            if (distinct.Count > 1)
            {
                diagnostics.Add(Diagnostic(
                    CharacterTraceDiagnosticSeverity.Error,
                    "ambiguous-" + role.ToLowerInvariant(),
                    sourcePackage,
                    $"The class imports multiple {role} candidates: {string.Join(", ", distinct)}."));
            }
            return null;
        }

        private static ArrayPropertyData? FindArray(UAsset asset, string propertyName) =>
            asset.Exports.OfType<NormalExport>().SelectMany(export => export.Data)
                .OfType<ArrayPropertyData>()
                .FirstOrDefault(property => property.Name.ToString()
                    .Equals(propertyName, StringComparison.OrdinalIgnoreCase));

        private static string ReadGameplayTag(UAsset asset, string propertyName)
        {
            var property = asset.Exports.OfType<NormalExport>().SelectMany(export => export.Data)
                .OfType<StructPropertyData>()
                .FirstOrDefault(item => item.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            return property?.Value.OfType<NamePropertyData>()
                .FirstOrDefault(item => item.Name.ToString().Equals("TagName", StringComparison.OrdinalIgnoreCase))
                ?.Value.ToString() ?? "";
        }

        private static List<string> ImportedPackages(UAsset asset) => asset.Imports
            .Where(import => import.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase))
            .Select(import => Normalize(import.ObjectName.ToString()))
            .Where(ExtractedPackagePathService.IsContentPackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        private static string ResolveObjectPackage(UAsset asset, FPackageIndex index)
        {
            if (!index.IsImport()) return "";
            var current = index;
            var seen = new HashSet<int>();
            while (current.IsImport() && seen.Add(current.Index))
            {
                var import = current.ToImport(asset);
                if (import.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase))
                {
                    return Normalize(import.ObjectName.ToString());
                }
                current = import.OuterIndex;
            }
            return "";
        }

        private static string ReferenceKey(CharacterTraceReference reference) =>
            reference.PackagePath + "|" + reference.SourceProperty;

        private sealed class ClassNode
        {
            public string PackagePath { get; set; } = "";
            public string ParentPackage { get; set; } = "";
            public List<string> RuntimeDataCandidates { get; set; } = new();
            public bool RuntimeDataExplicitNull { get; set; }
            public bool RuntimeDataUnresolved { get; set; }
            public List<string> MontageCandidates { get; set; } = new();
            public bool MontageExplicitNull { get; set; }
            public bool MontageUnresolved { get; set; }
            public List<string> LayerCandidates { get; set; } = new();
            public bool LayerExplicitNull { get; set; }
            public bool LayerUnresolved { get; set; }
            public List<CharacterTraceDiagnostic> Diagnostics { get; set; } = new();
        }

        private sealed record ClassAnchorReadResult(
            List<string> Packages,
            bool HasExplicitNull,
            bool HasUnresolvedValue);

        private sealed class ResolvedClassDependencies
        {
            public CharacterTraceReference? RuntimeData;
            public CharacterTraceReference? MontageComposite;
            public CharacterTraceReference? LayerComposite;
            public List<CharacterTraceReference> ClassChain { get; } = new();
            public List<CharacterTraceDiagnostic> Diagnostics { get; } = new();
        }
    }

    private sealed class BuildContext
    {
        public required string ContentRoot { get; init; }
        public required string MappingsPath { get; init; }
        public required List<ExtractedPackagePathService.Mount> Mounts { get; init; }
        public required List<AssetRecord> Assets { get; init; }
        public required string Fingerprint { get; init; }

        public static BuildContext Create(string extractedContentRoot, string mappingsPath)
        {
            if (string.IsNullOrWhiteSpace(extractedContentRoot) || !Directory.Exists(extractedContentRoot))
            {
                throw new DirectoryNotFoundException($"Extracted Content root not found: {extractedContentRoot}");
            }
            if (string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
            {
                throw new FileNotFoundException("A UE 5.6 .usmap is required to trace character dependencies.", mappingsPath);
            }
            var contentRoot = Path.GetFullPath(extractedContentRoot.Trim());
            var mappings = Path.GetFullPath(mappingsPath.Trim());
            var mounts = ExtractedPackagePathService.EnumerateMounts(contentRoot).ToList();
            var assets = new List<AssetRecord>();
            foreach (var mount in mounts)
            {
                foreach (var file in Directory.EnumerateFiles(mount.ContentRoot, "*.uasset", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(mount.ContentRoot, file).Replace('\\', '/');
                    var package = mount.PackageRoot + "/" + relative[..^".uasset".Length];
                    assets.Add(new AssetRecord(Normalize(package), Path.GetFullPath(file), mount.PackageRoot));
                }
            }
            assets.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.PackagePath, right.PackagePath));
            return new BuildContext
            {
                ContentRoot = contentRoot,
                MappingsPath = mappings,
                Mounts = mounts,
                Assets = assets,
                Fingerprint = ComputeFingerprint(contentRoot, mappings, mounts, assets),
            };
        }

        private static string ComputeFingerprint(
            string contentRoot,
            string mappingsPath,
            IReadOnlyList<ExtractedPackagePathService.Mount> mounts,
            IReadOnlyList<AssetRecord> assets)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(bytes);
                hash.AppendData([0]);
            }
            Add("character-dependency-trace");
            Add(CurrentSchemaVersion.ToString());
            Add(contentRoot.ToUpperInvariant());
            var mappings = new FileInfo(mappingsPath);
            Add(mappings.FullName.ToUpperInvariant());
            Add(mappings.Length.ToString());
            Add(mappings.LastWriteTimeUtc.Ticks.ToString());
            foreach (var mount in mounts)
            {
                Add(mount.PackageRoot.ToUpperInvariant());
                Add(mount.ContentRoot.ToUpperInvariant());
            }
            foreach (var asset in assets)
            {
                var info = new FileInfo(asset.FilePath);
                Add(asset.PackagePath.ToUpperInvariant());
                Add(info.Length.ToString());
                Add(info.LastWriteTimeUtc.Ticks.ToString());
                foreach (var extension in new[] { ".uexp", ".ubulk" })
                {
                    var sidecar = Path.ChangeExtension(asset.FilePath, extension);
                    if (!File.Exists(sidecar)) continue;
                    var sidecarInfo = new FileInfo(sidecar);
                    Add(extension);
                    Add(sidecarInfo.Length.ToString());
                    Add(sidecarInfo.LastWriteTimeUtc.Ticks.ToString());
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }

    private sealed record AssetRecord(string PackagePath, string FilePath, string MountRoot);

    private readonly record struct DependencyLevelSelection(string PackagePath, bool IsAmbiguous);

    private static DependencyLevelSelection ClassifyDependencyLevel(IEnumerable<string> candidates)
    {
        var distinct = candidates.Select(UnrealPathUtil.NormalizePackagePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count switch
        {
            0 => new DependencyLevelSelection("", false),
            1 => new DependencyLevelSelection(distinct[0], false),
            _ => new DependencyLevelSelection("", true),
        };
    }

    private static bool BlocksInheritedDependency(bool hasExplicitNull, bool hasUnresolvedValue) =>
        hasExplicitNull || hasUnresolvedValue;

    private static bool ComputeStructuralCertificate(CharacterGameplayProfileTrace profile) =>
        profile.RuntimeData.TargetExists &&
        profile.MontageComposite.TargetExists &&
        profile.LayerComposite.TargetExists &&
        profile.OrderedAbilitySets.Count > 0 &&
        profile.HasPlayableCore &&
        profile.CombatAbilitySetPackages.Count == 1 &&
        profile.GrappleDataSetPackages.Count <= 1 &&
        !profile.Diagnostics.Any(diagnostic => diagnostic.Severity == CharacterTraceDiagnosticSeverity.Error);

    private static bool ComputeVariantCertificate(
        CharacterGameplayProfileTrace profile,
        CharacterPlayableVariantTrace variant) =>
        profile.IsStructurallyCertified &&
        profile.IsDependencyClosureComplete &&
        variant.HasSerializedPlayableDcmdEvidence &&
        variant.Dcmds.Count > 0 &&
        variant.IsDependencyClosureComplete &&
        !variant.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == CharacterTraceDiagnosticSeverity.Error);

    private static bool IsConcretePlayableAsset(string assetName) =>
        assetName.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) &&
        assetName.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase) &&
        !assetName.Equals("BP_Playable", StringComparison.OrdinalIgnoreCase) &&
        !assetName.Equals("BP_CAT_Playable", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayablePawnTag(string pawnTag) =>
        pawnTag.StartsWith("Pawns.Playable.", StringComparison.OrdinalIgnoreCase);

    private static bool IsNullSoftReference(string packageName, string assetName) =>
        string.IsNullOrWhiteSpace(packageName) ||
        packageName.Equals("None", StringComparison.OrdinalIgnoreCase) ||
        assetName.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static string GameplayProfileKey(string dprd, string montageSet, string layerSet) =>
        $"{Normalize(dprd)}|{Normalize(montageSet)}|{Normalize(layerSet)}";

    private static bool IsCharacterArchetypeCandidate(string packagePath)
    {
        if (!IsHumanoidPackage(packagePath)) return false;
        var name = UnrealPathUtil.AssetName(packagePath);
        return name.Contains("Archetype", StringComparison.OrdinalIgnoreCase) &&
               (name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CAT_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHumanoidPackage(string packagePath) =>
        packagePath.Contains("/Characters/Minifig/", StringComparison.OrdinalIgnoreCase) ||
        packagePath.Contains("/Characters/Smallfig/", StringComparison.OrdinalIgnoreCase);

    private static CharacterTraceDiagnostic Diagnostic(
        CharacterTraceDiagnosticSeverity severity,
        string code,
        string sourcePackage,
        string message) => new()
    {
        Severity = severity,
        Code = code,
        SourcePackage = Normalize(sourcePackage),
        Message = message,
    };

    private static string Normalize(string? packagePath) =>
        UnrealPathUtil.NormalizePackagePath(packagePath ?? "");

    private static bool SamePackage(string? left, string? right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
}
