#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../Sidey.PowerShell.psm1') -Force
$repositoryRootPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$solutionPath = Join-Path $repositoryRootPath 'windows/SIDEY.Windows.slnx'
$publishDirectory = Join-Path $repositoryRootPath 'build/windows/publish-smoke'

Push-Location (Join-Path $repositoryRootPath 'windows')
try {
    Invoke-SideyNativeCommand `
        -FilePath 'dotnet' `
        -ArgumentList @('restore', $solutionPath) `
        -Description 'dotnet restore'
    Invoke-SideyNativeCommand `
        -FilePath 'dotnet' `
        -ArgumentList @('build', $solutionPath, '--configuration', 'Release', '--no-restore') `
        -Description 'Windows solution build'
    Invoke-SideyNativeCommand `
        -FilePath 'dotnet' `
        -ArgumentList @(
            'test', $solutionPath,
            '--configuration', 'Release',
            '--no-restore',
            '--no-build'
        ) `
        -Description 'Windows solution tests'
    Invoke-SideyNativeCommand `
        -FilePath 'dotnet' `
        -ArgumentList @(
            'publish',
            (Join-Path $repositoryRootPath 'windows/src/Sidey.App/Sidey.App.csproj'),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--no-restore',
            '-p:PublishSingleFile=false',
            '--output', $publishDirectory
        ) `
        -Description 'Windows smoke publish'
    & (Join-Path $PSScriptRoot 'Test-PowerShellSupport.ps1')
    & (Join-Path $PSScriptRoot 'Test-WindowsReleaseCandidateContract.ps1')
    & (Join-Path $PSScriptRoot 'Test-PublishedApplicationTimeouts.ps1')
    & (Join-Path $PSScriptRoot 'Test-SelfContainedPublish.ps1') `
        -PublishDirectory $publishDirectory
    & (Join-Path $PSScriptRoot 'Test-PublishedApplication.ps1') `
        -PublishDirectory $publishDirectory
}
finally {
    Pop-Location
}
