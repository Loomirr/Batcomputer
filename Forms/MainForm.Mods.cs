using System.Diagnostics;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Home-screen "Mods" section and the mod build. A mod bundles several suit projects
/// into one release: one pak, one <c>&lt;ModId&gt;PawnTags.ini</c>, one <c>ST_&lt;ModId&gt;</c>
/// StringTable, one <c>mod.json</c>. This partial owns the UI + orchestration; the
/// asset work lives in ModProjectService / PawnTagConfigService / StringTableGenService.
/// </summary>
public sealed partial class MainForm
{
    private ModProjectService ModService => new(_projectRootText.Text.Trim());

    /// <summary>Where a built mod's aggregate outputs land.</summary>
    private string ModBuildRoot(string modId) =>
        Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitModBuilds", modId);

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
        AppendLog($"Deleted mod project '{label}' (suits kept).");
        RefreshHomeTiles();
    }

    /// <summary>
    /// Copies a built mod's three products into the game: trio → ~mods/Slot,
    /// <c>&lt;ModId&gt;PawnTags.ini</c> → Config/Tags, <c>mod.json</c> →
    /// ue4ss/Mods/NewSuitSlotNative/SuitMods/&lt;ModId&gt;/.
    /// </summary>
    private void InstallMod(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null) { AppendLog("Install mod: could not load project."); return; }
        ModProjectService.ApplyDerivedFields(mod);

        var outRoot = ModBuildRoot(mod.ModId);
        var trioBase = Path.Combine(outRoot, mod.PackageBaseName);
        if (!File.Exists(trioBase + ".utoc"))
        {
            AppendLog($"Install mod: no built trio for '{mod.ModId}'. Right-click → Build mod first.");
            return;
        }

        try
        {
            var installed = 0;

            // 1) trio → ~mods/Slot
            var slotDest = AppSettings.Current.EffectiveGamePaksModFolder();
            Directory.CreateDirectory(slotDest);
            foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
            {
                var src = trioBase + ext;
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(slotDest, mod.PackageBaseName + ext), overwrite: true);
                    installed++;
                }
            }
            AppendLog($"  trio → {slotDest}");

            var gameRoot = GameLegoRoot();
            if (gameRoot is null)
            {
                AppendLog("  ⚠ could not locate the game's LEGOBatmanLotDK folder from settings — trio copied, but ini + mod.json were NOT installed. Set the game paks path in Setup.");
                AppendLog($"Install mod '{mod.DisplayName}': {installed} trio file(s) only.");
                return;
            }

            // 2) <ModId>PawnTags.ini → Config/Tags
            var iniSrc = Path.Combine(outRoot, "LooseFiles", "LEGOBatmanLotDK", "Config", "Tags", $"{mod.ModId}PawnTags.ini");
            if (File.Exists(iniSrc))
            {
                var tagsDest = Path.Combine(gameRoot, "Config", "Tags");
                Directory.CreateDirectory(tagsDest);
                File.Copy(iniSrc, Path.Combine(tagsDest, $"{mod.ModId}PawnTags.ini"), overwrite: true);
                installed++;
                AppendLog($"  {mod.ModId}PawnTags.ini → {tagsDest}");
            }

            // 3) mod.json → ue4ss/Mods/NewSuitSlotNative/SuitMods/<ModId>/
            var modJsonSrc = Path.Combine(outRoot, "mod.json");
            if (File.Exists(modJsonSrc))
            {
                var suitModsDest = Path.Combine(gameRoot, "Binaries", "Win64", "ue4ss", "Mods", "NewSuitSlotNative", "SuitMods", mod.ModId);
                Directory.CreateDirectory(suitModsDest);
                File.Copy(modJsonSrc, Path.Combine(suitModsDest, "mod.json"), overwrite: true);
                installed++;
                AppendLog($"  mod.json → {suitModsDest}");
            }

            AppendLog($"Installed mod '{mod.DisplayName}' — {installed} file(s). Restart the game to load it.");
        }
        catch (Exception ex)
        {
            AppendLog($"Install mod failed: {ex.Message}");
            // The user pressed Install and expects the mod to be in the game. A log line they may
            // never scroll to is not an answer.
            Dialog.Error(this, "Install failed",
                $"'{mod.DisplayName}' was not installed.\n\n{ex.Message}\n\n" +
                "Check that the game is closed and that the mod folder in Settings points at the " +
                "game's Paks\\~mods directory.");
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
    /// Builds the mod's aggregate loose products: <c>&lt;ModId&gt;PawnTags.ini</c>,
    /// <c>ST_&lt;ModId&gt;</c> StringTable (.uasset/.uexp), and <c>mod.json</c>. The combined
    /// IoStore trio (all suits' cooked assets in one pak) is the remaining fan-in step.
    /// </summary>
    private void BuildMod(string modProjectPath) => _ = BuildModAsync(modProjectPath);

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
                $"{names}\n\nEach mod is re-bundled from its suits' last packaged output. Build the suits first if they're stale.",
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
                await BuildModAsync(m.Path);
                ok++;
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

    private async Task BuildModAsync(string modProjectPath)
    {
        var mod = ModService.LoadMod(modProjectPath);
        if (mod is null) { AppendLog("Build mod: could not load project."); return; }
        ModProjectService.ApplyDerivedFields(mod);

        var enabled = mod.Suits.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) { AppendLog("Build mod: no enabled suits."); return; }

        var svc = new SuitProjectService(_projectRootText.Text.Trim());
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
                return;
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
            });
        }

        if (tagRows.Count == 0) { AppendLog("Build mod: nothing to build."); return; }

        var outRoot = ModBuildRoot(mod.ModId);
        Directory.CreateDirectory(outRoot);

        // Fresh stage each build so a removed suit's assets don't linger in the trio.
        var stageRoot = Path.Combine(outRoot, "Stage");
        try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
        catch (Exception ex) { AppendLog($"Build mod: could not clear old stage: {ex.Message}"); }

        // 1) PawnTags.ini (deterministic; throws on empty/duplicate tags).
        try
        {
            var looseRoot = Path.Combine(outRoot, "LooseFiles");
            var ini = new PawnTagConfigService().Generate(looseRoot, mod.ModId, tagRows);
            if (ini.Status != "created") { AppendLog($"Build mod: PawnTags.ini failed: {ini.Error}"); return; }
            AppendLog($"  PawnTags.ini: {ini.RowCount} tag(s) -> {ini.OutputPath}");
        }
        catch (Exception ex) { AppendLog($"Build mod ABORTED: {ex.Message}"); return; }

        // 2) StringTable ST_<ModId>.
        var stBase = Path.Combine(outRoot, "Stage", "LEGOBatmanLotDK", "Content", "Mods", mod.ModId, "Localization", $"ST_{mod.ModId}");
        var st = new StringTableGenService(_projectRootText.Text.Trim()).Generate(stBase, mod.ModId, stEntries);
        if (st.Status != "created")
        {
            AppendLog($"Build mod: StringTable failed: {st.Error}");
            return;
        }
        AppendLog($"  StringTable: {st.EntryCount} entries (namespace {st.TableNamespace}) -> {st.OutputUasset}");

        // 3) mod.json (schema 3) - the DLL's aggregate index.
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
        File.WriteAllText(modJsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        AppendLog($"  mod.json: {manifestSuits.Count} suit(s) -> {modJsonPath}");

        // 4) Combined IoStore trio: merge each suit's already-staged cooked content +
        //    the mod StringTable into one stage (no rebasing - distinct /Game roots),
        //    re-patch each suit's DCMD/UIMD text to the mod table, retoc to-zen ONCE.
        try
        {
            var stageContent = Path.Combine(stageRoot, "LEGOBatmanLotDK", "Content");
            var stObjectPath = StringTableGenService.ObjectPathFor(mod.ModId);
            var mappings = LoadModMappings();

            var mergedSuits = 0;
            // No-rebase means suits keep their own /Game roots in one pak - two suits
            // sharing a DCMD package path would silently overwrite on merge. Catch it.
            var seenDcmd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in enabled)
            {
                var abs = ModService.ResolveSuitProjectPath(entry);
                var suit = svc.LoadProject(abs);
                if (suit is null) continue;

                // Bundle the immutable output of the suit's last successful Package
                // operation. CurrentPackageContentRoot() points at a mutable authoring
                // stage, which may contain a stale DCMD and omit package-only products
                // such as the generated UIMD and generated textures.
                var suitContentRoot = LastPackagedSuitContentRoot(suit);
                var dcmdPkg = suit.TargetPackages?.Dcmd;

                if (!string.IsNullOrWhiteSpace(dcmdPkg) && !seenDcmd.Add(dcmdPkg!))
                {
                    AppendLog($"Build mod ABORTED: two suits share the asset path '{dcmdPkg}'. Each suit needs its own /Game/Mods/<folder> root.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(dcmdPkg) || !Directory.Exists(suitContentRoot))
                {
                    AppendLog($"Build mod ABORTED: suit '{suit.DisplayName}' has no packaged cooked content yet.");
                    AppendLog($"  Open that suit and Package it once (Base → Package), then rebuild the mod.");
                    return;
                }

                var requiredPackages = new[]
                {
                    (Role: "playable", Package: suit.TargetPackages?.Playable),
                    (Role: "cutscene", Package: suit.TargetPackages?.Cutscene),
                    (Role: "DCMD", Package: dcmdPkg),
                    (Role: "UIMD", Package: DeriveUimdPackagePath(dcmdPkg!)),
                };
                var missingRequired = requiredPackages
                    .Where(p => string.IsNullOrWhiteSpace(p.Package) ||
                                !HasCookedPackagePair(suitContentRoot, p.Package!))
                    .Select(p => $"{p.Role}: {p.Package ?? "<unset>"}")
                    .ToList();
                if (missingRequired.Count > 0)
                {
                    AppendLog($"Build mod ABORTED: suit '{suit.DisplayName}' has an incomplete last packaged stage.");
                    foreach (var missing in missingRequired)
                        AppendLog($"  missing {missing}");
                    AppendLog("  Open that suit and Package it again, confirm its preflight passes, then rebuild the mod.");
                    return;
                }

                AppendLog($"  packaged source validated for '{suit.DisplayName}': playable + cutscene + DCMD + UIMD");
                MergeContentRoot(suitContentRoot, stageContent);
                RepatchStagedSuitText(stageContent, suit, entry.SuitId, stObjectPath, mappings);
                mergedSuits++;
                AppendLog($"  bundled suit '{suit.DisplayName}' ({entry.SuitId}) → {suit.TargetPackages!.Dcmd}");
            }

            var trioBase = Path.Combine(outRoot, mod.PackageBaseName);
            AppendLog($"Packing combined trio ({mod.PackageBaseName}) with retoc…");
            var retocExit = await RunRetocToZenAsync(stageRoot, trioBase + ".utoc");
            if (retocExit != 0)
            {
                AppendLog($"Build mod: retoc to-zen failed (exit {retocExit}). Loose files are valid; trio not produced.");
                return;
            }

            AppendLog($"Build mod '{mod.DisplayName}' COMPLETE — installable trio for {mergedSuits} suit(s):");
            AppendLog($"  {trioBase}.pak / .ucas / .utoc");
            AppendLog($"  {mod.ModId}PawnTags.ini + mod.json also under {outRoot}");
            AppendLog($"  Install: trio → ~mods/Slot,  ini → Config/Tags,  mod.json → ue4ss/Mods/NewSuitSlotNative/SuitMods/{mod.ModId}/");
            RefreshHomeTiles();
        }
        catch (Exception ex)
        {
            AppendLog($"Build mod failed during trio packaging: {ex.Message}");
            // Packaging produced nothing shippable - say so rather than looking like it worked.
            if (!_batchMode)
            {
                Dialog.Error(this, "Build failed",
                    $"The mod did not finish packaging.\n\n{ex.Message}\n\n" +
                    "No pak was written, so nothing was installed. The log has the full sequence.");
            }
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
    /// Returns the stable Content tree emitted by the suit's most recent successful
    /// Package operation. Aggregate mod builds intentionally do not read the mutable
    /// PatchedNameMap/GraftedPart authoring stages.
    /// </summary>
    private string LastPackagedSuitContentRoot(NativeSuitProject suit)
    {
        return Path.Combine(
            AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()),
            "NativeSuitGuiProjects",
            suit.SlotId,
            "IoStore",
            "Stage",
            "LEGOBatmanLotDK",
            "Content");
    }

    private static bool HasCookedPackagePair(string contentRoot, string packagePath)
    {
        var basePath = PackagePathToContentPath(contentRoot, packagePath);
        return File.Exists(basePath + ".uasset") && File.Exists(basePath + ".uexp");
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
        var retoc = AppSettings.Current.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            AppendLog($"retoc.exe not found: {retoc}. Open Setup and select it.");
            return -1;
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
    }
}
