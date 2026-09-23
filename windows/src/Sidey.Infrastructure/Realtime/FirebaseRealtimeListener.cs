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
    // The selector is renewed at lease TTL - 30 seconds. Rotate shortly afterwards so
    // GetCredentialAsync re-bootstraps, while leaving enough overlap before the old lease ends.
    private static readonly TimeSpan s_rotationInterval = TimeSpan.FromSeconds(275);
    private static readonly TimeSpan s_rotationDeadline = TimeSpan.FromSeconds(20);

    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly Func<Uri, FirebaseRtdbRestClient> _createClient;
    private readonly Action<BackendEvent> _emit;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _snapshotGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _generationCancellation;
    private Task _generationTask = Task.CompletedTask;
    private Guid? _activeRoomId;
    private string? _accessRevision;
    private readonly Dictionary<Guid, string> _roomRevisions = [];
    private readonly Dictionary<Guid, long> _chatSequences = [];
    private FirebaseRealtimeLiveReconciler _liveReconciler = new();
    private IReadOnlyDictionary<string, string> _throwableCatalogItemIdsByWireCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = "patch_soft_ball",
        };
    private CancellationTokenSource? _typingExpiryCancellation;
    private int _ready;
    private long _generation;

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

    public void RequestReconnect()
    {
        CancellationTokenSource? current = Volatile.Read(ref _generationCancellation);
        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _ = RestartAfterCancellationAsync();
    }

    public async ValueTask DisposeAsync()
    {
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

        _shutdown.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task RunGenerationAsync(
        long generation,
        Guid? activeRoomId,
        CancellationToken cancellationToken)
    {
        StreamSet? current = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                StreamSet? next = null;
                try
                {
                    next = await OpenStreamSetAsync(
                        generation,
                        activeRoomId,
                        cancellationToken).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(s_rotationDeadline);
                    Task startup = await Task.WhenAny(next.Ready, next.Completion)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (startup == next.Completion)
                    {
                        await next.Completion.ConfigureAwait(false);
                        throw new InvalidDataException(
                            "Firebase realtime stream ended before its initial snapshot.");
                    }
                    await next.Ready.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (next is not null)
                    {
                        await next.DisposeAsync().ConfigureAwait(false);
                    }
                    _emit(new BackendEvent.Diagnostic(
                        $"firebase-listener-connect-failed kind={FailureKind(exception)}"));
                    await DelayForReconnectAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                StreamSet? previous = current;
                current = next;
                Volatile.Write(ref _ready, 1);
                _emit(new BackendEvent.Diagnostic(
                    $"firebase-listener-ready streams={(activeRoomId.HasValue ? 2 : 1)} generation={generation}"));
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                var rotation = Task.Delay(s_rotationInterval, cancellationToken);
                Task completed = await Task.WhenAny(current.Completion, rotation).ConfigureAwait(false);
                if (completed == rotation && !cancellationToken.IsCancellationRequested)
                {
                    // Open the replacement before closing the old set. With an active room this
                    // creates at most four streams (old/new inbox + room) during token rotation.
                    continue;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    Exception? failure = current.Failure;
                    _emit(new BackendEvent.Diagnostic(
                        $"firebase-listener-disconnected kind={FailureKind(failure)}"));
                    Volatile.Write(ref _ready, 0);
                    await current.DisposeAsync().ConfigureAwait(false);
                    current = null;
                    await DelayForReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<StreamSet> OpenStreamSetAsync(
        long generation,
        Guid? activeRoomId,
        CancellationToken cancellationToken)
    {
        FirebaseRealtimeCredential credential =
            await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        FirebaseRtdbRestClient client = _createClient(credential.DatabaseUrl);
        var streamSet = new StreamSet(client, activeRoomId.HasValue ? 2 : 1, cancellationToken);
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
        await using FirebaseRtdbEventStream stream = await streamSet.Client.OpenEventStreamAsync(
            path,
            credential.IdToken,
            linked.Token).ConfigureAwait(false);
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
                snapshot.Apply(firebaseEvent.GetMutation());
                ProcessSnapshot(
                    roomId,
                    snapshot.Value,
                    initialMutation,
                    credential.UserId,
                    generation);
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
    }

    private void ProcessSnapshot(
        Guid? roomId,
        JsonNode? value,
        bool initialMutation,
        Guid localUserId,
        long generation)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null");
        if (roomId is { } activeRoomId)
        {
            FirebaseRealtimeRoomPayload payload = FirebaseRealtimeProtocol.ParseRoomPayload(utf8);
            ProcessLiveActions(activeRoomId, localUserId, payload, generation);
            if (payload.ServerEvent is { } serverEvent)
            {
                ObserveChatSequence(activeRoomId, serverEvent.Sequence, initialMutation);
            }
            return;
        }

        FirebaseRealtimeInboxPayload inbox = FirebaseRealtimeProtocol.ParseInboxPayload(utf8);
        List<BackendEvent> pending = [];
        lock (_snapshotGate)
        {
            bool baseline = _accessRevision is null && initialMutation;
            if (_accessRevision is not null
                && StringComparer.Ordinal.Compare(inbox.AccessRevision, _accessRevision) > 0
                && !baseline)
            {
                pending.Add(new BackendEvent.ReconciliationRequired());
            }
            if (_accessRevision is null
                || StringComparer.Ordinal.Compare(inbox.AccessRevision, _accessRevision) > 0)
            {
                _accessRevision = inbox.AccessRevision;
            }

            foreach ((Guid id, FirebaseRealtimeInboxRoom room) in inbox.Rooms)
            {
                if (_roomRevisions.TryGetValue(id, out string? revision)
                    && StringComparer.Ordinal.Compare(room.Revision, revision) > 0
                    && !baseline)
                {
                    pending.Add(new BackendEvent.RoomStructureChanged(id));
                }
                if (!_roomRevisions.TryGetValue(id, out revision)
                    || StringComparer.Ordinal.Compare(room.Revision, revision) > 0)
                {
                    _roomRevisions[id] = room.Revision;
                }

                if (AdvanceSequenceWithinLock(id, room.ChatSequence) && !baseline)
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
        long generation)
    {
        IReadOnlyList<FirebaseRealtimeLiveAction> actions;
        IReadOnlyDictionary<string, string> throwableCatalogItemIds;
        lock (_snapshotGate)
        {
            actions = _liveReconciler.Consume(
                payload,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            throwableCatalogItemIds = _throwableCatalogItemIdsByWireCode;
        }
        foreach (FirebaseRealtimeLiveAction action in actions)
        {
            switch (action)
            {
                case FirebaseRealtimeLiveAction.Typing typing when typing.UserId != localUserId:
                    _emit(new BackendEvent.TypingChanged(roomId, typing.UserId, typing.Active));
                    break;
                case FirebaseRealtimeLiveAction.Pulse pulse when pulse.UserId != localUserId:
                    _emit(new BackendEvent.CharacterPulsed(
                        new CharacterPulseEvent(Guid.NewGuid(), roomId, pulse.UserId)));
                    break;
                case FirebaseRealtimeLiveAction.Throw characterThrow
                    when characterThrow.ActorUserId != localUserId
                        && characterThrow.ActorUserId != characterThrow.Payload.TargetUserId
                        && throwableCatalogItemIds.TryGetValue(
                            characterThrow.Payload.WireCode,
                            out string? throwableCatalogItemId):
                    _emit(new BackendEvent.CharacterThrown(new CharacterThrowEvent(
                        Guid.NewGuid(),
                        roomId,
                        characterThrow.ActorUserId,
                        characterThrow.Payload.TargetUserId,
                        PixelCharacterCatalog.FallbackId,
                        throwableCatalogItemId)));
                    break;
            }
        }
        ScheduleTypingExpiry(roomId, localUserId, generation);
    }

    private void ScheduleTypingExpiry(Guid roomId, Guid localUserId, long generation)
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
            long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            delay = TimeSpan.FromMilliseconds(Math.Max(0, deadline - now));
            replacement = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _typingExpiryCancellation = replacement;
        }
        _ = ExpireTypingAfterDelayAsync(
            roomId,
            localUserId,
            generation,
            delay,
            replacement);
    }

    private async Task ExpireTypingAfterDelayAsync(
        Guid roomId,
        Guid localUserId,
        long generation,
        TimeSpan delay,
        CancellationTokenSource cancellation)
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
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            }
            foreach (FirebaseRealtimeLiveAction.Typing typing in actions)
            {
                if (typing.UserId != localUserId)
                {
                    _emit(new BackendEvent.TypingChanged(roomId, typing.UserId, false));
                }
            }
            ScheduleTypingExpiry(roomId, localUserId, generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void ObserveChatSequence(Guid roomId, long sequence, bool initialMutation)
    {
        bool emit;
        lock (_snapshotGate)
        {
            bool baseline = !_chatSequences.ContainsKey(roomId) && initialMutation;
            emit = AdvanceSequenceWithinLock(roomId, sequence) && !baseline;
        }
        if (emit)
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
            Volatile.Write(ref _ready, 0);
        }
    }

    private async Task RestartAfterCancellationAsync()
    {
        try
        {
            await _lifecycleGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
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

    private static async Task DelayForReconnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    private static string FailureKind(Exception? exception) => exception switch
    {
        UnauthorizedAccessException => "access-denied",
        FirebaseRtdbRequestException request => request.FailureKind.ToString().ToLowerInvariant(),
        InvalidDataException => "protocol",
        OperationCanceledException => "canceled",
        null => "eof",
        _ => "transport",
    };

    private sealed class StreamSet : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _tasks = [];
        private readonly int _requiredReadyCount;
        private Task _completion = Task.CompletedTask;
        private Task _drained = Task.CompletedTask;
        private int _readyCount;
        private int _disposed;

        public StreamSet(
            FirebaseRtdbRestClient client,
            int requiredReadyCount,
            CancellationToken cancellationToken)
        {
            Client = client;
            _requiredReadyCount = requiredReadyCount;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public FirebaseRtdbRestClient Client { get; }
        public CancellationToken Token => _cancellation.Token;
        public Task Ready => _ready.Task;
        public Task Completion => _completion;
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
