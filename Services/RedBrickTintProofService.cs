using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Produces the first complete, cosmetic-only Red Brick authoring proof. It uses
/// the game's normal identity/progress/menu contracts, but the one custom payload
/// contains only character/vehicle tint rows. All cloned VFX soft paths are cleared,
/// so enabling the brick must not add a combat, gliding, trail, or overlay effect.
///
/// This is intentionally a CLI proof before a Batcomputer UI is added. Its contract
/// is the future authoring model: one mod-owned effect tag + progress tag + menu
/// metadata + tint payload, all discovered through ordinary Primary Asset rows.
/// </summary>
public sealed class RedBrickTintProofService
{
    private const string PayloadDonor = "/Game/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickData_Main";
    // RedBrickWorldSubsystem uses this as the shared lookup bucket for character
    // tint rows. It is not a per-brick identity: the individual entry is keyed
    // by its RedBrickEffectTag. Custom payloads must therefore retain the native
    // bucket while owning unique effect and progress tags of their own.
    private const string NativeTintPayloadRoutingTag = "Collectables.RedBrickTaggedAssets.MetaData.Default";
    private const string EffectDonor = "/Game/Global/Collectables/MetaData/RedBrickEffects/DA_RedBrickEffectDefinition_Police";
    private const string CollectableDonor = "/Game/Global/Collectables/MetaData/RedBricks/DA_Collectable_RedBrick_Police";
    private const string ProgressDonor = "/Game/GameProgress/PROG_RedBricks";
    private const string ProgressOverrideDonor = "/Game/GameProgress/Overrides/Debug/PROGO_CompleteAll";
    // A real early-story Red Brick progress key. This is used only by the
    // isolated borrowed-progress diagnostic: it is deliberately not a general
    // authoring default because multiple custom bricks would share one save key.
    private const string NinjaProgressTag = "GameProgress.Definitions.RedBricks.Story.00.04.01RB";

    private const string PayloadType = "RedBrickMetaDataAsset";
    private const string PayloadClass = "/Script/Dinner.RedBrickMetaDataAsset";
    private const string EffectType = "RedBrickEffectDefinition";
    private const string EffectClass = "/Script/Dinner.RedBrickEffectDefinition";
    private const string CollectableType = "TtCollectablesMetaData";
    private const string CollectableClass = "/Script/TtCollectables.TtCollectablesMetaData";
    private const string ProgressType = "TtGameProgressDefinitionSet";
    private const string ProgressClass = "/Script/TtGameProgress.TtGameProgressDefinitionSet";
    private const string ProgressOverrideType = "TtGameProgressOverrideCollection";
    private const string ProgressOverrideClass = "/Script/TtGameProgress.TtGameProgressOverrideCollection";

    public sealed class Request
    {
        public string ExtractedContentRoot { get; init; } = "";
        public string UsmapPath { get; init; } = "";
        public string OutputRoot { get; init; } = "";
        public string ModId { get; init; } = "RedBrickTintProof";
        public string DisplayName { get; init; } = "Tint Test";
        public string PrimaryColourRow { get; init; } = "BrightRed";
        public string SecondaryColourRow { get; init; } = "MediumBlue";
        public string TertiaryColourRow { get; init; } = "BrightYellow";
        /// <summary>
        /// Optional extracted Content root containing GameProgress/Overrides.
        /// A focused Developer Research refresh now retrieves this folder, but a
        /// separately extracted donor can be supplied for an isolated proof.
        /// </summary>
        public string OverrideDonorContentRoot { get; init; } = "";
        /// <summary>
        /// Replaces exactly one native default-unlocked Red Brick definition in the
        /// original PROG_RedBricks package. This is a diagnostic proof for the
        /// game's startup-only LiveData materialization, not the distributable
        /// multi-mod solution.
        /// </summary>
        public bool UseEarlyLiveEntryOverride { get; init; }
        /// <summary>
        /// Diagnostic-only path. The generated menu collectable and effect point
        /// at the native Ninja Red Brick's existing progress key. No custom
        /// progress-definition asset or tag is authored, so the menu should use
        /// the native Ninja unlock state if it permits distinct metadata rows to
        /// share a progress tag.
        /// </summary>
        public bool UseBorrowedNinjaProgressTag { get; init; }
        /// <summary>
        /// Creates a unique, mod-owned TtGameProgressOverrideCollection that
        /// marks exactly this Red Brick tag Complete. The collection is not
        /// automatically applied by asset discovery; the generated config maps
        /// it to a one-off command-line proof switch so runtime application can
        /// be tested separately and safely.
        /// </summary>
        public bool UseUniqueProgressUnlockOverride { get; init; }
    }

    public sealed class RegistryPluginResult
    {
        public string Purpose { get; init; } = "";
        public string PluginName { get; init; } = "";
        public string PluginDirectory { get; init; } = "";
        public string InstallDirectory { get; init; } = "";
        public string RegistryPath { get; init; } = "";
    }

    public sealed class Result
    {
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
        public string OutputRoot { get; set; } = "";
        public string StageRoot { get; set; } = "";
        public string OutputContentRoot { get; set; } = "";
        public string InstallRoot { get; set; } = "";
        public string TrioBasePath { get; set; } = "";
        public int RetocExitCode { get; set; } = -1;
        public string AssetTag { get; set; } = "";
        public string EffectTag { get; set; } = "";
        public string ProgressTag { get; set; } = "";
        public string PayloadPackage { get; set; } = "";
        public string EffectPackage { get; set; } = "";
        public string CollectablePackage { get; set; } = "";
        public string ProgressPackage { get; set; } = "";
        public string ProgressOverridePackage { get; set; } = "";
        public string ProgressOverrideCommand { get; set; } = "";
        public bool RequiresProgressOverrideActivation { get; set; }
        public bool UsesNativeProgressOverride { get; set; }
        public bool UsesBorrowedNativeProgressTag { get; set; }
        public string StringTablePackage { get; set; } = "";
        public string AssetManagerConfigPath { get; set; } = "";
        public string TagsConfigPath { get; set; } = "";
        /// <summary>
        /// The physical loose project tag source required before Game Progress
        /// materializes its live entries. Plugin-local tag files load too late.
        /// </summary>
        public string GameProgressTagsConfigPath { get; set; } = "";
        public string GameProgressSettingsConfigPath { get; set; } = "";
        public string ReportPath { get; set; } = "";
        /// <summary>
        /// The cloned progress donor contains several definitions. Until UAssetAPI
        /// supports this game's FInstancedStruct payload, all of them are retagged
        /// into this proof's private namespace. Only <see cref="ProgressTag"/> is
        /// referenced by the menu collectable and starts unlocked.
        /// </summary>
        public List<string> ProgressDefinitionTags { get; } = [];
        public List<RegistryPluginResult> RegistryPlugins { get; } = [];
        public List<string> Repointed { get; } = [];
        public List<string> Log { get; } = [];
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // UAssetAPI 1.1 exposes mutable cooked property trees but no public deep-clone
    // helper.  A payload row must be copied before it is retagged: modifying the
    // donor row in place would remove one native Red Brick from the shared bucket.
    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to locate Object.MemberwiseClone.");

    private sealed record AssetSpec(
        string Purpose,
        string DonorPackage,
        string TargetPackage,
        string PrimaryAssetType,
        string AssetClass);

    public async Task<Result> CreateAsync(Request request, Action<string>? log = null)
    {
        var result = new Result { OutputRoot = Path.GetFullPath(request.OutputRoot ?? "") };
        void Note(string value)
        {
            result.Log.Add(value);
            log?.Invoke(value);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(request.OutputRoot))
            {
                return Finish(result, "invalid-request", "An output folder is required.");
            }
            if (!IsSafeId(request.ModId))
            {
                return Finish(result, "invalid-request", "ModId must contain only letters, digits, and underscores.");
            }
            if (!IsSafeRowName(request.PrimaryColourRow) || !IsSafeRowName(request.SecondaryColourRow) || !IsSafeRowName(request.TertiaryColourRow))
            {
                return Finish(result, "invalid-request", "Colour-row names must contain only letters, digits, underscores, periods, or hyphens.");
            }
            if ((request.UseEarlyLiveEntryOverride && request.UseBorrowedNinjaProgressTag) ||
                (request.UseUniqueProgressUnlockOverride &&
                 (request.UseEarlyLiveEntryOverride || request.UseBorrowedNinjaProgressTag)))
            {
                return Finish(result, "invalid-request",
                    "Choose only one Red Brick progress diagnostic: early-live-entry, borrowed-Ninja, or unique unlock override.");
            }

            var usesBorrowedNinjaProgress = request.UseBorrowedNinjaProgressTag;
            var usesUniqueProgressUnlockOverride = request.UseUniqueProgressUnlockOverride;

            var contentRoot = AppSettings.NormalizeContentRoot(request.ExtractedContentRoot);
            if (!Directory.Exists(contentRoot))
            {
                return Finish(result, "missing-extracted-content", $"Extracted Content root was not found: {contentRoot}");
            }
            if (string.IsNullOrWhiteSpace(request.UsmapPath) || !File.Exists(request.UsmapPath))
            {
                return Finish(result, "missing-usmap", "A valid .usmap path is required to safely generate a Red Brick.");
            }

            var overrideDonorContentRoot = string.IsNullOrWhiteSpace(request.OverrideDonorContentRoot)
                ? contentRoot
                : AppSettings.NormalizeContentRoot(request.OverrideDonorContentRoot);

            var requiredDonors = usesBorrowedNinjaProgress
                ? new[] { PayloadDonor, EffectDonor, CollectableDonor }
                : new[] { PayloadDonor, EffectDonor, CollectableDonor, ProgressDonor };
            foreach (var donor in requiredDonors)
            {
                var donorBase = PackageToBase(contentRoot, donor);
                if (!File.Exists(donorBase + ".uasset") || !File.Exists(donorBase + ".uexp"))
                {
                    return Finish(result, "missing-donor", $"Required native Red Brick donor is missing: {donorBase}.uasset/.uexp");
                }
            }
            if (usesUniqueProgressUnlockOverride)
            {
                var overrideDonorBase = PackageToBase(overrideDonorContentRoot, ProgressOverrideDonor);
                if (!File.Exists(overrideDonorBase + ".uasset") || !File.Exists(overrideDonorBase + ".uexp"))
                {
                    return Finish(result, "missing-override-donor",
                        "The native Game Progress override donor is missing. Refresh Developer Research assets or provide OverrideDonorContentRoot containing " +
                        overrideDonorBase + ".uasset/.uexp");
                }
            }

            result.AssetTag = NativeTintPayloadRoutingTag;
            result.EffectTag = $"Collectables.RedBricks.EffectDefinitions.Mods.{request.ModId}";
            result.ProgressTag = usesBorrowedNinjaProgress
                ? NinjaProgressTag
                : $"GameProgress.Definitions.RedBricks.Mods.{request.ModId}";
            result.PayloadPackage = $"/Game/Mods/{request.ModId}/RedBrickEffects/DA_RedBrickData_{request.ModId}";
            result.EffectPackage = $"/Game/Mods/{request.ModId}/RedBrickEffects/DA_RedBrickEffectDefinition_{request.ModId}";
            result.CollectablePackage = $"/Game/Mods/{request.ModId}/Collectables/DA_Collectable_RedBrick_{request.ModId}";
            result.UsesNativeProgressOverride = request.UseEarlyLiveEntryOverride;
            result.UsesBorrowedNativeProgressTag = usesBorrowedNinjaProgress;
            result.ProgressPackage = usesBorrowedNinjaProgress
                ? ""
                : request.UseEarlyLiveEntryOverride
                    ? ProgressDonor
                    : $"/Game/Mods/{request.ModId}/GameProgress/PROG_RedBricks_{request.ModId}";
            result.ProgressOverridePackage = usesUniqueProgressUnlockOverride
                ? $"/Game/Mods/{request.ModId}/GameProgress/PROGO_Unlock_{request.ModId}"
                : "";
            result.ProgressOverrideCommand = usesUniqueProgressUnlockOverride
                ? "LOTDKUnlockRedBrick_" + request.ModId
                : "";
            result.RequiresProgressOverrideActivation = usesUniqueProgressUnlockOverride;
            result.StringTablePackage = StringTableGenService.PackagePathFor(request.ModId);
            result.StageRoot = Path.Combine(result.OutputRoot, "IoStoreStage");
            result.OutputContentRoot = Path.Combine(result.StageRoot, "LEGOBatmanLotDK", "Content");
            result.InstallRoot = Path.Combine(result.OutputRoot, "Install", "LEGOBatmanLotDK");
            result.TrioBasePath = Path.Combine(result.OutputRoot, request.ModId + "_P");
            result.ReportPath = Path.Combine(result.OutputRoot, "redbrick-tint-proof-report.json");

            var mappings = MappingsCache.Load(request.UsmapPath);
            var payload = CloneAsset(contentRoot, result.OutputContentRoot, mappings,
                new AssetSpec("tint payload", PayloadDonor, result.PayloadPackage, PayloadType, PayloadClass), result.Repointed);
            PatchTintPayload(payload.Asset, result.EffectTag, request.PrimaryColourRow, request.SecondaryColourRow, request.TertiaryColourRow);
            WriteAndValidate(payload, mappings, "AssetTag", result.AssetTag, result.Repointed);
            Note($"Created cosmetic tint payload in native routing bucket '{NativeTintPayloadRoutingTag}': {result.PayloadPackage}");

            var effect = CloneAsset(contentRoot, result.OutputContentRoot, mappings,
                new AssetSpec("effect identity", EffectDonor, result.EffectPackage, EffectType, EffectClass), result.Repointed);
            Require(NativeAssetTextPatch.SetGameplayTag(effect.Asset, "AssetGameplayTag", result.EffectTag),
                "The effect-definition donor did not expose AssetGameplayTag.");
            Require(NativeAssetTextPatch.SetGameplayTag(effect.Asset, "RedBrickTag", result.ProgressTag),
                "The effect-definition donor did not expose RedBrickTag.");
            effect.Asset.Write(effect.TargetBase + ".uasset");
            ValidatePackage(effect, mappings);
            Note($"Created Red Brick identity with no custom gameplay set: {result.EffectPackage}");

            ClonedAsset? progress = null;
            if (usesBorrowedNinjaProgress)
            {
                Note($"Borrowing native Ninja Red Brick progress key for this diagnostic: {result.ProgressTag}. No custom Game Progress asset or tag is being authored.");
            }
            else
            {
                progress = CloneAsset(contentRoot, result.OutputContentRoot, mappings,
                    new AssetSpec("unlocked progress definition", ProgressDonor, result.ProgressPackage, ProgressType, ProgressClass), result.Repointed);
                if (request.UseEarlyLiveEntryOverride)
                {
                    result.ProgressDefinitionTags.Add(PatchNativeUnlockedProgressDefinitionViaNameMap(progress.Asset, result.ProgressTag));
                }
                else
                {
                    result.ProgressDefinitionTags.AddRange(PatchProgressDefinitionsViaNameMap(progress.Asset, request.ModId, result.ProgressTag));
                }
                progress.Asset.Write(progress.TargetBase + ".uasset");
                ValidateProgressPackage(progress, mappings, result.ProgressTag, result.ProgressDefinitionTags, request.UseEarlyLiveEntryOverride);
                Note(request.UseEarlyLiveEntryOverride
                    ? $"Created same-path PROG_RedBricks override: the one native default-unlocked slot now uses {result.ProgressTag}; all other native definitions are untouched."
                    : $"Created isolated Red Brick progress scaffold: {result.ProgressTag} unlocked; {result.ProgressDefinitionTags.Count - 1} private locked definitions retained from the native donor.");
            }

            ClonedAsset? progressUnlockOverride = null;
            if (usesUniqueProgressUnlockOverride)
            {
                progressUnlockOverride = CloneAsset(overrideDonorContentRoot, result.OutputContentRoot, mappings,
                    new AssetSpec("unique Red Brick unlock override", ProgressOverrideDonor,
                        result.ProgressOverridePackage, ProgressOverrideType, ProgressOverrideClass), result.Repointed);
                PatchProgressUnlockOverride(progressUnlockOverride.Asset, result.ProgressTag);
                progressUnlockOverride.Asset.Write(progressUnlockOverride.TargetBase + ".uasset");
                ValidateProgressUnlockOverride(progressUnlockOverride, mappings, result.ProgressTag);
                Note($"Created exact custom unlock override: {result.ProgressOverridePackage} (scope={result.ProgressTag})");
            }

            var tableEntries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["RedBrick." + request.ModId + ".Name"] = string.IsNullOrWhiteSpace(request.DisplayName) ? "Tint Test" : request.DisplayName.Trim(),
                ["RedBrick." + request.ModId + ".Type"] = "COLOR",
            };
            var tableBase = PackageToBase(result.OutputContentRoot, result.StringTablePackage);
            var stringTable = new StringTableGenService(AppSettings.Current.EffectiveProjectRoot()).Generate(tableBase, request.ModId, tableEntries);
            if (!string.Equals(stringTable.Status, "created", StringComparison.OrdinalIgnoreCase))
            {
                return Finish(result, "string-table-failed", stringTable.Error ?? "Failed to generate the Red Brick StringTable.");
            }
            Note($"Created mod-owned Red Brick text: {result.StringTablePackage}");

            var collectable = CloneAsset(contentRoot, result.OutputContentRoot, mappings,
                new AssetSpec("menu collectable", CollectableDonor, result.CollectablePackage, CollectableType, CollectableClass), result.Repointed);
            Require(NativeAssetTextPatch.SetGameplayTag(collectable.Asset, "IdentifyingTag", result.ProgressTag),
                "The collectable donor did not expose IdentifyingTag.");
            var textObjectPath = StringTableGenService.ObjectPathFor(request.ModId);
            Require(NativeAssetTextPatch.SetStringTableText(collectable.Asset, "DisplayName", textObjectPath, "RedBrick." + request.ModId + ".Name"),
                "The collectable donor did not expose DisplayName.");
            Require(NativeAssetTextPatch.SetStringTableText(collectable.Asset, "DisplayType", textObjectPath, "RedBrick." + request.ModId + ".Type"),
                "The collectable donor did not expose DisplayType.");
            collectable.Asset.Write(collectable.TargetBase + ".uasset");
            ValidatePackage(collectable, mappings);
            Note($"Created native-style menu metadata: {result.CollectablePackage}");

            var pluginRequests = new List<(string Suffix, string Description, AssetSpec Spec)>
            {
                ("TintPayload", "Red Brick tint payload", payload.Spec),
                ("TintEffect", "Red Brick tint identity", effect.Spec),
                ("TintCollectable", "Red Brick menu metadata", collectable.Spec),
            };
            // A custom collectable is filtered through its Game Progress
            // definition. The last visible proof had this row; omitting it
            // makes the menu discard the tile before lock state is evaluated.
            // The ordinary custom scaffold must be advertised as a primary asset.
            // The early-default-unlock control deliberately replaces the native
            // /Game/GameProgress/PROG_RedBricks package, which cannot (and must
            // not) be published by a /Game/Mods registry plugin.  Its same-path
            // package is resolved directly by the mounted IoStore trio.
            if (progress is not null && !request.UseEarlyLiveEntryOverride)
            {
                pluginRequests.Add(("TintProgress", "Red Brick progress definition", progress.Spec));
            }
            // An override collection is only useful when the native Game
            // Progress system can resolve its soft object path.  Publish it as
            // its own primary asset for the one-command activation proof.  The
            // command is still opt-in and cannot touch a base-game tag because
            // PatchProgressUnlockOverride narrows it to this mod's unique tag.
            if (progressUnlockOverride is not null)
            {
                pluginRequests.Add(("TintProgressUnlock", "Red Brick unlock override", progressUnlockOverride.Spec));
            }
            foreach (var item in pluginRequests)
            {
                var registry = await new RegistryPluginService().BuildAsync(
                    result.OutputRoot,
                    request.ModId + item.Item1,
                    item.Item2,
                    [new RegistryPluginService.RegistryRow(item.Item3.TargetPackage, item.Item3.PrimaryAssetType, item.Item3.AssetClass)],
                    Note);
                if (!registry.Succeeded || registry.Layout is null)
                {
                    return Finish(result, "registry-failed", registry.Error);
                }
                var installDirectory = Path.Combine(result.InstallRoot, "Binaries", "Win64", "ue4ss", "LOTDKExpanded", "RegistryPlugins", registry.Layout.PluginName);
                result.RegistryPlugins.Add(new RegistryPluginResult
                {
                    Purpose = item.Item2,
                    PluginName = registry.Layout.PluginName,
                    PluginDirectory = registry.Layout.PluginDirectory,
                    InstallDirectory = installDirectory,
                    RegistryPath = registry.Layout.RegistryPath,
                });
            }

            // One enabled registry plugin owns the config contract for the custom
            // payload, effect, and menu metadata. The early-init proof deliberately
            // leaves GameProgress discovery alone: it overrides the already-native
            // base package before normal initialization instead.
            var configPlugin = result.RegistryPlugins[0];
            result.AssetManagerConfigPath = Path.Combine(configPlugin.PluginDirectory, "Config", "Game.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(result.AssetManagerConfigPath)!);
            File.WriteAllText(
                result.AssetManagerConfigPath,
                BuildAssetManagerConfig(
                    !request.UseEarlyLiveEntryOverride && !usesBorrowedNinjaProgress,
                    includeProgressOverrideMods: usesUniqueProgressUnlockOverride),
                new UTF8Encoding(false));

            // TtGameProgressSettings owns the game's native startup shortcut
            // mechanism.  Keep the three shipped shortcuts intact and append
            // one uniquely named, opt-in proof command.  Passing that command
            // on the game process command line makes TtGameProgressSystem load
            // and apply exactly the generated collection; merely installing the
            // asset does nothing.
            if (usesUniqueProgressUnlockOverride)
            {
                var shortCommandPath = Path.Combine(configPlugin.PluginDirectory, "Config", "DefaultGameProgressSettings.ini");
                File.WriteAllText(
                    shortCommandPath,
                    BuildProgressOverrideShortCommandConfig(result.ProgressOverrideCommand, result.ProgressOverridePackage),
                    new UTF8Encoding(false));
                Note($"Added native Game Progress short command '-{result.ProgressOverrideCommand}' for the scoped unlock collection.");
            }
            result.TagsConfigPath = Path.Combine(configPlugin.PluginDirectory, "Config", "Tags", request.ModId + "Tags.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(result.TagsConfigPath)!);
            File.WriteAllText(
                result.TagsConfigPath,
                BuildTagConfig(
                    result.EffectTag,
                    usesBorrowedNinjaProgress
                        ? Array.Empty<string>()
                        : result.ProgressDefinitionTags),
                new UTF8Encoding(false));

            // Unlike ordinary gameplay tags, a new Red Brick progress tag must
            // be visible to the game's startup-time TtGameProgressLiveData
            // initializer.  A tag file inside an injected registry plugin is
            // discovered after that point.  Stage the public tag in the game's
            // physical loose Config/Tags location instead.  It is intentionally
            // one shared LOTDK Expanded file so a release installer can merge
            // entries from any number of author packs without editing base INIs.
            if (!usesBorrowedNinjaProgress)
            {
                result.GameProgressTagsConfigPath = Path.Combine(
                    result.InstallRoot,
                    "Config",
                    "Tags",
                    "LOTDKExpandedRedBrickTags.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(result.GameProgressTagsConfigPath)!);
                File.WriteAllText(
                    result.GameProgressTagsConfigPath,
                    BuildLooseGameProgressTagConfig(result.ProgressTag),
                    new UTF8Encoding(false));
                Note($"Staged required loose Game Progress tag source: {result.GameProgressTagsConfigPath}");
            }

            Note(request.UseEarlyLiveEntryOverride
                ? "Added /Game/Mods discovery rules for custom Red Brick payloads, identities, and menu metadata; the replacement progress tag is declared through the dedicated GameProgressTags.ini source."
                : usesBorrowedNinjaProgress
                    ? "Added /Game/Mods discovery rules for custom Red Brick payloads, identities, and menu metadata only. The native Ninja Game Progress key is intentionally not redeclared."
                    : usesUniqueProgressUnlockOverride
                        ? "Added the proven /Game/Mods discovery rules, a registered scoped unlock collection, and one opt-in native Game Progress short command."
                        : "Added /Game/Mods discovery rules for Red Brick payloads, effect identities, collectable metadata, and progress sets.");

            result.RetocExitCode = await PackAsync(result.StageRoot, result.TrioBasePath + ".utoc", Note);
            if (result.RetocExitCode != 0)
            {
                return Finish(result, "pack-failed", $"retoc to-zen failed with exit code {result.RetocExitCode}.");
            }
            foreach (var extension in new[] { ".pak", ".utoc", ".ucas" })
            {
                if (!File.Exists(result.TrioBasePath + extension))
                {
                    return Finish(result, "pack-failed", $"retoc did not produce {Path.GetFileName(result.TrioBasePath + extension)}.");
                }
            }

            var pakInstall = Path.Combine(result.InstallRoot, "Content", "Paks", "~mods");
            Directory.CreateDirectory(pakInstall);
            foreach (var extension in new[] { ".pak", ".utoc", ".ucas" })
            {
                File.Copy(result.TrioBasePath + extension, Path.Combine(pakInstall, Path.GetFileName(result.TrioBasePath + extension)), true);
            }
            foreach (var registryPlugin in result.RegistryPlugins)
            {
                // A registry plugin has an isolated destination.  Replace that
                // destination atomically at the directory level so a previous
                // generator revision cannot leave a nested copy of the same
                // .uplugin behind.  The proxy discovers recursively, so such a
                // stale nested descriptor would otherwise be injected twice.
                ReplaceDirectory(registryPlugin.PluginDirectory, registryPlugin.InstallDirectory);
            }
            Note(request.UseEarlyLiveEntryOverride
                ? "Created a drop-in early-initialization proof: one trio plus three enabled registry plugins. It temporarily replaces one default native Red Brick slot and has tint only."
                : usesBorrowedNinjaProgress
                    ? "Created a drop-in borrowed-Ninja diagnostic: one trio plus three enabled registry plugins. The new tile shares Ninja's native Red Brick progress state and has tint only."
                    : usesUniqueProgressUnlockOverride
                        ? "Created a drop-in native unlock proof: one trio plus five enabled registry plugins (payload, effect, collectable, progress, and scoped unlock collection). The custom tile remains unique; it unlocks only when launched with the generated proof command."
                        : "Created a drop-in Install tree: one trio plus three enabled registry plugins. The Red Brick starts unlocked and has tint only.");

            result.Status = "created";
            SaveReport(result);
            return result;
        }
        catch (Exception ex)
        {
            return Finish(result, "error", ex.ToString());
        }
    }

    private sealed record ClonedAsset(AssetSpec Spec, UAsset Asset, string TargetBase);

    private static ClonedAsset CloneAsset(
        string sourceContentRoot,
        string targetContentRoot,
        Usmap mappings,
        AssetSpec spec,
        List<string> repointed)
    {
        var sourceBase = PackageToBase(sourceContentRoot, spec.DonorPackage);
        var targetBase = PackageToBase(targetContentRoot, spec.TargetPackage);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        CopyRequired(sourceBase + ".uasset", targetBase + ".uasset");
        CopyRequired(sourceBase + ".uexp", targetBase + ".uexp");
        CopyIfExists(sourceBase + ".ubulk", targetBase + ".ubulk");
        CopyIfExists(sourceBase + ".uptnl", targetBase + ".uptnl");

        var asset = new UAsset(targetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.None)
        {
            FolderName = new FString(spec.TargetPackage),
        };
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [spec.DonorPackage] = spec.TargetPackage,
            [UnrealPathUtil.ObjectPath(spec.DonorPackage)] = UnrealPathUtil.ObjectPath(spec.TargetPackage),
            [UnrealPathUtil.AssetName(spec.DonorPackage)] = UnrealPathUtil.AssetName(spec.TargetPackage),
        };
        var nameMap = asset.GetNameMapIndexList();
        for (var i = 0; i < nameMap.Count; i++)
        {
            var original = nameMap[i].ToString();
            if (replacements.TryGetValue(original, out var replacement) && !string.Equals(original, replacement, StringComparison.Ordinal))
            {
                asset.SetNameReference(i, new FString(replacement));
                repointed.Add($"{Path.GetFileName(spec.TargetPackage)}: {original} -> {replacement}");
            }
        }
        UnrealPathUtil.RepairSplitPathNameMapEntries(asset, [spec.TargetPackage], repointed);
        return new ClonedAsset(spec, asset, targetBase);
    }

    private static void PatchTintPayload(UAsset asset, string effectTag, string primary, string secondary, string tertiary)
    {
        var payloadExport = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.Data.OfType<ArrayPropertyData>().Any(property => property.Name.ToString() == "MetaData"))
            ?? throw new InvalidOperationException("The Red Brick payload donor has no MetaData array.");
        var metadata = payloadExport.Data.OfType<ArrayPropertyData>().First(property => property.Name.ToString() == "MetaData");
        var donorEntry = metadata.Value.OfType<StructPropertyData>().FirstOrDefault()
            ?? throw new InvalidOperationException("The Red Brick payload donor has an empty MetaData array.");
        var entry = (StructPropertyData)ClonePropertyTree(donorEntry);
        Require(SetNestedGameplayTag(asset, entry, "RedBrickEffectTag", effectTag),
            "The Red Brick payload entry did not expose RedBrickEffectTag.");
        Require(SetTintRows(asset, entry, primary, secondary, tertiary),
            "The Red Brick payload entry did not expose all CharacterTintData colour handles.");
        ClearSoftReferences(asset, entry);
        // AssetTag=...Default is a shared routing bucket. Keep every native row
        // and append this mod-owned effect row so native previews keep working.
        metadata.Value = [.. metadata.Value, entry];
    }

    private static PropertyData ClonePropertyTree(PropertyData source)
    {
        var clone = (PropertyData)MemberwiseCloneMethod.Invoke(source, null)!;
        switch (source)
        {
            case StructPropertyData sourceStruct:
            {
                var cloneStruct = (StructPropertyData)clone;
                cloneStruct.Value = sourceStruct.Value.Select(ClonePropertyTree).ToList();
                break;
            }
            case ArrayPropertyData sourceArray:
            {
                var cloneArray = (ArrayPropertyData)clone;
                cloneArray.Value = sourceArray.Value.Select(ClonePropertyTree).ToArray();
                break;
            }
        }
        return clone;
    }

    private static void WriteAndValidate(ClonedAsset clone, Usmap mappings, string requiredTagProperty, string requiredTag, List<string> repointed)
    {
        // The payload's AssetTag is patched last because its value is part of the
        // RedBrickWorldSubsystem map key, whereas the nested effect tag keys the one tint row.
        Require(NativeAssetTextPatch.SetGameplayTag(clone.Asset, requiredTagProperty, requiredTag),
            $"The {clone.Spec.Purpose} donor did not expose {requiredTagProperty}.");
        clone.Asset.Write(clone.TargetBase + ".uasset");
        var verify = new UAsset(clone.TargetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.None);
        var actual = NativeAssetTextPatch.GetGameplayTag(verify, requiredTagProperty);
        if (!string.Equals(actual, requiredTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {clone.Spec.Purpose} tag write failed. expected='{requiredTag}' actual='{actual ?? "<null>"}'.");
        }
        ValidatePackage(clone, mappings);
        repointed.Add($"{clone.Spec.Purpose}: {requiredTagProperty} -> {requiredTag}");
    }

    private static List<string> PatchProgressDefinitionsViaNameMap(UAsset asset, string modId, string unlockedProgressTag)
    {
        // ProgressDefinitions is an FInstancedStruct array.  UAssetAPI 1.1.0
        // deliberately preserves this game-specific unversioned payload but does
        // not expose its inner structs as PropertyData.  Its FGameplayTags are
        // still FNames, however, so we can safely remap their name-map entries
        // without touching the opaque serialized struct bytes.
        //
        // We retag *every* donor Red Brick definition.  That avoids registering a
        // second copy of any native tag, which would make this proof influence an
        // existing brick.  The native donor's known unlocked definition becomes the
        // test entry; all retained locked definitions become private unused stubs.
        const string prefix = "GameProgress.Definitions.RedBricks.";
        const string nativeUnlocked = "GameProgress.Definitions.RedBricks.Hub.SI.CAAC.Shop.01RB";
        var nameMap = asset.GetNameMapIndexList();
        var candidates = nameMap
            .Select((name, index) => new { Name = name.ToString(), Index = index })
            .Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "The Red Brick progress donor exposed no Red Brick FName entries. " +
                "Its FInstancedStruct payload cannot be safely authored with the current mappings.");
        }
        if (!candidates.Any(item => string.Equals(item.Name, nativeUnlocked, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The Red Brick progress donor no longer contains the expected unlocked template '{nativeUnlocked}'.");
        }

        var written = new List<string>(candidates.Count);
        var stubOrdinal = 1;
        foreach (var candidate in candidates)
        {
            var replacement = string.Equals(candidate.Name, nativeUnlocked, StringComparison.Ordinal)
                ? unlockedProgressTag
                : $"GameProgress.Definitions.RedBricks.Mods.{modId}.Definition{stubOrdinal++:D2}";
            asset.SetNameReference(candidate.Index, new FString(replacement));
            written.Add(replacement);
        }
        if (written.Distinct(StringComparer.Ordinal).Count() != written.Count)
        {
            throw new InvalidOperationException("Generated duplicate private Red Brick progress tags.");
        }
        if (!written.Contains(unlockedProgressTag, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The custom unlocked Red Brick progress tag was not written.");
        }
        return written;
    }

    private static string PatchNativeUnlockedProgressDefinitionViaNameMap(UAsset asset, string customProgressTag)
    {
        // The game populates TtGameProgressLiveData from the original progress
        // package during startup.  This proof preserves that package identity and
        // every definition except this established default-unlocked slot.  It is
        // intentionally a temporary one-slot replacement, not a merge strategy.
        const string nativeUnlocked = "GameProgress.Definitions.RedBricks.Hub.SI.CAAC.Shop.01RB";
        var nameMap = asset.GetNameMapIndexList();
        var index = nameMap
            .Select((name, candidateIndex) => new { Name = name.ToString(), Index = candidateIndex })
            .FirstOrDefault(item => string.Equals(item.Name, nativeUnlocked, StringComparison.Ordinal))?.Index;
        if (index is null)
        {
            throw new InvalidOperationException(
                $"The base Red Brick progress asset no longer contains the expected default-unlocked slot '{nativeUnlocked}'.");
        }

        asset.SetNameReference(index.Value, new FString(customProgressTag));
        var written = asset.GetNameMapIndexList()[index.Value].ToString();
        if (!string.Equals(written, customProgressTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The base Red Brick progress override did not retain the custom tag. expected='{customProgressTag}' actual='{written}'.");
        }
        return written;
    }

    private static void PatchProgressUnlockOverride(UAsset asset, string progressTag)
    {
        // Clone the native CompleteAll collection but replace its broad category
        // search with one exact mod-owned entry tag. This is a cosmetic-only
        // proof: it cannot affect a base-game Red Brick or another mod's tag.
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(candidate =>
            candidate.Data.OfType<ArrayPropertyData>().Any(property => property.Name.ToString() == "Overrides"))
            ?? throw new InvalidOperationException("The Game Progress override donor has no Overrides array.");
        var overrides = export.Data.OfType<ArrayPropertyData>().First(property => property.Name.ToString() == "Overrides");
        var entry = overrides.Value.OfType<StructPropertyData>().SingleOrDefault()
            ?? throw new InvalidOperationException("The Game Progress override donor must contain exactly one override entry.");
        var searchByTags = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == "SearchByTags");
        var tags = searchByTags?.Value.OfType<GameplayTagContainerPropertyData>().SingleOrDefault();
        if (tags is null)
        {
            throw new InvalidOperationException("The Game Progress override donor has no writable SearchByTags container.");
        }

        tags.Value = [new FName(asset, progressTag)];
        overrides.Value = [entry];
    }

    private static bool SetTintRows(UAsset asset, StructPropertyData entry, string primary, string secondary, string tertiary)
    {
        var tint = entry.Value.OfType<StructPropertyData>().FirstOrDefault(property => property.Name.ToString() == "CharacterTintData");
        if (tint is null) return false;
        return SetRowName(asset, tint, "PrimaryColourRowHandle", primary) &&
               SetRowName(asset, tint, "SecondaryColourRowHandle", secondary) &&
               SetRowName(asset, tint, "TertiaryColourRowHandle", tertiary);
    }

    private static bool SetRowName(UAsset asset, StructPropertyData parent, string handleName, string rowName)
    {
        var handle = parent.Value.OfType<StructPropertyData>().FirstOrDefault(property => property.Name.ToString() == handleName);
        var value = handle?.Value.OfType<NamePropertyData>().FirstOrDefault(property => property.Name.ToString() == "RowName");
        if (value is null) return false;
        value.Value = new FName(asset, rowName);
        return true;
    }

    private static bool SetNestedGameplayTag(UAsset asset, StructPropertyData parent, string propertyName, string tag)
    {
        var gameplayTag = parent.Value.OfType<StructPropertyData>().FirstOrDefault(property => property.Name.ToString() == propertyName);
        var name = gameplayTag?.Value.OfType<NamePropertyData>().FirstOrDefault(property => property.Name.ToString() == "TagName");
        if (name is null) return false;
        name.Value = new FName(asset, tag);
        return true;
    }

    private static void ClearSoftReferences(UAsset asset, StructPropertyData entry)
    {
        // The donor's tint data has a known-empty OverlayMaterial soft path. Reuse
        // that exact cooked representation rather than synthesizing a new soft path.
        var blank = DescendantSoftObjects(entry.Value).FirstOrDefault(soft =>
            soft.Name.ToString() == "OverlayMaterial")
            ?? throw new InvalidOperationException("The Red Brick tint donor has no blank OverlayMaterial soft path template.");
        foreach (var soft in DescendantSoftObjects(entry.Value))
        {
            soft.Value = blank.Value;
        }
    }

    private static IEnumerable<SoftObjectPropertyData> DescendantSoftObjects(IEnumerable<PropertyData> properties)
    {
        foreach (var property in properties)
        {
            switch (property)
            {
                case SoftObjectPropertyData soft:
                    yield return soft;
                    break;
                case StructPropertyData structure:
                    foreach (var nested in DescendantSoftObjects(structure.Value)) yield return nested;
                    break;
                case ArrayPropertyData array:
                    foreach (var nested in DescendantSoftObjects(array.Value)) yield return nested;
                    break;
            }
        }
    }

    private static void ValidatePackage(ClonedAsset clone, Usmap mappings)
    {
        var verify = new UAsset(clone.TargetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        if (!string.Equals(verify.FolderName?.ToString(), clone.Spec.TargetPackage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The written {clone.Spec.Purpose} has wrong FolderName '{verify.FolderName}', expected '{clone.Spec.TargetPackage}'.");
        }
    }

    private static void ValidateProgressPackage(
        ClonedAsset clone,
        Usmap mappings,
        string unlockedProgressTag,
        IReadOnlyCollection<string> expectedTags,
        bool isNativePackageOverride)
    {
        ValidatePackage(clone, mappings);
        var verify = new UAsset(clone.TargetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        var actual = verify.GetNameMapIndexList().Select(name => name.ToString()).ToHashSet(StringComparer.Ordinal);
        if (!actual.Contains(unlockedProgressTag) || !expectedTags.All(actual.Contains))
        {
            throw new InvalidOperationException(
                "The rewritten Red Brick progress name-map entries did not survive serialization. " +
                $"expected={expectedTags.Count} actual={actual.Count} unlocked_present={actual.Contains(unlockedProgressTag)}.");
        }
        if (isNativePackageOverride && actual.Contains("GameProgress.Definitions.RedBricks.Hub.SI.CAAC.Shop.01RB"))
        {
            throw new InvalidOperationException(
                "The native default-unlocked Red Brick tag survived the override, so the early-live-entry proof would be inconclusive.");
        }
    }

    private static void ValidateProgressUnlockOverride(ClonedAsset clone, Usmap mappings, string progressTag)
    {
        ValidatePackage(clone, mappings);
        var verify = new UAsset(clone.TargetBase + ".uasset", EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        var export = verify.Exports.OfType<NormalExport>().FirstOrDefault(candidate =>
            candidate.Data.OfType<ArrayPropertyData>().Any(property => property.Name.ToString() == "Overrides"))
            ?? throw new InvalidOperationException("The written Game Progress override has no Overrides array.");
        var entry = export.Data.OfType<ArrayPropertyData>().First(property => property.Name.ToString() == "Overrides")
            .Value.OfType<StructPropertyData>().SingleOrDefault()
            ?? throw new InvalidOperationException("The written Game Progress override has an invalid Overrides array.");
        var tags = entry.Value.OfType<StructPropertyData>()
            .FirstOrDefault(property => property.Name.ToString() == "SearchByTags")?
            .Value.OfType<GameplayTagContainerPropertyData>().SingleOrDefault()?.Value
            ?? [];
        if (tags.Length != 1 || !string.Equals(tags[0].ToString(), progressTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The written Game Progress override is not scoped to exactly the custom Red Brick tag. " +
                $"expected='{progressTag}' actual=[{string.Join(",", tags.Select(tag => tag.ToString()))}].");
        }
    }

    private static string BuildTagConfig(string effectTag, IEnumerable<string> progressTags) =>
        "[/Script/GameplayTags.GameplayTagsList]\r\n" +
        $"GameplayTagList=(Tag=\"{effectTag}\",DevComment=\"Batcomputer cosmetic-only Red Brick identity\")\r\n" +
        string.Concat(progressTags.Select(tag =>
            $"GameplayTagList=(Tag=\"{tag}\",DevComment=\"Batcomputer Red Brick tint-proof progress definition\")\r\n"));

    private static string BuildLooseGameProgressTagConfig(string progressTag) =>
        "[/Script/GameplayTags.GameplayTagsList]\r\n" +
        $"GameplayTagList=(Tag=\"{progressTag}\",DevComment=\"LOTDK Expanded custom Red Brick progress entry\")\r\n";

    private static string BuildProgressOverrideShortCommandConfig(string command, string overridePackage) =>
        "[/Script/TtGameProgress.TtGameProgressSettings]\r\n" +
        // This is a TMap property, so the setting must be emitted as one
        // complete map assignment rather than a '+' array append. Preserve the
        // three game-provided debug keys so this proof does not remove them.
        "GameProgressOverrideShortCommands=((\"100percent\", \"/Game/GameProgress/Overrides/Debug/PROGO_CompleteAll.PROGO_CompleteAll\")," +
        "(\"StoryComplete\", \"/Game/GameProgress/Overrides/Debug/PROGO_CompleteStory.PROGO_CompleteStory\")," +
        "(\"CollectCharsAndFriends\", \"/Game/GameProgress/Overrides/Debug/PROGO_CollectCharactersAndUpgrades.PROGO_CollectCharactersAndUpgrades\")," +
        $"(\"{command}\", \"{overridePackage}.{Path.GetFileName(overridePackage)}\"))\r\n";

    private static string BuildAssetManagerConfig(bool includeProgressMods, bool includeProgressOverrideMods) =>
        "[/Script/Engine.AssetManagerSettings]\r\n" +
        ScanRule("TtGameProgressDefinitionSet", "/Script/TtGameProgress.TtGameProgressDefinitionSet", "((Path=\"/Game/GameProgress\"),(Path=\"/Game/Developers\"),(Path=\"/TtMissions\"),(Path=\"/DinnerRandomCrimes/FunctionalTests\"),(Path=\"/TtObjectiveTasks\"),(Path=\"/Game/Levels/MechTest\"),(Path=\"/TtGameProgress\"),(Path=\"/Game/FunctionalTests\"),(Path=\"/TtMissionMarkers/FunctionalTests\"),(Path=\"/TtGameFlowIntegrations/FunctionalTests/MissionScenario\"),(Path=\"/DinnerObjectiveTasks\"),(Path=\"/Game/AdditionalContent\"),(Path=\"/DinnerFeatureTemplates/Templates/DLC/Content/GameProgress\"))", "-1", "-1", "Unknown", false, false) +
        (includeProgressMods
            ? ScanRule("TtGameProgressDefinitionSet", "/Script/TtGameProgress.TtGameProgressDefinitionSet", "((Path=\"/Game/GameProgress\"),(Path=\"/Game/Developers\"),(Path=\"/TtMissions\"),(Path=\"/DinnerRandomCrimes/FunctionalTests\"),(Path=\"/TtObjectiveTasks\"),(Path=\"/Game/Levels/MechTest\"),(Path=\"/TtGameProgress\"),(Path=\"/Game/FunctionalTests\"),(Path=\"/TtMissionMarkers/FunctionalTests\"),(Path=\"/TtGameFlowIntegrations/FunctionalTests/MissionScenario\"),(Path=\"/DinnerObjectiveTasks\"),(Path=\"/Game/AdditionalContent\"),(Path=\"/DinnerFeatureTemplates/Templates/DLC/Content/GameProgress\"),(Path=\"/Game/Mods\"))", "-1", "-1", "Unknown", true, true)
            : "") +
        (includeProgressOverrideMods
            ? ScanRule("TtGameProgressOverrideCollection", "/Script/TtGameProgress.TtGameProgressOverrideCollection", "((Path=\"/Game/GameProgress/Overrides\"))", "100", "0", "Unknown", true, false) +
              ScanRule("TtGameProgressOverrideCollection", "/Script/TtGameProgress.TtGameProgressOverrideCollection", "((Path=\"/Game/GameProgress/Overrides\"),(Path=\"/Game/Mods\"))", "100", "0", "Unknown", true, true)
            : "") +
        ScanRule("TtCollectablesMetaData", "/Script/TtCollectables.TtCollectablesMetaData", "((Path=\"/Game/Global/Collectables/MetaData\"),(Path=\"/Game/AdditionalContent/VillainMode/Collectables/Metadata\"))", "-1", "-1", "Unknown", false, false) +
        ScanRule("TtCollectablesMetaData", "/Script/TtCollectables.TtCollectablesMetaData", "((Path=\"/Game/Global/Collectables/MetaData\"),(Path=\"/Game/AdditionalContent/VillainMode/Collectables/Metadata\"),(Path=\"/Game/Mods\"))", "-1", "-1", "Unknown", true, true) +
        RedBrickScanRule("RedBrickEffectDefinition", "/Script/Dinner.RedBrickEffectDefinition", false) +
        RedBrickScanRule("RedBrickEffectDefinition", "/Script/Dinner.RedBrickEffectDefinition", true) +
        RedBrickScanRule("RedBrickCharacterMetaDataAsset", "/Script/Dinner.RedBrickCharacterMetaDataAsset", false) +
        RedBrickScanRule("RedBrickCharacterMetaDataAsset", "/Script/Dinner.RedBrickCharacterMetaDataAsset", true) +
        RedBrickScanRule("RedBrickVehicleMetaDataAsset", "/Script/Dinner.RedBrickVehicleMetaDataAsset", false) +
        RedBrickScanRule("RedBrickVehicleMetaDataAsset", "/Script/Dinner.RedBrickVehicleMetaDataAsset", true) +
        RedBrickScanRule("RedBrickMetaDataAsset", "/Script/Dinner.RedBrickMetaDataAsset", false) +
        RedBrickScanRule("RedBrickMetaDataAsset", "/Script/Dinner.RedBrickMetaDataAsset", true);

    private static string RedBrickScanRule(string type, string assetClass, bool includeMods) =>
        ScanRule(type, assetClass,
            includeMods
                ? "((Path=\"/Game/Global/Collectables/MetaData/RedBrickEffects\"),(Path=\"/Game/Mods\"))"
                : "((Path=\"/Game/Global/Collectables/MetaData/RedBrickEffects\"))",
            "100", "0", "AlwaysCook", true, includeMods);

    private static string ScanRule(string type, string assetClass, string directories, string priority, string chunkId, string cookRule, bool recursive, bool plus) =>
        (plus ? "+" : "-") +
        $"PrimaryAssetTypesToScan=(PrimaryAssetType=\"{type}\",AssetBaseClass=\"{assetClass}\",bHasBlueprintClasses=False,bIsEditorOnly=False,Directories={directories},SpecificAssets=,Rules=(Priority={priority},ChunkId={chunkId},bApplyRecursively={(recursive ? "True" : "False")},CookRule={cookRule}))\r\n";

    private static async Task<int> PackAsync(string stageRoot, string outputUtoc, Action<string> log)
    {
        var settings = AppSettings.Current;
        var oodleRetoc = settings.EffectiveOodleRetocExePath();
        var oodleRuntime = settings.EffectiveOodleRuntimeDllPath();
        var useOodle = File.Exists(oodleRetoc) && !string.IsNullOrWhiteSpace(oodleRuntime) && File.Exists(oodleRuntime);
        var retoc = useOodle ? oodleRetoc : settings.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            log($"retoc.exe was not found: {retoc}");
            return -1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputUtoc)!);
        var psi = new ProcessStartInfo
        {
            FileName = retoc,
            WorkingDirectory = Path.GetDirectoryName(retoc) ?? AppSettings.ToolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (useOodle)
        {
            var runtimeDirectory = Path.GetDirectoryName(oodleRuntime!)!;
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
                ? runtimeDirectory
                : runtimeDirectory + Path.PathSeparator + inheritedPath;
        }
        psi.ArgumentList.Add("to-zen");
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(GameAssetRefreshService.RetocEngineVersion);
        psi.ArgumentList.Add(stageRoot);
        psi.ArgumentList.Add(outputUtoc);
        using var process = Process.Start(psi);
        if (process is null) return -1;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        if (!string.IsNullOrWhiteSpace(stdout)) log(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) log(stderr.Trim());
        return process.ExitCode;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }
        CopyDirectory(source, destination);
    }

    private static void CopyRequired(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source)) CopyRequired(source, destination);
    }

    private static string PackageToBase(string contentRoot, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only /Game package paths can be staged.", nameof(packagePath));
        }
        return Path.Combine(contentRoot, package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static Result Finish(Result result, string status, string? error)
    {
        result.Status = status;
        result.Error = error;
        SaveReport(result);
        return result;
    }

    private static void SaveReport(Result result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(result.ReportPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(result.ReportPath)!);
            File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, ReportJson));
        }
        catch
        {
            // Preserve the primary generation result if reporting itself fails.
        }
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private static bool IsSafeId(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsSafeRowName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-');
}
