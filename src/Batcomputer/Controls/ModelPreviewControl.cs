using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// The 3D character preview as an embeddable control, so it can live inside a tab rather than only
/// in a pop-out window. Same WebView2 + three.js page the pop-out uses - this just owns the host.
/// </summary>
public sealed class ModelPreviewControl : UserControl
{
    internal async Task NotifyCustomMeshDraftAsync(string component, PreviewCustomMeshTransform transform, string? error = null)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        var data = JsonSerializer.Serialize(new { scale = transform.Scale,
            offset = new[] { transform.OffsetX, transform.OffsetY, transform.OffsetZ },
            rotation = new[] { transform.RotationPitch, transform.RotationYaw, transform.RotationRoll } });
        try { await core.ExecuteScriptAsync($"window.characterMeshEditor?.draftResult({JsonSerializer.Serialize(component)},{data},{JsonSerializer.Serialize(error)})"); }
        catch (Exception ex) { Debug.WriteLine("Preview draft acknowledgement: " + ex.Message); }
    }

    internal async Task NotifyCustomMeshBakeFailedAsync(string error)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync($"window.characterMeshEditor?.bakeFailed({JsonSerializer.Serialize(error)})"); }
        catch (Exception ex) { Debug.WriteLine("Preview bake acknowledgement: " + ex.Message); }
    }

    private string _virtualHost = "preview.batcomputer";
    private WebView2? _web;
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Theme.OnDarkMuted,
        Font = Theme.Body,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "Pick a character, then choose \"View in 3D\".",
    };

    private string? _pendingFolder;
    private bool _ready;
    private bool _active = true;
    private uint? _browserProcessId;
    private string? _userDataFolder;
    private int _rendererVersion;
    private string? _animationWorkspace;
    private NativeSuitProject? _animationCharacter;
    internal event Func<Task>? AnimationPackageImportRequested;
    private bool _animationSourceImporting;
    private async Task ImportAnimationSourceAsync(string kind, string layoutKey, string rigSignature)
    {
        var folder = _pendingFolder; var web = _web; var character = _animationCharacter; var workspace = _animationWorkspace;
        if (_animationSourceImporting || character is null || string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(folder) ||
            layoutKey != ViewerLayoutService.SuitKey(character) || rigSignature.Length is < 1 or > 20_000) return;
        _animationSourceImporting = true;
        string? error = null; object? result = null;
        try
        {
            if (kind is "pak" or "library")
            {
                if (kind == "pak" && AnimationPackageImportRequested is { } handler) await handler();
                if (folder != _pendingFolder || !ReferenceEquals(web, _web)) return;
                var catalog = await Task.Run(() => CharacterAnimationPreviewService.AddWorkspaceAnimations(folder, workspace));
                result = new { catalog };
            }
            else if (kind == "blender")
            {
                using var sourceDialog = new OpenFileDialog { Title = "Import animation from a LOTDK-rig Blender file",
                    Filter = "Blender project (*.blend)|*.blend", CheckFileExists = true };
                if (sourceDialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; }
                else
                {
                    var executable = AppSettings.Current.BlenderExePath;
                    if (!File.Exists(executable))
                    {
                        using var executableDialog = new OpenFileDialog { Title = "Select your installed blender.exe (used read-only)",
                            Filter = "Blender executable (blender.exe)|blender.exe", CheckFileExists = true };
                        if (executableDialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; }
                        else { executable = executableDialog.FileName; AppSettings.Current.BlenderExePath = executable; AppSettings.Current.Save(); }
                    }
                    if (result is null && executable is not null)
                    {
                        var work = Path.Combine(folder, "blender-imports", Guid.NewGuid().ToString("N"));
                        var manifest = Path.Combine(work, "choices.json");
                        await BlenderAnimationImportService.RunAsync(executable, sourceDialog.FileName, work, new { mode = "list", rigSignature, output = manifest });
                        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 500_000) throw new InvalidDataException("Blender returned an invalid action list.");
                        var choices = JsonSerializer.Deserialize<BlenderAnimationImportService.Choice[]>(File.ReadAllText(manifest),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                        if (choices.Length is < 1 or > 512) throw new InvalidDataException("No supported animation actions were found.");
                        if (folder != _pendingFolder || !ReferenceEquals(web, _web)) return;
                        var choice = ChooseBlenderAction(choices);
                        if (choice is null) result = new { cancelled = true };
                        else
                        {
                            var output = Path.Combine(work, "motion.glb");
                            await BlenderAnimationImportService.RunAsync(executable, sourceDialog.FileName, work,
                                new { mode = "export", rigSignature, output, rig = choice.Rig, action = choice.Action, slot = choice.Slot });
                            if (!File.Exists(output) || new FileInfo(output).Length is 0 or > 80_000_000)
                                throw new InvalidDataException("The animation export is missing or exceeds 80 MB.");
                            result = new { file = "blender-imports/" + Path.GetFileName(work) + "/motion.glb", name = choice.Action };
                        }
                    }
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        finally { _animationSourceImporting = false; }
        if (IsDisposed || folder != _pendingFolder || !ReferenceEquals(web, _web) || web?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync("window.characterAnimationCreator?.sourceImported(" +
            JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "," + JsonSerializer.Serialize(error) + ")"); }
        catch (Exception ex) { Debug.WriteLine("Animation source import response: " + ex.Message); }
    }
    private BlenderAnimationImportService.Choice? ChooseBlenderAction(BlenderAnimationImportService.Choice[] choices)
    {
        using var dialog = new Form { Text = "Choose an animation to edit", Size = new Size(640, 480), MinimumSize = new Size(460, 360),
            StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, Font = Theme.Body };
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = choices, DisplayMember = "Label", BackColor = Theme.WindowBg, ForeColor = Theme.OnDark };
        var help = new Label { Dock = DockStyle.Top, Height = 60, Text = "Choose one action on the LOTDK body rig. A copy becomes editable keys.\nYour Blender file and existing animations are not modified.", Padding = new Padding(12) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft };
        var import = new Button { Text = "Import action", Width = 120, Height = 36, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 36, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([import, cancel]); dialog.Controls.Add(list); dialog.Controls.Add(help); dialog.Controls.Add(buttons);
        dialog.AcceptButton = import; dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? list.SelectedItem as BlenderAnimationImportService.Choice : null;
    }
    internal void ConfigureAnimationLibrary(string workspace, NativeSuitProject? character)
    {
        _animationWorkspace = workspace;
        _animationCharacter = character;
    }
    private async Task SaveAnimationDraftAsync(string json, string? id, string layoutKey)
    {
        var folder = _pendingFolder; var web = _web; var character = _animationCharacter; var workspace = _animationWorkspace;
        if (character is null || string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(folder) || layoutKey != ViewerLayoutService.SuitKey(character)) return;
        string? error = null; AnimationDraftLibraryService.Entry? entry = null; AnimationDraftLibraryService.View[]? views = null;
        try
        {
            var service = new AnimationDraftLibraryService(workspace);
            entry = await Task.Run(() => service.Save(json, character, id));
            views = service.Views(character);
            // Refresh only the small private draft files; do not rebuild meshes or navigate.
            foreach (var view in views.Where(item => item.Available && item.Id == entry.Id))
            {
                var destination = Path.Combine(folder!, "drafts"); Directory.CreateDirectory(destination);
                File.Copy(Path.Combine(service.Root, view.Id + ".json"), Path.Combine(destination, view.Id + ".json"), true);
            }
        }
        catch (Exception ex) { error = ex.Message.Split('\n')[0]; }
        if (IsDisposed || folder != _pendingFolder || !ReferenceEquals(web, _web) || web?.CoreWebView2 is not { } core) return;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        try { await core.ExecuteScriptAsync("window.characterAnimationCreator?.librarySaved(" + JsonSerializer.Serialize(entry?.Id) + "," +
            JsonSerializer.Serialize(views, options) + "," + JsonSerializer.Serialize(error) + ")"); }
        catch (Exception ex) { Debug.WriteLine("Animation library save response: " + ex.Message); }
    }

    /// <summary>Raised when the in-viewer part mover asks the host to persist an alignment.</summary>
    public event EventHandler<PreviewPlacementSaveRequestedEventArgs>? PlacementSaveRequested;
    internal event EventHandler<PreviewSuitIconTestRequestedEventArgs>? SuitIconTestRequested;
    internal event EventHandler<PreviewSuitIconTestRequestedEventArgs>? SuitIconApplyRequested;
    internal event EventHandler<PreviewCharacterIconsRequestedEventArgs>? CharacterIconsApplyRequested;
    private async Task SendCharacterAnimationAsync(string package)
    {
        var folder = _pendingFolder;
        var web = _web;
        if (string.IsNullOrWhiteSpace(folder) || web?.CoreWebView2 is null) return;
        CharacterAnimationPreviewService.Bundle? bundle = null;
        string? error = null;
        try { bundle = await Task.Run(() => CharacterAnimationPreviewService.LoadBundle(folder, package)); }
        catch (Exception ex) { error = ex.Message.Split('\n')[0]; }
        if (IsDisposed || !ReferenceEquals(web, _web) || !string.Equals(folder, _pendingFolder, StringComparison.OrdinalIgnoreCase) ||
            web.CoreWebView2 is not { } core) return;
        try
        {
            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await core.ExecuteScriptAsync($"window.characterAnimationPreview?.bundleResult({JsonSerializer.Serialize(package)},{json},{JsonSerializer.Serialize(error)})");
        }
        catch (Exception ex) { Debug.WriteLine("Preview animation response: " + ex.Message); }
    }
    internal async Task NotifySuitIconTestAsync(bool success, string message)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try
        {
            var result = JsonSerializer.Serialize(new { success, message });
            await core.ExecuteScriptAsync($"window.characterIconStudio?.testResult({result})");
        }
        catch (Exception ex) { Debug.WriteLine("Preview icon-test acknowledgement: " + ex.Message); }
    }
    internal async Task NotifySuitIconApplyAsync(bool success, string message)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try
        {
            var result = JsonSerializer.Serialize(new { success, message });
            await core.ExecuteScriptAsync($"window.characterIconStudio?.applyResult({result})");
        }
        catch (Exception ex) { Debug.WriteLine("Preview icon-apply acknowledgement: " + ex.Message); }
    }
    internal event Action<string>? VehicleWorkshopMessageReceived;
    internal event Action<ItemWorkshopTransform>? ItemWorkshopTransformChanged;
    internal async Task NotifyItemWorkshopPreviewFailedAsync(string message)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync($"window.itemWorkshopEditor?.previewFailed({JsonSerializer.Serialize(message)})"); }
        catch (Exception ex) { Debug.WriteLine("Item workshop preview error: " + ex.Message); }
    }
    internal async Task SetItemWorkshopEditingAsync(bool enabled)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync($"window.itemWorkshopEditor?.setEnabled({(enabled ? "true" : "false")})"); }
        catch (Exception ex) { Debug.WriteLine("Item workshop editing state: " + ex.Message); }
    }
    internal static bool IsVehicleWorkshopMessage(string? type) => type is "vehicleWorkshopSave" or "vehicleWorkshopReady" or "vehicleWorkshopError" or "vehicleWorkshopSettings" or "vehicleWorkshopCopyMaterial" or "vehicleWorkshopRiders" or "vehicleWorkshopToybox" or "vehicleWorkshopSurface" or "vehicleWorkshopLoadPart";
    internal async Task ShowVehicleRidersAsync(string json)
    {
        if (_web?.CoreWebView2 is { } core) await core.ExecuteScriptAsync("window.vehicleWorkshop?.loadRiders(" + json + ")");
    }
    internal async Task ShowVehicleWorkshopPartAsync(string component, VehicleWorkshopService.DeferredMesh mesh)
    {
        if (_web?.CoreWebView2 is { } core)
            await core.ExecuteScriptAsync("window.vehicleWorkshop?.loadPart(" + JsonSerializer.Serialize(component) + "," + JsonSerializer.Serialize(mesh) + ")");
    }
    internal async Task ShowVehicleWorkshopPartFailedAsync(string component, string error)
    {
        if (_web?.CoreWebView2 is { } core)
            await core.ExecuteScriptAsync("window.vehicleWorkshop?.loadPart(" + JsonSerializer.Serialize(component) + ",null," + JsonSerializer.Serialize(error) + ")");
    }
    internal async Task<bool> RequestVehicleSettingsAsync()
    {
        if (_web?.CoreWebView2 is not { } core) return false;
        return await core.ExecuteScriptAsync("Boolean(window.vehicleWorkshop?.ready && (vehicleWorkshop.openSettings(), true))") == "true";
    }

    public ModelPreviewControl()
    {
        BackColor = Theme.WindowBg;
        Controls.Add(_message);
        _message.BringToFront();
    }

    /// <summary>Status line shown in place of the render (loading, errors, empty state).</summary>
    public void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
        if (_web is not null)
        {
            _web.Visible = false;
        }
    }

    /// <summary>Points the viewer at a built preview folder (index.html + glb + textures).</summary>
    public async Task ShowFolderAsync(string folder)
    {
        _pendingFolder = folder;
        if (!_active)
        {
            return;
        }
        if (!_ready && !await InitAsync())
        {
            return;
        }
        if (!_active)
        {
            return;
        }
        Navigate(folder);
    }

    /// <summary>Releases the WebView renderer while the 3D tab is hidden.</summary>
    public void ReleaseRenderer()
    {
        var rendererVersion = Interlocked.Increment(ref _rendererVersion);
        _active = false;
        _ready = false;
        var browserProcessId = _browserProcessId;
        _browserProcessId = null;
        var userDataFolder = _userDataFolder;
        _userDataFolder = null;
        var web = _web;
        _web = null;
        if (web is not null)
        {
            Controls.Remove(web);
            web.Dispose();
        }
        StopBrowserProcess(browserProcessId);
        QueueUserDataCleanup(userDataFolder);

        if (!string.IsNullOrWhiteSpace(_pendingFolder))
        {
            ShowMessage("3D preview paused.");
        }

        QueueMemoryTrim(rendererVersion);
    }

    /// <summary>Recreates the renderer and reloads the last preview when the tab returns.</summary>
    public async Task ResumeRendererAsync()
    {
        Interlocked.Increment(ref _rendererVersion);
        _active = true;
        if (!string.IsNullOrWhiteSpace(_pendingFolder) && Directory.Exists(_pendingFolder))
        {
            ShowMessage("Reloading 3D preview…");
            await ShowFolderAsync(_pendingFolder);
        }
    }

    /// <summary>Runs a second cleanup after a build that finished off-tab.</summary>
    public void TrimInactiveMemory()
    {
        if (!_active)
        {
            QueueMemoryTrim(Volatile.Read(ref _rendererVersion));
        }
    }

    private void Navigate(string folder)
    {
        var web = _web;
        if (web is null || !_active)
        {
            return;
        }
        try
        {
            // A fresh host name per load. Reusing one host lets WebView2 serve the PREVIOUS
            // character's models.js and .glb from cache, which renders as a stuck camera inside
            // stale geometry. Cache-busting index.html alone does not help - the sub-resources are
            // fetched by plain relative name.
            _virtualHost = $"p{Guid.NewGuid():N}.batcomputer";
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                _virtualHost, folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.Navigate($"https://{_virtualHost}/index.html");
            _message.Visible = false;
            web.Visible = true;
            web.BringToFront();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not open the preview.\n\n" + ex.Message);
        }
    }

    internal static bool IsCharacterExportDownload(string uri, string host, string path) =>
        uri.StartsWith($"blob:https://{host}/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCharacterIconDownload(string uri, string host, string path) =>
        uri.StartsWith($"blob:https://{host}/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).StartsWith("Suit-icon-", StringComparison.Ordinal);

    internal static bool IsAnimationDraftDownload(string uri, string host, string path) =>
        uri.StartsWith($"blob:https://{host}/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).EndsWith(".animation-draft.json", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCharacterIconTemplateDownload(string uri, string host, string path) =>
        uri.StartsWith($"blob:https://{host}/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).EndsWith(".icon-studio.json", StringComparison.OrdinalIgnoreCase);

    internal static void ConfigureCharacterExportDownloads(WebView2 web, Control owner, Func<string> currentHost, Func<bool> isCurrent)
    {
        web.CoreWebView2.DownloadStarting += (_, download) =>
        {
            download.Cancel = true;
            download.Handled = true;
            var icon = IsCharacterIconDownload(download.DownloadOperation.Uri, currentHost(), download.ResultFilePath);
            var draft = IsAnimationDraftDownload(download.DownloadOperation.Uri, currentHost(), download.ResultFilePath);
            var template = IsCharacterIconTemplateDownload(download.DownloadOperation.Uri, currentHost(), download.ResultFilePath);
            if (!icon && !draft && !template && !IsCharacterExportDownload(download.DownloadOperation.Uri, currentHost(), download.ResultFilePath)) return;
            var suggestedName = icon || draft || template ? Path.GetFileName(download.ResultFilePath) : "Character-assembled.glb";
            var host = currentHost();
            var deferral = download.GetDeferral();
            try
            {
                owner.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (owner.IsDisposed || host != currentHost() || !isCurrent()) return;
                        using var dialog = new SaveFileDialog
                        {
                            Title = icon ? "Save suit icon" : template ? "Export icon studio settings" : draft ? "Save animation draft" : "Export assembled character",
                            Filter = icon ? "PNG image (*.png)|*.png" : template ? "Icon studio template (*.icon-studio.json)|*.icon-studio.json" : draft ? "Animation draft (*.json)|*.json" : "glTF binary (*.glb)|*.glb",
                            DefaultExt = icon ? "png" : draft || template ? "json" : "glb", AddExtension = true, OverwritePrompt = true,
                            FileName = suggestedName,
                        };
                        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                        if (!Path.GetExtension(dialog.FileName).Equals(icon ? ".png" : draft || template ? ".json" : ".glb", StringComparison.OrdinalIgnoreCase)) return;
                        download.ResultFilePath = dialog.FileName;
                        download.Cancel = false;
                    }
                    finally { deferral.Complete(); }
                }));
            }
            catch (InvalidOperationException) { deferral.Complete(); }
        };
    }

    private async Task<bool> InitAsync()
    {
        try
        {
            var web = _web ??= CreateWebView();
            // Own user-data folder per renderer. A process-level folder made the embedded viewer
            // and pop-out viewer share one browser tree, so releasing one could terminate the other.
            var userData = Path.Combine(AppSettings.RuntimeRoot, "WebView2",
                $"{Environment.ProcessId}-embedded-{Guid.NewGuid():N}");
            Directory.CreateDirectory(userData);
            _userDataFolder = userData;
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);
            await web.EnsureCoreWebView2Async(env);
            // Only the embedded workspace has a project-aware icon-test handler. Standalone
            // read-only preview windows use the same page but must not offer this action.
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.BATCOMPUTER_ICON_TEST_HOST=true;window.BATCOMPUTER_ANIMATION_LIBRARY_HOST=true;");
            var browserProcessId = web.CoreWebView2.BrowserProcessId;
            if (!_active || !ReferenceEquals(web, _web))
            {
                StopBrowserProcess(browserProcessId);
                QueueUserDataCleanup(userData);
                return false;
            }
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ConfigureCharacterExportDownloads(web, this, () => _virtualHost, () => ReferenceEquals(web, _web));
            web.CoreWebView2.WebMessageReceived += (_, message) =>
            {
                if (message.Source.StartsWith($"https://{_virtualHost}/", StringComparison.OrdinalIgnoreCase))
                    HandleWebMessage(message.WebMessageAsJson);
            };
            web.DefaultBackgroundColor = Theme.WindowBg;
            _browserProcessId = browserProcessId;
            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage("The 3D preview needs the Microsoft WebView2 Runtime, which could not start.\n\n"
                        + ex.Message);
            return false;
        }
    }

    private void HandleWebMessage(string json)
    {
        if (json.Length < 2_100_000 && IsHandleCreated && !IsDisposed)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    if (type.GetString() == "save-animation-draft")
                    {
                        var value = document.RootElement;
                        if (value.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.Object &&
                            value.TryGetProperty("layoutKey", out var layout) && layout.ValueKind == JsonValueKind.String)
                            _ = SaveAnimationDraftAsync(draft.GetRawText(), value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null, layout.GetString() ?? "");
                        return;
                    }
                    if (type.GetString() == "import-animation-source" && json.Length < 24_000)
                    {
                        var value = document.RootElement;
                        if (value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "pak" or "blender" or "library" &&
                            value.TryGetProperty("layoutKey", out var layout) && layout.ValueKind == JsonValueKind.String &&
                            value.TryGetProperty("rigSignature", out var rig) && rig.ValueKind == JsonValueKind.String)
                            _ = ImportAnimationSourceAsync(kind.GetString()!, layout.GetString() ?? "", rig.GetString() ?? "");
                        return;
                    }
                    if (type.GetString() == "item-workshop-transform")
                    {
                        var folder = _pendingFolder;
                        if (ItemWorkshopTransform.TryParse(json, out var change))
                            BeginInvoke(() => { if (!IsDisposed && folder == _pendingFolder) ItemWorkshopTransformChanged?.Invoke(change); });
                        return;
                    }
                    if (type.GetString() == "apply-character-icons")
                    {
                        try
                        {
                            var request = PreviewCharacterIconsRequestedEventArgs.Parse(json);
                            var folder = _pendingFolder;
                            BeginInvoke(() => { if (!IsDisposed && folder == _pendingFolder) CharacterIconsApplyRequested?.Invoke(this, request); });
                        }
                        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
                        { _ = NotifySuitIconApplyAsync(false, "Icon assignment rejected: " + ex.Message); }
                        return;
                    }
                    if (type.GetString() is "test-suit-icon" or "apply-suit-icon")
                    {
                        var applying = type.GetString() == "apply-suit-icon";
                        try
                        {
                            var root = document.RootElement;
                            var layout = root.GetProperty("layout").GetString() ?? "";
                            if (layout.Length is < 1 or > 160)
                                throw new InvalidDataException("The icon request did not identify a saved suit.");
                            var png = SuitIconDryRunService.DecodePngDataUrl(root.GetProperty("png").GetString() ?? "");
                            BeginInvoke(() =>
                            {
                                if (IsDisposed) return;
                                var request = new PreviewSuitIconTestRequestedEventArgs(layout, png);
                                if (applying) SuitIconApplyRequested?.Invoke(this, request);
                                else SuitIconTestRequested?.Invoke(this, request);
                            });
                        }
                        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
                        {
                            if (applying) _ = NotifySuitIconApplyAsync(false, "Suit icon assignment rejected: " + ex.Message);
                            else _ = NotifySuitIconTestAsync(false, "Suit icon test rejected: " + ex.Message);
                        }
                        return;
                    }
                    if (type.GetString() == "preview-character-animation" && json.Length < 2_000 &&
                        document.RootElement.TryGetProperty("package", out var package) && package.ValueKind == JsonValueKind.String)
                    {
                        var value = package.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) _ = SendCharacterAnimationAsync(value);
                        return;
                    }
                    if (json.Length < 256_000 && IsVehicleWorkshopMessage(type.GetString()))
                        BeginInvoke(() => { if (!IsDisposed) VehicleWorkshopMessageReceived?.Invoke(json); });
                }
            }
            catch (JsonException) { /* Ignore malformed viewer messages. */ }
        }
        if (PreviewPlacementSaveRequestedEventArgs.TryParse(json, out var args) &&
            IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(() => PlacementSaveRequested?.Invoke(this, args));
        }
    }

    private WebView2 CreateWebView()
    {
        var web = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        Controls.Add(web);
        web.SendToBack();
        return web;
    }

    private static void StopBrowserProcess(uint? processId)
    {
        if (processId is null || processId.Value > int.MaxValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // The browser already exited.
        }
        catch (InvalidOperationException)
        {
            // The browser exited while the control was being released.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows was already tearing down the isolated browser process.
        }
    }

    private static void QueueUserDataCleanup(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(500 + attempt * 500).ConfigureAwait(false);
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }

    private void QueueMemoryTrim(int rendererVersion)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(750).ConfigureAwait(false);
            if (_active || rendererVersion != Volatile.Read(ref _rendererVersion))
            {
                return;
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            try
            {
                using var process = Process.GetCurrentProcess();
                EmptyWorkingSet(process.Handle);
            }
            catch (InvalidOperationException)
            {
                // The application is shutting down.
            }
        });
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _active = false;
            Interlocked.Increment(ref _rendererVersion);
            var web = _web;
            _web = null;
            var browserProcessId = _browserProcessId;
            _browserProcessId = null;
            var userDataFolder = _userDataFolder;
            _userDataFolder = null;
            web?.Dispose();
            StopBrowserProcess(browserProcessId);
            QueueUserDataCleanup(userDataFolder);
        }
        base.Dispose(disposing);
    }
}

internal sealed class PreviewSuitIconTestRequestedEventArgs(string layoutKey, byte[] pngBytes) : EventArgs
{
    internal string LayoutKey { get; } = layoutKey;
    internal byte[] PngBytes { get; } = pngBytes;
}

internal sealed class PreviewCharacterIconsRequestedEventArgs(string layoutKey, IReadOnlyDictionary<string, byte[]> icons) : EventArgs
{
    internal string LayoutKey { get; } = layoutKey;
    internal IReadOnlyDictionary<string, byte[]> Icons { get; } = icons;
    internal static PreviewCharacterIconsRequestedEventArgs Parse(string json)
    {
        if (json.Length > 6_100_000) throw new InvalidDataException("The icon request is too large.");
        using var doc = JsonDocument.Parse(json);
        var layout = doc.RootElement.GetProperty("layout").GetString() ?? "";
        if (layout.Length is < 1 or > 160) throw new InvalidDataException("The icons must identify a saved suit.");
        var images = doc.RootElement.GetProperty("icons");
        if (images.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid icon set.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var image in images.EnumerateObject())
            if (!result.TryAdd(image.Name, SuitIconDryRunService.DecodePngDataUrl(image.Value.GetString() ?? "", CharacterIconAssignmentService.Size(image.Name))))
                throw new InvalidDataException("Duplicate icon role.");
        CharacterIconAssignmentService.Validate(result);
        return new(layout, result);
    }
}

public sealed record PreviewCustomMeshTransform(
    float Scale,
    float OffsetX,
    float OffsetY,
    float OffsetZ,
    float RotationPitch,
    float RotationYaw,
    float RotationRoll);

public sealed class PreviewPlacementSaveRequestedEventArgs : EventArgs
{
    public PreviewPlacementSaveRequestedEventArgs(
        string layoutKey,
        string component,
        float offsetX,
        float offsetY,
        float offsetZ,
        int? uvChannel,
        float? scaleMultiplier,
        string? customMeshId = null,
        PreviewCustomMeshTransform? customMeshTransform = null,
        bool customMeshBakeRequested = false)
    {
        LayoutKey = layoutKey;
        Component = component;
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        UvChannel = uvChannel;
        ScaleMultiplier = scaleMultiplier;
        CustomMeshId = customMeshId;
        CustomMeshTransform = customMeshTransform;
        CustomMeshBakeRequested = customMeshBakeRequested;
    }

    public string LayoutKey { get; }
    public string Component { get; }
    public float OffsetX { get; }
    public float OffsetY { get; }
    public float OffsetZ { get; }
    public int? UvChannel { get; }
    public float? ScaleMultiplier { get; }
    public string? CustomMeshId { get; }
    public PreviewCustomMeshTransform? CustomMeshTransform { get; }
    public bool CustomMeshBakeRequested { get; }

    public static bool TryParse(string json, out PreviewPlacementSaveRequestedEventArgs args)
    {
        args = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !root.TryGetProperty("layout", out var layoutProperty) ||
                string.IsNullOrWhiteSpace(layoutProperty.GetString()) ||
                !root.TryGetProperty("component", out var componentProperty) ||
                string.IsNullOrWhiteSpace(componentProperty.GetString()))
            {
                return false;
            }

            var messageType = type.GetString();
            if (string.Equals(messageType, "save-custom-mesh", StringComparison.Ordinal) ||
                string.Equals(messageType, "save-custom-mesh-draft", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("customId", out var customIdProperty) ||
                    string.IsNullOrWhiteSpace(customIdProperty.GetString()) ||
                    !root.TryGetProperty("transform", out var transform) ||
                    transform.ValueKind != JsonValueKind.Object ||
                    !transform.TryGetProperty("scale", out var transformScaleProperty) ||
                    !transformScaleProperty.TryGetSingle(out var scale) ||
                    !float.IsFinite(scale) || scale is < 1f or > 1000f ||
                    !TryReadTriple(transform, "offset", 100000f, out var customOffset) ||
                    !TryReadTriple(transform, "rotation", 360f, out var rotation))
                {
                    return false;
                }

                var customTransform = new PreviewCustomMeshTransform(
                    scale, customOffset[0], customOffset[1], customOffset[2], rotation[0], rotation[1], rotation[2]);
                args = new PreviewPlacementSaveRequestedEventArgs(
                    layoutProperty.GetString()!, componentProperty.GetString()!,
                    customOffset[0], customOffset[1], customOffset[2], null, null,
                    customIdProperty.GetString(), customTransform,
                    customMeshBakeRequested: string.Equals(messageType, "save-custom-mesh", StringComparison.Ordinal));
                return true;
            }

            if (!string.Equals(type.GetString(), "save-placement", StringComparison.Ordinal) ||
                !root.TryGetProperty("offset", out var offset) ||
                offset.ValueKind != JsonValueKind.Array ||
                offset.GetArrayLength() != 3)
            {
                return false;
            }

            var values = offset.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Any(value => !float.IsFinite(value)))
            {
                return false;
            }

            int? uvChannel = null;
            if (root.TryGetProperty("uv", out var uvProperty) && uvProperty.ValueKind != JsonValueKind.Null)
            {
                if (uvProperty.ValueKind != JsonValueKind.Number || !uvProperty.TryGetInt32(out var value) ||
                    value < 0 || value > 7)
                {
                    return false;
                }
                uvChannel = value;
            }

            float? scaleMultiplier = null;
            if (root.TryGetProperty("scale", out var scaleProperty) && scaleProperty.ValueKind != JsonValueKind.Null)
            {
                if (scaleProperty.ValueKind != JsonValueKind.Number || !scaleProperty.TryGetSingle(out var value) ||
                    !float.IsFinite(value) || value is < 0.01f or > 100f)
                {
                    return false;
                }
                scaleMultiplier = value;
            }

            args = new PreviewPlacementSaveRequestedEventArgs(
                layoutProperty.GetString()!, componentProperty.GetString()!, values[0], values[1], values[2], uvChannel, scaleMultiplier);
            return true;
        }
        catch
        {
            // A malformed page message must never break the embedded preview.
            return false;
        }
    }

    private static bool TryReadTriple(JsonElement root, string propertyName, float limit, out float[] values)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array || property.GetArrayLength() != 3)
        {
            return false;
        }
        values = property.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        return values.All(value => float.IsFinite(value) && MathF.Abs(value) <= limit);
    }
}
