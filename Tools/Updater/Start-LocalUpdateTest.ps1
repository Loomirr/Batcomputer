param(
    [string]$OutputRoot,
    [ValidateRange(1024, 65535)][int]$Port = 8754,
    [switch]$ServeOnly,
    [string]$FeedDirectory,
    [switch]$NoLaunch,
    [switch]$NoRestore,
    [switch]$FullApp,
    [switch]$FileUpdates,
    [switch]$SingleFileBase,
    [switch]$FlatBase,
    [ValidateSet('Normal', 'Slow', 'Corrupt')][string]$Scenario = 'Normal'
)
$ErrorActionPreference = 'Stop'

if ($ServeOnly) {
    # Deliberately loopback-only, two routes, no uploads or arbitrary file lookup.
    $feedRoot = [IO.Path]::GetFullPath($FeedDirectory)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    [IO.File]::WriteAllText((Join-Path $feedRoot 'server-ready.txt'), $PID.ToString())
    try {
        while ($true) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = [IO.StreamReader]::new($stream)
                $request = $reader.ReadLine()
                while ($reader.ReadLine()) { }
                $route = ($request -split ' ')[1]
                $name = switch -Regex ($route) {
                    '^/releases\.json$' { 'releases.json' }
                    '^/Batcomputer-update-win-x64\.zip$' { 'Batcomputer-update-win-x64.zip' }
                    '^/Batcomputer-update-win-x64\.files\.json$' { 'Batcomputer-update-win-x64.files.json' }
                    '^/bc-file-[0-9a-f]{64}\.gz$' { $route.Substring(1) }
                    default { $null }
                }
                if ($name -and $request.StartsWith('GET ')) {
                    $path = Join-Path $feedRoot $name
                    $length = (Get-Item -LiteralPath $path).Length
                    $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $length`r`nConnection: close`r`n`r`n")
                    $stream.Write($header, 0, $header.Length)
                    $file = [IO.File]::OpenRead($path)
                    try {
                        if ($Scenario -eq 'Slow' -and ($name.EndsWith('.zip') -or $name.EndsWith('.gz'))) {
                            $buffer = [byte[]]::new(65536)
                            while (($count = $file.Read($buffer, 0, $buffer.Length)) -gt 0) {
                                $stream.Write($buffer, 0, $count)
                                Start-Sleep -Milliseconds 40
                            }
                        } else { $file.CopyTo($stream) }
                    } finally { $file.Dispose() }
                } else {
                    $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 404 Not Found`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                    $stream.Write($header, 0, $header.Length)
                }
            } catch { # A cancelled download should not stop the local server.
            } finally { $client.Dispose() }
        }
    } finally { $listener.Stop() }
    exit
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo ('artifacts/updater-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$testRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $testRoot) { throw 'Choose a NEW test output folder. Existing files are never overwritten.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$base = Join-Path $testRoot 'installed'
$next = Join-Path $testRoot 'next-publish'
$feed = Join-Path $testRoot 'feed'
$project = Join-Path $repo 'Batcomputer.csproj'
$restoreArgs = @()
if ($NoRestore) { $restoreArgs = @('--no-restore') }

# Both are genuine self-contained releases with different executable versions.
# Neither creates Git tags, uploads assets, changes your usual build, or installs game mods.
$baseLayout = '-p:PublishSingleFile=false'
if ($SingleFileBase) { $baseLayout = '-p:PublishSingleFile=true' }
$organizedBase = '-p:BatcomputerOrganizedLayout=true'
if ($FlatBase) { $organizedBase = '-p:BatcomputerOrganizedLayout=false' }
& dotnet publish $project -c Release -o $base '-p:Version=99.0.0-updatertest.1' '-p:InformationalVersion=99.0.0-updatertest.1' '-p:FileVersion=99.0.0.1' $baseLayout $organizedBase -v minimal @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Base test publish failed.' }
& dotnet publish $project -c Release -o $next '-p:Version=99.0.0-updatertest.2' '-p:InformationalVersion=99.0.0-updatertest.2' '-p:FileVersion=99.0.0.2' '-p:PublishSingleFile=false' -v minimal @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Next test publish failed.' }
$packageCommand = '--create-app-update'
if ($FileUpdates) { $packageCommand = '--create-app-update-files' }
$packageProcess = Start-Process -FilePath (Join-Path $base 'Batcomputer.exe') -ArgumentList @($packageCommand, ('"' + $next + '"'), ('"' + $feed + '"')) -PassThru -Wait -WindowStyle Hidden
if ($packageProcess.ExitCode -ne 0) { throw "Package failed. See $feed/package-error.txt" }
$zip = Join-Path $feed 'Batcomputer-update-win-x64.zip'
$url = "http://127.0.0.1:$Port/"
$release = @{
    tag_name = '99.0.0-updatertest.2'; prerelease = $true; draft = $false
    body = 'LOCAL TEST ONLY. Close the test Updates window after scheduling installation. It should restart showing updatertest.2. The backup helper can restore updatertest.1.'
    assets = @(@{ name = 'Batcomputer-update-win-x64.zip'; size = (Get-Item -LiteralPath $zip).Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        browser_download_url = $url + 'Batcomputer-update-win-x64.zip' })
}
if ($FullApp) { $release.body = "FULL APP FLOW TEST`r`n`r`nOpen Updates from the logo, main menu or Settings. Download, then close and reopen just the update menu: the verified package should remain ready.`r`n`r`nChoose Install on exit, return to the workspace, save your work and exit the whole app. It should restart in the full workspace as updatertest.2. Open Updates again and confirm live startup status." }
if ($FileUpdates) {
    foreach ($assetFile in Get-ChildItem -LiteralPath $feed -File | Where-Object { $_.Name -eq 'Batcomputer-update-win-x64.files.json' -or $_.Name -match '^bc-file-[0-9a-f]{64}\.gz$' }) {
        $release.assets += @{ name = $assetFile.Name; size = $assetFile.Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $assetFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); browser_download_url = $url + $assetFile.Name }
    }
    $catalog = Get-Content -LiteralPath (Join-Path $feed 'Batcomputer-update-win-x64.files.json') -Raw | ConvertFrom-Json
    $changed = @($catalog.Files | Where-Object {
        $local = Join-Path $base $_.Path
        if (Test-Path -LiteralPath $local) { if ((Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -eq $_.Sha256) { return $false } }
        if ($FlatBase -and $_.Path.StartsWith('app/')) {
            $legacy = Join-Path $base $_.Path.Substring(4)
            if (Test-Path -LiteralPath $legacy) { if ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -eq $_.Sha256) { return $false } }
        }
        return $true
    })
    $changedBytes = ($changed | Sort-Object Asset -Unique | Measure-Object DownloadSize -Sum).Sum
    $fullBytes = ($catalog.Files | Sort-Object Asset -Unique | Measure-Object DownloadSize -Sum).Sum
    $summary = @{ fullZipBytes = (Get-Item -LiteralPath $zip).Length; fullPayloadBytes = $fullBytes; changedDownloadBytes = $changedBytes; changedFiles = $changed.Path; reusedFileCount = $catalog.Files.Count - $changed.Count }
    [IO.File]::WriteAllText((Join-Path $testRoot 'expected-download.json'), (ConvertTo-Json $summary -Depth 5), [Text.UTF8Encoding]::new($false))
    $release.body += "`r`n`r`nCHANGED-FILE TEST: Expected download $([math]::Round($changedBytes/1MB,2)) MB; reuses $($summary.reusedFileCount) unchanged files. Read expected-download.json beside the installed folder. Settings and KEEP-ME must survive."
}
if ($Scenario -eq 'Corrupt') { $release.assets[0].digest = 'sha256:' + ('0' * 64) }
if ($Scenario -eq 'Corrupt' -and $FileUpdates) { ($release.assets | Where-Object name -eq 'Batcomputer-update-win-x64.files.json').digest = 'sha256:' + ('0' * 64) }
[IO.File]::WriteAllText((Join-Path $feed 'releases.json'), (ConvertTo-Json -InputObject @($release) -Depth 5), [Text.UTF8Encoding]::new($false))
Set-Content -LiteralPath (Join-Path $base '.batcomputer-updater-sandbox') -Value ($url + 'releases.json') -Encoding ascii
if ($FullApp) { Set-Content -LiteralPath (Join-Path $base '.batcomputer-updater-full-app') -Value 'Disposable full-workspace updater test. Not a release build.' -Encoding ascii }
# Sentinels exercise preservation; these are not copied from your real app.
New-Item -ItemType Directory -Path (Join-Path $base 'Generated') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $base 'Generated/KEEP-ME.txt') -Value 'Local updater preservation test' -Encoding ascii
$fakePaks = Join-Path $base 'TestGame/Content/Paks'
New-Item -ItemType Directory -Path $fakePaks -Force | Out-Null
$fixtureSettings = @{ IncludeBetaAppUpdates = $true; ProjectRoot = $base; GamePaksRoot = $fakePaks; GamePaksModFolder = (Join-Path $fakePaks '~mods/Expanded') }
[IO.File]::WriteAllText((Join-Path $base 'Batcomputer.settings.json'), (ConvertTo-Json $fixtureSettings), [Text.UTF8Encoding]::new($false))
$shell = (Get-Process -Id $PID).Path
$server = Start-Process -FilePath $shell -ArgumentList @('-NoProfile', '-File', ('"' + $PSCommandPath + '"'), '-ServeOnly', '-Port', $Port, '-FeedDirectory', ('"' + $feed + '"'), '-Scenario', $Scenario) -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $testRoot 'server-error.txt') -RedirectStandardOutput (Join-Path $testRoot 'server-output.txt')
Set-Content -LiteralPath (Join-Path $testRoot 'server-pid.txt') -Value $server.Id
$readyDeadline = [DateTime]::UtcNow.AddSeconds(10)
while (-not (Test-Path -LiteralPath (Join-Path $feed 'server-ready.txt')) -and -not $server.HasExited -and [DateTime]::UtcNow -lt $readyDeadline) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path -LiteralPath (Join-Path $feed 'server-ready.txt'))) { throw "Local server did not start. See $testRoot/server-error.txt; choose an unused port." }
if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $base 'Batcomputer.exe') -WindowStyle Normal }
Write-Output "Local updater test: $base"
Write-Output "Local server PID: $($server.Id). Stop this process after testing. No GitHub releases were created."
