param([string]$AuditDirectory='output/redistribution-audit')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$records=Get-Content (Join-Path $AuditDirectory 'package-licenses.json') -Raw|ConvertFrom-Json
$used=(Get-Content (Join-Path $AuditDirectory 'summary.json') -Raw|ConvertFrom-Json).UsedPackages
$pins=Get-Content (Join-Path $PSScriptRoot 'DependencyLicensePins.json') -Raw|ConvertFrom-Json
foreach($record in $records){
    $pin=$pins|Where-Object Package -eq $record.Package|Select-Object -First 1
    if($pin){$record.Repository=$pin.Repository;$record.Commit=$pin.Commit}
}
$results=$records | Where-Object {$_.Package -in $used -and $_.Notices.Count -eq 0} | ForEach-Object -Parallel {
    $p=$_;$root=$using:repo;$reviewPins=$using:pins
    if($p.Repository -notmatch '^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$' -or $p.Commit -notmatch '^[0-9a-fA-F]{40}$'){
        [pscustomobject]@{Package=$p.Package;Status='No pinned repository commit; exact-version review remains';Files=@()};return
    }
    $upstream=$p.Repository -replace '\.git$','';$base=$upstream -replace '^https://github.com/','https://raw.githubusercontent.com/'
    $dir=Join-Path $root ('licenses/dependencies/'+$p.Package.Replace('/','-'));$found=@()
    foreach($name in @('LICENSE','LICENSE.md','LICENSE.txt','license.txt','LICENSE-MIT','LICENSE-APACHE','NOTICE','license.md','Licence.txt')){
        $url=$base+'/'+$p.Commit+'/'+$name
        try{
            $response=Invoke-WebRequest -Uri $url -TimeoutSec 15
            $content=[string]$response.Content
            if($content.Length -lt 50 -or $content -match '(?i)<html'){continue}
            New-Item -ItemType Directory -Path $dir -Force|Out-Null
            [IO.File]::WriteAllText((Join-Path $dir ($name+'.txt')),$content)
            $found+=[pscustomobject]@{File=('licenses/dependencies/'+$p.Package.Replace('/','-')+'/'+$name+'.txt');Url=$url}
        }catch{}
    }
    if($found.Count){
        $evidence=($reviewPins|Where-Object Package -eq $p.Package|Select-Object -First 1).Evidence
        if(-not$evidence){$evidence='Repository commit declared by the resolved NuGet package'}
        $sourceLines=@($p.Package,('Source basis: '+$evidence),'These notices do not certify the composition of prebuilt native dependencies.','')
        foreach($file in $found){$sourceLines+=($file.File+' | '+$file.Url+' | SHA256 '+(Get-FileHash (Join-Path $root $file.File) -Algorithm SHA256).Hash.ToLower())}
        [IO.File]::WriteAllLines((Join-Path $dir 'upstream-sources.txt'),$sourceLines)
    }
    [pscustomobject]@{Package=$p.Package;Status=if($found.Count){'Pinned upstream text collected; embedded dependencies still require review'}else{'License text not found at pinned commit'};Files=$found}
} -ThrottleLimit 6
[IO.File]::WriteAllText((Join-Path ([IO.Path]::GetFullPath($AuditDirectory)) 'upstream-license-sources.json'),(ConvertTo-Json -InputObject @($results) -Depth 6))
$results|Select-Object Package,Status|Format-Table -AutoSize
