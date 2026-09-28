param(
    [Parameter(Mandatory)][string]$BaseDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(1024,65535)][int]$Port = 8772,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$baseline = [IO.Path]::GetFullPath($BaseDirectory)
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Use a new output folder; no existing installation is overwritten.' }
if (-not (Test-Path -LiteralPath (Join-Path $baseline 'Batcomputer.exe'))) { throw 'Choose the complete baseline publish folder.' }
New-Item -ItemType Directory -Path $root | Out-Null
$candidate = Join-Path $root 'candidate'
$bridge = Join-Path $root 'bridge-runner'
$intermediate = (Join-Path $root 'obj') + [IO.Path]::DirectorySeparatorChar
$buildArgs = @("-p:BaseIntermediateOutputPath=$intermediate", "-p:MSBuildProjectExtensionsPath=$intermediate")
$project = Join-Path $repo 'Batcomputer.csproj'
if (-not $NoRestore) {
    & dotnet restore $project -r win-x64 -p:Configuration=Release --ignore-failed-sources -p:NuGetAudit=false @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test publish restore failed.' }
}
& dotnet publish $project -c Release -o $candidate --no-restore @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Candidate publish failed.' }
$baseVersion = (Get-Item -LiteralPath (Join-Path $baseline 'Batcomputer.exe')).VersionInfo.ProductVersion
# This current-code runner understands patch ZIPs. Its base version is a fixture
# stamp, not a claim that the original published updater already supports them.
& dotnet publish $project -c Release -o $bridge --no-restore "-p:Version=$baseVersion" "-p:InformationalVersion=$baseVersion" '-p:FileVersion=1.0.0.0' @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Compatible test runner publish failed.' }
$feed = Join-Path $root 'feed'
$pack = Start-Process -FilePath (Join-Path $candidate 'Batcomputer.exe') -ArgumentList @('--create-app-update-patch', ('"'+$baseline+'"'), ('"'+$candidate+'"'), ('"'+$feed+'"')) -PassThru -Wait -WindowStyle Hidden
if ($pack.ExitCode -ne 0) { throw "Packaging failed: $feed/package-error.txt" }
$catalog = Get-Content -LiteralPath (Join-Path $feed 'Batcomputer-update-win-x64.patches.json') -Raw | ConvertFrom-Json
$url = "http://127.0.0.1:$Port/"
$assets = @()
foreach ($name in @('Batcomputer-update-win-x64.zip','Batcomputer-update-win-x64.patches.json',$catalog.Asset)) {
    $file = Get-Item -LiteralPath (Join-Path $feed $name)
    $assets += @{ name=$name; size=$file.Length; digest=('sha256:'+(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()); browser_download_url=($url+$name) }
}
$release = @{ tag_name=('v'+$catalog.Manifest.Version); prerelease=$true; draft=$false;
    body='Local beta test. This includes all current changes. Download, restart, check the version and completion prompt, then reopen the Update center. No GitHub release has been published.'; assets=$assets }
[IO.File]::WriteAllText((Join-Path $feed 'releases.json'),(ConvertTo-Json -InputObject @($release) -Depth 6),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $baseline -Destination (Join-Path $root 'legacy-runner') -Recurse
foreach ($name in @('automated','legacy-client','full-zip','changed-after-check','manual')) {
    $source = if ($name -eq 'manual') { $bridge } else { $baseline }
    $copy = Join-Path $root $name
    Copy-Item -LiteralPath $source -Destination $copy -Recurse
    New-Item -ItemType Directory -Path (Join-Path $copy 'Generated'),(Join-Path $copy 'TestGame/Content/Paks') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $copy '.batcomputer-updater-sandbox'),($url+'releases.json'))
    [IO.File]::WriteAllText((Join-Path $copy '.batcomputer-updater-full-app'),'Disposable complete-workspace test; never install this fixture over your real app.')
    [IO.File]::WriteAllText((Join-Path $copy 'Generated/KEEP-ME.txt'),'Updater project-preservation sentinel')
    $settings=@{IncludeBetaAppUpdates=$true;CheckAppUpdatesOnStartup=$false;ProjectRoot=$copy;GamePaksRoot=(Join-Path $copy 'TestGame/Content/Paks');GamePaksModFolder=(Join-Path $copy 'TestGame/Content/Paks/~mods/Expanded')}
    [IO.File]::WriteAllText((Join-Path $copy 'Batcomputer.settings.json'),($settings|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
[IO.File]::WriteAllText((Join-Path $bridge '.batcomputer-updater-sandbox'),($url+'releases.json'))
$summary=@{ version=$catalog.Manifest.Version; baseVersion=$catalog.BaseVersion; patchBytes=$catalog.DownloadSize;
    fullZipBytes=(Get-Item -LiteralPath (Join-Path $feed 'Batcomputer-update-win-x64.zip')).Length;
    changedFiles=$catalog.ChangedPaths.Count; reusedFiles=$catalog.Manifest.Files.Count-$catalog.ChangedPaths.Count;
    originalClient='Original published base uses the full ZIP. The compatible bridge runner tests the new patch reader.' }
[IO.File]::WriteAllText((Join-Path $root 'expected-download.json'),($summary|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$shell=(Get-Process -Id $PID).Path
$server=Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'Start-LocalUpdateTest.ps1')+'"'),'-ServeOnly','-Port',$Port,'-FeedDirectory',('"'+$feed+'"')) -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $root 'server-error.txt') -RedirectStandardOutput (Join-Path $root 'server-output.txt')
[IO.File]::WriteAllText((Join-Path $root 'server-pid.txt'),$server.Id.ToString())
$deadline=[DateTime]::UtcNow.AddSeconds(10)
while (-not (Test-Path -LiteralPath (Join-Path $feed 'server-ready.txt')) -and -not $server.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path -LiteralPath (Join-Path $feed 'server-ready.txt'))) { throw "Test server failed. See $root/server-error.txt" }
Write-Output "Test prepared: $root"
Write-Output "Patch: $([math]::Round($summary.patchBytes/1MB,2)) MB; full ZIP: $([math]::Round($summary.fullZipBytes/1MB,2)) MB; reuses $($summary.reusedFiles) files."
Write-Output 'No app window launched, tag created, GitHub release published, or game mod installed.'
