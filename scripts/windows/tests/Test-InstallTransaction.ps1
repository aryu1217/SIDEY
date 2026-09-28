#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$HelperPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('SIDEY install transaction ' + [Guid]::NewGuid().ToString('N'))
$transactionExecutable = if ([string]::IsNullOrWhiteSpace($HelperPath)) {
    Join-Path $probeRoot 'Sidey.InstallTransaction.exe'
}
else {
    (Resolve-Path -LiteralPath $HelperPath).Path
}
$install = Join-Path $probeRoot 'SIDEY'
$staging = $install + '.sidey-staging-1234'
$rollback = $install + '.sidey-rollback'
$logPath = Join-Path $probeRoot 'install-transaction.log'
$productVersion = '9.8.7'
$updateVersion = '9.8.7000'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

function ConvertTo-NativeArgument([string]$Value) {
    return '"' + $Value.Replace('\', '\').Replace('"', '\"') + '"'
}

function Invoke-Helper([string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $transactionExecutable
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [ordered]@{
            ExitCode = $process.ExitCode
            ErrorText = $errorText.Trim()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-TransactionArguments(
    [string]$Action,
    [string]$StagingDirectory = $staging,
    [switch]$UseRealParentPolicy,
    [string]$InstallDirectory = $install,
    [string]$LogFile = $logPath,
    [switch]$OmitLogPath
) {
    $arguments = @(
        '--action', (ConvertTo-NativeArgument $Action),
        '--install-directory', (ConvertTo-NativeArgument $InstallDirectory),
        '--staging-directory', (ConvertTo-NativeArgument $StagingDirectory),
        '--rollback-directory', (ConvertTo-NativeArgument ($InstallDirectory + '.sidey-rollback')),
        '--product-version', (ConvertTo-NativeArgument $productVersion),
        '--update-version', (ConvertTo-NativeArgument $updateVersion)
    )
    if (-not $OmitLogPath) {
        $arguments += @('--log-path', (ConvertTo-NativeArgument $LogFile))
    }
    if (-not $UseRealParentPolicy) {
        $arguments += '--allow-user-writable-parent-for-tests'
    }
    return $arguments -join ' '
}

function Start-TransactionProcess([string]$Action) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $transactionExecutable
    $start.Arguments = Get-TransactionArguments $Action
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    return [Diagnostics.Process]::Start($start)
}

function Wait-TransactionPhase(
    [Diagnostics.Process]$Process,
    [string]$Phase,
    [int]$TimeoutMilliseconds
) {
    $statePath = $install + '.sidey-transaction.json'
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if ([IO.File]::Exists($statePath)) {
            try {
                $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($state.phase -ceq $Phase) {
                    return $true
                }
            }
            catch {
                # The helper can replace the state file while this process reads it.
            }
        }
        if ($Process.HasExited) {
            return $false
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $false
}

function Invoke-Transaction(
    [string]$Action,
    [string]$StagingDirectory = $staging,
    [switch]$AllowFailure
) {
    $arguments = Get-TransactionArguments $Action $StagingDirectory
    $result = Invoke-Helper $arguments
    if ($AllowFailure) {
        return $result.ExitCode
    }
    if ($result.ExitCode -ne 0) {
        throw "Install transaction failed. Action=$Action ExitCode=$($result.ExitCode) Error=$($result.ErrorText)"
    }
}

function New-Payload([string]$Marker) {
    [IO.Directory]::CreateDirectory((Join-Path $staging 'Runtime')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $staging 'SIDEY.exe'), $Marker)
    [IO.File]::WriteAllText((Join-Path $staging 'Runtime/SIDEY.Host.exe'), $Marker)
    [IO.File]::WriteAllText((Join-Path $staging 'Runtime/SIDEY.UninstallHelper.exe'), $Marker)
    [IO.File]::WriteAllText((Join-Path $staging 'Uninstall.exe'), $Marker)
}

function Assert-FailureDiagnostic(
    [string]$Action,
    [string]$Step,
    [string]$ExceptionType
) {
    Assert-True ([IO.File]::Exists($logPath)) `
        "A failed $Action transaction did not write a support diagnostic."
    $diagnostic = [IO.File]::ReadAllText($logPath)
    $lines = @($diagnostic -split '\r?\n')
    Assert-True ($lines -ccontains '[InstallTransaction]') `
        "A failed $Action transaction did not identify the helper."
    Assert-True ($lines -ccontains "action=$Action") `
        "A failed $Action transaction did not identify its action."
    Assert-True ($lines -ccontains "step=$Step") `
        "A failed $Action transaction did not identify the failed substep."
    Assert-True ($lines -ccontains "exception=$ExceptionType") `
        "A failed $Action transaction did not identify its exception type."
    Assert-True ($diagnostic -cmatch '(?m)^hresult=0x[0-9A-F]{8}\r?$') `
        "A failed $Action transaction did not record a hexadecimal HRESULT."
    Assert-True (-not $diagnostic.Contains($probeRoot)) `
        "A failed $Action diagnostic exposed the local test directory."
}

function Set-DeletionRestrictedPayload([string]$Path) {
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force) {
        [IO.File]::SetAttributes(
            $file.FullName,
            $file.Attributes -bor [IO.FileAttributes]::ReadOnly `
                -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System)
    }
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Recurse -Force) + `
        @(Get-Item -LiteralPath $Path -Force)
    foreach ($directory in $directories) {
        [IO.File]::SetAttributes(
            $directory.FullName,
            $directory.Attributes -bor [IO.FileAttributes]::ReadOnly `
                -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System)
    }
}

try {
    [IO.Directory]::CreateDirectory($probeRoot) | Out-Null
    if ([string]::IsNullOrWhiteSpace($HelperPath)) {
        & (Join-Path $root 'scripts/windows/New-SideyHelperExecutable.ps1') `
            -SourcePath (Join-Path $root 'windows/installer/Sidey.Setup/InstallTransaction.cs') `
            -OutputPath $transactionExecutable -Version $productVersion -FileVersion "$updateVersion.0" `
            -Title 'SIDEY Install Transaction' `
            -Description 'SIDEY atomic install transaction helper' `
            -IconPath (Join-Path $root 'windows/src/Sidey.App/Assets/Icons/SideyAppIcon.ico')
    }
    Assert-True ([IO.File]::Exists($transactionExecutable)) `
        'The compiled install transaction helper was not created.'

    [IO.Directory]::CreateDirectory($install) | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'marker.txt'), 'old')

    $legacyInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectLegacyLocation' -UseRealParentPolicy)
    Assert-True ($legacyInspection.ExitCode -eq 79) `
        'A clean legacy install under a user-writable parent was not eligible for relocation.'
    Assert-True ([IO.File]::Exists((Join-Path $install 'marker.txt'))) `
        'Legacy inspection changed the previous installation.'
    $statePath = $install + '.sidey-transaction.json'
    [IO.File]::WriteAllText($statePath, 'not-json')
    $legacyInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectLegacyLocation' -UseRealParentPolicy)
    Assert-True ($legacyInspection.ExitCode -eq 1) `
        'Legacy inspection ignored a pending transaction state.'
    Assert-True ([IO.File]::Exists($statePath)) `
        'Legacy inspection removed a pending transaction state.'
    [IO.File]::Delete($statePath)
    [IO.File]::WriteAllText($statePath + '.tmp', 'incomplete')
    $legacyInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectLegacyLocation' -UseRealParentPolicy)
    Assert-True ($legacyInspection.ExitCode -eq 1) `
        'Legacy inspection ignored an unpublished transaction state.'
    [IO.File]::Delete($statePath + '.tmp')
    [IO.Directory]::CreateDirectory($rollback) | Out-Null
    $legacyInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectLegacyLocation' -UseRealParentPolicy)
    Assert-True ($legacyInspection.ExitCode -eq 1) `
        'Legacy inspection ignored a rollback directory.'
    Assert-True ([IO.Directory]::Exists($rollback)) `
        'Legacy inspection removed a rollback directory.'
    Remove-Item -LiteralPath $rollback -Recurse -Force
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $legacyInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectLegacyLocation' -UseRealParentPolicy)
    Assert-True ($legacyInspection.ExitCode -eq 1) `
        'Legacy inspection ignored a staging directory.'
    Assert-True ([IO.Directory]::Exists($staging)) `
        'Legacy inspection removed a staging directory.'
    Remove-Item -LiteralPath $staging -Recurse -Force
    $relocationTargetInspection = Invoke-Helper (Get-TransactionArguments `
        'InspectRelocationTarget')
    Assert-True ($relocationTargetInspection.ExitCode -eq 1) `
        'Relocation target inspection accepted an existing installation.'
    $relocationTarget = Join-Path $probeRoot 'relocation-target'
    $targetArguments = Get-TransactionArguments 'InspectRelocationTarget' `
        ($relocationTarget + '.sidey-staging-1234') -InstallDirectory $relocationTarget
    $unsafeTargetArguments = Get-TransactionArguments 'InspectRelocationTarget' `
        ($relocationTarget + '.sidey-staging-1234') -InstallDirectory $relocationTarget `
        -UseRealParentPolicy
    $relocationTargetInspection = Invoke-Helper $unsafeTargetArguments
    Assert-True ($relocationTargetInspection.ExitCode -eq 1) `
        'Relocation target inspection accepted a user-writable parent.'
    $relocationTargetInspection = Invoke-Helper $targetArguments
    Assert-True ($relocationTargetInspection.ExitCode -eq 0) `
        'Relocation target inspection rejected an empty test target.'
    [IO.File]::WriteAllText($relocationTarget + '.sidey-transaction.json', 'pending')
    $relocationTargetInspection = Invoke-Helper $targetArguments
    Assert-True ($relocationTargetInspection.ExitCode -eq 1) `
        'Relocation target inspection accepted a pending transaction.'
    [IO.File]::Delete($relocationTarget + '.sidey-transaction.json')

    Invoke-Transaction Prepare
    $initialState = Get-Content -LiteralPath ($install + '.sidey-transaction.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($initialState.schemaVersion -eq 2) 'Prepare did not write the split-version transaction schema.'
    Assert-True ($initialState.productVersion -ceq $productVersion) `
        'Transaction state did not preserve the display Product Version.'
    Assert-True ($initialState.updateVersion -ceq $updateVersion) `
        'Transaction state did not preserve the internal Windows update version.'
    $initialCompletionId = [Guid]::ParseExact($initialState.completionId, 'N')
    Assert-True ($initialCompletionId -ne [Guid]::Empty) 'Prepare did not assign an installation identity.'
    New-Payload 'new'
    Invoke-Transaction Activate
    $activeState = Get-Content -LiteralPath ($install + '.sidey-transaction.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($activeState.completionId -ceq $initialState.completionId) `
        'Activation changed the desktop-completion identity.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'new') `
        'Activate did not promote the staged payload.'
    Assert-True ([IO.File]::Exists((Join-Path $rollback 'marker.txt'))) `
        'Activate did not retain the previous install for rollback.'
    Set-DeletionRestrictedPayload $install
    Assert-True (((Get-Item -LiteralPath (Join-Path $install 'SIDEY.exe') -Force).Attributes `
            -band [IO.FileAttributes]::ReadOnly) -ne 0) `
        'The rollback test payload was not made read-only.'
    Invoke-Transaction Rollback
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'marker.txt') -Raw) -ceq 'old') `
        'Rollback did not restore the previous install.'
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'Rollback left its backup directory behind.'

    Remove-Item -LiteralPath $install -Recurse -Force
    [IO.Directory]::CreateDirectory((Join-Path $install 'Runtime')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'SIDEY.exe'), 'framework-dependent-launcher')
    [IO.File]::WriteAllText((Join-Path $install 'Runtime/SIDEY.Host.exe'), 'framework-dependent-host')
    [IO.File]::WriteAllText(
        (Join-Path $install 'Runtime/SIDEY.Host.runtimeconfig.json'),
        '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
    Invoke-Transaction Prepare
    $nextState = Get-Content -LiteralPath ($install + '.sidey-transaction.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($nextState.completionId -cne $initialState.completionId) `
        'A new installation reused the prior desktop-completion identity.'
    New-Payload 'self-contained'
    $lockedFrameworkDependentHost = [IO.File]::Open(
        (Join-Path $install 'Runtime/SIDEY.Host.exe'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $activationProcess = $null
    try {
        $activationProcess = Start-TransactionProcess 'Activate'
        Assert-True (Wait-TransactionPhase $activationProcess 'prepared' 5000) `
            'Activate did not reach the prepared phase while the framework-dependent host was locked.'
        Assert-True (-not $activationProcess.WaitForExit(500)) `
            'Activate did not remain alive while retrying the transient framework-dependent host lock.'
        $lockedFrameworkDependentHost.Dispose()
        $lockedFrameworkDependentHost = $null
        Assert-True ($activationProcess.WaitForExit(10000)) `
            'Activate did not finish after the framework-dependent host lock was released.'
        $activationError = $activationProcess.StandardError.ReadToEnd().Trim()
        Assert-True ($activationProcess.ExitCode -eq 0) `
            "Activate did not retry a transient framework-dependent host lock: $activationError"
    }
    finally {
        if ($null -ne $activationProcess) {
            if (-not $activationProcess.HasExited) {
                $activationProcess.Kill()
                $activationProcess.WaitForExit()
            }
            $activationProcess.Dispose()
        }
        if ($null -ne $lockedFrameworkDependentHost) {
            $lockedFrameworkDependentHost.Dispose()
        }
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'self-contained') `
        'Activate did not promote the self-contained payload after the transient lock cleared.'
    Assert-True ([IO.File]::Exists((Join-Path $rollback 'Runtime/SIDEY.Host.runtimeconfig.json'))) `
        'Activate did not preserve the framework-dependent installation for rollback.'
    Invoke-Transaction Rollback

    Invoke-Transaction Prepare
    New-Payload 'blocked-self-contained'
    $persistentlyLockedHost = [IO.File]::Open(
        (Join-Path $install 'Runtime/SIDEY.Host.exe'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $blockedActivationProcess = $null
    try {
        $blockedActivationProcess = Start-TransactionProcess 'Activate'
        Assert-True (Wait-TransactionPhase $blockedActivationProcess 'prepared' 5000) `
            'Activate did not reach the prepared phase for a persistent host lock.'
        Assert-True (-not $blockedActivationProcess.WaitForExit(1000)) `
            'Activate failed immediately instead of retrying the persistent host lock.'
        Assert-True ($blockedActivationProcess.WaitForExit(10000)) `
            'Activate did not stop after the bounded retry window for a persistent host lock.'
        $blockedActivationError = $blockedActivationProcess.StandardError.ReadToEnd().Trim()
        Assert-True ($blockedActivationProcess.ExitCode -ne 0) `
            "Activate accepted a persistently locked framework-dependent host: $blockedActivationError"
    }
    finally {
        if ($null -ne $blockedActivationProcess) {
            if (-not $blockedActivationProcess.HasExited) {
                $blockedActivationProcess.Kill()
                $blockedActivationProcess.WaitForExit()
            }
            $blockedActivationProcess.Dispose()
        }
        $persistentlyLockedHost.Dispose()
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'Runtime/SIDEY.Host.exe') -Raw) `
            -ceq 'framework-dependent-host') `
        'A persistent activation lock changed the existing framework-dependent install.'
    Invoke-Transaction Rollback

    Invoke-Transaction Prepare
    New-Payload 'dual-failure-diagnostic'
    [IO.File]::Delete($logPath)
    $lockedHostForRollbackFailure = [IO.File]::Open(
        (Join-Path $install 'Runtime/SIDEY.Host.exe'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $lockedStagingForRollbackFailure = [IO.File]::Open(
        (Join-Path $staging 'Runtime/SIDEY.Host.exe'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $dualFailureProcess = $null
    try {
        $dualFailureProcess = Start-TransactionProcess 'Activate'
        Assert-True (Wait-TransactionPhase $dualFailureProcess 'prepared' 5000) `
            'Activate did not reach the prepared phase for the dual-failure diagnostic.'
        Assert-True (-not $dualFailureProcess.WaitForExit(500)) `
            'Activate did not retry the locked live directory before rollback.'
        Assert-True ($dualFailureProcess.WaitForExit(10000)) `
            'Activate did not finish after the bounded move retry and failed rollback.'
        Assert-True ($dualFailureProcess.ExitCode -eq 1) `
            'A failed activation with failed rollback did not return the transaction failure code.'
        $dualFailureLog = [IO.File]::ReadAllText($logPath)
        $diagnostics = @([regex]::Matches(
            $dualFailureLog,
            '(?s)\[InstallTransaction\]\r?\n.*?(?:\r?\n){2}'))
        Assert-True ($diagnostics.Count -eq 2) `
            'Activation and rollback failures did not produce two diagnostic records.'
        Assert-True ($diagnostics[0].Value.Contains('step=activate.move-live')) `
            'The first diagnostic did not identify the activation move failure.'
        Assert-True ($diagnostics[0].Value -match '(?m)^exception=(IOException|UnauthorizedAccessException)\r?$') `
            'The first diagnostic did not identify the activation move exception.'
        Assert-True ($diagnostics[1].Value.Contains('step=activate.undo.remove-staging')) `
            'The second diagnostic did not identify the rollback staging-removal failure.'
        Assert-True ($diagnostics[1].Value -match '(?m)^exception=(IOException|UnauthorizedAccessException)\r?$') `
            'The second diagnostic did not identify the rollback exception.'
        Assert-True (-not $dualFailureLog.Contains($probeRoot)) `
            'A dual-failure diagnostic exposed the local test directory.'
    }
    finally {
        if ($null -ne $dualFailureProcess) {
            if (-not $dualFailureProcess.HasExited) {
                $dualFailureProcess.Kill()
                $dualFailureProcess.WaitForExit()
            }
            $dualFailureProcess.Dispose()
        }
        $lockedHostForRollbackFailure.Dispose()
        $lockedStagingForRollbackFailure.Dispose()
    }
    Invoke-Transaction Rollback
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'Runtime/SIDEY.Host.exe') -Raw) `
            -ceq 'framework-dependent-host') `
        'Cleanup after the diagnostic test changed the previous installation.'
    Remove-Item -LiteralPath $install -Recurse -Force
    [IO.Directory]::CreateDirectory($install) | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'marker.txt'), 'old')

    $rolledBackState = [ordered]@{
        schemaVersion = 1
        phase = 'rolled-back'
        version = $productVersion
        installDirectory = $install
        stagingDirectory = $staging
        rollbackDirectory = $rollback
        previousInstallExisted = $true
        previousRegistration = [ordered]@{ managed = $false }
    }
    [IO.File]::WriteAllText(
        ($install + '.sidey-transaction.json'),
        ($rolledBackState | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false))
    Invoke-Transaction Prepare
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'marker.txt') -Raw) -ceq 'old') `
        'A stale rolled-back marker removed the restored live installation.'
    Invoke-Transaction Rollback

    Invoke-Transaction Prepare
    New-Payload 'new-committed'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    Set-DeletionRestrictedPayload $rollback
    Assert-True (((Get-Item -LiteralPath (Join-Path $rollback 'marker.txt') -Force).Attributes `
            -band [IO.FileAttributes]::ReadOnly) -ne 0) `
        'The Complete test backup was not made read-only.'
    Invoke-Transaction Complete
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'new-committed') `
        'Complete did not keep the committed payload.'
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'Complete did not remove the previous install.'
    Assert-True (-not [IO.File]::Exists($install + '.sidey-transaction.json')) `
        'Complete did not remove transaction state.'

    Invoke-Transaction Prepare
    New-Payload 'cleanup-retry'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    $lockedBackup = [IO.File]::Open(
        (Join-Path $rollback 'SIDEY.exe'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        [IO.File]::Delete($logPath)
        $cleanupExitCode = Invoke-Transaction Complete -AllowFailure
        Assert-True ($cleanupExitCode -eq 10) `
            'Complete did not report retryable cleanup failure with exit code 10.'
        Assert-FailureDiagnostic 'Complete' 'complete.remove-rollback' 'IOException'
        Assert-True ([IO.File]::Exists($install + '.sidey-transaction.json')) `
            'A failed cleanup removed transaction state needed for retry.'
    }
    finally {
        $lockedBackup.Dispose()
    }
    Invoke-Transaction Complete
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'A retried Complete did not remove the previous install.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'cleanup-retry') `
        'A retried Complete did not preserve the committed live payload.'

    Remove-Item -LiteralPath $install -Recurse -Force
    Invoke-Transaction Prepare
    New-Payload 'fresh-registering'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Rollback
    Assert-True (-not [IO.Directory]::Exists($install)) `
        'Registration rollback left a fresh payload orphaned.'
    [IO.Directory]::CreateDirectory($install) | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'SIDEY.exe'), 'restored-test-baseline')

    Invoke-Transaction Prepare
    [IO.File]::WriteAllText((Join-Path $staging 'partial.txt'), 'partial')
    Invoke-Transaction Prepare
    Assert-True ([IO.File]::Exists((Join-Path $install 'SIDEY.exe'))) `
        'Recovering an interrupted staging pass removed the live install.'
    Assert-True (-not [IO.File]::Exists((Join-Path $staging 'partial.txt'))) `
        'Prepare did not discard an interrupted partial staging payload.'

    Remove-Item -LiteralPath $staging -Recurse -Force
    Remove-Item -LiteralPath ($install + '.sidey-transaction.json') -Force
    Remove-Item -LiteralPath $install -Recurse -Force
    [IO.Directory]::CreateDirectory($install) | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'marker.txt'), 'interrupted-old')
    Invoke-Transaction Prepare
    New-Payload 'interrupted-new'
    Invoke-Transaction Activate
    Invoke-Transaction Prepare
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'marker.txt') -Raw) -ceq 'interrupted-old') `
        'Prepare did not recover an uncommitted interrupted activation.'
    Assert-True ([IO.Directory]::Exists($staging)) `
        'Prepare did not create a clean staging directory after recovery.'

    Remove-Item -LiteralPath $staging -Recurse -Force
    New-Payload 'pending-cleanup-new'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    Invoke-Transaction Prepare
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'pending-cleanup-new') `
        'Prepare discarded a committed payload awaiting cleanup.'
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'Prepare did not finish cleanup for a committed transaction.'

    Remove-Item -LiteralPath $staging -Recurse -Force
    New-Payload 'uninstall-cleanup-new'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    Set-DeletionRestrictedPayload $rollback
    Assert-True (((Get-Item -LiteralPath (Join-Path $rollback 'SIDEY.exe') -Force).Attributes `
            -band [IO.FileAttributes]::ReadOnly) -ne 0) `
        'The uninstall cleanup test backup was not made read-only.'
    Invoke-Transaction CleanupForUninstall
    Assert-True ([IO.File]::Exists((Join-Path $install 'SIDEY.exe'))) `
        'Uninstall cleanup removed the committed live payload.'
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'Uninstall cleanup left the previous-version backup behind.'
    Assert-True (-not [IO.File]::Exists($install + '.sidey-transaction.json')) `
        'Uninstall cleanup left transaction state behind.'

    Invoke-Transaction Prepare
    New-Payload 'committed-before-uninstall'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    Remove-Item -LiteralPath $install -Recurse -Force
    Invoke-Transaction Prepare
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'SIDEY.exe') -Raw) -ceq 'uninstall-cleanup-new') `
        'Prepare did not restore the last known-good backup when committed live was missing.'
    Assert-True (-not [IO.Directory]::Exists($rollback)) `
        'Prepare retained the backup after restoring a missing committed live install.'
    Assert-True ([IO.Directory]::Exists($staging)) `
        'Prepare did not start a clean transaction after committed-install removal.'
    New-Payload 'replacement-after-uninstall'
    Invoke-Transaction Activate
    Invoke-Transaction BeginRegistration
    Invoke-Transaction Commit
    Invoke-Transaction Complete

    Invoke-Transaction Prepare
    [IO.File]::WriteAllText((Join-Path $staging 'SIDEY.exe'), 'incomplete')
    $failedExitCode = Invoke-Transaction Activate -AllowFailure
    Assert-True ($failedExitCode -ne 0) 'Activate accepted an incomplete staged payload.'
    Assert-True ([IO.File]::Exists((Join-Path $install 'SIDEY.exe'))) `
        'An incomplete staged payload changed the live install.'
    Invoke-Transaction Rollback

    [IO.File]::Delete($logPath)
    [IO.Directory]::CreateDirectory($rollback) | Out-Null
    [IO.File]::WriteAllText((Join-Path $rollback 'marker.txt'), 'preserve')
    $recoveryExitCode = Invoke-Transaction Recover -AllowFailure
    Assert-True ($recoveryExitCode -eq 72) `
        'Recovery did not report the rollback-conflict exit code.'
    $recoveryLog = [IO.File]::ReadAllText($logPath)
    Assert-True ($recoveryLog.Contains('operation=recover.inspect-orphan')) `
        'A failed recovery did not identify its failed operation in the support log.'
    Assert-True ($recoveryLog.Contains('exception=InvalidOperationException')) `
        'A failed recovery did not identify the exception type in the support log.'
    Assert-True (-not $recoveryLog.Contains($probeRoot)) `
        'A recovery diagnostic exposed the local test directory.'
    Assert-True ([IO.File]::Exists((Join-Path $rollback 'marker.txt'))) `
        'A failed recovery removed an unrecognized rollback directory.'
    Remove-Item -LiteralPath $rollback -Recurse -Force
    Invoke-Transaction Recover

    $statePath = $install + '.sidey-transaction.json'
    [IO.File]::WriteAllText($statePath, 'not-json')
    $invalidStateExitCode = Invoke-Transaction Recover -AllowFailure
    Assert-True ($invalidStateExitCode -eq 71) `
        'Recovery did not report the invalid-state exit code.'
    Assert-True ([IO.File]::Exists($statePath)) `
        'Recovery discarded an invalid state file needed for diagnosis.'
    [IO.File]::Delete($statePath)

    [IO.File]::WriteAllText($statePath, '{}')
    $lockedState = [IO.File]::Open(
        $statePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $stateIoExitCode = Invoke-Transaction Recover -AllowFailure
        Assert-True ($stateIoExitCode -eq 77) `
            'Recovery did not report the filesystem-I/O exit code.'
    }
    finally {
        $lockedState.Dispose()
    }
    [IO.File]::Delete($statePath)

    $unsafeExitCode = Invoke-Transaction Prepare `
        -StagingDirectory (Join-Path $probeRoot 'not-a-sidey-stage') -AllowFailure
    Assert-True ($unsafeExitCode -ne 0) `
        'The transaction accepted an unrelated staging directory.'

    [IO.File]::Delete($logPath)
    [IO.Directory]::CreateDirectory($rollback) | Out-Null
    [IO.File]::WriteAllText((Join-Path $rollback 'marker.txt'), 'preserve')
    $prepareFailure = Invoke-Transaction Prepare -AllowFailure
    Assert-True ($prepareFailure -eq 1) `
        'Prepare did not report an orphaned rollback directory as a failure.'
    Assert-FailureDiagnostic 'Prepare' 'prepare.recover' 'InvalidOperationException'
    Assert-True ([IO.File]::Exists((Join-Path $rollback 'marker.txt'))) `
        'A failed Prepare removed an unrecognized rollback directory.'
    Remove-Item -LiteralPath $rollback -Recurse -Force

    Invoke-Transaction Prepare
    [IO.File]::WriteAllText((Join-Path $staging 'SIDEY.exe'), 'incomplete')
    [IO.File]::Delete($logPath)
    $activateFailure = Invoke-Transaction Activate -AllowFailure
    Assert-True ($activateFailure -eq 1) `
        'Activate did not reject an incomplete staged payload.'
    Assert-FailureDiagnostic 'Activate' 'activate.verify-payload.host' 'InvalidOperationException'
    Assert-True ([IO.File]::Exists((Join-Path $install 'SIDEY.exe'))) `
        'A failed Activate replaced the existing installation.'
    Invoke-Transaction Rollback

    [IO.File]::WriteAllText($statePath, 'not-json')
    [IO.File]::Delete($logPath)
    $recoverFailure = Invoke-Transaction Recover -AllowFailure
    Assert-True ($recoverFailure -eq 71) `
        'Recover did not preserve the invalid-state exit code.'
    Assert-FailureDiagnostic 'Recover' 'recover.read-state' 'InvalidOperationException'
    Assert-True ([IO.File]::Exists($statePath)) `
        'A failed Recover removed the invalid state file.'

    [IO.File]::Delete($logPath)
    $rollbackFailure = Invoke-Transaction Rollback -AllowFailure
    Assert-True ($rollbackFailure -eq 1) `
        'Rollback did not report an invalid transaction state as a failure.'
    Assert-FailureDiagnostic 'Rollback' 'rollback.read-state' 'InvalidOperationException'
    Assert-True ([IO.File]::Exists($statePath)) `
        'A failed Rollback removed the invalid state file.'

    $unwritableLogPath = Join-Path $probeRoot 'log-path-is-a-directory'
    [IO.Directory]::CreateDirectory($unwritableLogPath) | Out-Null
    $unwritableLogResult = Invoke-Helper (Get-TransactionArguments `
        'Recover' -LogFile $unwritableLogPath)
    Assert-True ($unwritableLogResult.ExitCode -eq 71) `
        'A support-log write failure replaced the original recovery exit code.'
    $missingLogResult = Invoke-Helper (Get-TransactionArguments `
        'Recover' -OmitLogPath)
    Assert-True ($missingLogResult.ExitCode -eq 71) `
        'Omitting the optional support log changed the original recovery exit code.'
    Assert-True ([IO.File]::Exists($statePath)) `
        'A recovery diagnostic failure removed the invalid state file.'
    [IO.File]::Delete($statePath)

    [IO.File]::Delete($logPath)
    $privateArgument = Join-Path $probeRoot 'private-argument'
    $invalidLoggedResult = Invoke-Helper ((Get-TransactionArguments 'Recover') +
        ' --unsupported ' + (ConvertTo-NativeArgument $privateArgument))
    Assert-True ($invalidLoggedResult.ExitCode -eq 64) `
        'An unsupported argument with a support log did not return the parse exit code.'
    Assert-FailureDiagnostic 'Parse' 'parse.arguments' 'ArgumentException'
    Assert-True (-not ([IO.File]::ReadAllText($logPath)).Contains($privateArgument)) `
        'An invalid-argument diagnostic exposed the rejected argument value.'

    $invalidResult = Invoke-Helper '--unsupported'
    Assert-True ($invalidResult.ExitCode -eq 64) `
        'The transaction helper did not reject unsupported arguments with exit code 64.'

    Write-Host "Install transaction tests passed. Assertions=$assertions"
}
finally {
    if ([IO.Directory]::Exists($probeRoot)) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}
