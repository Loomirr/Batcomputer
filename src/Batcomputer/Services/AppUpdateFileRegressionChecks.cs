using System.IO.Compression;
using System.Text.Json;

namespace Batcomputer;

internal static class AppUpdateFileRegressionChecks
{
    internal static void Run(string root, Action<string, Action> check, Action<Action> reject)
    {
        var publish = Path.Combine(root,"files-publish");Directory.CreateDirectory(publish);
        File.Copy(Environment.ProcessPath!,Path.Combine(publish,"Batcomputer.exe"));
        var random = new byte[256*1024];new Random(1729).NextBytes(random);
        File.WriteAllBytes(Path.Combine(publish,"unchanged.dll"),random);
        File.WriteAllText(Path.Combine(publish,"changed.dll"),"new library content");
        var package = Path.Combine(root,"files-package");
        AppUpdatePackageService.CreateWithFilePayloads(publish,package);
        var catalog = JsonSerializer.Deserialize<FileUpdateCatalog>(File.ReadAllText(Path.Combine(package,AppUpdateService.FileCatalogName)),AppUpdateService.Json)!;
        using var server = new AppUpdateRegressionChecks.LocalServer();
        foreach(var path in Directory.GetFiles(package))server.Routes["/"+Path.GetFileName(path)] = File.ReadAllBytes(path);
        var urls = catalog.Files.DistinctBy(f=>f.Asset).ToDictionary(f=>f.Asset,f=>new Uri(server.Url+f.Asset));
        var plan = new FileUpdatePlan(catalog,urls,catalog.Files.DistinctBy(f=>f.Asset).Sum(f=>f.DownloadSize),2);
        var release = new AppUpdateRelease(AppVersion.Current,"",new Uri(server.Url+AppUpdateService.FileCatalogName),1,"",plan);
        string Install()
        {
            var dir=Path.Combine(root,"files-install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(publish,"Batcomputer.exe"),Path.Combine(dir,"Batcomputer.exe"));
            File.Copy(Path.Combine(publish,"unchanged.dll"),Path.Combine(dir,"unchanged.dll"));
            File.WriteAllText(Path.Combine(dir,"changed.dll"),"old library content");
            File.WriteAllText(Path.Combine(dir,"Batcomputer.settings.json"),"sentinel");return dir;
        }
        StagedAppUpdate Download(string install, AppUpdateRelease? selected=null)
        {
            using var service=new AppUpdateService(new Uri(server.Url+"releases.json"),install);
            return service.DownloadAsync(selected??release,AppUpdateInstaller.NewTransaction(install),null,CancellationToken.None).GetAwaiter().GetResult();
        }
        check("file updates download only changed bytes; stage complete app; unchanged files not replaced; rollback subset",()=>
        {
            var install=Install();var stamp=File.GetLastWriteTimeUtc(Path.Combine(install,"unchanged.dll"));
            var tx=Download(install);var steady=catalog.Files.Single(f=>f.Path=="unchanged.dll");
            if(server.Requests.Contains("/"+steady.Asset))throw new Exception("Downloaded an unchanged dependency.");
            foreach(var file in AppUpdateService.ManifestFor(catalog).Files)AppUpdateService.VerifyFile(UpdatePaths.Resolve(Path.Combine(tx.Directory,"payload"),file.Path),file);
            AppUpdateInstaller.ApplyFiles(tx.Directory);
            var journal=JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(tx.Directory,"journal.json")),AppUpdateService.Json)!;
            if(journal.Files.Count!=1||journal.Files[0].NewFile.Path!="changed.dll")throw new Exception("Replaced unchanged files.");
            if(File.GetLastWriteTimeUtc(Path.Combine(install,"unchanged.dll"))!=stamp)throw new Exception("Touched unchanged timestamp.");
            AppUpdateInstaller.Rollback(tx.Directory);
            if(File.ReadAllText(Path.Combine(install,"changed.dll"))!="old library content"||File.ReadAllText(Path.Combine(install,"Batcomputer.settings.json"))!="sentinel")throw new Exception("Subset rollback failed.");
        });
        check("changed or missing dependency since Check is fetched, never blindly reused",()=>
        {
            foreach(bool missing in new[]{true,false})
            {
                var install=Install();var dependency=Path.Combine(install,"unchanged.dll");
                if(missing)File.Delete(dependency);else{var corrupt=(byte[])random.Clone();corrupt[0]^=1;File.WriteAllBytes(dependency,corrupt);}
                var tx=Download(install);var file=AppUpdateService.ManifestFor(catalog).Files.Single(f=>f.Path=="unchanged.dll");
                AppUpdateService.VerifyFile(Path.Combine(tx.Directory,"payload","unchanged.dll"),file);
            }
        });
        check("file updates reject corrupt compressed bytes and oversized expansion without changing installation",()=>
        {
            var changed=catalog.Files.Single(f=>f.Path=="changed.dll");var route="/"+changed.Asset;var good=server.Routes[route];
            var bad=(byte[])good.Clone();bad[^1]^=1;server.Routes[route]=bad;
            var install=Install();reject(()=>Download(install));server.Routes[route]=good;
            var bomb=catalog with{Files=catalog.Files.Select(f=>f.Path=="changed.dll"?f with{Size=1}:f).ToList()};
            reject(()=>Download(Install(),release with{FilePlan=plan with{Catalog=bomb}}));
            if(File.ReadAllText(Path.Combine(install,"changed.dll"))!="old library content")throw new Exception("Failed download changed application.");
        });
        check("file catalogs reject paths, duplicate entries, malformed payload names and version mismatch",()=>
        {
            var f=catalog.Files[0];
            foreach(var invalid in new[]{f with{Path="../oops.dll"},f with{Path="Batcomputer.settings.json"},f with{Asset="../../bad.gz"},f with{DownloadSize=0}})
                reject(()=>AppUpdateService.ValidateCatalog(catalog with{Files=new(){invalid}}));
            reject(()=>AppUpdateService.ValidateCatalog(catalog with{Files=catalog.Files.Concat(new[]{f}).ToList()}));
            reject(()=>Download(Install(),release with{Version="98.0.0"}));
        });
        check("authenticated file catalog selection, estimated bytes, missing payload and bad metadata digest",()=>
        {
            var newer=catalog with{Version="99.0.0"};
            var indexBytes=JsonSerializer.SerializeToUtf8Bytes(newer,AppUpdateService.Json);server.Routes["/"+AppUpdateService.FileCatalogName]=indexBytes;
            string Digest(byte[] b)=>"sha256:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            object Asset(string name,byte[] data)=>new{name,size=data.Length,digest=Digest(data),browser_download_url=server.Url+name};
            var assets=new List<object>{Asset(AppUpdateService.FileCatalogName,indexBytes)};
            assets.AddRange(newer.Files.DistinctBy(f=>f.Asset).Select(f=>Asset(f.Asset,server.Routes["/"+f.Asset])));
            void Feed()=>server.Routes["/releases.json"]=JsonSerializer.SerializeToUtf8Bytes(new[]{new{tag_name="99.0.0",draft=false,prerelease=false,assets}});
            Feed();var install=Install();using var service=new AppUpdateService(new Uri(server.Url+"releases.json"),install);
            var selected=service.CheckAsync(true,CancellationToken.None).GetAwaiter().GetResult();
            if(selected?.FilePlan?.ReusedFiles!=2||selected.Size!=catalog.Files.Single(f=>f.Path=="changed.dll").DownloadSize)throw new Exception("Incorrect selective download estimate.");
            assets.RemoveAt(assets.Count-1);Feed();reject(()=>service.CheckAsync(true,CancellationToken.None).GetAwaiter().GetResult());
            server.Routes["/"+AppUpdateService.FileCatalogName]=new byte[indexBytes.Length];reject(()=>service.CheckAsync(true,CancellationToken.None).GetAwaiter().GetResult());
        });
        check("cancelled file update leaves installed app unchanged",()=>
        {
            using var stop=new CancellationTokenSource();stop.Cancel();var install=Install();using var service=new AppUpdateService(new Uri(server.Url+"releases.json"),install);
            reject(()=>service.DownloadAsync(release,AppUpdateInstaller.NewTransaction(install),null,stop.Token).GetAwaiter().GetResult());
            if(File.ReadAllText(Path.Combine(install,"changed.dll"))!="old library content")throw new Exception("Cancelled update changed application.");
        });
    }
}
