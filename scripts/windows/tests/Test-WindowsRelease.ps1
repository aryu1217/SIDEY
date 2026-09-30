#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$CandidateSetupPath,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
$candidateSetupFilePath = (Resolve-Path -LiteralPath $CandidateSetupPath).Path
$setupName = "SIDEY-Windows-${Architecture}-v${ReleaseVersion}-Setup.exe"
if ([IO.Path]::GetFileName($candidateSetupFilePath) -ne $setupName) {
    throw "후보 파일 이름이 공개 계약과 다름: $setupName"
}

$tag = "windows-v$ReleaseVersion"
$releaseUrl = "https://github.com/sidey-app/SIDEY/releases/download/$tag/$setupName"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "sidey-windows-release-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $downloadedSetup = Join-Path $temporaryRoot $setupName
    Invoke-WebRequest -Uri $releaseUrl -OutFile $downloadedSetup

    $downloadedHash = (Get-FileHash -LiteralPath $downloadedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    $candidateHash = (Get-FileHash -LiteralPath $candidateSetupFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $candidateHash) {
        throw 'Release Setup EXE가 CI에서 검증한 로컬 후보와 다름'
    }

    Write-Host 'ReleaseVerified=true'
    Write-Host "ReleaseVersion=$ReleaseVersion"
    Write-Host "SHA256=$downloadedHash"
    Write-Host '이 확인 뒤에만 Windows CTA와 update manifest를 별도 변경하세요.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
