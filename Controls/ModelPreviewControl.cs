using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// The 3D character preview as an embeddable control, so it can live inside a tab rather than only
/// in a pop-out window. Same WebView2 + three.js page the pop-out uses - this just owns the host.
/// </summary>
public sealed class ModelPreviewControl : UserControl
{
    private string _virtualHost = "preview.batcomputer";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
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

    /// <summary>Raised when the in-viewer part mover asks the host to persist an alignment.</summary>
    public event EventHandler<PreviewPlacementSaveRequestedEventArgs>? PlacementSaveRequested;

    public ModelPreviewControl()
    {
        BackColor = Theme.WindowBg;
        Controls.Add(_web);
        Controls.Add(_message);
        _message.BringToFront();
    }

    /// <summary>Status line shown in place of the render (loading, errors, empty state).</summary>
    public void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
        _web.Visible = false;
    }

    /// <summary>Points the viewer at a built preview folder (index.html + glb + textures).</summary>
    public async Task ShowFolderAsync(string folder)
    {
        _pendingFolder = folder;
        if (!_ready && !await InitAsync())
        {
            return;
        }
        Navigate(folder);
    }

    private void Navigate(string folder)
    {
        try
        {
            // A fresh host name per load. Reusing one host lets WebView2 serve the PREVIOUS
            // character's models.js and .glb from cache, which renders as a stuck camera inside
            // stale geometry. Cache-busting index.html alone does not help - the sub-resources are
            // fetched by plain relative name.
            _virtualHost = $"p{Guid.NewGuid():N}.batcomputer";
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                _virtualHost, folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Navigate($"https://{_virtualHost}/index.html");
            _message.Visible = false;
            _web.Visible = true;
            _web.BringToFront();
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
            // Own user-data folder per process: the default one is derived from the exe path, so a
            // second instance (or a leftover msedgewebview2 child) locks it and startup fails with
            // 0x800700AA "resource in use".
            var userData = Path.Combine(AppSettings.RuntimeRoot, "WebView2",
                Environment.ProcessId.ToString());
            Directory.CreateDirectory(userData);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.WebMessageReceived += (_, message) =>
                HandleWebMessage(message.WebMessageAsJson);
            _web.DefaultBackgroundColor = Theme.WindowBg;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _web.Dispose();
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
