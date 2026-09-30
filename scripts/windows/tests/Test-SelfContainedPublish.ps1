#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
$repositoryRootPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$publishDirectoryPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$runtimeDirectoryPath = Join-Path $publishDirectoryPath 'Runtime'

if (Test-Path -LiteralPath (Join-Path $runtimeDirectoryPath 'Assets')) {
    throw 'Duplicate Runtime/Assets must not be included; use the deployment root Assets.'
}

$runtimeConfigurationPath = Join-Path $runtimeDirectoryPath 'SIDEY.Host.runtimeconfig.json'
$dependencyManifestPath = Join-Path $runtimeDirectoryPath 'SIDEY.Host.deps.json'
foreach ($requiredPath in @($runtimeConfigurationPath, $dependencyManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Published runtime metadata is missing: $requiredPath"
    }
}

$runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigurationPath -Raw |
    ConvertFrom-Json
$runtimeOptions = $runtimeConfiguration.runtimeOptions
$includedFrameworksProperty = $runtimeOptions.PSObject.Properties['includedFrameworks']
$frameworkProperty = $runtimeOptions.PSObject.Properties['framework']
$frameworksProperty = $runtimeOptions.PSObject.Properties['frameworks']
[object[]]$includedFrameworks = @()
if ($null -ne $includedFrameworksProperty) {
    $includedFrameworks = @($includedFrameworksProperty.Value)
}
if ($null -ne $frameworkProperty -or $null -ne $frameworksProperty -or
    $includedFrameworks.Count -ne 1 -or
    $includedFrameworks[0].name -cne 'Microsoft.NETCore.App') {
    throw 'Publish must carry one app-local Microsoft.NETCore.App runtime.'
}

$dependencyManifest = Get-Content -LiteralPath $dependencyManifestPath -Raw |
    ConvertFrom-Json
$runtimeIdentifier = "win-$Architecture"
if ($dependencyManifest.runtimeTarget.name -notlike "*/$runtimeIdentifier") {
    throw "Published runtime target does not match $runtimeIdentifier."
}
$dependencyLibraries = @($dependencyManifest.libraries.PSObject.Properties.Name)
$runtimePack = @($dependencyLibraries | Where-Object {
    $_ -like "runtimepack.Microsoft.NETCore.App.Runtime.$runtimeIdentifier/*"
})
$windowsAppSdk = @($dependencyLibraries | Where-Object {
    $_ -like 'Microsoft.WindowsAppSDK/*'
})
if ($runtimePack.Count -ne 1 -or $windowsAppSdk.Count -ne 1) {
    throw "The published $Architecture .NET runtime pack and Windows App SDK could not be resolved uniquely."
}
$runtimePackVersion = ($runtimePack[0] -split '/', 2)[1]
if ([version]$includedFrameworks[0].version -ne [version]$runtimePackVersion) {
    throw "The included .NET runtime version differs from the restored $Architecture runtime pack."
}

$applicationProjectPath = Join-Path $repositoryRootPath 'windows/src/Sidey.App/Sidey.App.csproj'
$applicationProject = [xml](Get-Content -LiteralPath $applicationProjectPath -Raw)
$configuredRuntimeVersion = [string]$applicationProject.Project.PropertyGroup.SideyDotNetRuntimeVersion
if ([string]::IsNullOrWhiteSpace($configuredRuntimeVersion) -or
    [version]$runtimePackVersion -ne [version]$configuredRuntimeVersion) {
    throw 'The restored .NET runtime pack does not match SideyDotNetRuntimeVersion.'
}

$requiredRuntimeFiles = @(
    'SIDEY.Host.exe',
    'SIDEY.Host.dll',
    'coreclr.dll',
    'clrjit.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'System.Private.CoreLib.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.WindowsAppRuntime.pri',
    'Microsoft.ui.xaml.dll',
    'Microsoft.UI.pri'
)
$missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $runtimeDirectoryPath $_) -PathType Leaf)
})
if ($missingRuntimeFiles.Count -gt 0) {
    throw "Self-contained runtime files are missing: $($missingRuntimeFiles -join ', ')"
}

# Managed AnyCPU helper executables remain portable. The native host and runtime
# must agree with the RID; a renamed or mixed publish tree is not a valid package.
$expectedMachine = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
foreach ($nativeFile in @(
    'SIDEY.Host.exe', 'coreclr.dll', 'clrjit.dll', 'hostfxr.dll',
    'hostpolicy.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll')) {
    $nativePath = Join-Path $runtimeDirectoryPath $nativeFile
    $stream = [IO.File]::OpenRead($nativePath)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Invalid PE image: $nativePath"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt $stream.Length - 6) {
            throw "Invalid PE header offset: $nativePath"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature: $nativePath"
        }
        if ($reader.ReadUInt16() -ne $expectedMachine) {
            throw "Published native binary does not match ${Architecture}: $nativePath"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$versionPropertiesPath = Join-Path $repositoryRootPath 'windows/Version.props'
$versionProperties = [xml](Get-Content -LiteralPath $versionPropertiesPath -Raw -Encoding UTF8)
$expectedProductVersion = [string]$versionProperties.Project.PropertyGroup.SideyProductVersion
$expectedFileVersion = [string]$versionProperties.Project.PropertyGroup.SideyMsixVersion
$hostVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $runtimeDirectoryPath 'SIDEY.Host.exe'))
if ($hostVersionInfo.ProductVersion -cne $expectedProductVersion) {
    throw "Published SIDEY.Host.exe ProductVersion does not match: $($hostVersionInfo.ProductVersion) / $expectedProductVersion"
}
if ($hostVersionInfo.FileVersion -cne $expectedFileVersion) {
    throw "Published SIDEY.Host.exe FileVersion does not match: $($hostVersionInfo.FileVersion) / $expectedFileVersion"
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectoryPath -Recurse -File)
$unexpectedPackages = @($publishedFiles | Where-Object {
    $_.Extension -in @('.msix', '.msixbundle', '.appx', '.appxbundle') -or
    $_.Name -match '^(dotnet|windowsdesktop|aspnetcore)-runtime.*\.exe$' -or
    $_.Name -match '^(vc_redist|windowsappruntimeinstall).*\.exe$'
})
if ($unexpectedPackages.Count -gt 0) {
    throw "Runtime installer or package payload found: $($unexpectedPackages.Name -join ', ')"
}

# PowerToys statically links most native modules and carries VC runtime DLLs only
# beside binaries that import them. SIDEY currently has no C++ project, so reject
# any future dynamic VC runtime import unless its app-local DLL is also published.
$nativeRuntimeNames = @(
    'msvcp140.dll',
    'msvcp140_1.dll',
    'msvcp140_2.dll',
    'msvcp140_app.dll',
    'vcruntime140.dll',
    'vcruntime140_1.dll',
    'vcruntime140_app.dll',
    'vcruntime140_1_app.dll'
)
$nativeRuntimeImports = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
# PowerShell 7 callers may promote findstr's expected no-match exit code to an
# error record. Keep the explicit 0/1/>1 contract below in both supported hosts.
$PSNativeCommandUseErrorActionPreference = $false
foreach ($runtimeName in $nativeRuntimeNames) {
    $matches = @(& findstr.exe /S /M /I "/C:$runtimeName" `
        (Join-Path $runtimeDirectoryPath '*.dll') `
        (Join-Path $runtimeDirectoryPath '*.exe') 2>$null)
    $findExitCode = $LASTEXITCODE
    if ($findExitCode -eq 0 -and $matches.Count -gt 0) {
        [void]$nativeRuntimeImports.Add($runtimeName)
    }
    elseif ($findExitCode -gt 1) {
        throw "VC runtime import scan failed for $runtimeName with exit code $findExitCode."
    }
}
# A successful scan may end with findstr's expected no-match code. Do not let
# that implementation detail become this entry script's process exit status.
$global:LASTEXITCODE = 0
$missingNativeRuntimes = @($nativeRuntimeImports | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $runtimeDirectoryPath $_) -PathType Leaf)
})
if ($missingNativeRuntimes.Count -gt 0) {
    throw "Native binaries import VC runtime DLLs that are not app-local: $($missingNativeRuntimes -join ', ')"
}

& (Join-Path $PSScriptRoot 'Test-ImpactAudioAssets.ps1') `
    -AssetsDirectory (Join-Path $publishDirectoryPath 'Assets')
Write-Host "SelfContainedPublish=true; DotNetRuntime=$runtimePackVersion; WindowsAppSDK=$(($windowsAppSdk[0] -split '/', 2)[1]); VCRuntimeImports=$($nativeRuntimeImports.Count); Files=$($publishedFiles.Count); Bytes=$(($publishedFiles | Measure-Object Length -Sum).Sum); RuntimeDirectory=Runtime; Architecture=$Architecture"
