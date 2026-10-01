using Sidey.Platform.Windows.Monitoring;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsFeedbackWindowMonitorTests
{
    [Theory]
    [InlineData(0x0218u, 0x12u, true)] // PBT_APMRESUMEAUTOMATIC
    [InlineData(0x0218u, 0x7u, true)] // PBT_APMRESUMESUSPEND
    [InlineData(0x02B1u, 0x8u, true)] // WTS_SESSION_UNLOCK
    [InlineData(0x02B1u, 0x7u, false)] // WTS_SESSION_LOCK
    [InlineData(0x0218u, 0x4u, false)] // PBT_APMSUSPEND
    public void OnlyResumeAndUnlockSignalsRequestConnectionRecovery(uint message, uint value, bool expected) =>
        Assert.Equal(expected, WindowsFeedbackWindowMonitor.IsResumeSignal(message, value));
}
