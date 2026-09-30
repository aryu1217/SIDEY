#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][string]$FileListPath,
    [Parameter(Mandatory = $true)][string]$ReleaseVersion
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$publishRootPath = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\', '/')
$payloadFilePath = [IO.Path]::GetFullPath($PayloadPath)
$fileListFilePath = [IO.Path]::GetFullPath($FileListPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($payloadFilePath)) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fileListFilePath)) | Out-Null
$files = [Collections.Generic.List[object]]::new()
$trustedLines = [Collections.Generic.List[string]]::new()
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$payloadStream = [IO.File]::Create($payloadFilePath)
$totalBytes = [long]0
try {
    foreach ($file in @(Get-ChildItem -LiteralPath $publishRootPath -Recurse -File |
        Where-Object { $_.Extension -ine '.pdb' } | Sort-Object FullName)) {
        $relativePath = $file.FullName.Substring($publishRootPath.Length + 1).Replace('\', '/')
        if ($relativePath -ieq 'Uninstall.exe') {
            $relativePath = 'Runtime/SIDEY.UninstallHelper.exe'
        }
        if ($relativePath.Length -gt 240 -or -not $paths.Add($relativePath) -or
            $relativePath.IndexOfAny([char[]]"`t`r`n") -ge 0) {
            throw "Invalid or duplicate incremental target path: $relativePath"
        }
        if ($files.Count -ge 10000) { throw 'Incremental file count exceeds the client limit.' }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Incremental publish files cannot be reparse points: $($file.FullName)"
        }
        $offset = $payloadStream.Position
        $inputStream = [IO.File]::OpenRead($file.FullName)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString($algorithm.ComputeHash($inputStream)).Replace('-', '').ToLowerInvariant()
            $inputStream.Position = 0
            $compressedStream = [IO.Compression.GZipStream]::new(
                $payloadStream, [IO.Compression.CompressionLevel]::Optimal, $true)
            try {
                $inputStream.CopyTo($compressedStream)
            } finally {
                $compressedStream.Dispose()
            }
            $size = $inputStream.Length
        } finally {
            $algorithm.Dispose()
            $inputStream.Dispose()
        }
        $totalBytes += $size
        if ($totalBytes -gt 2GB -or ($payloadStream.Position - $offset) -gt 128MB) {
            throw 'Incremental payload exceeds client size limits.'
        }
        $files.Add([ordered]@{
            path = $relativePath
            sha256 = $hash
            size = $size
            offset = $offset
            length = $payloadStream.Position - $offset
        })
        $trustedLines.Add("$hash`t$size`t$relativePath")
    }
} finally {
    $payloadStream.Dispose()
}
[IO.File]::WriteAllLines($fileListFilePath, $trustedLines.ToArray(), [Text.UTF8Encoding]::new($false))
return [ordered]@{
    schema = 1
    version = $ReleaseVersion
    payload = [ordered]@{
        name = [IO.Path]::GetFileName($payloadFilePath)
        sha256 = (Get-FileHash -LiteralPath $payloadFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $payloadFilePath).Length
    }
    files = $files.ToArray()
}
