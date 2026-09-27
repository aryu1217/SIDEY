using Sidey.Platform.Windows.Monitoring;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsMonitorServiceTests
{
    private static string FormatLabel(string key, object?[] args) => key switch
    {
        "settings.placement.monitor.display_name" => $"Monitor {args[0]}",
        "settings.placement.monitor.display_name_friendly" => $"Monitor {args[0]} · {args[1]}",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Fact]
    public void PhysicalMonitorNameKeepsLogicalDisplayNumber()
    {
        string first = WindowsMonitorService.FormatDisplayName(@"\\.\DISPLAY1", "LG ULTRAGEAR", 8, FormatLabel);
        string second = WindowsMonitorService.FormatDisplayName(@"\\.\DISPLAY2", "LG ULTRAGEAR", 8, FormatLabel);

        Assert.Equal("Monitor 1 · LG ULTRAGEAR", first);
        Assert.Equal("Monitor 2 · LG ULTRAGEAR", second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingPhysicalNameShowsMonitorNumberOnly(string? friendlyName)
    {
        string label = WindowsMonitorService.FormatDisplayName(@"\\.\DISPLAY3", friendlyName, 1, FormatLabel);

        Assert.Equal("Monitor 3", label);
    }

    [Fact]
    public void UnnumberedVirtualDisplayUsesEnumerationNumber()
    {
        string label = WindowsMonitorService.FormatDisplayName("monitor-2", null, 2, FormatLabel);

        Assert.Equal("Monitor 2", label);
    }

    [Fact]
    public void NativeFriendlyNameLookupHandlesCurrentDisplayTopology()
    {
        IReadOnlyDictionary<string, string> names = WindowsMonitorService.GetFriendlyNames();

        Assert.All(names, entry =>
        {
            Assert.StartsWith(@"\\.\DISPLAY", entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(entry.Value));
        });
    }
}
