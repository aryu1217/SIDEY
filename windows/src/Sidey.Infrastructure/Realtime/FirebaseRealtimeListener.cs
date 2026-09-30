using System.Text;
using System.Text.Json.Nodes;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Infrastructure.Realtime;

internal interface IFirebaseRealtimeListener : IAsyncDisposable
{
    public bool IsReady { get; }

    public Task StartAsync(Guid? activeRoomId, CancellationToken cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken);
    public void ConfigureThrowableWireCodes(IReadOnlyDictionary<string, string> catalogItemIdsByWireCode)
    {
    }
    public void RequestReconnect();
}

/// <summary>
/// Owns the Firebase inbox and active-room streams. Firebase delivers transient activity
/// directly while durable chat and structural hints trigger authoritative reconciliation.
/// </summary>
internal sealed class FirebaseRealtimeListener : IFirebaseRealtimeListener
{
    private static readonly TimeSpan s_rotationDeadline = TimeSpan.FromSeconds(20);

    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly Func<Uri, FirebaseRtdbRestClient> _createClient;
    private readonly Action<BackendEvent> _emit;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _snapshotGate = new();
    private readonly Lock _liveEmitGate = new();
    private readonly Lock _reconnectGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _generationCancellation;
    private Task _generationTask = Task.CompletedTask;
    private Task _reconnectTask = Task.CompletedTask;
    private bool _reconnectPending;
    private CancellationTokenSource? _rejectedGeneration;
    private long _credentialListenerGeneration;
    private long _credentialGeneration;
    private long _credentialAuthVersion;
    private long _rejectedCredentialGeneration;
    private long _rejectedAuthVersion;
    private Guid? _activeRoomId;
    private string? _accessRevision;
    private readonly Dictionary<Guid, string> _roomRevisions = [];
    private readonly Dictionary<Guid, long> _chatSequences = [];
    private readonly Dictionary<Guid, (Guid MessageId, long Sequence)> _liveChatEvents = [];
    private FirebaseRealtimeLiveReconciler _liveReconciler = new();
    private IReadOnlyDictionary<string, string> _throwableCatalogItemIdsByWireCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = "patch_soft_ball",
        };
    private CancellationTokenSource? _typingExpiryCancellation;
    private readonly HashSet<Guid> _activeTypingUserIds = [];
    private int _ready;
    private long _generation;
    private long _liveActionGeneration;

    public FirebaseRealtimeListener(
        IFirebaseRealtimeCredentialProvider credentials,
        Action<BackendEvent> emit,
        Func<Uri, FirebaseRtdbRestClient>? createClient = null,
        TimeProvider? timeProvider = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createClient = createClient ?? (databaseUrl =>
            new FirebaseRtdbRestClient(new FirebaseRtdbUrlBuilder(databaseUrl)));
        _credentials.CredentialRejected += OnCredentialRejected;
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public void ConfigureThrowableWireCodes(
        IReadOnlyDictionary<string, string> catalogItemIdsByWireCode)
    {
        ArgumentNullException.ThrowIfNull(catalogItemIdsByWireCode);
        lock (_snapshotGate)
        {
            _throwableCatalogItemIdsByWireCode =
                new Dictionary<string, string>(catalogItemIdsByWireCode, StringComparer.Ordinal);
        }
    }

    public async Task StartAsync(Guid? activeRoomId, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_generationCancellation is not null
                && _activeRoomId == activeRoomId
                && !_generationTask.IsCompleted)
            {
                return;
            }

            bool activeRoomChanged = _activeRoomId != activeRoomId;
            await StopWithinGateAsync().ConfigureAwait(false);
            _activeRoomId = activeRoomId;
            if (activeRoomChanged)
            {
                lock (_snapshotGate)
                {
                    _liveReconciler = new FirebaseRealtimeLiveReconciler();
                }
            }
            Volatile.Write(ref _ready, 0);
            var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdown.Token);
            _generationCancellation = generationCancellation;
            long generation = Interlocked.Increment(ref _generation);
            _generationTask = RunGenerationAsync(
                generation,
                activeRoomId,
                generationCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopWithinGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void RequestReconnect() => RequestReconnectCore(forceInvalidCredential: false);

    private void RequestReconnectCore(bool forceInvalidCredential)
    {
        CancellationTokenSource? rejected = null;
        lock (_reconnectGate)
        {
            CancellationTokenSource? current = Volatile.Read(ref _generationCancellation);
            if (_shutdown.IsCancellationRequested || current is null || (!forceInvalidCredential && !IsReady))
            {
                return;
            }
            if (forceInvalidCredential)
            {
                rejected = current;
            }
            if (_reconnectPending)
            {
                if (forceInvalidCredential)
                {
                    _rejectedGeneration = current;
                }
            }
            else
            {
                _reconnectPending = true;
                _reconnectTask = RunReconnectRequestsAsync(current);
            }
        }
        // A rejected token must not let an opening candidate become ready while a
        // previous reconnect is finishing. The queued restart remains coalesced.
        try
        {
            rejected?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnCredentialRejected(FirebaseRealtimeCredential credential)
    {
        lock (_reconnectGate)
        {
            if (credential.Generation > _rejectedCredentialGeneration)
            {
                _rejectedCredentialGeneration = credential.Generation;
                _rejectedAuthVersion = credential.AuthVersion;
            }
            else if (credential.Generation == _rejectedCredentialGeneration)
            {
                _rejectedAuthVersion = Math.Max(_rejectedAuthVersion, credential.AuthVersion);
            }
            if (_credentialListenerGeneration != Volatile.Read(ref _generation)
                || credential.Generation != _credentialGeneration
                || credential.AuthVersion != _credentialAuthVersion)
            {
                return;
            }
            RequestReconnectCore(forceInvalidCredential: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _credentials.CredentialRejected -= OnCredentialRejected;
        _shutdown.Cancel();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopWithinGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        Task reconnect;
        lock (_reconnectGate)
        {
            reconnect = _reconnectTask;
        }
        await reconnect.ConfigureAwait(false);
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task RunGenerationAsync(
        long generation,
        Guid? activeRoomId,
        CancellationToken cancellationToken)
    {
        StreamSet? current = null;
        int consecutiveFailures = 0;
        long? readySince = null;

        async Task DelayAfterFailureAsync(Exception? failure = null)
        {
            if (readySince is long timestamp
                && _timeProvider.GetElapsedTime(timestamp) >= FirebaseRealtimeRetryPolicy.StableConnection)
            {
                consecutiveFailures = 0;
            }
            readySince = null;
            consecutiveFailures = Math.Min(consecutiveFailures + 1, 17);
            System.Net.HttpStatusCode? statusCode = failure switch
            {
                HttpRequestException request => request.StatusCode,
                FirebaseRealtimeStreamRequestException request => request.StatusCode,
                FirebaseRtdbRequestException request => request.StatusCode,
                _ => null,
            };
            TimeSpan delay = FirebaseRealtimeRetryPolicy.CalculateDelay(
                consecutiveFailures,
                failure is FirebaseRealtimeRetryException ? null : statusCode,
                retryAfter: null,
                Random.Shared.NextDouble());
            if (failure is FirebaseRealtimeRetryException retryFailure && retryFailure.RetryAfter > delay)
            {
                // The provider already applies jitter to its shared deadline.
                delay = retryFailure.RetryAfter;
            }
            await FirebaseRealtimeRetryPolicy.DelayAsync(
                delay,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                StreamSet? next = null;
                using var openingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    Task<StreamSet> opening = OpenReadyStreamSetAsync(
                        generation,
                        activeRoomId,
                        openingCancellation.Token,
                        cancellationToken);
                    if (current is not null)
                    {
                        await Task.WhenAny(opening, current.Completion, current.Expiry).ConfigureAwait(false);
                        if (current.IsUnavailable && !opening.IsCompletedSuccessfully)
                        {
                            BeginDisconnect(generation);
                            openingCancellation.Cancel();
                            try
                            {
                                next = await opening.ConfigureAwait(false);
                                await next.DisposeAsync().ConfigureAwait(false);
                                next = null;
                            }
                            catch (Exception) when (!cancellationToken.IsCancellationRequested)
                            {
                                // The discarded candidate cannot preserve its partial baseline
                                // after the old streams lose authorization or end.
                            }
                            await DisconnectStreamSetAsync(current, generation, activeRoomId).ConfigureAwait(false);
                            current = null;
                            await DelayAfterFailureAsync().ConfigureAwait(false);
                            continue;
                        }
                    }
                    next = await opening.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (next.IsUnavailable)
                    {
                        throw new UnauthorizedAccessException("Firebase realtime candidate expired or ended before promotion.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (next is not null)
                    {
                        await next.DisposeAsync().ConfigureAwait(false);
                    }
                    break;
                }
                catch (Exception exception)
                {
                    if (current is { IsUnavailable: true })
                    {
                        await DisconnectStreamSetAsync(current, generation, activeRoomId).ConfigureAwait(false);
                        current = null;
                    }
                    bool initialConnectionFailure = current is null;
                    if (initialConnectionFailure)
                    {
                        BeginDisconnect(generation);
                    }
                    if (next is not null)
                    {
                        await next.DisposeAsync().ConfigureAwait(false);
                    }
                    if (initialConnectionFailure)
                    {
                        ClearLiveState(activeRoomId);
                    }
                    _emit(new BackendEvent.Diagnostic(
                        $"firebase-listener-connect-failed {FailureDiagnostic(exception)}"));
                    Task retry = DelayAfterFailureAsync(exception);
                    if (current is not null)
                    {
                        await Task.WhenAny(retry, current.Completion, current.Expiry).ConfigureAwait(false);
                        if (current.IsUnavailable)
                        {
                            await DisconnectStreamSetAsync(current, generation, activeRoomId).ConfigureAwait(false);
                            current = null;
                        }
                    }
                    await retry.ConfigureAwait(false);
                    continue;
                }

                StreamSet? previous = current;
                current = next;
                BeginLiveGeneration(generation);
                SetReady(ready: true);
                readySince = _timeProvider.GetTimestamp();
                _emit(new BackendEvent.Diagnostic(
                    $"firebase-listener-ready streams={(activeRoomId.HasValue ? 2 : 1)} generation={generation}"));
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                using var rotationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var rotation = Task.Delay(current.RemainingUntilRefresh, _timeProvider, rotationCancellation.Token);
                Task completed = await Task.WhenAny(current.Completion, current.Expiry, rotation).ConfigureAwait(false);
                rotationCancellation.Cancel();
                if (completed == rotation && !current.IsUnavailable && !cancellationToken.IsCancellationRequested)
                {
                    // Open the replacement before closing the old set. With an active room this
                    // creates at most four streams (old/new inbox + room) during token rotation.
                    continue;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    Exception? failure = current.Failure;
                    await DisconnectStreamSetAsync(current, generation, activeRoomId).ConfigureAwait(false);
                    current = null;
                    await DelayAfterFailureAsync(failure).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            BeginDisconnect(generation);
            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
            ClearLiveState(activeRoomId);
        }
    }

    private async Task DisconnectStreamSetAsync(StreamSet streamSet, long generation, Guid? activeRoomId)
    {
        string failure = streamSet.Expiry.IsCompletedSuccessfully
            ? "kind=lease-expired"
            : FailureDiagnostic(streamSet.Failure);
        BeginDisconnect(generation);
        await streamSet.DisposeAsync().ConfigureAwait(false);
        ClearLiveState(activeRoomId);
        _emit(new BackendEvent.Diagnostic($"firebase-listener-disconnected {failure}"));
    }

    private async Task<StreamSet> OpenReadyStreamSetAsync(
        long generation,
        Guid? activeRoomId,
        CancellationToken openingToken,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(s_rotationDeadline, _timeProvider);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(openingToken, deadline.Token);
        StreamSet? next = null;
        try
        {
            next = await OpenStreamSetAsync(generation, activeRoomId, startup.Token, cancellationToken).ConfigureAwait(false);
            await Task.WhenAny(next.Ready, next.Completion, next.Expiry).WaitAsync(startup.Token).ConfigureAwait(false);
            startup.Token.ThrowIfCancellationRequested();
            if (next.Expiry.IsCompletedSuccessfully)
            {
                throw new UnauthorizedAccessException("Firebase realtime credential lease expired before readiness.");
            }
            if (next.Completion.IsCompleted)
            {
                await next.Completion.ConfigureAwait(false);
                throw new InvalidDataException("Firebase realtime stream ended before its initial snapshot.");
            }
            await next.Ready.ConfigureAwait(false);
            EnsureCredentialNotRejected(next.Credential);
            return next;
        }
        catch
        {
            if (next is not null)
            {
                await next.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<StreamSet> OpenStreamSetAsync(
        long generation,
        Guid? activeRoomId,
        CancellationToken startupToken,
        CancellationToken cancellationToken)
    {
        FirebaseRealtimeCredential credential;
        try
        {
            credential = await _credentials.GetCredentialAsync(startupToken).ConfigureAwait(false);
        }
        catch (FirebaseRealtimeCredentialStageException exception)
        {
            throw new FirebaseRealtimeCredentialProtocolException(
                exception.Stage,
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw new FirebaseRealtimeCredentialProtocolException(
                detail: null,
                innerException: exception);
        }
        lock (_reconnectGate)
        {
            EnsureCredentialNotRejected(credential);
            _credentialListenerGeneration = generation;
            _credentialGeneration = credential.Generation;
            _credentialAuthVersion = credential.AuthVersion;
        }
        FirebaseRtdbRestClient client = _createClient(credential.DatabaseUrl);
        var streamSet = new StreamSet(client, activeRoomId.HasValue ? 2 : 1, credential, _timeProvider, cancellationToken);
        BeginLiveGeneration(generation);
        streamSet.Add(PumpStreamAsync(
            streamSet,
            FirebaseRealtimeProtocol.InboxPath(credential.UserId),
            credential,
            roomId: null,
            generation,
            cancellationToken));
        if (activeRoomId is { } roomId)
        {
            streamSet.Add(PumpStreamAsync(
                streamSet,
                FirebaseRealtimeProtocol.RoomPath(roomId),
                credential,
                roomId,
                generation,
                cancellationToken));
        }
        streamSet.Seal();
        return streamSet;
    }

    private async Task PumpStreamAsync(
        StreamSet streamSet,
        string path,
        FirebaseRealtimeCredential credential,
        Guid? roomId,
        long generation,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            credential.LifetimeToken,
            streamSet.Token);
        try
        {
            await using FirebaseRtdbEventStream stream = await streamSet.Client.OpenEventStreamAsync(
                path,
                credential.IdToken,
                linked.Token).ConfigureAwait(false);
            FirebaseRealtimeServerClock? serverClock = roomId.HasValue && stream.ServerDate is { } serverDate
                ? new FirebaseRealtimeServerClock(_timeProvider, serverDate)
                : null;
            if (roomId.HasValue)
            {
                _emit(new BackendEvent.Diagnostic(serverClock is null
                    ? "firebase-live-clock source=local"
                    : $"firebase-live-clock source=server offset-ms={serverClock.OffsetMilliseconds}"));
            }
            var snapshot = new FirebaseRtdbSnapshot();
            bool initialMutation = true;
            await foreach (FirebaseSseEvent firebaseEvent in stream.ReadEventsAsync(linked.Token))
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                if (firebaseEvent.Kind is FirebaseSseEventKind.Put or FirebaseSseEventKind.Patch)
                {
                    try
                    {
                        snapshot.Apply(firebaseEvent.GetMutation());
                        ProcessSnapshot(
                            roomId,
                            snapshot.Value,
                            initialMutation,
                            credential.UserId,
                            generation,
                            serverClock);
                    }
                    catch (InvalidDataException)
                    {
                        _emit(new BackendEvent.Diagnostic(
                            $"firebase-listener-snapshot-ignored kind=protocol stream={(roomId.HasValue ? "room" : "inbox")}"));
                        continue;
                    }
                    if (initialMutation)
                    {
                        initialMutation = false;
                        streamSet.MarkReady();
                    }
                    continue;
                }

                if (firebaseEvent.Kind is FirebaseSseEventKind.Cancel or FirebaseSseEventKind.AuthRevoked)
                {
                    throw new UnauthorizedAccessException("Firebase realtime permission was revoked.");
                }
            }

            throw new FirebaseRealtimeStreamEndedException(
                roomId.HasValue ? "room" : "inbox",
                initialMutation ? "initial" : "active");
        }
        catch (InvalidDataException exception)
        {
            throw new FirebaseRealtimeStreamProtocolException(
                roomId.HasValue ? "room" : "inbox",
                exception);
        }
        catch (FirebaseRtdbRequestException exception)
        {
            throw new FirebaseRealtimeStreamRequestException(
                roomId.HasValue ? "room" : "inbox",
                exception);
        }
    }

    private void ProcessSnapshot(
        Guid? roomId,
        JsonNode? value,
        bool initialMutation,
        Guid localUserId,
        long generation,
        FirebaseRealtimeServerClock? serverClock)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null");
        if (roomId is { } activeRoomId)
        {
            FirebaseRealtimeRoomPayload payload = FirebaseRealtimeProtocol.ParseRoomPayload(utf8);
            ProcessLiveActions(activeRoomId, localUserId, payload, generation, serverClock);
            if (payload.ServerEvent is { } serverEvent)
            {
                ObserveChatEvent(
                    activeRoomId,
                    serverEvent,
                    initialMutation,
                    ServerNowMilliseconds(serverClock));
            }
            return;
        }

        FirebaseRealtimeInboxPayload inbox = FirebaseRealtimeProtocol.ParseInboxPayload(utf8);
        List<BackendEvent> pending = [];
        lock (_snapshotGate)
        {
            bool baseline = _accessRevision is null && initialMutation;
            if (inbox.AccessRevision is { } accessRevision
                && _accessRevision is not null
                && StringComparer.Ordinal.Compare(accessRevision, _accessRevision) > 0
                && !baseline)
            {
                pending.Add(new BackendEvent.ReconciliationRequired());
            }
            if (inbox.AccessRevision is { } nextAccessRevision
                && (_accessRevision is null
                    || StringComparer.Ordinal.Compare(nextAccessRevision, _accessRevision) > 0))
            {
                _accessRevision = nextAccessRevision;
            }

            foreach ((Guid id, FirebaseRealtimeInboxRoom room) in inbox.Rooms)
            {
                if (room.Revision is { } roomRevision)
                {
                    if (_roomRevisions.TryGetValue(id, out string? revision)
                        && StringComparer.Ordinal.Compare(roomRevision, revision) > 0
                        && !baseline)
                    {
                        pending.Add(new BackendEvent.RoomStructureChanged(id));
                    }
                    if (!_roomRevisions.TryGetValue(id, out revision)
                        || StringComparer.Ordinal.Compare(roomRevision, revision) > 0)
                    {
                        _roomRevisions[id] = roomRevision;
                    }
                }

                if (room.ChatSequence is { } chatSequence
                    && AdvanceSequenceWithinLock(id, chatSequence)
                    && !baseline)
                {
                    pending.Add(new BackendEvent.MessagesInvalidated(id));
                }
            }
        }

        foreach (BackendEvent backendEvent in pending)
        {
            _emit(backendEvent);
        }
    }

    private void ProcessLiveActions(
        Guid roomId,
        Guid localUserId,
        FirebaseRealtimeRoomPayload payload,
        long generation,
        FirebaseRealtimeServerClock? serverClock)
    {
        IReadOnlyList<FirebaseRealtimeLiveAction> actions;
        IReadOnlyDictionary<string, string> throwableCatalogItemIds;
        FirebaseRealtimeLiveObservation observation;
        lock (_snapshotGate)
        {
            actions = _liveReconciler.Consume(
                payload,
                ServerNowMilliseconds(serverClock),
                out observation);
            throwableCatalogItemIds = _throwableCatalogItemIdsByWireCode;
        }
        lock (_liveEmitGate)
        {
            if (_liveActionGeneration != generation)
            {
                return;
            }
            if (observation.BaselinePulses > 0 || observation.BaselineThrows > 0)
            {
                _emit(new BackendEvent.Diagnostic(
                    $"firebase-live-baseline pulse={observation.BaselinePulses} throw={observation.BaselineThrows}"));
            }
            if (observation.StalePulses > 0 || observation.StaleThrows > 0)
            {
                _emit(new BackendEvent.Diagnostic(
                    $"firebase-live-stale pulse={observation.StalePulses} throw={observation.StaleThrows}"));
            }
            foreach (FirebaseRealtimeLiveAction action in actions)
            {
                switch (action)
                {
                    case FirebaseRealtimeLiveAction.Typing typing when typing.UserId != localUserId:
                        lock (_snapshotGate)
                        {
                            if (typing.Active)
                            {
                                _activeTypingUserIds.Add(typing.UserId);
                            }
                            else
                            {
                                _activeTypingUserIds.Remove(typing.UserId);
                            }
                        }
                        _emit(new BackendEvent.TypingChanged(roomId, typing.UserId, typing.Active));
                        break;
                    case FirebaseRealtimeLiveAction.Pulse pulse when pulse.UserId != localUserId:
                        _emit(new BackendEvent.Diagnostic("firebase-live-pulse result=accepted"));
                        _emit(new BackendEvent.CharacterPulsed(
                            new CharacterPulseEvent(Guid.NewGuid(), roomId, pulse.UserId)));
                        break;
                    case FirebaseRealtimeLiveAction.Pulse:
                        _emit(new BackendEvent.Diagnostic("firebase-live-pulse result=self-filtered"));
                        break;
                    case FirebaseRealtimeLiveAction.Throw characterThrow
                        when characterThrow.ActorUserId != localUserId
                            && characterThrow.ActorUserId != characterThrow.Payload.TargetUserId
                            && throwableCatalogItemIds.TryGetValue(
                                characterThrow.Payload.WireCode,
                                out string? throwableCatalogItemId):
                        _emit(new BackendEvent.Diagnostic("firebase-live-throw result=accepted"));
                        _emit(new BackendEvent.CharacterThrown(new CharacterThrowEvent(
                            Guid.NewGuid(),
                            roomId,
                            characterThrow.ActorUserId,
                            characterThrow.Payload.TargetUserId,
                            PixelCharacterCatalog.FallbackId,
                            throwableCatalogItemId)));
                        break;
                    case FirebaseRealtimeLiveAction.Throw characterThrow:
                        string reason = characterThrow.ActorUserId == localUserId
                            ? "self-filtered"
                            : characterThrow.ActorUserId == characterThrow.Payload.TargetUserId
                                ? "invalid-target"
                                : "unknown-wire-code";
                        _emit(new BackendEvent.Diagnostic($"firebase-live-throw result={reason}"));
                        break;
                }
            }
            ScheduleTypingExpiry(roomId, localUserId, generation, serverClock);
        }
    }

    private long ServerNowMilliseconds(FirebaseRealtimeServerClock? serverClock) =>
        serverClock?.NowMilliseconds() ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void ScheduleTypingExpiry(
        Guid roomId,
        Guid localUserId,
        long generation,
        FirebaseRealtimeServerClock? serverClock)
    {
        CancellationTokenSource? replacement = null;
        TimeSpan delay = TimeSpan.Zero;
        lock (_snapshotGate)
        {
            _typingExpiryCancellation?.Cancel();
            _typingExpiryCancellation?.Dispose();
            _typingExpiryCancellation = null;
            if (_liveReconciler.NextTypingExpiryMilliseconds is not { } deadline)
            {
                return;
            }
            long now = ServerNowMilliseconds(serverClock);
            delay = TimeSpan.FromMilliseconds(Math.Max(0, deadline - now));
            replacement = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _typingExpiryCancellation = replacement;
        }
        _ = ExpireTypingAfterDelayAsync(
            roomId,
            localUserId,
            generation,
            delay,
            replacement,
            serverClock);
    }

    private async Task ExpireTypingAfterDelayAsync(
        Guid roomId,
        Guid localUserId,
        long generation,
        TimeSpan delay,
        CancellationTokenSource cancellation,
        FirebaseRealtimeServerClock? serverClock)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
            IReadOnlyList<FirebaseRealtimeLiveAction.Typing> actions;
            lock (_snapshotGate)
            {
                if (generation != Volatile.Read(ref _generation)
                    || !ReferenceEquals(_typingExpiryCancellation, cancellation))
                {
                    return;
                }
                actions = _liveReconciler.ExpireTyping(
                    ServerNowMilliseconds(serverClock));
            }
            lock (_liveEmitGate)
            {
                if (_liveActionGeneration != generation)
                {
                    return;
                }
                foreach (FirebaseRealtimeLiveAction.Typing typing in actions)
                {
                    if (typing.UserId != localUserId)
                    {
                        lock (_snapshotGate)
                        {
                            _activeTypingUserIds.Remove(typing.UserId);
                        }
                        _emit(new BackendEvent.TypingChanged(roomId, typing.UserId, false));
                    }
                }
                ScheduleTypingExpiry(roomId, localUserId, generation, serverClock);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void ObserveChatEvent(
        Guid roomId,
        FirebaseRealtimeRoomEvent serverEvent,
        bool initialMutation,
        long receivedAtMilliseconds)
    {
        bool recheckMessage;
        bool invalidateMessages;
        lock (_snapshotGate)
        {
            bool baseline = !_chatSequences.ContainsKey(roomId) && initialMutation;
            bool hadSequence = _chatSequences.TryGetValue(roomId, out long previousSequence);
            bool advanced = AdvanceSequenceWithinLock(roomId, serverEvent.Sequence);
            bool unseen = !_liveChatEvents.TryGetValue(
                roomId, out (Guid MessageId, long Sequence) previousEvent)
                || serverEvent.Sequence > previousEvent.Sequence;
            if (unseen)
            {
                _liveChatEvents[roomId] = (serverEvent.MessageId, serverEvent.Sequence);
            }
            long age = receivedAtMilliseconds - serverEvent.Timestamp;
            recheckMessage = unseen && !initialMutation
                && age >= -FirebaseRealtimeLiveReconciler.FreshnessWindowMilliseconds
                && age <= ActiveBubbleLedger.DefaultLifetime.TotalMilliseconds;
            invalidateMessages = advanced && !baseline
                && (!recheckMessage
                    || (hadSequence && serverEvent.Sequence > previousSequence + 1));
        }
        if (recheckMessage)
        {
            _emit(new BackendEvent.MessageChanged(roomId, serverEvent.MessageId, "INSERT"));
        }
        if (invalidateMessages)
        {
            _emit(new BackendEvent.MessagesInvalidated(roomId));
        }
    }

    private bool AdvanceSequenceWithinLock(Guid roomId, long sequence)
    {
        if (_chatSequences.TryGetValue(roomId, out long current) && sequence <= current)
        {
            return false;
        }
        _chatSequences[roomId] = sequence;
        return true;
    }

    private async Task StopWithinGateAsync()
    {
        lock (_snapshotGate)
        {
            _typingExpiryCancellation?.Cancel();
            _typingExpiryCancellation?.Dispose();
            _typingExpiryCancellation = null;
        }
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _generationCancellation,
            null);
        if (cancellation is null)
        {
            SetReady(ready: false);
            ClearLiveState(_activeRoomId);
            return;
        }
        cancellation.Cancel();
        try
        {
            await _generationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            _generationTask = Task.CompletedTask;
            SetReady(ready: false);
            ClearLiveState(_activeRoomId);
        }
    }

    private void SetReady(bool ready)
    {
        int next = ready ? 1 : 0;
        if (Interlocked.Exchange(ref _ready, next) == next)
        {
            return;
        }
        _emit(new BackendEvent.ConnectionChanged(
            ready
                ? new RealtimeConnectionStatus(true, true, true)
                : RealtimeConnectionStatus.Disconnected));
    }

    private void BeginLiveGeneration(long generation)
    {
        lock (_liveEmitGate)
        {
            _liveActionGeneration = generation;
        }
    }

    private void BeginDisconnect(long generation)
    {
        lock (_liveEmitGate)
        {
            if (_liveActionGeneration == generation)
            {
                _liveActionGeneration = 0;
            }
            SetReady(ready: false);
        }
    }

    private void ClearLiveState(Guid? roomId)
    {
        Guid[] typingUserIds;
        lock (_snapshotGate)
        {
            _typingExpiryCancellation?.Cancel();
            _typingExpiryCancellation?.Dispose();
            _typingExpiryCancellation = null;
            typingUserIds = [.. _activeTypingUserIds.OrderBy(userId => userId)];
            _activeTypingUserIds.Clear();
            _liveReconciler = new FirebaseRealtimeLiveReconciler();
        }
        if (roomId is not { } id)
        {
            return;
        }
        foreach (Guid userId in typingUserIds)
        {
            _emit(new BackendEvent.TypingChanged(id, userId, false));
        }
    }

    private async Task RunReconnectRequestsAsync(CancellationTokenSource expectedGeneration)
    {
        // Let callers in the same burst share this task before touching the lifecycle.
        await Task.Yield();
        try
        {
            while (true)
            {
                await RestartAfterCancellationAsync(expectedGeneration).ConfigureAwait(false);
                lock (_reconnectGate)
                {
                    if (_rejectedGeneration is not { } rejected)
                    {
                        _reconnectPending = false;
                        return;
                    }
                    _rejectedGeneration = null;
                    expectedGeneration = rejected;
                }
            }
        }
        catch
        {
            lock (_reconnectGate)
            {
                _reconnectPending = false;
            }
            throw;
        }
    }

    private void EnsureCredentialNotRejected(FirebaseRealtimeCredential credential)
    {
        lock (_reconnectGate)
        {
            if (credential.Generation == _rejectedCredentialGeneration
                && credential.AuthVersion <= _rejectedAuthVersion)
            {
                throw new UnauthorizedAccessException("Firebase realtime credential was rejected before readiness.");
            }
        }
    }

    private async Task RestartAfterCancellationAsync(CancellationTokenSource expectedGeneration)
    {
        try
        {
            await _lifecycleGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_generationCancellation, expectedGeneration)
                    || _shutdown.IsCancellationRequested)
                {
                    return;
                }
                Guid? activeRoomId = _activeRoomId;
                await StopWithinGateAsync().ConfigureAwait(false);
                var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token);
                _generationCancellation = generationCancellation;
                long generation = Interlocked.Increment(ref _generation);
                _generationTask = RunGenerationAsync(
                    generation,
                    activeRoomId,
                    generationCancellation.Token);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static string FailureDiagnostic(Exception? exception) => exception switch
    {
        FirebaseRealtimeCredentialProtocolException credential =>
            $"kind=protocol stage=credential{DetailDiagnostic(credential.Detail)}",
        FirebaseRealtimeStreamEndedException ended =>
            $"kind=eof stream={ended.StreamKind} phase={ended.Phase}",
        FirebaseRealtimeStreamRequestException request =>
            $"kind={request.FailureKind.ToString().ToLowerInvariant()} stream={request.StreamKind} stage=request{StatusDiagnostic(request.StatusCode)}",
        FirebaseRealtimeStreamProtocolException protocol =>
            $"kind=protocol stream={protocol.StreamKind}",
        UnauthorizedAccessException => "kind=access-denied",
        FirebaseRtdbRequestException request =>
            $"kind={request.FailureKind.ToString().ToLowerInvariant()}",
        InvalidDataException => "kind=protocol",
        OperationCanceledException => "kind=canceled",
        null => "kind=eof",
        _ => "kind=transport",
    };

    private static string StatusDiagnostic(System.Net.HttpStatusCode? statusCode) =>
        statusCode is { } value ? $" status={(int)value}" : string.Empty;

    private static string DetailDiagnostic(string? detail) =>
        detail is not null ? $" detail={detail}" : string.Empty;

    private sealed class FirebaseRealtimeCredentialProtocolException(
        string? detail,
        Exception innerException) : Exception(
            "Firebase realtime credential payload is invalid.",
            innerException)
    {
        public string? Detail { get; } = detail;
    }

    private sealed class FirebaseRealtimeStreamProtocolException(
        string streamKind,
        InvalidDataException innerException) : Exception(
            "Firebase realtime stream payload is invalid.",
            innerException)
    {
        public string StreamKind { get; } = streamKind;
    }

    private sealed class FirebaseRealtimeStreamEndedException(
        string streamKind,
        string phase) : Exception(
        "Firebase realtime stream ended.")
    {
        public string StreamKind { get; } = streamKind;
        public string Phase { get; } = phase;
    }

    private sealed class FirebaseRealtimeStreamRequestException(
        string streamKind,
        FirebaseRtdbRequestException innerException) : Exception(
            "Firebase realtime stream request failed.",
            innerException)
    {
        public string StreamKind { get; } = streamKind;
        public FirebaseRtdbFailureKind FailureKind { get; } = innerException.FailureKind;
        public System.Net.HttpStatusCode? StatusCode { get; } = innerException.StatusCode;
    }

    private sealed class StreamSet : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _tasks = [];
        private readonly int _requiredReadyCount;
        private readonly TimeProvider _timeProvider;
        private readonly long _receivedTimestamp;
        private readonly TimeSpan _refreshAfter;
        private Task _completion = Task.CompletedTask;
        private Task _drained = Task.CompletedTask;
        private int _readyCount;
        private int _disposed;

        public StreamSet(
            FirebaseRtdbRestClient client,
            int requiredReadyCount,
            FirebaseRealtimeCredential credential,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            Client = client;
            Credential = credential;
            _requiredReadyCount = requiredReadyCount;
            _timeProvider = timeProvider;
            _receivedTimestamp = timeProvider.GetTimestamp();
            _refreshAfter = credential.RefreshAfter;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Expiry = Task.Delay(credential.ExpiresAfter, timeProvider, _cancellation.Token);
        }

        public FirebaseRtdbRestClient Client { get; }
        public FirebaseRealtimeCredential Credential { get; }
        public CancellationToken Token => _cancellation.Token;
        public Task Ready => _ready.Task;
        public Task Completion => _completion;
        public Task Expiry { get; }
        public bool IsUnavailable => Completion.IsCompleted || Expiry.IsCompletedSuccessfully;
        public TimeSpan RemainingUntilRefresh => TimeSpan.FromTicks(Math.Max(
            0, (_refreshAfter - _timeProvider.GetElapsedTime(_receivedTimestamp)).Ticks));
        public Exception? Failure => _completion.Exception?.GetBaseException();

        public void Add(Task task) => _tasks.Add(task);

        public void Seal()
        {
            _completion = Task.WhenAny(_tasks).Unwrap();
            _drained = Task.WhenAll(_tasks);
        }

        public void MarkReady()
        {
            if (Interlocked.Increment(ref _readyCount) == _requiredReadyCount)
            {
                _ready.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _cancellation.Cancel();
            try
            {
                await _drained.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException
                or FirebaseRtdbRequestException
                or UnauthorizedAccessException
                or FirebaseRealtimeCredentialProtocolException
                or FirebaseRealtimeStreamEndedException
                or FirebaseRealtimeStreamRequestException
                or FirebaseRealtimeStreamProtocolException
                or InvalidDataException)
            {
            }
            finally
            {
                await Client.DisposeAsync().ConfigureAwait(false);
                _cancellation.Dispose();
            }
        }
    }
}
