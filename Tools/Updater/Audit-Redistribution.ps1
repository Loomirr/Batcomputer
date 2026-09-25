param([Parameter(Mandatory)][string]$PublishDirectory,[string]$OutputDirectory='output/redistribution-audit',[switch]$CollectNotices)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assets=Get-Content (Join-Path $repo 'obj/project.assets.json') -Raw | ConvertFrom-Json
$cache=$assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$packages=@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
$config=Join-Path $publish 'app/Batcomputer.runtimeconfig.json'
if(-not(Test-Path $config)){$config=Join-Path $publish 'Batcomputer.runtimeconfig.json'}
if(Test-Path $config){foreach($framework in (Get-Content $config -Raw|ConvertFrom-Json).runtimeOptions.includedFrameworks){$packages+=($framework.name.ToLower()+'.runtime.win-x64/'+$framework.version)}}
$byHash=@{};$packageRecords=@()
foreach($package in ($packages|Sort-Object -Unique)){
    $dir=Join-Path $cache $package
    $spec=Get-ChildItem -LiteralPath $dir -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$xml=Get-Content -LiteralPath $spec.FullName
    $meta=$xml.package.metadata
    $notices=@(Get-ChildItem -LiteralPath $dir -File | Where-Object Name -Match '(?i)license|notice|copying|copyright')
    $id=$package.Replace('/','-');$collected=@()
    foreach($notice in $notices){
        # Preserve exact upstream text; use portable .txt paths in the distribution.
        $relative='licenses/dependencies/'+$id+'/'+$notice.Name+'.txt'
        if($CollectNotices){$dest=Join-Path $repo $relative;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force|Out-Null;Copy-Item -LiteralPath $notice.FullName -Destination $dest}
        $collected+=$relative
    }
    $packageRecords += [pscustomobject]@{Package=$package;License=$meta.license.InnerText;LicenseType=$meta.license.type;LicenseUrl=$meta.licenseUrl;Repository=$meta.repository.url;Commit=$meta.repository.commit;Project=$meta.projectUrl;Notices=$collected}
    foreach($file in (Get-ChildItem -LiteralPath $dir -File -Recurse | Where-Object Extension -In '.dll','.exe')){
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if(-not$byHash.ContainsKey($hash)){$byHash[$hash]=@()}
        $byHash[$hash]+=[pscustomobject]@{Package=$package;Source=[IO.Path]::GetRelativePath($dir,$file.FullName).Replace('\','/')}
    }
}
$rows=@();$blocked=@()
$proofFile=Join-Path $repo 'licenses/native/build-provenance.txt'
$nativeProof=$null
if(Test-Path $proofFile){
    $candidate=Get-Content $proofFile -Raw|ConvertFrom-Json
    $valid=$true
    foreach($patch in $candidate.Patches){if(!(Test-Path (Join-Path $repo $patch.Path)) -or (Get-FileHash (Join-Path $repo $patch.Path)).Hash -ne $patch.Sha256){$valid=$false}}
    if($valid){$nativeProof=$candidate}
}
foreach($file in (Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object Extension -In '.dll','.exe')){
    $relative=[IO.Path]::GetRelativePath($publish,$file.FullName).Replace('\','/')
    if($relative.StartsWith('.batcomputer-updates/')){continue}
    $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $origins=@();$status='UNMATCHED: provenance review required';$sourceBuilt=$false
    if($byHash.ContainsKey($hash)){$origins=$byHash[$hash];$status='Package hash matched; review its license and notice obligations'}
    elseif($relative -in 'Batcomputer.exe','app/Batcomputer.dll','Batcomputer.dll'){$status='Batcomputer build output (MIT); launcher also contains .NET apphost code'}
    elseif($relative -match '(^|/)CUE4Parse-Natives.dll$'){$status='Locally supplied native helper: exact source/build provenance and embedded dependency review required'}
    elseif($relative -eq 'Tools/retoc-oodle/retoc.exe'){$status='Locally built retoc fork: Cargo dependency/license and build-provenance review required'}
    $proofPath=if($relative -eq 'app/CUE4Parse-Natives.dll'){'CUE4Parse-Natives.dll'}else{$relative}
    if($nativeProof -and @($nativeProof.Binaries|Where-Object {$_.Path -eq $proofPath -and $_.Sha256 -eq $hash}).Count){$sourceBuilt=$true;$status='Matches recorded source-pinned helper build and current patch/lockfile hashes; see native notices and acceptance report'}
    if($relative -eq 'Tools/BatcomputerRegistryWriter/Prebuilt/Win64/UnrealEditor-BatcomputerRegistryWriter.dll'){$status='Release owner retains authored prebuilt writer; distribution terms remain a documented review item'}
    elseif($file.Name -match '^(oo2core.*\.dll|oodle-data-shared\.dll|liboodle.*\.(so|dylib)|LOTDKExpanded\.dll|UnrealEditor.*\.(dll|exe))$'){$status='HOLD: redistribution review required (game/Oodle runtime or Unreal editor binary)';$blocked+=$relative}
    $rows+=[pscustomobject]@{Path=$relative;Size=$file.Length;Sha256=$hash.ToLower();Origins=$origins;SourceBuilt=$sourceBuilt;Status=$status}
}
[IO.File]::WriteAllText((Join-Path $output 'binary-inventory.json'),(ConvertTo-Json -InputObject $rows -Depth 8))
[IO.File]::WriteAllText((Join-Path $output 'package-licenses.json'),(ConvertTo-Json -InputObject $packageRecords -Depth 8))
$used=@($rows|ForEach-Object {$_.Origins}|ForEach-Object {$_.Package}|Sort-Object -Unique)
$missing=@($packageRecords|Where-Object {$_.Package -in $used -and $_.Notices.Count -eq 0})
$coverage=@(foreach($package in $used){
    $noticeDir=Join-Path $repo ('licenses/dependencies/'+$package.Replace('/','-'))
    $texts=@(Get-ChildItem -LiteralPath $noticeDir -File -ErrorAction SilentlyContinue|Where-Object {$_.Name -match '(?i)license|licence|notice|copying|copyright|apache' -and $_.Name -ne 'upstream-sources.txt'})
    [pscustomobject]@{Package=$package;CollectedTexts=@($texts|ForEach-Object {[IO.Path]::GetRelativePath($repo,$_.FullName).Replace('\','/')})}
})
[IO.File]::WriteAllText((Join-Path $output 'notice-coverage.json'),(ConvertTo-Json -InputObject $coverage -Depth 6))
$summary=[pscustomobject]@{Binaries=$rows.Count;Matched=@($rows|Where-Object {$_.Origins.Count -gt 0}).Count;SourceBuilt=@($rows|Where-Object SourceBuilt).Count;UsedPackages=$used;MissingLocalLicenseTexts=$missing.Package;PackagesWithCollectedTexts=@($coverage|Where-Object {$_.CollectedTexts.Count -gt 0}).Count;MissingCollectedTexts=@($coverage|Where-Object {$_.CollectedTexts.Count -eq 0}|ForEach-Object {$_.Package});ManualReview=@($rows|Where-Object {$_.Origins.Count -eq 0 -and !$_.SourceBuilt -and $_.Path -notin 'Batcomputer.exe','Batcomputer.dll','app/Batcomputer.dll'}|Select-Object Path,Status);Blocked=$blocked;LegalClearance='NOT CERTIFIED: package metadata/hash matches and notice presence do not establish every license obligation or embedded native component'}
[IO.File]::WriteAllText((Join-Path $output 'summary.json'),(ConvertTo-Json $summary -Depth 8))
$summary|ConvertTo-Json -Depth 8
if($blocked.Count){exit 1}
