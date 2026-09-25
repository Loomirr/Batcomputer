using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

internal static class AppUpdateRegressionChecks
{
    internal static int Run(string outputDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(outputDirectory), "updater-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "results.txt")) { AutoFlush = true };
        var failed = 0;
        void Check(string name, Action test)
        {
            try { test(); log.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; log.WriteLine("FAIL " + name + ": " + ex); }
        }
        void Reject(Action test)
        {
            try { test(); } catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException) { return; }
            throw new Exception("Expected rejection.");
        }
        Check("semantic versions: beta 10 > beta 2; stable > beta; legacy tags; no downgrade", () =>
        {
            if (AppVersion.Compare("v1.0.0-beta.10", "1.0.0-beta.2") <= 0 || AppVersion.Compare("1.0.0", "1.0.0-beta.10") <= 0
                || AppVersion.Compare("v1.0-Beta", "1.0.0-beta") != 0 || AppVersion.Compare("1.0.0", "2.0.0") >= 0) throw new Exception("Ordering failed.");
        });
        Check("pre-1.0 releases are legacy; download rejects them before network or staging", () =>
        {
            foreach (var version in new[] { "0.9.0", "v0.9.0-beta.11", "0.99.99" })
            {
                if (AppUpdateService.IsSupportedRelease(version)) throw new Exception("Legacy release accepted.");
                using var service = new AppUpdateService();
                var transaction = Path.Combine(root, "legacy-" + version.Replace('.', '_'));
                Reject(() => service.DownloadAsync(new(version, "", new Uri("https://example.invalid/never-requested"), 1, new string('a', 64)), transaction, null, CancellationToken.None).GetAwaiter().GetResult());
                if (Directory.Exists(transaction)) throw new Exception("Legacy download created staging.");
            }
            foreach (var version in new[] { "v1.0-Beta", "1.0.0-beta.1", "1.0.0", "2.0.0-alpha" })
                if (!AppUpdateService.IsSupportedRelease(version)) throw new Exception("Supported release rejected.");
        });
        Check("reject paths escaping app files, state, ADS, device names and Windows aliases", () =>
        {
            foreach (var path in new[] { "../Batcomputer.exe", "C:/oops.dll", "Tools//a", "Tools/../a", "Tools/a:stream", "Tools/CON.txt", "Tools/a. ", "Data/rig.json", "Generated/project.json", "Runtime/foo.dll", "Batcomputer.settings.json", "Tools\\foo", "app/../Data/project.json", "app/Batcomputer.settings.json", "app/Generated/project.json" })
                Reject(() => UpdatePaths.ValidateRelative(path));
        });
        Check("manifest duplicates are case insensitive", () => Reject(() => AppUpdateService.ValidateManifest(new(1, "1.0.0", new()
        { new("Batcomputer.exe", 0, new string('a', 64)), new("batcomputer.EXE", 0, new string('a', 64)) }))));
        Check("live activity changes from pending to confirmed without reopening, rollback overrides old health", () =>
        {
            var activityRoot = Path.Combine(root, "activity");
            var tx = AppUpdateInstaller.NewTransaction(activityRoot);
            File.WriteAllText(Path.Combine(tx, "status.txt"), "Application files installed. Waiting for startup confirmation.");
            if (AppUpdateActivity.ReadLatest(activityRoot).StartupConfirmed) throw new Exception("Premature success.");
            File.WriteAllText(Path.Combine(tx, "healthy.txt"), AppVersion.Current);
            File.WriteAllText(Path.Combine(tx, "status.txt"), "Installed " + AppVersion.Current + ". Startup confirmed. Backup retained.");
            if (!AppUpdateActivity.ReadLatest(activityRoot).StartupConfirmed) throw new Exception("Stale pending status.");
            File.WriteAllText(Path.Combine(tx, "status.txt"), "Rolled back. Your settings and projects were not changed.");
            if (AppUpdateActivity.ReadLatest(activityRoot).StartupConfirmed) throw new Exception("Historical health overrides rollback.");
        });

        string Fixture()
        {
            var install = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "Batcomputer.exe"), "old binary");
            File.WriteAllText(Path.Combine(install, "Batcomputer.settings.json"), "settings sentinel");
            Directory.CreateDirectory(Path.Combine(install, "Generated"));
            File.WriteAllText(Path.Combine(install, "Generated", "project.json"), "project sentinel");
            var transaction = AppUpdateInstaller.NewTransaction(install);
            var payload = Path.Combine(transaction, "payload"); Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "Batcomputer.exe"), "new binary");
            File.WriteAllText(Path.Combine(payload, "new-library.dll"), "new library");
            var files = Directory.GetFiles(payload).Select(p => new UpdateFile(Path.GetFileName(p), new FileInfo(p).Length, AppUpdateService.Hash(p))).ToList();
            File.WriteAllText(Path.Combine(transaction, AppUpdateService.ManifestName), JsonSerializer.Serialize(new UpdateManifest(1, "99.0.0", files)));
            return transaction;
        }
        Check("install, retained backup, rollback, rollback twice, state untouched", () =>
        {
            var tx = Fixture(); var install = AppUpdateInstaller.InstallRoot(tx);
            AppUpdateInstaller.ApplyFiles(tx);
            if (File.ReadAllText(Path.Combine(install, "Batcomputer.exe")) != "new binary") throw new Exception("Not installed.");
            AppUpdateInstaller.Rollback(tx); AppUpdateInstaller.Rollback(tx);
            if (File.ReadAllText(Path.Combine(install, "Batcomputer.exe")) != "old binary" || File.Exists(Path.Combine(install, "new-library.dll"))) throw new Exception("Not restored.");
            if (File.ReadAllText(Path.Combine(install, "Batcomputer.settings.json")) != "settings sentinel"
                || File.ReadAllText(Path.Combine(install, "Generated", "project.json")) != "project sentinel") throw new Exception("User data changed.");
        });
        Check("organized migration archives recognized legacy files, preserves unknown DLLs, and rolls back", () =>
        {
            foreach (bool interrupt in new[] { false, true })
            {
                var tx = Fixture(); var install = AppUpdateInstaller.InstallRoot(tx); var payload = Path.Combine(tx, "payload");
                Directory.CreateDirectory(Path.Combine(payload, "app"));
                foreach (var pair in new[] { ("Batcomputer.dll", "old app", "new app"), ("shared.dll", "same library", "same library"), ("custom.dll", "user modified", "new library") })
                { File.WriteAllText(Path.Combine(install, pair.Item1), pair.Item2); File.WriteAllText(Path.Combine(payload, "app", pair.Item1), pair.Item3); }
                var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Select(p => new UpdateFile(Path.GetRelativePath(payload,p).Replace('\\','/'),new FileInfo(p).Length,AppUpdateService.Hash(p))).ToList();
                File.WriteAllText(Path.Combine(tx, AppUpdateService.ManifestName),JsonSerializer.Serialize(new UpdateManifest(1,"99.0.0",files)));
                if (interrupt) Reject(() => AppUpdateInstaller.ApplyFiles(tx, count => { if (count == files.Count + 1) throw new IOException("Interrupted after first legacy removal"); }));
                else
                {
                    AppUpdateInstaller.ApplyFiles(tx);
                    if (File.Exists(Path.Combine(install,"Batcomputer.dll")) || File.Exists(Path.Combine(install,"shared.dll"))) throw new Exception("Legacy file not archived.");
                    if (File.ReadAllText(Path.Combine(install,"custom.dll")) != "user modified") throw new Exception("Unknown/modified DLL changed.");
                    AppUpdateInstaller.Rollback(tx);
                }
                if (File.ReadAllText(Path.Combine(install,"Batcomputer.dll")) != "old app" || File.ReadAllText(Path.Combine(install,"shared.dll")) != "same library") throw new Exception("Migration rollback did not restore originals.");
                if (File.Exists(Path.Combine(install,"app","Batcomputer.dll"))) throw new Exception("New-layout binary survived rollback.");
            }
        });
        Check("completion prompt requires successful startup and matching version; shown once", () =>
        {
            var tx = Fixture(); var install = AppUpdateInstaller.InstallRoot(tx); var token = Path.GetFileName(tx);
            if (AppUpdateCompletion.Claim(install,token,"99.0.0") != null) throw new Exception("Premature prompt.");
            AppUpdateInstaller.ApplyFiles(tx);
            if (AppUpdateCompletion.Claim(install,token,"99.0.0") != null) throw new Exception("Prompt before health confirmation.");
            File.WriteAllText(Path.Combine(tx,"healthy.txt"),"99.0.0");
            if (AppUpdateCompletion.Claim(install,token,"98.0.0") != null || AppUpdateCompletion.Claim(install,"../bad","99.0.0") != null) throw new Exception("Invalid success claim.");
            if (AppUpdateCompletion.Claim(install,token,"99.0.0") is not { } message || !message.Contains("v99.0.0")) throw new Exception("Missing installed version.");
            if (AppUpdateCompletion.Claim(install,token,"99.0.0") != null) throw new Exception("Repeated prompt.");
        });
        Check("mid-install failure restores old files and removes only new app files", () =>
        {
            var tx = Fixture(); var install = AppUpdateInstaller.InstallRoot(tx);
            Reject(() => AppUpdateInstaller.ApplyFiles(tx, n => { if (n == 2) throw new IOException("Injected interruption"); }));
            if (File.ReadAllText(Path.Combine(install, "Batcomputer.exe")) != "old binary" || File.Exists(Path.Combine(install, "new-library.dll"))) throw new Exception("Rollback failed.");
        });
        Check("corrupt staged file rejected before changing install", () =>
        {
            var tx = Fixture(); File.WriteAllText(Path.Combine(tx, "payload", "new-library.dll"), "corrupt");
            Reject(() => AppUpdateInstaller.ApplyFiles(tx));
            if (File.ReadAllText(Path.Combine(AppUpdateInstaller.InstallRoot(tx), "Batcomputer.exe")) != "old binary") throw new Exception("Changed install.");
        });
        Check("locked binary prevents partial install", () =>
        {
            var tx = Fixture(); var install = AppUpdateInstaller.InstallRoot(tx);
            using var locked = new FileStream(Path.Combine(install, "Batcomputer.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Reject(() => AppUpdateInstaller.ApplyFiles(tx));
            if (File.ReadAllText(Path.Combine(install, "Batcomputer.exe")) != "old binary") throw new Exception("Changed locked file.");
        });
        Check("manual rollback preserves user-modified app files", () =>
        {
            var tx = Fixture(); AppUpdateInstaller.ApplyFiles(tx);
            File.WriteAllText(Path.Combine(AppUpdateInstaller.InstallRoot(tx), "new-library.dll"), "subsequent user change");
            Reject(() => AppUpdateInstaller.Rollback(tx));
        });
        Check("shared app lock blocks installer until all instances exit", () =>
        {
            var dir = AppUpdateInstaller.InstallRoot(Fixture());
            using (var a = AppUpdateInstaller.AcquireAppLock(dir))
            using (var b = AppUpdateInstaller.AcquireAppLock(dir))
                Reject(() => { using var exclusive = new FileStream(Path.Combine(dir, AppUpdateInstaller.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            using var after = new FileStream(Path.Combine(dir, AppUpdateInstaller.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        // Exercise real HTTP, redirects policy, package creation and extraction with the current real executable.
        var publish = Path.Combine(root, "publish"); Directory.CreateDirectory(publish);
        File.Copy(Environment.ProcessPath!, Path.Combine(publish, "Batcomputer.exe"));
        var package = AppUpdatePackageService.Create(publish, Path.Combine(root, "package"));
        Check("public packages reject proprietary runtimes and misplaced writer binaries", () =>
        {
            var tools = Path.Combine(publish,"Tools","review"); Directory.CreateDirectory(tools);
            foreach (var name in new[] { "oo2core_9_win64.dll", "oodle-data-shared.dll", "UnrealEditor-Core.dll", "UnrealEditor-BatcomputerRegistryWriter.dll" })
            {
                var forbidden = Path.Combine(tools,name); File.WriteAllText(forbidden,"prohibited fixture");
                Reject(() => AppUpdatePackageService.Create(publish,Path.Combine(root,Guid.NewGuid().ToString("N"))));
                File.Delete(forbidden);
            }
        });
        Check("authored prebuilt writer remains packageable only at its expected tool path", () =>
        {
            var writer = Path.Combine(publish,"Tools","BatcomputerRegistryWriter","Prebuilt","Win64","UnrealEditor-BatcomputerRegistryWriter.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(writer)!); File.WriteAllText(writer,"authored fixture module");
            try { AppUpdatePackageService.Create(publish,Path.Combine(root,Guid.NewGuid().ToString("N"))); }
            finally { File.Delete(writer); }
        });
        Check("retoc receives only the configured existing local runtime, never a stale inherited override", () =>
        {
            var local = Path.Combine(root,"runtime-settings"); Directory.CreateDirectory(local);
            var settings = new AppSettings { UnrealEngineRoot = local };
            var start = new System.Diagnostics.ProcessStartInfo("fixture");
            start.Environment[RetocRuntime.Variable] = "stale.dll";
            RetocRuntime.Configure(start,settings);
            if(start.Environment.ContainsKey(RetocRuntime.Variable)) throw new Exception("Stale runtime inherited");
            var selected = Path.Combine(local,"selected.dll"); File.WriteAllText(selected,"fixture only");
            settings.OodleRuntimeDllPath = selected; RetocRuntime.Configure(start,settings);
            if(start.Environment[RetocRuntime.Variable] != Path.GetFullPath(selected)) throw new Exception("Local runtime not passed");
        });
        var zipBytes = File.ReadAllBytes(package);
        using var server = new LocalServer();
        server.Routes["/update.zip"] = zipBytes;
        var asset = new { name = AppUpdateService.AssetName, size = zipBytes.Length, digest = "sha256:" + AppUpdateService.Hash(package), browser_download_url = server.Url + "update.zip" };
        object Release(string version, bool prerelease, bool draft = false) => new { tag_name = version, prerelease, draft, body = "Local regression fixture", assets = new[] { asset } };
        server.Routes["/releases.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { Release("99.0.0-beta.2", true), Release("99.0.0-beta.10", true), Release("98.0.0", false), Release("100.0.0", false, true) });
        Check("real local HTTP feed: beta/stable/draft ordering", () =>
        {
            using var service = new AppUpdateService(new Uri(server.Url + "releases.json"));
            var beta = service.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult();
            var stable = service.CheckAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            if (beta?.Version != "99.0.0-beta.10" || stable?.Version != "98.0.0") throw new Exception("Wrong release selected.");
        });
        Check("real HTTP ZIP download, digest, manifest, executable version and extraction", () =>
        {
            using var service = new AppUpdateService(new Uri(server.Url + "releases.json"));
            var release = new AppUpdateRelease(AppVersion.Current, "", new Uri(server.Url + "update.zip"), zipBytes.Length, AppUpdateService.Hash(package));
            var staged = service.DownloadAsync(release, AppUpdateInstaller.NewTransaction(Path.Combine(root, "download")), null, CancellationToken.None).GetAwaiter().GetResult();
            if (staged.Manifest.Files.Count != 1) throw new Exception("Unexpected manifest.");
            Reject(() => service.DownloadAsync(release with { Sha256 = new string('0', 64) }, AppUpdateInstaller.NewTransaction(Path.Combine(root, "bad-hash")), null, CancellationToken.None).GetAwaiter().GetResult());
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject(() => service.CheckAsync(true, cancel.Token).GetAwaiter().GetResult());
            server.Routes["/truncated.zip"] = zipBytes[..(zipBytes.Length / 2)];
            Reject(() => service.DownloadAsync(release with { Download = new Uri(server.Url + "truncated.zip") }, AppUpdateInstaller.NewTransaction(Path.Combine(root, "truncated")), null, CancellationToken.None).GetAwaiter().GetResult());
            Reject(() => service.DownloadAsync(release with { Version = "98.0.0" }, AppUpdateInstaller.NewTransaction(Path.Combine(root, "wrong-version")), null, CancellationToken.None).GetAwaiter().GetResult());
        });
        Check("test feed cannot redirect downloads to remote hosts; missing digests fail closed", () =>
        {
            using var service = new AppUpdateService(new Uri(server.Url + "releases.json"));
            foreach (var badAsset in new[] {
                new { name = AppUpdateService.AssetName, size = 100, digest = "sha256:" + new string('a', 64), browser_download_url = "https://example.com/update.zip" },
                new { name = AppUpdateService.AssetName, size = 100, digest = "", browser_download_url = server.Url + "update.zip" } })
            {
                server.Routes["/releases.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "99.0.0", prerelease = false, draft = false, assets = new[] { badAsset } } });
                Reject(() => service.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult());
            }
        });
        Check("low disk budget is rejected before extraction or install", () =>
        {
            var available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
            Reject(() => AppUpdateService.CheckSpace(root, available + 1));
        });
        Check("reject ZIP traversal and duplicate paths before extraction", () =>
        {
            foreach (var name in new[] { "../Batcomputer.exe", "Batcomputer.exe" })
            {
                var bad = Path.Combine(root, Guid.NewGuid() + ".zip");
                using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
                { zip.CreateEntry(name); zip.CreateEntry(name); }
                Reject(() => AppUpdateService.Stage(bad, Path.Combine(root, Guid.NewGuid().ToString()), AppVersion.Current, CancellationToken.None));
            }
        });
        AppUpdateFileRegressionChecks.Run(root, Check, Reject);
        log.WriteLine($"RESULT: {failed} failures. Fixtures retained at {root}");
        return failed == 0 ? 0 : 1;
    }

    internal sealed class LocalServer : IDisposable
    {
        internal readonly Dictionary<string, byte[]> Routes = new();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        internal string Url { get; }
        internal LocalServer()
        {
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                        var request = await reader.ReadLineAsync(_stop.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        var key = request?.Split(' ').ElementAtOrDefault(1) ?? "";
                        Requests.Enqueue(key);
                        var found = Routes.TryGetValue(key, out var data); data ??= Array.Empty<byte>();
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(data, _stop.Token);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
            });
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); } catch { } _stop.Dispose(); }
    }
}
