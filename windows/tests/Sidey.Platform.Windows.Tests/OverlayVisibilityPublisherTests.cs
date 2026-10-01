using Sidey.App.Services;

namespace Sidey.Platform.Windows.Tests;

public sealed class OverlayVisibilityPublisherTests
{
    [Fact]
    public async Task ResetCancelsInFlightMutationBeforeAReplacementCanRun()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var publisher = new OverlayVisibilityPublisher(async (suppressed, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                using CancellationTokenRegistration registration = cancellationToken.Register(firstCancelled.SetResult);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
                secondStarted.SetResult(suppressed);
        });

        publisher.Update(true);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        publisher.Reset();
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        publisher.Reconnect(false);
        Assert.False(await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
