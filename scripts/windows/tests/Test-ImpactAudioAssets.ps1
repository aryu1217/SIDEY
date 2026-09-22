#requires -Version 5.1

[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssetsDirectory)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$assetDirectoryPath = (Resolve-Path -LiteralPath $AssetsDirectory).Path
$impactAudioDirectory = Join-Path $assetDirectoryPath 'Impacts'
if (Test-Path -LiteralPath (Join-Path $assetDirectoryPath 'Audio/Impacts')) { throw 'Obsolete Audio/Impacts directory must not be deployed.' }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $impactAudioDirectory 'manifest.json') | ConvertFrom-Json
if ($manifest.Count -ne 19 -or @($manifest.id | Select-Object -Unique).Count -ne 19) { throw 'Expected nineteen unique approved impact sounds.' }
foreach ($sound in $manifest) {
    if ($sound.id -notmatch '^[a-z_]+$' -or $sound.file -cne ($sound.id + '/' + $sound.id + '.wav')) { throw 'Invalid impact filename.' }
    $audioFilePath = Join-Path $impactAudioDirectory $sound.file
    if ((Get-FileHash -LiteralPath $audioFilePath -Algorithm SHA256).Hash -ne $sound.sha256) { throw "Impact hash mismatch: $($sound.id)" }
    $bytes = [IO.File]::ReadAllBytes($audioFilePath)
    if ($bytes.Length -lt 44 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -cne 'RIFF' -or [Text.Encoding]::ASCII.GetString($bytes, 8, 4) -cne 'WAVE') { throw 'Invalid WAV asset.' }
}
if (@(Get-ChildItem -LiteralPath $impactAudioDirectory -Filter '*.wav' -File -Recurse).Count -ne 19) { throw 'Unexpected impact audio files.' }
Write-Output 'Verified nineteen approved impact WAV files.'
