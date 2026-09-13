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
        var tiles = new List<VirtualTilePanel.Tile>(); AddVehicleTiles(tiles, "VEHICLES");
        ShowVirtualTiles(tiles, hero: new VirtualTilePanel.HeroModel { Overline = "VEHICLE WORKSPACE", Title = "Vehicles", Subtitle = "Separate selection · rigged body · materials · lights and attachments", ThumbAccent = Theme.Gliders });
    }
    private void AddVehicleTiles(List<VirtualTilePanel.Tile> tiles, string section)
    {
        tiles.Add(new() { Section = section, Title = "＋ New vehicle", Subtitle = "Batman Forever driving base", Accent = Theme.Gliders, Dashed = true, OnClick = () => EditVehicle() });
        var (active, mod) = ResolveHomeActiveMod(ModService.ListMods());
        if (active is not null) tiles.Add(new() { Section = section, Title = "Manage vehicles in mod", Subtitle = active.DisplayName, Accent = Theme.Gliders, OnClick = () => EditModVehicles(active.Path) });
        foreach (var summary in VehicleService.List())
        {
            var entry = mod?.Vehicles.FirstOrDefault(e => e.VehicleId == summary.Id);
            tiles.Add(new() { Section = section, Title = summary.Name, Subtitle = summary.Error.Length > 0 ? "Unreadable project · click for details" : summary.Owner.Split('.').Last() + " · " + (entry is null ? "not in selected mod" : entry.Enabled ? "in mod · enabled" : "in mod · disabled"),
                Accent = summary.Error.Length > 0 ? Theme.Warn : Theme.Gliders, OnClick = () => EditVehicle(summary.Path), MenuFactory = () => {
                    var menu = new ContextMenuStrip(); menu.Items.Add("Edit vehicle…", null, (_, _) => EditVehicle(summary.Path));
                    if (active is not null && summary.Error.Length == 0) menu.Items.Add(entry is null ? "Add to " + active.DisplayName : "Remove from " + active.DisplayName, null, (_, _) => {
                        var current = ModService.LoadMod(active.Path)!; current.Vehicles.RemoveAll(e => e.VehicleId == summary.Id);
                        if (entry is null) current.Vehicles.Add(VehicleService.Entry(VehicleService.Load(summary.Path)));
                        ModService.SaveMod(current); RefreshWorkspaceAfterModChange();
                    }); return menu;
                } });
        }
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
