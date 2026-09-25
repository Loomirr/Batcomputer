param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$NotesFile
)
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
$manifest=Get-Content -LiteralPath (Join-Path $package 'Batcomputer.update.json') -Raw | ConvertFrom-Json
$version=$manifest.Version.TrimStart('v')
if($version -notmatch '^\d+\.\d+\.\d+-beta\.\d+$' -or $version.StartsWith('99.')){throw 'Only genuine numbered beta versions may be published.'}
if($Commit -notmatch '^[0-9a-f]{40}$'){throw 'Use the exact pushed commit SHA.'}
$files=@((Join-Path $package 'Batcomputer-update-win-x64.zip'),(Join-Path $package 'Batcomputer-update-win-x64.zip.sha256'))
foreach($file in $files){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing release asset: $file"}}
$zipHash=(Get-FileHash -LiteralPath $files[0]).Hash.ToLowerInvariant()
if((Get-Content -LiteralPath $files[1] -Raw).Split(' ')[0] -ne $zipHash){throw 'ZIP checksum sidecar mismatch.'}
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
    $body=@{tag_name=$tag;target_commitish=$Commit;name="Batcomputer $version — updater prerelease test";body=(Get-Content -LiteralPath $NotesFile -Raw);draft=$true;prerelease=$true;make_latest='false'} | ConvertTo-Json
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
