param(
    [Parameter(Mandatory)][string]$BaseDirectory,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$baseline=[IO.Path]::GetFullPath($BaseDirectory)
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new output folder; existing release packages are never overwritten.'}
foreach($directory in @($baseline,$publish)) {
    if(!(Test-Path -LiteralPath (Join-Path $directory 'Batcomputer.exe'))){throw "Missing complete app: $directory"}
    if(Test-Path -LiteralPath (Join-Path $directory 'Batcomputer.settings.json')){throw 'Do not package a user installation.'}
}
$process=Start-Process -FilePath (Join-Path $publish 'Batcomputer.exe') -ArgumentList @('--create-app-update-patch',('"'+$baseline+'"'),('"'+$publish+'"'),('"'+$output+'"')) -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw "Packaging failed; inspect $output/package-error.txt"}
$catalog=Get-Content (Join-Path $output 'Batcomputer-update-win-x64.patches.json') -Raw|ConvertFrom-Json
if($catalog.BaseVersion -eq $catalog.Manifest.Version){throw 'Patch baseline must be an older published version.'}
Write-Output "Full install: Batcomputer-update-win-x64.zip"
Write-Output "Updater patch: $($catalog.Asset)"
Write-Output "Baseline: $($catalog.BaseVersion); target: $($catalog.Manifest.Version)"
Write-Output 'Test both routes and run the publisher DryRun before uploading all ZIPs, catalog and checksum sidecars.'
