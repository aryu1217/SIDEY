#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [string]$BuildDirectory = 'build/windows/manual-release-build',

    [string]$MakensisPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Sidey.PowerShell.psm1') -Force

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$candidatePath = [IO.Path]::GetFullPath($OutputDirectory)
$buildPath = [IO.Path]::GetFullPath($BuildDirectory)
if ($candidatePath -ieq $buildPath -or
    $candidatePath.StartsWith($buildPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $buildPath.StartsWith($candidatePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Candidate and build directories must be separate.'
}
foreach ($path in @($candidatePath, $buildPath)) {
    if ((Test-Path -LiteralPath $path) -and
        @(Get-ChildItem -LiteralPath $path -Force).Count -gt 0) {
        throw "Release output directory must be empty: $path"
    }
}

Push-Location $repositoryRoot
try {
    $branch = git branch --show-current
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') {
        throw 'Release candidates must be built from main.'
    }
    $sourceCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the release source commit.'
    }
    $remoteLine = git ls-remote origin refs/heads/main
    if ($LASTEXITCODE -ne 0 -or @($remoteLine).Count -ne 1 -or
        ($remoteLine -split '\s+')[0] -cne $sourceCommit) {
        throw 'Current HEAD must equal origin/main before building a release candidate.'
    }
    $trackedChanges = @(git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -gt 0) {
        throw 'Release source has tracked changes.'
    }

    $release = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/windows.json') `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    $releaseVersion = [string]$release.releaseVersion
    $productVersion = [string]$release.version
    if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$' -or
        $productVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Invalid Windows release or product version.'
    }
    Invoke-SideyNativeCommand -FilePath 'python' `
        -ArgumentList @('-X', 'utf8', 'scripts/skills/verify_release_consistency.py',
            '--platform', 'windows', '--allow-unreleased-source') `
        -Description 'Windows release candidate metadata check'
    Invoke-SideyNativeCommand -FilePath 'python' `
        -ArgumentList @('scripts/validate_pixel_assets.py') -Description 'Pixel asset check'
    Invoke-SideyNativeCommand -FilePath 'python' `
        -ArgumentList @('-X', 'utf8', 'windows/tools/sync_product_assets.py', '--check') `
        -Description 'Windows catalog and asset check'

    Push-Location (Join-Path $repositoryRoot 'windows')
    try {
        Invoke-SideyNativeCommand -FilePath 'dotnet' `
            -ArgumentList @('restore', 'SIDEY.Windows.slnx') -Description 'Windows restore'
        Invoke-SideyNativeCommand -FilePath 'dotnet' `
            -ArgumentList @('format', 'SIDEY.Windows.slnx', '--no-restore',
                '--verify-no-changes', '--verbosity', 'minimal') -Description 'Windows format check'
        Invoke-SideyNativeCommand -FilePath 'dotnet' `
            -ArgumentList @('build', 'SIDEY.Windows.slnx', '--configuration', 'Release',
                '--no-restore') -Description 'Windows build'
        Invoke-SideyNativeCommand -FilePath 'dotnet' `
            -ArgumentList @('test', 'SIDEY.Windows.slnx', '--configuration', 'Release',
                '--no-restore', '--no-build') -Description 'Windows tests'
    }
    finally {
        Pop-Location
    }

    [IO.Directory]::CreateDirectory($buildPath) | Out-Null
    $artifactPath = Join-Path $buildPath 'artifacts'
    foreach ($architecture in @('x64', 'arm64')) {
        $platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
        $publishPath = Join-Path $buildPath "publish-$architecture"
        Push-Location (Join-Path $repositoryRoot 'windows')
        try {
            Invoke-SideyNativeCommand -FilePath 'dotnet' `
                -ArgumentList @('publish', 'src/Sidey.App/Sidey.App.csproj',
                    '--configuration', 'Release', '--runtime', "win-$architecture",
                    "-p:Platform=$platform", '--self-contained', 'true',
                    '-p:PublishSingleFile=false', '--output', $publishPath) `
                -Description "$architecture self-contained publish"
        }
        finally {
            Pop-Location
        }
        & (Join-Path $PSScriptRoot 'tests/Test-SelfContainedPublish.ps1') `
            -PublishDirectory $publishPath -Architecture $architecture
        $installerParameters = @{
            PublishDirectory = $publishPath
            OutputDirectory = $artifactPath
            ProductVersion = $productVersion
            ReleaseVersion = $releaseVersion
            Architecture = $architecture
        }
        if (-not [string]::IsNullOrWhiteSpace($MakensisPath)) {
            $installerParameters.MakensisPath = $MakensisPath
        }
        & (Join-Path $PSScriptRoot 'New-WindowsInstaller.ps1') @installerParameters
    }

    [IO.Directory]::CreateDirectory($candidatePath) | Out-Null
    $finalSourceCommit = git rev-parse HEAD
    $finalTrackedChanges = @(git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $finalSourceCommit -cne $sourceCommit -or
        $finalTrackedChanges.Count -gt 0) {
        throw 'Release source changed while building the candidate.'
    }
    $records = @(foreach ($architecture in @('x64', 'arm64')) {
        foreach ($suffix in @('Setup.exe', 'Update.exe', 'Update.json', 'Payload.bin')) {
            $name = "SIDEY-Windows-$architecture-v$releaseVersion-$suffix"
            $sourcePath = Join-Path $artifactPath $name
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "Installer did not produce expected release asset: $name"
            }
            $targetPath = Join-Path $candidatePath $name
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath
            [ordered]@{
                name = $name
                sha256 = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                size = (Get-Item -LiteralPath $targetPath).Length
            }
        }
    })
    $manifest = [ordered]@{
        schema = 1
        sourceCommit = $sourceCommit
        productVersion = $productVersion
        releaseVersion = $releaseVersion
        files = $records
    }
    $manifestPath = Join-Path $candidatePath 'windows-release-candidate.json'
    [IO.File]::WriteAllText($manifestPath,
        ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'tests/Test-WindowsReleaseCandidate.ps1') `
        -CandidateDirectory $candidatePath
    Write-Host "CandidateDirectory=$candidatePath"
}
finally {
    Pop-Location
}
