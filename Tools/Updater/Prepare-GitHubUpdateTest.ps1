param([string]$OutputRoot, [switch]$NoRestore)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $OutputRoot){$OutputRoot=Join-Path $repo ('artifacts/github-update-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$root=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $root){throw 'Choose a new output folder; existing installations are never overwritten.'}
New-Item -ItemType Directory -Path $root | Out-Null
$base=Join-Path $root 'base'
$candidate=Join-Path $root 'candidate'
$intermediate=(Join-Path $root 'obj')+[IO.Path]::DirectorySeparatorChar
$buildArgs=@("-p:BaseIntermediateOutputPath=$intermediate", "-p:MSBuildProjectExtensionsPath=$intermediate")
if(-not $NoRestore){
    & dotnet restore (Join-Path $repo 'Batcomputer.csproj') -r win-x64 -p:Configuration=Release --ignore-failed-sources -p:NuGetAudit=false @buildArgs
    if($LASTEXITCODE -ne 0){throw 'Fixture restore failed.'}
}
$restore=@('--no-restore')
# A bridge fixture built from current code, NOT a claim that the old public beta had an updater.
& dotnet publish (Join-Path $repo 'Batcomputer.csproj') -c Release -o $base '-p:Version=1.0.0-beta.2' '-p:InformationalVersion=1.0.0-beta.2' '-p:FileVersion=1.0.0.2' @buildArgs @restore
if($LASTEXITCODE -ne 0){throw 'Base fixture publish failed.'}
& dotnet publish (Join-Path $repo 'Batcomputer.csproj') -c Release -o $candidate @buildArgs @restore
if($LASTEXITCODE -ne 0){throw 'Candidate publish failed.'}
$package=Join-Path $root 'release'
$pack=Start-Process -FilePath (Join-Path $candidate 'Batcomputer.exe') -ArgumentList @('--create-app-update',('"'+$candidate+'"'),('"'+$package+'"')) -Wait -PassThru -WindowStyle Hidden
if($pack.ExitCode -ne 0){throw "Packaging failed: $package/package-error.txt"}
foreach($name in @('automated','manual')){
    $copy=Join-Path $root $name
    Copy-Item -LiteralPath $base -Destination $copy -Recurse
    New-Item -ItemType Directory -Path (Join-Path $copy 'Generated'),(Join-Path $copy 'TestGame/Content/Paks') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $copy '.batcomputer-updater-github-test'),'Disposable GitHub updater fixture; normal fixed production feed.')
    [IO.File]::WriteAllText((Join-Path $copy 'Generated/KEEP-ME.txt'),'GitHub updater preservation test')
    $settings=@{IncludeBetaAppUpdates=$true;CheckAppUpdatesOnStartup=$false;ProjectRoot=$copy;GamePaksRoot=(Join-Path $copy 'TestGame/Content/Paks');GamePaksModFolder=(Join-Path $copy 'TestGame/Content/Paks/~mods/Expanded')}
    [IO.File]::WriteAllText((Join-Path $copy 'Batcomputer.settings.json'),($settings|ConvertTo-Json))
}
Write-Output "Ready: $root"
Write-Output 'No windows launched or GitHub release created. Publish the candidate, then run the GitHub fixture check from base/Batcomputer.exe against automated/.'
