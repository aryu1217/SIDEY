#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateDirectory,

    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$PublicRepository = 'sidey-app/SIDEY',

    [string]$ReleaseNotesPath,

    [switch]$Publish
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Sidey.PowerShell.psm1') -Force

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$candidatePath = (Resolve-Path -LiteralPath $CandidateDirectory).Path
& (Join-Path $PSScriptRoot 'tests/Test-WindowsReleaseCandidate.ps1') `
    -CandidateDirectory $candidatePath
$manifest = Get-Content -LiteralPath (Join-Path $candidatePath 'windows-release-candidate.json') `
    -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.releaseVersion
$tag = "windows-v$version"

Push-Location $repositoryRoot
try {
    $branch = git branch --show-current
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') {
        throw 'Windows releases must be published from main.'
    }
    $sourceCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -cne [string]$manifest.sourceCommit) {
        throw 'Candidate source commit differs from current main.'
    }
    $remoteLine = git ls-remote origin refs/heads/main
    if ($LASTEXITCODE -ne 0 -or @($remoteLine).Count -ne 1 -or
        ($remoteLine -split '\s+')[0] -cne $sourceCommit) {
        throw 'Current main is not the published origin/main commit.'
    }
    $trackedChanges = @(git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -gt 0) {
        throw 'Release source has tracked changes.'
    }

    $release = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/windows.json') `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$release.releaseVersion -cne $version -or
        [string]$release.version -cne [string]$manifest.productVersion) {
        throw 'Candidate release metadata differs from current main.'
    }
    Invoke-SideyNativeCommand -FilePath 'python' `
        -ArgumentList @('-X', 'utf8', 'scripts/skills/verify_release_consistency.py',
            '--platform', 'windows') -Description 'Windows release metadata check'

    $expectedNotesPath = Join-Path $repositoryRoot "docs/releases/windows-v$version.md"
    if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
        $ReleaseNotesPath = $expectedNotesPath
    }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotesPath).Path
    if ($notesPath -ine $expectedNotesPath -or
        -not (Test-Path -LiteralPath $notesPath -PathType Leaf) -or
        (Get-Item -LiteralPath $notesPath).Length -le 0) {
        throw "The nonempty release note must be docs/releases/windows-v$version.md."
    }
    git ls-files --error-unmatch -- "docs/releases/windows-v$version.md" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Windows release note must be committed on main.'
    }

    Write-Host "WindowsReleasePreflight=true"
    Write-Host "PublicRepository=$PublicRepository"
    Write-Host "Tag=$tag"
    Write-Host "SourceCommit=$sourceCommit"
    if (-not $Publish) {
        Write-Host 'Publication=not requested'
        return
    }
    if (-not $PSCmdlet.ShouldProcess("$PublicRepository $tag", 'Publish Windows release')) {
        return
    }

    $expectedNames = @($manifest.files | ForEach-Object { [string]$_.name })
    $PSNativeCommandUseErrorActionPreference = $false
    gh release view $tag --repo $PublicRepository *> $null
    $releaseExists = $LASTEXITCODE -eq 0
    if ($releaseExists) {
        throw "Public release tag already exists: $tag"
    }
    $publicTargetCommit = gh api "repos/$PublicRepository/commits/main" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $publicTargetCommit -notmatch '^[0-9a-f]{40}$') {
        throw "Unable to resolve the public repository main commit: $PublicRepository"
    }
    gh api "repos/$PublicRepository/git/ref/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Public release tag already exists: $tag"
    }
    $candidateFiles = @($expectedNames | ForEach-Object { Join-Path $candidatePath $_ })
    Invoke-SideyNativeCommand -FilePath 'gh' `
        -ArgumentList (@('release', 'create', $tag) + $candidateFiles + @(
            '--repo', $PublicRepository,
            '--target', $publicTargetCommit,
            '--title', "SIDEY Windows v$($manifest.productVersion)",
            '--notes-file', $notesPath,
            '--draft')) -Description 'Create Windows release draft'

    $draftJson = gh release view $tag --repo $PublicRepository `
        --json isDraft,isPrerelease,targetCommitish,assets
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect Windows release draft.'
    }
    $draft = $draftJson | ConvertFrom-Json
    $remoteNames = @($draft.assets | ForEach-Object { [string]$_.name })
    if (-not $draft.isDraft -or $draft.isPrerelease -or
        [string]$draft.targetCommitish -cne $publicTargetCommit -or
        $remoteNames.Count -ne 8 -or
        @(Compare-Object $expectedNames $remoteNames -CaseSensitive).Count -ne 0) {
        throw 'Windows release draft must target the pinned public commit and contain exactly eight assets.'
    }
    $tagTargetCommit = gh api "repos/$PublicRepository/git/ref/tags/$tag" --jq .object.sha
    if ($LASTEXITCODE -ne 0 -or $tagTargetCommit -cne $publicTargetCommit) {
        throw 'Windows release tag does not target the pinned public repository commit.'
    }

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $verifyPath = Join-Path $temporaryRoot `
        "sidey-windows-release-verify-$([Guid]::NewGuid().ToString('N'))"
    [IO.Directory]::CreateDirectory($verifyPath) | Out-Null
    try {
        foreach ($record in $manifest.files) {
            $name = [string]$record.name
            Invoke-SideyNativeCommand -FilePath 'gh' `
                -ArgumentList @('release', 'download', $tag, '--repo', $PublicRepository,
                    '--pattern', $name, '--dir', $verifyPath) `
                -Description "Download draft asset $name"
            $downloadedPath = Join-Path $verifyPath $name
            if (-not (Test-Path -LiteralPath $downloadedPath -PathType Leaf) -or
                (Get-Item -LiteralPath $downloadedPath).Length -ne [long]$record.size -or
                (Get-FileHash -LiteralPath $downloadedPath -Algorithm SHA256).Hash.ToLowerInvariant() `
                    -cne [string]$record.sha256) {
                throw "Downloaded draft asset differs from candidate: $name"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $verifyPath -PathType Container) {
            $resolvedVerifyPath = (Resolve-Path -LiteralPath $verifyPath).Path
            $temporaryPrefix = $temporaryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedVerifyPath.StartsWith($temporaryPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Release verification directory escaped the temporary root.'
            }
            Remove-Item -LiteralPath $resolvedVerifyPath -Recurse -Force
        }
    }

    Invoke-SideyNativeCommand -FilePath 'gh' `
        -ArgumentList @('release', 'edit', $tag, '--repo', $PublicRepository, '--draft=false') `
        -Description 'Publish verified Windows release'
    $publishedJson = gh release view $tag --repo $PublicRepository `
        --json isDraft,isPrerelease,targetCommitish,assets
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect published Windows release.'
    }
    $published = $publishedJson | ConvertFrom-Json
    $publishedNames = @($published.assets | ForEach-Object { [string]$_.name })
    if ($published.isDraft -or $published.isPrerelease -or
        [string]$published.targetCommitish -cne $publicTargetCommit -or
        $publishedNames.Count -ne 8 -or
        @(Compare-Object $expectedNames $publishedNames -CaseSensitive).Count -ne 0) {
        throw 'Published Windows release no longer matches the verified draft.'
    }
    Write-Host 'WindowsReleasePublished=true'
}
finally {
    Pop-Location
}
