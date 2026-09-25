using System.Diagnostics;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Explicit, disposable-only CLI acceptance. Never opens or controls application windows.</summary>
internal static class AppUpdateFixtureCheck
{
    internal static int VerifyHelper()
    {
        var root = AppSettings.ToolRoot;
        if (!File.Exists(Path.Combine(root, AppUpdateInstaller.SandboxMarker))) return 1;
        var report = Path.Combine(root, "updater-helper-result.txt");
        try
        {
            var transaction = AppUpdateInstaller.NewTransaction(root);
            var executable = AppUpdateInstaller.PrepareHelper(transaction);
            var helper = Path.GetDirectoryName(executable)!;
            File.Copy(Path.Combine(root, AppUpdateInstaller.SandboxMarker), Path.Combine(helper, AppUpdateInstaller.SandboxMarker));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helper };
            start.ArgumentList.Add("--updater-test-runtime");
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            using var child = Process.Start(start) ?? throw new IOException("Helper did not start.");
            if (!child.WaitForExit(30000) || child.ExitCode != 0) throw new IOException("Helper runtime check failed.");
            if (File.ReadAllText(Path.Combine(helper, "updater-runtime-version.txt")) != AppVersion.Current) throw new InvalidDataException("Helper version/root mismatch.");
            File.WriteAllText(report, "PASS private helper copied and executed with its own bundled runtime and correct root. No UI or installation.\n" + helper);
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, "FAIL " + ex); return 1; }
    }
    internal static int Run(string install, string? feed, bool fullZip = false)
    {
        install = Path.GetFullPath(install);
        var report = Path.Combine(install,"updater-fixture-result.txt");
        try
        {
            UpdatePaths.RejectLinks(install);
            var marker = feed == null ? AppUpdateInstaller.GitHubTestMarker : AppUpdateInstaller.SandboxMarker;
            if (!File.Exists(Path.Combine(install,marker)) || install.Equals(AppSettings.ToolRoot,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use an idle disposable fixture, not the running app folder.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var service = new AppUpdateService(feed == null ? null : new Uri(feed),install,preferFileUpdates:!fullZip);
            var original = AppUpdateService.ProductVersion(Path.Combine(install,"Batcomputer.exe"));
            var settings = Path.Combine(install,"Batcomputer.settings.json");
            var sentinel = Path.Combine(install,"Generated","KEEP-ME.txt");
            var settingsHash = AppUpdateService.Hash(settings);var sentinelHash=AppUpdateService.Hash(sentinel);
            var selected = service.CheckAsync(true,timeout.Token).GetAwaiter().GetResult() ?? throw new InvalidOperationException("No update.");
            if (feed == null)
            {
                var stable = service.CheckAsync(false,timeout.Token).GetAwaiter().GetResult();
                if (stable != null && AppVersion.IsPrerelease(stable.Version)) throw new InvalidDataException("Stable channel offered a prerelease.");
            }
            var staged = service.DownloadAsync(selected,AppUpdateInstaller.NewTransaction(install),null,timeout.Token).GetAwaiter().GetResult();
            using(var exclusive=new FileStream(Path.Combine(install,AppUpdateInstaller.LockFile),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))AppUpdateInstaller.Apply(staged.Directory);
            foreach(var file in staged.Manifest.Files)AppUpdateService.VerifyFile(UpdatePaths.Resolve(install,file.Path),file);
            var start=new ProcessStartInfo(Path.Combine(install,"Batcomputer.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=install};start.ArgumentList.Add("--updater-test-runtime");
            using var child=Process.Start(start)??throw new IOException("Fixture did not start.");
            if(!child.WaitForExit(30000)||child.ExitCode!=0)throw new IOException("Updated runtime verification failed.");
            if(File.ReadAllText(Path.Combine(install,"updater-runtime-version.txt"))!=AppVersionNormalized(staged.Manifest.Version))throw new InvalidDataException("Wrong version executed.");
            using(var exclusive=new FileStream(Path.Combine(install,AppUpdateInstaller.LockFile),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))AppUpdateInstaller.Rollback(staged.Directory);
            if(AppUpdateService.ProductVersion(Path.Combine(install,"Batcomputer.exe"))!=original)throw new InvalidDataException("Rollback version mismatch.");
            if(AppUpdateService.Hash(settings)!=settingsHash||AppUpdateService.Hash(sentinel)!=sentinelHash)throw new InvalidDataException("Fixture data changed.");
            File.WriteAllText(report,$"PASS {original} -> {staged.Manifest.Version} -> {original}\nSource: {(feed == null ? AppUpdateService.ReleasesPage : feed)}\nFull staged and installed manifests verified; updated app runtime executed without UI; rollback and settings/project preservation passed.\nTransaction: {staged.Directory}\n");
            return 0;
        }
        catch(Exception ex){File.WriteAllText(report,"FAIL "+ex);return 1;}
    }
    private static string AppVersionNormalized(string value)=>value.TrimStart('v','V').Split('+')[0];
}
