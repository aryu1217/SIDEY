using System.Diagnostics;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsUpdateCleanupTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("SIDEY-update-cleanup-").FullName;

    [Fact]
    public void RemovesInstalledVersionsButPreservesNewerUpdatesAndUnrelatedFiles()
    {
        string oldInstaller = CreateFile("1.0.9", "SIDEY-Windows-x64-v1.0.9-Setup.exe");
        string currentInstaller = CreateFile("1.0.10", "SIDEY-Windows-x64-v1.0.10-Setup.exe");
        string partial = CreateFile("1.0.10", "SIDEY-Windows-x64-v1.0.10-Setup.exe.download");
        string legacyInstaller = CreateFile("1.0.5", "SIDEY-Windows-x64-v1.0.5.msi");
        string prerelease = CreateFile("1.0.10-alpha.1", "SIDEY-Windows-x64-v1.0.10-alpha.1-Setup.exe");
        string futureInstaller = CreateFile("1.0.11", "SIDEY-Windows-x64-v1.0.11-Setup.exe");
        string futurePartial = CreateFile("1.0.11", "SIDEY-Windows-x64-v1.0.11-Setup.exe.download");
        string unrelated = CreateFile("1.0.10", "notes.txt");
        string nested = CreateFile("1.0.10/nested", "keep.txt");
        string invalidVersion = CreateFile("not-a-version", "SIDEY-Windows-x64-vnot-a-version-Setup.exe");
        string oversizedVersion = CreateFile("999999999999999999.0.0", "keep.txt");
        var service = new WindowsUpdateService(
            currentProductVersion: "1.0.10",
            currentUpdateVersion: "1.0.10",
            updateCacheDirectory: _cache);

        Assert.Equal(5, service.CleanupInstalledUpdates());

        foreach (string file in new[] { oldInstaller, currentInstaller, partial, legacyInstaller, prerelease })
        {
            Assert.False(File.Exists(file));
        }
        foreach (string file in new[] { futureInstaller, futurePartial, unrelated, nested, invalidVersion, oversizedVersion })
        {
            Assert.True(File.Exists(file));
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldInstaller)));
        Assert.Equal(0, service.CleanupInstalledUpdates());
    }

    [Fact]
    public void LockedInstallerIsRetriedOnTheNextCleanupWithoutBlockingOtherFiles()
    {
        string installer = CreateFile("1.0.10", "SIDEY-Windows-x64-v1.0.10-Setup.exe");
        string partial = CreateFile("1.0.10", "SIDEY-Windows-x64-v1.0.10-Setup.exe.download");
        var service = new WindowsUpdateService(
            currentProductVersion: "1.0.10",
            currentUpdateVersion: "1.0.10",
            updateCacheDirectory: _cache);

        using (FileStream locked = File.Open(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(1, service.CleanupInstalledUpdates());
            Assert.True(File.Exists(installer));
            Assert.False(File.Exists(partial));
        }

        Assert.Equal(1, service.CleanupInstalledUpdates());
        Assert.False(Directory.Exists(Path.GetDirectoryName(installer)));
    }

    [Fact]
    public void RemovesCompletedIncrementalFilesButPreservesUnrelatedAndNewerCaches()
    {
        string digest = new('a', 64);
        string[] owned =
        [
            CreateFile("1.0.9/incremental/files", digest + ".bin"),
            CreateFile("1.0.10/incremental/files", digest + ".bin"),
            CreateFile("1.0.10/incremental/files", new string('B', 64) + ".bin.download"),
            CreateFile("1.0.10/incremental", "manifest.json"),
            CreateFile("1.0.10/incremental", "SIDEY-Windows-x64-v1.0.10-Update.exe"),
            CreateFile("1.0.10/incremental", "SIDEY-Windows-x64-v1.0.10-Update.exe.download"),
        ];
        string[] preserved =
        [
            CreateFile("1.0.10/incremental", "notes.txt"),
            CreateFile("1.0.10/incremental", "SIDEY-Windows-x64-v1.0.11-Update.exe"),
            CreateFile("1.0.10/incremental/files", "not-a-digest.bin"),
            CreateFile("1.0.10/incremental/files", digest + ".txt"),
            CreateFile("1.0.10/incremental/files/nested", digest + ".bin"),
            CreateFile("1.0.11/incremental/files", digest + ".bin"),
            CreateFile("1.0.11/incremental", "manifest.json"),
            CreateFile("1.0.11/incremental", "SIDEY-Windows-x64-v1.0.11-Update.exe"),
        ];
        var service = new WindowsUpdateService(
            currentProductVersion: "1.0.10",
            currentUpdateVersion: "1.0.10",
            updateCacheDirectory: _cache);

        Assert.Equal(owned.Length, service.CleanupInstalledUpdates());

        foreach (string file in owned)
            Assert.False(File.Exists(file));
        foreach (string file in preserved)
            Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(_cache, "1.0.9")));
        Assert.Equal(0, service.CleanupInstalledUpdates());
    }

    [Fact]
    public void LockedIncrementalUpdaterIsRetriedWithoutBlockingPayloadCleanup()
    {
        string updater = CreateFile("1.0.10/incremental", "SIDEY-Windows-x64-v1.0.10-Update.exe");
        string manifest = CreateFile("1.0.10/incremental", "manifest.json");
        string payload = CreateFile("1.0.10/incremental/files", new string('a', 64) + ".bin");
        var service = new WindowsUpdateService(
            currentProductVersion: "1.0.10",
            currentUpdateVersion: "1.0.10",
            updateCacheDirectory: _cache);

        using (FileStream locked = File.Open(updater, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(2, service.CleanupInstalledUpdates());
            Assert.True(File.Exists(updater));
            Assert.False(File.Exists(manifest));
            Assert.False(File.Exists(payload));
        }

        Assert.Equal(1, service.CleanupInstalledUpdates());
        Assert.False(Directory.Exists(Path.Combine(_cache, "1.0.10")));
        Assert.Equal(0, service.CleanupInstalledUpdates());
    }

    [Theory]
    [InlineData("1.0.10/incremental")]
    [InlineData("1.0.10/incremental/files")]
    public void DoesNotFollowIncrementalCacheDirectoryJunctions(string linkedDirectory)
    {
        string target = Directory.CreateTempSubdirectory("SIDEY-update-cleanup-target-").FullName;
        string link = Path.Combine(_cache, linkedDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        string payload = Path.Combine(target, new string('a', 64) + ".bin");
        string manifest = Path.Combine(target, "manifest.json");
        File.WriteAllText(payload, "must survive cleanup");
        File.WriteAllText(manifest, "must survive cleanup");
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            bool exited = process.WaitForExit(10000);
            if (!exited)
                process.Kill(entireProcessTree: true);
            Assert.True(exited);
            Assert.Equal(0, process.ExitCode);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            var service = new WindowsUpdateService(
                currentProductVersion: "1.0.10",
                currentUpdateVersion: "1.0.10",
                updateCacheDirectory: _cache);

            Assert.Equal(0, service.CleanupInstalledUpdates());
            Assert.Equal("must survive cleanup", File.ReadAllText(payload));
            Assert.Equal("must survive cleanup", File.ReadAllText(manifest));
            Assert.True(Directory.Exists(link));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void MissingCacheDoesNotGetCreated()
    {
        string missing = Path.Combine(_cache, "missing");
        var service = new WindowsUpdateService(
            currentProductVersion: "1.0.10",
            currentUpdateVersion: "1.0.10",
            updateCacheDirectory: missing);

        Assert.Equal(0, service.CleanupInstalledUpdates());
        Assert.False(Directory.Exists(missing));
    }

    private string CreateFile(string version, string name)
    {
        string directory = Path.Combine(_cache, version);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name);
        File.WriteAllText(file, "update cleanup fixture");
        return file;
    }

    public void Dispose() => Directory.Delete(_cache, recursive: true);
}
