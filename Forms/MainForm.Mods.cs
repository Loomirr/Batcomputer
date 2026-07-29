using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Home-screen "Mods" section and the mod build. A mod bundles several suit projects
/// into one release: one pak, one <c>&lt;ModId&gt;PawnTags.ini</c>, one <c>ST_&lt;ModId&gt;</c>
/// StringTable, one <c>mod.json</c>, and one plugin-local cooked AssetRegistry.bin.
/// This partial owns the UI + orchestration; the asset work lives in focused services.
/// </summary>
public sealed partial class MainForm
{
    private ModProjectService ModService => new(_projectRootText.Text.Trim());

    /// <summary>Where a built mod's aggregate outputs land.</summary>
    private string ModBuildRoot(string modId) =>
        Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitModBuilds", modId);

    /// <summary>The portable release archive created from one completed mod build.</summary>
    private string ModReleaseZipPath(string modId) =>
        Path.Combine(ModBuildRoot(modId), $"{modId}-release.zip");

    private async Task<RegistryPluginService.WriterPreparationResult> PrepareRegistryWriterAsync(
        RegistryWriterProgressForm? progressWindow = null)
    {
        AppendLog("Preparing the UE 5.6 Asset Registry writer for future builds...");
        var result = await new RegistryPluginService().PrepareAsync(line =>
        {
            AppendLog("  registry: " + line);
            progressWindow?.UpdateFromWriterLog(line);
        });
        if (result.Succeeded)
        {
            AppendLog($"Asset Registry writer ready ({(result.Rebuilt ? "built" : "cache verified")}).");
            progressWindow?.SetFinished();
            return result;
        }

        AppendLog("Asset Registry writer setup failed: " + result.Error);
        if (!string.IsNullOrWhiteSpace(result.VerificationLine))
        {
            AppendLog("  registry verification: " + result.VerificationLine);
        }
        progressWindow?.SetFailed(result.Error);
        return result;
    }

    internal async Task RunInitialSetupTasksAsync(bool prepareRegistryWriter, bool extractAssets)
    {
        if (prepareRegistryWriter)
        {
            using var writerProgress = new RegistryWriterProgressForm();
            writerProgress.Show(this);
            var result = await PrepareRegistryWriterAsync(writerProgress);
            if (result.Succeeded)
            {
                await Task.Delay(450);
            }
            writerProgress.Close();

            if (!result.Succeeded)
            {
                Dialog.Warn(this, "Registry writer not ready",
                    result.Error + "\n\nBatcomputer will continue with your requested game-asset extraction. You can fix the UE 5.6 or writer-project path in Settings before your first mod build.",
                    "Batcomputer - Setup");
            }
        }

        if (extractAssets)
        {
            await RunFirstTimeAssetExtractionAsync();
        }
    }

    /// <summary>
    /// The command-bar entry point. A suit can be the only entry in a mod, but every
    /// shippable export is still a mod-level trio with its registry and loose files.
    /// </summary>
    private async Task BuildModForCurrentSuitAsync()
    {
        if (_currentProject is null)
        {
            Dialog.Info(this, "Build mod", "Create or open a suit before building a mod.");
            return;
        }
        if (!HasCurrentSuitBase())
        {
            Dialog.Info(this, "Build mod", "Set a playable and cutscene base before building this suit's mod.");
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var suits = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
        var suitPath = suits.SaveProject(_currentProject);
        var matches = FindModsForSuit(suitPath, _currentProject.SlotId);
        string? modPath;

        if (matches.Count == 0)
        {
            var displayName = PromptForText(
                "Create mod for this suit",
                "Exports are mod-based. Give the new one-suit mod a display name:",
                $"{_currentProject.DisplayName} Mod");
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            var modId = ModProjectService.DeriveModId(displayName);
            if (string.IsNullOrWhiteSpace(modId))
            {
                Dialog.Warn(this, "Create mod", "That name does not contain a usable Mod ID.");
                return;
            }
            if (ModService.ListMods().Any(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)))
            {
                Dialog.Warn(this, "Create mod", $"A mod with ID '{modId}' already exists. Add this suit to it from Home.");
                return;
            }

            var mod = new NativeSuitModProject { ModId = modId, DisplayName = displayName.Trim() };
            mod.Suits.Add(new ModSuitEntry
            {
                SuitProjectPath = ModService.MakeRelativeSuitProjectPath(suitPath),
                SuitId = _currentProject.SlotId,
                Enabled = true,
                MenuOrder = 100,
            });
            modPath = ModService.SaveMod(mod);
            AppendLog($"Created one-suit mod '{mod.DisplayName}' ({mod.ModId}) for this export.");
            RefreshHomeTiles();
        }
        else if (matches.Count == 1)
        {
            modPath = matches[0].Path;
        }
        else
        {
            Dialog.Warn(this, "Choose a mod",
                $"This suit belongs to {matches.Count} mods. Open the intended mod from Home and choose Build mod.\n\n" +
                string.Join("\n", matches.Select(m => $"- {m.DisplayName} ({m.ModId})")));
            return;
        }

        await BuildAndInstallModAsync(modPath);
    }

    private void InstallModForCurrentSuit()
    {
        if (_currentProject is null)
        {
            Dialog.Info(this, "Install mod", "Create or open a suit before installing a mod.");
            return;
        }
        if (!HasCurrentSuitBase())
        {
            Dialog.Info(this, "Install mod", "Set a playable and cutscene base before installing this suit's mod.");
            return;
        }

        var suits = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
        var suitPath = suits.SaveProject(_currentProject);
        var matches = FindModsForSuit(suitPath, _currentProject.SlotId);
        if (matches.Count == 1)
        {
            InstallMod(matches[0].Path);
            return;
        }

        var detail = matches.Count == 0
            ? "Build a mod for this suit first. The Build mod button can create a one-suit mod."
            : "This suit belongs to more than one mod. Open the intended mod from Home and choose Install mod.";
        Dialog.Info(this, "Install mod", detail);
    }

    private List<ModProjectService.ModSummary> FindModsForSuit(string suitPath, string suitId)
    {
        var result = new List<ModProjectService.ModSummary>();
        foreach (var summary in ModService.ListMods())
        {
            var mod = ModService.LoadMod(summary.Path);
            if (mod?.Suits is null)
            {
                continue;
            }
            if (mod.Suits.Any(entry =>
                    string.Equals(entry.SuitId, suitId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ModService.ResolveSuitProjectPath(entry), suitPath, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(summary);
            }
        }
        return result;
    }

    /// <summary>Chooses the mod the Home workspace is currently presenting.</summary>
    private (ModProjectService.ModSummary? Summary, NativeSuitModProject? Project) ResolveHomeActiveMod(
        IReadOnlyList<ModProjectService.ModSummary> mods)
    {
        ModProjectService.ModSummary? summary = null;
        if (!string.IsNullOrWhiteSpace(_homeActiveModProjectPath))
        {
            summary = mods.FirstOrDefault(m => string.Equals(m.Path, _homeActiveModProjectPath, StringComparison.OrdinalIgnoreCase));
        }

        // When a saved suit is opened, favor the mod that owns it. This keeps Home's
        // workspace coherent without pretending a suit belongs to an arbitrary mod.
        if (summary is null && _currentProject is not null)
        {
            summary = mods.FirstOrDefault(m =>
            {
                var mod = ModService.LoadMod(m.Path);
                return mod?.Suits.Any(entry => string.Equals(entry.SuitId, _currentProject.SlotId, StringComparison.OrdinalIgnoreCase)) == true;
            });
        }

        summary ??= mods.FirstOrDefault();
        if (summary is null)
        {
            _homeActiveModProjectPath = "";
            return (null, null);
        }

        _homeActiveModProjectPath = summary.Path;
        return (summary, ModService.LoadMod(summary.Path));
    }

    private void SelectHomeMod(string modProjectPath)
    {
        _homeActiveModProjectPath = modProjectPath;
        RefreshHomeTiles();
    }

    /// <summary>Direct rail action for the Home-selected release collection.</summary>
    private void BuildActiveModFromWorkspace()
    {
        var mods = ModService.ListMods().ToList();
        var (summary, _) = ResolveHomeActiveMod(mods);
        if (summary is null)
        {
            Dialog.Info(this, "Build mod", "Create or select a mod first. A mod can contain one suit or a whole collection.");
            return;
        }

        var mod = ModService.LoadMod(summary.Path);
        if (mod?.Suits.Any(entry => entry.Enabled) != true)
        {
            Dialog.Info(this, "Build mod", "Add at least one enabled suit to the active mod before building it.");
            return;
        }

        BuildMod(summary.Path);
    }

    /// <summary>Build-focused rail screen for the currently active mod workspace.</summary>
    private void RefreshBuildModTiles()
    {
        var mods = ModService.ListMods().ToList();
        var (activeSummary, activeMod) = ResolveHomeActiveMod(mods);
        var activeSuitCount = activeMod?.Suits.Count(entry => entry.Enabled) ?? 0;
        var hasActiveMod = activeSummary is not null && activeMod is not null;
        var hasBuild = hasActiveMod && File.Exists(Path.Combine(ModBuildRoot(activeSummary!.ModId), activeMod!.PackageBaseName + ".utoc"));
        var hasInstalledRelease = hasBuild && File.Exists(Path.Combine(
            AppSettings.Current.EffectiveGamePaksModFolder(),
            activeMod!.PackageBaseName + ".utoc"));

        var hero = new VirtualTilePanel.HeroModel
        {
            Overline = "MOD RELEASE",
            Title = hasActiveMod ? activeSummary!.DisplayName : "Choose a mod to build",
            Subtitle = hasActiveMod
                ? $"{activeSuitCount} enabled suit{(activeSuitCount == 1 ? "" : "s")} will build and install as one game release."
                : "Select a saved mod or create one, then build and install it from here.",
            ThumbAccent = Theme.Gold,
            Chips = new List<(string, Color)>
            {
                (hasActiveMod ? "mod selected" : "no mod selected", hasActiveMod ? Theme.Research : Theme.Warn),
                ($"{activeSuitCount} enabled suit{(activeSuitCount == 1 ? "" : "s")}", activeSuitCount > 0 ? Theme.Parts : Theme.OnDarkMuted),
                (hasInstalledRelease ? "installed" : hasBuild ? "built; not installed" : "not built", hasInstalledRelease ? Theme.Good : hasBuild ? Theme.Warn : Theme.Warn),
            },
            Workflow = new[]
            {
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "MOD", Detail = hasActiveMod ? "selected" : "choose one", Accent = Theme.Research, Complete = hasActiveMod, Current = !hasActiveMod },
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "SUITS", Detail = activeSuitCount > 0 ? $"{activeSuitCount} enabled" : "add a suit", Accent = Theme.Base, Complete = activeSuitCount > 0, Current = hasActiveMod && activeSuitCount == 0 },
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "RELEASE", Detail = hasInstalledRelease ? "built + installed" : hasBuild ? "built; install next" : "build + install", Accent = Theme.Gold, Complete = hasInstalledRelease, Current = hasActiveMod && activeSuitCount > 0 && !hasInstalledRelease },
            },
        };

        const string SectionRelease = "RELEASE";
        const string SectionMods = "MODS";
        var tiles = new List<VirtualTilePanel.Tile>();
        if (hasActiveMod)
        {
            var modPath = activeSummary!.Path;
            var modId = activeSummary.ModId;
            if (activeSuitCount > 0)
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionRelease,
                    Title = $"Build {TrimMiddle(activeSummary.DisplayName, 20)}",
                    Subtitle = "build and install your mod/suits to your game",
                    Accent = Theme.Gold,
                    OnClick = () => BuildMod(modPath),
                });
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionRelease,
                    Title = "Validate release",
                    Subtitle = "check identities, assets, textures, and registry rows",
                    Accent = Theme.Good,
                    OnClick = () => ValidateModReleaseFromWorkspace(modPath),
                });
            }
            else
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionRelease,
                    Title = "Add a suit first",
                    Subtitle = "this mod has no enabled suits to build",
                    Accent = Theme.Base,
                    Dashed = true,
                    OnClick = () => EditModSuits(modPath),
                });
            }
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionRelease,
                Title = "Manage mod",
                Subtitle = "identity, suits, output",
                Accent = Theme.Research,
                OnClick = () => OpenModDetails(modPath, modId),
            });
            if (hasBuild)
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionRelease,
                    Title = $"Zip {TrimMiddle(activeSummary.DisplayName, 20)}",
                    Subtitle = "create a player-ready mod archive",
                    Accent = Theme.Info,
                    OnClick = () => CreateModReleaseZip(modPath),
                });
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionRelease,
                    Title = "Open build output",
                    Subtitle = "inspect the release files",
                    Accent = Theme.Inspector,
                    OnClick = () => OpenModBuildOutput(modId),
                });
            }
        }
        else
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionRelease,
                Title = "＋ New mod",
                Subtitle = "start a release collection",
                Accent = Theme.Gold,
                Dashed = true,
                OnClick = CreateModFlow,
            });
        }

        foreach (var summary in mods.Take(8))
        {
            var captured = summary;
            var isActive = hasActiveMod && string.Equals(captured.Path, activeSummary!.Path, StringComparison.OrdinalIgnoreCase);
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionMods,
                Title = TrimMiddle(captured.DisplayName, 26),
                Subtitle = isActive
                    ? $"{captured.SuitCount} suit{(captured.SuitCount == 1 ? "" : "s")} · active"
                    : $"{captured.SuitCount} suit{(captured.SuitCount == 1 ? "" : "s")} · select to build",
                Accent = isActive ? Theme.Research : Theme.OnDarkMuted,
                OnClick = () =>
                {
                    _homeActiveModProjectPath = captured.Path;
                    RefreshBuildModTiles();
                },
                MenuFactory = () => BuildModTileMenu(captured.Path, captured.ModId),
            });
        }

        ShowVirtualTiles(tiles, hero: hero);
    }

    /// <summary>
    /// Creates a new suit as a real saved project and immediately attaches it to
    /// the selected mod. This makes Home's "Add suit" action truthful: the suit
    /// is already in the release collection before the user starts picking its base.
    /// </summary>
    private void StartNewSuitInMod(string modProjectPath)
    {
        var selectedMod = ModService.LoadMod(modProjectPath);
        if (selectedMod is null)
        {
            Dialog.Error(this, "Add suit", "The selected mod could not be loaded.");
            return;
        }

        StartNewSuit(project =>
        {
            try
            {
                var suits = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
                var suitPath = suits.SaveProject(project);
                var mod = ModService.LoadMod(modProjectPath);
                if (mod is null)
                {
                    throw new InvalidOperationException("The selected mod was removed while the new suit was being created.");
                }

                if (!mod.Suits.Any(entry => string.Equals(entry.SuitId, project.SlotId, StringComparison.OrdinalIgnoreCase)))
                {
                    AddSuitEntries(mod, new[] { suitPath });
                    ModService.SaveMod(mod);
                }

                _homeActiveModProjectPath = modProjectPath;
                AppendLog($"Added new suit '{project.DisplayName}' to mod '{mod.DisplayName}'.");
            }
            catch (Exception ex)
            {
                AppendLog($"Could not add the new suit to the selected mod: {ex.Message}");
                Dialog.Error(this, "Add suit", ex.Message);
            }
        });
    }

    private void AddModTiles(List<VirtualTilePanel.Tile> tiles)
    {
        const string SectionMods = "MODS";
        tiles.Add(new VirtualTilePanel.Tile
        {
            Section = SectionMods,
            Title = "＋ New mod",
            Subtitle = "bundle suits into one pak",
            Accent = Theme.Gold,
            Dashed = true,
            OnClick = CreateModFlow,
        });

        try
        {
            foreach (var m in ModService.ListMods())
            {
                var path = m.Path;
                var modId = m.ModId;
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionMods,
                    Title = TrimMiddle(m.DisplayName, 26),
                    Subtitle = $"{m.SuitCount} suit{(m.SuitCount == 1 ? "" : "s")} · {m.ModId}",
                    Accent = Theme.Research,
                    MenuFactory = () => BuildModTileMenu(path, modId),
                    OnClick = () => OpenModDetails(path, modId),
                });
            }
        }
        catch { /* no mods dir yet */ }
    }

    /// <summary>
    /// Clicking a mod tile opens its details: identity, the suits it bundles, whether it has been
    /// built, and the same actions as the right-click menu.
    /// </summary>
    private void OpenModDetails(string modProjectPath, string modId)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null)
        {
            Dialog.Error(this, "Could not open mod", $"Failed to read the mod project:\n{modProjectPath}");
            return;
        }

        _homeActiveModProjectPath = modProjectPath;

        // Resolve each entry to a readable suit name, falling back to the cached id.
        var suits = new List<(string Suit, string Slot)>();
        try
        {
            var projects = new SuitProjectService(_projectRootText.Text.Trim()).ListProjects().ToList();
            foreach (var entry in mod.Suits.OrderBy(s => s.MenuOrder))
            {
                var match = projects.FirstOrDefault(p =>
                    string.Equals(p.SlotId, entry.SuitId, StringComparison.OrdinalIgnoreCase));
                var name = match?.DisplayName ?? entry.SuitId;
                suits.Add((entry.Enabled ? name : name + "  (disabled)", entry.SuitId));
            }
        }
        catch
        {
            foreach (var entry in mod.Suits)
            {
                suits.Add((entry.SuitId, entry.SuitId));
            }
        }

        var buildDir = ModBuildRoot(modId);
        var built = Directory.Exists(buildDir);

        using var dlg = new ModDetailsDialog(mod, suits, built, buildDir);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        switch (dlg.Chosen)
        {
            case ModDetailsDialog.ModAction.EditSuits: EditModSuits(modProjectPath); break;
            case ModDetailsDialog.ModAction.Rename: RenameMod(modProjectPath); break;
            case ModDetailsDialog.ModAction.Build: BuildMod(modProjectPath); break;
            case ModDetailsDialog.ModAction.Install: InstallMod(modProjectPath); break;
            case ModDetailsDialog.ModAction.OpenOutput: OpenModBuildOutput(modId); break;
            case ModDetailsDialog.ModAction.Delete: DeleteMod(modProjectPath); break;
        }
    }

    private System.Windows.Forms.ContextMenuStrip BuildModTileMenu(string modProjectPath, string modId)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Edit suits (add / remove)...", null, (_, _) => EditModSuits(modProjectPath));
        menu.Items.Add("Rename mod...", null, (_, _) => RenameMod(modProjectPath));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Build mod (trio + config + StringTable)", null, (_, _) => BuildMod(modProjectPath));
        menu.Items.Add("Install mod to game", null, (_, _) => InstallMod(modProjectPath));
        menu.Items.Add("Open build output", null, (_, _) => OpenModBuildOutput(modId));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Delete mod (keeps suits)", null, (_, _) => DeleteMod(modProjectPath));
        return menu;
    }

    private void CreateModFlow()
    {
        var name = PromptForText("Create mod", "Mod display name (spaces allowed):", "My Batman Pack");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var modId = ModProjectService.DeriveModId(name);
        if (string.IsNullOrWhiteSpace(modId))
        {
            AppendLog("Create mod: the name has no valid characters for a Mod ID.");
            return;
        }

        // Confirm the derived, immutable-after-release ID.
        var confirmed = PromptForText("Confirm Mod ID",
            "Stable Mod ID (pak / content-root / config all key off this — immutable after release):", modId);
        if (string.IsNullOrWhiteSpace(confirmed))
        {
            return;
        }
        modId = ModProjectService.DeriveModId(confirmed);

        if (ModService.ListMods().Any(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"Create mod: a mod with ID '{modId}' already exists.");
            return;
        }

        var mod = new NativeSuitModProject { ModId = modId, DisplayName = name.Trim() };
        var picked = PickSuits(modId, alreadyIn: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (picked is null)
        {
            return; // cancelled
        }
        AddSuitEntries(mod, picked);

        var saved = ModService.SaveMod(mod);
        _homeActiveModProjectPath = saved;
        AppendLog($"Created mod '{mod.DisplayName}' ({modId}) with {mod.Suits.Count} suit(s): {saved}");
        RefreshHomeTiles();
    }

    private void RenameMod(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null) { AppendLog("Rename mod: could not load project."); return; }
        var name = PromptForText("Rename mod", "New display name (Mod ID stays the same):", mod.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return;
        mod.DisplayName = name.Trim();
        ModService.SaveMod(mod);
        AppendLog($"Renamed mod to '{mod.DisplayName}' (ID {mod.ModId} unchanged).");
        RefreshHomeTiles();
    }

    private void EditModSuits(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null) { AppendLog("Edit mod: could not load project."); return; }

        var current = new HashSet<string>(
            mod.Suits.Select(s => ModService.ResolveSuitProjectPath(s)),
            StringComparer.OrdinalIgnoreCase);

        var picked = PickSuits(mod.ModId, current);
        if (picked is null) return; // cancelled

        mod.Suits.Clear();
        AddSuitEntries(mod, picked);
        ModService.SaveMod(mod);
        AppendLog($"Mod '{mod.DisplayName}' now has {mod.Suits.Count} suit(s).");
        RefreshHomeTiles();
    }

    /// <summary>Rebuilds a mod's suit entries from a set of absolute suit-project paths.</summary>
    private void AddSuitEntries(NativeSuitModProject mod, IReadOnlyList<string> suitProjectPaths)
    {
        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        var order = 100;
        foreach (var abs in suitProjectPaths)
        {
            var suit = svc.LoadProject(abs);
            mod.Suits.Add(new ModSuitEntry
            {
                SuitProjectPath = ModService.MakeRelativeSuitProjectPath(abs),
                SuitId = suit?.SlotId ?? Path.GetFileName(abs).Replace(".native-suit-project.json", ""),
                Enabled = true,
                MenuOrder = order,
            });
            order += 10;
        }
    }

    private void DeleteMod(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        var label = mod?.DisplayName ?? Path.GetFileName(modProjectPath);
        if (!Dialog.Confirm(this,
                $"Delete mod '{label}'?",
                "This removes the mod project only. The suits it referenced are NOT deleted.",
                confirmText: "Delete mod", severity: Dialog.Level.Crit))
        {
            return;
        }
        ModService.DeleteMod(modProjectPath);
        if (string.Equals(_homeActiveModProjectPath, modProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            _homeActiveModProjectPath = "";
        }
        AppendLog($"Deleted mod project '{label}' (suits kept).");
        RefreshHomeTiles();
    }

    private enum ModInstallStatus { Complete, Partial, Failed }

    private sealed class ModInstallResult
    {
        public ModInstallStatus Status;
        public string ModName = "";
        public string BuildOutput = "";
        public string TrioDestination = "";
        public string TagsDestination = "";
        public string RegistryDestination = "";
        public string AssetRegistryDestination = "";
        public int FilesCopied;
        public string Detail = "";
    }

    /// <summary>
    /// Copies a built mod's three products into the game: trio → ~mods/Slot,
    /// <c>&lt;ModId&gt;PawnTags.ini</c> → Config/Tags, <c>mod.json</c> →
    /// ue4ss/Mods/NewSuitSlotNative/SuitMods/&lt;ModId&gt;/.
    /// </summary>
    private void InstallMod(string modProjectPath)
    {
        var result = InstallModCore(modProjectPath);
        if (result.Status == ModInstallStatus.Failed)
        {
            Dialog.Error(this, "Install failed",
                $"'{result.ModName}' was not installed.\n\n{result.Detail}\n\n" +
                "Check that the game is closed and that the mod folder in Settings points at the " +
                "game's Paks\\~mods directory.");
        }
        else if (result.Status == ModInstallStatus.Partial)
        {
            Dialog.Warn(this, "Install incomplete", result.Detail);
        }
    }

    private ModInstallResult InstallModCore(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null)
        {
            AppendLog("Install mod: could not load project.");
            return new ModInstallResult { Status = ModInstallStatus.Failed, ModName = "this mod", Detail = "Could not load the mod project." };
        }
        ModProjectService.ApplyDerivedFields(mod);

        var outRoot = ModBuildRoot(mod.ModId);
        var trioBase = Path.Combine(outRoot, mod.PackageBaseName);
        var result = new ModInstallResult
        {
            ModName = mod.DisplayName,
            BuildOutput = outRoot,
            TrioDestination = AppSettings.Current.EffectiveGamePaksModFolder(),
        };
        if (!File.Exists(trioBase + ".utoc"))
        {
            AppendLog($"Install mod: no built trio for '{mod.ModId}'. Right-click → Build mod first.");
            result.Status = ModInstallStatus.Failed;
            result.Detail = "No built release trio was found. Build the mod first.";
            return result;
        }

        var installed = 0;
        var trioFilesCopied = 0;
        var tagsInstalled = false;
        var registryInstalled = false;
        var assetRegistryInstalled = false;
        try
        {

            // 1) trio → ~mods/Slot
            var slotDest = result.TrioDestination;
            ModReleaseStep("Copying the IoStore release files…");
            Directory.CreateDirectory(slotDest);
            foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
            {
                var src = trioBase + ext;
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(slotDest, mod.PackageBaseName + ext), overwrite: true);
                    installed++;
                    trioFilesCopied++;
                }
            }
            AppendLog($"  trio → {slotDest}");

            var gameRoot = GameLegoRoot();
            if (gameRoot is null)
            {
                AppendLog("  ⚠ could not locate the game's LEGOBatmanLotDK folder from settings — trio copied, but ini + mod.json were NOT installed. Set the game paks path in Setup.");
                AppendLog($"Install mod '{mod.DisplayName}': {installed} trio file(s) only.");
                result.Status = ModInstallStatus.Partial;
                result.FilesCopied = installed;
                result.Detail = "The release trio was copied, but Batcomputer could not locate the game root to install PawnTags.ini and mod.json.";
                return result;
            }

            // 2) <ModId>PawnTags.ini → Config/Tags
            var iniSrc = Path.Combine(outRoot, "LooseFiles", "LEGOBatmanLotDK", "Config", "Tags", $"{mod.ModId}PawnTags.ini");
            result.TagsDestination = Path.Combine(gameRoot, "Config", "Tags");
            ModReleaseStep("Installing the PawnTags configuration…");
            if (File.Exists(iniSrc))
            {
                var tagsDest = result.TagsDestination;
                Directory.CreateDirectory(tagsDest);
                File.Copy(iniSrc, Path.Combine(tagsDest, $"{mod.ModId}PawnTags.ini"), overwrite: true);
                installed++;
                tagsInstalled = true;
                AppendLog($"  {mod.ModId}PawnTags.ini → {tagsDest}");
            }

            // 3) mod.json → ue4ss/Mods/NewSuitSlotNative/SuitMods/<ModId>/
            var modJsonSrc = Path.Combine(outRoot, "mod.json");
            result.RegistryDestination = Path.Combine(gameRoot, "Binaries", "Win64", "ue4ss", "Mods", "NewSuitSlotNative", "SuitMods", mod.ModId);
            ModReleaseStep("Installing the mod registry entry…");
            if (File.Exists(modJsonSrc))
            {
                var suitModsDest = result.RegistryDestination;
                Directory.CreateDirectory(suitModsDest);
                File.Copy(modJsonSrc, Path.Combine(suitModsDest, "mod.json"), overwrite: true);
                installed++;
                registryInstalled = true;
                AppendLog($"  mod.json → {suitModsDest}");
            }

            AppendLog($"Installed mod '{mod.DisplayName}' — {installed} file(s). Restart the game to load it.");
            var plugin = RegistryPluginService.CreateLayout(outRoot, mod.ModId);
            result.AssetRegistryDestination = Path.Combine(
                gameRoot,
                "Binaries",
                "Win64",
                "ue4ss",
                "SuitSlots",
                "RegistryPlugins",
                plugin.PluginName);
            ModReleaseStep("Installing the Asset Registry plugin...");
            if (File.Exists(plugin.DescriptorPath) && File.Exists(plugin.RegistryPath) &&
                !string.IsNullOrWhiteSpace(result.AssetRegistryDestination))
            {
                CopyDirectoryContents(plugin.PluginDirectory, result.AssetRegistryDestination, overwrite: true);
                installed += Directory.EnumerateFiles(plugin.PluginDirectory, "*", SearchOption.AllDirectories).Count();
                assetRegistryInstalled = true;
                AppendLog($"  {plugin.PluginName} -> {result.AssetRegistryDestination}");
            }

            result.Status = trioFilesCopied == 3 && tagsInstalled && registryInstalled && assetRegistryInstalled
                ? ModInstallStatus.Complete
                : ModInstallStatus.Partial;
            result.FilesCopied = installed;
            result.Detail = result.Status == ModInstallStatus.Complete
                ? "The release is installed. Restart the game before testing the mod."
                : "Some expected release files were missing, so this install may be incomplete. Check Diagnostics before testing.";
            return result;
        }
        catch (Exception ex)
        {
            AppendLog($"Install mod failed: {ex.Message}");
            result.Status = ModInstallStatus.Failed;
            result.FilesCopied = installed;
            result.Detail = ex.Message;
            return result;
        }
    }

    /// <summary>Walks up from the game paks mod folder to the game's LEGOBatmanLotDK root.</summary>
    private static string? GameLegoRoot()
    {
        var cursor = new DirectoryInfo(Path.GetFullPath(AppSettings.Current.EffectiveGamePaksModFolder()));
        while (cursor is not null)
        {
            if (cursor.Name.Equals("LEGOBatmanLotDK", StringComparison.OrdinalIgnoreCase))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        return null;
    }

    /// <summary>Copies only this mod's generated plugin files; other installed mods remain untouched.</summary>
    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite);
        }
    }

    private void OpenModBuildOutput(string modId)
    {
        var dir = ModBuildRoot(modId);
        if (!Directory.Exists(dir))
        {
            AppendLog($"No build output yet for '{modId}'. Right-click the mod → Build mod.");
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog($"Could not open output folder: {ex.Message}"); }
    }

    /// <summary>
    /// Writes a player-ready archive using the same paths as <see cref="InstallModCore"/>.
    /// The archive starts one directory above LEGOBatmanLotDK, so extracting it directly into
    /// Steam's common directory preserves the game's intended layout.
    /// </summary>
    private void CreateModReleaseZip(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null)
        {
            Dialog.Error(this, "Zip mod", "The selected mod could not be loaded.");
            return;
        }
        ModProjectService.ApplyDerivedFields(mod);

        var outRoot = ModBuildRoot(mod.ModId);
        var trioBase = Path.Combine(outRoot, mod.PackageBaseName);
        var plugin = RegistryPluginService.CreateLayout(outRoot, mod.ModId);
        var files = new List<(string Source, string ArchivePath)>();
        const string ArchiveRoot = "LEGO Batman - Legacy of the Dark Knight";

        void AddRequired(string source, string archivePath)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("A required built release file is missing.", source);
            }
            files.Add((source, archivePath.Replace('\\', '/')));
        }

        try
        {
            foreach (var extension in new[] { ".pak", ".ucas", ".utoc" })
            {
                var source = trioBase + extension;
                AddRequired(source, $"{ArchiveRoot}/LEGOBatmanLotDK/Content/Paks/~mods/Slot/{Path.GetFileName(source)}");
            }

            AddRequired(
                Path.Combine(outRoot, "LooseFiles", "LEGOBatmanLotDK", "Config", "Tags", $"{mod.ModId}PawnTags.ini"),
                $"{ArchiveRoot}/LEGOBatmanLotDK/Config/Tags/{mod.ModId}PawnTags.ini");
            AddRequired(
                Path.Combine(outRoot, "mod.json"),
                $"{ArchiveRoot}/LEGOBatmanLotDK/Binaries/Win64/ue4ss/Mods/NewSuitSlotNative/SuitMods/{mod.ModId}/mod.json");
            AddRequired(
                plugin.DescriptorPath,
                $"{ArchiveRoot}/LEGOBatmanLotDK/Binaries/Win64/ue4ss/SuitSlots/RegistryPlugins/{plugin.PluginName}/{Path.GetFileName(plugin.DescriptorPath)}");
            AddRequired(
                plugin.RegistryPath,
                $"{ArchiveRoot}/LEGOBatmanLotDK/Binaries/Win64/ue4ss/SuitSlots/RegistryPlugins/{plugin.PluginName}/{Path.GetFileName(plugin.RegistryPath)}");
        }
        catch (FileNotFoundException ex)
        {
            var missing = string.IsNullOrWhiteSpace(ex.FileName) ? ex.Message : ex.FileName;
            Dialog.Warn(this, "Zip mod",
                "This mod needs a complete successful build before it can be archived.\n\nMissing:\n" + missing);
            return;
        }

        var zipPath = ModReleaseZipPath(mod.ModId);
        var temporaryZipPath = zipPath + ".tmp";
        try
        {
            Directory.CreateDirectory(outRoot);
            if (File.Exists(temporaryZipPath)) File.Delete(temporaryZipPath);
            using (var stream = File.Create(temporaryZipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (source, archivePath) in files)
                {
                    archive.CreateEntryFromFile(source, archivePath, CompressionLevel.Fastest);
                }
            }
            File.Move(temporaryZipPath, zipPath, overwrite: true);

            var sizeMb = new FileInfo(zipPath).Length / 1024d / 1024d;
            AppendLog($"Created player-ready release archive: {zipPath} ({sizeMb:0.0} MB, {files.Count} files).");
            Dialog.Show(this, new Dialog.Model
            {
                WindowTitle = "Batcomputer - Mod release archive",
                Title = "Mod release archive created",
                Subtitle = mod.DisplayName,
                Message = "Extract this archive into your Steam common folder. Its game-relative paths are already arranged for installation.",
                Severity = Dialog.Level.Good,
                PrimaryText = "Done",
                CalloutTitle = "Ready to share",
                CalloutDetail = $"{files.Count} release file{(files.Count == 1 ? "" : "s")} packaged. The archive contains no authoring projects or generated previews.",
                Fields = new List<(string Label, string Value)>
                {
                    ("Archive", zipPath),
                    ("Extract into", "...\\Steam\\steamapps\\common"),
                },
            });
            RefreshBuildModTiles();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temporaryZipPath)) File.Delete(temporaryZipPath);
            }
            catch { /* preserve the original failure */ }
            AppendLog($"Could not create release archive: {ex.Message}");
            Dialog.Error(this, "Zip mod", $"The release archive was not created.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Multi-select suit picker. Returns the chosen suit-project ABSOLUTE paths, or null
    /// if cancelled. <paramref name="alreadyIn"/> pre-checks the suits already in the mod.
    /// </summary>
    private IReadOnlyList<string>? PickSuits(string modId, ISet<string> alreadyIn)
    {
        var suits = new SuitProjectService(_projectRootText.Text.Trim()).ListProjects().ToList();
        if (suits.Count == 0)
        {
            AppendLog("No saved suits to add. Create and save a suit first.");
            return Array.Empty<string>();
        }

        using var dlg = new System.Windows.Forms.Form
        {
            Text = $"Suits in {modId}",
            Width = 460,
            Height = 460,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        var lbl = new System.Windows.Forms.Label
        {
            Text = "Check the suits to include in this mod:",
            Left = 14, Top = 12, Width = 420, ForeColor = Theme.OnDark,
        };
        var list = new System.Windows.Forms.CheckedListBox
        {
            Left = 14, Top = 40, Width = 420, Height = 320,
            BackColor = Theme.SlateDark, ForeColor = Theme.OnDark,
            CheckOnClick = true, IntegralHeight = false,
        };
        foreach (var s in suits)
        {
            var idx = list.Items.Add(new SuitItem(s));
            if (alreadyIn.Contains(s.Path)) list.SetItemChecked(idx, true);
        }
        var ok = new System.Windows.Forms.Button { Text = "OK", DialogResult = System.Windows.Forms.DialogResult.OK, Left = 264, Top = 372, Width = 80 };
        var cancel = new System.Windows.Forms.Button { Text = "Cancel", DialogResult = System.Windows.Forms.DialogResult.Cancel, Left = 354, Top = 372, Width = 80 };
        Theme.StyleGoldButton(ok);
        Theme.StyleDarkButton(cancel);
        dlg.Controls.AddRange(new System.Windows.Forms.Control[] { lbl, list, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return null;

        return list.CheckedItems.Cast<SuitItem>().Select(i => i.Summary.Path).ToList();
    }

    private sealed record SuitItem(SuitProjectService.ProjectSummary Summary)
    {
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Summary.DisplayName) ? Summary.SlotId : Summary.DisplayName;
    }

    /// <summary>
    /// Builds a mod's only release unit: its aggregate loose files and one combined
    /// IoStore trio. Suit projects are authoring inputs; they are not exported alone.
    /// </summary>
    private ProgressDialog? _modReleaseProgress;
    private sealed record ModReleaseFailure(string ModName, ModReleaseValidationService.Result Result);
    private ModReleaseFailure? _lastModReleaseFailure;

    private void ModReleaseStep(string detail) => _modReleaseProgress?.Report(detail);

    private sealed record ModReleasePreflight(
        ModReleaseValidationService.Result Result);

    /// <summary>
    /// Validates saved authoring facts without creating a stage, so users can fix
    /// release blockers before a build touches generated or game-facing files.
    /// </summary>
    private ModReleasePreflight ValidateModReleaseAuthoring(
        NativeSuitModProject mod,
        IReadOnlyList<ModSuitEntry> enabled)
    {
        ModReleaseStep("Running release preflight...");
        var suits = new SuitProjectService(_projectRootText.Text.Trim());
        var inputs = new List<ModReleaseValidationService.SuitInput>();
        foreach (var entry in enabled)
        {
            var projectPath = ModService.ResolveSuitProjectPath(entry);
            try
            {
                inputs.Add(new ModReleaseValidationService.SuitInput(entry, projectPath, suits.LoadProject(projectPath)));
            }
            catch (Exception ex)
            {
                inputs.Add(new ModReleaseValidationService.SuitInput(entry, projectPath, null,
                    $"Could not read the saved suit project '{projectPath}': {ex.Message}"));
            }
        }

        var service = new ModReleaseValidationService();
        var result = service.ValidateAuthoring(
            mod,
            inputs,
            AppSettings.Current.EffectiveExportContentRoot(),
            AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()),
            EffectiveGameRuntimeSuitsFolder());
        AppendLog($"Release preflight: {(result.Passed ? "passed" : "blocked")} ({result.ErrorCount} error(s), {result.WarningCount} warning(s)).");
        foreach (var finding in result.Findings.Where(f => !f.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase)))
        {
            var suit = string.IsNullOrWhiteSpace(finding.SuitId) ? "" : $" [{finding.SuitId}]";
            AppendLog($"  {finding.Severity.ToLowerInvariant()}{suit}: {finding.Message}");
        }
        return new ModReleasePreflight(result);
    }

    private void ValidateModReleaseFromWorkspace(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null)
        {
            Dialog.Error(this, "Validate release", "The selected mod could not be loaded.");
            return;
        }
        ModProjectService.ApplyDerivedFields(mod);
        var enabled = mod.Suits.Where(entry => entry.Enabled).ToList();
        var preflight = ValidateModReleaseAuthoring(mod, enabled);
        ReleasePreflightForm.Show(this, mod.DisplayName, preflight.Result);
    }

    private void BuildMod(string modProjectPath) => _ = BuildAndInstallModAsync(modProjectPath);

    /// <summary>
    /// The user-facing build command creates the release and immediately deploys that
    /// exact successful build. A failed package never falls through to an older trio.
    /// </summary>
    private async Task BuildAndInstallModAsync(string modProjectPath)
    {
        _lastModReleaseFailure = null;
        var progress = new ProgressDialog(this, "Building mod");
        _modReleaseProgress = progress;
        var built = false;
        ModInstallResult? install = null;
        Exception? unexpected = null;
        try
        {
            progress.SetStep("Building mod release");
            ModReleaseStep("Reading the mod and its saved suits…");
            built = await BuildModAsync(modProjectPath);
            if (built)
            {
                progress.SetStep("Installing mod to game");
                ModReleaseStep("Preparing the game installation…");
                install = InstallModCore(modProjectPath);
            }
        }
        catch (Exception ex)
        {
            unexpected = ex;
            AppendLog($"Build and install mod failed: {ex.Message}");
        }
        finally
        {
            _modReleaseProgress = null;
            progress.Dispose();
        }

        if (unexpected is not null)
        {
            Dialog.Error(this, "Build failed", $"The mod was not installed.\n\n{unexpected.Message}");
            return;
        }
        if (!built)
        {
            if (_lastModReleaseFailure is not null)
            {
                ReleasePreflightForm.Show(this, _lastModReleaseFailure.ModName, _lastModReleaseFailure.Result);
                return;
            }
            Dialog.Error(this, "Build failed", "The mod did not finish packaging, so no files were installed. Check Diagnostics for the exact failed step.");
            return;
        }
        if (install is not null)
        {
            ShowModReleaseResult(install);
        }
    }

    private void ShowModReleaseResult(ModInstallResult result)
    {
        var isComplete = result.Status == ModInstallStatus.Complete;
        var isPartial = result.Status == ModInstallStatus.Partial;
        var model = new Dialog.Model
        {
            WindowTitle = "Batcomputer - Mod release",
            Title = isComplete ? "Mod built and installed" : isPartial ? "Mod built; install incomplete" : "Mod installation failed",
            Subtitle = string.IsNullOrWhiteSpace(result.ModName) ? "Mod release" : result.ModName,
            Message = result.Detail,
            Severity = isComplete ? Dialog.Level.Good : isPartial ? Dialog.Level.Warn : Dialog.Level.Crit,
            PrimaryText = "Done",
            CalloutTitle = isComplete ? "Ready for an in-game test" : "Review the installation locations",
            CalloutDetail = isComplete
                ? "Restart the game before testing this mod."
                : "The exact problem is recorded in Diagnostics as well.",
        };
        model.Chips.Add(($"{result.FilesCopied} file{(result.FilesCopied == 1 ? "" : "s")} copied", isComplete ? Theme.Good : Theme.Warn));
        if (!string.IsNullOrWhiteSpace(result.BuildOutput)) model.Fields.Add(("Build output", result.BuildOutput));
        if (!string.IsNullOrWhiteSpace(result.TrioDestination)) model.Fields.Add(("Pak files", result.TrioDestination));
        if (!string.IsNullOrWhiteSpace(result.TagsDestination)) model.Fields.Add(("PawnTags", result.TagsDestination));
        if (!string.IsNullOrWhiteSpace(result.RegistryDestination)) model.Fields.Add(("Suit manifest", result.RegistryDestination));
        if (!string.IsNullOrWhiteSpace(result.AssetRegistryDestination)) model.Fields.Add(("Asset Registry", result.AssetRegistryDestination));
        Dialog.Show(this, model);
    }

    /// <summary>
    /// Rebuilds every saved mod, in sequence. The suit equivalent (<see cref="UpdateAllSuitsAsync"/>)
    /// re-stages suits against the current dump; this re-bundles the mods that package them, which is
    /// the step you otherwise have to remember to do afterwards.
    /// </summary>
    private async Task UpdateAllModsAsync()
    {
        List<ModProjectService.ModSummary> mods;
        try
        {
            mods = ModService.ListMods().ToList();
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Update all mods", $"Could not list mods:\n{ex.Message}");
            return;
        }

        if (mods.Count == 0)
        {
            Dialog.Info(this, "Update all mods", "No saved mods found.");
            return;
        }

        var names = string.Join("\n", mods.Select(m => $"  {m.DisplayName}  ({m.SuitCount} suit{(m.SuitCount == 1 ? "" : "s")})"));
        if (!Dialog.Confirm(this,
                $"Rebuild {mods.Count} mod{(mods.Count == 1 ? "" : "s")}?",
                $"{names}\n\nEach mod is rebuilt from the latest saved state of its included suits.",
                confirmText: "Rebuild all"))
        {
            return;
        }

        AppendLog($"=== Update all mods: {mods.Count} mod(s) ===");
        var ok = 0;
        var failed = new List<string>();
        foreach (var m in mods)
        {
            try
            {
                AppendLog($"--- {m.DisplayName} ({m.ModId}) ---");
                if (await BuildModAsync(m.Path)) ok++;
                else failed.Add(m.DisplayName);
            }
            catch (Exception ex)
            {
                failed.Add($"{m.DisplayName}: {ex.Message}");
                AppendLog($"  FAILED: {ex.Message}");
            }
        }

        AppendLog($"=== Update all mods complete: {ok} rebuilt, {failed.Count} failed ===");
        RefreshHomeTiles();
        if (failed.Count == 0)
        {
            Dialog.Success(this, "Update all mods", $"Rebuilt {ok} mod{(ok == 1 ? "" : "s")}.");
        }
        else
        {
            Dialog.Warn(this, "Update all mods",
                $"Rebuilt {ok} of {mods.Count} mod(s).\n\nFailed:\n{string.Join("\n", failed)}");
        }
    }

    private async Task<bool> BuildModAsync(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null) { AppendLog("Build mod: could not load project."); return false; }
        ModProjectService.ApplyDerivedFields(mod);
        ModReleaseStep("Checking enabled suits and gameplay tags…");

        var enabled = mod.Suits.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) { AppendLog("Build mod: no enabled suits."); return false; }

        var preflight = ValidateModReleaseAuthoring(mod, enabled);
        if (!preflight.Result.Passed)
        {
            _lastModReleaseFailure = new ModReleaseFailure(mod.DisplayName, preflight.Result);
            AppendLog("Build mod ABORTED: release preflight found blockers.");
            return false;
        }

        var outRoot = ModBuildRoot(mod.ModId);
        Directory.CreateDirectory(outRoot);

        var projectRoot = _projectRootText.Text.Trim();
        var svc = new SuitProjectService(projectRoot);
        var tagRows = new List<PawnTagConfigService.TagRow>();
        var stEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var manifestSuits = new List<ModManifestSuit>();

        foreach (var entry in enabled)
        {
            var abs = ModService.ResolveSuitProjectPath(entry);
            var suit = svc.LoadProject(abs);
            if (suit is null) { AppendLog($"  skip (unreadable): {abs}"); continue; }

            if (string.IsNullOrWhiteSpace(suit.PawnTag))
            {
                AppendLog($"Build mod ABORTED: suit '{suit.DisplayName}' ({suit.SlotId}) has no PawnTag. Set one before building.");
                return false;
            }

            var suitId = entry.SuitId;
            var nameKey = $"Suit.{suitId}.Name";
            var descKey = $"Suit.{suitId}.Description";
            var lockKey = $"Suit.{suitId}.LockedDescription";

            tagRows.Add(new PawnTagConfigService.TagRow(suit.PawnTag.Trim(), $"{mod.ModId}: {suit.DisplayName}"));
            stEntries[nameKey] = suit.DisplayName ?? "";
            stEntries[descKey] = suit.Description ?? "";
            stEntries[lockKey] = suit.LockedDescription ?? "";

            manifestSuits.Add(new ModManifestSuit
            {
                suit_id = suitId,
                enabled = true,
                menu_order = entry.MenuOrder,
                pawn_tag = suit.PawnTag.Trim(),
                progress_tag = suit.ProgressTag,
                display_name_key = nameKey,
                description_key = descKey,
                locked_description_key = lockKey,
                playable = suit.TargetPackages.Playable,
                cutscene = suit.TargetPackages.Cutscene,
                dcmd = suit.TargetPackages.Dcmd,
                uimd = DeriveUimdPackagePath(suit.TargetPackages.Dcmd),
            });
        }

        if (tagRows.Count == 0) { AppendLog("Build mod: nothing to build."); return false; }

        // Fresh stage each build so a removed suit's assets don't linger in the trio.
        var stageRoot = Path.Combine(outRoot, "Stage");
        try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
        catch (Exception ex) { AppendLog($"Build mod: could not clear old stage: {ex.Message}"); }

        // 1) PawnTags.ini (deterministic; throws on empty/duplicate tags).
        try
        {
            ModReleaseStep("Generating PawnTags configuration…");
            var looseRoot = Path.Combine(outRoot, "LooseFiles");
            var ini = new PawnTagConfigService().Generate(looseRoot, mod.ModId, tagRows);
            if (ini.Status != "created") { AppendLog($"Build mod: PawnTags.ini failed: {ini.Error}"); return false; }
            AppendLog($"  PawnTags.ini: {ini.RowCount} tag(s) -> {ini.OutputPath}");
        }
        catch (Exception ex) { AppendLog($"Build mod ABORTED: {ex.Message}"); return false; }

        // 2) StringTable ST_<ModId>.
        ModReleaseStep("Generating the mod StringTable…");
        var stBase = Path.Combine(outRoot, "Stage", "LEGOBatmanLotDK", "Content", "Mods", mod.ModId, "Localization", $"ST_{mod.ModId}");
        var st = new StringTableGenService(_projectRootText.Text.Trim()).Generate(stBase, mod.ModId, stEntries);
        if (st.Status != "created")
        {
            AppendLog($"Build mod: StringTable failed: {st.Error}");
            return false;
        }
        AppendLog($"  StringTable: {st.EntryCount} entries (namespace {st.TableNamespace}) -> {st.OutputUasset}");

        // 3) Build the schema-3 aggregate index. It is written only after the merged
        // stage validates, so a failed build never claims assets that were not packaged.
        var manifest = new ModManifest
        {
            mod_id = mod.ModId,
            display_name = mod.DisplayName,
            package_base_name = mod.PackageBaseName,
            content_root = mod.ContentRoot,
            string_table = StringTableGenService.ObjectPathFor(mod.ModId),
            build_id = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}",
            suits = manifestSuits,
        };
        var modJsonPath = Path.Combine(outRoot, "mod.json");

        // 4) Combined IoStore trio: prepare each suit's current authoring stage,
        //    merge it with the mod StringTable (no rebasing - distinct /Game roots),
        //    re-patch each suit's DCMD/UIMD text to the mod table, retoc to-zen ONCE.
        try
        {
            var stageContent = Path.Combine(stageRoot, "LEGOBatmanLotDK", "Content");
            var stObjectPath = StringTableGenService.ObjectPathFor(mod.ModId);
            var mappings = LoadModMappings();

            var mergedSuits = 0;
            var preparedSuits = new List<NativeSuitProject>();
            // No-rebase means suits keep their own /Game roots in one pak - two suits
            // sharing a DCMD package path would silently overwrite on merge. Catch it.
            var seenDcmd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in enabled)
            {
                var abs = ModService.ResolveSuitProjectPath(entry);
                var suit = svc.LoadProject(abs);
                if (suit is null) continue;

                var dcmdPkg = suit.TargetPackages?.Dcmd;

                if (!string.IsNullOrWhiteSpace(dcmdPkg) && !seenDcmd.Add(dcmdPkg!))
                {
                    AppendLog($"Build mod ABORTED: two suits share the asset path '{dcmdPkg}'. Each suit needs its own /Game/Mods/<folder> root.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(dcmdPkg))
                {
                    AppendLog($"Build mod ABORTED: suit '{suit.DisplayName}' has no generated DCMD package path.");
                    return false;
                }

                ModReleaseStep($"Preparing {suit.DisplayName} for the shared release…");
                if (!PrepareSuitForMod(suit, svc, out var suitContentRoot, out var prepareError))
                {
                    preflight.Result.AddError("texture staging", prepareError, suit.SlotId);
                    _lastModReleaseFailure = new ModReleaseFailure(mod.DisplayName, preflight.Result);
                    AppendLog($"Build mod ABORTED: could not prepare '{suit.DisplayName}': {prepareError}");
                    return false;
                }

                AppendLog($"  prepared '{suit.DisplayName}': playable + cutscene + DCMD + UIMD");
                MergeContentRoot(suitContentRoot, stageContent);
                RepatchStagedSuitText(stageContent, suit, entry.SuitId, stObjectPath, mappings);
                preparedSuits.Add(suit);
                mergedSuits++;
                AppendLog($"  bundled suit '{suit.DisplayName}' ({entry.SuitId}) → {suit.TargetPackages!.Dcmd}");
            }

            ModReleaseStep("Validating the combined release…");
            var validationErrors = ValidateModReleaseStage(mod, manifest, stageContent);
            try
            {
                var structural = new StageValidationService(stageContent, AppSettings.Current.EffectiveUsmapPath());
                foreach (var suit in preparedSuits)
                {
                    foreach (var finding in structural.Validate(suit))
                    {
                        if (finding.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                        {
                            validationErrors.Add($"{suit.SlotId}: {finding.Message}");
                        }
                        else
                        {
                            preflight.Result.AddWarning("staged release", finding.Message, suit.SlotId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                preflight.Result.AddWarning("staged release", $"Structural asset validation could not run: {ex.Message}");
            }
            if (validationErrors.Count > 0)
            {
                foreach (var error in validationErrors)
                {
                    preflight.Result.AddError("staged release", error);
                }
                _lastModReleaseFailure = new ModReleaseFailure(mod.DisplayName, preflight.Result);
                AppendLog("Build mod ABORTED: combined stage validation failed.");
                foreach (var error in validationErrors) AppendLog("  " + error);
                return false;
            }

            // Do this before retoc so a bad primary-asset row never produces a
            // misleadingly successful package.
            ModReleaseStep("Verifying the mod Asset Registry plugin...");
            var registry = await new RegistryPluginService().BuildAsync(
                outRoot,
                mod.ModId,
                mod.DisplayName,
                manifestSuits.Select(suit => new RegistryPluginService.RegistryRow(suit.dcmd)),
                line => AppendLog("  registry: " + line));
            if (!registry.Succeeded || registry.Layout is null)
            {
                preflight.Result.AddError("Asset Registry", registry.Error);
                if (!string.IsNullOrWhiteSpace(registry.VerificationLine))
                {
                    preflight.Result.AddError("Asset Registry", registry.VerificationLine);
                }
                _lastModReleaseFailure = new ModReleaseFailure(mod.DisplayName, preflight.Result);
                AppendLog($"Build mod ABORTED: Asset Registry plugin failed: {registry.Error}");
                if (!string.IsNullOrWhiteSpace(registry.VerificationLine))
                {
                    AppendLog("  registry verification: " + registry.VerificationLine);
                }
                return false;
            }
            preflight.Result.AddInfo("Asset Registry", $"Verified {registry.Rows.Count} PawnMetaData row(s).");
            AppendLog($"  Asset Registry: {registry.Rows.Count} PawnMetaData row(s) -> {registry.Layout.RegistryPath}");
            AppendLog("  registry verification: " + registry.VerificationLine);

            File.WriteAllText(modJsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog($"  mod.json: {manifestSuits.Count} suit(s) -> {modJsonPath}");

            var trioBase = Path.Combine(outRoot, mod.PackageBaseName);
            ModReleaseStep($"Packing {mod.PackageBaseName} into an IoStore trio…");
            AppendLog($"Packing combined trio ({mod.PackageBaseName}) with retoc…");
            var retocExit = await RunRetocToZenAsync(stageRoot, trioBase + ".utoc");
            if (retocExit != 0)
            {
                AppendLog($"Build mod: retoc to-zen failed (exit {retocExit}). Loose files are valid; trio not produced.");
                return false;
            }

            AppendLog($"Build mod '{mod.DisplayName}' COMPLETE — installable trio for {mergedSuits} suit(s):");
            AppendLog($"  {trioBase}.pak / .ucas / .utoc");
            AppendLog($"  {mod.ModId}PawnTags.ini + mod.json also under {outRoot}");
            AppendLog($"  Install: trio → ~mods/Slot,  ini → Config/Tags,  mod.json → ue4ss/Mods/NewSuitSlotNative/SuitMods/{mod.ModId}/");
            RefreshHomeTiles();
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Build mod failed during trio packaging: {ex.Message}");
            // Packaging produced nothing shippable - say so rather than looking like it worked.
            if (!_batchMode && _modReleaseProgress is null)
            {
                Dialog.Error(this, "Build failed",
                    $"The mod did not finish packaging.\n\n{ex.Message}\n\n" +
                    "No pak was written, so nothing was installed. The log has the full sequence.");
            }
            return false;
        }
    }

    /// <summary>Copies every file from one suit's staged Content tree into the shared mod stage.</summary>
    private static void MergeContentRoot(string srcContent, string dstContent)
    {
        foreach (var file in Directory.EnumerateFiles(srcContent, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcContent, file);
            var dest = Path.Combine(dstContent, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Materializes one suit's current saved authoring state into the stage that will
    /// be merged into a mod. Only the aggregate mod owns a final retoc export.
    /// </summary>
    private bool PrepareSuitForMod(NativeSuitProject suit, SuitProjectService projectService,
        out string contentRoot, out string error)
    {
        contentRoot = "";
        error = "";
        try
        {
            if (EnsureCrossKindHeadGraftHidesBaseHead(suit))
            {
                projectService.SaveProject(suit);
                AppendLog($"  saved Head:0 removal for '{suit.DisplayName}' cross-kind head graft.");
            }

            var gliderComponent = ActiveGliderVisualComponent(suit);
            if (!string.IsNullOrWhiteSpace(gliderComponent))
            {
                if (RemoveSavedRemovalForComponent(suit, gliderComponent))
                {
                    projectService.SaveProject(suit);
                    AppendLog($"  removed stale remove-component rule for '{suit.DisplayName}' glider '{gliderComponent}'.");
                }
                RestoreProtectedGliderComponent(suit, gliderComponent);
            }

            ApplySavedComponentRemovals(suit, logNoRemovals: false);
            contentRoot = CurrentPackageContentRoot(suit);
            if (!Directory.Exists(contentRoot))
            {
                error = "no staged content exists. Set a base and let the tool build its editable stage first.";
                return false;
            }

            StageGeneratedMaterialsIntoContentRoot(suit, contentRoot);
            if (!StageGeneratedTexturesIntoContentRoot(suit, contentRoot, out var textureStageError))
            {
                error = textureStageError;
                return false;
            }
            StageGeneratedDcmdIntoContentRoot(suit, contentRoot);
            StageLibraryAnimsIntoContentRoot(suit, contentRoot);

            if (suit.UseCustomArchetype)
            {
                var archetype = new AnimArchetypeGraftService().ApplyToPackagedRoot(suit, contentRoot);
                foreach (var line in archetype.Log) AppendLog("    archetype: " + line);
                if (string.Equals(archetype.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(archetype.Error))
                {
                    error = archetype.Error ?? "custom archetype preparation failed.";
                    return false;
                }
            }

            // Grafting can replace the stage from the donor, so material bindings must
            // be applied after the last possible stage rebuild.
            ApplySavedMaterials(suit, logIfNone: false);

            var requiredPackages = new[]
            {
                (Role: "playable", Package: suit.TargetPackages?.Playable),
                (Role: "cutscene", Package: suit.TargetPackages?.Cutscene),
                (Role: "DCMD", Package: suit.TargetPackages?.Dcmd),
                (Role: "UIMD", Package: DeriveUimdPackagePath(suit.TargetPackages?.Dcmd ?? "")),
            };
            var stagedContentRoot = contentRoot;
            var missing = requiredPackages
                .Where(p => string.IsNullOrWhiteSpace(p.Package) || !HasCookedPackagePair(stagedContentRoot, p.Package!))
                .Select(p => $"{p.Role}: {p.Package ?? "<unset>"}")
                .ToList();
            if (missing.Count > 0)
            {
                error = "required staged assets are missing: " + string.Join(", ", missing);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasCookedPackagePair(string contentRoot, string packagePath)
    {
        var basePath = PackagePathToContentPath(contentRoot, packagePath);
        return File.Exists(basePath + ".uasset") && File.Exists(basePath + ".uexp");
    }

    /// <summary>Checks the aggregate stage matches the schema-3 mod registration contract.</summary>
    private static List<string> ValidateModReleaseStage(NativeSuitModProject mod, ModManifest manifest, string contentRoot)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(mod.ModId)) errors.Add("mod_id is empty.");
        if (string.IsNullOrWhiteSpace(mod.PackageBaseName)) errors.Add("package_base_name is empty.");
        if (!string.Equals(manifest.string_table, StringTableGenService.ObjectPathFor(mod.ModId), StringComparison.Ordinal))
            errors.Add("mod.json string_table does not point at the mod-owned StringTable.");
        if (!HasCookedPackagePair(contentRoot, StringTableGenService.PackagePathFor(mod.ModId)))
            errors.Add("mod-owned StringTable is missing its .uasset or .uexp.");

        var suitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pawnTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suit in manifest.suits)
        {
            if (!suitIds.Add(suit.suit_id)) errors.Add($"duplicate suit_id '{suit.suit_id}'.");
            if (string.IsNullOrWhiteSpace(suit.pawn_tag) || !pawnTags.Add(suit.pawn_tag))
                errors.Add($"missing or duplicate PawnTag for suit '{suit.suit_id}'.");
            if (!string.Equals(suit.display_name_key, $"Suit.{suit.suit_id}.Name", StringComparison.Ordinal) ||
                !string.Equals(suit.description_key, $"Suit.{suit.suit_id}.Description", StringComparison.Ordinal) ||
                !string.Equals(suit.locked_description_key, $"Suit.{suit.suit_id}.LockedDescription", StringComparison.Ordinal))
            {
                errors.Add($"StringTable keys are incomplete or do not match suit '{suit.suit_id}'.");
            }

            foreach (var required in new[]
            {
                (Role: "playable", Package: suit.playable),
                (Role: "cutscene", Package: suit.cutscene),
                (Role: "DCMD", Package: suit.dcmd),
                (Role: "UIMD", Package: suit.uimd),
            })
            {
                var normalized = UnrealPathUtil.NormalizePackagePath(required.Package);
                if (string.IsNullOrWhiteSpace(required.Package) || !string.Equals(required.Package, normalized, StringComparison.Ordinal))
                {
                    errors.Add($"{suit.suit_id} {required.Role} is not a clean package path: '{required.Package}'.");
                    continue;
                }
                if (!normalized.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{suit.suit_id} {required.Role} must be under /Game/Mods/: '{normalized}'.");
                    continue;
                }
                if (!HasCookedPackagePair(contentRoot, normalized))
                    errors.Add($"{suit.suit_id} {required.Role} is missing its .uasset or .uexp: '{normalized}'.");
            }
        }
        return errors;
    }

    /// <summary>
    /// Repoints a bundled suit's staged DCMD/UIMD text at the mod StringTable. The
    /// per-suit staging leaves DisplayName/Description pointing at the donor tables
    /// (ST_TagNames/ST_UI); this fixes them to ST_&lt;ModId&gt; + the suit's own keys,
    /// and re-asserts the pawn tag. Property-level (see NativeAssetTextPatch).
    /// </summary>
    private void RepatchStagedSuitText(string stageContent, NativeSuitProject suit, string suitId, string stObjectPath, Usmap? mappings)
    {
        try
        {
            var dcmdPkg = suit.TargetPackages!.Dcmd;
            var dcmdFile = PackagePathToContentPath(stageContent, dcmdPkg) + ".uasset";
            if (File.Exists(dcmdFile))
            {
                var a = new UAsset(dcmdFile, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                var changed = false;
                if (!string.IsNullOrWhiteSpace(suit.PawnTag))
                    changed |= NativeAssetTextPatch.SetGameplayTag(a, "PawnTag", suit.PawnTag.Trim());
                changed |= NativeAssetTextPatch.SetStringTableText(a, "DisplayName", stObjectPath, $"Suit.{suitId}.Name");
                if (changed) a.Write(dcmdFile);
            }

            var uimdPkg = DeriveUimdPackagePath(dcmdPkg);
            var uimdFile = PackagePathToContentPath(stageContent, uimdPkg) + ".uasset";
            if (File.Exists(uimdFile))
            {
                var a = new UAsset(uimdFile, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                var changed = false;
                if (!string.IsNullOrWhiteSpace(suit.PawnTag))
                    changed |= NativeAssetTextPatch.SetGameplayTag(a, "PawnTag", suit.PawnTag.Trim());
                changed |= NativeAssetTextPatch.SetStringTableText(a, "Description", stObjectPath, $"Suit.{suitId}.Description");
                changed |= NativeAssetTextPatch.SetStringTableText(a, "LockedDescription", stObjectPath, $"Suit.{suitId}.LockedDescription");
                if (changed) a.Write(uimdFile);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  ⚠ text repatch failed for '{suitId}': {ex.Message}");
        }
    }

    private static Usmap? LoadModMappings()
    {
        var u = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(u) && File.Exists(u) ? MappingsCache.Load(u) : null;
    }

    private async Task<int> RunRetocToZenAsync(string inputDir, string outUtoc)
    {
        var settings = AppSettings.Current;
        var oodleRetoc = settings.EffectiveOodleRetocExePath();
        var oodleRuntime = settings.EffectiveOodleRuntimeDllPath();
        var useOodle = File.Exists(oodleRetoc) &&
            !string.IsNullOrWhiteSpace(oodleRuntime) && File.Exists(oodleRuntime);
        var retoc = useOodle ? oodleRetoc : settings.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            AppendLog($"retoc.exe not found: {retoc}. Open Setup and select it.");
            return -1;
        }
        if (useOodle)
        {
            AppendLog($"Packing with Oodle compression ({Path.GetFileName(oodleRuntime)}).");
        }
        else
        {
            AppendLog("Packing without Oodle compression. Configure the Oodle packer and local runtime in Setup for compact releases.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outUtoc)!);

        var psi = new ProcessStartInfo
        {
            FileName = retoc,
            WorkingDirectory = Path.GetDirectoryName(retoc) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (useOodle)
        {
            var runtimeFolder = Path.GetDirectoryName(oodleRuntime)!;
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
                ? runtimeFolder
                : runtimeFolder + Path.PathSeparator + inheritedPath;
        }
        psi.ArgumentList.Add("to-zen");
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(GameAssetRefreshService.RetocEngineVersion);
        psi.ArgumentList.Add(inputDir);
        psi.ArgumentList.Add(outUtoc);

        using var p = Process.Start(psi);
        if (p is null) { AppendLog("Could not start retoc.exe."); return -1; }
        var o = await p.StandardOutput.ReadToEndAsync();
        var e = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (!string.IsNullOrWhiteSpace(o)) AppendLog(o.Trim());
        if (!string.IsNullOrWhiteSpace(e)) AppendLog(e.Trim());
        return p.ExitCode;
    }

    /// <summary>
    /// Runs retoc <c>to-legacy</c> to unpack a zen container directory into loose cooked assets.
    /// <paramref name="inputDir"/> must contain the mod trio AND the game's global.utoc/.ucas
    /// (retoc needs the global script objects to resolve /Script imports - a standalone mod trio
    /// alone fails). Returns retoc's exit code.
    /// </summary>
    private async Task<int> RunRetocToLegacyAsync(string inputDir, string outDir)
    {
        var retoc = AppSettings.Current.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            AppendLog($"retoc.exe not found: {retoc}. Open Setup and select it.");
            return -1;
        }
        Directory.CreateDirectory(outDir);

        var psi = new ProcessStartInfo
        {
            FileName = retoc,
            WorkingDirectory = Path.GetDirectoryName(retoc) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("to-legacy");
        psi.ArgumentList.Add("--no-shaders");
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(GameAssetRefreshService.RetocEngineVersion);
        psi.ArgumentList.Add(inputDir);
        psi.ArgumentList.Add(outDir);

        using var p = Process.Start(psi);
        if (p is null) { AppendLog("Could not start retoc.exe."); return -1; }
        var o = await p.StandardOutput.ReadToEndAsync();
        var e = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (!string.IsNullOrWhiteSpace(o)) AppendLog(o.Trim());
        if (!string.IsNullOrWhiteSpace(e)) AppendLog(e.Trim());
        return p.ExitCode;
    }

    // --- mod.json (schema 3) serialization shapes ---
    private sealed class ModManifest
    {
        public int schema_version { get; set; } = 3;
        public string format { get; set; } = "native_suit_mod";
        public string mod_id { get; set; } = "";
        public string display_name { get; set; } = "";
        public string package_base_name { get; set; } = "";
        public string content_root { get; set; } = "";
        public string string_table { get; set; } = "";
        public string build_id { get; set; } = "";
        public List<ModManifestSuit> suits { get; set; } = new();
    }

    private sealed class ModManifestSuit
    {
        public string suit_id { get; set; } = "";
        public bool enabled { get; set; } = true;
        public int menu_order { get; set; }
        public string pawn_tag { get; set; } = "";
        public string progress_tag { get; set; } = "";
        public string display_name_key { get; set; } = "";
        public string description_key { get; set; } = "";
        public string locked_description_key { get; set; } = "";
        public string playable { get; set; } = "";
        public string cutscene { get; set; } = "";
        public string dcmd { get; set; } = "";
        public string uimd { get; set; } = "";
    }
}
