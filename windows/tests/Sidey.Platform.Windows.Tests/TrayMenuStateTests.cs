using Sidey.Core.Domain;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class TrayMenuStateTests
{
    [Fact]
    public void GoogleSignInCompletionConfirmsSuccessAndOpensSideyWhenClicked()
    {
        (string Title, string Body, TrayCommand ClickCommand) notification = TrayIconService.GoogleSignInCompleteNotification();

        Assert.Equal("SIDEY", notification.Title);
        Assert.Equal("Google 로그인이 완료되었습니다. 브라우저의 로그인 탭을 닫아도 됩니다.", notification.Body);
        Assert.Equal(TrayCommand.Open, notification.ClickCommand);
    }

    [Theory]
    [InlineData(false, true, 0x0000u)]
    [InlineData(true, true, 0x0008u)]
    [InlineData(false, false, 0x0001u)]
    [InlineData(true, false, 0x0009u)]
    public void NativeMenuFlagsUseTheWindowsCheckMark(
        bool isChecked,
        bool isEnabled,
        uint expected)
    {
        Assert.Equal(expected, TrayIconService.NativeMenuFlags(isChecked, isEnabled));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OverlayHiddenCheckStateIsCheckedOnlyWhileHidden(
        bool overlayVisible,
        bool expected)
    {
        Assert.Equal(expected, TrayIconService.OverlayHiddenCheckState(overlayVisible));
    }

    [Theory]
    [InlineData(AppThemePreference.System, 1)]
    [InlineData(AppThemePreference.Dark, 2)]
    [InlineData(AppThemePreference.Light, 3)]
    public void TrayMenuThemeMapsToTheRequestedWindowsAppMode(
        AppThemePreference theme,
        int expected)
    {
        Assert.Equal(expected, TrayIconService.PreferredAppModeValue(theme));
    }

    [Theory]
    [InlineData((int)TrayUpdateNotification.Latest, "", "최신 버전입니다.")]
    [InlineData((int)TrayUpdateNotification.Available, "1.2.2", "SIDEY 1.2.2 업데이트가 있습니다. 업데이트에서 다운로드하세요.")]
    [InlineData(
        (int)TrayUpdateNotification.Failed,
        "",
        "업데이트를 확인하지 못했습니다. 잠시 후 다시 시도해 주세요.")]
    [InlineData(
        (int)TrayUpdateNotification.Installed,
        "1.3.2",
        "SIDEY 1.3.2 업데이트를 완료했습니다. 왼쪽 클릭하여 변경 내용을 확인해 주세요.")]
    public void UpdateNotificationsUseTheLocalizedBody(
        int notification,
        string version,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayIconService.UpdateNotificationBody((TrayUpdateNotification)notification, version));
    }

    [Fact]
    public void DownloadedUpdateNotificationSaysInstallationIsReady()
    {
        string body = TrayIconService.UpdateNotificationBody(
            TrayUpdateNotification.ReadyToInstall,
            "1.2.2");

        Assert.Contains("1.2.2", body);
        Assert.Contains("설치", body);
    }

    [Theory]
    [InlineData((int)TrayUpdateNotification.Available, TrayCommand.OpenUpdates)]
    [InlineData((int)TrayUpdateNotification.ReadyToInstall, TrayCommand.OpenUpdates)]
    [InlineData((int)TrayUpdateNotification.Latest, TrayCommand.Open)]
    [InlineData((int)TrayUpdateNotification.Failed, TrayCommand.Open)]
    [InlineData((int)TrayUpdateNotification.Installed, TrayCommand.ReleaseNotes)]
    public void UpdateNotificationOpensTheRelevantDestination(
        int notification,
        TrayCommand expected)
    {
        Assert.Equal(
            expected,
            TrayIconService.NotificationClickCommand((TrayUpdateNotification)notification));
    }
}
