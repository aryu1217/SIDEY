using Sidey.Platform.Windows.Deployment;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsUpdateCompletionTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"sidey-update-completion-{Guid.NewGuid():N}");

    [Fact]
    public void RetainedUserDataWithoutInstallerMarkerDoesNotReportUpdate()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "last-launched-version.txt"), "1.0.0");
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.Null(tracker.PendingNotificationVersion("1.4.1000"));
        Assert.True(tracker.TryMarkLaunched("1.4.1000"));
        Assert.False(File.Exists(StatePath()));
    }

    [Fact]
    public void InstallerUpgradeMarkerIsConsumedOnce()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath(), "1.4.1000");
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.Equal("1.4.1000", tracker.PendingNotificationVersion("1.4.1000"));
        Assert.True(tracker.TryMarkLaunched("1.4.1000"));
        Assert.Null(tracker.PendingNotificationVersion("1.4.1000"));
    }

    [Theory]
    [InlineData("1.4.999")]
    [InlineData("1.4.1001")]
    [InlineData("invalid")]
    public void StaleOrInvalidMarkerDoesNotReportDifferentUpdate(string marker)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath(), marker);
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.Null(tracker.PendingNotificationVersion("1.4.1000"));
        Assert.True(tracker.TryMarkLaunched("1.4.1000"));
        Assert.False(File.Exists(StatePath()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedUpdateConsumesMarkerAndHonorsReleaseNotesPreference(bool showReleaseNotes)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath(), "1.4.1000");
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.True(tracker.TryConsumeCompletedUpdate(
            "1.4.1000", showReleaseNotes, out bool shouldOpenReleaseNotes));

        Assert.Equal(showReleaseNotes, shouldOpenReleaseNotes);
        Assert.False(File.Exists(StatePath()));
        Assert.True(tracker.TryConsumeCompletedUpdate(
            "1.4.1000", true, out bool shouldOpenOnNextLaunch));
        Assert.False(shouldOpenOnNextLaunch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.4.999")]
    [InlineData("1.4.1001")]
    public void MissingOrDifferentBuildMarkerDoesNotOpenReleaseNotes(string? marker)
    {
        Directory.CreateDirectory(_directory);
        if (marker is not null)
            File.WriteAllText(StatePath(), marker);
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.True(tracker.TryConsumeCompletedUpdate(
            "1.4.1000", true, out bool shouldOpenReleaseNotes));

        Assert.False(shouldOpenReleaseNotes);
        Assert.False(File.Exists(StatePath()));
    }

    [Fact]
    public void UnreadableCompletionStateDoesNotOpenReleaseNotesOrReportConsumption()
    {
        Directory.CreateDirectory(StatePath());
        var tracker = new WindowsUpdateCompletionTracker(StatePath());

        Assert.False(tracker.TryConsumeCompletedUpdate(
            "1.4.1000", true, out bool shouldOpenReleaseNotes));

        Assert.False(shouldOpenReleaseNotes);
        Assert.True(Directory.Exists(StatePath()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string StatePath() => Path.Combine(_directory, "pending-installed-update.txt");
}
