param([Parameter(Mandatory)][string]$RetocSource,[Parameter(Mandatory)][string]$CueSource)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$loader=(Join-Path $PSScriptRoot 'OodleLoader').Replace('\','/')
$patchConfig='patch."https://github.com/trumank/repak.git".oodle_loader.path="'+$loader+'"'
$meta=cargo metadata --locked --offline --format-version 1 --filter-platform x86_64-pc-windows-msvc --manifest-path (Join-Path $RetocSource 'Cargo.toml') --config $patchConfig|ConvertFrom-Json
if($LASTEXITCODE){throw 'Cargo metadata failed'}
$inventory=@();$missing=@()
foreach($p in $meta.packages){
    $sourceDir=Split-Path $p.manifest_path
    $texts=@(Get-ChildItem -LiteralPath $sourceDir -Recurse -File|Where-Object {$_.Name -match '^(?i:licen[cs]e|copying|copyright|notice)([._-].*)?$' -and $_.Length -lt 2MB})
    if(!$texts.Count -and $p.source -like 'git+*'){
        $texts=@(Get-ChildItem -LiteralPath (Split-Path $sourceDir) -File|Where-Object {$_.Name -match '^(?i:licen[cs]e|copying|copyright|notice)([._-].*)?$'})
    }
    if(!$texts.Count -and $p.name -in 'retoc','retoc_cli'){$texts=@(Get-Item (Join-Path $RetocSource 'LICENSE'))}
    if(!$texts.Count -and $p.name -eq 'oodle_loader'){$texts=@(Get-Item (Join-Path $repo 'LICENSE'))}
    $dest=Join-Path $repo ('licenses/native/retoc/'+$p.name+'-'+$p.version)
    New-Item -ItemType Directory -Path $dest -Force|Out-Null
    if(!$texts.Count -and $p.name -in 'pariter','typed-path','ser-hex'){
        # These exact crates explicitly offer Apache-2.0 but omit a license file.
        # Retain their original declaration and author/source information as evidence.
        $texts=@(Get-Item (Join-Path $sourceDir 'Cargo.toml'),(Join-Path $sourceDir 'README.md'),(Join-Path $sourceDir '.cargo_vcs_info.json') -ErrorAction SilentlyContinue)
        if($p.name -eq 'ser-hex'){$texts+=Get-Item (Join-Path (Split-Path $sourceDir) 'Cargo.toml')}
        Copy-Item -LiteralPath (Join-Path $CueSource 'LICENSE') -Destination (Join-Path $dest 'Apache-2.0.txt')
        [IO.File]::WriteAllLines((Join-Path $dest 'LICENSE-SELECTION.txt'),@('Apache-2.0 selected from upstream declaration: '+$p.license,'Authors: '+($p.authors -join ', '),'Repository: '+$p.repository,'Source: '+$p.source,'Original license declaration files are retained alongside this text.'))
    }
    if(!$texts.Count){$missing+=($p.name+'-'+$p.version);continue}
    foreach($t in $texts){
        $relative=[IO.Path]::GetRelativePath($sourceDir,$t.FullName)
        if($relative.StartsWith('..')){$relative='workspace-'+$t.Name}
        $target=Join-Path $dest ($relative+'.txt')
        New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
        Copy-Item -LiteralPath $t.FullName -Destination $target
    }
    $inventory+=[pscustomobject]@{Name=$p.name;Version=$p.version;License=$p.license;Source=$p.source;Repository=$p.repository}
}
$native=Join-Path $repo 'licenses/native'
[IO.File]::WriteAllText((Join-Path $native 'retoc-dependencies.txt'),(ConvertTo-Json -InputObject $inventory -Depth 5))
foreach($entry in @(
    @('CUE4Parse',(Join-Path $CueSource 'LICENSE')),
    @('CUE4Parse-NOTICE',(Join-Path $CueSource 'NOTICE')),
    @('ACL',(Join-Path $CueSource 'CUE4Parse-Natives/ACL/external/acl/LICENSE')),
    @('RTM',(Join-Path $CueSource 'CUE4Parse-Natives/ACL/external/acl/external/rtm/LICENSE')))){
    Copy-Item -LiteralPath $entry[1] -Destination (Join-Path $native ($entry[0]+'.txt'))
}
if($missing.Count){throw ('Missing native dependency license text: '+($missing -join ', '))}
'Collected native dependency notices for '+$inventory.Count+' resolved packages (includes build/test dependencies).'
