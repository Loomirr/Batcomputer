using System.Text.Json;

namespace Batcomputer;

/// <summary>An isolated placement session. Only the matching local page may return validated edits.</summary>
internal sealed class VehicleWorkshopForm : AdaptiveForm
{
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft };
    private VehicleProject _source;
    private readonly string _directory;
    private readonly string _folder = Path.Combine(AppSettings.RuntimeRoot, "VehicleWorkshops", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cancel = new();
    private VehicleWorkshopService.Scene? _scene;
    private bool _busy;
    private string? _activeFolder;
    internal VehicleProject? Result { get; private set; }

    internal VehicleWorkshopForm(string directory, VehicleProject project)
    {
        _directory = directory; _source = project.Clone(); Text = "Batcomputer — " + project.DisplayName + " / Vehicle workshop";
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(1440, 900); MinimumSize = new Size(960, 650);
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var cancel = new Button { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel }; Theme.StyleDarkButton(cancel);
        var setup = new Button { Text = "Vehicle setup", Width = 130 }; Theme.StyleDarkButton(setup); setup.Click += async (_, _) => { if (_busy) return; try { if (!await _viewer.RequestVehicleSettingsAsync()) await OpenSettings(null); } catch (Exception ex) { _status.Text = "Setup could not open: " + ex.Message; } };
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 6, 12, 6), ColumnCount = 3 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 140)); footer.ColumnStyles.Add(new(SizeType.Absolute, 115)); footer.Controls.Add(_status, 0, 0); footer.Controls.Add(setup, 1, 0); footer.Controls.Add(cancel, 2, 0);
        Controls.Add(_viewer); Controls.Add(footer); CancelButton = cancel;
        _viewer.VehicleWorkshopMessageReceived += Receive;
        Shown += async (_, _) => await Reload();
        FormClosing += (_, e) => { if (_busy) { _cancel.Cancel(); e.Cancel = true; _status.Text = "Stopping preview preparation…"; } };
        FormClosed += (_, _) =>
        {
            _viewer.Dispose(); _cancel.Dispose();
            try { if (Directory.Exists(_folder) && Guid.TryParseExact(Path.GetFileName(_folder), "N", out _) && FileSystemPathUtil.IsWithinDirectory(_folder, Path.Combine(AppSettings.RuntimeRoot, "VehicleWorkshops"))) Directory.Delete(_folder, true); }
            catch { /* A renderer can briefly retain a file handle; never touch another preview. */ }
        };
    }
    private async Task Reload()
    {
        _busy = true; _scene = null; _status.Text = "Preparing vehicle workshop…"; _viewer.ShowMessage(_status.Text);
        try
        {
            var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
            _scene = await Task.Run(() => VehicleWorkshopService.Create(_directory, _source, folder, Guid.NewGuid().ToString("N"), _cancel.Token,
                text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _status.Text = text; }); }));
            _cancel.Token.ThrowIfCancellationRequested(); await _viewer.ShowFolderAsync(folder);
            _activeFolder = folder;
            _status.Text = "Select a surface to change its material. Save vehicle keeps your edits.";
        }
        catch (OperationCanceledException) { _status.Text = "Preview cancelled."; }
        catch (Exception ex) { _status.Text = ex.Message; _viewer.ShowMessage("Workshop could not load. Vehicle setup is still available below.\n\n" + ex.Message); }
        finally { _busy = false; if (_cancel.IsCancellationRequested) Close(); }
    }
    private async Task OpenSettings(string? draft)
    {
        if (_busy) return;
        try
        {
            // Preserve the canvas draft before entering setup; neither action saves the recipe.
            if (draft is not null && _scene is not null) _source = ReadPlacementMessage(_source, _scene, draft);
            using var setup = new VehicleEditorForm(AppSettings.Current.EffectiveProjectRoot(), _source, settingsOnly: true);
            if (setup.ShowDialog(this) == DialogResult.OK && setup.Result is { } edited) { _source = edited; await Reload(); }
        }
        catch (Exception ex) { _status.Text = "Setup was not applied: " + ex.Message; }
    }
    private async void Receive(string json)
    {
        if (_scene is null || _busy) return;
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.GetProperty("session").GetString() != _scene.Session || root.GetProperty("vehicleId").GetString() != _source.Id) return;
            if (root.GetProperty("type").GetString() == "vehicleWorkshopSave")
            {
                Result = ReadPlacementMessage(_source, _scene, json); DialogResult = DialogResult.OK; Close();
            }
            else if (root.GetProperty("type").GetString() == "vehicleWorkshopSettings") await OpenSettings(json);
            else if (root.GetProperty("type").GetString() == "vehicleWorkshopCopyMaterial") await CopyMaterial(json);
            else if (root.GetProperty("type").GetString() == "vehicleWorkshopRiders") await PrepareRiders();
            else if (root.GetProperty("type").GetString() == "vehicleWorkshopError") _status.Text = "Preview: " + root.GetProperty("error").GetString();
        }
        catch (Exception ex) { _status.Text = "Placements were not applied: " + ex.Message; }
    }
    private async Task PrepareRiders()
    {
        if (_busy || _activeFolder is null) return;
        _busy = true;
        try
        {
            var riders = await Task.Run(() => VehicleRiderPreviewService.Create(Path.Combine(_activeFolder, "Riders"), _cancel.Token,
                text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _status.Text = text; }); }));
            _cancel.Token.ThrowIfCancellationRequested();
            await _viewer.ShowVehicleRidersAsync(JsonSerializer.Serialize(riders, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            _status.Text = "Seated idle preview. Seat edits move the figures; steering, entry/exit and cape simulation still need in-game checks.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Seated preview could not load: " + ex.Message; await _viewer.ShowVehicleRidersAsync("null"); }
        finally { _busy = false; if (_cancel.IsCancellationRequested) Close(); }
    }
    internal static VehicleProject ReadPlacementMessage(VehicleProject source, VehicleWorkshopService.Scene scene, string json)
    {
        if (json.Length > 256_000) throw new InvalidDataException("Placement message is too large.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("type").GetString() is not ("vehicleWorkshopSave" or "vehicleWorkshopSettings" or "vehicleWorkshopCopyMaterial") || root.GetProperty("session").GetString() != scene.Session || root.GetProperty("vehicleId").GetString() != source.Id)
            throw new InvalidDataException("Stale vehicle workshop session.");
        var edits = JsonSerializer.Deserialize<List<VehicleComponentTransform>>(root.GetProperty("transforms").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing placements.");
        if (edits.Count > scene.Parts.Count + source.Transforms.Count || edits.Any(e => e is null)) throw new InvalidDataException("Unexpected placement data.");
        var allowed = scene.Parts.Where(p => p.Editable).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edit in edits.Where(e => !allowed.Contains(e.Component)))
        {
            var original = source.Transforms.FirstOrDefault(t => t.Component == edit.Component);
            if (original is null || JsonSerializer.Serialize(original) != JsonSerializer.Serialize(edit)) throw new InvalidDataException("This component is read-only: " + edit.Component);
        }
        foreach (var original in source.Transforms.Where(t => !allowed.Contains(t.Component)))
            if (!edits.Any(t => t.Component == original.Component)) throw new InvalidDataException("A read-only placement was omitted.");
        var result = source.Clone(); result.Transforms = edits;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (root.TryGetProperty("materialOverrides", out var materials))
        {
            result.MaterialOverrides = JsonSerializer.Deserialize<List<VehicleMaterialOverride>>(materials.GetRawText(), options) ?? throw new InvalidDataException("Missing materials.");
            var catalog = scene.MaterialCatalog.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in result.MaterialOverrides)
            {
                if (entry is null || !scene.Parts.Any(p => p.Id == entry.Component && p.Materials.Any(m => m.Slot == entry.Slot))) throw new InvalidDataException("Unknown material slot.");
                if (!catalog.Contains(entry.MaterialPath) && !source.MaterialOverrides.Any(m => m.Component == entry.Component && m.Slot == entry.Slot && m.MaterialPath == entry.MaterialPath)) throw new InvalidDataException("Choose a material from the available library.");
            }
        }
        if (root.TryGetProperty("disabledParts", out var disabled))
        {
            result.DisabledParts = JsonSerializer.Deserialize<List<string>>(disabled.GetRawText()) ?? throw new InvalidDataException("Missing part state.");
            if (result.DisabledParts.Any(id => !scene.Parts.Any(p => p.Id == id && p.CanDisable))) throw new InvalidDataException("This part cannot be disabled.");
        }
        if (root.TryGetProperty("lights", out var lights))
        {
            result.Lights = JsonSerializer.Deserialize<List<VehicleLightSettings>>(lights.GetRawText(), options) ?? throw new InvalidDataException("Missing light settings.");
            if (result.Lights.Any(l => l is null || !scene.Parts.Any(p => p.Id == l.Component && p.Light is not null))) throw new InvalidDataException("This component is not an editable light source.");
        }
        if (root.TryGetProperty("accentColor", out var accent)) result.AccentColor = accent.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Deserialize<VehicleRgbColor>(accent.GetRawText(), options) ?? throw new InvalidDataException("Invalid accent color.");
        if (root.TryGetProperty("lightSurfaces", out var surfaces))
        {
            result.LightSurfaces = JsonSerializer.Deserialize<List<VehicleLightSurface>>(surfaces.GetRawText(), options) ?? throw new InvalidDataException("Missing light surface assignments.");
            foreach (var binding in result.LightSurfaces)
                if (binding is null || !scene.LightSurfaceChoices.Any(s => s.Slot == binding.Slot && s.CanAssign)) throw new InvalidDataException("Choose a rigid Body-weighted light surface.");
        }
        VehicleProjectService.ValidateIdentity(result); return result;
    }
    private async Task CopyMaterial(string json)
    {
        if (_scene is null || _busy) return;
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var package = root.GetProperty("copySource").GetString() ?? "";
        var component = root.GetProperty("copyComponent").GetString(); var slot = root.GetProperty("copySlot").GetInt32();
        if (!_scene.MaterialCatalog.Any(m => m.Path == package) || !_scene.Parts.Any(p => p.Id == component && p.Materials.Any(m => m.Slot == slot))) throw new InvalidDataException("Choose a material and a valid destination slot.");
        _source = ReadPlacementMessage(_source, _scene, json); _busy = true;
        try
        {
            _status.Text = "Preparing material copy…";
            var projectRoot = AppSettings.Current.EffectiveProjectRoot();
            var path = await VehicleMaterialCatalogService.PrepareCopySource(projectRoot, package, Path.Combine(_folder, "MaterialSources", Guid.NewGuid().ToString("N")), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            var name = "MI_" + _source.Id + "_Copy_" + Guid.NewGuid().ToString("N")[..6];
            using var wizard = new MaterialWizard(projectRoot, "Vehicle_" + _source.Id, name);
            wizard.PrefillBase(path, name, editInPlace: false);
            if (wizard.ShowDialog(this) != DialogResult.OK || wizard.ResultMiPackagePath is not { Length: > 0 } output) { _status.Text = "Material copy cancelled. Vehicle edits are still open."; return; }
            new ToolMaterialLibraryService(projectRoot).Register([new GeneratedMaterialEntry { DisplayName = UnrealPathUtil.AssetName(output), Kind = "Material", PackagePath = output, SourceMaterialPackagePath = package, ParentMaterialPath = wizard.ResultParentMaterialPath ?? "", CreatedUtc = DateTime.UtcNow.ToString("O") }]);
            _source.MaterialOverrides.RemoveAll(m => m.Component == component && m.Slot == slot);
            _source.MaterialOverrides.Add(new() { Component = component!, Slot = slot, MaterialPath = output });
            await Reload();
        }
        catch (OperationCanceledException) { }
        finally { _busy = false; if (_cancel.IsCancellationRequested) Close(); }
    }
}
