using System.Runtime.CompilerServices;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseV2RealtimeTransportTests
{
    private static readonly Guid s_roomId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid s_messageId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid s_userId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid s_otherRoomId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Fact]
    public async Task DisabledServerSelectionKeepsLegacyAndDoesNotOpenFirebase()
    {
        var legacy = new FakeLegacyTransport();
        var listener = new FakeFirebaseListener();
        await using var transport = new FirebaseV2RealtimeTransport(
            legacy,
            new FakeSelector(enabled: false),
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        Assert.False(transport.UsesFirebaseChat);
        Assert.Equal(1, legacy.SynchronizeCount);
        Assert.Equal(0, listener.StartCount);
        Assert.True(listener.StopCount >= 1);
    }

    [Fact]
    public async Task FailureFallbackRepeatedSynchronizationsDoNotReemitConnection()
    {
        var legacy = new FakeLegacyTransport
        {
            ConnectionStatus = new RealtimeConnectionStatus(true, true, false),
        };
        var listener = new FakeFirebaseListener();
        await using var transport = new FirebaseV2RealtimeTransport(
            legacy,
            new FakeSelector(enabled: false)
            {
                Source = FirebaseRealtimeRolloutSelectionSource.FailureFallback,
            },
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        var epochs = new Dictionary<Guid, long> { [s_roomId] = 1 };

        for (int index = 0; index < 6; index++)
        {
            await transport.SynchronizeAsync(epochs, s_roomId, PresenceState.Online, CancellationToken.None);
        }

        BackendEvent.Diagnostic sentinel = new("fallback-synchronization-complete");
        listener.Publish(sentinel);
        List<BackendEvent> received = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (BackendEvent backendEvent in transport.ReadEventsAsync(timeout.Token))
        {
            if (ReferenceEquals(backendEvent, sentinel))
            {
                break;
            }
            received.Add(backendEvent);
        }

        Assert.Equal(6, legacy.SynchronizeCount);
        BackendEvent.ConnectionChanged connection = Assert.Single(
            received.OfType<BackendEvent.ConnectionChanged>());
        Assert.True(connection.Status.ActiveRoomTransportConnected);
        Assert.DoesNotContain(received, item => item is BackendEvent.ReconciliationRequired);
        Assert.Equal(0, listener.StartCount);
    }

    [Fact]
    public async Task EnabledServerSelectionOpensFirebaseAndUsesCallableChat()
    {
        var legacy = new FakeLegacyTransport();
        var listener = new FakeFirebaseListener();
        var chat = new FakeChatClient();
        await using var transport = new FirebaseV2RealtimeTransport(
            legacy,
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        FirebaseRealtimeChatResult? result = await transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "hello",
            CancellationToken.None);

        Assert.True(transport.UsesFirebaseChat);
        Assert.True(transport.ConnectionStatus.IsReady);
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(s_roomId, listener.ActiveRoomId);
        Assert.Equal(1, chat.SendCount);
        Assert.Equal(s_messageId, result?.MessageId);
    }

    [Fact]
    public async Task ChatRequiresReadyListenerAndActiveRoom()
    {
        var listener = new FakeFirebaseListener { PublishReadyOnStart = false };
        var chat = new FakeChatClient();
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        await Assert.ThrowsAsync<ChatNotReadyException>(() => transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "not-ready",
            CancellationToken.None));
        listener.PublishReady();
        await Assert.ThrowsAsync<ChatNotReadyException>(() => transport.PublishChatAsync(
            s_messageId,
            s_otherRoomId,
            "wrong-room",
            CancellationToken.None));

        Assert.Equal(0, chat.SendCount);
        await transport.PublishChatAsync(s_messageId, s_roomId, "Recovered", CancellationToken.None);
        Assert.Equal(1, chat.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedSynchronizationDoesNotRestartRecoveryButActualDisconnectDoes(bool reconnect)
    {
        // The durable transport leaves reconciliation to the gateway and reports false here.
        var legacy = new FakeLegacyTransport
        {
            ConnectionStatus = new RealtimeConnectionStatus(true, true, false),
        };
        var listener = new FakeFirebaseListener { PublishReadyOnStart = false };
        await using var transport = new FirebaseV2RealtimeTransport(
            legacy,
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        var epochs = new Dictionary<Guid, long> { [s_roomId] = 1 };
        await transport.SynchronizeAsync(epochs, s_roomId, PresenceState.Online, CancellationToken.None);
        listener.PublishReady();

        // Every reconciled snapshot is applied by synchronizing the same room again.
        for (int index = 0; index < 5; index++)
        {
            await transport.SynchronizeAsync(epochs, s_roomId, PresenceState.Online, CancellationToken.None);
        }
        if (reconnect)
        {
            listener.PublishDisconnected();
            listener.PublishReady();
        }
        BackendEvent.Diagnostic sentinel = new("synchronization-complete");
        listener.Publish(sentinel);
        List<BackendEvent> received = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (BackendEvent backendEvent in transport.ReadEventsAsync(timeout.Token))
        {
            if (ReferenceEquals(backendEvent, sentinel))
            {
                break;
            }
            received.Add(backendEvent);
        }

        bool[] activeRoomStates = [.. received.OfType<BackendEvent.ConnectionChanged>()
            .Select(change => change.Status.ActiveRoomTransportConnected)];
        Assert.Equal(reconnect ? [false, true, false, true] : [false, true], activeRoomStates);
        // The gateway reconciles on ConnectionChanged; an extra request here
        // would fetch the full Supabase snapshot twice for each Firebase recovery.
        Assert.DoesNotContain(received, item => item is BackendEvent.ReconciliationRequired);
    }

    [Fact]
    public async Task FirebaseRecoveryWithoutActiveRoomStillRequestsOneReconciliation()
    {
        FakeLegacyTransport legacy = new()
        {
            ConnectionStatus = new RealtimeConnectionStatus(true, false, false),
        };
        FakeFirebaseListener listener = new() { PublishReadyOnStart = false };
        await using FirebaseV2RealtimeTransport transport = new(
            legacy,
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        Dictionary<Guid, long> epochs = new() { [s_roomId] = 1 };
        await transport.SynchronizeAsync(epochs, null, PresenceState.Online, CancellationToken.None);
        listener.PublishReady();

        BackendEvent.Diagnostic sentinel = new("firebase-ready-without-active-room");
        listener.Publish(sentinel);
        List<BackendEvent> received = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await foreach (BackendEvent backendEvent in transport.ReadEventsAsync(timeout.Token))
        {
            if (ReferenceEquals(backendEvent, sentinel))
            {
                break;
            }
            received.Add(backendEvent);
        }

        Assert.Single(received.OfType<BackendEvent.ConnectionChanged>());
        Assert.Single(received.OfType<BackendEvent.ReconciliationRequired>());
    }

    [Fact]
    public async Task ExplicitSessionInvalidationResetsFirebaseCredentialGeneration()
    {
        var credentials = new FakeCredentialProvider();
        var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            credentials,
            sink => new FakeFirebaseListener().WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        await transport.InvalidateSessionAsync();

        Assert.Equal(1, credentials.ResetCount);
        await transport.DisposeAsync();
        Assert.Equal(1, credentials.ResetCount);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "blocked",
            CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ConvergeGrantAsync(
            "00000000000000000003",
            CancellationToken.None));
    }

    [Fact]
    public async Task GrantConvergenceFailureStopsFirebaseWithoutTurningCommittedMutationIntoFailure()
    {
        var listener = new FakeFirebaseListener();
        var credentials = new FakeCredentialProvider
        {
            ConvergeFailure = new HttpRequestException("offline"),
        };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            credentials,
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        await transport.ConvergeGrantAsync(
            "00000000000000000002",
            CancellationToken.None);

        Assert.True(transport.UsesFirebaseChat);
        Assert.False(transport.ConnectionStatus.IsReady);
        Assert.True(listener.StopCount >= 1);
    }

    [Fact]
    public async Task SessionInvalidationCancelsChatThatAlreadyPassedTheTerminalFence()
    {
        var chat = new FakeChatClient { Block = true };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => new FakeFirebaseListener().WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        Task<FirebaseRealtimeChatResult?> pending = transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "hello",
            CancellationToken.None);
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await transport.InvalidateSessionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ActiveRoomChangeMarksInFlightChatCommitAmbiguous()
    {
        var chat = new FakeChatClient { Block = true };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => new FakeFirebaseListener().WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1, [s_otherRoomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        Task<FirebaseRealtimeChatResult?> pending = transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "hello",
            CancellationToken.None);
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1, [s_otherRoomId] = 1 },
            s_otherRoomId,
            PresenceState.Online,
            CancellationToken.None);

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            FirebaseRealtimeChatFailureClassification.CommitAmbiguous,
            exception.Classification);
        Assert.Equal("transport-canceled", exception.Code);
    }

    [Fact]
    public async Task DisconnectAfterCallableCommitMarksResponseWaitAmbiguous()
    {
        var chat = new FakeChatClient { Block = true };
        var listener = new FakeFirebaseListener();
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        Task<FirebaseRealtimeChatResult?> pending = transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "hello",
            CancellationToken.None);
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        listener.PublishDisconnected();

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            FirebaseRealtimeChatFailureClassification.CommitAmbiguous,
            exception.Classification);
        Assert.Equal("transport-canceled", exception.Code);
    }

    [Fact]
    public async Task CallerCancellationPreservesCancellationSemanticsForInFlightChat()
    {
        var chat = new FakeChatClient { Block = true };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            chat,
            new FakeCredentialProvider(),
            sink => new FakeFirebaseListener().WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<FirebaseRealtimeChatResult?> pending = transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "hello",
            cancellation.Token);
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnchangedSelectorRenewalKeepsListenerAndInFlightChatAlive()
    {
        var selector = new FakeSelector(enabled: true) { CacheTtl = TimeSpan.FromMilliseconds(100) };
        var listener = new FakeFirebaseListener();
        var chat = new FakeChatClient { Block = true };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(), selector, chat, new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId, PresenceState.Online, CancellationToken.None);
        Task<FirebaseRealtimeChatResult?> pending = transport.PublishChatAsync(
            s_messageId, s_roomId, "private chat", CancellationToken.None);
        await chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int selections = 0;
        await foreach (BackendEvent item in transport.ReadEventsAsync(timeout.Token))
        {
            if (item is BackendEvent.Diagnostic { Stage: "firebase-selector transport=v2 source=Server" }
                && ++selections == 2)
            {
                break;
            }
        }

        Assert.Equal(0, listener.ReconnectCount);
        Assert.True(transport.ConnectionStatus.IsReady);
        Assert.False(pending.IsCompleted);
        chat.Complete.TrySetResult();
        Assert.Equal(s_messageId, (await pending.WaitAsync(timeout.Token))?.MessageId);
    }

    [Fact]
    public async Task SelectorFailureStopsListenerAndFailsClosedForReadsAndWrites()
    {
        var selector = new FakeSelector(enabled: true)
        {
            CacheTtl = TimeSpan.FromMilliseconds(100),
            RefreshFailure = new FirebaseRealtimeRolloutException(
                failClosed: true,
                statusCode: System.Net.HttpStatusCode.ServiceUnavailable,
                failureCode: "PGRST003",
                retryAfter: TimeSpan.FromSeconds(90)),
        };
        var listener = new FakeFirebaseListener();
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            selector,
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<BackendEvent> received = [];
        var reader = Task.Run(async () =>
        {
            await foreach (BackendEvent backendEvent in transport.ReadEventsAsync(timeout.Token))
            {
                received.Add(backendEvent);
                if (backendEvent is BackendEvent.Diagnostic
                    { Stage: "after-selector-failure" })
                {
                    break;
                }
            }
        });
        await selector.RefreshAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => listener.StopCount >= 1);
        listener.Publish(new BackendEvent.CharacterPulsed(
            new CharacterPulseEvent(Guid.NewGuid(), s_roomId, s_userId)));
        listener.Publish(new BackendEvent.Diagnostic("after-selector-failure"));
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(received, item => item is BackendEvent.Diagnostic
        { Stage: "firebase-selector-error status=503 code=PGRST003 retry-seconds=90" });
        Assert.False(transport.ConnectionStatus.IsReady);
        Assert.DoesNotContain(received, item => item is BackendEvent.CharacterPulsed);
        await Assert.ThrowsAsync<ChatNotReadyException>(() => transport.PublishChatAsync(
            s_messageId,
            s_roomId,
            "blocked",
            CancellationToken.None));
    }

    [Fact]
    public async Task GrantConvergenceFailureWakesLongSelectorDelayForImmediateRecovery()
    {
        var selector = new FakeSelector(enabled: true) { CacheTtl = TimeSpan.FromHours(1) };
        var listener = new FakeFirebaseListener();
        var credentials = new FakeCredentialProvider
        {
            ConvergeFailure = new HttpRequestException("offline"),
        };
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            selector,
            new FakeChatClient(),
            credentials,
            sink => listener.WithSink(sink));
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        await transport.ConvergeGrantAsync("00000000000000000002", CancellationToken.None);

        await selector.RefreshAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => listener.StartCount >= 2);
        Assert.True(transport.ConnectionStatus.IsReady);
    }

    [Fact]
    public async Task EnabledSelectionPublishesAllTransientActionsThroughFirebase()
    {
        var transients = new FakeTransientClient();
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(),
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => new FakeFirebaseListener().WithSink(sink),
            transients);
        transport.ConfigureThrowableWireCodes(new Dictionary<string, string>
        {
            ["throwable_leaf"] = "18",
        });
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        await transport.PublishTypingAsync(s_roomId, active: true, CancellationToken.None);
        await transport.PublishCharacterPulseAsync(s_roomId, CancellationToken.None);
        await transport.PublishCharacterThrowAsync(
            s_roomId,
            s_userId,
            "throwable_leaf",
            CancellationToken.None);

        Assert.Equal(1, transients.TypingCount);
        Assert.Equal(1, transients.PulseCount);
        Assert.Equal(1, transients.ThrowCount);
        Assert.Equal("18", transients.LastWireCode);
    }

    [Fact]
    public async Task EnabledSelectionDropsLegacyTransientMirrorEvents()
    {
        BackendEvent.Diagnostic sentinel = new("legacy-sentinel");
        var legacy = new FakeLegacyTransport(
        [
            new BackendEvent.TypingChanged(s_roomId, s_userId, true),
            new BackendEvent.MessageChanged(s_roomId, s_messageId, "INSERT"),
            new BackendEvent.MessagesInvalidated(s_roomId),
            new BackendEvent.CharacterPulsed(
                new CharacterPulseEvent(Guid.NewGuid(), s_roomId, s_userId)),
            new BackendEvent.CharacterThrown(new CharacterThrowEvent(
                Guid.NewGuid(),
                s_roomId,
                s_userId,
                Guid.NewGuid(),
                "pixel_hamster")),
            sentinel,
        ]);
        await using var transport = new FirebaseV2RealtimeTransport(
            legacy,
            new FakeSelector(enabled: true),
            new FakeChatClient(),
            new FakeCredentialProvider(),
            sink => new FakeFirebaseListener().WithSink(sink),
            new FakeTransientClient());
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId,
            PresenceState.Online,
            CancellationToken.None);

        List<BackendEvent> received = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (BackendEvent backendEvent in transport.ReadEventsAsync(timeout.Token))
        {
            received.Add(backendEvent);
            if (ReferenceEquals(backendEvent, sentinel))
            {
                break;
            }
        }

        Assert.DoesNotContain(received, item => item is BackendEvent.TypingChanged
            or BackendEvent.CharacterPulsed
            or BackendEvent.CharacterThrown);
        Assert.Contains(received, item => item is BackendEvent.MessageChanged
        {
            RoomId: var roomId,
            MessageId: var messageId,
            Operation: "INSERT",
        } && roomId == s_roomId && messageId == s_messageId);
        Assert.Contains(received, item => item is BackendEvent.MessagesInvalidated
        {
            RoomId: var roomId,
        } && roomId == s_roomId);
        Assert.Contains(received, item => item is BackendEvent.Diagnostic
        {
            Stage: "legacy-live-pulse result=firebase-selected",
        });
        Assert.Contains(received, item => item is BackendEvent.Diagnostic
        {
            Stage: "legacy-live-throw result=firebase-selected",
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendDiagnosticDistinguishesServerOutcomeWithoutPayload(bool chat, bool fail)
    {
        Exception? failure = fail ? new HttpRequestException("private server detail") : null;
        var listener = new FakeFirebaseListener();
        await using var transport = new FirebaseV2RealtimeTransport(
            new FakeLegacyTransport(), new FakeSelector(enabled: true),
            new FakeChatClient { Failure = failure }, new FakeCredentialProvider(),
            sink => listener.WithSink(sink), new FakeTransientClient { Failure = failure });
        await transport.SynchronizeAsync(
            new Dictionary<Guid, long> { [s_roomId] = 1 },
            s_roomId, PresenceState.Online, CancellationToken.None);
        Task sending = chat
            ? transport.PublishChatAsync(s_messageId, s_roomId, "private message body", CancellationToken.None)
            : transport.PublishCharacterThrowAsync(s_roomId, s_userId, "patch_soft_ball", CancellationToken.None);
        if (fail)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => sending));
        }
        else
        {
            await sending;
        }

        BackendEvent.Diagnostic sentinel = new("send-complete");
        listener.Publish(sentinel);
        List<string> diagnostics = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (BackendEvent item in transport.ReadEventsAsync(timeout.Token))
        {
            if (ReferenceEquals(item, sentinel))
            {
                break;
            }
            if (item is BackendEvent.Diagnostic diagnostic
                && diagnostic.Stage.StartsWith("firebase-send", StringComparison.Ordinal))
            {
                diagnostics.Add(diagnostic.Stage);
            }
        }
        string kind = chat ? "chat" : "throw";
        string result = fail ? "failed reason=transport" : chat ? "committed" : "accepted";
        Assert.Equal(
            [$"firebase-send kind={kind} result=started", $"firebase-send kind={kind} result={result}"],
            diagnostics);
    }

    private sealed class FakeSelector(bool enabled) : IFirebaseRealtimeRolloutSelector
    {
        public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
        public FirebaseRealtimeRolloutSelectionSource Source { get; init; } =
            FirebaseRealtimeRolloutSelectionSource.Server;
        public Exception? RefreshFailure { get; init; }
        public TaskCompletionSource RefreshAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<FirebaseRealtimeRolloutSelection> SelectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new FirebaseRealtimeRolloutSelection(
                enabled
                    ? FirebaseRealtimeSelectedTransport.FirebaseV2
                    : FirebaseRealtimeSelectedTransport.LegacySupabase,
                killSwitch: !enabled,
                CacheTtl,
                Source));
        }

        public ValueTask<FirebaseRealtimeRolloutSelection> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshAttempted.TrySetResult();
            return RefreshFailure is null
                ? SelectAsync(cancellationToken)
                : ValueTask.FromException<FirebaseRealtimeRolloutSelection>(RefreshFailure);
        }
    }

    private sealed class FakeChatClient : IFirebaseRealtimeChatClient
    {
        public int SendCount { get; private set; }
        public bool Block { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FirebaseRealtimeChatResult> SendAsync(
            Guid messageId,
            Guid roomId,
            string body,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            Started.TrySetResult();
            if (Failure is not null)
            {
                throw Failure;
            }
            if (Block)
            {
                await Complete.Task.WaitAsync(cancellationToken);
            }
            return new FirebaseRealtimeChatResult(
                messageId,
                roomId,
                s_userId,
                body,
                1_750_000_000_000,
                sequence: 1,
                bubbleWireCode: null);
        }
    }

    private sealed class FakeCredentialProvider : IFirebaseRealtimeCredentialProvider
    {
        public int ResetCount { get; private set; }
        public Exception? ConvergeFailure { get; init; }

        public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FirebaseRealtimeCredential(
                s_userId,
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                "token",
                new Uri("https://sidey.asia-southeast1.firebasedatabase.app"),
                generation: 1));

        public ValueTask<FirebaseRealtimeCredential> ConvergeAsync(
            string minimumAccessRevision,
            CancellationToken cancellationToken = default) =>
            ConvergeFailure is null
                ? GetCredentialAsync(cancellationToken)
                : ValueTask.FromException<FirebaseRealtimeCredential>(ConvergeFailure);

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFirebaseListener : IFirebaseRealtimeListener
    {
        private Action<BackendEvent>? _sink;

        public bool IsReady { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Guid? ActiveRoomId { get; private set; }
        public bool PublishReadyOnStart { get; init; } = true;
        public int ReconnectCount { get; private set; }

        public FakeFirebaseListener WithSink(Action<BackendEvent> sink)
        {
            _sink = sink;
            return this;
        }

        public Task StartAsync(Guid? activeRoomId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            ActiveRoomId = activeRoomId;
            if (PublishReadyOnStart && !IsReady)
            {
                PublishReady();
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            IsReady = false;
            _sink?.Invoke(new BackendEvent.ConnectionChanged(RealtimeConnectionStatus.Disconnected));
            return Task.CompletedTask;
        }

        public void PublishReady()
        {
            IsReady = true;
            _sink?.Invoke(new BackendEvent.ConnectionChanged(
                new RealtimeConnectionStatus(true, true, true)));
        }

        public void PublishDisconnected()
        {
            IsReady = false;
            _sink?.Invoke(new BackendEvent.ConnectionChanged(
                RealtimeConnectionStatus.Disconnected));
        }

        public void Publish(BackendEvent backendEvent) => _sink?.Invoke(backendEvent);

        public void RequestReconnect()
        {
            ReconnectCount++;
            PublishDisconnected();
        }

        public ValueTask DisposeAsync()
        {
            IsReady = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransientClient : IFirebaseRealtimeTransientClient
    {
        public Exception? Failure { get; init; }
        public int TypingCount { get; private set; }
        public int PulseCount { get; private set; }
        public int ThrowCount { get; private set; }
        public string? LastWireCode { get; private set; }

        public Task PublishTypingAsync(
            Guid roomId,
            bool active,
            CancellationToken cancellationToken)
        {
            TypingCount++;
            return Task.CompletedTask;
        }

        public Task PublishCharacterPulseAsync(Guid roomId, CancellationToken cancellationToken)
        {
            PulseCount++;
            return Task.CompletedTask;
        }

        public Task PublishCharacterThrowAsync(
            Guid roomId,
            Guid targetUserId,
            string wireCode,
            CancellationToken cancellationToken)
        {
            ThrowCount++;
            LastWireCode = wireCode;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class FakeLegacyTransport(
        IReadOnlyList<BackendEvent>? events = null) : IRealtimeTransport
    {
        public int SynchronizeCount { get; private set; }
        public RealtimeConnectionStatus ConnectionStatus { get; init; } = new(true, true, true);
        public bool IsRecoveryPaused => false;

        public async IAsyncEnumerable<BackendEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (BackendEvent backendEvent in events ?? [])
            {
                yield return backendEvent;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public Task SynchronizeAsync(
            IReadOnlyDictionary<Guid, long> roomEpochs,
            Guid? activeRoomId,
            PresenceState localPresence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SynchronizeCount++;
            return Task.CompletedTask;
        }

        public Task PublishPresenceAsync(
            Guid roomId,
            PresenceState state,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<bool> RunWhileConnectedAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            await operation(cancellationToken);
            return true;
        }

        public void RequestReconnect(bool userInitiated = false)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
