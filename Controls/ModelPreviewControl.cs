using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// The 3D character preview as an embeddable control, so it can live inside a tab rather than only
/// in a pop-out window. Same WebView2 + three.js page the pop-out uses - this just owns the host.
/// </summary>
public sealed class ModelPreviewControl : UserControl
{
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

    /// <summary>Raised when the in-viewer part mover asks the host to persist an alignment.</summary>
    public event EventHandler<PreviewPlacementSaveRequestedEventArgs>? PlacementSaveRequested;

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
        _active = false;
        _ready = false;
        var browserProcessId = _browserProcessId;
        _browserProcessId = null;
        var web = _web;
        _web = null;
        if (web is not null)
        {
            Controls.Remove(web);
            web.Dispose();
        }
        StopBrowserProcess(browserProcessId);

        if (!string.IsNullOrWhiteSpace(_pendingFolder))
        {
            ShowMessage("3D preview paused.");
        }
    }

    /// <summary>Recreates the renderer and reloads the last preview when the tab returns.</summary>
    public async Task ResumeRendererAsync()
    {
        _active = true;
        if (!string.IsNullOrWhiteSpace(_pendingFolder) && Directory.Exists(_pendingFolder))
        {
            ShowMessage("Reloading 3D preview...");
            await ShowFolderAsync(_pendingFolder);
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

    private async Task<bool> InitAsync()
    {
        try
        {
            var web = _web ??= CreateWebView();
            // Own user-data folder per process: the default one is derived from the exe path, so a
            // second instance (or a leftover msedgewebview2 child) locks it and startup fails with
            // 0x800700AA "resource in use".
            var userData = Path.Combine(AppSettings.RuntimeRoot, "WebView2",
                Environment.ProcessId.ToString());
            Directory.CreateDirectory(userData);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);
            await web.EnsureCoreWebView2Async(env);
            var browserProcessId = web.CoreWebView2.BrowserProcessId;
            if (!_active || !ReferenceEquals(web, _web))
            {
                StopBrowserProcess(browserProcessId);
                return false;
            }
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.WebMessageReceived += (_, message) =>
                HandleWebMessage(message.WebMessageAsJson);
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
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _web?.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class PreviewPlacementSaveRequestedEventArgs : EventArgs
{
    public PreviewPlacementSaveRequestedEventArgs(
        string layoutKey,
        string component,
        float offsetX,
        float offsetY,
        float offsetZ,
        int? uvChannel)
    {
        LayoutKey = layoutKey;
        Component = component;
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        UvChannel = uvChannel;
    }

    public string LayoutKey { get; }
    public string Component { get; }
    public float OffsetX { get; }
    public float OffsetY { get; }
    public float OffsetZ { get; }
    public int? UvChannel { get; }

    public static bool TryParse(string json, out PreviewPlacementSaveRequestedEventArgs args)
    {
        args = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "save-placement", StringComparison.Ordinal) ||
                !root.TryGetProperty("layout", out var layoutProperty) ||
                string.IsNullOrWhiteSpace(layoutProperty.GetString()) ||
                !root.TryGetProperty("component", out var componentProperty) ||
                string.IsNullOrWhiteSpace(componentProperty.GetString()) ||
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

            args = new PreviewPlacementSaveRequestedEventArgs(
                layoutProperty.GetString()!, componentProperty.GetString()!, values[0], values[1], values[2], uvChannel);
            return true;
        }
        catch
        {
            // A malformed page message must never break the embedded preview.
            return false;
        }
    }
}
