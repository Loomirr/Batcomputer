using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Animation overrides, swaps, and the anim-set browser.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// Browses animation building blocks and shows the current suit's composition.
    /// Animations are compositional: a suit's MAS_Char/LAS_Char pull in categorized
    /// blocks (Equipment/Traversal/Interaction/…) via ParentSetsArray. Applying a
    /// custom composition requires the suit to own its anim sets (custom archetype),
    /// which is a separate generation step - this tab is the browse/plan surface.
    /// </summary>
    private void RefreshAnimationTiles(string? type)
    {
        var gd = GameDataService.Instance;
        if (!gd.HasAnimSets)
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote(
                "Animation data not loaded. Rebuild gamedata (--build-gamedata) after dumping Content/Animation."));
            return;
        }

        var family = gd.FamilyForBasePath(_basePlayableText.Text.Trim());
        var search = CurrentToyboxSearch();

        if (type == "Swap category by family")
        {
            RefreshAnimSwapTiles();
            return;
        }

        if (type == "Replace idle/walk/run")
        {
            RefreshLocomotionTiles();
            return;
        }

        if (type == "Overview & setup" || string.IsNullOrEmpty(type))
        {
            RefreshAnimationOverview(family);
            return;
        }

        // Building-block browser (reference only).
        IEnumerable<GameDataAnimSet> sets;
        string header;
        if (type == "Browse: Montage composites")
        {
            sets = gd.AnimSets("Montage").Where(a => a.IsCharacterComposite);
            header = "Per-family montage composites (MAS_Char_*). Each is a family's full montage set. Reference only.";
        }
        else if (type == "Browse: Layer blocks")
        {
            sets = gd.AnimSets("Layer");
            header = "All layer anim sets (LAS). Equipment/Traversal/Interaction blocks + per-family composites. Reference only.";
        }
        else
        {
            var cat = type!.StartsWith("Browse: Layer · ", StringComparison.Ordinal) ? type["Browse: Layer · ".Length..] : type;
            sets = gd.AnimSets("Layer", cat);
            header = $"Layer anim blocks · {cat}. Reference only.";
        }

        var categoryFilter = FilterVal(0);
        _toyboxTileFlow.Controls.Add(FullWidthNote(header));
        foreach (var set in sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (categoryFilter is not null && !set.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchesToyboxSearch(search, set.Name, set.Category)) continue;
            var tile = MakeTile(set.Name.Replace("LAS_", "").Replace("MAS_", ""), set.Category,
                () => ShowAnimSetDetail(set.Name), Theme.Animations);
            _toyboxTileFlow.Controls.Add(tile);
        }
    }

    /// <summary>
    /// Per-category "borrow another family's animations" tiles. Whole-set swap:
    /// picks e.g. Locomotion → Catwoman, replacing LAS_Default_Batman with
    /// LAS_Default_Catwoman in the suit's composition (requires custom archetype).
    /// </summary>
    private void RefreshAnimSwapTiles()
    {
        var gd = GameDataService.Instance;
        EnsureProject();
        var archetypeOn = _currentProject?.UseCustomArchetype == true;
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            archetypeOn
                ? "Borrow a whole animation category from another family. ✔ Montage categories (Movement/Glide/LedgeGrab) are safe. ⚠ Layer categories (Locomotion/Traversal) swap a compiled AnimBlueprint and CRASH across families — use only for same-skeleton-compatible cases or leave on donor default."
                : "⚠ Turn on Custom archetype (This suit's composition tab) first — animation swaps need it."));

        foreach (var (category, kind, _, _) in GameDataService.AnimCategoryMap)
        {
            var current = _currentProject?.AnimationOverrides.FirstOrDefault(o => o.Category == category);
            var risky = kind.Equals("Layer", StringComparison.OrdinalIgnoreCase);
            var label = (risky ? "⚠ " : "") + category.Split(' ')[0];
            var sub = current is null ? $"{kind} · donor default{(risky ? " (ABP — crash-prone)" : "")}" : $"→ {current.ReplacementSet.Split('_').Last()}";
            var accent = current is not null ? Theme.Animations : risky ? Color.FromArgb(220, 120, 60) : Theme.OnDarkMuted;
            var cat = category;
            var tile = MakeTile(label, sub, () => PickAnimSwapFamily(cat), accent);
            tile.Height = 88;
            _toyboxTileFlow.Controls.Add(tile);
        }
    }

    /// <summary>
    /// Animations landing page: explains what the tool can actually do now and
    /// gates it behind the custom archetype. Custom archetype clones the suit's
    /// own family archetype + anim assets so we can edit them per-suit.
    /// </summary>
    private void RefreshAnimationOverview(GameDataFamily? family)
    {
        EnsureProject();
        var on = _currentProject?.UseCustomArchetype == true;

        var intro = FullWidthNote(
            "How suit animations work: turn on Custom archetype and the tool clones this suit's own character archetype + anim sets into your mod, so it can edit them per-suit without touching the base game. Then:\n" +
            "  • Replace idle/walk/run — swap individual locomotion poses for custom or borrowed AnimSequences (safe).\n" +
            "  • Swap category by family — borrow a whole montage category (jump/glide). ⚠ Layer/locomotion category swaps crash across families — use Replace idle/walk/run instead.\n" +
            "  • Equipment animations graft in automatically when you add a foreign gadget (Equipment tab).");
        var introTextHeight = TextRenderer.MeasureText(
            intro.Text,
            intro.Font,
            new Size(Math.Max(1, intro.ClientSize.Width - intro.Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        intro.Height = Math.Max(132, introTextHeight + intro.Padding.Vertical + 4);
        _toyboxTileFlow.Controls.Add(intro);

        // The custom-archetype gate toggle.
        var toggle = MakeTile(
            on ? "✓ Custom archetype: ON" : "Custom archetype: OFF",
            on ? "editing enabled — click to disable" : "click to enable animation editing",
            () =>
            {
                EnsureProject();
                if (_currentProject is null) { AppendLog("Set a base suit first."); return; }
                _currentProject.UseCustomArchetype = !_currentProject.UseCustomArchetype;
                _currentProject.GliderAutoEnabledCustomArchetype = false;
                RecordChange("Animations", "archetype", _currentProject.UseCustomArchetype ? "custom archetype enabled" : "custom archetype disabled", status: "staged");
                AppendLog($"Custom archetype {(_currentProject.UseCustomArchetype ? "ENABLED" : "disabled")}. Clones this suit's family archetype + anim sets and reparents the playable/cutscene on next package.");
                RefreshToyboxTiles();
            },
            on ? Theme.Animations : Color.FromArgb(150, 156, 166));
        toggle.Height = 92;
        toggle.Width = 250;
        _toyboxTileFlow.Controls.Add(toggle);
        _toyboxTileFlow.SetFlowBreak(toggle, true);

        if (!on)
        {
            return;
        }

        // Shortcuts into the actual editors.
        _toyboxTileFlow.Controls.Add(MakeTile("Replace idle/walk/run →", "custom locomotion poses",
            () => SelectComboValue(_toyboxTypeCombo, "Replace idle/walk/run"), Theme.Animations));
        _toyboxTileFlow.Controls.Add(MakeTile("Swap category by family →", "borrow jump/glide (montage)",
            () => SelectComboValue(_toyboxTypeCombo, "Swap category by family"), Theme.Animations));

        // Import a modder-cooked animation pak trio into the library. Only AnimSequences are kept,
        // and they ship inside a suit's pak only when that suit's overrides reference them.
        var importedCount = 0;
        try
        {
            var lib = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath()).Load();
            importedCount = lib.Entries.Count(e => e.CachedFiles.Count > 0
                && !e.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase)
                && !e.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase));
        }
        catch { /* library optional */ }
        var importTile = MakeTile("Import custom animations →",
            importedCount > 0 ? $"{importedCount} in library · add more from a pak" : "add cooked anims from a UE pak trio",
            () => _ = ImportCustomAnimationsFromPakAsync(), Theme.Animations);
        _toyboxTileFlow.Controls.Add(importTile);
        _toyboxTileFlow.SetFlowBreak(importTile, true);

        // Current staged animation changes for this suit.
        var loco = _currentProject?.LocomotionOverrides.Count ?? 0;
        var swaps = _currentProject?.AnimationOverrides.Count ?? 0;
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            $"Staged for this suit: {loco} locomotion pose override(s), {swaps} category swap(s). Package the suit to apply them."));
    }

    /// <summary>
    /// Picker for a locomotion replacement: imported custom animations (library) first, then the
    /// game AnimSequence catalog, plus a free-text custom /Game path. Returns an object path
    /// (<c>/Game/…/A_Foo.A_Foo</c>) or null. Imported anims ship in the suit's pak automatically
    /// when referenced; a raw custom path only resolves if that anim is mounted (import it, or ship
    /// it in its own pak).
    /// </summary>
    private string? PickAnimReplacement(string title)
    {
        var svc = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath());
        var libEntries = svc.Load().Entries
            .Where(e => e.CachedFiles.Count > 0
                        && !e.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase)
                        && !e.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gd = GameDataService.Instance;
        var gameAnims = gd.HasCatalog
            ? gd.AssetsOfClass("AnimSequence").Select(a => a.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        var rows = new List<(string Display, string Pkg)>();
        foreach (var e in libEntries)
        {
            rows.Add(($"★ custom · {e.Name}    {e.PackagePath}", UnrealPathUtil.NormalizePackagePath(e.PackagePath)));
        }
        foreach (var p in gameAnims)
        {
            rows.Add((p, p));
        }

        using var dlg = new AdaptiveDialogForm
        {
            Text = title,
            Width = 800,
            Height = 580,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(620, 440),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var search = new TextBox { Dock = DockStyle.Top, Height = 30, PlaceholderText = "Filter, or paste a custom /Game/… path" };
        Theme.StyleDarkInput(search);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.None };
        Theme.StyleListBox(list);
        var ok = new Button { Text = "Use selected", Dock = DockStyle.Bottom, Height = 34 };
        Theme.StyleGoldButton(ok);
        ok.DialogResult = DialogResult.OK;

        var view = new List<(string Display, string Pkg)>();
        void Fill(string term)
        {
            list.BeginUpdate();
            list.Items.Clear();
            view.Clear();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(term) || r.Display.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    view.Add(r);
                    list.Items.Add(r.Display);
                }
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        list.DoubleClick += (_, _) => { if (list.SelectedItem is not null) ok.PerformClick(); };
        Fill("");

        dlg.Controls.Add(list);
        dlg.Controls.Add(search);
        dlg.Controls.Add(ok);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        // A typed game or installed Game Feature path wins; otherwise use the selected row.
        var typed = search.Text.Trim();
        string? pkg = null;
        if (ExtractedPackagePathService.IsContentPackagePath(typed))
        {
            pkg = UnrealPathUtil.NormalizePackagePath(typed);
        }
        else if (list.SelectedIndex >= 0 && list.SelectedIndex < view.Count)
        {
            pkg = view[list.SelectedIndex].Pkg;
        }
        if (string.IsNullOrWhiteSpace(pkg))
        {
            return null;
        }
        var leaf = pkg[(pkg.LastIndexOf('/') + 1)..];
        return $"{pkg}.{leaf}";
    }

    /// <summary>
    /// "Import custom animations": unpacks a modder-cooked pak trio (retoc to-legacy, staged next
    /// to the game's global container for script objects), registers every AnimSequence into the
    /// library (rejecting non-animations), and leaves them available to Replace-idle/walk/run. They
    /// ship inside a suit's pak only when that suit references them (base-game anims never ship).
    /// </summary>
    private async Task ImportCustomAnimationsFromPakAsync()
    {
        var projectRoot = _projectRootText.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            AppendLog("Set a project root first.");
            return;
        }

        using var ofd = new OpenFileDialog
        {
            Title = "Pick your cooked animation pak trio (.utoc)",
            Filter = "IoStore container (*.utoc)|*.utoc",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var utoc = ofd.FileName;
        var trioBase = Path.Combine(Path.GetDirectoryName(utoc)!, Path.GetFileNameWithoutExtension(utoc));

        var paksRoot = AppSettings.Current.EffectiveGamePaksRoot();
        var globalUtoc = string.IsNullOrWhiteSpace(paksRoot) ? "" : Path.Combine(paksRoot, "global.utoc");
        if (string.IsNullOrWhiteSpace(paksRoot) || !File.Exists(globalUtoc))
        {
            AppendLog($"Need the game's global.utoc to unpack (Settings → game Paks root). Looked for: {globalUtoc}");
            return;
        }

        AppendLog($"Importing custom animations from {Path.GetFileName(utoc)}…");
        var work = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "AnimImport", Guid.NewGuid().ToString("N"));
        var stageDir = Path.Combine(work, "in");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(stageDir);
        try
        {
            foreach (var ext in new[] { ".utoc", ".ucas", ".pak" })
            {
                var f = trioBase + ext;
                if (File.Exists(f)) File.Copy(f, Path.Combine(stageDir, Path.GetFileName(f)), true);
            }
            var globalBase = Path.Combine(paksRoot, "global");
            foreach (var ext in new[] { ".utoc", ".ucas" })
            {
                var f = globalBase + ext;
                if (File.Exists(f)) File.Copy(f, Path.Combine(stageDir, Path.GetFileName(f)), true);
            }

            var exit = await RunRetocToLegacyAsync(stageDir, outDir);
            if (exit != 0)
            {
                AppendLog($"Import failed: retoc to-legacy exit {exit}.");
                return;
            }

            var svc = new AnimLibraryService(projectRoot, AppSettings.Current.EffectiveUsmapPath());
            var lib = svc.Load();
            var report = svc.ImportAnimationPakFolder(lib, outDir);

            foreach (var e in report.Imported) AppendLog($"  ✓ imported '{e.Name}' → {e.PackagePath}");
            foreach (var r in report.RejectedNonAnim) AppendLog($"  ✗ skipped (not an AnimSequence): {r}");
            foreach (var w in report.Warnings) AppendLog($"  ⚠ {w}");
            AppendLog($"Import complete: {report.Imported.Count} animation(s) added, {report.RejectedNonAnim.Count} non-animation asset(s) skipped.");
            if (report.Imported.Count > 0)
            {
                AppendLog("They now appear in Replace idle/walk/run, and ship inside a suit's pak when that suit uses them.");
            }
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            AppendLog($"Import error: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    private void PickAnimSwapFamily(string category)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Changing the animation family"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null) { AppendLog("Set a base suit first."); return; }

        var gd = GameDataService.Instance;
        var currentFamily = TargetFamilyNameForProject(_currentProject);
        var families = gd.FamiliesWithAnimCategory(category)
            .Where(f => !f.Equals(currentFamily, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (families.Count == 0) { AppendLog($"No other families have a '{category}' set."); return; }

        using var dlg = new AdaptiveDialogForm { Text = $"{category} — source family", Width = 360, Height = 420, AutoScaleMode = AutoScaleMode.Dpi, MinimumSize = new Size(320, 340), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.None };
        Theme.StyleListBox(list);
        list.Items.Add("(donor default — remove override)");
        foreach (var f in families) list.Items.Add(f);
        var ok = new Button { Text = "Use", Dock = DockStyle.Bottom, Height = 32 };
        Theme.StyleGoldButton(ok);
        ok.DialogResult = DialogResult.OK;
        list.DoubleClick += (_, _) => ok.PerformClick();
        dlg.Controls.Add(list);
        dlg.Controls.Add(ok);
        if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedItem is null) return;

        _currentProject.AnimationOverrides.RemoveAll(o => o.Category == category);
        _currentProject.GliderAutoEnabledCustomArchetype = false;
        var choice = list.SelectedItem.ToString()!;
        if (choice.StartsWith("("))
        {
            RecordChange("Animations", category, "reverted to donor default", status: "staged");
            AppendLog($"{category}: reverted to donor default.");
        }
        else
        {
            var ov = gd.BuildAnimOverride(category, choice);
            if (ov is null) { AppendLog($"{choice} has no {category} set."); return; }
            if (ov.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase))
            {
                var proceed = Dialog.Confirm(this,
                    "Crash-prone animation swap",
                    $"'{category}' swaps a compiled AnimBlueprint (ABP_Core_{choice}). Driving another family's animgraph on this pawn is KNOWN to crash — confirmed with Catwoman locomotion on Thomas.",
                    confirmText: "Apply anyway", severity: Dialog.Level.Crit);
                if (!proceed) { RefreshToyboxTiles(); return; }
            }
            _currentProject.AnimationOverrides.Add(ov);
            RecordChange("Animations", category, $"borrow {choice} ({ov.ReplacementSet})", status: "staged");
            AppendLog($"{category} → {choice} ({ov.ReplacementSet}). Regenerate to apply.");
        }
        RefreshToyboxTiles();
        PopulateToyboxSlots();
    }

    private void ShowAnimSetDetail(string name)
    {
        var set = GameDataService.Instance.FindAnimSet(name);
        if (set is null)
        {
            Dialog.Info(null, "Animation set", $"{name}\n\n(not in catalog — it may be a building block whose folder wasn't dumped)");
            return;
        }
        var lines = new List<string>
        {
            $"Name: {set.Name}",
            $"Kind: {set.Kind} anim set   Category: {set.Category}",
            $"Package: {set.Package}",
            set.IsCharacterComposite ? $"Composite of {set.ParentSets.Count} block(s):" : "(building block)",
        };
        if (set.ParentSets.Count > 0)
        {
            lines.AddRange(set.ParentSets.Select(p => "  • " + p));
        }
        Dialog.Info(null, $"Animation · {set.Name}", string.Join(Environment.NewLine, lines));
    }

    /// <summary>Stages library-owned animation assets referenced by this suit.</summary>
    private void StageLibraryAnimsIntoContentRoot(NativeSuitProject project, string contentRootToPackage)
    {
        try
        {
            var svc = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath());
            var lib = svc.Load();
            if (lib.Entries.Count == 0)
            {
                return;
            }

            var referenced = project.AnimationOverrides.Select(o => o.ReplacementPackage)
                .Concat(project.LocomotionOverrides.Select(o => o.ReplacementPackage));
            var shippable = svc.ReferencedShippable(lib, referenced);
            if (shippable.Count == 0)
            {
                return;
            }

            var total = 0;
            foreach (var entry in shippable)
            {
                var staged = svc.StageInto(entry, contentRootToPackage);
                if (staged <= 0)
                {
                    throw new InvalidOperationException(
                        $"Referenced library animation '{entry.Name}' has no cached cooked files to stage for {entry.PackagePath}.");
                }
                total += staged;
                AppendLog($"  library anim '{entry.Name}' ({entry.SourceMode}): staged {staged} file(s) → {entry.PackagePath}");
            }
            AppendLog($"Library animations staged into pak: {shippable.Count} entr(y/ies), {total} file(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"  could not stage required library animations: {ex.Message}");
            throw new InvalidOperationException(
                "Required library animation staging failed; release preparation was stopped.",
                ex);
        }
    }
}
