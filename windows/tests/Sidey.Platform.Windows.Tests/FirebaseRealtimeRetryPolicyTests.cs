using System.Net;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeRetryPolicyTests
{
    [Fact]
    public async Task ServerWaitLongerThanTimerRangeCanBeCancelledWithoutShorteningDeadline()
    {
        using CancellationTokenSource cancellation = new();
        Task waiting = FirebaseRealtimeRetryPolicy.DelayAsync(TimeSpan.FromDays(60), TimeProvider.System, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
    [Theory]
    [InlineData(1, 0, 4)]
    [InlineData(1, 0.5, 5)]
    [InlineData(2, 0.5, 10)]
    [InlineData(3, 1, 24)]
    [InlineData(17, 1, 300)]
    [InlineData(-1, -1, 4)]
    public void TransientLocalDelayGrowsWithBoundedJitter(int failures, double jitter, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), FirebaseRealtimeRetryPolicy.CalculateDelay(
            failures, HttpStatusCode.ServiceUnavailable, null, jitter));
    }

    [Fact]
    public void RateLimitHasThirtySecondMinimumWithOnlyUpwardJitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), FirebaseRealtimeRetryPolicy.CalculateDelay(
            1, HttpStatusCode.TooManyRequests, null, 0));
        Assert.Equal(TimeSpan.FromSeconds(36), FirebaseRealtimeRetryPolicy.CalculateDelay(
            1, HttpStatusCode.TooManyRequests, null, 1));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(11520)]
    public void LongerServerDeadlineOutranksLocalCap(int minutes)
    {
        TimeSpan required;
        required = TimeSpan.FromMinutes(minutes);
        Assert.Equal(required, FirebaseRealtimeRetryPolicy.CalculateDelay(17, null, required, 0));
        Assert.Equal(TimeSpan.FromSeconds(required.TotalSeconds * 1.2),
            FirebaseRealtimeRetryPolicy.CalculateDelay(17, null, required, 1));
    }
}
