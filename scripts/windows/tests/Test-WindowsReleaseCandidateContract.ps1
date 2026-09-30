#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$candidatePath = Join-Path $temporaryRoot `
    "sidey-release-candidate-test-$([Guid]::NewGuid().ToString('N'))"
$version = '2.9999.1'
$sourceCommit = 'a' * 40
$utf8 = [Text.UTF8Encoding]::new($false)
$records = [Collections.Generic.List[object]]::new()
$validatorPath = Join-Path $PSScriptRoot 'Test-WindowsReleaseCandidate.ps1'

function Assert-CandidateRejected {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ExpectedMessage)

    $failure = $null
    try {
        & $validatorPath -CandidateDirectory $candidatePath
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure -or
        -not $failure.Exception.Message.Contains($ExpectedMessage)) {
        throw "Candidate was not rejected for: $ExpectedMessage"
    }
}

try {
    [IO.Directory]::CreateDirectory($candidatePath) | Out-Null
    foreach ($architecture in @('x64', 'arm64')) {
        $prefix = "SIDEY-Windows-$architecture-v$version-"
        foreach ($suffix in @('Setup.exe', 'Update.exe', 'Payload.bin')) {
            [IO.File]::WriteAllText((Join-Path $candidatePath "$prefix$suffix"),
                "$architecture $suffix", $utf8)
        }
        $descriptor = [ordered]@{
            schema = 1
            version = $version
            payload = [ordered]@{
                name = "${prefix}Payload.bin"
                sha256 = (Get-FileHash -LiteralPath (Join-Path $candidatePath "${prefix}Payload.bin") `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
                size = (Get-Item -LiteralPath (Join-Path $candidatePath "${prefix}Payload.bin")).Length
            }
            updater = [ordered]@{
                name = "${prefix}Update.exe"
                sha256 = (Get-FileHash -LiteralPath (Join-Path $candidatePath "${prefix}Update.exe") `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
                size = (Get-Item -LiteralPath (Join-Path $candidatePath "${prefix}Update.exe")).Length
            }
        }
        [IO.File]::WriteAllText((Join-Path $candidatePath "${prefix}Update.json"),
            ($descriptor | ConvertTo-Json -Depth 5), $utf8)
        foreach ($suffix in @('Setup.exe', 'Update.exe', 'Update.json', 'Payload.bin')) {
            $name = "$prefix$suffix"
            $path = Join-Path $candidatePath $name
            $records.Add([ordered]@{
                name = $name
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                size = (Get-Item -LiteralPath $path).Length
            })
        }
    }
    $manifest = [ordered]@{
        schema = 1
        sourceCommit = $sourceCommit
        productVersion = $version
        releaseVersion = $version
        files = $records.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $candidatePath 'windows-release-candidate.json'),
        ($manifest | ConvertTo-Json -Depth 5), $utf8)

    & $validatorPath -CandidateDirectory $candidatePath

    $armSetup = Join-Path $candidatePath "SIDEY-Windows-arm64-v$version-Setup.exe"
    Move-Item -LiteralPath $armSetup -Destination "$armSetup.missing"
    try {
        Assert-CandidateRejected -ExpectedMessage 'exactly the eight x64 and ARM64 assets'
    }
    finally {
        Move-Item -LiteralPath "$armSetup.missing" -Destination $armSetup
    }

    $armPayload = Join-Path $candidatePath "SIDEY-Windows-arm64-v$version-Payload.bin"
    [IO.File]::AppendAllText($armPayload, 'tampered', $utf8)
    Assert-CandidateRejected -ExpectedMessage 'Candidate asset size or SHA-256 differs'
    [IO.File]::WriteAllText($armPayload, 'arm64 Payload.bin', $utf8)

    $armDescriptor = Join-Path $candidatePath "SIDEY-Windows-arm64-v$version-Update.json"
    $originalDescriptor = [IO.File]::ReadAllText($armDescriptor, $utf8)
    $changedDescriptor = $originalDescriptor.Replace(
        "SIDEY-Windows-arm64-v$version-Payload.bin",
        "SIDEY-Windows-x64-v$version-Payload.bin")
    [IO.File]::WriteAllText($armDescriptor, $changedDescriptor, $utf8)
    $manifest.files | Where-Object { $_.name -eq [IO.Path]::GetFileName($armDescriptor) } |
        ForEach-Object {
            $_.sha256 = (Get-FileHash -LiteralPath $armDescriptor -Algorithm SHA256).Hash.ToLowerInvariant()
            $_.size = (Get-Item -LiteralPath $armDescriptor).Length
        }
    [IO.File]::WriteAllText((Join-Path $candidatePath 'windows-release-candidate.json'),
        ($manifest | ConvertTo-Json -Depth 5), $utf8)
    Assert-CandidateRejected -ExpectedMessage 'Update descriptor payload does not match'
    Write-Host 'WindowsReleaseCandidateContractTests=PASS'
}
finally {
    if (Test-Path -LiteralPath $candidatePath -PathType Container) {
        $resolvedCandidatePath = (Resolve-Path -LiteralPath $candidatePath).Path
        $temporaryPrefix = $temporaryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedCandidatePath.StartsWith($temporaryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Release contract test directory escaped the temporary root.'
        }
        Remove-Item -LiteralPath $resolvedCandidatePath -Recurse -Force
    }
}
