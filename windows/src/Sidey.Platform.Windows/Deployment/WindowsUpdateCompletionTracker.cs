using Sidey.Core.Storage;

namespace Sidey.Platform.Windows.Deployment;

public sealed class WindowsUpdateCompletionTracker
{
    private readonly string _path;

    public WindowsUpdateCompletionTracker(string? path = null)
    {
        _path = path ?? Path.Combine(
            SideyStoragePaths.LocalApplicationDataRoot(),
            "SIDEY",
            "pending-installed-update.txt");
    }

    public string? PendingNotificationVersion(string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        try
        {
            // Only the installer creates this marker after a genuine upgrade.
            // Existing onboarding/user data is not proof that setup upgraded.
            return File.Exists(_path)
                && string.Equals(File.ReadAllText(_path).Trim(), currentVersion, StringComparison.Ordinal)
                ? currentVersion : null;
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

    public bool TryMarkLaunched(string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        try
        {
            File.Delete(_path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
