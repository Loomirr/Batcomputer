param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$NotesFile,
    [switch]$DryRun
)
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
$manifest=Get-Content -LiteralPath (Join-Path $package 'Batcomputer.update.json') -Raw | ConvertFrom-Json
$version=$manifest.Version.TrimStart('v')
if($version -notmatch '^\d+\.\d+\.\d+-beta\.\d+$' -or $version.StartsWith('99.')){throw 'Only genuine numbered beta versions may be published.'}
if($Commit -notmatch '^[0-9a-f]{40}$'){throw 'Use the exact pushed commit SHA.'}
$files=@((Join-Path $package 'Batcomputer-update-win-x64.zip'),(Join-Path $package 'Batcomputer-update-win-x64.zip.sha256'))
$catalogPath=Join-Path $package 'Batcomputer-update-win-x64.patches.json'
if(Test-Path -LiteralPath $catalogPath -PathType Leaf){
    $catalog=Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    if($catalog.Schema -ne 1 -or $catalog.Manifest.Version -ne $manifest.Version -or
       $catalog.Asset -cnotmatch '^Batcomputer-update-from-[0-9A-Za-z.-]+-win-x64\.zip$'){
        throw 'Patch catalog version, schema or asset name is invalid.'
    }
    $target=@($manifest.Files | ForEach-Object { "$($_.Path)|$($_.Size)|$($_.Sha256)" })
    $patchTarget=@($catalog.Manifest.Files | ForEach-Object { "$($_.Path)|$($_.Size)|$($_.Sha256)" })
    if($target.Count -ne $patchTarget.Count -or (Compare-Object $target $patchTarget)){throw 'Patch and full target manifests differ.'}
    $patchPath=Join-Path $package $catalog.Asset
    if(-not(Test-Path -LiteralPath $patchPath -PathType Leaf) -or
       (Get-Item -LiteralPath $patchPath).Length -ne $catalog.DownloadSize -or
       (Get-FileHash -LiteralPath $patchPath).Hash -ne $catalog.DownloadSha256){throw 'Patch ZIP differs from its catalog.'}
    $files+=@($patchPath,($patchPath+'.sha256'),$catalogPath,($catalogPath+'.sha256'))
} elseif(Get-ChildItem -LiteralPath $package -Filter 'Batcomputer-update-from-*-win-x64.zip'){
    throw 'Patch ZIP found without its catalog.'
}
foreach($file in $files){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing release asset: $file"}}
foreach($file in $files | Where-Object { -not $_.EndsWith('.sha256') }){
    $hash=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    $expected=$hash+'  '+[IO.Path]::GetFileName($file)
    if((Get-Content -LiteralPath ($file+'.sha256') -Raw).Trim() -cne $expected){throw "Checksum sidecar mismatch: $file"}
}
if(-not(Test-Path -LiteralPath $NotesFile -PathType Leaf)){throw 'Release notes file is missing.'}
if($DryRun){$files | ForEach-Object { Write-Output ('Validated release asset: '+[IO.Path]::GetFileName($_)) }; return}
# Credentials stay in memory and are sent only to GitHub's API/upload hosts.
$credential=@{}
try {
    $lines="protocol=https`nhost=github.com`n`n" | git credential fill
    foreach($line in $lines){if($line -match '^([^=]+)=(.*)$'){$credential[$matches[1]]=$matches[2]}}
    if(-not $credential.password){throw 'Sign in to GitHub with Git Credential Manager first.'}
    $headers=@{Authorization=('Bearer '+$credential.password);Accept='application/vnd.github+json';'User-Agent'='Batcomputer-release-publisher';'X-GitHub-Api-Version'='2022-11-28'}
    $api='https://api.github.com/repos/Loomirr/Batcomputer'
    $tag='v'+$version
    # Refuse duplicates, including drafts. Never overwrite an existing release or tag.
    for($page=1; $page -le 10; $page++){
        $existing=Invoke-RestMethod -Uri "$api/releases?per_page=100&page=$page" -Headers $headers
        if($existing | Where-Object tag_name -eq $tag){throw "Release $tag already exists; inspect it before retrying."}
        if($existing.Count -lt 100){break}
    }
    $null=Invoke-RestMethod -Uri "$api/commits/$Commit" -Headers $headers
    $body=@{tag_name=$tag;target_commitish=$Commit;name="Batcomputer $version";body=(Get-Content -LiteralPath $NotesFile -Raw);draft=$true;prerelease=$true;make_latest='false'} | ConvertTo-Json
    $release=Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    [IO.File]::WriteAllText((Join-Path $package 'github-release-receipt.json'),($release|ConvertTo-Json -Depth 10))
    foreach($file in $files){
        $name=[IO.Path]::GetFileName($file)
        $upload="https://uploads.github.com/repos/Loomirr/Batcomputer/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))"
        $asset=Invoke-RestMethod -Uri $upload -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file
        $expected='sha256:'+(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
        if($asset.digest -ne $expected -or $asset.size -ne (Get-Item -LiteralPath $file).Length){throw 'GitHub asset digest/size mismatch; release left as draft.'}
        Write-Output "Verified uploaded asset: $name"
    }
    $published=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"prerelease":true,"make_latest":"false"}'
    [IO.File]::WriteAllText((Join-Path $package 'github-release-receipt.json'),($published|ConvertTo-Json -Depth 10))
    Write-Output $published.html_url
} finally {$credential.Clear(); $lines=$null; $headers=$null}
