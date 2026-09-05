using System.Reflection;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Suit-local DPRD AbilitySet authoring and the corresponding Toybox surface.</summary>
public sealed partial class MainForm
{
    private void RefreshAbilityTiles(string? type)
    {
        EnsureProject();
        if (_currentProject is null)
        {
            ShowVirtualTiles(
                Array.Empty<VirtualTilePanel.Tile>(),
                header: "Abilities are inherited from the selected gameplay donor.",
                emptyMessage: "Open or create a suit before editing its abilities.");
            return;
        }

        var profile = _currentProject.AbilityLoadout;
        var enabledSets = profile?.AbilitySets.Count(set => set.Enabled) ?? 0;
        var addedGrants = profile?.AbilitySets.Sum(set => set.AddedGameplayAbilities.Count) ?? 0;
        var removedGrants = profile?.AbilitySets.Sum(set => set.RemovedGameplayAbilities.Count) ?? 0;
        var state = profile is null
            ? "Gameplay donor unchanged"
            : $"{enabledSets} enabled set(s) · {addedGrants} added / {removedGrants} removed grant(s)";
        if (!string.IsNullOrWhiteSpace(profile?.FightingStyleId) &&
            FightingStyleProfileService.Find(profile.FightingStyleId) is { } activeStyle)
        {
            state += $" · {activeStyle.DisplayName} bundle";
        }

        var tiles = new List<VirtualTilePanel.Tile>
        {
            new()
            {
                Section = "LOADOUT",
                Title = "Edit suit abilities",
                Subtitle = "ability sets + gameplay grants",
                Accent = Theme.Abilities,
                Dashed = profile is null,
                OnClick = () => _ = OpenAbilityExplorerAsync(),
                ToolTip = "Inspect the gameplay donor's inherited AbilitySets, add or remove sets, reorder them, and author suit-local gameplay-ability grants.",
            },
        };

        // The path-only fallback is intentionally cheap enough for every refresh. The extractor-backed
        // service, when present, is only invoked by the editor because it may inspect cooked assets.
        var fallback = BuildFallbackAbilityCatalog(_currentProject);
        var search = CurrentToyboxSearch();
        if (string.Equals(type, "Ability-set library", StringComparison.OrdinalIgnoreCase))
        {
            tiles.AddRange(fallback.AvailableAbilitySets
                .Where(set => MatchesAbilitySearch(search, set.DisplayName, set.PackagePath, set.Category, set.Source))
                .Take(1000)
                .Select(set => new VirtualTilePanel.Tile
                {
                    Section = "ABILITY SETS",
                    Title = string.IsNullOrWhiteSpace(set.DisplayName) ? UnrealPathUtil.AssetName(set.PackagePath) : set.DisplayName,
                    Subtitle = $"{set.Category} · {set.Source}",
                    Accent = set.IsCore ? Theme.Warn : Theme.Abilities,
                    ToolTip = set.PackagePath + (set.IsCore ? "\nCore set: advanced unlock required before destructive edits." : ""),
                    OnClick = () => _ = OpenAbilityExplorerAsync(set.PackagePath, libraryView: true),
                }));
        }
        else if (string.Equals(type, "Gameplay abilities", StringComparison.OrdinalIgnoreCase))
        {
            tiles.AddRange(fallback.GameplayAbilities
                .Where(grant => MatchesAbilitySearch(search, grant.PackagePath, grant.InputTag, grant.SourceAbilitySetPackage))
                .Take(1000)
                .Select(grant => new VirtualTilePanel.Tile
                {
                    Section = "GAMEPLAY ABILITIES",
                    Title = UnrealPathUtil.AssetName(grant.PackagePath),
                    Subtitle = string.IsNullOrWhiteSpace(grant.InputTag) ? "passive / no input tag" : grant.InputTag,
                    Accent = Theme.Abilities,
                    ToolTip = grant.PackagePath,
                    OnClick = () => _ = OpenAbilityExplorerAsync(grant.SourceAbilitySetPackage, grant.PackagePath),
                }));
        }

        ShowVirtualTiles(
            tiles,
            header: $"{state}. Changes are written only to this suit's generated DPRD and AbilitySet clones; the base-game donor remains untouched.",
            emptyMessage: "No abilities matched the current search.");
    }

    private async Task OpenAbilityExplorerAsync(
        string? initialSetPackage = null,
        string? initialGrantPackage = null,
        bool libraryView = false)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("open the ability editor"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            Dialog.Warn(this, "Open a suit first", "Open or create a suit before editing its abilities.");
            return;
        }

        var project = _currentProject;
        var editContext = CaptureCurrentProjectEditContext(project);

        AbilityEditorCatalog catalog;
        UseWaitCursor = true;
        try
        {
            catalog = await Task.Run(() => BuildAbilityEditorCatalog(project));
        }
        catch (Exception ex)
        {
            if (!CurrentProjectEditContextMatches(editContext))
            {
                AppendLog("Ability inspection stopped because another suit or workspace was selected.");
                return;
            }
            Dialog.Error(
                this,
                "Abilities could not be inspected",
                "Batcomputer could not read the gameplay donor's DPRD and AbilitySets. Nothing was changed.\n\n" + ex.Message,
                windowTitle: "Ability Explorer");
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Ability inspection stopped because another suit or workspace was selected.");
            return;
        }

        if (string.IsNullOrWhiteSpace(catalog.DonorDprdPackage) ||
            string.IsNullOrWhiteSpace(catalog.DonorAbilitySetFingerprint) ||
            catalog.InheritedAbilitySets.Count == 0)
        {
            var detail = catalog.Warnings.FirstOrDefault() ??
                         "The selected gameplay donor did not expose a readable ordered AbilitySet loadout.";
            Dialog.Error(
                this,
                "Abilities could not be inspected",
                detail + "\n\nRefresh the game assets and verify the configured UE 5.6 .usmap, then try again.",
                windowTitle: "Ability Explorer");
            return;
        }

        foreach (var warning in catalog.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
        {
            AppendLog("Ability catalog: " + warning);
        }
        if (catalog.SavedLoadoutNeedsRemap)
        {
            Dialog.Warn(
                this,
                "Ability loadout needs remapping",
                "The saved loadout belongs to a different gameplay donor or donor revision. " +
                "The editor opened the current donor's clean loadout instead of applying stale edits. " +
                "Re-create the intended changes and save them for this donor.",
                windowTitle: "Ability Explorer");
        }

        AbilityLoadoutProfile? result;
        try
        {
            using var explorer = new AbilityExplorerForm(
                project,
                catalog,
                initialSetPackage,
                initialGrantPackage,
                libraryView);
            if (explorer.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            result = explorer.ResetToDonorRequested ? null : explorer.ResultProfile;
        }
        catch (Exception ex)
        {
            AppendLog("Ability Explorer failed to open: " + ex);
            Dialog.Error(
                this,
                "Ability Explorer could not open",
                "Batcomputer could not create or display the ability editor. Nothing was changed.\n\n" + ex.Message,
                windowTitle: "Ability Explorer");
            return;
        }

        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Ability edits were discarded because another suit or workspace was selected.");
            return;
        }

        NativeSuitProject rollback;
        try
        {
            rollback = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))
                       ?? throw new InvalidOperationException("The suit snapshot was empty.");
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Abilities were not saved", "Batcomputer could not create a rollback snapshot, so it left the suit unchanged.\n\n" + ex.Message);
            return;
        }

        project.AbilityLoadout = result;

        project.Changes.RemoveAll(change =>
            change.Category.Equals("Abilities", StringComparison.OrdinalIgnoreCase) &&
            change.Target.Equals("loadout", StringComparison.OrdinalIgnoreCase));
        project.Changes.Add(new SavedChange
        {
            When = DateTime.Now.ToString("o"),
            Category = "Abilities",
            Target = "loadout",
            Detail = result is null
                ? "restored gameplay donor"
                : $"{result.AbilitySets.Count(set => set.Enabled)} enabled set(s), " +
                  $"{result.AbilitySets.Sum(set => set.AddedGameplayAbilities.Count)} added and " +
                  $"{result.AbilitySets.Sum(set => set.RemovedGameplayAbilities.Count)} removed grant(s)" +
                  (FightingStyleProfileService.Find(result.FightingStyleId) is { } savedStyle
                      ? $", {savedStyle.DisplayName} dependency bundle"
                      : ""),
            Status = "staged",
        });

        var saveCapture = CaptureCurrentProjectSave(editContext, "save the suit ability loadout");

        try
        {
            var saveResult = await CommitCurrentProjectSaveCaptureAsync(saveCapture);
            RequireCurrentProjectSaveCommitted(saveResult, "save the suit ability loadout");
        }
        catch (CurrentProjectSaveSupersededException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!CurrentProjectEditContextMatches(editContext))
            {
                AppendLog("Ability save failed after another suit or workspace was selected; the current editor was left unchanged.");
                return;
            }
            _currentProject = rollback;
            ApplyProjectToFields(_currentProject);
            UpdateSelectedLabels();
            Dialog.Error(
                this,
                "Abilities were not saved",
                "Batcomputer restored the previous suit because its project file could not be saved. Close anything holding the project file, then retry.\n\n" + ex.Message,
                windowTitle: "Ability Explorer");
            RefreshToyboxTiles();
            PopulateToyboxSlots();
            RefreshInspector();
            return;
        }

        if (!CurrentProjectEditContextMatches(editContext))
        {
            return;
        }

        _session.RaiseChanged();
        AppendLog(result is null
            ? "Restored the gameplay donor's original ability loadout."
            : "Saved a suit-local ability loadout. The base-game donor assets remain unchanged.");
        RefreshToyboxTiles();
        PopulateToyboxSlots();
        RefreshInspector();
    }

    private AbilityEditorCatalog BuildAbilityEditorCatalog(NativeSuitProject project)
    {
        // Keep MainForm decoupled from UAssetAPI. AbilityCatalogService implements this seam when
        // extractor-backed inspection is available; packaged builds still get the shipped path catalog.
        var serviceType = typeof(MainForm).Assembly.GetType("Batcomputer.AbilityCatalogService", throwOnError: false);
        if (serviceType is not null && typeof(IAbilityCatalogSource).IsAssignableFrom(serviceType))
        {
            var instance = CreateAbilityCatalogSource(serviceType);
            if (instance is not null)
            {
                return instance.BuildForProject(project);
            }
        }

        return BuildFallbackAbilityCatalog(project);
    }

    private static IAbilityCatalogSource? CreateAbilityCatalogSource(Type serviceType)
    {
        const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (serviceType.GetProperty("Instance", StaticFlags)?.GetValue(null) is IAbilityCatalogSource singleton)
        {
            return singleton;
        }

        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var usmapPath = AppSettings.Current.EffectiveUsmapPath();
        foreach (var args in new object?[][]
                 {
                     Array.Empty<object?>(),
                     new object?[] { contentRoot },
                     new object?[] { contentRoot, usmapPath },
                 })
        {
            try
            {
                if (Activator.CreateInstance(serviceType, args) is IAbilityCatalogSource source)
                {
                    return source;
                }
            }
            catch (MissingMethodException)
            {
                // Try the next supported constructor shape.
            }
        }
        return null;
    }

    private static AbilityEditorCatalog BuildFallbackAbilityCatalog(NativeSuitProject project)
    {
        var saved = project.AbilityLoadout;
        var sets = GameDataService.Instance.AssetsOfClass("TtAbilitySet")
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
            .Select(asset =>
            {
                var package = UnrealPathUtil.NormalizePackagePath(asset.Path);
                var name = UnrealPathUtil.AssetName(package);
                return new AbilitySetCatalogEntry
                {
                    PackagePath = package,
                    DisplayName = name,
                    Category = AbilityCategory(package),
                    Source = AbilitySource(package),
                    IsCore = IsLikelyCoreAbilitySet(package),
                };
            })
            .GroupBy(set => set.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(set => set.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byPackage = sets.ToDictionary(set => set.PackagePath, StringComparer.OrdinalIgnoreCase);
        var inherited = new List<AbilitySetCatalogEntry>();
        foreach (var selection in saved?.AbilitySets.OrderBy(set => set.Order) ?? Enumerable.Empty<AbilitySetSelection>())
        {
            var package = UnrealPathUtil.NormalizePackagePath(selection.PackagePath);
            if (package.Length == 0) continue;
            if (!byPackage.TryGetValue(package, out var entry))
            {
                entry = new AbilitySetCatalogEntry
                {
                    PackagePath = package,
                    DisplayName = UnrealPathUtil.AssetName(package),
                    Category = AbilityCategory(package),
                    Source = AbilitySource(package),
                    IsCore = IsLikelyCoreAbilitySet(package),
                    IsAvailable = false,
                };
                sets.Add(entry);
                byPackage[package] = entry;
            }
            inherited.Add(entry);
        }

        var grants = GameDataService.Instance.Db.Assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
            .Where(asset =>
                UnrealPathUtil.AssetName(asset.Path).StartsWith("GA_", StringComparison.OrdinalIgnoreCase) ||
                asset.Class.Contains("GameplayAbility", StringComparison.OrdinalIgnoreCase))
            .Select(asset => new GameplayAbilityCatalogEntry
            {
                PackagePath = UnrealPathUtil.NormalizePackagePath(asset.Path),
            })
            .Where(grant => grant.PackagePath.Length > 0)
            .GroupBy(grant => grant.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(grant => grant.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AbilityEditorCatalog
        {
            DonorDprdPackage = saved?.DonorDprdPackage ?? project.DcmdTemplate?.PackagePath ?? "",
            DonorAbilitySetFingerprint = saved?.DonorAbilitySetFingerprint ?? "",
            InheritedAbilitySets = inherited,
            AvailableAbilitySets = sets,
            GameplayAbilities = grants,
            Warnings = new List<string>
            {
                "Using the shipped path-only ability catalog. Refresh/extract game assets to inspect the donor's exact grants and input tags.",
            },
        };
    }

    private static string AbilityCategory(string package)
    {
        var normalized = package.Replace('\\', '/');
        var parent = Path.GetFileName(Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar)) ?? "");
        return string.IsNullOrWhiteSpace(parent) ? "Other" : parent;
    }

    private static string AbilitySource(string package) =>
        package.Contains("/DLC/", StringComparison.OrdinalIgnoreCase) ||
        package.Contains("/AdditionalContent/", StringComparison.OrdinalIgnoreCase)
            ? "Installed DLC"
            : "Base game";

    private static bool IsLikelyCoreAbilitySet(string package)
    {
        var name = UnrealPathUtil.AssetName(package);
        return name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Input", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Movement", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAbilitySearch(string search, params string?[] values) =>
        string.IsNullOrWhiteSpace(search) || values.Any(value =>
            !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase));
}
