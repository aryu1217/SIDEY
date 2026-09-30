using System.Text.Json;
using System.Xml.Linq;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsReleaseContractTests
{
    [Fact]
    public void CheckedInManifestDefinesTheWindowsProductionRelease()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(RepositoryPath("release", "windows.json")));
        JsonElement manifest = document.RootElement;

        Assert.Equal(1, manifest.GetProperty("schema").GetInt32());
        Assert.Equal("windows", manifest.GetProperty("platform").GetString());
        Assert.Equal("production", manifest.GetProperty("channel").GetString());
        string publicVersion = manifest.GetProperty("version").GetString()!;
        var versionProperties = XDocument.Load(RepositoryPath("windows", "Version.props"));
        string sourceVersion = versionProperties.Descendants("SideyProductVersion").Single().Value;
        Assert.Equal(publicVersion, sourceVersion);
        Assert.Equal(
            int.Parse(versionProperties.Descendants("SideyWindowsBuild").Single().Value),
            manifest.GetProperty("windowsBuild").GetInt32());
        Assert.Equal(publicVersion, manifest.GetProperty("updateVersion").GetString());
        Assert.Equal(publicVersion, manifest.GetProperty("releaseVersion").GetString());
        Assert.Equal(
            versionProperties.Descendants("SideyWindowsUpdateVersion").Single().Value,
            manifest.GetProperty("updateVersion").GetString());
        Assert.Equal(
            versionProperties.Descendants("SideyWindowsReleaseVersion").Single().Value,
            manifest.GetProperty("releaseVersion").GetString());
        Assert.Equal(
            versionProperties.Descendants("SideyMsixVersion").Single().Value,
            manifest.GetProperty("msixVersion").GetString());
        Assert.False(File.Exists(RepositoryPath("website", "windows-latest.json")));
        Assert.False(File.Exists(RepositoryPath("website", "windows", "update.json")));
    }

    [Fact]
    public void ReleaseAutomationUsesAPlatformScopedTag()
    {
        string validationWorkflow = Read(".github", "workflows", "windows-build-and-tests.yml");
        string releaseWorkflow = Read(".github", "workflows", "windows-release.yml");
        string releaseVerifier = Read("scripts", "windows", "tests", "Test-WindowsRelease.ps1");

        Assert.DoesNotContain("tags:", validationWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("tags:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("tag=windows-v$releaseVersion", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("$tag = \"windows-v$ReleaseVersion\"", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("--get windowsReleaseVersion", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowRequiresManualValidationBeforePublishing()
    {
        string validationWorkflow = Read(".github", "workflows", "windows-build-and-tests.yml");
        string releaseWorkflow = Read(".github", "workflows", "windows-release.yml");

        Assert.Contains("workflow_dispatch:", validationWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("  push:", validationWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("  pull_request:", validationWorkflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("confirm_version:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("$manifest.releaseVersion", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("$version = [string]$manifest.version", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("validate:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("--draft", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("gh release edit $tag --draft=false", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/website-deployment.yml", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SelfSigned", releaseWorkflow, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("windows-build-and-tests.yml")]
    [InlineData("windows-release.yml")]
    public void CoverageUploadOnlyRunsAfterTheTestStepStarts(string workflowName)
    {
        string workflow = Read(".github", "workflows", workflowName);

        Assert.Contains("id: tests", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "steps.tests.outcome == 'success' || steps.tests.outcome == 'failure'",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
    }

    private static string Read(params string[] pathSegments) =>
        File.ReadAllText(RepositoryPath(pathSegments));

    private static string RepositoryPath(params string[] pathSegments)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "windows", "src")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine([root!.FullName, .. pathSegments]);
    }
}
