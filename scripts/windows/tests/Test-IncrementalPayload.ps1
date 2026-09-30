#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$HelperPath,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Descriptor
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repositoryRootPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$probeRootPath = Join-Path ([IO.Path]::GetTempPath()) ('SIDEY incremental ' + [Guid]::NewGuid().ToString('N'))
$cachePath = Join-Path $probeRootPath 'cache'
$stagingPath = Join-Path $probeRootPath 'staging'
$rollbackPath = Join-Path $probeRootPath 'rollback'
$redirectedPath = Join-Path $probeRootPath 'redirected'

function Invoke-PayloadHelper([string]$Executable, [string]$CacheDirectory, [string]$StagingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = '--cache "' + $CacheDirectory + '" --staging "' + $StagingDirectory + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            throw 'Incremental helper timed out.'
        }
        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    foreach ($directory in @($cachePath, $stagingPath, $rollbackPath)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $rollbackMarkerPath = Join-Path $rollbackPath 'previous-install.txt'
    [IO.File]::WriteAllText($rollbackMarkerPath, 'keep previous install')
    $payloadStream = [IO.File]::OpenRead($PayloadPath)
    try {
        foreach ($file in $Descriptor.files) {
            $payloadStream.Position = [long]$file.offset
            $compressedBytes = [byte[]]::new([int]$file.length)
            $read = 0
            while ($read -lt $compressedBytes.Length) {
                $count = $payloadStream.Read($compressedBytes, $read, $compressedBytes.Length - $read)
                if ($count -eq 0) { throw 'Payload ended inside a compressed file.' }
                $read += $count
            }
            $compressedStream = [IO.MemoryStream]::new($compressedBytes, $false)
            $decompressedStream = [IO.Compression.GZipStream]::new($compressedStream, [IO.Compression.CompressionMode]::Decompress)
            $cacheFilePath = Join-Path $cachePath ($file.sha256 + '.bin')
            $outputStream = [IO.File]::Create($cacheFilePath)
            try {
                $decompressedStream.CopyTo($outputStream)
            } finally {
                $outputStream.Dispose()
                $decompressedStream.Dispose()
                $compressedStream.Dispose()
            }
            Assert-True ((Get-Item -LiteralPath $cacheFilePath).Length -eq $file.size) 'Payload size differs.'
            Assert-True ((Get-FileHash -LiteralPath $cacheFilePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $file.sha256) 'Payload hash differs.'
        }
    } finally {
        $payloadStream.Dispose()
    }
    Assert-True ((Invoke-PayloadHelper $HelperPath $cachePath $stagingPath) -eq 0) 'Complete verified cache must reconstruct the target.'
    foreach ($file in $Descriptor.files) {
        $stagedPath = Join-Path $stagingPath $file.path
        $publishedPath = if ($file.path -ceq 'Runtime/SIDEY.UninstallHelper.exe') {
            Join-Path $PublishDirectory 'Uninstall.exe'
        } else {
            Join-Path $PublishDirectory $file.path
        }
        Assert-True ((Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash) 'Reconstructed file differs from full publish.'
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $stagingPath 'Uninstall.exe'))) 'NSIS owns the generated root uninstaller.'
    Assert-True ((Invoke-PayloadHelper $HelperPath $cachePath $stagingPath) -ne 0) 'Nonempty staging must not be overwritten.'
    $failureStagingPath = Join-Path $probeRootPath 'failure-staging'
    [IO.Directory]::CreateDirectory($failureStagingPath) | Out-Null
    $firstFile = $Descriptor.files[0]
    $corruptPath = Join-Path $cachePath ($firstFile.sha256 + '.bin')
    $originalBytes = [IO.File]::ReadAllBytes($corruptPath)
    $corruptBytes = [byte[]]$originalBytes.Clone()
    if ($corruptBytes.Length -gt 0) {
        $corruptBytes[0] = $corruptBytes[0] -bxor 1
    } else {
        $corruptBytes = [byte[]]@(1)
    }
    [IO.File]::WriteAllBytes($corruptPath, $corruptBytes)
    Assert-True ((Invoke-PayloadHelper $HelperPath $cachePath $failureStagingPath) -ne 0) 'Corrupted cache must be rejected.'
    Assert-True (([IO.File]::ReadAllBytes($corruptPath))[0] -eq $corruptBytes[0]) 'Failure must leave source cache untouched.'
    Assert-True ([IO.File]::ReadAllText($rollbackMarkerPath) -ceq 'keep previous install') 'Failure must preserve rollback.'
    [IO.File]::WriteAllBytes($corruptPath, $originalBytes)

    New-Item -ItemType Junction -Path $redirectedPath -Target $cachePath | Out-Null
    $redirectStagingPath = Join-Path $probeRootPath 'redirect-staging'
    [IO.Directory]::CreateDirectory($redirectStagingPath) | Out-Null
    Assert-True ((Invoke-PayloadHelper $HelperPath $redirectedPath $redirectStagingPath) -ne 0) 'Redirected cache must be rejected.'
    [IO.Directory]::Delete($redirectedPath)

    $unsafeListPath = Join-Path $probeRootPath 'unsafe.tsv'
    [IO.File]::WriteAllText($unsafeListPath, "$($firstFile.sha256)`t$($firstFile.size)`t../escaped.exe", [Text.UTF8Encoding]::new($false))
    $unsafeHelperPath = Join-Path $probeRootPath 'UnsafePayload.exe'
    $helperVersion = (Get-Item -LiteralPath $HelperPath).VersionInfo
    & (Join-Path $repositoryRootPath 'scripts/windows/New-SideyHelperExecutable.ps1') `
        -SourcePath (Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/IncrementalPayload.cs') `
        -OutputPath $unsafeHelperPath -Version $helperVersion.ProductVersion -FileVersion $helperVersion.FileVersion `
        -Title 'SIDEY unsafe path probe' -Description 'Incremental payload path rejection probe' `
        -IconPath (Join-Path $repositoryRootPath 'windows/src/Sidey.App/Assets/Icons/SideyAppIcon.ico') `
        -ResourcePath $unsafeListPath -ResourceName 'Sidey.IncrementalFiles'
    Assert-True ((Invoke-PayloadHelper $unsafeHelperPath $cachePath $redirectStagingPath) -ne 0) 'Traversal in a target manifest must be rejected.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $probeRootPath 'escaped.exe'))) 'Traversal must not write outside staging.'
    Write-Host 'IncrementalPayload=PASS; reconstruction, corruption, rollback, nonempty staging, reparse, traversal'
} finally {
    # The test owns exactly this newly created temporary directory. Unlink any
    # remaining junction before recursively deleting the verified owned root.
    if (Test-Path -LiteralPath $redirectedPath) {
        [IO.Directory]::Delete($redirectedPath)
    }
    $resolvedProbePath = [IO.Path]::GetFullPath($probeRootPath)
    $temporaryRootPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedProbePath.StartsWith($temporaryRootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the temporary root.'
    }
    if (Test-Path -LiteralPath $resolvedProbePath) {
        Remove-Item -LiteralPath $resolvedProbePath -Recurse -Force
    }
}
