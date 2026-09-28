using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Sidey.Core.Localization;
using Sidey.Core.Storage;
using Sidey.Platform.Windows;
using Sidey.Presentation.Services;

namespace Sidey.App.Services;

internal sealed class WindowsUpdateServiceAdapter : IUpdateService
{
    private static readonly Uri s_releaseNotesUri = new(
        "https://sidey-app.github.io/SIDEY/whats-new/windows/");
    private static readonly string s_lastCheckedPath = Path.Combine(
        SideyStoragePaths.LocalApplicationDataRoot(),
        "SIDEY",
        "update-last-checked.txt");
    private static readonly string s_preferencesPath = Path.Combine(
        SideyStoragePaths.LocalApplicationDataRoot(),
        "SIDEY",
        "update-preferences.json");
    private readonly WindowsUpdateService _service;

    public WindowsUpdateServiceAdapter()
    {
        Assembly assembly = typeof(App).Assembly;
        string productVersion = assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("SIDEY assembly version metadata is missing.");
        string? fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!Version.TryParse(fileVersion, out Version? parsedFileVersion)
            || parsedFileVersion.Build < 0
            || parsedFileVersion.Revision != 0)
        {
            throw new InvalidOperationException("SIDEY file version metadata is missing or invalid.");
        }
        _service = new WindowsUpdateService(
            currentProductVersion: productVersion,
            currentUpdateVersion: parsedFileVersion.ToString(3));
        LastCheckedAt = ReadLastCheckedAt();
        Preferences = ReadPreferences();
    }

    public string CurrentVersion => _service.EffectiveCurrentProductVersion;

    public string CurrentUpdateVersion => _service.EffectiveCurrentUpdateVersion;

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public Uri CurrentReleaseNotesUri => s_releaseNotesUri;

    public UpdatePreferences Preferences { get; private set; }

    public void SavePreferences(UpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string? directory = Path.GetDirectoryName(s_preferencesPath);
        if (directory is null)
        {
            throw new InvalidOperationException("The update preferences path is invalid.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = s_preferencesPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, s_preferencesPath, overwrite: true);
            Preferences = preferences;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task CleanupInstalledUpdatesAsync()
    {
        try
        {
            int deletedFiles = await Task.Run(_service.CleanupInstalledUpdates);
            StartupDiagnostics.Stage($"update-cache-cleanup deleted-files={deletedFiles}");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("update-cache-cleanup", exception);
        }
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        StartupDiagnostics.Stage("update-check-started");
        try
        {
            WindowsUpdateManifest? manifest = await _service.CheckAsync(cancellationToken);
            LastCheckedAt = DateTimeOffset.UtcNow;
            SaveLastCheckedAt(LastCheckedAt.Value);
            StartupDiagnostics.Stage(
                $"update-check-completed result={(manifest is null ? "latest" : "available")}");
            return manifest is null
                ? null
                : new AvailableUpdate(
                    manifest.ProductVersion,
                    manifest.InstallerUri,
                    manifest.Sha256,
                    s_releaseNotesUri,
                    manifest.UpdateVersion,
                    manifest.UpdateTag);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("update-check", exception);
            throw;
        }
    }

    public async Task DownloadAndLaunchInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        await DownloadInstallerAsync(update, cancellationToken, progress);
        string installerPath = _service.InstallerPath(update.UpdateVersion!);
        WindowsUpdateService.LaunchInstaller(installerPath);
    }

    public async Task DownloadInstallerAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        WindowsUpdateManifest manifest = ToManifest(update);
        try
        {
            await _service.DownloadInstallerAsync(manifest, cancellationToken, progress);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.NonFatal("update-download-install", exception);
            throw;
        }
    }

    public Task<bool> IsInstallerReadyAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken = default) =>
        _service.IsInstallerReadyAsync(ToManifest(update), cancellationToken);

    private static WindowsUpdateManifest ToManifest(AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.InstallerUri is null
            || string.IsNullOrWhiteSpace(update.Sha256)
            || string.IsNullOrWhiteSpace(update.UpdateVersion)
            || string.IsNullOrWhiteSpace(update.UpdateTag))
        {
            throw new InvalidDataException(I18n.Get("update.install.missing"));
        }

        return new WindowsUpdateManifest(
            "production",
            update.Version,
            update.UpdateVersion,
            update.UpdateTag,
            update.InstallerUri,
            update.Sha256);
    }

    private static UpdatePreferences ReadPreferences()
    {
        try
        {
            return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(s_preferencesPath))
                ?? new UpdatePreferences();
        }
        catch (IOException) { return new UpdatePreferences(); }
        catch (UnauthorizedAccessException) { return new UpdatePreferences(); }
        catch (JsonException) { return new UpdatePreferences(); }
    }

    public Task OpenReleaseNotesAsync(Uri releaseNotesUri)
    {
        ArgumentNullException.ThrowIfNull(releaseNotesUri);
        if (!StringComparer.Ordinal.Equals(releaseNotesUri.AbsoluteUri, s_releaseNotesUri.AbsoluteUri))
        {
            throw new InvalidOperationException("The SIDEY release notes URL is not trusted.");
        }

        Process.Start(new ProcessStartInfo(releaseNotesUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        StartupDiagnostics.Stage("release-notes-opened");
        return Task.CompletedTask;
    }

    private static DateTimeOffset? ReadLastCheckedAt()
    {
        try
        {
            string value = File.ReadAllText(s_lastCheckedPath).Trim();
            return DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveLastCheckedAt(DateTimeOffset timestamp)
    {
        try
        {
            string? directory = Path.GetDirectoryName(s_lastCheckedPath);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(
                s_lastCheckedPath,
                timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
