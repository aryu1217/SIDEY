#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$candidatePath = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$manifestPath = Join-Path $candidatePath 'windows-release-candidate.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Windows release candidate manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.releaseVersion
if ([int]$manifest.schema -ne 1 -or
    $version -notmatch '^\d+\.\d+\.\d+$' -or
    [string]$manifest.productVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [string]$manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Windows release candidate metadata is invalid.'
}

$expectedNames = @(foreach ($architecture in @('x64', 'arm64')) {
    foreach ($suffix in @('Setup.exe', 'Update.exe', 'Update.json', 'Payload.bin')) {
        "SIDEY-Windows-$architecture-v$version-$suffix"
    }
})
$actualNames = @(Get-ChildItem -LiteralPath $candidatePath -File | ForEach-Object Name)
$expectedDirectoryNames = @($expectedNames) + @('windows-release-candidate.json')
if ($actualNames.Count -ne $expectedDirectoryNames.Count -or
    @(Compare-Object $expectedDirectoryNames $actualNames -CaseSensitive).Count -ne 0) {
    throw 'Candidate directory must contain exactly the eight x64 and ARM64 assets and its manifest.'
}

$recordedFiles = @($manifest.files)
$recordedNames = @($recordedFiles | ForEach-Object { [string]$_.name })
if ($recordedNames.Count -ne 8 -or
    @(Compare-Object $expectedNames $recordedNames -CaseSensitive).Count -ne 0) {
    throw 'Candidate manifest must record exactly the eight x64 and ARM64 assets.'
}

$hashes = @{}
foreach ($record in $recordedFiles) {
    $name = [string]$record.name
    $path = Join-Path $candidatePath $name
    $size = (Get-Item -LiteralPath $path).Length
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$record.sha256 -cne $hash -or [long]$record.size -ne $size -or $size -le 0) {
        throw "Candidate asset size or SHA-256 differs from its manifest: $name"
    }
    $hashes[$name] = $hash
}

foreach ($architecture in @('x64', 'arm64')) {
    $prefix = "SIDEY-Windows-$architecture-v$version-"
    $descriptorPath = Join-Path $candidatePath "${prefix}Update.json"
    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$descriptor.version -cne $version) {
        throw "Update descriptor version differs from the candidate: $architecture"
    }
    foreach ($part in @(@('payload', 'Payload.bin'), @('updater', 'Update.exe'))) {
        $record = $descriptor.($part[0])
        $assetName = "$prefix$($part[1])"
        $assetPath = Join-Path $candidatePath $assetName
        if ([string]$record.name -cne $assetName -or
            [string]$record.sha256 -cne $hashes[$assetName] -or
            [long]$record.size -ne (Get-Item -LiteralPath $assetPath).Length) {
            throw "Update descriptor $($part[0]) does not match the $architecture candidate asset."
        }
    }
}

Write-Host "WindowsReleaseCandidateVerified=true"
Write-Host "ReleaseVersion=$version"
Write-Host "SourceCommit=$($manifest.sourceCommit)"
Write-Host 'Architectures=x64,arm64'
