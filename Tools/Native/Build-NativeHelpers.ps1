param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [string]$CMake='cmake',
    [string]$Generator='Visual Studio 18 2026'
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=[IO.Path]::GetFullPath($BuildRoot)
if(Test-Path $root){throw 'Use a new empty build directory; this script never resets existing checkouts.'}
New-Item -ItemType Directory -Path $root|Out-Null
function Run([string]$Exe,[string[]]$Arguments){ & $Exe @Arguments; if($LASTEXITCODE){throw "$Exe failed: $LASTEXITCODE"} }
$retoc=Join-Path $root 'retoc';$cue=Join-Path $root 'cue'
Run git @('clone','--filter=blob:none','--no-checkout','https://github.com/Loomirr/retoc-oodle.git',$retoc)
Run git @('-C',$retoc,'checkout','--detach','a3d3c84f030118c36896568e35e0dcea6305e2b9')
Run git @('-C',$retoc,'apply','--ignore-space-change',(Join-Path $PSScriptRoot 'retoc-local-runtime.patch'))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'retoc.Cargo.lock') -Destination (Join-Path $retoc 'Cargo.lock')
$loader=(Join-Path $PSScriptRoot 'OodleLoader').Replace('\','/')
$patchConfig='patch."https://github.com/trumank/repak.git".oodle_loader.path="'+$loader+'"'
Run cargo @('test','--locked','--target-dir',(Join-Path $retoc 'target'),'--manifest-path',(Join-Path $retoc 'Cargo.toml'),'--config',$patchConfig,'-p','oodle_loader')
Run cargo @('build','--locked','--release','--target-dir',(Join-Path $retoc 'target'),'--manifest-path',(Join-Path $retoc 'Cargo.toml'),'--config',$patchConfig,'-p','retoc_cli')
Run git @('clone','--filter=blob:none','--no-checkout','https://github.com/FabianFG/CUE4Parse.git',$cue)
Run git @('-C',$cue,'checkout','--detach','ecad882a3049df6f27e0c5c3a3531346305c010b')
Run git @('-C',$cue,'submodule','update','--init','CUE4Parse-Natives/ACL/external/acl')
Run git @('-C',(Join-Path $cue 'CUE4Parse-Natives/ACL/external/acl'),'submodule','update','--init','external/rtm')
$aclBuild=Join-Path $root 'acl-build'
Run $CMake @('-S',(Join-Path $cue 'CUE4Parse-Natives'),'-B',$aclBuild,'-G',$Generator,'-A','x64','-DCMAKE_POLICY_DEFAULT_CMP0091=NEW','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
Run $CMake @('--build',$aclBuild,'--config','Release')
$out=Join-Path $root 'binaries';New-Item -ItemType Directory -Path $out|Out-Null
Copy-Item (Join-Path $retoc 'target/release/retoc.exe') $out
Copy-Item (Join-Path $aclBuild 'Release/CUE4Parse-Natives.dll') $out
Get-FileHash (Join-Path $out '*.exe'),(Join-Path $out '*.dll') -Algorithm SHA256
Write-Output 'Source-pinned build finished. Run acceptance checks and collect notices before promoting binaries; no installation was changed.'
