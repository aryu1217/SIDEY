using System.Net;
using System.Text;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeListenerTests
{
    private static readonly Guid s_userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid s_sessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid s_roomId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task InitialSnapshotsAreBaselinesAndHigherChatHintInvalidatesOnce()
    {
        var events = new List<BackendEvent>();
        object eventGate = new();
        var credentials = new FakeCredentialProvider();
        await using var listener = new FirebaseRealtimeListener(
            credentials,
            backendEvent =>
            {
                lock (eventGate)
                {
                    events.Add(backendEvent);
                }
            },
            _ => CreateClient());

        await listener.StartAsync(s_roomId, CancellationToken.None);
        await WaitUntilAsync(() => listener.IsReady);
        await WaitUntilAsync(() =>
        {
            lock (eventGate)
            {
                return events.OfType<BackendEvent.MessagesInvalidated>().Any();
            }
        });

        BackendEvent.MessagesInvalidated[] invalidations;
        lock (eventGate)
        {
            invalidations = [.. events.OfType<BackendEvent.MessagesInvalidated>()];
        }
        Assert.Single(invalidations);
        Assert.Equal(s_roomId, invalidations[0].RoomId);
        Assert.DoesNotContain(events, item => item is BackendEvent.TechnicalError);
    }

    [Fact]
    public async Task StopCancelsBothStreamsAndClearsReadiness()
    {
        var credentials = new FakeCredentialProvider();
        await using var listener = new FirebaseRealtimeListener(
            credentials,
            _ => { },
            _ => CreateClient());

        await listener.StartAsync(s_roomId, CancellationToken.None);
        await WaitUntilAsync(() => listener.IsReady);

        await listener.StopAsync(CancellationToken.None);

        Assert.False(listener.IsReady);
        Assert.Equal(1, credentials.RequestCount);
    }

    [Fact]
    public async Task ReconnectsWhenEitherActiveStreamEnds()
    {
        var credentials = new FakeCredentialProvider();
        var roomEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new FirebaseRealtimeListener(
            credentials,
            _ => { },
            _ => CreateClient(roomEnd.Task));

        await listener.StartAsync(s_roomId, CancellationToken.None);
        await WaitUntilAsync(() => listener.IsReady);

        roomEnd.TrySetResult();

        await WaitUntilAsync(() => credentials.RequestCount >= 2);
    }

    [Fact]
    public async Task FreshFirebasePulseAndThrowSnapshotsEmitPeerActions()
    {
        var actorUserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var targetUserId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<BackendEvent>();
        object eventGate = new();
        await using var listener = new FirebaseRealtimeListener(
            new FakeCredentialProvider(),
            backendEvent =>
            {
                lock (eventGate)
                {
                    events.Add(backendEvent);
                }
            },
            _ => CreateClient(
                Task.Delay(Timeout.InfiniteTimeSpan),
                RoomTransientEvents(actorUserId, targetUserId, now)));
        listener.ConfigureThrowableWireCodes(new Dictionary<string, string>
        {
            ["18"] = "throwable_leaf",
        });

        await listener.StartAsync(s_roomId, CancellationToken.None);
        await WaitUntilAsync(() =>
        {
            lock (eventGate)
            {
                return events.OfType<BackendEvent.CharacterPulsed>().Any()
                    && events.OfType<BackendEvent.CharacterThrown>().Any();
            }
        });

        lock (eventGate)
        {
            Assert.Equal(actorUserId, Assert.Single(
                events.OfType<BackendEvent.CharacterPulsed>()).Pulse.UserId);
            CharacterThrowEvent characterThrow = Assert.Single(
                events.OfType<BackendEvent.CharacterThrown>()).Throw;
            Assert.Equal(actorUserId, characterThrow.ActorUserId);
            Assert.Equal(targetUserId, characterThrow.TargetUserId);
            Assert.Equal("throwable_leaf", characterThrow.ThrowableId);
        }
    }

    private static FirebaseRtdbRestClient CreateClient()
        => CreateClient(Task.Delay(Timeout.InfiniteTimeSpan));

    private static FirebaseRtdbRestClient CreateClient(Task roomEnd) =>
        CreateClient(roomEnd, RoomEvents());

    private static FirebaseRtdbRestClient CreateClient(Task roomEnd, string roomEvents)
    {
        var urls = new FirebaseRtdbUrlBuilder(
            new Uri("https://sidey.asia-southeast1.firebasedatabase.app"));
        return FirebaseRtdbRestClient.CreateForTesting(urls, (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string content = path.Contains("/v2/n/", StringComparison.Ordinal)
                ? InboxEvents()
                : roomEvents;
            Stream stream = path.Contains("/v2/n/", StringComparison.Ordinal)
                ? new PrefixThenWaitStream(Encoding.UTF8.GetBytes(content))
                : new PrefixThenSignalStream(Encoding.UTF8.GetBytes(content), roomEnd);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            return Task.FromResult(response);
        });
    }

    private static string InboxEvents()
    {
        string roomId = s_roomId.ToString("D");
        string initial = JsonSerializer.Serialize(new
        {
            path = "/",
            data = new
            {
                a = "00000000000000000001",
                r = new Dictionary<string, object>
                {
                    [roomId] = new { v = "00000000000000000001", n = 1 },
                },
            },
        });
        string update = JsonSerializer.Serialize(new
        {
            path = $"/r/{roomId}",
            data = new { n = 2 },
        });
        return $"event: put\ndata: {initial}\n\nevent: patch\ndata: {update}\n\n";
    }

    private static string RoomEvents()
    {
        string messageId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd").ToString("D");
        string initial = JsonSerializer.Serialize(new
        {
            path = "/",
            data = new
            {
                e = new
                {
                    i = messageId,
                    s = s_userId,
                    b = "hello",
                    t = 1_750_000_000_000,
                    n = 1,
                },
            },
        });
        return $"event: put\ndata: {initial}\n\n";
    }

    private static string RoomTransientEvents(Guid actorUserId, Guid targetUserId, long now)
    {
        long baselineTimestamp = now - 1_000;
        string actor = actorUserId.ToString("D");
        string initial = JsonSerializer.Serialize(new
        {
            path = "/",
            data = new
            {
                c = new Dictionary<string, long> { [actor] = baselineTimestamp },
                x = new Dictionary<string, object>
                {
                    [actor] = new { u = targetUserId, k = "18", t = baselineTimestamp },
                },
            },
        });
        string pulse = JsonSerializer.Serialize(new
        {
            path = $"/c/{actor}",
            data = now,
        });
        string characterThrow = JsonSerializer.Serialize(new
        {
            path = $"/x/{actor}",
            data = new { u = targetUserId, k = "18", t = now },
        });
        return $"event: put\ndata: {initial}\n\n"
            + $"event: put\ndata: {pulse}\n\n"
            + $"event: put\ndata: {characterThrow}\n\n";
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeCredentialProvider : IFirebaseRealtimeCredentialProvider
    {
        public int RequestCount { get; private set; }

        public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return ValueTask.FromResult(new FirebaseRealtimeCredential(
                s_userId,
                s_sessionId,
                "id-token",
                new Uri("https://sidey.asia-southeast1.firebasedatabase.app"),
                generation: 1));
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class PrefixThenWaitStream(byte[] prefix) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixThenSignalStream(byte[] prefix, Task end) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await end.WaitAsync(cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
