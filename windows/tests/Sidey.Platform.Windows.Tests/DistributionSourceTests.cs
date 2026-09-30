using System.Text.Json;
using System.Xml.Linq;

namespace Sidey.Platform.Windows.Tests;

public sealed class DistributionSourceTests
{
    [Fact]
    public void SelfContainedRuntimeVersionsArePinnedToStableReleases()
    {
        using var globalJson = JsonDocument.Parse(File.ReadAllText(RepositoryPath(
            "windows", "global.json")));
        JsonElement sdk = globalJson.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.401", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());

        var packages = XDocument.Load(RepositoryPath("windows", "Directory.Packages.props"));
        XElement windowsAppSdk = packages.Descendants("PackageVersion").Single(element =>
            (string?)element.Attribute("Include") == "Microsoft.WindowsAppSDK");
        Assert.Equal("2.4.0", (string?)windowsAppSdk.Attribute("Version"));
    }

    [Fact]
    public void AppPublishIsMultiFileSelfContainedWithExternalAssets()
    {
        var project = XDocument.Load(AssetPath("Sidey.App.csproj.xml"));

        Assert.Equal("false", Value(project, "PublishSingleFile"));
        Assert.Equal("true", Value(project, "WindowsAppSDKSelfContained"));
        Assert.Equal("true", Value(project, "SelfContained"));
        Assert.Equal("10.0.12", Value(project, "SideyDotNetRuntimeVersion"));
        Assert.Equal("true", Value(project, "TargetLatestRuntimePatch"));
        Assert.Empty(project.Descendants("WindowsAppSdkBootstrapInitialize"));
        Assert.Equal("true", Value(project, "EnableMsixTooling"));
        Assert.Equal("false", Value(project, "IncludeAllContentForSelfExtract"));
        Assert.Equal("false", Value(project, "PublishTrimmed"));
        Assert.Empty(project.Descendants("Version"));
        Assert.Empty(project.Descendants("FileVersion"));
        Assert.Empty(project.Descendants("AssemblyVersion"));
        Assert.Equal("SIDEY.Host", Value(project, "AssemblyName"));
        Assert.Equal("SIDEY", Value(project, "AssemblyTitle"));
        Assert.Equal("SIDEY", Value(project, "Product"));

        XElement internalLanguages = project.Descendants("None").Single(element =>
            (string?)element.Attribute("Update") == "InternalLangs\\*.json");
        Assert.Equal("'$(Configuration)' == 'Debug'", (string?)internalLanguages.Attribute("Condition"));
        Assert.Equal("PreserveNewest", internalLanguages.Element("CopyToOutputDirectory")?.Value);
        Assert.Equal("PreserveNewest", internalLanguages.Element("CopyToPublishDirectory")?.Value);

        Assert.Empty(project.Descendants("ExcludeFromSingleFile"));
        Assert.DoesNotContain(
            project.Descendants("Target"),
            element => ((string?)element.Attribute("Name"))?.Contains(
                "SingleFile",
                StringComparison.Ordinal) == true);

        XElement characterAssets = project.Descendants("None").Single(element =>
            (string?)element.Attribute("Include") == "@(_SideyExternalCharacterAsset)");
        Assert.Equal("PreserveNewest", characterAssets.Element("CopyToPublishDirectory")?.Value);
        Assert.DoesNotContain(
            project.Descendants("Content"),
            element => ((string?)element.Attribute("Include"))?.Contains(
                "Assets/Characters",
                StringComparison.Ordinal) == true);

        XElement throwableAssets = project.Descendants("None").Single(element =>
            (string?)element.Attribute("Include") == "@(_SideyExternalThrowableAsset)");
        Assert.Equal("PreserveNewest", throwableAssets.Element("CopyToPublishDirectory")?.Value);

        XElement copyExternal = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "CopyExternalCharacterAssetsAfterPublish");
        Assert.Equal("Publish", (string?)copyExternal.Attribute("AfterTargets"));
        Assert.Contains(
            "%(RecursiveDir)",
            copyExternal.Descendants("Copy").Single().Attribute("DestinationFiles")?.Value);

        XElement copyThrowables = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "CopyExternalThrowableAssetsAfterPublish");
        Assert.Equal("Publish", (string?)copyThrowables.Attribute("AfterTargets"));
        Assert.Contains(
            "Assets\\Throwables",
            copyThrowables.Descendants("Copy").Single().Attribute("DestinationFiles")?.Value,
            StringComparison.Ordinal);

        XElement organize = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "OrganizeStructuredPublish");
        Assert.Equal("Publish", (string?)organize.Attribute("AfterTargets"));
        Assert.Contains(
            "ConvertTo-PublishLayout.ps1",
            organize.Descendants("Exec").Single().Attribute("Command")?.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedVersionPropertiesUseTheWindowsHeadVerBuild()
    {
        var properties = XDocument.Load(RepositoryPath("windows", "Version.props"));
        XElement propertyGroup = properties.Descendants("PropertyGroup").Single();
        string productVersion = Value(properties, "SideyProductVersion");
        int windowsBuild = int.Parse(Value(properties, "SideyWindowsBuild"));
        string updateVersion = Value(properties, "SideyWindowsUpdateVersion");
        string releaseVersion = Value(properties, "SideyWindowsReleaseVersion");
        string msixVersion = Value(properties, "SideyMsixVersion");
        int[] productParts = [.. productVersion.Split('.').Select(int.Parse)];

        Assert.Equal(3, productParts.Length);
        Assert.Equal(4, productVersion.Split('.')[1].Length);
        Assert.Equal(windowsBuild, productParts[2]);
        Assert.InRange(windowsBuild, 1, 65535);
        Assert.Equal($"{productVersion}.0", msixVersion);
        Assert.Equal(productVersion, updateVersion);
        Assert.Equal(productVersion, releaseVersion);
        Assert.Equal(productVersion, Value(properties, "Version"));
        Assert.Equal(productVersion, Value(properties, "VersionPrefix"));
        Assert.Equal(productVersion, Value(properties, "InformationalVersion"));
        Assert.Equal($"{productVersion}.0", Value(properties, "AssemblyVersion"));
        Assert.Equal(msixVersion, Value(properties, "FileVersion"));
        Assert.Equal("false", Value(properties, "IncludeSourceRevisionInInformationalVersion"));
        Assert.All(
            propertyGroup.Elements(),
            property => Assert.False(string.IsNullOrWhiteSpace(property.Value)));

        var directoryProperties = XDocument.Load(RepositoryPath("windows", "Directory.Build.props"));
        XElement import = directoryProperties.Descendants("Import").Single(element =>
            ((string?)element.Attribute("Project"))?.EndsWith("Version.props", StringComparison.Ordinal) == true);
        Assert.Equal("$(MSBuildThisFileDirectory)Version.props", (string?)import.Attribute("Project"));
        XElement validation = directoryProperties.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "ValidateSideyVersionMetadata");
        Assert.Equal("BeforeBuild", (string?)validation.Attribute("BeforeTargets"));
        Assert.Contains(
            "scripts/sidey_version.py --check windows",
            validation.Descendants("Exec").Single().Attribute("Command")?.Value,
            StringComparison.Ordinal);

        string installer = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-WindowsInstaller.ps1"));
        Assert.Contains("-FileVersion $publishedFileVersion", installer, StringComparison.Ordinal);
        Assert.Contains("/DAPP_UPDATE_VERSION=$expectedUpdateVersion", installer, StringComparison.Ordinal);
        Assert.Contains("/DAPP_FILE_VERSION=$publishedFileVersion", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("-FileVersion \"$Version.0\"", installer, StringComparison.Ordinal);
        Assert.Contains("[string]$ProductVersion", installer, StringComparison.Ordinal);
        Assert.Contains("[string]$ReleaseVersion", installer, StringComparison.Ordinal);
        Assert.Contains("v${ReleaseVersion}-Setup.exe", installer, StringComparison.Ordinal);

        string powerShellSupport = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "tests", "Test-PowerShellSupport.ps1"));
        Assert.Contains("SideyProductVersion", powerShellSupport, StringComparison.Ordinal);
        Assert.Contains("SideyMsixVersion", powerShellSupport, StringComparison.Ordinal);
        Assert.DoesNotContain("$fileVersion = \"$version.0\"", powerShellSupport, StringComparison.Ordinal);

        string smoke = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "tests", "Test-WindowsBuild.ps1"));
        Assert.DoesNotContain("-p:Version=", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:FileVersion=", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:AssemblyVersion=", smoke, StringComparison.Ordinal);

        string windowsWorkflow = File.ReadAllText(RepositoryPath(
            ".github", "workflows", "windows-build-and-tests.yml"));
        Assert.Contains(
            "scripts/sidey_version.py --get productVersion",
            windowsWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "scripts/sidey_version.py --get windowsReleaseVersion",
            windowsWorkflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Sidey.App.csproj -Raw",
            windowsWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverlaySupportsTheUnpackagedApp()
    {
        var project = XDocument.Load(AssetPath("Sidey.Overlay.csproj.xml"));

        Assert.Equal("true", Value(project, "EnableMsixTooling"));
        Assert.Empty(project.Descendants("ExcludeFromSingleFile"));
    }

    [Fact]
    public void SetupExeOffersAnInstallLocationAndReusesItForUpdates()
    {
        string setup = ReadSetupScript();

        Assert.Contains("InstallDir \"$PROGRAMFILES64\\SIDEY\"", setup, StringComparison.Ordinal);
        Assert.Contains("InstallDirRegKey HKLM", setup, StringComparison.Ordinal);
        Assert.Contains("RequestExecutionLevel admin", setup, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_DIRECTORY", setup, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallState \"upgrade\"", setup, StringComparison.Ordinal);
        Assert.Contains("$InstallState == \"repair\"", setup, StringComparison.Ordinal);
        Assert.Contains("ExecWait '\"$INSTDIR\\Uninstall.exe\"' $0", setup, StringComparison.Ordinal);
        Assert.Contains("$INSTDIR.sidey-staging-$0", setup, StringComparison.Ordinal);
        Assert.Contains("$INSTDIR.sidey-rollback", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("Uninstall.exe\" /S _?=$INSTDIR", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupExeSupportsAllSevenInstallerLanguages()
    {
        string setup = ReadSetupScript();

        Assert.Contains("MUI_LANGUAGE \"English\"", setup, StringComparison.Ordinal);
        Assert.Contains("MUI_LANGUAGE \"Korean\"", setup, StringComparison.Ordinal);
        foreach (string language in new[] { "Japanese", "SimpChinese", "TradChinese", "Russian", "Ukrainian" })
        {
            Assert.Contains($"MUI_LANGUAGE \"{language}\"", setup, StringComparison.Ordinal);
        }
        Assert.Contains("Call SelectInstallerLanguage", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("MUI_LANGDLL_DISPLAY", setup, StringComparison.Ordinal);
        Assert.Contains("WriteRegStr HKLM \"${PRODUCT_REGISTRY_KEY}\" \"Language\" $LANGUAGE", setup, StringComparison.Ordinal);
        Assert.Contains("SetFont /LANG=${LANG_ENGLISH} \"Segoe UI\" 9", setup, StringComparison.Ordinal);
        Assert.Contains("SetFont /LANG=${LANG_KOREAN} \"맑은 고딕\" 9", setup, StringComparison.Ordinal);
        Assert.Contains("LangString MaintenanceTitle ${LANG_ENGLISH}", setup, StringComparison.Ordinal);
        Assert.Contains("LangString MaintenanceTitle ${LANG_KOREAN}", setup, StringComparison.Ordinal);
        Assert.Contains("LangString DeleteLocalData ${LANG_KOREAN}", setup, StringComparison.Ordinal);
        Assert.Contains("LangString DeleteCredentials ${LANG_KOREAN}", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicLauncherPassesTheInstallerLanguageToTheApp()
    {
        string launcher = File.ReadAllText(RepositoryPath(
            "windows", "src", "Sidey.Launcher", "Program.cs"));
        string organizer = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "ConvertTo-PublishLayout.ps1"));

        Assert.Contains("RegistryHive.LocalMachine", launcher, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry64", launcher, StringComparison.Ordinal);
        Assert.Contains("InstallerLanguages.AppLanguage", launcher, StringComparison.Ordinal);
        Assert.Contains("start.EnvironmentVariables[LanguageEnvironmentVariable] = language", launcher, StringComparison.Ordinal);
        Assert.Contains("InstallerLanguages.cs", organizer, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicLauncherReadsNestedStartupMessagesFromEveryCatalog()
    {
        string languageDirectory = RepositoryPath("windows", "src", "Sidey.App", "Langs");
        string[] catalogs = Directory.GetFiles(languageDirectory, "*.json");
        Assert.NotEmpty(catalogs);

        foreach (string path in catalogs)
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            JsonElement startup = document.RootElement.GetProperty("app").GetProperty("startup");

            AssertLocalizedValue(json, "app.startup.runtime_missing", startup.GetProperty("runtime_missing"));
            AssertLocalizedValue(json, "app.startup.error.title", startup.GetProperty("error").GetProperty("title"));
            AssertLocalizedValue(json, "app.startup.failed", startup.GetProperty("failed"));
        }
    }

    [Fact]
    public void LegacyMsiMessagesUseTheSavedInstallerLanguageAcrossAllSevenCatalogs()
    {
        string helper = File.ReadAllText(RepositoryPath(
            "windows", "src", "Sidey.Uninstaller", "Program.cs"));

        Assert.Contains("InstallerLanguageValueName = \"Language\"", helper, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry64", helper, StringComparison.Ordinal);
        Assert.Contains("GetUserDefaultUILanguage()", helper, StringComparison.Ordinal);
        Assert.Contains("SupportedLanguageOrEnglish", helper, StringComparison.Ordinal);
        foreach (string language in new[]
                 {
                     "EnglishLanguage",
                     "KoreanLanguage",
                     "JapaneseLanguage",
                     "SimplifiedChineseLanguage",
                     "TraditionalChineseLanguage",
                     "RussianLanguage",
                     "UkrainianLanguage",
                 })
        {
            Assert.Contains(language, helper, StringComparison.Ordinal);
        }
        Assert.Contains("FormatExceptionErrorCode", helper, StringComparison.Ordinal);
        Assert.Contains("NativeErrorCode", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TwoLetterISOLanguageName", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRegistersOnlyTheProductionGoogleCallbackScheme()
    {
        string setup = ReadSetupScript();

        Assert.Contains("Software\\Classes\\sidey", setup, StringComparison.Ordinal);
        Assert.Contains("URL:SIDEY authentication callback", setup, StringComparison.Ordinal);
        Assert.Contains("$INSTDIR\\SIDEY.exe$", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("Software\\Classes\\sidey-dev", setup, StringComparison.Ordinal);
        Assert.Contains("DeleteRegKey HKLM \"${PRODUCT_PROTOCOL_KEY}\"", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentCommerceCanOnlyBeCompiledIntoExplicitDebugBuilds()
    {
        string props = File.ReadAllText(RepositoryPath("windows", "Directory.Build.props"));

        Assert.Contains("'$(Configuration)' == 'Debug'", props, StringComparison.Ordinal);
        Assert.Contains("'$(SideyDevelopmentCommerce)' == 'true'", props, StringComparison.Ordinal);
        Assert.Contains("SIDEY_DEVELOPMENT_COMMERCE", props, StringComparison.Ordinal);
        Assert.Contains("RejectDevelopmentCommerceOutsideDebug", props, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshInstallAndUpgradeRequireTermsAcceptance()
    {
        string setup = ReadSetupScript();
        string package = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-WindowsInstaller.ps1"));
        string generator = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-InstallerTerms.ps1"));

        Assert.Contains("MUI_LICENSEPAGE_CHECKBOX", setup, StringComparison.Ordinal);
        Assert.Contains("MUI_LICENSEPAGE_CHECKBOX_TEXT \"$(AcceptTerms)\"", setup, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_LICENSE \"${TERMS_LICENSE_FILE}\"", setup, StringComparison.Ordinal);
        Assert.Contains("LangString AcceptTerms ${LANG_ENGLISH}", setup, StringComparison.Ordinal);
        Assert.Contains("LangString AcceptTerms ${LANG_KOREAN}", setup, StringComparison.Ordinal);
        Assert.Contains("Function TermsPagePre", setup, StringComparison.Ordinal);
        Assert.Contains("$InstallState == \"repair\"", setup, StringComparison.Ordinal);
        Assert.Contains("New-InstallerTerms.ps1", package, StringComparison.Ordinal);
        Assert.Contains("/DTERMS_LICENSE_FILE=", package, StringComparison.Ordinal);
        Assert.Contains("termsBytes[0] -ne 0xEF", package, StringComparison.Ordinal);
        Assert.Contains("[Text.UTF8Encoding]::new($true, $true)", package, StringComparison.Ordinal);
        Assert.Contains("[Text.UTF8Encoding]::new($true)", generator, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupExeUsesAWhiteSideyWelcomeBitmap()
    {
        string setup = ReadSetupScript();
        byte[] bitmap = File.ReadAllBytes(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "SideyWelcome.bmp"));

        Assert.Contains("MUI_WELCOMEFINISHPAGE_BITMAP", setup, StringComparison.Ordinal);
        Assert.Contains("SideyWelcome.bmp", setup, StringComparison.Ordinal);
        Assert.Equal((byte)'B', bitmap[0]);
        Assert.Equal((byte)'M', bitmap[1]);
        Assert.Equal(164, BitConverter.ToInt32(bitmap, 18));
        Assert.Equal(314, BitConverter.ToInt32(bitmap, 22));
        Assert.Equal(24, BitConverter.ToInt16(bitmap, 28));
    }

    [Fact]
    public void SameVersionOffersRepairRemoveAndCloseWhileDowngradesAreBlocked()
    {
        string setup = ReadSetupScript();

        Assert.Contains("${VersionCompare}", setup, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallState \"same\"", setup, StringComparison.Ordinal);
        Assert.Contains("${NSD_CreateButton}", setup, StringComparison.Ordinal);
        Assert.Contains("$(RepairAction)", setup, StringComparison.Ordinal);
        Assert.Contains("$(RemoveAction)", setup, StringComparison.Ordinal);
        Assert.Contains("$(CloseAction)", setup, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallState \"remove\"", setup, StringComparison.Ordinal);
        Assert.Contains("StrCpy $InstallState \"close\"", setup, StringComparison.Ordinal);
        Assert.Contains("HideWindow", setup, StringComparison.Ordinal);
        Assert.Contains("ExecWait '\"$INSTDIR\\Uninstall.exe\"'", setup, StringComparison.Ordinal);
        Assert.Contains("$(DowngradeBlocked)", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallCleanupChoicesAreIndependentAndUncheckedByDefault()
    {
        string setup = ReadSetupScript();

        Assert.Contains("${NSD_Uncheck} $DeleteLocalDataCheckbox", setup, StringComparison.Ordinal);
        Assert.Contains("${NSD_Uncheck} $DeleteCredentialsCheckbox", setup, StringComparison.Ordinal);
        Assert.Contains("--cleanup-local-data", setup, StringComparison.Ordinal);
        Assert.Contains("--cleanup-local-data-as-desktop-user", setup, StringComparison.Ordinal);
        Assert.Contains("$DeleteLocalData == ${BST_CHECKED}", setup, StringComparison.Ordinal);
        Assert.Contains("--cleanup-credentials", setup, StringComparison.Ordinal);
        Assert.Contains("--cleanup-credentials-as-desktop-user", setup, StringComparison.Ordinal);
        Assert.Contains("$DeleteCredentials == ${BST_CHECKED}", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("RMDir /r \"$LOCALAPPDATA", setup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallAlwaysRemovesEveryInstallerOwnedRegistryEntry()
    {
        string setup = ReadSetupScript();
        string uninstall = setup[setup.IndexOf("Section \"Uninstall\"", StringComparison.Ordinal)..];

        Assert.Contains("--cleanup-startup-as-desktop-user", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteRegValue HKCU", uninstall, StringComparison.Ordinal);
        Assert.Contains("DeleteRegKey HKLM \"${PRODUCT_UNINSTALL_KEY}\"", uninstall, StringComparison.Ordinal);
        Assert.Contains("DeleteRegKey HKLM \"${PRODUCT_PROTOCOL_KEY}\"", uninstall, StringComparison.Ordinal);
        Assert.Contains("DeleteRegKey HKLM \"${PRODUCT_REGISTRY_KEY}\"", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupLeavesTheLegacyMsiIntactUntilTheUserRemovesIt()
    {
        string setup = ReadSetupScript();

        Assert.Contains("LEGACY_MSI_UPGRADE_CODE", setup, StringComparison.Ordinal);
        Assert.Contains("--detect-legacy-msi", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("--uninstall-legacy-msi", setup, StringComparison.Ordinal);
        Assert.Contains("Sidey.SetupSupport.exe", setup, StringComparison.Ordinal);
        Assert.Contains("--cleanup-legacy-install-as-desktop-user", setup, StringComparison.Ordinal);
        Assert.Contains(
            "ExecWait '\"$INSTDIR\\Runtime\\SIDEY.UninstallHelper.exe\" --cleanup-legacy-install-as-desktop-user",
            setup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--cleanup-legacy-msi", setup, StringComparison.Ordinal);
        Assert.Contains("$(LegacyMigrationManual)", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupAndUninstallerUseTheSideyApplicationIcon()
    {
        string setup = ReadSetupScript();

        Assert.Contains(
            "Icon \"${PUBLISH_DIR}\\Assets\\Icons\\SideyAppIcon.ico\"",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "UninstallIcon \"${PUBLISH_DIR}\\Assets\\Icons\\SideyAppIcon.ico\"",
            setup,
            StringComparison.Ordinal);
        Assert.Contains("WriteUninstaller \"$StagingDirectory\\Uninstall.exe\"", setup, StringComparison.Ordinal);

        string organizer = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "ConvertTo-PublishLayout.ps1"));
        Assert.Contains("Uninstall.exe", organizer, StringComparison.Ordinal);
        Assert.Contains("-IconPath $iconPath", organizer, StringComparison.Ordinal);
        string helperBuilder = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-SideyHelperExecutable.ps1"));
        Assert.Contains("ApplicationIcon = $iconFilePath", helperBuilder, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributionPipelineBuildsOnlyThePublicSetupExeWithoutSelfSigning()
    {
        string package = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-WindowsInstaller.ps1"));

        Assert.Contains("$throwableDirectory.Name -eq 'throwable_toy_cannon'", package, StringComparison.Ordinal);
        Assert.Contains("@('emitter.bgra', 'emitter.png', 'preview.png')", package, StringComparison.Ordinal);
        Assert.Contains("Compare-Object $expectedFileNames $fileNames", package, StringComparison.Ordinal);
        Assert.Contains("SIDEY-Windows-x64-v${ReleaseVersion}-Setup.exe", package, StringComparison.Ordinal);
        Assert.Contains("NSIS 3.12", package, StringComparison.Ordinal);
        Assert.Contains("makensis.exe", package, StringComparison.Ordinal);
        Assert.Contains("/VERSION", package, StringComparison.Ordinal);
        Assert.Contains("SideyPayloadInstall.nsh", package, StringComparison.Ordinal);
        Assert.Contains("SideyPayloadUninstallFiles.nsh", package, StringComparison.Ordinal);
        Assert.Contains("SideyPayloadUninstallDirectories.nsh", package, StringComparison.Ordinal);
        Assert.Contains("Generated NSIS payload paths must use runtime transaction variables", package, StringComparison.Ordinal);
        Assert.Contains("$destination = '$StagingDirectory'", package, StringComparison.Ordinal);
        Assert.Contains("IfErrors payload_stage_failed", package, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertTo-NsisLiteral $destination", package, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", package, StringComparison.Ordinal);
        Assert.Contains("SHA256=$hash", package, StringComparison.Ordinal);
        Assert.Contains("Runtime/SIDEY.Host.exe", package, StringComparison.Ordinal);
        Assert.Contains("Uninstall.exe", package, StringComparison.Ordinal);
        Assert.Contains("'SIDEY.Host.dll'", package, StringComparison.Ordinal);
        Assert.Contains("'Sidey.Core.dll'", package, StringComparison.Ordinal);
        Assert.Contains("'Sidey.Infrastructure.dll'", package, StringComparison.Ordinal);
        Assert.Contains("'Sidey.Overlay.dll'", package, StringComparison.Ordinal);
        Assert.Contains("'Sidey.Platform.Windows.dll'", package, StringComparison.Ordinal);
        Assert.Contains("'Sidey.Presentation.dll'", package, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-self-signed", package, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-AuthenticodeSignature", package, StringComparison.Ordinal);
        Assert.DoesNotContain("SIDEY-SelfSigned", package, StringComparison.Ordinal);
        Assert.DoesNotContain(".msi", package, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".sha256", package, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(RepositoryPath(
            "windows", "installer", "Sidey.Msi", "Sidey.Msi.wixproj")));

        string organizer = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "ConvertTo-PublishLayout.ps1"));
        Assert.Contains("SIDEY.Host.exe", organizer, StringComparison.Ordinal);
        Assert.Contains("Runtime", organizer, StringComparison.Ordinal);
        Assert.Contains("Uninstall.exe", organizer, StringComparison.Ordinal);
    }

    private static string ReadSetupScript() => File.ReadAllText(AssetPath("Sidey.Setup.nsi"));

    private static void AssertLocalizedValue(string json, string key, JsonElement expected)
    {
        Assert.True(LauncherLocalization.TryGet(json, key, out string? actual), key);
        Assert.Equal(expected.GetString(), actual);
    }

    [Fact]
    public void SelfContainedPayloadIsStagedBeforeReplacingTheExistingApp()
    {
        string setup = ReadSetupScript();
        string section = setup[setup.IndexOf("Section \"SIDEY\" MainSection", StringComparison.Ordinal)..];
        string[] operations =
        [
            "!include \"${PAYLOAD_INSTALL_INCLUDE}\"",
            "--detect-legacy-msi",
            "Call StopSideyProcesses",
            "!insertmacro RunInstallTransaction \"Activate\"",
            "!insertmacro RunInstallTransaction \"BeginRegistration\"",
            "WriteRegStr HKLM \"${PRODUCT_REGISTRY_KEY}\" \"InstalledVersion\"",
            "!insertmacro RunInstallTransaction \"Commit\"",
        ];
        int previous = -1;
        foreach (string operation in operations)
        {
            int position = section.IndexOf(operation, StringComparison.Ordinal);
            Assert.True(position > previous, $"Expected operation after the previous step: {operation}");
            previous = position;
        }
        Assert.DoesNotContain("EnsurePrerequisites", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("prerequisites.json", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("windowsappruntimeinstall", setup, StringComparison.OrdinalIgnoreCase);

        string uninstall = setup[setup.IndexOf("Section \"Uninstall\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SetupRuntime.ps1", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsurePrerequisites", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-AppxPackage", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("$PROGRAMFILES64\\dotnet", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupStagesAndRollsBackBeforeReplacingTheLiveInstall()
    {
        string setup = ReadSetupScript();
        string transaction = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallTransaction.cs"));

        Assert.Contains("RunInstallTransaction \"Prepare\"", setup, StringComparison.Ordinal);
        Assert.Contains("$StagingDirectory\\Runtime", setup, StringComparison.Ordinal);
        Assert.Contains("WriteUninstaller \"$StagingDirectory\\Uninstall.exe\"", setup, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"Activate\"", setup, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"BeginRegistration\"", setup, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"Commit\"", setup, StringComparison.Ordinal);
        Assert.Contains("IfErrors registration_failed", setup, StringComparison.Ordinal);
        Assert.Contains("RestorePreviousRegistration", transaction, StringComparison.Ordinal);
        Assert.Contains("Call RollbackInstallTransaction", setup, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"Complete\"", setup, StringComparison.Ordinal);
        Assert.Contains("Global\\SIDEY.Setup.InstallTransaction", setup, StringComparison.Ordinal);
        Assert.Contains("CleanupForUninstall", setup, StringComparison.Ordinal);
        Assert.Contains("PRODUCT_TRANSACTION_KEY", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecWait '\"$INSTDIR\\Uninstall.exe\" /S", setup, StringComparison.Ordinal);

        int onInitStart = setup.IndexOf("Function .onInit", StringComparison.Ordinal);
        string onInit = setup[onInitStart..setup.IndexOf("FunctionEnd", onInitStart, StringComparison.Ordinal)];
        Assert.Contains("$PendingInstallLocation == \"\"", onInit, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"InspectLegacyLocation\"", onInit, StringComparison.Ordinal);
        Assert.Contains("RunInstallTransaction \"InspectRelocationTarget\"", onInit, StringComparison.Ordinal);
        Assert.True(
            onInit.IndexOf("RunInstallTransaction \"Recover\"", StringComparison.Ordinal)
                < onInit.LastIndexOf("ReadRegStr $InstalledVersion", StringComparison.Ordinal),
            "Interrupted installation recovery must precede final version classification.");

        Assert.Contains("UndoTransaction", transaction, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", transaction, StringComparison.Ordinal);
        Assert.Contains("AssertSecureTransactionParent", transaction, StringComparison.Ordinal);
        Assert.Contains("ProtectStagingDirectory", transaction, StringComparison.Ordinal);
        Assert.Contains("previousRegistration", transaction, StringComparison.Ordinal);
        Assert.Contains("--product-version \"${APP_VERSION}\"", setup, StringComparison.Ordinal);
        Assert.Contains("--update-version \"${APP_UPDATE_VERSION}\"", setup, StringComparison.Ordinal);
        Assert.Contains(
            "${VersionCompare} $InstalledVersion \"${APP_UPDATE_VERSION}\"",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"InstalledVersion\" \"${APP_UPDATE_VERSION}\"",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DisplayVersion\" \"${APP_VERSION}\"",
            setup,
            StringComparison.Ordinal);
        Assert.Contains("state.PreviousRegistration.UpdateVersion", transaction, StringComparison.Ordinal);
        Assert.Contains("state.UpdateVersion", transaction, StringComparison.Ordinal);
        Assert.Contains("previousRegistration.ProductVersion", transaction, StringComparison.Ordinal);
        Assert.Contains("previousRegistration.UpdateVersion", transaction, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledUpdateCompletionMatchesInternalVersionButDisplaysProductVersion()
    {
        string app = File.ReadAllText(RepositoryPath("windows", "src", "Sidey.App", "App.xaml.cs"));

        Assert.Contains(
            "PendingNotificationVersion(\r\n            _updateService.CurrentUpdateVersion)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "_tray.NotifyUpdateInstalled(_updateService.CurrentVersion)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulSetupRemovesMachineWideInstallerDiagnosticsAfterLaunchHandling()
    {
        string setup = ReadSetupScript();
        int mainSectionStart = setup.IndexOf("Section \"SIDEY\" MainSection", StringComparison.Ordinal);
        string mainSection = setup[mainSectionStart..setup.IndexOf("SectionEnd", mainSectionStart, StringComparison.Ordinal)];
        int complete = mainSection.IndexOf("RunInstallTransaction \"Complete\"", StringComparison.Ordinal);
        int completedCleanly = mainSection.IndexOf("StrCpy $InstallerCompletedCleanly 1", StringComparison.Ordinal);
        Assert.True(complete >= 0 && completedCleanly > complete,
            "Installer diagnostics may only become disposable after transaction cleanup succeeds.");

        int launchStart = setup.IndexOf("Function LaunchSideyAsDesktopUser", StringComparison.Ordinal);
        string launch = setup[launchStart..setup.IndexOf("FunctionEnd", launchStart, StringComparison.Ordinal)];
        Assert.Contains("StrCpy $InstallerCompletedCleanly 0", launch, StringComparison.Ordinal);

        int guiEndStart = setup.IndexOf("Function .onGUIEnd", StringComparison.Ordinal);
        string guiEnd = setup[guiEndStart..setup.IndexOf("FunctionEnd", guiEndStart, StringComparison.Ordinal)];
        Assert.Contains("${If} $InstallerCompletedCleanly == 1", guiEnd, StringComparison.Ordinal);
        Assert.Contains("Sidey.InstallerErrorHelper.exe", guiEnd, StringComparison.Ordinal);
        Assert.Contains("--remove-installer-logs", guiEnd, StringComparison.Ordinal);
        Assert.DoesNotContain("RMDir /r", guiEnd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerRuntimeUsesCompiledHelpersWithoutPowerShellOrTaskkill()
    {
        string setup = ReadSetupScript();
        string errors = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrors.nsh"));
        string package = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-WindowsInstaller.ps1"));

        Assert.Contains("Sidey.InstallTransaction.exe", setup, StringComparison.Ordinal);
        Assert.Contains("Sidey.InstallerErrorHelper.exe", setup, StringComparison.Ordinal);
        Assert.Contains("Sidey.SetupSupport.exe", setup, StringComparison.Ordinal);
        Assert.Contains("--stop-sidey-processes", setup, StringComparison.Ordinal);
        Assert.Contains("ExecWait '\"$INSTDIR\\Runtime\\SIDEY.UninstallHelper.exe\" --stop-sidey-processes' $0", setup, StringComparison.Ordinal);
        int stopStart = setup.IndexOf("Call StopSideyProcesses", StringComparison.Ordinal);
        string stopBlock = setup[stopStart..setup.IndexOf(
            "!insertmacro RunInstallTransaction \"Activate\"",
            stopStart,
            StringComparison.Ordinal)];
        Assert.Contains("${If} $0 != 0", stopBlock, StringComparison.Ordinal);
        Assert.Contains("--normalize-error", errors, StringComparison.Ordinal);
        Assert.Contains("/DINSTALL_TRANSACTION_EXE=", package, StringComparison.Ordinal);
        Assert.Contains("/DINSTALLER_ERROR_HELPER_EXE=", package, StringComparison.Ordinal);
        Assert.Contains("-HelperPath $installTransactionExecutablePath", package, StringComparison.Ordinal);
        Assert.Contains("-HelperPath $installerErrorHelperExecutablePath", package, StringComparison.Ordinal);
        Assert.Contains("tests/Test-PowerShellSupport.ps1", package, StringComparison.Ordinal);
        Assert.Contains("forbiddenRuntimeToken", package, StringComparison.Ordinal);
        foreach (string source in new[] { setup, errors })
        {
            Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExecutionPolicy", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ps1", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nsExec::ExecToLog", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("taskkill.exe", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ElevatedInstallerDelegatesUserWorkToTheDesktopToken()
    {
        string setup = ReadSetupScript();
        string helper = File.ReadAllText(RepositoryPath(
            "windows", "src", "Sidey.Uninstaller", "Program.cs"));

        Assert.Contains("MUI_FINISHPAGE_RUN_FUNCTION LaunchSideyAsDesktopUser", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("MUI_FINISHPAGE_RUN \"$INSTDIR\\SIDEY.exe\"", setup, StringComparison.Ordinal);
        Assert.Contains(
            "--launch-sidey-as-desktop-user --show-about-after-install' $0",
            setup,
            StringComparison.Ordinal);
        Assert.Contains("--request-shutdown-as-desktop-user", setup, StringComparison.Ordinal);
        Assert.Contains("--complete-install-as-desktop-user", helper, StringComparison.Ordinal);
        Assert.Contains("--cleanup-startup-as-desktop-user", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("--run-windows-app-runtime-as-desktop-user", helper, StringComparison.Ordinal);

        Assert.Contains("GetShellWindow", helper, StringComparison.Ordinal);
        Assert.Contains("GetWindowThreadProcessId", helper, StringComparison.Ordinal);
        Assert.Contains("DuplicateTokenEx", helper, StringComparison.Ordinal);
        Assert.Contains("CreateProcessWithTokenW", helper, StringComparison.Ordinal);
        Assert.Contains("CreateEnvironmentBlock", helper, StringComparison.Ordinal);
        Assert.Contains("TryIsCurrentProcessElevated", helper, StringComparison.Ordinal);
        Assert.Contains("IsCurrentDesktopUser", helper, StringComparison.Ordinal);
        Assert.Contains("WindowsIdentity", helper, StringComparison.Ordinal);
        Assert.Contains("Registry.CurrentUser", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupCompletesDesktopRegistrationThroughRecoverableTransaction()
    {
        string setup = ReadSetupScript();
        string helper = File.ReadAllText(RepositoryPath(
            "windows", "src", "Sidey.Uninstaller", "Program.cs"));
        int mainSectionStart = setup.IndexOf("Section \"SIDEY\" MainSection", StringComparison.Ordinal);
        string mainSection = setup[mainSectionStart..setup.IndexOf(
            "SectionEnd",
            mainSectionStart,
            StringComparison.Ordinal)];

        int commit = mainSection.IndexOf(
            "RunInstallTransaction \"Commit\"",
            StringComparison.Ordinal);
        int complete = mainSection.IndexOf(
            "RunInstallTransaction \"Complete\"",
            StringComparison.Ordinal);
        Assert.True(
            commit >= 0 && complete > commit,
            "Desktop registration belongs to the recoverable committed completion phase.");

        Assert.Contains("--complete-install", helper, StringComparison.Ordinal);
        int requestShutdown = mainSection.IndexOf(
            "--request-shutdown-as-desktop-user",
            StringComparison.Ordinal);
        int forceStop = mainSection.IndexOf("Call StopSideyProcesses", StringComparison.Ordinal);
        Assert.True(
            requestShutdown >= 0 && forceStop > requestShutdown,
            "Setup must request a settings-flushing shutdown before the bounded force-stop fallback.");
        Assert.DoesNotContain(
            "Goto ",
            mainSection[requestShutdown..forceStop],
            StringComparison.Ordinal);
        Assert.Contains("--shutdown-for-update", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerErrorHelperExposesNormalizationAndSafeLogCleanup()
    {
        string source = File.ReadAllText(RepositoryPath(
            "windows", "installer", "Sidey.Setup", "InstallerErrorNormalizer.cs"));

        Assert.Contains("--normalize-error", source, StringComparison.Ordinal);
        Assert.Contains("--remove-installer-logs", source, StringComparison.Ordinal);
        Assert.Contains("Unsupported helper mode.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--check-only", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--cleanup-private-runtime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrerequisiteConfiguration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellTestsLiveUnderTheDedicatedTestsDirectory()
    {
        string scriptsDirectory = RepositoryPath("scripts", "windows");
        string testsDirectory = Path.Combine(scriptsDirectory, "tests");
        string[] canonicalTestScripts = Directory.GetFiles(
            testsDirectory,
            "Test-*.ps1",
            SearchOption.TopDirectoryOnly);
        string[] rootTestScripts = Directory.GetFiles(
            scriptsDirectory,
            "Test-*.ps1",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(canonicalTestScripts);
        Assert.Empty(rootTestScripts);

        string[] misplacedTests =
        [
            .. Directory.GetFiles(
                scriptsDirectory,
                "Test-*.ps1",
                SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(
                    Path.GetDirectoryName(path),
                    scriptsDirectory,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    Path.GetDirectoryName(path),
                    testsDirectory,
                    StringComparison.OrdinalIgnoreCase))
        ];
        Assert.Empty(misplacedTests);

        string integration = File.ReadAllText(RepositoryPath(
            ".github", "workflows", "ci.yml"));
        Assert.Contains(
            "./scripts/windows/tests/Test-SelfContainedPublish.ps1",
            integration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Test-FrameworkDependentPublish.ps1",
            integration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Test-PrerequisiteInstaller.ps1",
            integration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Install-WindowsPrerequisites.ps1",
            integration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--self-contained false",
            integration,
            StringComparison.Ordinal);
        Assert.Contains(
            "./scripts/windows/tests/Test-PublishedApplication.ps1",
            integration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "./scripts/windows/Test-",
            integration,
            StringComparison.Ordinal);

        string package = File.ReadAllText(RepositoryPath(
            "scripts", "windows", "New-WindowsInstaller.ps1"));
        Assert.Contains(
            "tests/Test-SelfContainedPublish.ps1",
            package,
            StringComparison.Ordinal);
        Assert.Contains(
            "tests/Test-InstallerPayloadUninstall.ps1",
            package,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows-build-and-tests.yml", true)]
    [InlineData("windows-release.yml", false)]
    public void WorkflowsValidatePublishedFilesAndLimitGuiSmokeToManualCandidates(string workflowName, bool runsManualSmoke)
    {
        string workflow = File.ReadAllText(RepositoryPath(".github", "workflows", workflowName));
        Assert.Contains("--self-contained true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsAppSDKSelfContained=false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-PrerequisiteInstaller.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-SelfContainedPublish.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SetupRuntime.ps1", workflow, StringComparison.Ordinal);
        if (runsManualSmoke)
        {
            Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
            Assert.Contains("Test-PublishedApplication.ps1", workflow, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("Test-PublishedApplication.ps1", workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManualCandidateWorkflowUploadsOnlyEncryptedInstallerArchives()
    {
        string workflow = File.ReadAllText(RepositoryPath(".github", "workflows", "windows-build-and-tests.yml"));
        int encryptionStep = workflow.IndexOf("- name: Encrypt owner-only test candidate", StringComparison.Ordinal);
        int uploadStep = workflow.IndexOf("- name: Upload encrypted manual test candidate", StringComparison.Ordinal);
        Assert.True(encryptionStep >= 0 && uploadStep > encryptionStep);
        Assert.Contains("-t7z -mhe=on", workflow, StringComparison.Ordinal);
        Assert.Contains("IsNullOrWhiteSpace($env:SIDEY_DEV_ARTIFACT_PASSWORD)", workflow, StringComparison.Ordinal);
        Assert.Contains("path: ${{ runner.temp }}/sidey-windows-private-${{ github.sha }}.7z", workflow[uploadStep..], StringComparison.Ordinal);
        Assert.DoesNotContain("*Setup.exe", workflow[uploadStep..], StringComparison.Ordinal);
    }

    private static string Value(XDocument document, string name) =>
        document.Descendants(name).Single().Value;

    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", name);

    private static string RepositoryPath(params string[] pathSegments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "windows", "src")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine([root!.FullName, .. pathSegments]);
    }
}
