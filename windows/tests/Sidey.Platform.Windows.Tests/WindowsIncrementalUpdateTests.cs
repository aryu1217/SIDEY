using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sidey.Platform.Windows.Deployment;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsIncrementalUpdateTests
{
    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public async Task DistantOlderInstallationDownloadsOnlyChangedFilesAndPreparesExactLatestPayload(Architecture architecture)
    {
        using var fixture = new UpdateFixture(architecture);
        WindowsUpdateManifest update = fixture.CreateUpdate();

        string updater = await fixture.Service.DownloadInstallerAsync(update);

        Assert.EndsWith("-Update.exe", updater, StringComparison.Ordinal);
        Assert.Equal(fixture.Updater, await File.ReadAllBytesAsync(updater));
        Assert.Equal(new[] { fixture.RangeFor("Runtime/SIDEY.Host.exe") }, fixture.Handler.PayloadRanges);
        Assert.Equal(0, fixture.Handler.FullDownloads);
        string filesDirectory = Path.Combine(Path.GetDirectoryName(updater)!, "files");
        Assert.Equal(fixture.Files.Count, Directory.GetFiles(filesDirectory).Length);
        foreach ((string path, byte[] bytes) in fixture.Files)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(filesDirectory, Digest(bytes) + ".bin")));
        Assert.Equal("obsolete", await File.ReadAllTextAsync(Path.Combine(fixture.InstallationDirectory, "obsolete.dll")));
        Assert.True(await fixture.Service.IsInstallerReadyAsync(update));

        fixture.Handler.RejectNetwork = true;
        Assert.Equal(updater, await fixture.Service.DownloadInstallerAsync(update));
        Assert.True(await fixture.Service.IsInstallerReadyAsync(update));
        Assert.False(File.Exists(fixture.Service.InstallerPath(update.UpdateVersion)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptedInstalledFileIsFetchedInsteadOfReused(bool missing)
    {
        using var fixture = new UpdateFixture();
        string launcher = Path.Combine(fixture.InstallationDirectory, "SIDEY.exe");
        if (missing)
            File.Delete(launcher);
        else
            await File.WriteAllBytesAsync(launcher, [.. Enumerable.Repeat((byte)0x41, fixture.Files["SIDEY.exe"].Length)]);

        string updater = await fixture.Service.DownloadInstallerAsync(fixture.CreateUpdate());

        Assert.EndsWith("-Update.exe", updater, StringComparison.Ordinal);
        Assert.Equal(new[] { fixture.RangeFor("SIDEY.exe"), fixture.RangeFor("Runtime/SIDEY.Host.exe") },
            fixture.Handler.PayloadRanges);
        Assert.Equal(fixture.Files["SIDEY.exe"], await File.ReadAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(updater)!, "files", Digest(fixture.Files["SIDEY.exe"]) + ".bin")));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("manifest-hash")]
    [InlineData("path")]
    [InlineData("range")]
    [InlineData("updater-hash")]
    [InlineData("file-hash")]
    [InlineData("range-header")]
    [InlineData("range-ignored")]
    [InlineData("timeout")]
    public async Task InvalidIncrementalArtifactsFallBackToIndependentlyVerifiedFullSetup(string failure)
    {
        using var fixture = new UpdateFixture();
        fixture.Handler.Failure = failure;
        WindowsUpdateManifest update = fixture.CreateUpdate(document =>
        {
            if (failure == "path")
                document["files"]![0]!["path"] = "../SIDEY.exe";
            if (failure == "range")
                document["files"]![1]!["offset"] = 0;
            if (failure == "updater-hash")
                document["updater"]!["sha256"] = new string('0', 64);
            if (failure == "file-hash")
                document["files"]![1]!["sha256"] = new string('0', 64);
        });

        string setup = await fixture.Service.DownloadInstallerAsync(update);

        Assert.Equal(fixture.Service.InstallerPath(update.UpdateVersion), setup);
        Assert.Equal(fixture.Setup, await File.ReadAllBytesAsync(setup));
        Assert.Equal(1, fixture.Handler.FullDownloads);
        Assert.True(await fixture.Service.IsInstallerReadyAsync(update));
    }

    [Fact]
    public async Task Arm64IncrementalArtifactsWithX64NamesFallBackOnlyToArm64Setup()
    {
        using var fixture = new UpdateFixture(Architecture.Arm64);
        WindowsUpdateManifest update = fixture.CreateUpdate(document =>
        {
            document["updater"]!["name"] = "SIDEY-Windows-x64-v2.2640.9-Update.exe";
            document["payload"]!["name"] = "SIDEY-Windows-x64-v2.2640.9-Payload.bin";
        });

        string setup = await fixture.Service.DownloadInstallerAsync(update);

        Assert.EndsWith("SIDEY-Windows-arm64-v2.2640.9-Setup.exe", setup, StringComparison.Ordinal);
        Assert.Equal(fixture.Setup, await File.ReadAllBytesAsync(setup));
        Assert.Empty(fixture.Handler.PayloadRanges);
        Assert.Equal(0, fixture.Handler.UpdaterDownloads);
        Assert.Equal(1, fixture.Handler.FullDownloads);
    }

    [Fact]
    public async Task FullSetupFallbackStillRejectsItsOwnChecksumMismatch()
    {
        using var fixture = new UpdateFixture();
        fixture.Handler.Failure = "range-ignored";
        WindowsUpdateManifest update = fixture.CreateUpdate() with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadInstallerAsync(update));

        Assert.Equal(1, fixture.Handler.FullDownloads);
        Assert.False(File.Exists(fixture.Service.InstallerPath(update.UpdateVersion)));
        Assert.False(await fixture.Service.IsInstallerReadyAsync(update));
    }

    [Fact]
    public async Task SmallerFullSetupAvoidsDownloadingIncrementalRanges()
    {
        using var fixture = new UpdateFixture();
        fixture.Handler.AdvertisedSetupSize = 1;
        WindowsUpdateManifest update = fixture.CreateUpdate();

        string setup = await fixture.Service.DownloadInstallerAsync(update);

        Assert.Equal(fixture.Service.InstallerPath(update.UpdateVersion), setup);
        Assert.Empty(fixture.Handler.PayloadRanges);
        Assert.Equal(0, fixture.Handler.UpdaterDownloads);
        Assert.Equal(1, fixture.Handler.FullDownloads);
    }

    [Fact]
    public async Task CancellationDuringRangeDownloadDoesNotStartFullFallback()
    {
        using var fixture = new UpdateFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.CancelRange = cancellation;
        WindowsUpdateManifest update = fixture.CreateUpdate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.DownloadInstallerAsync(update, cancellation.Token));

        Assert.Equal(0, fixture.Handler.FullDownloads);
        Assert.False(File.Exists(fixture.Service.InstallerPath(update.UpdateVersion)));
    }

    [Fact]
    public async Task CorruptedPreparedFileCannotBeReportedReady()
    {
        using var fixture = new UpdateFixture();
        WindowsUpdateManifest update = fixture.CreateUpdate();
        string updater = await fixture.Service.DownloadInstallerAsync(update);
        string cacheFile = Path.Combine(Path.GetDirectoryName(updater)!, "files", Digest(fixture.Files["SIDEY.exe"]) + ".bin");
        await File.WriteAllBytesAsync(cacheFile, new byte[fixture.Files["SIDEY.exe"].Length]);
        fixture.Handler.RejectNetwork = true;

        Assert.False(await fixture.Service.IsInstallerReadyAsync(update));
    }

    [Fact]
    public void SilentUpdaterArgumentsCarryAbsoluteCacheDirectoryAndRestartFlag()
    {
        string updater = Path.Combine(Path.GetTempPath(), "SIDEY update with spaces", "SIDEY-Windows-x64-v2.2640.9-Update.exe");

        Assert.Equal($"/S /RESTARTSIDEY /UPDATECACHE=\"{Path.Combine(Path.GetDirectoryName(updater)!, "files")}\"",
            WindowsUpdateService.InstallerArguments(updater, silent: true));
        Assert.Equal("/S /RESTARTSIDEY", WindowsUpdateService.InstallerArguments(
            updater.Replace("-Update.exe", "-Setup.exe", StringComparison.Ordinal), silent: true));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class UpdateFixture : IDisposable
    {
        private const string Version = "2.2640.9";
        private readonly string _prefix;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sidey-incremental-{Guid.NewGuid():N}");
        private readonly HttpClient _client;
        private readonly JsonObject _manifest;
        private readonly Dictionary<string, string> _ranges = new(StringComparer.Ordinal);

        public UpdateFixture(Architecture architecture = Architecture.X64)
        {
            _prefix = "SIDEY-Windows-" + (architecture == Architecture.Arm64 ? "arm64" : "x64") + "-v" + Version;
            InstallationDirectory = Path.Combine(_directory, "installed");
            Directory.CreateDirectory(Path.Combine(InstallationDirectory, "Runtime"));
            Files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["SIDEY.exe"] = Encoding.UTF8.GetBytes("unchanged SIDEY launcher"),
                ["Runtime/SIDEY.Host.exe"] = Encoding.UTF8.GetBytes("latest SIDEY host with new UI and update support"),
                ["Runtime/SIDEY.UninstallHelper.exe"] = Encoding.UTF8.GetBytes("unchanged uninstall helper"),
            };
            File.WriteAllBytes(Path.Combine(InstallationDirectory, "SIDEY.exe"), Files["SIDEY.exe"]);
            File.WriteAllText(Path.Combine(InstallationDirectory, "Runtime", "SIDEY.Host.exe"), "distant older host");
            File.WriteAllBytes(Path.Combine(InstallationDirectory, "Runtime", "SIDEY.UninstallHelper.exe"), Files["Runtime/SIDEY.UninstallHelper.exe"]);
            File.WriteAllText(Path.Combine(InstallationDirectory, "obsolete.dll"), "obsolete");
            Updater = Encoding.UTF8.GetBytes("NSIS updater fixture");
            Setup = [.. Enumerable.Repeat((byte)0x53, 4096)];
            var files = new JsonArray();
            using var payload = new MemoryStream();
            foreach ((string path, byte[] bytes) in Files)
            {
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                    gzip.Write(bytes);
                byte[] segment = compressed.ToArray();
                long offset = payload.Length;
                payload.Write(segment);
                _ranges[path] = $"bytes={offset}-{offset + segment.Length - 1}";
                files.Add(new JsonObject
                {
                    ["path"] = path,
                    ["sha256"] = Digest(bytes),
                    ["size"] = bytes.Length,
                    ["offset"] = offset,
                    ["length"] = segment.Length,
                });
            }
            byte[] payloadBytes = payload.ToArray();
            _manifest = new JsonObject
            {
                ["schema"] = 1,
                ["version"] = Version,
                ["updater"] = new JsonObject { ["name"] = _prefix + "-Update.exe", ["sha256"] = Digest(Updater), ["size"] = Updater.Length },
                ["payload"] = new JsonObject { ["name"] = _prefix + "-Payload.bin", ["sha256"] = Digest(payloadBytes), ["size"] = payloadBytes.Length },
                ["files"] = files,
            };
            Handler = new ArtifactHandler(payloadBytes, Updater, Setup);
            _client = new HttpClient(Handler);
            Service = new WindowsUpdateService(_client, "0.1.0", "0.1.0", Path.Combine(_directory, "cache"), InstallationDirectory, processArchitecture: architecture);
        }

        public string InstallationDirectory { get; }
        public Dictionary<string, byte[]> Files { get; }
        public byte[] Updater { get; }
        public byte[] Setup { get; }
        public ArtifactHandler Handler { get; }
        public WindowsUpdateService Service { get; }

        public string RangeFor(string path) => _ranges[path];

        public WindowsUpdateManifest CreateUpdate(Action<JsonObject>? change = null)
        {
            change?.Invoke(_manifest);
            Handler.Manifest = Handler.Failure == "json" ? Encoding.UTF8.GetBytes("{invalid JSON") : Encoding.UTF8.GetBytes(_manifest.ToJsonString());
            string digest = Handler.Failure == "manifest-hash" ? new string('0', 64) : Digest(Handler.Manifest);
            var baseUri = new Uri("https://github.com/sidey-app/SIDEY/releases/download/windows-v" + Version + "/");
            return new WindowsUpdateManifest("production", Version, Version, "windows-v" + Version,
                new Uri(baseUri, _prefix + "-Setup.exe"), Digest(Setup),
                new WindowsIncrementalUpdate(new Uri(baseUri, _prefix + "-Update.json"), digest));
        }

        public void Dispose()
        {
            _client.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ArtifactHandler(byte[] payload, byte[] updater, byte[] setup) : HttpMessageHandler
    {
        public byte[] Manifest { get; set; } = [];
        public string? Failure { get; set; }
        public bool RejectNetwork { get; set; }
        public long? AdvertisedSetupSize { get; set; }
        public CancellationTokenSource? CancelRange { get; set; }
        public List<string> PayloadRanges { get; } = [];
        public int FullDownloads { get; private set; }
        public int UpdaterDownloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RejectNetwork)
                throw new InvalidOperationException("Prepared updates must not contact the network.");
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Head)
            {
                if (Failure == "timeout")
                    throw new TaskCanceledException("HTTP timeout without caller cancellation.");
                Assert.EndsWith("-Setup.exe", path, StringComparison.Ordinal);
                HttpResponseMessage response = Response([]);
                response.Content.Headers.ContentLength = AdvertisedSetupSize ?? setup.Length;
                return Task.FromResult(response);
            }
            if (path.EndsWith("-Update.json", StringComparison.Ordinal))
                return Task.FromResult(Response(Manifest));
            if (path.EndsWith("-Setup.exe", StringComparison.Ordinal))
            {
                FullDownloads++;
                return Task.FromResult(Response(setup));
            }
            if (path.EndsWith("-Update.exe", StringComparison.Ordinal))
            {
                UpdaterDownloads++;
                return Task.FromResult(Response(updater));
            }
            Assert.EndsWith("-Payload.bin", path, StringComparison.Ordinal);
            Assert.NotNull(request.Headers.Range);
            PayloadRanges.Add(request.Headers.Range.ToString());
            if (CancelRange is not null)
            {
                CancelRange.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
            if (Failure == "range-ignored")
                return Task.FromResult(Response(payload));
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range.Ranges);
            long from = range.From!.Value;
            long to = range.To!.Value;
            HttpResponseMessage partial = Response(payload[(int)from..((int)to + 1)], HttpStatusCode.PartialContent);
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                Failure == "range-header" ? from + 1 : from, to, payload.Length);
            return Task.FromResult(partial);
        }

        private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new ByteArrayContent(bytes) };
    }
}
