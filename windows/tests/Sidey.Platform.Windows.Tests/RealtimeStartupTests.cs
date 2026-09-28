using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Realtime;
using Sidey.Infrastructure;

namespace Sidey.Platform.Windows.Tests;

public sealed class RealtimeStartupTests
{
    private static readonly Guid s_userId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task BothSubscriptionsAreSentBeforeEitherReplyAndOnlyAcknowledgedTopicsConnect()
    {
        var bothReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            Assert.Equal("phx_join", first.GetProperty("event").GetString());
            Assert.Equal("phx_join", second.GetProperty("event").GetString());
            Assert.NotEqual(first.GetProperty("topic").GetString(), second.GetProperty("topic").GetString());

            JsonElement[] joins = [first, second];
            JsonElement ephemeral = Assert.Single(
                joins,
                join => join.GetProperty("topic").GetString()!.EndsWith(":ephemeral", StringComparison.Ordinal));
            JsonElement database = Assert.Single(
                joins,
                join => join.GetProperty("topic").GetString()!.EndsWith(":db", StringComparison.Ordinal));
            JsonElement ephemeralConfig = ephemeral.GetProperty("payload").GetProperty("config");
            Assert.True(ephemeralConfig.GetProperty("presence").GetProperty("enabled").GetBoolean());
            Assert.Equal(
                s_userId.ToString("D"),
                ephemeralConfig.GetProperty("presence").GetProperty("key").GetString());
            Assert.False(database.GetProperty("payload").GetProperty("config").TryGetProperty(
                "presence",
                out JsonElement _));
            Assert.All(
                joins,
                join => Assert.Equal(
                    "test-token",
                    join.GetProperty("payload").GetProperty("access_token").GetString()));
            bothReceived.SetResult();
            await releaseReplies.Task.WaitAsync(token);
            await ReplyAsync(socket, second, token); // Out-of-order acknowledgements are valid.
            await ReplyAsync(socket, first, token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var roomId = Guid.NewGuid();
        Task synchronization = transport.SynchronizeAsync(new Dictionary<Guid, long> { [roomId] = 1 },
            roomId, PresenceState.Online, server.Token);
        await bothReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(synchronization.IsCompleted);
        Assert.False(transport.ConnectionStatus.ActiveRoomTransportConnected);
        releaseReplies.SetResult();
        await synchronization.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
        Assert.Equal("?apikey=test-key&vsn=1.0.0", server.LastConnectUri?.Query);
    }

    [Fact]
    public async Task RefreshedSessionTokenIsAppliedToEveryJoinedChannel()
    {
        var authorizationUpdated = new TaskCompletionSource<string[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            var updatedTopics = new HashSet<string>(StringComparer.Ordinal);
            while (updatedTopics.Count < joins.Length)
            {
                JsonElement message = await ReadAsync(socket, token);
                if (message.GetProperty("event").GetString() != "access_token")
                {
                    continue;
                }

                Assert.Equal(
                    "refreshed-token",
                    message.GetProperty("payload").GetProperty("access_token").GetString());
                updatedTopics.Add(message.GetProperty("topic").GetString()!);
            }

            authorizationUpdated.SetResult([.. updatedTopics.Order(StringComparer.Ordinal)]);
        });
        var sessions = new RotatingSessions("initial-token");
        await using SupabaseRealtimeTransport transport = CreateTransport(
            server,
            sessions: sessions,
            watchdogInterval: TimeSpan.FromMilliseconds(20),
            authorizationRefreshInterval: TimeSpan.FromMilliseconds(20));
        var roomId = Guid.NewGuid();

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);
        sessions.SetAccessToken("refreshed-token");
        string[] topics = await authorizationUpdated.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, topics.Length);
        Assert.Contains(topics, topic => topic.EndsWith(":db", StringComparison.Ordinal));
        Assert.Contains(topics, topic => topic.EndsWith(":ephemeral", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChannelAuthorizationFailureDisconnectsAndRecoversTheWholeSubscriptionSet()
    {
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            if (connection != 1)
            {
                return;
            }

            var updatedTopics = new HashSet<string>(StringComparer.Ordinal);
            while (updatedTopics.Count < joins.Length)
            {
                JsonElement message = await ReadAsync(socket, token);
                if (message.GetProperty("event").GetString() == "access_token")
                {
                    updatedTopics.Add(message.GetProperty("topic").GetString()!);
                }
            }

            string databaseTopic = Assert.Single(
                updatedTopics,
                topic => topic.EndsWith(":db", StringComparison.Ordinal));
            await SendEventAsync(
                socket,
                databaseTopic,
                "system",
                new { status = "error", message = "Unauthorized: denied" },
                token);
        });
        var sessions = new RotatingSessions("initial-token");
        await using SupabaseRealtimeTransport transport = CreateTransport(
            server,
            sessions: sessions,
            watchdogInterval: TimeSpan.FromMilliseconds(20),
            authorizationRefreshInterval: TimeSpan.FromMilliseconds(20));
        var roomId = Guid.NewGuid();
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);

        sessions.SetAccessToken("refreshed-token");
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.TransportConnected: false });
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });

        Assert.Equal(2, server.ConnectionCount);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
    }

    [Theory]
    [InlineData("Unauthorized", "authorization")]
    [InlineData("ClientPresenceRateLimitReached", "capacity")]
    public async Task ChannelSystemFailureReportsOnlySafeCategoryAndRecoversTheWholeSubscriptionSet(
        string errorCode,
        string expectedCategory)
    {
        var sendFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            if (connection != 1)
            {
                return;
            }

            await sendFailure.Task.WaitAsync(token);
            string databaseTopic = Assert.Single(
                joins.Select(join => join.GetProperty("topic").GetString()!),
                topic => topic.EndsWith(":db", StringComparison.Ordinal));
            await SendEventAsync(
                socket,
                databaseTopic,
                "system",
                new { status = "error", message = $"{errorCode}: private-server-detail" },
                token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var roomId = Guid.NewGuid();
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);

        sendFailure.SetResult();
        BackendEvent.Diagnostic termination = await WaitForEventAsync<BackendEvent.Diagnostic>(
            transport,
            item => item.Stage.StartsWith("realtime-channel-terminated", StringComparison.Ordinal));
        Assert.Equal(
            $"realtime-channel-terminated kind=database event=system failure={expectedCategory}",
            termination.Stage);
        Assert.DoesNotContain("private-server-detail", termination.Stage);
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.TransportConnected: false });
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });

        Assert.Equal(2, server.ConnectionCount);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
    }

    [Fact]
    public async Task IdenticalPresenceIsSuppressedButStateChangesAndNewSubscriptionsPublish()
    {
        var initialStates = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedState = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            var states = new List<string>();
            while (true)
            {
                JsonElement message = await ReadAsync(socket, token);
                if (message.GetProperty("event").GetString() != "presence")
                {
                    continue;
                }

                string state = message.GetProperty("payload").GetProperty("payload").GetProperty("state").GetString()!;
                if (connection != 1)
                {
                    reconnectedState.TrySetResult(state);
                    return;
                }

                states.Add(state);
                if (state == "away")
                {
                    initialStates.TrySetResult([.. states]);
                    return;
                }
            }
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var roomId = Guid.NewGuid();
        var rooms = new Dictionary<Guid, long> { [roomId] = 1 };
        for (int index = 0; index < 10; index++)
        {
            await transport.SynchronizeAsync(rooms, roomId, PresenceState.Online, server.Token);
        }
        await transport.PublishPresenceAsync(roomId, PresenceState.Away, server.Token);

        Assert.Equal(new[] { "online", "away" }, await initialStates.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        transport.RequestReconnect(userInitiated: true);
        Assert.Equal("away", await reconnectedState.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task TransientSessionRefreshFailureKeepsTheHealthySocketConnected()
    {
        var sessions = new OneTimeFailingSessions(
            new HttpRequestException("Injected transient session refresh failure."));
        var heartbeatAfterFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            while (!heartbeatAfterFailure.Task.IsCompleted)
            {
                JsonElement message = await ReadAsync(socket, token);
                if (message.GetProperty("event").GetString() == "heartbeat"
                    && sessions.FailureObserved.IsCompleted)
                {
                    heartbeatAfterFailure.TrySetResult();
                }
            }
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(
            server,
            sessions: sessions,
            watchdogInterval: TimeSpan.FromMilliseconds(20),
            authorizationRefreshInterval: TimeSpan.FromMilliseconds(20));
        var roomId = Guid.NewGuid();
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);

        sessions.FailNextRequest();
        await sessions.FailureObserved.WaitAsync(TimeSpan.FromSeconds(3));
        await heartbeatAfterFailure.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, server.ConnectionCount);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
    }

    [Fact]
    public async Task UnexpectedSessionRefreshFailureRecoversTheWholeSubscriptionSet()
    {
        var sessions = new OneTimeFailingSessions(
            new InvalidDataException("Injected malformed session response."));
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(
            server,
            sessions: sessions,
            watchdogInterval: TimeSpan.FromMilliseconds(20),
            authorizationRefreshInterval: TimeSpan.FromMilliseconds(20));
        var roomId = Guid.NewGuid();
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);

        sessions.FailNextRequest();
        await sessions.FailureObserved.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.TransportConnected: false });
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });

        Assert.Equal(2, server.ConnectionCount);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
    }

    [Fact]
    public async Task DatabaseBroadcastEmitsMessageIdentityWithoutTrustingMessageBody()
    {
        var roomId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var messageId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            JsonElement database = Assert.Single(
                joins,
                join => join.GetProperty("topic").GetString()!.EndsWith(":db", StringComparison.Ordinal));
            await SendEventAsync(
                socket,
                database.GetProperty("topic").GetString()!,
                "broadcast",
                new
                {
                    @event = "message_changed",
                    payload = new
                    {
                        room_id = roomId,
                        message_id = messageId,
                        operation = "INSERT",
                        body = "This body must never become a ChatMessage.",
                    },
                },
                token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);
        BackendEvent.MessageChanged changed = await WaitForEventAsync<BackendEvent.MessageChanged>(transport);

        Assert.Equal(roomId, changed.RoomId);
        Assert.Equal(messageId, changed.MessageId);
        Assert.Equal("INSERT", changed.Operation);
    }

    [Fact]
    public async Task PresenceSnapshotPublishesRemoteStateAndMarksMissingUsersOffline()
    {
        var roomId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var friendId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var firstSnapshotSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendEmptySnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement join in joins)
            {
                await ReplyAsync(socket, join, token);
            }

            JsonElement ephemeral = Assert.Single(
                joins,
                join => join.GetProperty("topic").GetString()!.EndsWith(":ephemeral", StringComparison.Ordinal));
            string topic = ephemeral.GetProperty("topic").GetString()!;
            await SendEventAsync(
                socket,
                topic,
                "presence_state",
                new Dictionary<string, object>
                {
                    [friendId.ToString("D")] = new { metas = new[] { new { state = "Away" } } },
                },
                token);
            firstSnapshotSent.SetResult();
            await sendEmptySnapshot.Task.WaitAsync(token);
            await SendEventAsync(socket, topic, "presence_state", new { }, token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [roomId] = 1 },
            roomId,
            PresenceState.Online,
            server.Token);
        await firstSnapshotSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        BackendEvent.PresenceChanged away = await WaitForEventAsync<BackendEvent.PresenceChanged>(
            transport,
            item => item.UserId == friendId && item.State == PresenceState.Away);
        sendEmptySnapshot.SetResult();
        BackendEvent.PresenceChanged offline = await WaitForEventAsync<BackendEvent.PresenceChanged>(
            transport,
            item => item.UserId == friendId && item.State == PresenceState.Offline);

        Assert.Equal(roomId, away.RoomId);
        Assert.Equal(roomId, offline.RoomId);
    }

    [Fact]
    public async Task LostInitialSocketReleasesPendingJoinsAndRecoversWithoutEightSecondDelay()
    {
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            if (connection == 1)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "retry", token);
                dropped.SetResult();
                return;
            }
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var roomId = Guid.NewGuid();
        Task synchronization = transport.SynchronizeAsync(new Dictionary<Guid, long> { [roomId] = 1 },
            roomId, PresenceState.Online, server.Token);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await synchronization.WaitAsync(TimeSpan.FromSeconds(2)); // Must not wait for the five-second join timeout.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (BackendEvent item in transport.ReadEventsAsync(deadline.Token))
        {
            if (item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true })
                break;
        }
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Theory]
    [InlineData(1, 350)]
    [InlineData(2, 8000)]
    [InlineData(3, 16000)]
    [InlineData(4, 30000)]
    [InlineData(5, 60000)]
    [InlineData(6, 120000)]
    [InlineData(7, 240000)]
    [InlineData(10, 300000)]
    [InlineData(int.MaxValue, 300000)]
    public void RepeatedFailuresStillBackOff(int attempt, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), RealtimeRecoveryPolicy.ConnectionDelayForAttempt(attempt, 0.5));

    [Theory]
    [InlineData(2, 6400, 9600)]
    [InlineData(7, 192000, 288000)]
    [InlineData(8, 240000, 300000)]
    public void JitterSpreadsRetriesWithoutExceedingFiveMinutes(int attempt, int min, int max)
    {
        Assert.Equal(min, RealtimeRecoveryPolicy.ConnectionDelayForAttempt(attempt, 0).TotalMilliseconds);
        Assert.Equal(max, RealtimeRecoveryPolicy.ConnectionDelayForAttempt(attempt, 1).TotalMilliseconds);
    }

    [Fact]
    public async Task LosingInternetDisconnectsImmediatelyAndPausesReconnectUntilItReturns()
    {
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        var network = new Network();
        await using SupabaseRealtimeTransport transport = CreateTransport(server, network);
        var roomId = Guid.NewGuid();
        var rooms = new Dictionary<Guid, long> { [roomId] = 1 };
        await transport.SynchronizeAsync(rooms, roomId, PresenceState.Online, server.Token);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);

        var reconciliationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> reconciliation = transport.RunWhileConnectedAsync(async token =>
        {
            reconciliationStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, server.Token);
        await reconciliationStarted.Task;

        network.SetAvailable(false);
        Assert.Equal(RealtimeConnectionStatus.Disconnected, transport.ConnectionStatus);
        Assert.False(await reconciliation.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await transport.RunWhileConnectedAsync(_ => throw new InvalidOperationException("No offline query is allowed."), server.Token));
        await transport.SynchronizeAsync(rooms, roomId, PresenceState.Online, server.Token);
        await Task.Delay(700, server.Token); // Past the first recovery delay; no socket may be opened.
        Assert.Equal(1, server.ConnectionCount);

        network.SetAvailable(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (BackendEvent item in transport.ReadEventsAsync(deadline.Token))
        {
            if (item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true }
                && transport.ConnectionStatus.ActiveRoomTransportConnected)
                break;
        }
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryHintSkipsBackoffAndCoalescesRepeatedNotifications(bool manual)
    {
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            if (connection <= 2)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "retry", token);
                return;
            }
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        var network = new Network();
        await using SupabaseRealtimeTransport transport = CreateTransport(server, network);
        var room = Guid.NewGuid();
        await transport.SynchronizeAsync(new Dictionary<Guid, long> { [room] = 1 }, room, PresenceState.Online, server.Token);
        await WaitForEventAsync(transport, item => item is BackendEvent.Diagnostic diagnostic
            && diagnostic.Stage.StartsWith("realtime-reconnect-scheduled attempt=2 ", StringComparison.Ordinal));
        for (int index = 0; index < 20; index++)
        {
            if (manual)
                transport.RequestReconnect(userInitiated: true);
            else
                network.ChangePath();
        }
        await WaitForEventAsync(transport, item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });
        Assert.Equal(3, server.ConnectionCount);
        // Changes to Windows' connectivity assessment must not restart a healthy socket.
        network.ChangePath();
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
        await Task.Delay(500, server.Token);
        Assert.Equal(3, server.ConnectionCount);
    }

    [Fact]
    public async Task PathLossCancelsInitialHandshakeBeforeAnySocketHasBeenInstalled()
    {
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new Network();
        await using var transport = new SupabaseRealtimeTransport(
            new SupabaseRuntimeConfiguration(new Uri("https://unused.invalid"), "test-key"),
            new Sessions(), network, async (_, token) =>
            {
                connecting.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The offline handshake must be canceled.");
            });
        var room = Guid.NewGuid();
        Task synchronization = transport.SynchronizeAsync(new Dictionary<Guid, long> { [room] = 1 },
            room, PresenceState.Online, CancellationToken.None);
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        network.SetAvailable(false);
        await synchronization.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(transport.ConnectionStatus.TransportConnected);
    }

    [Fact]
    public async Task InitialHandshakeFailureReturnsAndRecoversInBackground()
    {
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        int attempts = 0;
        await using var transport = new SupabaseRealtimeTransport(
            new SupabaseRuntimeConfiguration(new Uri("https://unused.invalid"), "test-key"),
            new Sessions(), new Network(), (uri, token) =>
                ++attempts == 1 ? Task.FromException<ClientWebSocket>(new WebSocketException("Injected connect failure"))
                    : server.ConnectAsync(uri, token));
        var room = Guid.NewGuid();
        await transport.SynchronizeAsync(new Dictionary<Guid, long> { [room] = 1 }, room, PresenceState.Online, server.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(transport.ConnectionStatus.TransportConnected);
        await WaitForEventAsync(transport, item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("Unauthorized: denied", "Authorization")]
    [InlineData("InvalidJWTExpiration: token expired", "Authorization")]
    [InlineData("InvalidJWTToken: expired", "Authorization")]
    [InlineData("JwtSignatureError: invalid", "Authorization")]
    [InlineData("You do not have permissions to read from this Topic", "Authorization")]
    [InlineData("Token has expired", "Authorization")]
    [InlineData("ClientJoinRateLimitReached: too many joins", "Capacity")]
    [InlineData("ConnectionRateLimitReached: too many connections", "Capacity")]
    [InlineData("DatabaseLackOfConnections: unavailable", "Capacity")]
    [InlineData("RealtimeDisabledForTenant: quota reached", "Configuration")]
    [InlineData("Too many messages per second", "Capacity")]
    [InlineData("Client presence rate limit exceeded", "Capacity")]
    [InlineData("TopicNameRequired: missing", "Configuration")]
    [InlineData("RealtimeRestarting: please standby", "Transient")]
    [InlineData("Unknown Error on Channel", "Transient")]
    public void ServerRejectionsAreClassifiedWithoutRetainingRawReason(
        string reason,
        string expected)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { response = new { reason } }));
        var exception = RealtimeSubscriptionException.FromServerPayload(payload.RootElement);
        Assert.Equal(expected, exception.FailureKind.ToString());
        Assert.Equal(expected == "Authorization", exception.AuthorizationFailure);
        Assert.DoesNotContain(reason, exception.Message);
    }

    [Theory]
    [InlineData("ConnectionRateLimitReached: quota reached", "connection.error.busy")]
    [InlineData("RealtimeDisabledForTenant: upgrade required", "connection.error.configuration_unavailable")]
    [InlineData("Unauthorized: denied", "connection.error.access_required")]
    [InlineData("Unknown Error on Channel", "connection.error.service_unavailable")]
    public void ServerRejectionsUseActionableMessagesWithoutProviderDetails(string reason, string messageKey)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { response = new { reason } }));
        var exception = RealtimeSubscriptionException.FromServerPayload(payload.RootElement);

        string message = RealtimeUserErrorMessage.From(exception);

        Assert.Equal(I18n.Get(messageKey), message);
        Assert.DoesNotContain("Supabase", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebSocket", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quota", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upgrade", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reason.Split(':', 2)[0], message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(400, "connection.error.configuration_unavailable")]
    [InlineData(403, "connection.error.configuration_unavailable")]
    [InlineData(401, "connection.error.access_required")]
    public void HandshakeHttpFailuresAvoidUnsupportedAccountAdvice(int statusCode, string messageKey)
    {
        var exception = new HttpRequestException(
            "Handshake failed.",
            null,
            (HttpStatusCode)statusCode);

        Assert.Equal(I18n.Get(messageKey), RealtimeUserErrorMessage.From(exception));
    }

    [Fact]
    public void TrayConnectionFailureOffersManualRecoveryWithoutPromisingAutomaticRetry()
    {
        string message = I18n.Get("tray.connection.error.message");

        Assert.Contains(I18n.Get("connection.retry.action"), message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automatically", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("자동", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialCapacityRejectionEmitsTheActionableBusyMessage()
    {
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement request in joins)
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    topic = request.GetProperty("topic").GetString(),
                    @event = "phx_reply",
                    @ref = request.GetProperty("ref").GetString(),
                    payload = new
                    {
                        status = "error",
                        response = new { reason = "ConnectionRateLimitReached: quota reached" },
                    },
                }), WebSocketMessageType.Text, true, token);
            }
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var room = Guid.NewGuid();

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [room] = 1 },
            room,
            PresenceState.Online,
            server.Token);
        BackendEvent.TechnicalError error = await WaitForEventAsync<BackendEvent.TechnicalError>(transport);

        Assert.Equal(I18n.Get("connection.error.busy"), error.Message);
        Assert.DoesNotContain("ConnectionRateLimitReached", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("quota", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigurationRejectionPausesUntilTheUserRequestsReconnect()
    {
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            if (connection == 1)
            {
                foreach (JsonElement request in joins)
                {
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        topic = request.GetProperty("topic").GetString(),
                        @event = "phx_reply",
                        @ref = request.GetProperty("ref").GetString(),
                        payload = new
                        {
                            status = "error",
                            response = new { reason = "RealtimeDisabledForTenant: upgrade required" },
                        },
                    }), WebSocketMessageType.Text, true, token);
                }
                return;
            }

            foreach (JsonElement request in joins)
            {
                await ReplyAsync(socket, request, token);
            }
        });
        var network = new Network();
        await using SupabaseRealtimeTransport transport = CreateTransport(server, network);
        var room = Guid.NewGuid();

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [room] = 1 },
            room,
            PresenceState.Online,
            server.Token);
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.Diagnostic
            {
                Stage: "realtime-reconnect-paused reason=configuration",
            });
        BackendEvent.TechnicalError error = await WaitForEventAsync<BackendEvent.TechnicalError>(transport);
        Assert.Equal(I18n.Get("connection.error.configuration_unavailable"), error.Message);

        network.ChangePath();
        transport.RequestReconnect();
        await Task.Delay(700, server.Token);
        Assert.Equal(1, server.ConnectionCount);

        transport.RequestReconnect(userInitiated: true);
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task ForbiddenHandshakePausesUntilTheUserRequestsReconnect()
    {
        await using var server = new LocalServer(async (socket, _, token) =>
        {
            JsonElement[] joins = [await ReadAsync(socket, token), await ReadAsync(socket, token)];
            foreach (JsonElement request in joins)
            {
                await ReplyAsync(socket, request, token);
            }
        });
        var network = new Network();
        int attempts = 0;
        await using var transport = new SupabaseRealtimeTransport(
            new SupabaseRuntimeConfiguration(new Uri("https://unused.invalid"), "test-key"),
            new Sessions(),
            network,
            (uri, token) => ++attempts == 1
                ? Task.FromException<ClientWebSocket>(new HttpRequestException(
                    "Handshake forbidden.",
                    null,
                    HttpStatusCode.Forbidden))
                : server.ConnectAsync(uri, token));
        var room = Guid.NewGuid();

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [room] = 1 },
            room,
            PresenceState.Online,
            server.Token);
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.Diagnostic
            {
                Stage: "realtime-reconnect-paused reason=configuration",
            });
        BackendEvent.TechnicalError error = await WaitForEventAsync<BackendEvent.TechnicalError>(transport);
        Assert.Equal(I18n.Get("connection.error.configuration_unavailable"), error.Message);

        network.ChangePath();
        transport.RequestReconnect();
        await Task.Delay(700, server.Token);
        Assert.Equal(1, attempts);

        transport.RequestReconnect(userInitiated: true);
        await WaitForEventAsync(
            transport,
            item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void AppInitiatedWebSocketAbortIsNotAUserFacingFailure()
    {
        var exception = new WebSocketException(
            "The WebSocket was aborted.",
            new SocketException((int)SocketError.OperationAborted));

        Assert.True(RealtimeUserErrorMessage.IsExpectedLocalAbort(exception));
    }

    [Fact]
    public void NetworkAndSecureConnectionFailuresGiveSpecificUserActions()
    {
        var network = new SocketException((int)SocketError.NetworkUnreachable);
        var secure = new AuthenticationException("Handshake failed.", new IOException("Certificate error."));

        Assert.Equal(I18n.Get("connection.error.network_unavailable"), RealtimeUserErrorMessage.From(network));
        Assert.Equal(I18n.Get("connection.error.secure_connection_failed"), RealtimeUserErrorMessage.From(secure));
    }

    [Fact]
    public async Task ResumeReplacesAnApparentlyOpenSocketAndWaitsForNewAcknowledgements()
    {
        var newJoins = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            if (connection == 2)
            {
                newJoins.SetResult();
                await acknowledge.Task.WaitAsync(token);
            }
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(server);
        var room = Guid.NewGuid();
        await transport.SynchronizeAsync(new Dictionary<Guid, long> { [room] = 1 }, room, PresenceState.Online, server.Token);
        transport.RequestReconnect();
        Assert.False(transport.ConnectionStatus.TransportConnected);
        await newJoins.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(transport.ConnectionStatus.TransportConnected);
        acknowledge.SetResult();
        await WaitForEventAsync(transport, item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true }
            && transport.ConnectionStatus.ActiveRoomTransportConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task FourRoomReconnectJoinsOneRoomAtATimeDespiteSlowReplies()
    {
        Guid[] roomIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Guid[] orderedRoomIds = [.. roomIds.Order()];
        Dictionary<Guid, long> rooms = roomIds.ToDictionary(roomId => roomId, _ => 1L);
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            Task<JsonElement>? pendingNextJoin = null;
            foreach (Guid roomId in orderedRoomIds)
            {
                JsonElement first = pendingNextJoin is null
                    ? await ReadAsync(socket, token)
                    : await pendingNextJoin;
                JsonElement second = await ReadAsync(socket, token);
                Assert.Equal("phx_join", first.GetProperty("event").GetString());
                Assert.Equal("phx_join", second.GetProperty("event").GetString());
                string[] expectedTopics = [.. RealtimeEpochSubscriptionPlan.Descriptors(roomId, 1)
                    .Select(descriptor => descriptor.PhoenixTopic)
                    .Order(StringComparer.Ordinal)];
                string[] actualTopics = [.. new[] { first, second }
                    .Select(join => join.GetProperty("topic").GetString()!)
                    .Order(StringComparer.Ordinal)];
                Assert.Equal(expectedTopics, actualTopics);

                pendingNextJoin = connection > 1 && roomId != orderedRoomIds[^1]
                    ? ReadAsync(socket, token)
                    : null;
                if (connection > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700), token);
                }

                await ReplyAsync(socket, first, token);
                if (connection > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700), token);
                    Assert.True(pendingNextJoin is null || !pendingNextJoin.IsCompleted,
                        "The next room joined before both current room channels were acknowledged.");
                }

                await ReplyAsync(socket, second, token);
            }
        });
        await using SupabaseRealtimeTransport transport = CreateTransport(
            server,
            watchdogInterval: TimeSpan.FromSeconds(30));
        await transport.SynchronizeAsync(rooms, roomIds[0], PresenceState.Online, server.Token);
        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);

        transport.RequestReconnect();
        Assert.False(transport.ConnectionStatus.TransportConnected);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        await foreach (BackendEvent item in transport.ReadEventsAsync(deadline.Token))
        {
            if (item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true }
                && transport.ConnectionStatus.ActiveRoomTransportConnected
                && server.ConnectionCount == 2)
            {
                break;
            }
        }

        Assert.True(transport.ConnectionStatus.ActiveRoomTransportConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task RepeatedAccessRejectionPausesAcrossWatchdogAndNetworkHintsUntilManualRetry()
    {
        await using var server = new LocalServer(async (socket, connection, token) =>
        {
            JsonElement first = await ReadAsync(socket, token);
            JsonElement second = await ReadAsync(socket, token);
            if (connection <= 2)
            {
                foreach (JsonElement request in new[] { first, second })
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        topic = request.GetProperty("topic").GetString(),
                        @event = "phx_reply",
                        @ref = request.GetProperty("ref").GetString(),
                        payload = new { status = "error", response = new { reason = "Unauthorized: denied" } },
                    }), WebSocketMessageType.Text, true, token);
                return;
            }
            await ReplyAsync(socket, first, token);
            await ReplyAsync(socket, second, token);
        });
        var network = new Network();
        await using SupabaseRealtimeTransport transport = CreateTransport(server, network);
        var room = Guid.NewGuid();
        await transport.SynchronizeAsync(new Dictionary<Guid, long> { [room] = 1 }, room, PresenceState.Online, server.Token);
        await WaitForEventAsync(transport, item => item is BackendEvent.Diagnostic { Stage: "realtime-reconnect-paused reason=authorization" });
        network.ChangePath();
        network.SetAvailable(false);
        network.SetAvailable(true);
        transport.RequestReconnect(); // Resume does not clear an authorization rejection.
        await Task.Delay(5200, server.Token);
        Assert.Equal(2, server.ConnectionCount);
        Assert.False(transport.ConnectionStatus.TransportConnected);
        transport.RequestReconnect(userInitiated: true);
        await WaitForEventAsync(transport, item => item is BackendEvent.ConnectionChanged { Status.ActiveRoomTransportConnected: true });
        Assert.Equal(3, server.ConnectionCount);
    }

    private static async Task WaitForEventAsync(SupabaseRealtimeTransport transport, Func<BackendEvent, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (BackendEvent item in transport.ReadEventsAsync(timeout.Token))
            if (predicate(item))
                return;
        Assert.Fail("Expected realtime event was not emitted.");
    }

    private static async Task<TEvent> WaitForEventAsync<TEvent>(
        SupabaseRealtimeTransport transport,
        Func<TEvent, bool>? predicate = null)
        where TEvent : BackendEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (BackendEvent item in transport.ReadEventsAsync(timeout.Token))
        {
            if (item is TEvent typed && (predicate is null || predicate(typed)))
            {
                return typed;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected {typeof(TEvent).Name} was not emitted.");
    }

    private static SupabaseRealtimeTransport CreateTransport(
        LocalServer server,
        Network? network = null,
        IAuthSessionAccessor? sessions = null,
        TimeSpan? watchdogInterval = null,
        TimeSpan? authorizationRefreshInterval = null) => new(
            new SupabaseRuntimeConfiguration(new Uri("https://unused.invalid"), "test-key"),
            sessions ?? new Sessions(),
            network ?? new Network(),
            server.ConnectAsync,
            watchdogInterval,
            authorizationRefreshInterval);

    private static async Task<JsonElement> ReadAsync(WebSocket socket, CancellationToken token)
    {
        byte[] bytes = new byte[8192];
        int count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes.AsMemory(count), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Peer closed.");
            count += result.Count;
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
        return document.RootElement.Clone();
    }

    private static async Task ReplyAsync(WebSocket socket, JsonElement request, CancellationToken token) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            topic = request.GetProperty("topic").GetString(),
            @event = "phx_reply",
            @ref = request.GetProperty("ref").GetString(),
            payload = new { status = "ok", response = new { } },
        }), WebSocketMessageType.Text, true, token);

    private static async Task SendEventAsync(
        WebSocket socket,
        string topic,
        string eventName,
        object payload,
        CancellationToken token) =>
        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                topic,
                @event = eventName,
                payload,
            }),
            WebSocketMessageType.Text,
            true,
            token);

    private sealed class Sessions : IAuthSessionAccessor
    {
        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StoredSupabaseSession?>(new(
                "test-token",
                "test-refresh",
                s_userId,
                DateTimeOffset.MaxValue));
    }

    private sealed class RotatingSessions(string accessToken) : IAuthSessionAccessor
    {
        private string _accessToken = accessToken;

        public void SetAccessToken(string accessToken) => Volatile.Write(ref _accessToken, accessToken);

        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<StoredSupabaseSession?>(new(
                Volatile.Read(ref _accessToken),
                "test-refresh",
                s_userId,
                DateTimeOffset.MaxValue));
        }
    }

    private sealed class OneTimeFailingSessions(Exception failure) : IAuthSessionAccessor
    {
        private readonly TaskCompletionSource _failureObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failNextRequest;

        public Task FailureObserved => _failureObserved.Task;

        public void FailNextRequest() => Interlocked.Exchange(ref _failNextRequest, 1);

        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _failNextRequest, 0) != 0)
            {
                _failureObserved.TrySetResult();
                return ValueTask.FromException<StoredSupabaseSession?>(
                    failure);
            }

            return ValueTask.FromResult<StoredSupabaseSession?>(new(
                "test-token",
                "test-refresh",
                s_userId,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class Network : INetworkAvailabilityMonitor
    {
        public bool IsAvailable { get; private set; } = true;
        public event Action<bool>? AvailabilityChanged;
        public event Action? PathChanged;
        public void ChangePath() => PathChanged?.Invoke();
        public void Refresh() { }
        public void SetAvailable(bool available)
        {
            IsAvailable = available;
            AvailabilityChanged?.Invoke(available);
        }
        public void Start() { }
        public void Dispose() { }
    }

    private sealed class LocalServer(Func<WebSocket, int, CancellationToken, Task> script) : IAsyncDisposable
    {
        private readonly TcpListener _listener = StartListener();
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
        private readonly List<Task> _handlers = [];
        public int ConnectionCount { get; private set; }
        public Uri? LastConnectUri { get; private set; }
        public CancellationToken Token => _lifetime.Token;

        private static TcpListener StartListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        public async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken token)
        {
            LastConnectUri = uri;
            int connection = ++ConnectionCount;
            _handlers.Add(ServeAsync(connection));
            var client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"), token);
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        private async Task ServeAsync(int connection)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(Token);
                NetworkStream stream = client.GetStream();
                var header = new StringBuilder();
                byte[] one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, Token) == 0)
                        throw new IOException("Incomplete upgrade.");
                    header.Append((char)one[0]);
                }
                string key = header.ToString().Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), Token);
                using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                await script(socket, connection, Token);
                // Keep the connection alive while the client publishes its initial presence.
                while (socket.State == WebSocketState.Open)
                    await ReadAsync(socket, Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            await Task.WhenAll(_handlers);
            _lifetime.Dispose();
        }
    }
}
