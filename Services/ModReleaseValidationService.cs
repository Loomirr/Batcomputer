using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Release-level preflight for a native-suit mod. This checks the saved authoring
/// inputs before Batcomputer creates a stage; the build then adds its existing
/// staged-asset checks to the same report before retoc is allowed to run.
/// </summary>
public sealed class ModReleaseValidationService
{
    public sealed record SuitInput(ModSuitEntry Entry, string ProjectPath, NativeSuitProject? Project, string? LoadError = null);
    public sealed record Finding(string Severity, string Area, string Message, string SuitId = "");

    public sealed class Result
    {
        public List<Finding> Findings { get; } = new();
        public int ErrorCount => Findings.Count(f => f.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        public int WarningCount => Findings.Count(f => f.Severity.Equals("WARN", StringComparison.OrdinalIgnoreCase));
        public bool Passed => ErrorCount == 0;

        public void AddError(string area, string message, string suitId = "") =>
            Findings.Add(new Finding("ERROR", area, message, suitId));

        public void AddWarning(string area, string message, string suitId = "") =>
            Findings.Add(new Finding("WARN", area, message, suitId));

        public void AddInfo(string area, string message, string suitId = "") =>
            Findings.Add(new Finding("INFO", area, message, suitId));
    }

    public Result ValidateAuthoring(
        NativeSuitModProject mod,
        IReadOnlyList<SuitInput> inputs,
        string exportContentRoot,
        string generatedRoot,
        string? installedContentPacksRoot)
    {
        var result = new Result();
        ValidateModIdentity(mod, result);

        var enabled = inputs.Where(input => input.Entry.Enabled).ToList();
        var enabledBricks = (mod.RedBricks ?? []).Where(brick => brick.Enabled).ToList();
        if (enabled.Count == 0 && enabledBricks.Count == 0)
        {
            result.AddError("mod", "The mod has no enabled suits or Red Bricks.");
            return result;
        }

        var suitIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pawnTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packageOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textureOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registryRows = new List<RegistryPluginService.RegistryRow>();

        foreach (var input in enabled)
        {
            var entryId = (input.Entry.SuitId ?? "").Trim();
            if (input.Project is null)
            {
                result.AddError("suit project",
                    string.IsNullOrWhiteSpace(input.LoadError)
                        ? $"Could not load the saved suit project: {input.ProjectPath}"
                        : input.LoadError,
                    entryId);
                continue;
            }

            var suit = input.Project;
            var suitId = (suit.SlotId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(suitId))
            {
                result.AddError("identity", "The suit ID is empty.", entryId);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entryId) && !entryId.Equals(suitId, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("identity", $"The mod entry says '{entryId}', but the saved suit ID is '{suitId}'. Re-add the suit to this mod.", suitId);
            }
            AddUnique(suitIds, suitId, suitId, "suit ID", result, suitId);

            ValidateBase(suit, result, suitId);
            ValidatePawnTag(suit, pawnTags, result, suitId);
            ValidateTargetPackages(suit, packageOwners, registryRows, result, suitId);
            ValidateGeneratedTextures(suit, textureOwners, result, suitId);
            ValidateIcons(suit, result, suitId);
            ValidateMaterialAssignments(suit, exportContentRoot, generatedRoot, result, suitId);
        }

        var redBrickTexturePackages = ValidateRedBrickTextures(mod, enabledBricks, packageOwners, result);
        ValidateRedBricks(mod, enabledBricks, redBrickTexturePackages, packageOwners, registryRows, result);

        foreach (var error in RegistryPluginService.ValidateRows(registryRows))
        {
            result.AddError("Asset Registry", error);
        }

        ValidateInstalledCollisions(installedContentPacksRoot, mod.ModId, suitIds, pawnTags, packageOwners, result);
        return result;
    }

    private static void ValidateRedBricks(
        NativeSuitModProject mod,
        IReadOnlyList<ModRedBrickEntry> bricks,
        IReadOnlySet<string> redBrickTexturePackages,
        Dictionary<string, string> packageOwners,
        List<RegistryPluginService.RegistryRow> registryRows,
        Result result)
    {
        if (bricks.Count == 0) return;

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modId = (mod.ModId ?? "").Trim();
        var payloadPackage = $"/Game/Mods/{modId}/RedBricks/DA_RedBrickData_{modId}";
        AddUnique(packageOwners, payloadPackage, "Red Bricks", "Red Brick package path", result, "Red Bricks");
        registryRows.Add(new RegistryPluginService.RegistryRow(
            payloadPackage, "RedBrickMetaDataAsset", "/Script/Dinner.RedBrickMetaDataAsset"));

        foreach (var brick in bricks)
        {
            var id = (brick.BrickId ?? "").Trim();
            var label = string.IsNullOrWhiteSpace(brick.DisplayName) ? "Red Brick" : brick.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(id, ModProjectService.DeriveModId(id), StringComparison.Ordinal))
            {
                result.AddError("Red Brick", $"'{label}' needs a package-safe Brick ID.", label);
                continue;
            }
            if (string.IsNullOrWhiteSpace(brick.DisplayName))
            {
                result.AddError("Red Brick", $"'{id}' needs a display name.", id);
            }
            AddUnique(ids, id, label, "Red Brick ID", result, id);
            if (!string.Equals(brick.EffectPreset, "TintOnly", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("Red Brick", $"'{label}' uses an unsupported effect preset.", id);
            }
            foreach (var row in new[] { brick.PrimaryColourRow, brick.SecondaryColourRow, brick.TertiaryColourRow })
            {
                if (!RedBrickPalette.Contains(row))
                {
                    result.AddError("Red Brick", $"'{label}' has an unknown LEGO colour row '{row}'.", id);
                }
            }
            var usesCookedIcon = !string.IsNullOrWhiteSpace(brick.IconTexturePackagePath);
            if (!usesCookedIcon && string.IsNullOrWhiteSpace(brick.IconSourcePng))
            {
                result.AddError("Red Brick", $"'{label}' needs a 512x512 PNG menu icon or a cooked UI texture from this mod.", id);
            }
            else if (!usesCookedIcon && (!File.Exists(brick.IconSourcePng) || !Path.GetExtension(brick.IconSourcePng).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            {
                result.AddError("Red Brick", $"'{label}' icon must point at a readable PNG.", id);
            }
            else if (!usesCookedIcon)
            {
                try
                {
                    using var icon = System.Drawing.Image.FromFile(brick.IconSourcePng);
                    if (icon.Width != 512 || icon.Height != 512)
                    {
                        result.AddError("Red Brick", $"'{label}' icon must be exactly 512x512 pixels.", id);
                    }
                }
                catch
                {
                    result.AddError("Red Brick", $"'{label}' icon could not be opened as a PNG.", id);
                }
            }
            else if (!redBrickTexturePackages.Contains(UnrealPathUtil.NormalizePackagePath(brick.IconTexturePackagePath)))
            {
                result.AddError("Red Brick", $"'{label}' must use a cooked Red Brick icon owned by this mod. Open Red Bricks > Icons to cook one.", id);
            }

            var paths = new[]
            {
                (Package: $"/Game/Mods/{modId}/RedBricks/DA_RedBrickEffectDefinition_{id}", Type: "RedBrickEffectDefinition", Class: "/Script/Dinner.RedBrickEffectDefinition"),
                (Package: $"/Game/Mods/{modId}/RedBricks/DA_Collectable_RedBrick_{id}", Type: "TtCollectablesMetaData", Class: "/Script/TtCollectables.TtCollectablesMetaData"),
                (Package: $"/Game/Mods/{modId}/Progress/PROG_RedBricks_{id}", Type: "TtGameProgressDefinitionSet", Class: "/Script/TtGameProgress.TtGameProgressDefinitionSet"),
            };
            foreach (var item in paths)
            {
                AddUnique(packageOwners, item.Package, id, "Red Brick package path", result, id);
                registryRows.Add(new RegistryPluginService.RegistryRow(item.Package, item.Type, item.Class));
            }
        }
    }

    private static HashSet<string> ValidateRedBrickTextures(
        NativeSuitModProject mod,
        IReadOnlyList<ModRedBrickEntry> bricks,
        Dictionary<string, string> packageOwners,
        Result result)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPackages = new HashSet<string>(
            bricks
                .Select(brick => UnrealPathUtil.NormalizePackagePath(brick.IconTexturePackagePath))
                .Where(package => !string.IsNullOrWhiteSpace(package)),
            StringComparer.OrdinalIgnoreCase);
        if (referencedPackages.Count == 0)
        {
            return packages;
        }

        var modId = (mod.ModId ?? "").Trim();
        var expectedPrefix = $"/Game/Mods/{modId}/Textures/";
        foreach (var texture in mod.RedBrickTextures ?? Enumerable.Empty<GeneratedTextureEntry>())
        {
            var package = UnrealPathUtil.NormalizePackagePath(texture.PackagePath);
            if (!referencedPackages.Contains(package))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(texture.DisplayName) ? "unnamed Red Brick icon" : texture.DisplayName;
            if (!string.Equals(texture.Kind?.Trim(), "RedBrick", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("Red Brick icons", $"'{label}' is not marked as a RedBrick texture.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(package) || !package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("Red Brick icons", $"'{label}' must live under {expectedPrefix}.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(texture.CookProfile))
            {
                result.AddError("Red Brick icons", $"'{label}' has no selected cook profile.");
            }
            if (string.IsNullOrWhiteSpace(texture.SourcePng) || !File.Exists(texture.SourcePng))
            {
                result.AddWarning("Red Brick icons", $"'{label}' has no readable source PNG. Its already-cooked output can still build, but it cannot be re-cooked.");
            }
            packages.Add(package);
            AddUnique(packageOwners, package, label, "Red Brick icon package", result, "Red Brick icons");
        }
        return packages;
    }

    private static void ValidateModIdentity(NativeSuitModProject mod, Result result)
    {
        var modId = (mod.ModId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modId))
        {
            result.AddError("mod", "The Mod ID is empty.");
            return;
        }
        if (!string.Equals(modId, ModProjectService.DeriveModId(modId), StringComparison.Ordinal))
        {
            result.AddError("mod", $"Mod ID '{modId}' is not filesystem/package safe. Use letters and numbers only.");
        }
        if (string.IsNullOrWhiteSpace(mod.DisplayName))
        {
            result.AddWarning("mod", "The display name is empty; the release will be harder to identify in Batcomputer.");
        }
        if (!string.Equals(mod.PackageBaseName, $"{modId}_P", StringComparison.Ordinal))
        {
            result.AddError("mod", "The package base name does not match the Mod ID. Reopen or save the mod to refresh its derived identity.");
        }
        if (!string.Equals(mod.ContentRoot, $"/Game/Mods/{modId}", StringComparison.Ordinal))
        {
            result.AddError("mod", "The content root does not match the Mod ID. Reopen or save the mod to refresh its derived identity.");
        }
    }

    private static void ValidateBase(NativeSuitProject suit, Result result, string suitId)
    {
        var eligibility = BaseEligibilityService.Evaluate(suit);
        if (!eligibility.IsReady)
        {
            result.AddError("base", eligibility.Detail, suitId);
        }
    }

    private static void ValidatePawnTag(
        NativeSuitProject suit,
        Dictionary<string, string> pawnTags,
        Result result,
        string suitId)
    {
        var tag = (suit.PawnTag ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            result.AddError("PawnTag", "The suit has no PawnTag.", suitId);
            return;
        }
        if (!tag.StartsWith("Pawns.Playable.", StringComparison.OrdinalIgnoreCase))
        {
            result.AddWarning("PawnTag", $"'{tag}' is outside the Pawns.Playable namespace.", suitId);
        }
        AddUnique(pawnTags, tag, suitId, "PawnTag", result, suitId);
    }

    private static void ValidateTargetPackages(
        NativeSuitProject suit,
        Dictionary<string, string> packageOwners,
        List<RegistryPluginService.RegistryRow> registryRows,
        Result result,
        string suitId)
    {
        var targets = suit.TargetPackages ?? new TargetPackages();
        var playable = ValidateGeneratedPackage("playable", targets.Playable, packageOwners, result, suitId);
        var cutscene = ValidateGeneratedPackage("cutscene", targets.Cutscene, packageOwners, result, suitId);
        var dcmd = ValidateGeneratedPackage("DCMD", targets.Dcmd, packageOwners, result, suitId);
        var uimd = string.IsNullOrWhiteSpace(dcmd) ? "" : DeriveUimdPackagePath(dcmd);
        ValidateGeneratedPackage("UIMD", uimd, packageOwners, result, suitId);

        var root = ModFolderFromPackagePath(playable);
        foreach (var (role, package) in new[] { ("cutscene", cutscene), ("DCMD", dcmd), ("UIMD", uimd) })
        {
            if (!string.IsNullOrWhiteSpace(root) && !string.Equals(root, ModFolderFromPackagePath(package), StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("package paths", $"The {role} package belongs to a different /Game/Mods root than the playable package.", suitId);
            }
        }
        if (!string.IsNullOrWhiteSpace(dcmd))
        {
            registryRows.Add(new RegistryPluginService.RegistryRow(dcmd));
        }
    }

    private static string ValidateGeneratedPackage(
        string role,
        string? rawPackage,
        Dictionary<string, string> packageOwners,
        Result result,
        string suitId)
    {
        var raw = rawPackage?.Trim() ?? "";
        var normalized = UnrealPathUtil.NormalizePackagePath(raw);
        if (string.IsNullOrWhiteSpace(raw))
        {
            result.AddError("package paths", $"The {role} package path is empty.", suitId);
            return "";
        }
        if (!string.Equals(raw, normalized, StringComparison.Ordinal) || !normalized.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError("package paths", $"The {role} package must be a clean /Game/Mods package path: '{raw}'.", suitId);
            return "";
        }
        AddUnique(packageOwners, normalized, suitId, $"{role} package path", result, suitId);
        return normalized;
    }

    private static void ValidateGeneratedTextures(
        NativeSuitProject suit,
        Dictionary<string, string> textureOwners,
        Result result,
        string suitId)
    {
        var expectedRoot = ModFolderFromPackagePath(suit.TargetPackages?.Playable);
        foreach (var texture in suit.GeneratedTextures ?? Enumerable.Empty<GeneratedTextureEntry>())
        {
            var label = string.IsNullOrWhiteSpace(texture.DisplayName) ? "unnamed texture" : texture.DisplayName;
            var package = UnrealPathUtil.NormalizePackagePath(texture.PackagePath ?? "");
            if (string.IsNullOrWhiteSpace(texture.PackagePath) || !string.Equals(texture.PackagePath, package, StringComparison.Ordinal) ||
                !package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("texture", $"'{label}' has no clean /Game/Mods texture package path.", suitId);
                continue;
            }
            var expectedPrefix = string.IsNullOrWhiteSpace(expectedRoot) ? "" : $"/Game/Mods/{expectedRoot}/Textures/";
            if (!string.IsNullOrWhiteSpace(expectedPrefix) && !package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("texture", $"'{label}' must live under {expectedPrefix}.", suitId);
            }
            AddUnique(textureOwners, package, suitId, "generated texture package", result, suitId);

            if (string.IsNullOrWhiteSpace(texture.CookProfile))
            {
                result.AddWarning("texture",
                    $"'{label}' is a legacy texture with no recorded cook profile. Packaging will preserve its existing cooked output and stop if that output is incomplete; use Change cook profile before a future recook.",
                    suitId);
            }
            if (string.IsNullOrWhiteSpace(texture.TemplateJson) || !File.Exists(texture.TemplateJson))
            {
                result.AddError("texture", $"'{label}' is missing its native cook template.", suitId);
            }
            if (string.IsNullOrWhiteSpace(texture.OutputRoot))
            {
                result.AddError("texture", $"'{label}' has no generated-output folder.", suitId);
            }
            if (string.IsNullOrWhiteSpace(texture.SourcePng) || !File.Exists(texture.SourcePng))
            {
                result.AddWarning("texture", $"'{label}' has no readable source PNG. It can only build while its existing cooked output stays valid.", suitId);
            }
            if (texture.CookWidth <= 0 || texture.CookHeight <= 0 || string.IsNullOrWhiteSpace(texture.CookPixelFormat))
            {
                result.AddWarning("texture", $"'{label}' has incomplete recorded cook dimensions or pixel format.", suitId);
            }
            if (!string.IsNullOrWhiteSpace(texture.ObjectPath) &&
                !texture.ObjectPath.StartsWith(package + ".", StringComparison.OrdinalIgnoreCase))
            {
                result.AddWarning("texture", $"'{label}' has an object path that does not match its package path.", suitId);
            }
        }
    }

    private static void ValidateIcons(NativeSuitProject suit, Result result, string suitId)
    {
        var generated = new HashSet<string>(
            (suit.GeneratedTextures ?? Enumerable.Empty<GeneratedTextureEntry>())
                .Select(texture => UnrealPathUtil.NormalizePackagePath(texture.PackagePath ?? ""))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (slot, rawPath) in new[]
        {
            ("menu", suit.IconMenu), ("suit", suit.IconSuit), ("left", suit.IconLeft), ("right", suit.IconRight),
        })
        {
            var path = UnrealPathUtil.NormalizePackagePath(rawPath ?? "");
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("icons", $"The {slot} icon is not a /Game package path.", suitId);
            }
            else if (path.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) && !generated.Contains(path))
            {
                result.AddWarning("icons", $"The {slot} icon points at a mod texture that is not listed in this suit's generated textures.", suitId);
            }
        }
    }

    private static void ValidateMaterialAssignments(
        NativeSuitProject suit,
        string exportContentRoot,
        string generatedRoot,
        Result result,
        string suitId)
    {
        var ownModRoot = ModFolderFromPackagePath(suit.TargetPackages?.Playable);
        var materialSourceRoots = MaterialSourceRoots(suit, exportContentRoot, generatedRoot);
        foreach (var assignment in suit.MaterialAssignments ?? Enumerable.Empty<SavedMaterialAssignment>())
        {
            var package = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath ?? "");
            var target = string.IsNullOrWhiteSpace(assignment.Component) ? "an unnamed component" : assignment.Component;
            if (string.IsNullOrWhiteSpace(package) || !package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("material", $"{target} has no valid material package path.", suitId);
                continue;
            }
            if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) continue;

            if (!string.Equals(ownModRoot, ModFolderFromPackagePath(package), StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("material", $"{target} points at a material from another mod root: {package}", suitId);
                continue;
            }
            var materialExists = materialSourceRoots
                .Select(root => PackagePathToContentBase(root, package) + ".uasset")
                .Any(File.Exists);
            if (!materialExists)
            {
                result.AddError("material",
                    $"{target} uses generated material '{package}', but its .uasset is missing from every valid material source (the export folder and this suit's saved authoring stages).",
                    suitId);
            }
        }
    }

    /// <summary>
    /// Generated MIs may be stored directly in the shared export folder, or in the
    /// editable stage owned by a saved suit. Mod packaging reads the latter when it
    /// prepares a suit, so both locations are valid authoring sources.
    /// </summary>
    private static IReadOnlyList<string> MaterialSourceRoots(
        NativeSuitProject suit,
        string exportContentRoot,
        string generatedRoot)
    {
        var roots = new List<string> { exportContentRoot };
        if (!string.IsNullOrWhiteSpace(generatedRoot) && !string.IsNullOrWhiteSpace(suit.SlotId))
        {
            var suitRoot = Path.Combine(generatedRoot, "NativeSuitGuiProjects", suit.SlotId);
            foreach (var stage in new[] { "GraftedPartStage", "GraftedTorso2Stage", "PatchedNameMapStage" })
            {
                roots.Add(Path.Combine(suitRoot, stage, "LEGOBatmanLotDK", "Content"));
            }

            // Older saved projects can retain their last successful individual
            // IoStore stage while their editable stage is being rebuilt.
            roots.Add(Path.Combine(suitRoot, "IoStore", "Stage", "LEGOBatmanLotDK", "Content"));
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateInstalledCollisions(
        string? installedContentPacksRoot,
        string activeModId,
        IReadOnlyDictionary<string, string> suitIds,
        IReadOnlyDictionary<string, string> pawnTags,
        IReadOnlyDictionary<string, string> packageOwners,
        Result result)
    {
        if (string.IsNullOrWhiteSpace(installedContentPacksRoot) || !Directory.Exists(installedContentPacksRoot)) return;

        try
        {
            foreach (var manifestPath in Directory.EnumerateDirectories(installedContentPacksRoot)
                         .Select(packDirectory => Path.Combine(packDirectory, "mod.json"))
                         .Where(File.Exists))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var installedModId = root.TryGetProperty("mod_id", out var modId) ? modId.GetString() ?? "" : "";
                if (installedModId.Equals(activeModId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!root.TryGetProperty("suits", out var suits) || suits.ValueKind != JsonValueKind.Array) continue;

                foreach (var installed in suits.EnumerateArray())
                {
                    var installedSuitId = ReadString(installed, "suit_id");
                    var installedTag = ReadString(installed, "pawn_tag");
                    var installedDcmd = UnrealPathUtil.NormalizePackagePath(ReadString(installed, "dcmd"));
                    if (!string.IsNullOrWhiteSpace(installedSuitId) && suitIds.TryGetValue(installedSuitId, out var ownSuit))
                    {
                        result.AddError("installed collision", $"Suit ID '{installedSuitId}' is already installed by mod '{installedModId}'.", ownSuit);
                    }
                    if (!string.IsNullOrWhiteSpace(installedTag) && pawnTags.TryGetValue(installedTag, out var ownTagSuit))
                    {
                        result.AddError("installed collision", $"PawnTag '{installedTag}' is already installed by mod '{installedModId}'.", ownTagSuit);
                    }
                    if (!string.IsNullOrWhiteSpace(installedDcmd) && packageOwners.TryGetValue(installedDcmd, out var ownPackageSuit))
                    {
                        result.AddError("installed collision", $"DCMD package '{installedDcmd}' is already installed by mod '{installedModId}'.", ownPackageSuit);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.AddWarning("installed collision", $"Could not inspect installed mod manifests: {ex.Message}");
        }
    }

    private static void AddUnique(
        Dictionary<string, string> owners,
        string key,
        string owner,
        string label,
        Result result,
        string suitId)
    {
        if (owners.TryGetValue(key, out var existing))
        {
            result.AddError("collision", $"Duplicate {label} '{key}' is used by both '{existing}' and '{owner}'.", suitId);
            return;
        }
        owners[key] = owner;
    }

    private static string DeriveUimdPackagePath(string dcmdPackagePath)
    {
        var mod = ModFolderFromPackagePath(dcmdPackagePath);
        var stem = UnrealPathUtil.AssetName(dcmdPackagePath);
        const string prefix = "DA_DCMD_";
        const string suffix = "_Playable";
        if (stem.StartsWith(prefix, StringComparison.Ordinal)) stem = stem[prefix.Length..];
        if (stem.EndsWith(suffix, StringComparison.Ordinal)) stem = stem[..^suffix.Length];
        return string.IsNullOrWhiteSpace(mod) ? "" : $"/Game/Mods/{mod}/UI/DA_UIMD_{stem}";
    }

    private static string ModFolderFromPackagePath(string? packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath ?? "");
        const string prefix = "/Game/Mods/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        var remaining = normalized[prefix.Length..];
        var slash = remaining.IndexOf('/');
        return slash > 0 ? remaining[..slash] : "";
    }

    private static string PackagePathToContentBase(string contentRoot, string packagePath)
    {
        var relative = packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(contentRoot, relative);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

}
