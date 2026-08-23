[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedPdbName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$resolvedDestination = [IO.Path]::GetFullPath($DestinationPath)
if ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedSource, $resolvedDestination)) {
    throw 'The sanitized dependency must be written to a private copy.'
}

[byte[]]$sourceBytes = [IO.File]::ReadAllBytes($resolvedSource)
$binaryText = [Text.Encoding]::ASCII.GetString($sourceBytes)
$replacementBytes = [Text.Encoding]::ASCII.GetBytes($ExpectedPdbName)
$matchingRecords = 0
$scanOffset = 0

while ($scanOffset -le $binaryText.Length - 24) {
    $recordOffset = $binaryText.IndexOf('RSDS', $scanOffset, [StringComparison]::Ordinal)
    if ($recordOffset -lt 0) {
        break
    }

    $pathOffset = $recordOffset + 24 # RSDS + GUID + age
    $pathEnd = $binaryText.IndexOf([char]0, $pathOffset)
    if ($pathEnd -gt $pathOffset -and $pathEnd - $pathOffset -le 4096) {
        $embeddedPath = $binaryText.Substring($pathOffset, $pathEnd - $pathOffset)
        $embeddedName = [IO.Path]::GetFileName($embeddedPath.Replace('/', '\'))
        if ([StringComparer]::OrdinalIgnoreCase.Equals($embeddedName, $ExpectedPdbName)) {
            $availableBytes = $pathEnd - $pathOffset + 1
            if ($replacementBytes.Length + 1 -gt $availableBytes) {
                throw 'The replacement PDB name does not fit the existing CodeView record.'
            }

            [Array]::Clear($sourceBytes, $pathOffset, $availableBytes)
            [Array]::Copy($replacementBytes, 0, $sourceBytes, $pathOffset, $replacementBytes.Length)
            $matchingRecords++
        }
    }

    $scanOffset = $recordOffset + 4
}

if ($matchingRecords -ne 1) {
    throw "Expected exactly one CodeView record for '$ExpectedPdbName'; found $matchingRecords."
}

$destinationDirectory = [IO.Path]::GetDirectoryName($resolvedDestination)
[IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
[IO.File]::WriteAllBytes($resolvedDestination, $sourceBytes)
Write-Host "Sanitized CodeView path for $ExpectedPdbName."
