#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [string]$MakensisPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Sidey.PowerShell.psm1') -Force
$repositoryRootPath = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$publishDirectoryPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$outputDirectoryPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$launcherExecutablePath = Join-Path $publishDirectoryPath 'SIDEY.exe'
$uninstallerPath = Join-Path $publishDirectoryPath 'Uninstall.exe'
$runtimeDirectory = Join-Path $publishDirectoryPath 'Runtime'
$hostExecutablePath = Join-Path $runtimeDirectory 'SIDEY.Host.exe'
$legacyExecutablePath = Join-Path $publishDirectoryPath 'Sidey.App.exe'
$setupScriptPath = Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/Sidey.Setup.nsi'
& (Join-Path $PSScriptRoot 'tests/Test-SelfContainedPublish.ps1') `
    -PublishDirectory $publishDirectoryPath

function Get-SideyRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $basePathWithSeparator = [System.IO.Path]::GetFullPath($BasePath)
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
    if (-not $basePathWithSeparator.EndsWith(
        $separator,
        [System.StringComparison]::Ordinal)) {
        $basePathWithSeparator += $separator
    }

    $baseUri = [Uri]::new($basePathWithSeparator)
    $targetUri = [Uri]::new([System.IO.Path]::GetFullPath($TargetPath))
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace(
        '/',
        [System.IO.Path]::DirectorySeparatorChar)
}

if (-not (Test-Path -LiteralPath $launcherExecutablePath -PathType Leaf)) {
    throw "게시 폴더에 SIDEY.exe가 없음: $publishDirectoryPath"
}
if (-not (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
    throw "Published Uninstall.exe is missing: $publishDirectoryPath"
}
if (-not (Test-Path -LiteralPath $hostExecutablePath -PathType Leaf)) {
    throw "게시 폴더에 Runtime/SIDEY.Host.exe가 없음: $publishDirectoryPath"
}
if (Test-Path -LiteralPath $legacyExecutablePath -PathType Leaf) {
    throw '게시 진입점 이름이 아직 Sidey.App.exe임. SIDEY.exe 하나로 통일해야 함.'
}

$deployableFiles = @(Get-ChildItem -LiteralPath $publishDirectoryPath -Recurse -File |
    Where-Object { $_.Extension -ne '.pdb' })
$characterAssetDirectory = Join-Path $publishDirectoryPath 'Assets/Characters'
$throwableAssetDirectory = Join-Path $publishDirectoryPath 'Assets/Throwables'
$iconDirectory = Join-Path $publishDirectoryPath 'Assets/Icons'
$languageDirectory = Join-Path $publishDirectoryPath 'Langs'
$requiredSideyBinaries = @(
    $launcherExecutablePath,
    $uninstallerPath,
    $hostExecutablePath,
    (Join-Path $runtimeDirectory 'SIDEY.Host.dll'),
    (Join-Path $runtimeDirectory 'Sidey.Core.dll'),
    (Join-Path $runtimeDirectory 'Sidey.Infrastructure.dll'),
    (Join-Path $runtimeDirectory 'Sidey.Overlay.dll'),
    (Join-Path $runtimeDirectory 'Sidey.Platform.Windows.dll'),
    (Join-Path $runtimeDirectory 'Sidey.Presentation.dll')
)
$missingBinaries = @($requiredSideyBinaries | Where-Object {
    -not (Test-Path -LiteralPath $_ -PathType Leaf)
})
if ($missingBinaries.Count -gt 0) {
    throw "Required SIDEY binaries are missing: $($missingBinaries -join ', ')"
}

$allowedRootNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    'SIDEY.exe',
    'Uninstall.exe',
    'Assets',
    'Langs',
    'Runtime')) {
    [void]$allowedRootNames.Add($name)
}
$unexpectedRootItems = @(Get-ChildItem -LiteralPath $publishDirectoryPath -Force |
    Where-Object { -not $allowedRootNames.Contains($_.Name) })
if ($unexpectedRootItems.Count -gt 0) {
    throw "Unexpected item at the publish root: $($unexpectedRootItems.Name -join ', ')"
}
foreach ($directory in @($characterAssetDirectory, $throwableAssetDirectory, $iconDirectory, $languageDirectory)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Required publish directory is missing: $directory"
    }
}

$sourceIconDirectory = Join-Path $repositoryRootPath 'windows/src/Sidey.App/Assets/Icons'
$sourceIconNames = @(Get-ChildItem -LiteralPath $sourceIconDirectory -File |
    Select-Object -ExpandProperty Name |
    Sort-Object)
$publishedIconNames = @(Get-ChildItem -LiteralPath $iconDirectory -File |
    Select-Object -ExpandProperty Name |
    Sort-Object)
if ($sourceIconNames.Count -eq 0 -or
    @(Compare-Object $sourceIconNames $publishedIconNames).Count -gt 0) {
    throw 'Published SIDEY icon variants do not match the source icon set.'
}

$legacyAssetDirectories = @(
    (Join-Path $publishDirectoryPath 'Assets/Character'),
    (Join-Path $publishDirectoryPath 'Assets/Throwable'),
    (Join-Path $publishDirectoryPath 'Assets/CharacterThrow')
)
$presentLegacyAssetDirectories = @($legacyAssetDirectories | Where-Object {
    Test-Path -LiteralPath $_ -PathType Container
})
if ($presentLegacyAssetDirectories.Count -gt 0) {
    throw "Legacy character asset directories remain in the publish output: $($presentLegacyAssetDirectories -join ', ')"
}

$characterAssetFiles = @(Get-ChildItem -LiteralPath $characterAssetDirectory -Recurse -File)
$manifests = @($characterAssetFiles | Where-Object { $_.Name -eq 'manifest.json' })
$sourceCharacterAssetDirectory = Join-Path $repositoryRootPath 'windows/src/Sidey.Overlay/Assets/Characters'
$sourceManifestNames = @(Get-ChildItem -LiteralPath $sourceCharacterAssetDirectory -Filter 'manifest.json' -Recurse -File |
    ForEach-Object { Get-SideyRelativePath $sourceCharacterAssetDirectory $_.FullName } |
    Sort-Object)
$publishedManifestNames = @($manifests |
    ForEach-Object { Get-SideyRelativePath $characterAssetDirectory $_.FullName } |
    Sort-Object)
if ($sourceManifestNames.Count -eq 0 -or
    @(Compare-Object $sourceManifestNames $publishedManifestNames).Count -gt 0) {
    throw '소스와 게시 폴더의 캐릭터 manifest 목록이 일치하지 않음'
}

$expectedAssets = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($manifest in $manifests) {
    $metadata = Get-Content -LiteralPath $manifest.FullName -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $characterId = [string]$metadata.character_id
    if ([string]::IsNullOrWhiteSpace($characterId) -or
        $manifest.Directory.Name -ne $characterId) {
        throw "캐릭터 폴더 이름과 character_id가 일치하지 않음: $($manifest.FullName)"
    }
    foreach ($name in @('base.png', 'base.bgra', 'throw_hit.png', 'throw_hit.bgra', 'manifest.json')) {
        $requiredAssetPath = Join-Path $manifest.Directory.FullName $name
        if (-not (Test-Path -LiteralPath $requiredAssetPath -PathType Leaf)) {
            throw "캐릭터 외부 에셋이 누락됨: $characterId/$name"
        }
        [void]$expectedAssets.Add([System.IO.Path]::GetFullPath($requiredAssetPath))
    }
}
$unexpectedCharacterAssets = @($characterAssetFiles | Where-Object {
    -not $expectedAssets.Contains($_.FullName)
})
if ($unexpectedCharacterAssets.Count -gt 0 -or
    $characterAssetFiles.Count -ne $expectedAssets.Count) {
    throw '캐릭터 외부 에셋에는 base/throw_hit PNG·BGRA와 manifest 세트만 둘 수 있음'
}

$sourceThrowableAssetDirectory = Join-Path $repositoryRootPath 'windows/src/Sidey.Overlay/Assets/Throwables'
$sourceThrowableFiles = @(Get-ChildItem -LiteralPath $sourceThrowableAssetDirectory -Recurse -File |
    ForEach-Object { Get-SideyRelativePath $sourceThrowableAssetDirectory $_.FullName } |
    Sort-Object)
$publishedThrowableFiles = @(Get-ChildItem -LiteralPath $throwableAssetDirectory -Recurse -File |
    ForEach-Object { Get-SideyRelativePath $throwableAssetDirectory $_.FullName } |
    Sort-Object)
if ($sourceThrowableFiles.Count -eq 0 -or
    @(Compare-Object $sourceThrowableFiles $publishedThrowableFiles).Count -gt 0) {
    throw '소스와 게시 폴더의 투척물 에셋 목록이 일치하지 않음'
}
foreach ($throwableDirectory in @(Get-ChildItem -LiteralPath $throwableAssetDirectory -Directory)) {
    $fileNames = @(Get-ChildItem -LiteralPath $throwableDirectory.FullName -File |
        Select-Object -ExpandProperty Name |
        Sort-Object)
    $expectedFileNames = @('sprite.bgra', 'sprite.png')
    if ($throwableDirectory.Name -eq 'throwable_toy_cannon') {
        $expectedFileNames += @('emitter.bgra', 'emitter.png', 'preview.png')
    }
    $expectedFileNames = @($expectedFileNames | Sort-Object)
    if (@(Compare-Object $expectedFileNames $fileNames).Count -gt 0) {
        throw "투척물 외부 에셋 구성이 허용 목록과 다름: $($throwableDirectory.FullName)"
    }
}

if ($ProductVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Windows Product Version must contain three numeric parts: $ProductVersion"
}
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Windows Release Version must contain three numeric parts: $ReleaseVersion"
}
$publishedVersionInfo = (Get-Item -LiteralPath $hostExecutablePath).VersionInfo
$publishedProductVersion = [string]$publishedVersionInfo.ProductVersion
$publishedFileVersion = [string]$publishedVersionInfo.FileVersion
$versionPropertiesPath = Join-Path $repositoryRootPath 'windows/Version.props'
$versionProperties = [xml](Get-Content -LiteralPath $versionPropertiesPath -Raw -Encoding UTF8)
$expectedProductVersion = [string]$versionProperties.Project.PropertyGroup.SideyProductVersion
$expectedUpdateVersion = [string]$versionProperties.Project.PropertyGroup.SideyWindowsUpdateVersion
$expectedReleaseVersion = [string]$versionProperties.Project.PropertyGroup.SideyWindowsReleaseVersion
$expectedFileVersion = [string]$versionProperties.Project.PropertyGroup.SideyMsixVersion
if ([string]::IsNullOrWhiteSpace($expectedUpdateVersion) -or
    $expectedFileVersion -cne ($expectedUpdateVersion + '.0')) {
    throw 'windows/Version.props contains inconsistent Windows update and MSIX versions.'
}
if ($ProductVersion -cne $expectedProductVersion) {
    throw "Product Version does not match windows/Version.props: $ProductVersion / $expectedProductVersion"
}
if ($ReleaseVersion -cne $expectedReleaseVersion) {
    throw "Release Version does not match windows/Version.props: $ReleaseVersion / $expectedReleaseVersion"
}
if ($publishedProductVersion -cne $ProductVersion) {
    throw "Published SIDEY.Host.exe ProductVersion does not match: $publishedProductVersion / $ProductVersion"
}
if ($publishedFileVersion -cne $expectedFileVersion) {
    throw "Published SIDEY.Host.exe FileVersion does not match: $publishedFileVersion / $expectedFileVersion"
}

[IO.Directory]::CreateDirectory($outputDirectoryPath) | Out-Null
$installerBuildDirectory = Join-Path $outputDirectoryPath 'internal/setup-build'
[IO.Directory]::CreateDirectory($installerBuildDirectory) | Out-Null
$languageSelectorPath = Join-Path $installerBuildDirectory 'language/Sidey.SetupLanguage.exe'
$helperDirectory = Join-Path $installerBuildDirectory 'helpers'
$installTransactionExecutablePath = Join-Path $helperDirectory 'Sidey.InstallTransaction.exe'
$installerErrorHelperExecutablePath = Join-Path $helperDirectory 'Sidey.InstallerErrorHelper.exe'
$termsSourcePath = Join-Path $repositoryRootPath 'website/src/pages/ko/terms.md'
$termsGeneratorPath = Join-Path $PSScriptRoot 'New-InstallerTerms.ps1'
$termsLicenseFilePath = Join-Path $installerBuildDirectory 'SideyTerms.txt'
& $termsGeneratorPath -SourceMarkdownPath $termsSourcePath -OutputPath $termsLicenseFilePath
if (-not (Test-Path -LiteralPath $termsLicenseFilePath -PathType Leaf)) {
    throw 'SIDEY installer terms file was not generated.'
}
$termsBytes = [IO.File]::ReadAllBytes($termsLicenseFilePath)
if ($termsBytes.Length -lt 3 -or
    $termsBytes[0] -ne 0xEF -or
    $termsBytes[1] -ne 0xBB -or
    $termsBytes[2] -ne 0xBF) {
    throw 'SIDEY installer terms must be UTF-8 with BOM.'
}
$strictUtf8 = [Text.UTF8Encoding]::new($true, $true)
try {
    [void]$strictUtf8.GetString($termsBytes, 3, $termsBytes.Length - 3)
}
catch {
    throw 'SIDEY installer terms contain invalid UTF-8 bytes.'
}

$makensisCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($MakensisPath)) {
    $makensisCandidates += $MakensisPath
}
$makensisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
if ($null -ne $makensisCommand) {
    $makensisCandidates += $makensisCommand.Source
}
$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFiles)
$makensisCandidates += @(
    (Join-Path $programFilesX86 'NSIS\makensis.exe'),
    (Join-Path $programFiles 'NSIS\makensis.exe')
)
$resolvedMakensisPath = $makensisCandidates |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($resolvedMakensisPath)) {
    throw 'NSIS 3.12 or newer is required. Install NSIS.NSIS or pass -MakensisPath.'
}
$makensisVersionText = (Invoke-SideyNativeCommand `
    -FilePath $resolvedMakensisPath `
    -ArgumentList @('/VERSION') `
    -Description 'NSIS compiler version check' | Out-String).Trim()
if ($makensisVersionText -notmatch '^v?(?<version>\d+\.\d+(?:\.\d+)?)$') {
    throw "Unable to read the NSIS compiler version: $makensisVersionText"
}
$makensisVersion = [Version]::Parse($Matches.version)
if ($makensisVersion -lt [Version]'3.12') {
    throw "NSIS 3.12 or newer is required. Found $makensisVersion."
}
Invoke-SideyNativeCommand `
    -FilePath 'powershell.exe' `
    -ArgumentList @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'New-InstallerLanguageSelector.ps1'),
        '-OutputPath', $languageSelectorPath,
        '-Version', $ProductVersion,
        '-FileVersion', $publishedFileVersion,
        '-NsisDirectory', (Split-Path -Parent $resolvedMakensisPath)
    ) `
    -Description 'Installer language selector build'

$helperBuilderPath = Join-Path $PSScriptRoot 'New-SideyHelperExecutable.ps1'
$helperIconPath = Join-Path $repositoryRootPath 'windows/src/Sidey.App/Assets/Icons/SideyAppIcon.ico'
& $helperBuilderPath `
    -SourcePath (Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/InstallTransaction.cs') `
    -OutputPath $installTransactionExecutablePath `
    -Version $ProductVersion -FileVersion $publishedFileVersion `
    -Title 'SIDEY Install Transaction' `
    -Description 'SIDEY atomic install transaction helper' `
    -IconPath $helperIconPath
& $helperBuilderPath `
    -SourcePath (Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/InstallerErrorNormalizer.cs') `
    -OutputPath $installerErrorHelperExecutablePath `
    -Version $ProductVersion -FileVersion $publishedFileVersion `
    -Title 'SIDEY Installer Error Helper' `
    -Description 'SIDEY installer error normalization helper' `
    -IconPath $helperIconPath

& (Join-Path $PSScriptRoot 'tests/Test-HelperExecutables.ps1') `
    -PublishDirectory $publishDirectoryPath -SelectorExecutablePath $languageSelectorPath `
    -Version $ProductVersion -FileVersion $publishedFileVersion `
    -NsisDirectory (Split-Path -Parent $resolvedMakensisPath)
& (Join-Path $PSScriptRoot 'tests/Test-InstallTransaction.ps1') `
    -HelperPath $installTransactionExecutablePath
& (Join-Path $PSScriptRoot 'tests/Test-PowerShellSupport.ps1') `
    -HelperPath $installerErrorHelperExecutablePath
& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'tests/Test-PowerShellSupport.ps1') `
    -HelperPath $installerErrorHelperExecutablePath
if ($LASTEXITCODE -ne 0) {
    throw "Windows PowerShell process verification failed with exit code $LASTEXITCODE."
}

$runtimeInstallerSources = @(Get-ChildItem `
    -LiteralPath (Split-Path -Parent $setupScriptPath) -File |
    Where-Object { $_.Extension -in @('.nsi', '.nsh') } |
    Select-Object -ExpandProperty FullName)
foreach ($runtimeInstallerSource in $runtimeInstallerSources) {
    $runtimeInstallerText = [IO.File]::ReadAllText($runtimeInstallerSource)
    foreach ($forbiddenRuntimeToken in @(
        'powershell.exe',
        'ExecutionPolicy',
        '.ps1',
        'nsExec::ExecToLog',
        'taskkill.exe')) {
        if ($runtimeInstallerText.IndexOf(
            $forbiddenRuntimeToken,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Installer runtime source contains forbidden token '$forbiddenRuntimeToken': $runtimeInstallerSource"
        }
    }
}

function ConvertTo-NsisLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)

    return $Value.Replace('$', '$$').Replace('"', '$\"')
}

$payloadFiles = @($deployableFiles | Where-Object {
    $_.FullName -ne $uninstallerPath
})
$installInclude = Join-Path $installerBuildDirectory 'SideyPayloadInstall.nsh'
$uninstallFilesInclude = Join-Path $installerBuildDirectory 'SideyPayloadUninstallFiles.nsh'
$uninstallDirectoriesInclude = Join-Path $installerBuildDirectory 'SideyPayloadUninstallDirectories.nsh'
$installLines = [Collections.Generic.List[string]]::new()
$uninstallFileLines = [Collections.Generic.List[string]]::new()
$uninstallDirectoryLines = [Collections.Generic.List[string]]::new()
$payloadDirectories = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)

foreach ($file in $payloadFiles) {
    $relativePath = Get-SideyRelativePath $publishDirectoryPath $file.FullName
    $relativeDirectory = Split-Path $relativePath -Parent
    $destination = '$StagingDirectory'
    if (-not [string]::IsNullOrWhiteSpace($relativeDirectory)) {
        $destination += "\$(ConvertTo-NsisLiteral $relativeDirectory)"
        # Keep removal non-recursive, but include parents that contain only
        # child directories so an owned payload can still disappear fully.
        $directory = $relativeDirectory
        while (-not [string]::IsNullOrWhiteSpace($directory)) {
            [void]$payloadDirectories.Add($directory)
            $directory = Split-Path $directory -Parent
        }
    }

    $installLines.Add("SetOutPath `"$destination`"")
    $installLines.Add('ClearErrors')
    $installLines.Add("File `"$(ConvertTo-NsisLiteral $file.FullName)`"")
    $installLines.Add('IfErrors payload_stage_failed')
    $uninstallFileLines.Add(
        "Delete `"`$INSTDIR\$(ConvertTo-NsisLiteral $relativePath)`"")
}

foreach ($directory in @($payloadDirectories) |
    Sort-Object `
        @{ Expression = { ($_ -split '[\\/]').Count }; Descending = $true }, `
        @{ Expression = { $_ }; Descending = $false }) {
    # SIDEY.UninstallHelper.exe is installed separately under Runtime and must
    # remain available when an earlier owned-file deletion fails. The NSIS
    # section removes the helper and Runtime only after this manifest succeeds.
    if ($directory.Equals('Runtime', [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    $uninstallDirectoryLines.Add(
        "RMDir `"`$INSTDIR\$(ConvertTo-NsisLiteral $directory)`"")
}

if (@($installLines | Where-Object {
    $_.IndexOf('$$StagingDirectory', [StringComparison]::Ordinal) -ge 0 -or
    $_.IndexOf('$INSTDIR', [StringComparison]::Ordinal) -ge 0
}).Count -gt 0 -or @($uninstallFileLines + $uninstallDirectoryLines | Where-Object {
    $_.IndexOf('$$INSTDIR', [StringComparison]::Ordinal) -ge 0
}).Count -gt 0) {
    throw 'Generated NSIS payload paths must use runtime transaction variables.'
}

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines($installInclude, $installLines.ToArray(), $utf8WithoutBom)
[IO.File]::WriteAllLines(
    $uninstallFilesInclude,
    $uninstallFileLines.ToArray(),
    $utf8WithoutBom)
[IO.File]::WriteAllLines(
    $uninstallDirectoriesInclude,
    $uninstallDirectoryLines.ToArray(),
    $utf8WithoutBom)
& (Join-Path $PSScriptRoot 'tests/Test-InstallerPayloadUninstall.ps1') `
    -PublishDirectory $publishDirectoryPath `
    -UninstallFilesIncludePath $uninstallFilesInclude `
    -UninstallDirectoriesIncludePath $uninstallDirectoriesInclude

Invoke-SideyNativeCommand `
    -FilePath $resolvedMakensisPath `
    -ArgumentList @(
        '/INPUTCHARSET', 'UTF8',
        "/DAPP_VERSION=$ProductVersion",
        "/DAPP_UPDATE_VERSION=$expectedUpdateVersion",
        "/DAPP_FILE_VERSION=$publishedFileVersion",
        "/DOUTPUT_DIR=$installerBuildDirectory",
        "/DPUBLISH_DIR=$publishDirectoryPath",
        "/DPAYLOAD_INSTALL_INCLUDE=$installInclude",
        "/DPAYLOAD_UNINSTALL_FILES_INCLUDE=$uninstallFilesInclude",
        "/DPAYLOAD_UNINSTALL_DIRECTORIES_INCLUDE=$uninstallDirectoriesInclude",
        "/DTERMS_LICENSE_FILE=$termsLicenseFilePath",
        "/DLANGUAGE_SELECTOR_EXE=$languageSelectorPath",
        "/DINSTALL_TRANSACTION_EXE=$installTransactionExecutablePath",
        "/DINSTALLER_ERROR_HELPER_EXE=$installerErrorHelperExecutablePath",
        $setupScriptPath
    ) `
    -Description 'SIDEY Setup EXE build'

$builtSetupPath = Join-Path $installerBuildDirectory 'SIDEY-Setup.exe'
if (-not (Test-Path -LiteralPath $builtSetupPath -PathType Leaf)) {
    throw 'NSIS did not produce the full Setup EXE.'
}

$setupName = "SIDEY-Windows-x64-v${ReleaseVersion}-Setup.exe"
$setupFilePath = Join-Path $outputDirectoryPath $setupName
Copy-Item -LiteralPath $builtSetupPath -Destination $setupFilePath -Force
$updateName = "SIDEY-Windows-x64-v${ReleaseVersion}-Update.exe"
$updateFilePath = Join-Path $outputDirectoryPath $updateName
$payloadFilePath = Join-Path $outputDirectoryPath "SIDEY-Windows-x64-v${ReleaseVersion}-Payload.bin"
$fileListPath = Join-Path $installerBuildDirectory 'SideyIncrementalFiles.tsv'
$descriptor = & (Join-Path $PSScriptRoot 'New-WindowsIncrementalPayload.ps1') `
    -PublishDirectory $publishDirectoryPath -PayloadPath $payloadFilePath `
    -FileListPath $fileListPath -ReleaseVersion $ReleaseVersion
$incrementalHelperPath = Join-Path $helperDirectory 'Sidey.IncrementalPayload.exe'
& $helperBuilderPath `
    -SourcePath (Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/IncrementalPayload.cs') `
    -OutputPath $incrementalHelperPath `
    -Version $ProductVersion -FileVersion $publishedFileVersion `
    -Title 'SIDEY Incremental Payload' -Description 'SIDEY verified incremental payload staging' `
    -IconPath $helperIconPath -ResourcePath $fileListPath -ResourceName 'Sidey.IncrementalFiles'
& (Join-Path $PSScriptRoot 'tests/Test-IncrementalPayload.ps1') `
    -HelperPath $incrementalHelperPath -PublishDirectory $publishDirectoryPath `
    -PayloadPath $payloadFilePath -Descriptor $descriptor
Invoke-SideyNativeCommand `
    -FilePath $resolvedMakensisPath `
    -ArgumentList @(
        '/INPUTCHARSET', 'UTF8', '/DINCREMENTAL_PAYLOAD',
        "/DINCREMENTAL_PAYLOAD_EXE=$incrementalHelperPath",
        "/DAPP_VERSION=$ProductVersion",
        "/DAPP_UPDATE_VERSION=$expectedUpdateVersion",
        "/DAPP_FILE_VERSION=$publishedFileVersion",
        "/DOUTPUT_DIR=$installerBuildDirectory",
        "/DPUBLISH_DIR=$publishDirectoryPath",
        "/DPAYLOAD_INSTALL_INCLUDE=$installInclude",
        "/DPAYLOAD_UNINSTALL_FILES_INCLUDE=$uninstallFilesInclude",
        "/DPAYLOAD_UNINSTALL_DIRECTORIES_INCLUDE=$uninstallDirectoriesInclude",
        "/DTERMS_LICENSE_FILE=$termsLicenseFilePath",
        "/DLANGUAGE_SELECTOR_EXE=$languageSelectorPath",
        "/DINSTALL_TRANSACTION_EXE=$installTransactionExecutablePath",
        "/DINSTALLER_ERROR_HELPER_EXE=$installerErrorHelperExecutablePath",
        $setupScriptPath
    ) -Description 'SIDEY incremental Update EXE build'
Copy-Item -LiteralPath (Join-Path $installerBuildDirectory 'SIDEY-Update.exe') -Destination $updateFilePath -Force
$descriptor['updater'] = [ordered]@{
    name = $updateName
    sha256 = (Get-FileHash -LiteralPath $updateFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    size = (Get-Item -LiteralPath $updateFilePath).Length
}
$descriptorPath = Join-Path $outputDirectoryPath "SIDEY-Windows-x64-v${ReleaseVersion}-Update.json"
$descriptorJson = $descriptor | ConvertTo-Json -Depth 6
if ($utf8WithoutBom.GetByteCount($descriptorJson) -gt 4MB) {
    throw 'Incremental descriptor exceeds the client manifest size limit.'
}
[IO.File]::WriteAllText($descriptorPath, $descriptorJson, $utf8WithoutBom)
$hash = (Get-FileHash -LiteralPath $setupFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
$publishBytes = ($deployableFiles | Measure-Object -Property Length -Sum).Sum

Write-Host "PublishLayout=structured self-contained; Files=$($deployableFiles.Count); Bytes=$publishBytes"
Write-Host "NSIS=$makensisVersion"
Write-Host "Created public Setup EXE $setupFilePath"
Write-Host "SHA256=$hash"
