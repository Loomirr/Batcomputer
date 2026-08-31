using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Clones a donor UIMD into the mod's package path.</summary>
public sealed class UimdGenService
{
    private const string BaseUimdPackage = "Characters/Minifig/Batman/DA_UIMD_Batman";
    private const string SrcUimdPkg = "/Game/Characters/Minifig/Batman/DA_UIMD_Batman";
    private const string SrcUimdAsset = "DA_UIMD_Batman";

    // Base icon object paths (package == object for a Texture2D top-level asset).
    public const string SrcMenuIcon = "/Game/UI/Icons/Characters/T_UI_IconChar_Batman_TheBatman2025_Menu_BCA";
    public const string SrcLeftIcon = "/Game/UI/Icons/Characters/T_UI_IconChar_Batman_TheBatman2025_Left_BCA";
    public const string SrcRightIcon = "/Game/UI/Icons/Characters/T_UI_IconChar_Batman_TheBatman2025_Right_BCA";
    public const string SrcSuitIcon = "/Game/UI/Icons/Suits/T_UI_IconSuit_Batman_TheBatman2025_BCA";

    public string ProjectRoot { get; }

    public UimdGenService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public sealed class GenResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string OutputUasset { get; set; } = "";
        public List<string> Repointed { get; } = new();
    }

    /// <summary>
    /// Explicit UIMD-role targets. These are separate from the legacy source-to-target map so a
    /// selected icon can still be written when an extracted donor's original icon path is missing
    /// or uses a name-map layout the catalog reader did not recognize.
    /// </summary>
    public sealed record IconTargets(string Menu, string Suit, string Left, string Right);

    internal sealed record IconPropertyTarget(string Role, string PropertyName, string TargetPath);

    internal static IReadOnlyList<IconPropertyTarget> IconPropertyTargets(IconTargets? targets)
    {
        if (targets is null)
        {
            return [];
        }

        // Native UIMDs name the portrait by the direction the character faces. The authored
        // "Left" texture is stored in RightFacing and vice versa; preserve that game convention.
        return
        [
            new("menu", "MenuIcon", targets.Menu),
            new("suit", "SuitIcon", targets.Suit),
            new("left", "RightFacing", targets.Left),
            new("right", "LeftFacing", targets.Right),
        ];
    }

    public static string ResolveBaseUimdPath()
    {
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        return Path.Combine(root, BaseUimdPackage.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    /// <param name="iconOverrides">Maps donor icon paths to replacement texture paths.</param>
    /// <param name="pawnTag">
    /// Native pawn tag to write into the UIMD's PawnTag (e.g.
    /// "Pawns.Playable.Batman.Electric"). Null/empty leaves the inherited tag - the
    /// §7.2 gap for legacy/donor suits.
    /// </param>
    /// <param name="descriptionTableObjectPath">
    /// Object path of the mod's StringTable (ST_&lt;ModId&gt;.ST_&lt;ModId&gt;) for the
    /// Description/LockedDescription FText. Null skips text repointing (the inherited
    /// base description - e.g. the Zoo-activity text - is intentionally NOT retained
    /// when a key is provided; see §7.1).
    /// </param>
    public GenResult Generate(
        string outputBasePath,
        string uimdPackagePath,
        IReadOnlyDictionary<string, string>? iconOverrides = null,
        string? pawnTag = null,
        string? descriptionTableObjectPath = null,
        string? descriptionKey = null,
        string? lockedDescriptionKey = null,
        NativeMetadataDonorService.Donor? donor = null,
        IconTargets? iconTargets = null)
    {
        var result = new GenResult();
        try
        {
            var sourceUasset = donor?.UimdUassetPath;
            if (donor is not null && (string.IsNullOrWhiteSpace(sourceUasset) || !File.Exists(sourceUasset)))
            {
                result.Status = "missing-donor";
                result.Error = $"Selected donor UIMD is not extracted: {sourceUasset}";
                return result;
            }
            if (donor is null)
            {
                sourceUasset = ResolveBaseUimdPath();
            }
            if (!File.Exists(sourceUasset))
            {
                result.Status = "missing-base";
                result.Error = $"Donor UIMD not found: {sourceUasset}";
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputBasePath)!);
            var baseNoExt = Path.Combine(
                Path.GetDirectoryName(sourceUasset)!,
                Path.GetFileNameWithoutExtension(sourceUasset));
            CopyIfExists(baseNoExt + ".uasset", outputBasePath + ".uasset");
            CopyIfExists(baseNoExt + ".uexp", outputBasePath + ".uexp");

            var asset = new UAsset(outputBasePath + ".uasset", EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);
            var cleanUimdPackagePath = UnrealPathUtil.NormalizePackagePath(uimdPackagePath);
            asset.FolderName = new FString(cleanUimdPackagePath);

            var uimdStem = UnrealPathUtil.AssetName(cleanUimdPackagePath);
            var sourceUimdPackage = !string.IsNullOrWhiteSpace(donor?.UimdPackagePath)
                ? UnrealPathUtil.NormalizePackagePath(donor.UimdPackagePath)
                : SrcUimdPkg;
            var sourceUimdAsset = UnrealPathUtil.AssetName(sourceUimdPackage);
            var replacements = new List<KeyValuePair<string, string>>
            {
                new(sourceUimdPackage, cleanUimdPackagePath),
                new(sourceUimdAsset, uimdStem),
            };
            var cleanPackageTargets = new List<string> { cleanUimdPackagePath };

            // Explicit role targets are property-scoped and therefore safe when a donor reuses one
            // texture for multiple roles. Retain global name-map replacement only for legacy
            // callers that have not supplied role targets.
            if (iconOverrides is not null && iconTargets is null)
            {
                var sourceIcons = donor is null
                    ? new[] { SrcMenuIcon, SrcLeftIcon, SrcRightIcon, SrcSuitIcon }
                    : new[] { donor.IconPaths.Menu, donor.IconPaths.Suit, donor.IconPaths.Left, donor.IconPaths.Right };
                foreach (var src in sourceIcons.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (iconOverrides.TryGetValue(src, out var target) && !string.IsNullOrWhiteSpace(target))
                    {
                        var targetPackagePath = UnrealPathUtil.NormalizePackagePath(target);
                        // Honor an explicit object name if the modder gave one
                        // (e.g. "...ElectricSuitFront.0"); otherwise default to the
                        // package stem. Some cooked textures export their object as
                        // "0" rather than matching the package name.
                        var targetObjectName = ExplicitObjectName(target) ?? UnrealPathUtil.AssetName(targetPackagePath);

                        replacements.Add(new(src, targetPackagePath));
                        replacements.Add(new(UnrealPathUtil.AssetName(src), targetObjectName));
                        cleanPackageTargets.Add(targetPackagePath);
                    }
                }
            }

            // Exact-match per name-map entry (whole-entry, overlap-proof).
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in replacements)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value) && pair.Key != pair.Value)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            var nameMap = asset.GetNameMapIndexList();
            for (var i = 0; i < nameMap.Count; i++)
            {
                var original = nameMap[i].ToString();
                if (map.TryGetValue(original, out var patched))
                {
                    asset.SetNameReference(i, new FString(patched));
                    result.Repointed.Add($"{original} -> {patched}");
                }
            }

            UnrealPathUtil.RepairSplitPathNameMapEntries(
                asset,
                cleanPackageTargets,
                result.Repointed);

            // Name-map substitution is retained for legacy callers, but it cannot be the final
            // authority: if donor icon discovery returned an empty source path, there is nothing
            // to substitute and the cloned UIMD silently keeps the base icon. Patch the four known
            // soft-object properties by role and verify the in-memory result before writing.
            var explicitIconProperties = IconPropertyTargets(iconTargets)
                .Where(icon => !string.IsNullOrWhiteSpace(icon.TargetPath))
                .ToList();
            foreach (var icon in explicitIconProperties)
            {
                var targetPackage = UnrealPathUtil.NormalizePackagePath(icon.TargetPath);
                var targetObject = ExplicitObjectName(icon.TargetPath) ?? UnrealPathUtil.AssetName(targetPackage);
                if (!ExtractedPackagePathService.IsContentPackagePath(targetPackage) ||
                    string.IsNullOrWhiteSpace(targetObject))
                {
                    throw new InvalidOperationException(
                        $"Selected {icon.Role} icon is not a valid game/DLC content object path: '{icon.TargetPath}'.");
                }
                if (!NativeAssetTextPatch.SetOrAddSoftObject(
                        asset,
                        icon.PropertyName,
                        targetPackage,
                        targetObject,
                        "PawnTag",
                        "MenuIcon",
                        "SuitIcon",
                        "RightFacing",
                        "LeftFacing"))
                {
                    throw new InvalidOperationException(
                        $"The donor UIMD has no writable metadata export for the selected {icon.Role} icon.");
                }

                var written = NativeAssetTextPatch.GetSoftReference(asset, icon.PropertyName);
                if (written is null ||
                    !UnrealPathUtil.NormalizePackagePath(written.Value.PackageName)
                        .Equals(targetPackage, StringComparison.OrdinalIgnoreCase) ||
                    !written.Value.AssetName.Equals(targetObject, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The selected {icon.Role} icon did not remain linked in the generated UIMD.");
                }
                result.Repointed.Add($"{icon.PropertyName} -> {targetPackage}.{targetObject}");
            }

            // Native-suit identity + localization (property-level; see §7.2/§7.3).
            if (!string.IsNullOrWhiteSpace(pawnTag))
            {
                if (NativeAssetTextPatch.SetGameplayTag(asset, "PawnTag", pawnTag!.Trim()))
                {
                    result.Repointed.Add($"PawnTag -> {pawnTag!.Trim()}");
                }
            }
            if (!string.IsNullOrWhiteSpace(descriptionTableObjectPath))
            {
                if (!string.IsNullOrWhiteSpace(descriptionKey) &&
                    NativeAssetTextPatch.SetStringTableText(asset, "Description", descriptionTableObjectPath!, descriptionKey!))
                {
                    result.Repointed.Add($"Description -> {descriptionTableObjectPath}:{descriptionKey}");
                }
                if (!string.IsNullOrWhiteSpace(lockedDescriptionKey) &&
                    NativeAssetTextPatch.SetStringTableText(asset, "LockedDescription", descriptionTableObjectPath!, lockedDescriptionKey!))
                {
                    result.Repointed.Add($"LockedDescription -> {descriptionTableObjectPath}:{lockedDescriptionKey}");
                }
            }

            asset.Write(outputBasePath + ".uasset");
            if (explicitIconProperties.Count > 0)
            {
                var writtenAsset = new UAsset(
                    outputBasePath + ".uasset",
                    EngineVersion.VER_UE5_6,
                    LoadMappings(),
                    CustomSerializationFlags.SkipPreloadDependencyLoading);
                foreach (var icon in explicitIconProperties)
                {
                    var targetPackage = UnrealPathUtil.NormalizePackagePath(icon.TargetPath);
                    var targetObject = ExplicitObjectName(icon.TargetPath) ?? UnrealPathUtil.AssetName(targetPackage);
                    var written = NativeAssetTextPatch.GetSoftReference(writtenAsset, icon.PropertyName);
                    if (written is null ||
                        !UnrealPathUtil.NormalizePackagePath(written.Value.PackageName)
                            .Equals(targetPackage, StringComparison.OrdinalIgnoreCase) ||
                        !written.Value.AssetName.Equals(targetObject, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"The generated UIMD was written, but its {icon.Role} icon still points at the donor asset.");
                    }
                }
                result.Repointed.Add($"verified {explicitIconProperties.Count} icon property link(s) after write");
            }
            result.OutputUasset = outputBasePath + ".uasset";
            result.Status = "created";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>Returns the object name after the last '.' (after the last '/'), or null if none.</summary>
    private static string? ExplicitObjectName(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }
        var raw = rawPath.Trim().Trim('\'', '"');
        var lastSlash = raw.LastIndexOf('/');
        var dot = raw.IndexOf('.', lastSlash + 1);
        if (dot < 0 || dot == raw.Length - 1)
        {
            return null;
        }
        return raw[(dot + 1)..].Trim();
    }

    private Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
