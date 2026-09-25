param([Parameter(Mandatory)][string]$BackupDirectory)
$ErrorActionPreference='Stop'
$backup=[IO.Path]::GetFullPath($BackupDirectory)
if(Test-Path -LiteralPath $backup){throw 'Choose a new backup folder.'}
New-Item -ItemType Directory -Path $backup | Out-Null
$credential=@{}
try {
    $lines="protocol=https`nhost=github.com`n`n" | git credential fill
    foreach($line in $lines){if($line -match '^([^=]+)=(.*)$'){$credential[$matches[1]]=$matches[2]}}
    if(-not $credential.password){throw 'No GitHub credential available.'}
    $headers=@{Authorization=('Bearer '+$credential.password);Accept='application/vnd.github+json';'User-Agent'='Batcomputer-release-maintenance'}
    $api='https://api.github.com/repos/Loomirr/Batcomputer'
    for($page=1; $page -le 10; $page++){
        $releases=Invoke-RestMethod -Uri "$api/releases?per_page=100&page=$page" -Headers $headers
        foreach($release in $releases | Where-Object { -not $_.draft -and $_.tag_name -match '^[vV]?0\.\d+(?:\.\d+)?(?:-|$)' }){
            if($release.name -like 'LEGACY*'){continue}
            [IO.File]::WriteAllText((Join-Path $backup "$($release.id).json"),($release|ConvertTo-Json -Depth 20))
            $banner='> **Legacy release (pre-1.0).** Archived for manual downloads only; not supported by the in-app updater. Use a 1.0 beta or newer release.'
            $body=@{name=('LEGACY — '+$release.name);body=($banner+"`n`n"+$release.body)} | ConvertTo-Json
            $updated=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body $body
            Write-Output "Marked legacy: $($updated.tag_name)"
            Start-Sleep -Milliseconds 1100
        }
        if($releases.Count -lt 100){break}
    }
} finally {$credential.Clear(); $lines=$null; $headers=$null}
