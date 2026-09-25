param([Parameter(Mandatory)][string]$RetocBinary,[Parameter(Mandatory)][string]$AclBinary)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$patches=@('Tools/Native/retoc-local-runtime.patch','Tools/Native/retoc.Cargo.lock','Tools/Native/OodleLoader/Cargo.toml','Tools/Native/OodleLoader/src/lib.rs')
$record=[ordered]@{
    RecordedUtc=[DateTime]::UtcNow.ToString('o')
    RetocSource='https://github.com/Loomirr/retoc-oodle/tree/a3d3c84f030118c36896568e35e0dcea6305e2b9'
    CueSource='https://github.com/FabianFG/CUE4Parse/tree/ecad882a3049df6f27e0c5c3a3531346305c010b'
    AclSource='https://github.com/nfrechette/acl/tree/414689d5cff4286a7898487a46dc5e48005d38da'
    RtmSource='https://github.com/nfrechette/rtm/tree/d7982f2b2524feeb322f424b29cf43df30b5d5b7'
    Rustc=((& rustc --version) -join '')
    BuildRecipe='Tools/Native/Build-NativeHelpers.ps1; pinned sources/lockfile, local-only loader; CUE ACL + RTM only, MSVC static CRT'
    Patches=@($patches|ForEach-Object {[ordered]@{Path=$_;Sha256=(Get-FileHash (Join-Path $repo $_)).Hash.ToLower()}})
    Binaries=@([ordered]@{Path='Tools/retoc-oodle/retoc.exe';Sha256=(Get-FileHash $RetocBinary).Hash.ToLower()},[ordered]@{Path='CUE4Parse-Natives.dll';Sha256=(Get-FileHash $AclBinary).Hash.ToLower()})
    Note='Source-pinned/rebuildable, not a promise of bit-identical output across toolchain versions. No Oodle runtime/source included. Distribution terms for the separate Unreal writer remain outside this record.'
}
[IO.File]::WriteAllText((Join-Path $repo 'licenses/native/build-provenance.txt'),(ConvertTo-Json $record -Depth 7))
