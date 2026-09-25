using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Batcomputer;

/// <summary>Offline, presentation-only update screen. Downloads/install decisions remain native.</summary>
internal sealed class UpdateCenterView : UserControl
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _host = $"update{Guid.NewGuid():N}.batcomputer";
    private readonly string _folder = Path.Combine(AppSettings.RuntimeRoot, "UpdaterView", Guid.NewGuid().ToString("N"));
    private bool _ready;
    private string? _lastState;
    internal event Action? Ready;
    internal event Action<string, bool>? Command;
    internal event Action? Failed;

    internal UpdateCenterView()
    {
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(25, 28, 35);
        _web.DefaultBackgroundColor = BackColor; Controls.Add(_web);
    }

    internal async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            foreach (var file in new[] { "index.html", "updater.css", "updater.js", "Batcomputer.glb", "Batcomputer-poster.png", "three.min.js", "GLTFLoader.js" })
            {
                var source = file is "three.min.js" or "GLTFLoader.js" ? "preview/" : "updater/";
                File.WriteAllBytes(Path.Combine(_folder, file), EmbeddedAssets.ReadBytes(source + file) ?? throw new FileNotFoundException(file));
            }
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_folder, "browser"));
            if (IsDisposed) return;
            await _web.EnsureCoreWebView2Async(env);
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(_host, _folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => e.Cancel = !IsLocal(e.Uri);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!IsLocal(e.Request.Uri) && !e.Request.Uri.StartsWith("blob:https://" + _host + "/", StringComparison.Ordinal))
                    e.Response = env.CreateWebResourceResponse(null, 403, "Offline view", "");
            };
            core.ProcessFailed += (_, _) => { if (!IsDisposed) { _ready = false; Failed?.Invoke(); } };
            core.WebMessageReceived += (_, e) =>
            {
                if (!IsLocal(e.Source) || IsDisposed) return;
                try
                {
                    using var json = JsonDocument.Parse(e.WebMessageAsJson);
                    if (!json.RootElement.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String) return;
                    var name = action.GetString()!;
                    if (name == "ready") { _ready = true; Ready?.Invoke(); return; }
                    if (name is not ("primary" or "restart" or "cancel" or "channel" or "startup" or "releases" or "recovery")) return;
                    bool value = json.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
                    Command?.Invoke(name, value);
                }
                catch (JsonException) { }
            };
            core.Navigate($"https://{_host}/index.html");
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!_ready && !IsDisposed) Failed?.Invoke();
        }
        catch { if (!IsDisposed) Failed?.Invoke(); }
    }

    private bool IsLocal(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == _host && uri.IsDefaultPort;

    internal void Present(object state)
    {
        if (!_ready || IsDisposed) return;
        var json = JsonSerializer.Serialize(state);
        if (json == _lastState) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(json); _lastState = json; }
        catch (InvalidOperationException) { _ready = false; Failed?.Invoke(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ready = false; _web.Dispose();
            // Only this instance's generated resources/profile, never shared viewer data.
            var folder = _folder;
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(1000 + i * 1000).ConfigureAwait(false);
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            });
        }
        base.Dispose(disposing);
    }
}
