using Sidey.Core.Domain;
using Sidey.Infrastructure.Backend;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class MessageRecoveryQueueTests
{
    [Fact]
    public async Task FailedIdentityReadRecoversRoomMessagesAndCoalescesDirtyMarks()
    {
        var roomId = Guid.NewGuid();
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        var published = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        TaskCompletionSource finalPublish = Signal();
        ChatMessage message = new(Guid.NewGuid(), roomId, Guid.NewGuid(), "recovered", DateTimeOffset.UnixEpoch);
        await using var queue = new MessageRecoveryQueue(async (_, token) =>
        {
            Interlocked.Increment(ref reads);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return [message];
        }, (_, epoch, messages, _) =>
        {
            Assert.Equal(7, epoch);
            published.TrySetResult(messages);
            if (Volatile.Read(ref reads) == 2)
                finalPublish.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [roomId] = 7 });
        queue.Mark(roomId, new HttpRequestException());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int index = 0; index < 20; index++)
            queue.Mark(roomId, new HttpRequestException());
        release.TrySetResult();
        Assert.Equal(message, Assert.Single(await published.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        // Marks during the first read require one subsequent reconciliation, not twenty requests.
        await finalPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task DirtyMarkDuringPublishTriggersAnotherRead()
    {
        var roomId = Guid.NewGuid();
        TaskCompletionSource publishing = Signal();
        TaskCompletionSource release = Signal();
        TaskCompletionSource republished = Signal();
        int reads = 0;
        await using var queue = new MessageRecoveryQueue((_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
        }, async (_, _, _, token) =>
        {
            if (Volatile.Read(ref reads) == 1)
            {
                publishing.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else
                republished.TrySetResult();
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [roomId] = 1 });
        queue.Mark(roomId, new HttpRequestException());
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Mark(roomId, new IOException());
        release.TrySetResult();
        await republished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaveOrEpochChangeRejectsLateNonCooperativeRead(bool rejoined)
    {
        var roomId = Guid.NewGuid();
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        int publishes = 0;
        await using var queue = new MessageRecoveryQueue(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return [];
        }, (_, _, _, _) =>
        {
            Interlocked.Increment(ref publishes);
            return Task.CompletedTask;
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [roomId] = 1 });
        queue.Mark(roomId, new IOException());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.UpdateRooms(rejoined ? new Dictionary<Guid, long> { [roomId] = 2 } : []);
        release.TrySetResult();
        await queue.DisposeAsync();
        Assert.Equal(0, publishes);
    }

    [Fact]
    public async Task ReadsAcrossRoomsAreSingleFlightAndPermanentFailureStopsRetry()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        TaskCompletionSource secondStarted = Signal();
        int active = 0;
        int maximum = 0;
        await using var queue = new MessageRecoveryQueue(async (room, token) =>
        {
            int current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            try
            {
                if (room == first)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                    throw new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
                }
                secondStarted.TrySetResult();
                return [];
            }
            finally { Interlocked.Decrement(ref active); }
        }, (_, _, _, _) => Task.CompletedTask, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [first] = 1, [second] = 1 });
        queue.Mark(first, new IOException());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Mark(second, new IOException());
        Assert.False(secondStarted.Task.IsCompleted);
        release.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task TransientFailureRetainsDirtyRoomUntilSuccessfulRead()
    {
        var room = Guid.NewGuid();
        int reads = 0;
        TaskCompletionSource recovered = Signal();
        await using var queue = new MessageRecoveryQueue((_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
                throw new HttpRequestException("unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
            return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
        }, (_, _, _, _) =>
        {
            recovered.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [room] = 1 });
        queue.Mark(room, new IOException());
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task TransientPublishFailureRefetchesBeforePublishing()
    {
        var room = Guid.NewGuid();
        int reads = 0;
        int publishes = 0;
        TaskCompletionSource recovered = Signal();
        await using var queue = new MessageRecoveryQueue((_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
        }, (_, _, _, _) =>
        {
            if (Interlocked.Increment(ref publishes) == 1)
                throw new HttpRequestException("A newer message arrived during recovery.");
            recovered.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(new Dictionary<Guid, long> { [room] = 1 });
        queue.Mark(room, new IOException());
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
        Assert.Equal(2, publishes);
    }

    [Fact]
    public async Task PendingMetadataIsBoundedToFiveRooms()
    {
        Guid[] rooms = [.. Enumerable.Range(0, 6).Select(_ => Guid.NewGuid())];
        TaskCompletionSource firstStarted = Signal();
        TaskCompletionSource release = Signal();
        TaskCompletionSource completed = Signal();
        var fetched = new List<Guid>();
        int published = 0;
        await using var queue = new MessageRecoveryQueue(async (room, token) =>
        {
            fetched.Add(room);
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return [];
        }, (_, _, _, _) =>
        {
            if (Interlocked.Increment(ref published) == 5)
                completed.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None, Immediate);
        queue.UpdateRooms(rooms.ToDictionary(room => room, _ => 1L));
        queue.Mark(rooms[0], new IOException());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Guid room in rooms.Skip(1))
            queue.Mark(room, new IOException());
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.DisposeAsync();
        Assert.Equal(5, fetched.Count);
        Assert.DoesNotContain(rooms[5], fetched);
    }

    private static TimeSpan Immediate(int _, Exception __) => TimeSpan.Zero;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
