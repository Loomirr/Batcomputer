namespace Batcomputer;

public sealed partial class MainForm
{
    private VehicleProjectService VehicleService => new(_projectRootText.Text.Trim());
    private void EditVehicle(string? path = null)
    {
        try
        {
            var service = VehicleService;
            var project = path is null ? new VehicleProject() : service.Load(path);
            using var editor = new VehicleWorkshopForm(service.DirectoryFor(project), project);
            if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is not { } saved) return;
            service.Save(saved); AppendLog("Saved vehicle '" + saved.DisplayName + "' (ID " + saved.Id + ")."); RefreshToyboxTiles();
        }
        catch (Exception ex) { Dialog.Error(this, "Vehicle could not be opened or saved", ex.Message); }
    }
    private void RefreshVehicleWorkspaceTiles()
    {
        var (active, mod) = ResolveHomeActiveMod(ModService.ListMods());
        var saved = VehicleService.List();
        var tiles = new List<VirtualTilePanel.Tile>();
        tiles.Add(new() { Section = "WORKSPACE", Title = "＋ New vehicle", Subtitle = "Start in the 3D workshop", Accent = Theme.Gliders, Dashed = true, OnClick = () => EditVehicle() });
        tiles.Add(new() { Section = "WORKSPACE", Title = active is null ? "Create a mod" : "Change selected mod", Subtitle = active?.DisplayName ?? "Vehicles can ship on their own", Accent = Theme.Gold, OnClick = active is null ? CreateModFlow : ChooseVehicleMod });
        if (active is not null)
        {
            tiles.Add(new() { Section = "WORKSPACE", Title = "Choose vehicles for mod", Subtitle = "Add, enable or remove", Accent = Theme.Gliders, OnClick = () => EditModVehicles(active.Path) });
            tiles.Add(new() { Section = "WORKSPACE", Title = "Build selected mod", Subtitle = active.DisplayName, Accent = Theme.Gold, OnClick = BuildActiveModFromWorkspace });
            tiles.Add(new() { Section = "WORKSPACE", Title = "Mod details & output", Subtitle = "Install, rename, release files", Accent = Theme.OnDarkMuted, OnClick = () => OpenModDetails(active.Path, active.ModId) });
        }
        AddVehicleTiles(tiles, "VEHICLE LIBRARY", includeActions: false);
        ShowVirtualTiles(tiles, hero: new VirtualTilePanel.HeroModel {
            Overline = "VEHICLE WORKSPACE", Title = active?.DisplayName ?? "Your vehicle library",
            Subtitle = saved.Count == 0 ? "Create a vehicle, fit its body in the workshop, then add it to a mod." : "Open a vehicle to edit its body, materials, lights, seats and hardpoints.",
            ThumbAccent = Theme.Gliders, Badge = "EXPERIMENTAL", BadgeColor = Theme.Warn,
            Chips = [(saved.Count + " saved vehicles", Theme.Gliders), ((mod?.Vehicles.Count(v => v.Enabled) ?? 0) + " enabled in selected mod", Theme.Gold), ("No suit required for native owners", Theme.OnDarkMuted)] });
    }
    private void ChooseVehicleMod()
    {
        using var dialog = new AdaptiveDialogForm { Text = "Select mod", ClientSize = new Size(520, 190), MinimumSize = new Size(400, 190), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, Font = Theme.Body, Padding = new Padding(16) };
        var choices = new ThemedDropDown { Dock = DockStyle.Top };
        foreach (var mod in ModService.ListMods()) choices.Items.Add(new VehicleModChoice(mod));
        choices.SelectedItem = choices.Items.Cast<VehicleModChoice>().FirstOrDefault(m => m.Mod.Path == _homeActiveModProjectPath);
        var select = new Button { Text = "Select mod", Width = 130, DialogResult = DialogResult.OK }; Theme.StyleGoldButton(select);
        var create = new Button { Text = "New mod…", Width = 120, DialogResult = DialogResult.Retry }; Theme.StyleDarkButton(create);
        dialog.Controls.Add(choices); dialog.Controls.Add(DialogActionFooter.Create(select, create)); dialog.AcceptButton = select;
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.OK && choices.SelectedItem is VehicleModChoice selected) SelectHomeMod(selected.Mod.Path);
        else if (result == DialogResult.Retry) CreateModFlow();
    }
    private sealed record VehicleModChoice(ModProjectService.ModSummary Mod) { public override string ToString() => Mod.DisplayName + " · " + Mod.ModId; }
    private void AddVehicleTiles(List<VirtualTilePanel.Tile> tiles, string section, bool includeActions = true)
    {
        if (includeActions) tiles.Add(new() { Section = section, Title = "＋ New vehicle", Subtitle = "Choose a driving base", Accent = Theme.Gliders, Dashed = true, OnClick = () => EditVehicle() });
        var (active, mod) = ResolveHomeActiveMod(ModService.ListMods());
        if (includeActions && active is not null) tiles.Add(new() { Section = section, Title = "Manage vehicles in mod", Subtitle = active.DisplayName, Accent = Theme.Gliders, OnClick = () => EditModVehicles(active.Path) });
        var search = includeActions ? "" : CurrentToyboxSearch();
        var library = VehicleService.List().Where(v => (v.Name + " " + v.Owner + " " + v.Id).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => mod?.Vehicles.Any(e => e.VehicleId == v.Id) == true ? 0 : 1).ToArray();
        foreach (var summary in library)
        {
            var entry = mod?.Vehicles.FirstOrDefault(e => e.VehicleId == summary.Id);
            tiles.Add(new() { Section = includeActions ? section : entry is null ? "AVAILABLE VEHICLES" : "IN SELECTED MOD", Title = summary.Name, Subtitle = summary.Error.Length > 0 ? "Unreadable project · click for details" : summary.Owner.Split('.').Last() + " · " + (entry is null ? "not in selected mod" : entry.Enabled ? "in mod · enabled" : "in mod · disabled"),
                ToolTip = summary.Error.Length > 0 ? summary.Error : summary.Name + "\nID: " + summary.Id + "\nOpen: 3D workshop\nRight-click: mod membership",
                Accent = summary.Error.Length > 0 ? Theme.Warn : Theme.Gliders, OnClick = () => EditVehicle(summary.Path), MenuFactory = () => {
                    var menu = new ContextMenuStrip(); menu.Items.Add("Edit vehicle…", null, (_, _) => EditVehicle(summary.Path));
                    if (active is not null && summary.Error.Length == 0) menu.Items.Add(entry is null ? "Add to " + active.DisplayName : "Remove from " + active.DisplayName, null, (_, _) => {
                        var current = ModService.LoadMod(active.Path)!; current.Vehicles.RemoveAll(e => e.VehicleId == summary.Id);
                        if (entry is null) current.Vehicles.Add(VehicleService.Entry(VehicleService.Load(summary.Path)));
                        ModService.SaveMod(current); RefreshWorkspaceAfterModChange();
                    }); return menu;
                } });
        }
        if (!includeActions && library.Length == 0) tiles.Add(new() { Section = section, Title = search.Length > 0 ? "No matching vehicles" : "No saved vehicles yet", Subtitle = search.Length > 0 ? "Try a name, owner or vehicle ID" : "Start with New vehicle above", Accent = Theme.OnDarkMuted });
    }
    private void EditModVehicles(string modPath)
    {
        try
        {
            var mod = ModService.LoadMod(modPath) ?? throw new InvalidDataException("Mod project is missing.");
            using var dialog = new AdaptiveDialogForm { Text = "Vehicles in " + mod.DisplayName, ClientSize = new Size(610, 490), MinimumSize = new Size(450, 350), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, Font = Theme.Body, Padding = new Padding(14) };
            var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, DisplayMember = "Label" };
            var saved = VehicleService.List();
            foreach (var summary in saved) { var entry = mod.Vehicles.FirstOrDefault(e => e.VehicleId == summary.Id); list.Items.Add(new VehicleChoice(summary.Name, summary.Path, summary.Id), entry?.Enabled == true); }
            foreach (var missing in mod.Vehicles.Where(e => !saved.Any(s => s.Id == e.VehicleId))) list.Items.Add(new VehicleChoice("Missing: " + missing.VehicleId, VehicleService.Resolve(missing), missing.VehicleId), missing.Enabled);
            var ok = new Button { Text = "Save selection", Width = 155, DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel }; Theme.StyleGoldButton(ok); Theme.StyleDarkButton(cancel);
            dialog.Controls.Add(list); dialog.Controls.Add(DialogActionFooter.Create(ok, cancel)); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            mod.Vehicles = list.Items.Cast<VehicleChoice>().Select((item, index) => new ModVehicleEntry { VehicleId = item.Id, VehicleProjectPath = Path.GetRelativePath(_projectRootText.Text.Trim(), item.Path), Enabled = list.GetItemChecked(index) }).Where(e => e.Enabled || mod.Vehicles.Any(old => old.VehicleId == e.VehicleId)).ToList();
            ModService.SaveMod(mod); RefreshWorkspaceAfterModChange();
        }
        catch (Exception ex) { Dialog.Error(this, "Vehicle selection not saved", ex.Message); }
    }
    private sealed record VehicleChoice(string Label, string Path, string Id) { public override string ToString() => Label; }

    private List<ModSuitEntry> ExpandModMembers(NativeSuitModProject mod)
    {
        var suits = new SuitProjectService(_projectRootText.Text.Trim()); var members = mod.Suits.ToList();
        foreach (var vehicle in VehicleService.Enabled(mod))
        {
            if (string.IsNullOrWhiteSpace(vehicle.OwnerCharacterProjectPath))
            {
                if (!VehicleAssetService.NativeOwners.Contains(vehicle.OwnerTag, StringComparer.Ordinal)) throw new InvalidDataException("Vehicle '" + vehicle.DisplayName + "' needs its saved custom character owner.");
                continue;
            }
            var entry = new ModSuitEntry { SuitProjectPath = vehicle.OwnerCharacterProjectPath, Enabled = true };
            var owner = suits.LoadProject(ModService.ResolveSuitProjectPath(entry));
            if (owner?.CustomCharacter is not { IsDefinition: true } identity || CustomCharacterProjectService.Scope(identity) != vehicle.OwnerTag) throw new InvalidDataException("The saved owner of '" + vehicle.DisplayName + "' is missing or has a different identity.");
            entry.SuitId = owner.SlotId;
            if (members.Any(e => e.SuitId == owner.SlotId && !e.Enabled)) throw new InvalidDataException("The owner of '" + vehicle.DisplayName + "' is disabled in this mod. Enable the character or disable its vehicle.");
            if (!members.Any(e => e.SuitId == owner.SlotId && e.Enabled)) members.Add(entry);
        }
        return CustomCharacterProjectService.ExpandMembers(members, ModService, suits);
    }
    internal async Task<HeadlessModBuildResult> BuildVehicleModForCliAsync(string projectRoot, string modProjectPath)
    {
        _batchMode = true; _projectRootText.Text = Path.GetFullPath(projectRoot); _projectService = new(_projectRootText.Text);
        var mod = ModService.LoadMod(modProjectPath) ?? throw new InvalidDataException("Mod not found.");
        if (!mod.Vehicles.Any(e => e.Enabled)) throw new InvalidDataException("No enabled vehicle to test.");
        var success = await BuildModAsync(modProjectPath);
        return new(success, _diagnostics.LogText, ExpectedModTrioPaths(ModBuildRoot(mod.ModId), mod.PackageBaseName));
    }
}
