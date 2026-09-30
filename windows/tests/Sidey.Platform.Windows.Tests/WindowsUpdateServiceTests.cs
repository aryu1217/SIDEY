using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsUpdateServiceTests
{
    [Fact]
    public void CurrentVersionMustComeFromTheApplicationArtifact()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new WindowsUpdateService());

        Assert.Equal("currentProductVersion", exception.ParamName);
    }

    [Theory]
    [InlineData("2.2640.1", "2.2640.1", "2.2640.1")]
    [InlineData("2.0.1", "2.0.1000", "2.0.1")]
    [InlineData("2.0.1", "2.0.1001", "2.0.1001")]
    public void CurrentReleaseIdentitySupportsHeadVerAndPreviouslyInstalledVersions(
        string productVersion,
        string updateVersion,
        string expectedReleaseVersion)
    {
        var service = new WindowsUpdateService(
            currentProductVersion: productVersion,
            currentUpdateVersion: updateVersion,
            processArchitecture: Architecture.X64);

        Assert.Equal(expectedReleaseVersion, service.EffectiveCurrentReleaseVersion);
    }

    [Fact]
    public async Task MissingManifestUsesAnActionableMessage()
    {
        using var client = new HttpClient(new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync());

        Assert.Equal("새 업데이트를 아직 사용할 수 없습니다. 잠시 후 다시 확인해 주세요.", exception.Message);
    }

    [Fact]
    public async Task CurrentWindowsManifestDoesNotOfferAnUpdate()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "1.0.6",
              "tag": "windows-v1.0.6",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v1.0.6/SIDEY-Windows-x64-v1.0.6-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
        };
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task HeadVerManifestOffersTheNextWindowsBuild()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.2640.2",
              "tag": "windows-v2.2640.2",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.2/SIDEY-Windows-x64-v2.2640.2-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.2640.1", "2.2640.1");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("2.2640.2", update.ProductVersion);
        Assert.Equal("2.2640.2", update.UpdateVersion);
        Assert.Equal("windows-v2.2640.2", update.UpdateTag);
        Assert.EndsWith("v2.2640.2-Setup.exe", update.InstallerUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadVerManifestRejectsDivergentProductAndUpdateVersions()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.2640.2",
              "tag": "windows-v2.2640.2",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.2/SIDEY-Windows-x64-v2.2640.2-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "product_version": "2.2640.2",
              "update_version": "2.2640.3",
              "update_tag": "windows-v2.2640.3",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.3/SIDEY-Windows-x64-v2.2640.3-Setup.exe",
              "update_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.2640.1", "2.2640.1");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task DownloadClosesTheHashStreamBeforePublishingTheInstaller()
    {
        byte[] installerBytes = Encoding.UTF8.GetBytes("SIDEY update regression fixture");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(installerBytes));
        string updateVersion = $"2.0.1000-file-handle-{Guid.NewGuid():N}";
        const string InstallerName = "SIDEY-Windows-x64-v2.0.1-Setup.exe";
        string updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "SIDEY",
            "Updates",
            updateVersion);
        string expectedPath = Path.Combine(
            updateDirectory,
            $"SIDEY-Windows-x64-v{updateVersion}-Setup.exe");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(installerBytes),
        };
        var handler = new StubHandler(response);
        using var client = new HttpClient(handler);
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");
        var manifest = new WindowsUpdateManifest(
            "production",
            "2.0.1",
            updateVersion,
            "windows-v2.0.1",
            new Uri($"https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/{InstallerName}"),
            sha256);
        var reportedPercentages = new List<int>();

        try
        {
            string actualPath = await service.DownloadInstallerAsync(
                manifest,
                progress: new InlineProgress<int>(reportedPercentages.Add));

            Assert.Equal(expectedPath, actualPath);
            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(actualPath));
            Assert.False(File.Exists($"{expectedPath}.download"));
            Assert.Equal(0, reportedPercentages.First());
            Assert.Equal(100, reportedPercentages.Last());
            Assert.True(await service.IsInstallerReadyAsync(manifest));
            Assert.Equal(expectedPath, await service.DownloadInstallerAsync(manifest));
            Assert.Equal(1, handler.RequestCount);

            await File.WriteAllTextAsync(expectedPath, "incomplete replacement");
            Assert.False(await service.IsInstallerReadyAsync(manifest));
        }
        finally
        {
            if (Directory.Exists(updateDirectory))
            {
                Directory.Delete(updateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NewerManifestRequiresTheVersionedInstallerAndSha256()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "1.0.11",
              "tag": "windows-v1.0.11",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v1.0.11/SIDEY-Windows-x64-v1.0.11-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
        };
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("1.0.11", update.ProductVersion);
        Assert.Equal("1.0.11", update.UpdateVersion);
        Assert.Equal("windows-v1.0.11", update.UpdateTag);
        Assert.Equal(
            "https://github.com/sidey-app/SIDEY/releases/download/windows-v1.0.11/" +
            "SIDEY-Windows-x64-v1.0.11-Setup.exe",
            update.InstallerUri.AbsoluteUri);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            update.Sha256);
    }

    [Fact]
    public async Task InjectedArtifactVersionCanExerciseAnAlreadyPublishedUpdate()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "1.0.10",
              "tag": "windows-v1.0.10",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v1.0.10/SIDEY-Windows-x64-v1.0.10-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
        };
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "1.0.9", "1.0.9");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.Equal("1.0.9", service.EffectiveCurrentProductVersion);
        Assert.Equal("1.0.9", service.EffectiveCurrentUpdateVersion);
        Assert.NotNull(update);
        Assert.Equal("1.0.10", update.ProductVersion);
        Assert.Equal("1.0.10", update.UpdateVersion);
    }

    [Fact]
    public async Task NewerManifestWithoutInstallerMetadataIsRejected()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "1.0.11",
              "tag": "windows-v1.0.11"
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
        };
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task NewerManifestRejectsTheFormerMsiContract()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "1.0.11",
              "tag": "windows-v1.0.11",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v1.0.11/SIDEY-Windows-x64-v1.0.11.msi",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
        };
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "1.0.10", "1.0.10");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task CompatibilityManifestUsesUpdateVersionButReturnsProductVersion()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "product_version": "2.0.1",
              "update_version": "2.0.1000",
              "update_tag": "windows-v2.0.1",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "update_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.0", "2.0.0");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.1", update.ProductVersion);
        Assert.Equal("2.0.1000", update.UpdateVersion);
        Assert.Equal("windows-v2.0.1", update.UpdateTag);
        Assert.EndsWith("v2.0.1-Setup.exe", update.InstallerUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsRevisionCanUpdateWithoutChangingDisplayedProductVersion()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "product_version": "2.0.1",
              "update_version": "2.0.1001",
              "update_tag": "windows-v2.0.1001",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1001/SIDEY-Windows-x64-v2.0.1001-Setup.exe",
              "update_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.1", "2.0.1000");

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.1", update.ProductVersion);
        Assert.Equal("2.0.1001", update.UpdateVersion);
        Assert.Equal("windows-v2.0.1001", update.UpdateTag);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            update.Sha256);
    }

    [Fact]
    public async Task PartialNewUpdateContractIsRejected()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "product_version": "2.0.1",
              "update_version": "2.0.1000"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.0", "2.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task NewContractProductVersionMustMatchTheLegacyBridgeVersion()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "product_version": "2.0.2",
              "update_version": "2.0.2000",
              "update_tag": "windows-v2.0.2",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.2/SIDEY-Windows-x64-v2.0.2-Setup.exe",
              "update_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.0", "2.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task NewContractTagMustMatchTheDerivedReleaseVersion()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "product_version": "2.0.1",
              "update_version": "2.0.1001",
              "update_tag": "windows-v2.0.1",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "update_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.1", "2.0.1000");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task LegacyContractRejectsAnUpdateVersionOutsideTheEncodedRange()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.0.1",
              "tag": "windows-v2.0.1",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "product_version": "2.0.1",
              "update_version": "2.0.100",
              "update_tag": "windows-v2.0.100",
              "update_installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.100/SIDEY-Windows-x64-v2.0.100-Setup.exe",
              "update_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
            """;
        using HttpResponseMessage response = JsonResponse(Manifest);
        using var client = new HttpClient(new StubHandler(response));
        WindowsUpdateService service = CreateService(client, "2.0.0", "2.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Theory]
    [InlineData(Architecture.X64, "x64", "windows-latest.json")]
    [InlineData(Architecture.Arm64, "arm64", "windows-arm64-latest.json")]
    public async Task ProcessArchitectureSelectsMatchingFeedAndInstaller(
        Architecture architecture, string assetArchitecture, string feed)
    {
        string manifest = $$"""
            {
              "channel": "production",
              "version": "2.2640.2",
              "tag": "windows-v2.2640.2",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.2/SIDEY-Windows-{{assetArchitecture}}-v2.2640.2-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var handler = new StubHandler(JsonResponse(manifest));
        using var client = new HttpClient(handler);
        var service = new WindowsUpdateService(client, "2.2640.1", "2.2640.1",
            processArchitecture: architecture);

        WindowsUpdateManifest? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal(new Uri("https://sidey-app.github.io/SIDEY/" + feed), handler.RequestUri);
        Assert.Contains("SIDEY-Windows-" + assetArchitecture + "-", update.InstallerUri.AbsoluteUri);
        Assert.EndsWith($"SIDEY-Windows-{assetArchitecture}-v2.2640.2-Setup.exe",
            service.InstallerPath(update.UpdateVersion), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arm64FeedRejectsX64InstallerInsteadOfChangingArchitecture()
    {
        const string Manifest = """
            {
              "channel": "production",
              "version": "2.2640.2",
              "tag": "windows-v2.2640.2",
              "installer_url": "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.2/SIDEY-Windows-x64-v2.2640.2-Setup.exe",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
            """;
        using var handler = new StubHandler(JsonResponse(Manifest));
        using var client = new HttpClient(handler);
        var service = new WindowsUpdateService(client, "2.2640.1", "2.2640.1",
            processArchitecture: Architecture.Arm64);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(WindowsUpdateService.Arm64ManifestUri, handler.RequestUri);
    }

    [Fact]
    public async Task MissingArm64FeedNeverFallsBackToX64Feed()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var service = new WindowsUpdateService(client, "2.2640.1", "2.2640.1",
            processArchitecture: Architecture.Arm64);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync());
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(WindowsUpdateService.Arm64ManifestUri, handler.RequestUri);
    }

    [Fact]
    public async Task Arm64DownloadRejectsExternallySuppliedX64ManifestBeforeNetworkAccess()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var service = new WindowsUpdateService(client, "2.2640.1", "2.2640.1",
            processArchitecture: Architecture.Arm64);
        var update = new WindowsUpdateManifest("production", "2.2640.2", "2.2640.2", "windows-v2.2640.2",
            new Uri("https://github.com/sidey-app/SIDEY/releases/download/windows-v2.2640.2/SIDEY-Windows-x64-v2.2640.2-Setup.exe"),
            new string('a', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(update));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.IsInstallerReadyAsync(update));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void UnsupportedProcessArchitectureCannotSelectAnUpdate(Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(() => new WindowsUpdateService(
            currentProductVersion: "2.2640.1", currentUpdateVersion: "2.2640.1",
            processArchitecture: architecture));
    }

    private static WindowsUpdateService CreateService(
        HttpClient client,
        string productVersion,
        string updateVersion) => new(
            client,
            currentProductVersion: productVersion,
            currentUpdateVersion: updateVersion,
            processArchitecture: Architecture.X64);

    private static HttpResponseMessage JsonResponse(string manifest) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(manifest, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            _ = cancellationToken;
            RequestCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
