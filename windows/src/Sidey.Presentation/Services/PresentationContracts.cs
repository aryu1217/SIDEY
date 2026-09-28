namespace Sidey.Presentation.Services;

public sealed record MonitorOption(string Identifier, string Name, bool IsPrimary);

public sealed record ValidationMetricsSnapshot(
    double ElapsedSeconds,
    int SampleCount,
    double MaximumFrameMilliseconds,
    long CurrentWorkingSetBytes,
    long PeakWorkingSetBytes,
    uint MaximumGdiHandles,
    uint MaximumUserHandles);

public sealed record AvailableUpdate(
    string Version,
    Uri? InstallerUri = null,
    string? Sha256 = null,
    Uri? ReleaseNotesUri = null,
    string? UpdateVersion = null,
    string? UpdateTag = null);

public sealed record UpdatePreferences(
    bool AutoDownload = false,
    bool NotifyWhenAvailable = true,
    bool ShowReleaseNotesAfterUpdate = true,
    bool AutoInstall = true);

public interface IUpdateService
{
    public string CurrentVersion { get; }

    public string CurrentUpdateVersion { get; }

    public DateTimeOffset? LastCheckedAt { get; }

    public Uri CurrentReleaseNotesUri { get; }

    public UpdatePreferences Preferences { get; }

    public void SavePreferences(UpdatePreferences preferences);

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    public Task<bool> IsInstallerReadyAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default);

    public Task DownloadInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null);

    public Task DownloadAndLaunchInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null,
        bool silent = false);

    public Task OpenReleaseNotesAsync(Uri releaseNotesUri);
}
