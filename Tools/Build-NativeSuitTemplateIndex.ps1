param(
    [Parameter(Mandatory = $true)]
    [string]$ExtractedContentRoot,
    [string]$JsonExportContentRoot,
    [string]$OutputRoot,
    [switch]$NoJsonExportEnrichment,
    [switch]$AllowExternalOutputRoot
)

$ErrorActionPreference = "Stop"

function Require-Directory([string]$Path, [string]$Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Message`nMissing: $Path"
    }
}

function Convert-ToPackagePath([string]$ContentRoot, [string]$UassetPath) {
    $contentRootFull = [System.IO.Path]::GetFullPath($ContentRoot).TrimEnd('\')
    $fileFull = [System.IO.Path]::GetFullPath($UassetPath)
    if (-not $fileFull.StartsWith($contentRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File is not under Content root.`nRoot: $contentRootFull`nFile: $fileFull"
    }

    $relative = $fileFull.Substring($contentRootFull.Length).TrimStart('\')
    $withoutExtension = if ($relative.EndsWith(".uasset", [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative.Substring(0, $relative.Length - ".uasset".Length)
    }
    else {
        [System.IO.Path]::ChangeExtension($relative, $null).TrimEnd('.')
    }
    return "/Game/" + ($withoutExtension -replace '\\', '/')
}

function Convert-PackagePathToContentRelative([string]$PackagePath) {
    if (-not $PackagePath.StartsWith("/Game/", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Only /Game package paths are supported. Got: $PackagePath"
    }

    return $PackagePath.Substring(6).Replace("/", "\")
}

function Read-BinaryTextForSearch([string[]]$Paths) {
    $builder = New-Object System.Text.StringBuilder
    $encoding = [System.Text.Encoding]::GetEncoding(28591)

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($path)
        [void]$builder.Append($encoding.GetString($bytes))
        [void]$builder.Append("`n")
    }

    return $builder.ToString()
}

function Get-RoleFromStem([string]$Stem) {
    if ($Stem -like "DA_DCMD_*") {
        return "dcmd"
    }
    if ($Stem -like "DA_UIMD_*") {
        return "uimd"
    }
    if ($Stem -like "DA_DPRD_*") {
        return "dprd"
    }
    if ($Stem -like "DA_TtInteractFilter*") {
        return "interact_filter"
    }
    if ($Stem -like "BP_CAT_Archetype*") {
        return "archetype"
    }
    if ($Stem -like "AS_*") {
        return "anim_set"
    }
    if ($Stem -like "MI_*" -or $Stem -like "M_*") {
        return "material"
    }
    if ($Stem -like "BP_*") {
        if ($Stem -match "(?i)(_Cutscene$|_Default_Cutscene$|_Costume_Cutscene$|_NoCowl_Cutscene$|_Shirtless_Cutscene$|_CUT$|Cutscene$)") {
            return "cutscene"
        }
        if ($Stem -match "(?i)Batcave") {
            return "batcave"
        }
        return "playable_like"
    }

    return "other"
}

function Get-TemplateKey([string]$Character, [string]$Stem, [string]$Role) {
    $key = $Stem
    $key = $key -replace '^BP_', ''
    $key = $key -replace '^DA_DCMD_', ''
    $key = $key -replace '^DA_UIMD_', ''
    $key = $key -replace '_Playable$', ''
    $key = $key -replace '_Default_Cutscene$', ''
    $key = $key -replace '_Costume_Cutscene$', ''
    $key = $key -replace '_NoCowl_Cutscene$', ''
    $key = $key -replace '_Shirtless_Cutscene$', ''
    $key = $key -replace '_Cutscene$', ''
    $key = $key -replace '_CUT$', ''
    $key = $key -replace '_Batcave$', ''
    return "$Character/$key"
}

function Test-ContainsAny([string]$Text, [string[]]$Needles) {
    foreach ($needle in $Needles) {
        if ($Text.Contains($needle)) {
            return $true
        }
    }
    return $false
}

function New-FeatureObject([string]$SearchText, [string]$JsonText) {
    $combined = "$SearchText`n$JsonText"
    return [pscustomobject]@{
        has_torso2 = (Test-ContainsAny $combined @("Torso2", "TtCharacterAsset.Torso2"))
        has_batman_absolute_torso = (Test-ContainsAny $combined @("TorsoA_BatmanAbsolute", "SK_TorsoA_BatmanAbsolute", "MI_TorsoA_BatmanAbsolute_EoM"))
        has_static_mesh_component = (Test-ContainsAny $combined @("StaticMeshComponent"))
        has_skeletal_mesh_budgeted = (Test-ContainsAny $combined @("SkeletalMeshComponentBudgeted"))
        has_headstud_socket = (Test-ContainsAny $combined @("HeadStud_Attach_Socket"))
        has_chest_socket = (Test-ContainsAny $combined @("Chest_Socket"))
        has_slickback = (Test-ContainsAny $combined @("SlickBack", "SM_HAIR_SlickBack", "MI_HAIR_SlickBack"))
        has_any_hair = (Test-ContainsAny $combined @("SM_HAIR", "/Hair/", "TtCharacterAsset.Hair"))
        has_head_asset_tag = (Test-ContainsAny $combined @("TtCharacterAsset.Head"))
        has_face_asset_tag = (Test-ContainsAny $combined @("TtCharacterAsset.Face"))
        has_cape_asset_tag = (Test-ContainsAny $combined @("TtCharacterAsset.Cape"))
        has_dcmd_soft_paths = (Test-ContainsAny $combined @("CinematicsActor", "MenuActor", "PawnTag", "ProgressTag"))
        has_equipment_strings = (Test-ContainsAny $combined @("DA_ETA_", "Equipment", "Batarang", "BatClaw", "NinjaStar", "FoamGun"))
        has_ninjastar = (Test-ContainsAny $combined @("NinjaStar", "DA_ETA_NinjaStar"))
        has_foamgun = (Test-ContainsAny $combined @("FoamGun", "DA_ETA_FoamGun"))
    }
}

function Get-FeatureScore([string]$Role, [object]$Features, [bool]$HasPair, [bool]$HasDcmd) {
    $score = 0
    if ($Role -eq "playable_like") { $score += 20 }
    if ($Role -eq "cutscene") { $score += 12 }
    if ($Role -eq "dcmd") { $score += 10 }
    if ($HasPair) { $score += 20 }
    if ($HasDcmd) { $score += 8 }
    if ($Features.has_torso2) { $score += 18 }
    if ($Features.has_batman_absolute_torso) { $score += 20 }
    if ($Features.has_static_mesh_component) { $score += 8 }
    if ($Features.has_skeletal_mesh_budgeted) { $score += 4 }
    if ($Features.has_headstud_socket) { $score += 5 }
    if ($Features.has_chest_socket) { $score += 5 }
    if ($Features.has_slickback) { $score += 16 }
    elseif ($Features.has_any_hair) { $score += 10 }
    if ($Features.has_equipment_strings) { $score += 6 }
    if ($Features.has_ninjastar) { $score += 4 }
    if ($Features.has_foamgun) { $score += 4 }
    return $score
}

$scriptDir = Split-Path -Parent $PSCommandPath
$projectRoot = Resolve-Path (Join-Path $scriptDir "..")

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot "_generated\NativeSuitTemplates"
}

$resolvedProjectRoot = (Resolve-Path -LiteralPath $projectRoot).Path
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if ((-not $AllowExternalOutputRoot) -and (-not $resolvedOutputRoot.StartsWith($resolvedProjectRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to write outside the project root. OutputRoot must stay under: $resolvedProjectRoot`nGot: $resolvedOutputRoot"
}

Require-Directory $ExtractedContentRoot "Extracted Content root was not found."
$minifigRoot = Join-Path $ExtractedContentRoot "Characters\Minifig"
Require-Directory $minifigRoot "Extracted Minifig root was not found."

$useJson = (-not $NoJsonExportEnrichment) -and
    (-not [string]::IsNullOrWhiteSpace($JsonExportContentRoot)) -and
    (Test-Path -LiteralPath $JsonExportContentRoot -PathType Container)

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$records = @()
$uassetFiles = Get-ChildItem -LiteralPath $minifigRoot -Recurse -File -Filter "*.uasset"

foreach ($uasset in $uassetFiles) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($uasset.Name)
    $uexp = [System.IO.Path]::ChangeExtension($uasset.FullName, ".uexp")
    $ubulk = [System.IO.Path]::ChangeExtension($uasset.FullName, ".ubulk")
    $packagePath = Convert-ToPackagePath $ExtractedContentRoot $uasset.FullName
    $contentRelative = Convert-PackagePathToContentRelative $packagePath
    $character = $uasset.DirectoryName.Substring($minifigRoot.Length).TrimStart('\').Split('\')[0]
    $role = Get-RoleFromStem $stem
    $templateKey = Get-TemplateKey $character $stem $role

    $searchFiles = @($uasset.FullName)
    if (Test-Path -LiteralPath $uexp -PathType Leaf) {
        $searchFiles += $uexp
    }
    if (Test-Path -LiteralPath $ubulk -PathType Leaf) {
        $searchFiles += $ubulk
    }

    $jsonPath = $null
    $jsonText = ""
    if ($useJson) {
        $candidateJson = Join-Path $JsonExportContentRoot "$contentRelative.json"
        if (Test-Path -LiteralPath $candidateJson -PathType Leaf) {
            $jsonPath = $candidateJson
            $jsonText = Get-Content -LiteralPath $candidateJson -Raw
        }
    }

    $searchText = Read-BinaryTextForSearch $searchFiles
    $features = New-FeatureObject $searchText $jsonText

    $records += [pscustomobject]@{
        package_path = $packagePath
        content_relative = $contentRelative
        stem = $stem
        character = $character
        role = $role
        template_key = $templateKey
        uasset = $uasset.FullName
        uexp = if (Test-Path -LiteralPath $uexp -PathType Leaf) { $uexp } else { $null }
        ubulk = if (Test-Path -LiteralPath $ubulk -PathType Leaf) { $ubulk } else { $null }
        json_export = $jsonPath
        uasset_length = $uasset.Length
        uexp_length = if (Test-Path -LiteralPath $uexp -PathType Leaf) { (Get-Item -LiteralPath $uexp).Length } else { 0 }
        has_split_pair = (Test-Path -LiteralPath $uexp -PathType Leaf)
        features = $features
        has_pair = $false
        has_dcmd = $false
        score = 0
    }
}

$recordsByKey = @{}
foreach ($record in $records) {
    if (-not $recordsByKey.ContainsKey($record.template_key)) {
        $recordsByKey[$record.template_key] = @()
    }
    $recordsByKey[$record.template_key] += $record
}

$groups = @()
foreach ($key in ($recordsByKey.Keys | Sort-Object)) {
    $items = @($recordsByKey[$key])
    $playable = $items | Where-Object { $_.role -eq "playable_like" } | Select-Object -First 1
    $cutscene = $items | Where-Object { $_.role -eq "cutscene" } | Select-Object -First 1
    $dcmd = $items | Where-Object { $_.role -eq "dcmd" } | Select-Object -First 1

    foreach ($item in $items) {
        $item.has_pair = [bool](($item.role -eq "playable_like" -and $cutscene) -or ($item.role -eq "cutscene" -and $playable))
        $item.has_dcmd = [bool]$dcmd
        $item.score = Get-FeatureScore $item.role $item.features $item.has_pair $item.has_dcmd
    }

    $groups += [pscustomobject]@{
        template_key = $key
        package_count = $items.Count
        playable = if ($playable) { $playable.package_path } else { $null }
        cutscene = if ($cutscene) { $cutscene.package_path } else { $null }
        dcmd = if ($dcmd) { $dcmd.package_path } else { $null }
        max_score = (@($items | Measure-Object -Property score -Maximum).Maximum)
        has_torso2 = [bool](@($items | Where-Object { $_.features.has_torso2 }).Count)
        has_batman_absolute_torso = [bool](@($items | Where-Object { $_.features.has_batman_absolute_torso }).Count)
        has_static_mesh_component = [bool](@($items | Where-Object { $_.features.has_static_mesh_component }).Count)
        has_any_hair = [bool](@($items | Where-Object { $_.features.has_any_hair }).Count)
        has_slickback = [bool](@($items | Where-Object { $_.features.has_slickback }).Count)
    }
}

$absolutePlayable = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/Batman/BP_Batman_Absolute_Playable" } | Select-Object -First 1
$absoluteCutscene = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/Batman/BP_Batman_Absolute_Cutscene" } | Select-Object -First 1
$absoluteDcmd = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/Batman/DA_DCMD_Batman_Absolute_Playable" } | Select-Object -First 1
$thomasPlayable = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/ThomasWayne/BP_ThomasWayne_Casual" } | Select-Object -First 1
$thomasCut = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/ThomasWayne/BP_ThomasWayne_Casual_CUT" } | Select-Object -First 1
$thomasDcmd = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/ThomasWayne/DA_DCMD_ThomasWayne_Casual" } | Select-Object -First 1
$riddlerCutscene = $records | Where-Object { $_.package_path -eq "/Game/Characters/Minifig/Riddler/BP_Riddler_Cutscene" } | Select-Object -First 1

$recommendedThomasPlan = [pscustomobject]@{
    slot_id = "batman_thomas"
    intent = "First template-patching donor plan for generated native Thomas."
    playable_donor = $absolutePlayable
    cutscene_donor = $absoluteCutscene
    dcmd_donor = $absoluteDcmd
    thomas_source = $thomasPlayable
    thomas_cutscene_source = $thomasCut
    thomas_dcmd_source = $thomasDcmd
    static_mesh_component_shape_donor = $riddlerCutscene
    target_packages = [pscustomobject]@{
        playable = "/Game/Mods/Batman_Thomas/Characters/BP_Batman_Thomas_Playable"
        cutscene = "/Game/Mods/Batman_Thomas/Characters/BP_Batman_Thomas_Cutscene"
        dcmd = "/Game/Mods/Batman_Thomas/Characters/DA_DCMD_Batman_Thomas_Playable"
    }
    warnings = @(
        "This plan selects donors only. It does not rename package/class/object names yet.",
        "Absolute is best for Torso2, Thomas is best for source material/metadata, and Riddler cutscene is a useful StaticMeshComponent shape donor.",
        "A real UAsset name-map/export patcher is still needed before cloned packages can become true independent generated assets."
    )
}

$recordsSorted = @($records | Sort-Object -Property @{ Expression = "score"; Descending = $true }, @{ Expression = "package_path"; Descending = $false })
$groupsSorted = @($groups | Sort-Object -Property @{ Expression = "max_score"; Descending = $true }, @{ Expression = "template_key"; Descending = $false })

$indexPath = Join-Path $OutputRoot "template-index.json"
$playablePath = Join-Path $OutputRoot "playable-candidates.json"
$cutscenePath = Join-Path $OutputRoot "cutscene-candidates.json"
$groupPath = Join-Path $OutputRoot "template-groups.json"
$planPath = Join-Path $OutputRoot "recommended-thomas-template-plan.json"
$legacyReportPath = Join-Path $OutputRoot "TemplateIndex_Report.md"

$recordsSorted | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $indexPath -Encoding UTF8
@($recordsSorted | Where-Object { $_.role -eq "playable_like" }) | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $playablePath -Encoding UTF8
@($recordsSorted | Where-Object { $_.role -eq "cutscene" }) | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $cutscenePath -Encoding UTF8
$groupsSorted | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $groupPath -Encoding UTF8
$recommendedThomasPlan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $planPath -Encoding UTF8
if (Test-Path -LiteralPath $legacyReportPath -PathType Leaf) {
    Remove-Item -LiteralPath $legacyReportPath -Force
}

Write-Host "Native suit template index output:"
Write-Host "  $OutputRoot"
Write-Host ""
Write-Host "Important files:"
Write-Host "  $planPath"
Write-Host "  $indexPath"
Write-Host "  $playablePath"
Write-Host "  $cutscenePath"
