#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SelectorExecutablePath,
    [string]$SetupScriptPath,
    [string]$OutputDirectory,
    [string]$MakensisPath = 'C:/Program Files (x86)/NSIS/makensis.exe'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../Sidey.PowerShell.psm1') -Force
$repositoryRootPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not $SetupScriptPath) {
    $SetupScriptPath = Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/Sidey.Setup.nsi'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRootPath 'build/windows/incremental-update-launch'
}
$selectorPath = (Resolve-Path -LiteralPath $SelectorExecutablePath).Path
[void][Reflection.Assembly]::LoadFile($selectorPath)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$setupSource = Get-Content -LiteralPath $SetupScriptPath -Raw -Encoding UTF8
$validationFunction = [regex]::Match($setupSource, '(?ms)^Function ValidateIncrementalLaunch\r?\n.*?^FunctionEnd').Value
$prepareFunction = [regex]::Match($setupSource, '(?ms)^Function PrepareInstallerActivation\r?\n.*?^FunctionEnd').Value
if (-not $validationFunction -or -not $prepareFunction) {
    throw 'Production incremental launch validation or helper preparation function is missing.'
}
$startup = [regex]::Match($setupSource, '(?ms)^Function \.onInit\r?\n.*?^FunctionEnd').Value
$gate = [regex]::Match($startup, '\bCall\s+ValidateIncrementalLaunch\b')
foreach ($laterStep in @('\bCall\s+SelectInstallerLanguage\b', '\bReadRegStr\s+\$LANGUAGE\b')) {
    $step = [regex]::Match($startup, $laterStep)
    if (-not $gate.Success -or -not $step.Success -or $gate.Index -ge $step.Index) {
        throw 'Production launch validation must precede installed-language lookup and the language picker.'
    }
}

# Execute only the production argument gate and helper extraction. The fixture
# has no installation payload, registry writes, elevation, or language picker.
$fixture = @'
Unicode true
RequestExecutionLevel user
Name "SIDEY incremental launch test"
OutFile "@OUTPUT@\IncrementalUpdateLaunch.exe"
!define INCREMENTAL_PAYLOAD
!define LANGUAGE_SELECTOR_EXE "@SELECTOR@"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
Var UpdateCacheDirectory
Function .onInit
  Call ValidateIncrementalLaunch
  SetErrorLevel 42
  Quit
FunctionEnd
@VALIDATE@
@PREPARE@
Section
  Abort
SectionEnd
'@
function ConvertTo-NsisTestLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace('$', '$$').Replace('"', '$\"')
}
$fixture = $fixture.Replace('@OUTPUT@', (ConvertTo-NsisTestLiteral $outputPath)).
    Replace('@SELECTOR@', (ConvertTo-NsisTestLiteral $selectorPath)).
    Replace('@VALIDATE@', $validationFunction).Replace('@PREPARE@', $prepareFunction)
$fixturePath = Join-Path $outputPath 'IncrementalUpdateLaunch.nsi'
[IO.File]::WriteAllText($fixturePath, $fixture, [Text.UTF8Encoding]::new($true))
Invoke-SideyNativeCommand -FilePath $MakensisPath -ArgumentList @('/V2', $fixturePath) `
    -Description 'Incremental launch fixture compilation'
$fixtureExecutable = Join-Path $outputPath 'IncrementalUpdateLaunch.exe'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class IncrementalLaunchProbe {
    private delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    public static IntPtr[] Windows(uint process) {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == process && IsWindowVisible(window)) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static string Text(IntPtr window) {
        var text = new StringBuilder(4096);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    public static string[] ChildTexts(IntPtr window) {
        var texts = new List<string>();
        EnumChildWindows(window, delegate(IntPtr child, IntPtr state) {
            texts.Add(Text(child));
            return true;
        }, IntPtr.Zero);
        return texts.ToArray();
    }
    public static IntPtr AcceptButton(IntPtr window) {
        var buttons = new List<IntPtr>();
        EnumChildWindows(window, delegate(IntPtr child, IntPtr state) {
            var name = new StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == "Button") buttons.Add(child);
            return true;
        }, IntPtr.Zero);
        return buttons.Count == 1 ? buttons[0] : IntPtr.Zero;
    }
    public static string[] ChildControls(IntPtr window) {
        var controls = new List<string>();
        EnumChildWindows(window, delegate(IntPtr child, IntPtr state) {
            var name = new StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            controls.Add(GetDlgCtrlID(child) + ":" + name + ":" + Text(child));
            return true;
        }, IntPtr.Zero);
        return controls.ToArray();
    }
}
'@

function Stop-TestProcess {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Process.Id)" |
        Where-Object { $_.Name -eq 'Sidey.SetupLanguage.exe' })
    foreach ($child in $children) {
        Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if (-not $Process.HasExited) { $Process.Kill() }
    $Process.Dispose()
}

function Test-SilentLaunch {
    param([Parameter(Mandatory = $true)][string]$Arguments, [int]$ExpectedExitCode)
    $process = Start-Process -FilePath $fixtureExecutable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(10000)) { throw "Silent fixture did not exit: $Arguments" }
        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "Unexpected exit code $($process.ExitCode) for '$Arguments'; expected $ExpectedExitCode."
        }
    }
    finally { Stop-TestProcess -Process $process }
}

Test-SilentLaunch -Arguments '/S' -ExpectedExitCode 87
Test-SilentLaunch -Arguments '/S /UPDATECACHE=' -ExpectedExitCode 87
Test-SilentLaunch -Arguments '/S /UPDATECACHE=""' -ExpectedExitCode 87
Test-SilentLaunch -Arguments '/S /UPDATECACHE="C:\SIDEY launch fixture cache"' -ExpectedExitCode 42

$expectedMessage = [Sidey.Installer.InstallerLanguages]::UpdateLaunchWarning(
    [IncrementalLaunchProbe]::GetUserDefaultUILanguage())
foreach ($arguments in @('', '/UPDATECACHE=', '/UPDATECACHE=""')) {
    $startArguments = @{ FilePath = $fixtureExecutable; PassThru = $true; WindowStyle = 'Hidden' }
    if ($arguments) { $startArguments.ArgumentList = $arguments }
    $process = Start-Process @startArguments
    try {
        $dialog = [IntPtr]::Zero
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $helpers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id) AND Name = 'Sidey.SetupLanguage.exe'")
            if ($helpers.Count -gt 1) { throw 'Invalid update launch created multiple warning helpers.' }
            if ($helpers.Count -eq 1) {
                $windows = @([IncrementalLaunchProbe]::Windows($helpers[0].ProcessId))
                if ($windows.Count -gt 1) { throw 'Invalid update launch displayed multiple dialogs.' }
                if ($windows.Count -eq 1) { $dialog = $windows[0] }
            }
            if ($dialog -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 }
        } while ($dialog -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
        if ($dialog -eq [IntPtr]::Zero) { throw 'Invalid interactive update launch did not display its warning.' }
        if ([IncrementalLaunchProbe]::Text($dialog) -cne 'SIDEY') { throw 'Expected the SIDEY warning, not a language picker.' }
        if (@([IncrementalLaunchProbe]::ChildTexts($dialog)) -cnotcontains $expectedMessage) {
            throw 'Update warning does not match the Windows system UI language.'
        }
        $acceptButton = [IncrementalLaunchProbe]::AcceptButton($dialog)
        if ($acceptButton -eq [IntPtr]::Zero) { throw "Warning has no OK button: $([IncrementalLaunchProbe]::ChildControls($dialog) -join '; ')" }
        [void][IncrementalLaunchProbe]::PostMessage($acceptButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $process.WaitForExit(10000)) { throw 'Dismissed warning did not terminate the invalid update launch.' }
        if ($process.ExitCode -ne 87) { throw "Invalid interactive launch returned $($process.ExitCode), expected 87." }
    }
    finally { Stop-TestProcess -Process $process }
}
Write-Host 'IncrementalUpdateLaunch=PASS; SilentCases=4; InteractiveCases=3; InstallActions=none'
