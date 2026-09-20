param([string]$NativeBuild = (Join-Path $PSScriptRoot '../../../../build-ninja'))
$ErrorActionPreference = 'Stop'
$bcBuild = (Resolve-Path $NativeBuild).Path
$bcWorkspace = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$bcOutput = Join-Path $bcWorkspace 'artifacts/ModeAccess_20260919/Native'
New-Item -ItemType Directory -Path $bcOutput -Force | Out-Null
# Reuse the exact ABI flags and libraries of this machine's existing UE4SS build.
# No CMake reconfiguration, external source edits, live-game copying or install target.
$bcNinja = Get-Content -Raw -LiteralPath (Join-Path $bcBuild 'build.ninja')
$bcCompile = [regex]::Match($bcNinja, '(?ms)^build NewSuitSlotNative\\CMakeFiles\\LOTDKExpanded.dir\\dllmain.cpp.obj:.*?(?=^\s*$)').Value
$bcLink = [regex]::Match($bcNinja, '(?ms)^build NewSuitSlotNative\\LOTDKExpanded.dll .*?(?=^\s*$)').Value
function Read-BuildValue([string]$block,[string]$name) {
    $bcMatch = [regex]::Match($block, '(?m)^  ' + $name + ' = (.+)$')
    if (!$bcMatch.Success) { throw "Missing native ABI setting: $name" }
    return $bcMatch.Groups[1].Value.Trim()
}
if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $bcVs = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat'
    $bcEnvironment = & cmd.exe /d /c ('call "' + $bcVs + '" -no_logo -arch=x64 -host_arch=x64 && set')
    $bcCompilerPath = $null
    foreach ($bcLine in $bcEnvironment) { if ($bcLine -match '^([^=]+)=(.*)$') {
        if ($matches[1] -ieq 'Path' -and $matches[2].Contains('MSVC')) { $bcCompilerPath = $matches[2] }
        [Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process')
    } }
    if ($bcCompilerPath) { $env:Path = $bcCompilerPath }
    if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw "Visual Studio environment setup did not expose cl.exe ($($bcEnvironment.Count) output lines)." }
}
$bcDefines = (Read-BuildValue $bcCompile 'DEFINES').Replace('LOTDKExpanded_EXPORTS','LOTDKModeAccess_EXPORTS').Replace('\"','"')
$bcIncludes = Read-BuildValue $bcCompile 'INCLUDES'
$bcFlags = (Read-BuildValue $bcCompile 'FLAGS') -replace '/Zi',''
$bcLibs = (Read-BuildValue $bcLink 'LINK_LIBRARIES') -split '\s+' | ForEach-Object { if ($_ -match '\\') { '"' + (Join-Path $bcBuild $_) + '"' } else { $_ } }
$bcRsp = Join-Path $bcOutput 'build.rsp'
$bcArguments = "$bcDefines`n$bcIncludes`n$bcFlags`n/c /nologo`n/Fo`"$bcOutput/main.obj`"`n`"$PSScriptRoot/main.cpp`""
[IO.File]::WriteAllText($bcRsp,$bcArguments)
& cl.exe "@$bcRsp"
if ($LASTEXITCODE -ne 0) { throw "Mode Access native build failed ($LASTEXITCODE)" }
$bcLinkRsp = Join-Path $bcOutput 'link.rsp'
[IO.File]::WriteAllText($bcLinkRsp,"/DLL /NOLOGO /MACHINE:X64 /INCREMENTAL:NO /OPT:REF /OPT:ICF`n/OUT:`"$bcOutput/main.dll`"`n/IMPLIB:`"$bcOutput/main.lib`"`n`"$bcOutput/main.obj`"`n" + ($bcLibs -join ' '))
& link.exe "@$bcLinkRsp"
if ($LASTEXITCODE -ne 0) { throw "Mode Access native link failed ($LASTEXITCODE)" }
$bcData = Join-Path $bcWorkspace 'Data/ModeAccess'
New-Item -ItemType Directory -Path $bcData -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bcOutput 'main.dll') -Destination (Join-Path $bcData 'main.dll')
Copy-Item -LiteralPath (Join-Path $bcWorkspace 'LICENSE') -Destination (Join-Path $bcData 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CharacterUserGuide.md') -Destination (Join-Path $bcData 'README.md')
$bcLicenseFiles = @((Join-Path $bcWorkspace '../../RE-UE4SS/LICENSE'))
foreach ($bcLib in @('ASMHelper','SinglePassSigScanner','LuaMadeSimple','LuaRaw','IniParser','JSON','ParserBase','Input','DynamicOutput','File','Helpers')) {
    $bcLicenseFiles += Join-Path $bcWorkspace "../../RE-UE4SS/deps/first/$bcLib/LICENSE"
}
foreach ($bcLib in @('polyhook2-src/LICENSE','polyhook2-src/asmjit/LICENSE.md','polyhook2-src/asmtk/LICENSE.md','polyhook2-src/zydis/LICENSE','polyhook2-src/zydis/dependencies/zycore/LICENSE','zydis-src/LICENSE','zydis-src/dependencies/zycore/LICENSE','fmt-src/LICENSE','imgui-src/LICENSE.txt','glfw-src/LICENSE.md')) {
    $bcLicenseFiles += Join-Path $bcBuild "_deps/$bcLib"
}
$bcNotices = foreach ($bcLicense in $bcLicenseFiles) {
    if (!(Test-Path -LiteralPath $bcLicense)) { throw "Missing redistribution notice: $bcLicense" }
    $bcLabel = $bcLicense -replace '^.*[\\/](RE-UE4SS|_deps)[\\/]', '$1/'
    "`n--- $bcLabel ---`n" + [IO.File]::ReadAllText($bcLicense)
}
[IO.File]::WriteAllText((Join-Path $bcData 'THIRD-PARTY-NOTICES.txt'),($bcNotices -join "`n"))
& cl.exe /nologo /std:c++20 /EHsc /MD "/Fo$bcOutput/policy_tests.obj" "/Fe$bcOutput/policy_tests.exe" "$PSScriptRoot/policy_tests.cpp"
if ($LASTEXITCODE -ne 0) { throw 'Policy test build failed' }
& (Join-Path $bcOutput 'policy_tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Policy assertions failed' }
& cl.exe /nologo /std:c++20 /EHsc /MD "/Fo$bcOutput/callsite_tests.obj" "/Fe$bcOutput/callsite_tests.exe" "$PSScriptRoot/callsite_tests.cpp"
if ($LASTEXITCODE -ne 0) { throw 'Call-site test build failed' }
& (Join-Path $bcOutput 'callsite_tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Call-site isolation/rollback assertions failed' }
$bcLuaInclude = Join-Path $bcWorkspace '../../RE-UE4SS/deps/first/LuaRaw/include'
$bcLuaLib = Join-Path $bcBuild 'Game__Shipping__Win64/lib/LuaRaw.lib'
& cl.exe /nologo /std:c++20 /EHsc /MD "/I$bcLuaInclude" "/Fo$bcOutput/lua_tests.obj" "/Fe$bcOutput/lua_tests.exe" "$PSScriptRoot/lua_tests.cpp" "$bcLuaLib"
if ($LASTEXITCODE -ne 0) { throw 'Lua scenario test build failed' }
& (Join-Path $bcOutput 'lua_tests.exe') (Join-Path $PSScriptRoot '../Lua/LOTDKJokerHarleyNormal/Scripts/main.lua') (Join-Path $PSScriptRoot '../Lua/LOTDKHeroesMayhem/Scripts/main.lua')
if ($LASTEXITCODE -ne 0) { throw 'Lua scenarios failed' }
Write-Output "Built helper only: $bcData/main.dll (not installed)"
