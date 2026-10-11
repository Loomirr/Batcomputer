param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$ReleaseTag,[switch]$DryRun)
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
$manifest=Get-Content (Join-Path $package 'Batcomputer.update.json') -Raw|ConvertFrom-Json
$catalog=Get-Content (Join-Path $package 'Batcomputer-update-win-x64.patches.json') -Raw|ConvertFrom-Json
if($ReleaseTag -cne ('v'+$manifest.Version) -or $catalog.Manifest.Version -cne $manifest.Version){throw 'Tag/target mismatch'}
if($catalog.Asset -cnotmatch '^Batcomputer-update-from-[0-9A-Za-z.-]+-win-x64\.zip$'){throw 'Invalid patch asset name'}
$target=@($manifest.Files|ForEach-Object{"$($_.Path)|$($_.Size)|$($_.Sha256)"})
$patchTarget=@($catalog.Manifest.Files|ForEach-Object{"$($_.Path)|$($_.Size)|$($_.Sha256)"})
if($target.Count -ne $patchTarget.Count -or (Compare-Object $target $patchTarget)){throw 'Patch target differs from full target'}
# Catalog last: the update client cannot select a patch until all referenced assets exist.
$names=@($catalog.Asset,($catalog.Asset+'.sha256'),'Batcomputer-update-win-x64.patches.json.sha256','Batcomputer-update-win-x64.patches.json')
foreach($name in @('Batcomputer-update-win-x64.zip',$catalog.Asset,'Batcomputer-update-win-x64.patches.json')){
 $path=Join-Path $package $name
 $hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
 if((Get-Content ($path+'.sha256') -Raw).Trim() -cne ($hash+'  '+$name)){throw "Bad checksum: $name"}
}
if((Get-FileHash (Join-Path $package $catalog.Asset)).Hash -ne $catalog.DownloadSha256 -or (Get-Item (Join-Path $package $catalog.Asset)).Length -ne $catalog.DownloadSize){throw 'Patch does not match catalog'}
if($DryRun){Write-Output ('Validated missing-patch repair for '+$ReleaseTag);return}
$credential=@{}
try {
 $lines="protocol=https`nhost=github.com`n`n"|git credential fill
 foreach($line in $lines){if($line -match '^([^=]+)=(.*)$'){$credential[$matches[1]]=$matches[2]}}
 if(!$credential.password){throw 'GitHub sign-in required'}
 $headers=@{Authorization=('Bearer '+$credential.password);Accept='application/vnd.github+json';'User-Agent'='Batcomputer-release-repair'}
 $api='https://api.github.com/repos/Loomirr/Batcomputer'
 $release=Invoke-RestMethod "$api/releases/tags/$ReleaseTag" -Headers $headers
 if($release.draft){throw 'Use normal publisher for draft releases'}
 $full=@($release.assets|Where-Object name -eq 'Batcomputer-update-win-x64.zip')
 $fullPath=Join-Path $package 'Batcomputer-update-win-x64.zip'
 if($full.Count -ne 1 -or $full[0].digest -cne ('sha256:'+(Get-FileHash $fullPath).Hash.ToLowerInvariant()) -or $full[0].size -ne (Get-Item $fullPath).Length){throw 'Full ZIP differs from the published release; no uploads made'}
 # Preflight ALL collisions before any mutation. Existing identical assets make retries safe.
 foreach($name in $names){
  $existing=@($release.assets|Where-Object name -ceq $name)
  if($existing.Count -gt 1){throw "Duplicate asset: $name"}
  if($existing.Count -eq 1 -and ($existing[0].digest -cne ('sha256:'+(Get-FileHash (Join-Path $package $name)).Hash.ToLowerInvariant()) -or $existing[0].size -ne (Get-Item (Join-Path $package $name)).Length)){throw "Conflicting existing asset: $name"}
 }
 foreach($name in $names){
  if($release.assets|Where-Object name -ceq $name){Write-Output "Already verified: $name";continue}
  $path=Join-Path $package $name
  $url="https://uploads.github.com/repos/Loomirr/Batcomputer/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
  $asset=Invoke-RestMethod $url -Headers $headers -Method Post -ContentType 'application/octet-stream' -InFile $path
  if($asset.digest -cne ('sha256:'+(Get-FileHash $path).Hash.ToLowerInvariant()) -or $asset.size -ne (Get-Item $path).Length){throw 'Uploaded digest mismatch; inspect remote state before retrying'}
  Write-Output "Verified: $name"
 }
 Write-Output $release.html_url
} finally {$credential.Clear();$lines=$null;$headers=$null}
