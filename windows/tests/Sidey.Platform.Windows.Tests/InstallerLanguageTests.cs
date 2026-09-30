using System.Text.RegularExpressions;
using Sidey.Installer;

namespace Sidey.Platform.Windows.Tests;

public sealed class InstallerLanguageTests
{
    [Theory]
    [InlineData(1033, "This file is used by SIDEY for automatic updates. Update from within SIDEY, or download and run Setup for a manual update.")]
    [InlineData(1041, "このファイルはSIDEYの自動更新用です。SIDEYアプリ内で更新するか、手動で更新する場合はSetupインストーラーをダウンロードして実行してください。")]
    [InlineData(1042, "이 파일은 SIDEY의 자동 업데이트용입니다. SIDEY 앱에서 업데이트하거나, 수동으로 업데이트하려면 Setup 설치 파일을 다운로드하여 실행하세요.")]
    [InlineData(1049, "Этот файл используется SIDEY для автоматических обновлений. Обновите SIDEY через приложение или скачайте и запустите установщик Setup для обновления вручную.")]
    [InlineData(1058, "Цей файл використовується SIDEY для автоматичних оновлень. Оновіть SIDEY через застосунок або завантажте й запустіть інсталятор Setup для оновлення вручну.")]
    [InlineData(2052, "此文件用于 SIDEY 的自动更新。请在 SIDEY 应用内更新，或下载并运行 Setup 安装程序以手动更新。")]
    [InlineData(1028, "此檔案用於 SIDEY 的自動更新。請在 SIDEY 應用程式內更新，或下載並執行 Setup 安裝程式以手動更新。")]
    public void DirectUpdateLaunchWarningExplainsAutomaticAndManualUpdateRoutes(int systemLanguage, string expected)
    {
        Assert.Equal(expected, InstallerLanguages.UpdateLaunchWarning(systemLanguage));
    }

    [Theory]
    [InlineData(2057, 1033)]
    [InlineData(3081, 1033)]
    [InlineData(4100, 2052)]
    [InlineData(3076, 1028)]
    [InlineData(5124, 1028)]
    [InlineData(0x7c04, 1028)]
    [InlineData(0x0819, 1049)]
    [InlineData(1036, 1033)]
    [InlineData(0, 1033)]
    public void DirectUpdateLaunchWarningUsesDisplayLanguageVariantsAndEnglishFallback(int systemLanguage, int expectedLanguage)
    {
        Assert.Equal(
            InstallerLanguages.UpdateLaunchWarning(expectedLanguage),
            InstallerLanguages.UpdateLaunchWarning(systemLanguage));
    }

    [Theory]
    [InlineData(1033, 1033)]
    [InlineData(2057, 1033)]
    [InlineData(3081, 1033)]
    [InlineData(1041, 1041)]
    [InlineData(1042, 1042)]
    [InlineData(1049, 1049)]
    [InlineData(1058, 1058)]
    [InlineData(2052, 2052)]
    [InlineData(4100, 2052)]
    [InlineData(1028, 1028)]
    [InlineData(3076, 1028)]
    [InlineData(5124, 1028)]
    [InlineData(0x7c04, 1028)]
    [InlineData(1036, 0)]
    public void WindowsDisplayLanguageMapsToTheSupportedLanguage(int systemLanguage, int expected)
    {
        Assert.Equal(expected, InstallerLanguages.MatchSystemLanguage(systemLanguage));
    }

    [Theory]
    [InlineData(1033)]
    [InlineData(1041)]
    [InlineData(1042)]
    [InlineData(1049)]
    [InlineData(1058)]
    [InlineData(2052)]
    [InlineData(1028)]
    public void SystemLanguageIsFirstAndTheRestAreAlphabeticalWithoutDuplicates(int systemLanguage)
    {
        InstallerLanguage[] ordered = InstallerLanguages.Ordered(systemLanguage);
        Assert.Equal(systemLanguage, ordered[0].Id);
        Assert.Equal(7, ordered.Length);
        Assert.Equal(7, ordered.Select(language => language.Id).Distinct().Count());
        string[] remaining = [.. ordered.Skip(1).Select(language => language.EnglishName)];
        Assert.Equal(remaining.Order(StringComparer.Ordinal), remaining);
    }

    [Fact]
    public void UnsupportedDisplayLanguageUsesEnglishAndAnAlphabeticalList()
    {
        Assert.Equal(1033, InstallerLanguages.DefaultSelection(1036, 0));
        Assert.Equal(
            ["Chinese (Simplified)", "Chinese (Traditional)", "English", "Japanese", "Korean", "Russian", "Ukrainian"],
            InstallerLanguages.Ordered(1036).Select(language => language.EnglishName));
    }

    [Fact]
    public void SavedChoiceDoesNotChangeSystemFirstOrdering()
    {
        Assert.Equal(1058, InstallerLanguages.DefaultSelection(1042, 1058));
        Assert.Equal(1042, InstallerLanguages.Ordered(1042)[0].Id);
        Assert.Equal(1042, InstallerLanguages.DefaultSelection(1042, -1));
        Assert.Equal(1042, InstallerLanguages.DefaultSelection(1042, 1036));
    }

    [Theory]
    [InlineData(1033, "en-US")]
    [InlineData(1041, "ja-JP")]
    [InlineData(1042, "ko-KR")]
    [InlineData(1049, "ru-RU")]
    [InlineData(1058, "uk-UA")]
    [InlineData(2052, "zh-CN")]
    [InlineData(1028, "zh-TW")]
    [InlineData(1036, "")]
    public void InstallerChoiceMapsToTheAppCatalog(int installerLanguage, string expected)
    {
        Assert.Equal(expected, InstallerLanguages.AppLanguage(installerLanguage));
    }

    [Fact]
    public void AllInstallerLanguagesHaveEveryCustomStringAndPreservePlaceholders()
    {
        string source = File.ReadAllText(RepositoryPath("windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"))
            + Environment.NewLine + File.ReadAllText(RepositoryPath("windows", "installer", "Sidey.Setup", "Languages.nsh"))
            + Environment.NewLine + File.ReadAllText(RepositoryPath("windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        (string Key, string Language, string Value)[] strings = [.. Regex.Matches(source, "^LangString (\\w+) \\$\\{LANG_(\\w+)\\} \\\"(.*)\\\"\\r?$", RegexOptions.Multiline).Select(match => (Key: match.Groups[1].Value, Language: match.Groups[2].Value, match.Groups[3].Value))];
        var english = strings.Where(value => value.Language == "ENGLISH").ToDictionary(value => value.Key, value => value.Value);
        Assert.True(english.Count >= 25);
        foreach (string language in new[] { "ENGLISH", "KOREAN", "JAPANESE", "SIMPCHINESE", "TRADCHINESE", "RUSSIAN", "UKRAINIAN" })
        {
            var localized = strings.Where(value => value.Language == language).ToDictionary(value => value.Key, value => value.Value);
            Assert.Equal(english.Keys.Order(), localized.Keys.Order());
            foreach (string key in english.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(localized[key]));
                Assert.Equal(Placeholders(english[key]), Placeholders(localized[key]));
            }
        }
    }

    [Fact]
    public void FrameworkDependentErrorsSeparateCauseFromUserActionInEveryLanguage()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        const string ParagraphBreak = "$\\r$\\n$\\r$\\n";
        string[] categoryKeys =
        [
            "InstallerErrorNetwork",
            "InstallerErrorDownload",
            "InstallerErrorDiskFull",
            "InstallerErrorPermission",
            "InstallerErrorPolicy",
            "InstallerErrorPackage",
            "InstallerErrorSignature",
            "InstallerErrorDependency",
            "InstallerErrorDependencyConflict",
            "InstallerErrorIncompatible",
            "InstallerErrorAppInUse",
            "InstallerErrorAnotherInstall",
            "InstallerErrorAlreadyInstalled",
            "InstallerErrorRestart",
            "InstallerErrorCancelled",
            "InstallerErrorRegistration",
            "InstallerErrorRepository",
        ];

        foreach (string language in new[]
                 {
                     "JAPANESE",
                     "SIMPCHINESE",
                     "TRADCHINESE",
                     "RUSSIAN",
                     "UKRAINIAN",
                 })
        {
            foreach (string key in categoryKeys)
            {
                Match match = Regex.Match(
                    source,
                    $"^LangString {key} \\${{LANG_{language}}} \\\"(?<message>.*)\\\"\\r?$",
                    RegexOptions.Multiline);
                Assert.True(match.Success, $"Missing {key} for {language}.");
                Assert.True(
                    match.Groups["message"].Value.Split(
                        ParagraphBreak,
                        StringSplitOptions.None).Length >= 3,
                    $"{key} for {language} must separate the failure, cause, and user action.");
            }
        }
    }

    [Fact]
    public void InstallerRoutesLifecycleFailuresToDistinctUserMessages()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));

        string mutex = Section(source, "!macro AcquireSetupMutex HANDLE", "!macroend");
        Assert.Contains("${If} $1 == 183", mutex, StringComparison.Ordinal);
        Assert.Contains("${ElseIf} ${HANDLE} == 0", mutex, StringComparison.Ordinal);
        Assert.Contains("SetErrorLevel 1618", mutex, StringComparison.Ordinal);
        Assert.Contains("${IfNot} ${Silent}", mutex, StringComparison.Ordinal);
        Assert.Contains("Call ${ACTIVATE_FUNCTION}", mutex, StringComparison.Ordinal);
        Assert.Contains("$(SetupAlreadyRunning)", mutex, StringComparison.Ordinal);
        Assert.Contains("$(SetupInitializationFailed)", mutex, StringComparison.Ordinal);

        string initialization = Section(source, "Function .onInit", "FunctionEnd");
        Assert.Contains("$(TransactionRecoveryFailed)", initialization, StringComparison.Ordinal);
        Assert.Contains("Call ShowLifecycleError", initialization, StringComparison.Ordinal);
        Assert.Contains("!insertmacro ResolveRecoveryErrorSymbol", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("$(TransactionStateUnknown)", initialization, StringComparison.Ordinal);

        string maintenanceRemoval = Section(source, "Function TermsPagePre", "FunctionEnd");
        Assert.Contains("$(ExistingRemovalFailed)", maintenanceRemoval, StringComparison.Ordinal);
        Assert.Contains("Call ShowLifecycleError", maintenanceRemoval, StringComparison.Ordinal);
        Assert.Contains("SetErrorLevel 1", maintenanceRemoval, StringComparison.Ordinal);

        Assert.DoesNotContain("Function EnsurePrerequisites", source, StringComparison.Ordinal);
        Assert.DoesNotContain("windowsappruntimeinstall", source, StringComparison.OrdinalIgnoreCase);

        string installation = Section(source, "Section \"SIDEY\" MainSection", "SectionEnd");
        foreach (string command in new[]
                 {
                     "Sidey.InstallTransaction.exe --action Prepare",
                     "Sidey.SetupSupport.exe --stop-sidey-processes",
                     "Sidey.InstallTransaction.exe --action Activate",
                     "Sidey.InstallTransaction.exe --action BeginRegistration",
                     "Sidey.InstallTransaction.exe --action Commit",
                     "Stage SIDEY application files",
                     "Register SIDEY shortcuts and Windows metadata",
                 })
        {
            Assert.Contains(command, installation, StringComparison.Ordinal);
        }
        Assert.Contains("StrCpy $InstallerErrorSource \"PROCESS\"", installation, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorSource \"REGISTRY\"", installation, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorSource \"FILESYSTEM\"", installation, StringComparison.Ordinal);

        string uninstall = Section(source, "Section \"Uninstall\"", "SectionEnd");
        Assert.Contains("Goto uninstall_preflight_failed", uninstall, StringComparison.Ordinal);
        Assert.Contains("$(UninstallPreflightFailed)", uninstall, StringComparison.Ordinal);
        Assert.Contains("Goto uninstall_state_unknown", uninstall, StringComparison.Ordinal);
        Assert.Contains("$(UninstallStateUnknown)", uninstall, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorSource \"PROCESS\"", uninstall, StringComparison.Ordinal);
        Assert.Contains(
            "SIDEY.UninstallHelper.exe --stop-sidey-processes",
            uninstall,
            StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorSource \"FILESYSTEM\"", uninstall, StringComparison.Ordinal);
        Assert.Contains(
            "Extract Sidey.InstallTransaction.exe",
            uninstall,
            StringComparison.Ordinal);
        Assert.Equal(
            4,
            Regex.Matches(uninstall, "Call un\\.ShowLifecycleError").Count);

        string uninitialization = Section(source, "Function un.onInit", "FunctionEnd");
        Assert.Contains("Call un.InitializeInstallerErrorHandling", uninitialization, StringComparison.Ordinal);

        string errors = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        string display = Section(errors, "Function ShowInstallerError", "FunctionEnd");
        Assert.Contains("MB_ICONEXCLAMATION", display, StringComparison.Ordinal);
        Assert.Contains("MB_ICONINFORMATION", display, StringComparison.Ordinal);
        Assert.Contains("MB_ICONSTOP", display, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorOpenLog)", display, StringComparison.Ordinal);
        Assert.Contains("/SD IDNO", display, StringComparison.Ordinal);

        string lifecycleDisplay = Section(errors, "Function ShowLifecycleError", "FunctionEnd");
        Assert.Contains("Call LogInstallerErrorFallback", lifecycleDisplay, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorComponent)", lifecycleDisplay, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorLog)", lifecycleDisplay, StringComparison.Ordinal);

        string uninstallLifecycleDisplay = Section(errors, "Function un.ShowLifecycleError", "FunctionEnd");
        Assert.Contains("Call un.LogInstallerErrorFallback", uninstallLifecycleDisplay, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorComponent)", uninstallLifecycleDisplay, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorLog)", uninstallLifecycleDisplay, StringComparison.Ordinal);

        foreach (string hardCodedTarget in new[]
                 {
                     "SIDEY installation state",
                     "SIDEY installation",
                     "SIDEY removal preflight",
                     "SIDEY removal state",
                     "Current-user SIDEY data",
                 })
        {
            Assert.DoesNotContain(
                $"StrCpy $InstallerErrorTarget \"{hardCodedTarget}\"",
                source,
                StringComparison.Ordinal);
        }
        Assert.Contains("$(InstallerComponentInstallation)", source, StringComparison.Ordinal);
        Assert.Contains("$(InstallerComponentRegistration)", source, StringComparison.Ordinal);
        Assert.Contains("$(InstallerComponentRemoval)", source, StringComparison.Ordinal);
        Assert.Contains("$(InstallerComponentCurrentUserData)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerLocksBeforeLanguageSelectionAndMarksEveryInteractiveRootWindow()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));
        string initialization = Section(source, "Function .onInit", "FunctionEnd");

        int prepare = initialization.IndexOf("Call PrepareInstallerActivation", StringComparison.Ordinal);
        int acquire = initialization.IndexOf("!insertmacro AcquireSetupMutex", StringComparison.Ordinal);
        int selectLanguage = initialization.IndexOf("Call SelectInstallerLanguage", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && prepare < acquire);
        Assert.True(acquire < selectLanguage);

        Assert.Contains(
            $"!define SETUP_ACTIVATION_PROPERTY \"{InstallerWindowActivation.WindowPropertyName}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!define MUI_CUSTOMFUNCTION_GUIINIT ShowInstallerAfterLanguageSelection",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!define MUI_CUSTOMFUNCTION_UNGUIINIT un.MarkUninstallerWindow",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SetPropW(p $HWNDPARENT", Section(
            source,
            "Function ShowInstallerAfterLanguageSelection",
            "FunctionEnd"), StringComparison.Ordinal);
        Assert.Contains("SetPropW(p $HWNDPARENT", Section(
            source,
            "Function un.MarkUninstallerWindow",
            "FunctionEnd"), StringComparison.Ordinal);
        Assert.Contains("--activate-existing", Section(
            source,
            "Function ActivateExistingInstaller",
            "FunctionEnd"), StringComparison.Ordinal);

        string maintenanceRemoval = Section(source, "Function TermsPagePre", "FunctionEnd");
        Assert.True(
            maintenanceRemoval.IndexOf("RemovePropW(p $HWNDPARENT", StringComparison.Ordinal)
                < maintenanceRemoval.IndexOf("Call ReleaseSetupMutex", StringComparison.Ordinal));
        Assert.Contains("ShowWindow $HWNDPARENT ${SW_SHOW}", maintenanceRemoval, StringComparison.Ordinal);
        Assert.Contains("SetPropW(p $HWNDPARENT", maintenanceRemoval, StringComparison.Ordinal);

        string uninitialization = Section(source, "Function un.onInit", "FunctionEnd");
        Assert.True(
            uninitialization.IndexOf("Call un.PrepareInstallerActivation", StringComparison.Ordinal)
                < uninitialization.IndexOf("!insertmacro AcquireSetupMutex", StringComparison.Ordinal));
    }

    [Fact]
    public void UninstallReportsCleanupFailuresOnceWithoutExposingNativeCodes()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));
        string uninstall = Section(source, "Section \"Uninstall\"", "SectionEnd");

        Assert.Contains("$(CleanupLocalDataFailure)", uninstall, StringComparison.Ordinal);
        Assert.Contains("$(CleanupCredentialsFailure)", uninstall, StringComparison.Ordinal);
        Assert.Contains("$(CleanupStartupFailure)", uninstall, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorMessage \"$(CleanupFailed)\"", uninstall, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorDetail $CleanupFailureDiagnosticDetails", uninstall, StringComparison.Ordinal);
        Assert.Contains("Call un.ShowLifecycleError", uninstall, StringComparison.Ordinal);

        foreach (string key in new[]
                 {
                     "CleanupLocalDataFailure",
                     "CleanupCredentialsFailure",
                     "CleanupStartupFailure",
                 })
        {
            Match message = Regex.Match(
                source,
                $"^LangString {key} \\${{LANG_ENGLISH}} \\\"(?<message>.*)\\\"\\r?$",
                RegexOptions.Multiline);
            Assert.True(message.Success);
            Assert.DoesNotContain("$0", message.Groups["message"].Value, StringComparison.Ordinal);
        }

        Assert.Contains("localDataExitCode=$0", uninstall, StringComparison.Ordinal);
        Assert.Contains("credentialsExitCode=$0", uninstall, StringComparison.Ordinal);
        Assert.Contains("startupExitCode=$0", uninstall, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorDetail $CleanupFailureDiagnosticDetails", uninstall, StringComparison.Ordinal);
        Assert.True(
            uninstall.IndexOf("DeleteRegKey HKLM", StringComparison.Ordinal)
                < uninstall.IndexOf("StrCpy $InstallerErrorMessage \"$(CleanupFailed)\"", StringComparison.Ordinal));
        Assert.Contains(
            "One or more SIDEY-owned files, directories, shortcuts, or registry entries could not be removed.",
            uninstall,
            StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallerErrorSymbol \"UNINSTALL_STATE_UNKNOWN\"", uninstall, StringComparison.Ordinal);
        int payloadInclude = uninstall.IndexOf(
            "!include \"${PAYLOAD_UNINSTALL_FILES_INCLUDE}\"",
            StringComparison.Ordinal);
        int payloadErrorCheck = uninstall.IndexOf("${If} ${Errors}", payloadInclude, StringComparison.Ordinal);
        int directoryInclude = uninstall.IndexOf(
            "!include \"${PAYLOAD_UNINSTALL_DIRECTORIES_INCLUDE}\"",
            StringComparison.Ordinal);
        int deleteUninstaller = uninstall.IndexOf(
            "Delete /REBOOTOK \"$INSTDIR\\Uninstall.exe\"",
            StringComparison.Ordinal);
        int deleteRegistration = uninstall.IndexOf("DeleteRegKey HKLM", StringComparison.Ordinal);
        Assert.True(payloadInclude >= 0 && payloadInclude < payloadErrorCheck);
        Assert.True(payloadErrorCheck < directoryInclude);
        Assert.True(payloadErrorCheck < deleteUninstaller);
        Assert.True(payloadErrorCheck < deleteRegistration);
        int deleteHelper = uninstall.IndexOf(
            "Delete /REBOOTOK \"$INSTDIR\\Runtime\\SIDEY.UninstallHelper.exe\"",
            StringComparison.Ordinal);
        Assert.True(directoryInclude < deleteRegistration);
        Assert.True(deleteRegistration < deleteHelper);
        Assert.True(deleteHelper < deleteUninstaller);
        string directoryPruning = uninstall[payloadErrorCheck..deleteRegistration];
        Assert.Contains("ClearErrors", directoryPruning, StringComparison.Ordinal);
        int removeRuntimeDirectory = uninstall.IndexOf(
            "RMDir \"$INSTDIR\\Runtime\"",
            StringComparison.Ordinal);
        Assert.True(deleteHelper < removeRuntimeDirectory);
        int lastCheckedFailure = uninstall.LastIndexOf(
            "${If} ${Errors}",
            deleteHelper,
            StringComparison.Ordinal);
        Assert.True(deleteRegistration < lastCheckedFailure && lastCheckedFailure < deleteHelper);
        int deleteStartMenuShortcut = uninstall.IndexOf(
            "Delete \"$SMPROGRAMS\\SIDEY\\Uninstall SIDEY.lnk\"",
            StringComparison.Ordinal);
        int shortcutErrorCheck = uninstall.IndexOf(
            "${If} ${Errors}",
            deleteStartMenuShortcut,
            StringComparison.Ordinal);
        int removeStartMenuDirectory = uninstall.IndexOf(
            "RMDir \"$SMPROGRAMS\\SIDEY\"",
            StringComparison.Ordinal);
        Assert.True(
            deleteStartMenuShortcut < shortcutErrorCheck
                && shortcutErrorCheck < removeStartMenuDirectory);
    }

    [Fact]
    public void InstallerOpensDiagnosticLogsWithNotepadWithoutFileAssociation()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        const string OpenWithNotepad =
            "ExecShell \"open\" \"$SYSDIR\\notepad.exe\" '\"$InstallerErrorLogPath\"'";

        Assert.Equal(
            3,
            source.Split(OpenWithNotepad, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(
            "ExecShell \"open\" \"$InstallerErrorLogPath\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingRemovalFailureSeparatesStateActionAndIssueGuidance()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));
        Match match = Regex.Match(
            source,
            "^LangString ExistingRemovalFailed \\${LANG_KOREAN} \\\"(?<message>.*)\\\"\\r?$",
            RegexOptions.Multiline);

        Assert.True(match.Success);
        string message = match.Groups["message"].Value;
        Assert.Contains("설치 파일이 없거나 사용 중일 수 있습니다", message, StringComparison.Ordinal);
        Assert.Contains("다시 실행하여 삭제", message, StringComparison.Ordinal);
        Assert.Contains("Windows를 다시 시작", message, StringComparison.Ordinal);
        Assert.Contains("$\\r$\\n$\\r$\\n", message, StringComparison.Ordinal);
        Assert.Contains("GitHub 이슈", message, StringComparison.Ordinal);
        Assert.Contains("스크린샷과 진단 데이터", message, StringComparison.Ordinal);
        Assert.DoesNotContain("$0", message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf("복구", StringComparison.Ordinal)
                < message.IndexOf("Windows를 다시 시작", StringComparison.Ordinal));
    }

    [Fact]
    public void InstallerErrorsShowComponentAndDiagnosticLogWithoutUnsafeRuntimeRemovalAdvice()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));

        Assert.Contains("$(InstallerErrorComponent)", source, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorCode)", source, StringComparison.Ordinal);
        Assert.Contains("$(InstallerErrorLog)", source, StringComparison.Ordinal);
        Assert.Contains("SIDEY error code: $InstallerErrorSideyCode", source, StringComparison.Ordinal);
        Assert.Contains("오류 코드: $InstallerErrorSideyCode", source, StringComparison.Ordinal);
        Assert.Contains("Diagnostic data: $InstallerErrorLogPath", source, StringComparison.Ordinal);
        Assert.Contains("진단 데이터: $InstallerErrorLogPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Native error code", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("send this file to customer support", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("이 파일을 고객지원에 보내세요", source, StringComparison.Ordinal);
        Assert.Contains(
            "LangString InstallerComponentInstallation ${LANG_KOREAN} \"SIDEY 설치\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LangString InstallerComponentRemoval ${LANG_JAPANESE} \"SIDEY の削除\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Remove the conflicting version", source, StringComparison.Ordinal);
        Assert.DoesNotContain("충돌하는 버전을 제거", source, StringComparison.Ordinal);
        Assert.DoesNotContain("No further changes were made", source, StringComparison.Ordinal);
        Assert.DoesNotContain("더 이상 변경하지 않았습니다", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".NET Desktop Runtime 10.0.12", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0x80070032", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x80131500", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerErrorGuidanceCoversStoragePackageSignatureAndSharedRuntimeSafety()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));

        string disk = EnglishMessage(source, "InstallerErrorDiskFull");
        Assert.Contains("Windows system drive", disk, StringComparison.Ordinal);
        Assert.Contains("selected SIDEY installation drive", disk, StringComparison.Ordinal);

        string package = EnglishMessage(source, "InstallerErrorPackage");
        Assert.Contains("downloads a fresh copy from Microsoft", package, StringComparison.Ordinal);
        Assert.DoesNotContain("Download Setup again", package, StringComparison.OrdinalIgnoreCase);

        string signature = EnglishMessage(source, "InstallerErrorSignature");
        Assert.Contains("date and time", signature, StringComparison.Ordinal);
        Assert.Contains("Windows updates", signature, StringComparison.Ordinal);
        Assert.Contains("Do not disable signature verification", signature, StringComparison.Ordinal);

        Assert.Contains("Do not manually remove shared Microsoft runtimes", EnglishMessage(source, "InstallerErrorDependency"), StringComparison.Ordinal);
        Assert.Contains("Do not manually remove shared Microsoft runtimes", EnglishMessage(source, "InstallerErrorAlreadyInstalled"), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerUsesCommonErrorFooterAndNoDeadRestartMessage()
    {
        string errors = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        string setup = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));
        string languages = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Languages.nsh"));

        Assert.Contains(
            "$(InstallerErrorComponent)$\\r$\\n$(InstallerErrorLog)$\\r$\\n$\\r$\\n$(InstallerErrorCode)",
            errors,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PrerequisitesRestart", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("PrerequisitesRestart", languages, StringComparison.Ordinal);
        Assert.DoesNotContain("--download-directory", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("PrerequisitesStatus", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("PrerequisitesStatus", languages, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerUsesStableSideyCodesAndKeepsNativeCodesInDiagnostics()
    {
        string errors = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        MatchCollection mappings = Regex.Matches(
            errors,
            @"StrCpy \$InstallerErrorSideyCode ""(?<code>0x51DE[0-9A-F]{4})""");

        Assert.NotEmpty(mappings);
        Assert.All(mappings, mapping =>
            Assert.Matches("^0x51DE[0-9A-F]{4}$", mapping.Groups["code"].Value));
        Assert.Contains("nativeCode=$InstallerErrorNativeCode", errors, StringComparison.Ordinal);
        Assert.Contains("sideyCode=$InstallerErrorSideyCode", errors, StringComparison.Ordinal);
        Assert.Contains("TRANSACTION_RECOVERY_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("RECOVERY_STATE_CHECK_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("RECOVERY_FILESYSTEM_IO_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("!insertmacro ResolveSideyInstallerErrorCode", errors, StringComparison.Ordinal);
        Assert.Contains("Function LogInstallerDisplayCode", errors, StringComparison.Ordinal);
        Assert.Contains("Call LogInstallerDisplayCode", Section(
            errors,
            "Function ShowInstallerError",
            "FunctionEnd"), StringComparison.Ordinal);
    }

    [Fact]
    public void SelfContainedFailuresHaveDedicatedCopyAndCodesWhileFrameworkCopyRemains()
    {
        string errors = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        string setup = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "Sidey.Setup.nsi"));

        Assert.Contains("PAYLOAD_STAGE_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("PROCESS_STOP_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("PAYLOAD_ACTIVATION_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("REGISTRATION_FAILED", errors, StringComparison.Ordinal);
        Assert.Contains("프로그램을 설치하지 못했습니다.", setup, StringComparison.Ordinal);
        Assert.Contains("$(PayloadStageFailed)", setup, StringComparison.Ordinal);
        Assert.Contains("$(ProcessStopFailed)", setup, StringComparison.Ordinal);
        Assert.Contains("$(PayloadActivationFailed)", setup, StringComparison.Ordinal);
        Assert.Contains("$(RegistrationFailed)", setup, StringComparison.Ordinal);

        Assert.Contains("InstallerErrorDependency", errors, StringComparison.Ordinal);
        Assert.Contains("InstallerErrorDependencyConflict", errors, StringComparison.Ordinal);
        Assert.Contains("InstallerErrorRegistration", errors, StringComparison.Ordinal);
    }

    private static string[] Placeholders(string text) =>
        [.. Regex.Matches(text, @"\$\{[A-Z_]+\}|\$[0-9]|\$[A-Za-z_][A-Za-z0-9_]*|%LOCALAPPDATA%")
            .Select(match => match.Value).Order(StringComparer.Ordinal)];

    private static string EnglishMessage(string source, string key)
    {
        Match match = Regex.Match(
            source,
            $"^LangString {key} \\${{LANG_ENGLISH}} \\\"(?<message>.*)\\\"\\r?$",
            RegexOptions.Multiline);
        Assert.True(match.Success);
        return match.Groups["message"].Value;
    }

    private static string Section(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0);
        return source[startIndex..(endIndex + end.Length)];
    }

    private static string RepositoryPath(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "windows", "src")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        return Path.Combine([root.FullName, .. parts]);
    }
}
