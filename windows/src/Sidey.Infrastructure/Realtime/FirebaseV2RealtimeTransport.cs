using System.Runtime.CompilerServices;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Infrastructure.Realtime;

/// <summary>
/// Mixed-version transport. Presence stays on the existing private Supabase socket while
/// Firebase v2 owns chat, typing, pulse, and projectile activity when selected.
/// </summary>
internal sealed class FirebaseV2RealtimeTransport : IRealtimeTransport
{
    private readonly IRealtimeTransport _legacy;
    private readonly IFirebaseRealtimeRolloutSelector _selector;
    private readonly IFirebaseRealtimeChatClient _chat;
    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly IFirebaseRealtimeListener _firebase;
    private readonly IFirebaseRealtimeTransientClient _transients;
    private readonly bool _ownsTransients;
    private readonly RealtimeEventQueue _events = new();
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly SemaphoreSlim _selectionWake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _stateGate = new();
    private RealtimeConnectionStatus? _lastEmittedConnectionStatus;
    private IReadOnlyDictionary<string, string> _throwableWireCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["patch_soft_ball"] = "0",
        };
    private static readonly TimeSpan s_selectorFailureRetry = TimeSpan.FromSeconds(5);
    private Task? _legacyPump;
    private Task? _selectionLoop;
    private Guid? _activeRoomId;
    private TimeSpan _selectionTtl = TimeSpan.FromMinutes(5);
    private int _firebaseEnabled;
    private int _selectorFailedClosed;
    private int _selectorRetrying;
    private int _sessionInvalidated;
    private int _disposed;
    private CancellationTokenSource? _firebaseWriteFence;

    public FirebaseV2RealtimeTransport(
        IRealtimeTransport legacy,
        IFirebaseRealtimeRolloutSelector selector,
        IFirebaseRealtimeChatClient chat,
        IFirebaseRealtimeCredentialProvider credentials,
        Func<Action<BackendEvent>, IFirebaseRealtimeListener> createFirebase,
        IFirebaseRealtimeTransientClient? transients = null)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transients = transients ?? new FirebaseRealtimeTransientClient(credentials);
        _ownsTransients = transients is null;
        ArgumentNullException.ThrowIfNull(createFirebase);
        _firebase = createFirebase(OnFirebaseEvent)
            ?? throw new InvalidOperationException("Firebase listener factory returned null.");
    }

    public RealtimeConnectionStatus ConnectionStatus
    {
        get
        {
            RealtimeConnectionStatus legacy = _legacy.ConnectionStatus;
            if (!UsesFirebaseChat)
            {
                return legacy;
            }
            bool ready = _firebase.IsReady && Volatile.Read(ref _selectorFailedClosed) == 0;
            return legacy with
            {
                ActiveRoomTransportConnected = legacy.ActiveRoomTransportConnected && ready,
                RecoveryReconciled = legacy.RecoveryReconciled && ready,
            };
        }
    }

    public bool IsRecoveryPaused =>
        _legacy.IsRecoveryPaused || (UsesFirebaseChat && Volatile.Read(ref _selectorFailedClosed) != 0);

    public bool UsesFirebaseChat => Volatile.Read(ref _firebaseEnabled) != 0;
    public bool RequiresThrowableWireCodes => true;

    public async IAsyncEnumerable<BackendEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureLegacyPumpStarted();
        await foreach (BackendEvent backendEvent in _events.ReadAllAsync(cancellationToken))
        {
            yield return backendEvent;
        }
    }

    public async Task SynchronizeAsync(
        IReadOnlyDictionary<Guid, long> roomEpochs,
        Guid? activeRoomId,
        PresenceState localPresence,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _sessionInvalidated) != 0,
            this);
        ArgumentNullException.ThrowIfNull(roomEpochs);
        Guid? nextActiveRoomId = activeRoomId is { } id && roomEpochs.ContainsKey(id) ? id : null;
        lock (_stateGate)
        {
            if (_activeRoomId != nextActiveRoomId)
            {
                CancelFirebaseWriteFenceWithinLock();
            }
            _activeRoomId = nextActiveRoomId;
        }
        await _legacy.SynchronizeAsync(
            roomEpochs,
            _activeRoomId,
            localPresence,
            cancellationToken).ConfigureAwait(false);
        await RefreshSelectionAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        EnsureSelectionLoopStarted();
    }

    public Task PublishPresenceAsync(
        Guid roomId,
        PresenceState state,
        CancellationToken cancellationToken) =>
        _legacy.PublishPresenceAsync(roomId, state, cancellationToken);

    public async Task<FirebaseRealtimeChatResult?> PublishChatAsync(
        Guid messageId,
        Guid roomId,
        string body,
        CancellationToken cancellationToken)
    {
        EmitSendDiagnostic("chat", "started");
        try
        {
            FirebaseRealtimeChatResult? result = await PublishChatCoreAsync(
                messageId, roomId, body, cancellationToken).ConfigureAwait(false);
            EmitSendDiagnostic("chat", "committed");
            return result;
        }
        catch (Exception exception)
        {
            EmitSendFailure("chat", exception);
            throw;
        }
    }

    private async Task<FirebaseRealtimeChatResult?> PublishChatCoreAsync(
        Guid messageId,
        Guid roomId,
        string body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _sessionInvalidated) != 0,
            this);
        using FirebaseWriteOperation operation = CreateFirebaseWriteOperation(
            roomId,
            "Firebase realtime chat is not ready.",
            cancellationToken,
            localChatNotReady: true);
        try
        {
            return await _chat.SendAsync(
                messageId,
                roomId,
                body,
                operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && !_shutdown.IsCancellationRequested
            && operation.FenceToken.IsCancellationRequested)
        {
            throw new FirebaseRealtimeChatException(
                "transport-canceled",
                FirebaseRealtimeChatFailureClassification.CommitAmbiguous);
        }
    }

    public Task PublishTypingAsync(
        Guid roomId,
        bool active,
        CancellationToken cancellationToken) =>
        PublishTransientAsync(
            roomId,
            token => _transients.PublishTypingAsync(roomId, active, token),
            cancellationToken);

    public Task PublishCharacterPulseAsync(
        Guid roomId,
        CancellationToken cancellationToken) =>
        PublishTransientAsync(
            roomId,
            token => _transients.PublishCharacterPulseAsync(roomId, token),
            cancellationToken);

    public Task PublishCharacterThrowAsync(
        Guid roomId,
        Guid targetUserId,
        string throwableCatalogItemId,
        CancellationToken cancellationToken)
    {
        string wireCode;
        lock (_stateGate)
        {
            if (!_throwableWireCodes.TryGetValue(throwableCatalogItemId, out wireCode!))
            {
                throw new InvalidOperationException("The equipped throwable has no Firebase wire code.");
            }
        }
        return PublishTransientAsync(
            roomId,
            token => _transients.PublishCharacterThrowAsync(
                roomId,
                targetUserId,
                wireCode,
                token),
            cancellationToken,
            diagnosticKind: "throw");
    }

    public void ConfigureThrowableWireCodes(
        IReadOnlyDictionary<string, string> wireCodesByCatalogItemId)
    {
        ArgumentNullException.ThrowIfNull(wireCodesByCatalogItemId);
        Dictionary<string, string> outbound = new(StringComparer.Ordinal)
        {
            ["patch_soft_ball"] = "0",
        };
        Dictionary<string, string> inbound = new(StringComparer.Ordinal)
        {
            ["0"] = "patch_soft_ball",
        };
        foreach ((string catalogItemId, string wireCode) in wireCodesByCatalogItemId)
        {
            if (string.IsNullOrWhiteSpace(catalogItemId)
                || !IsCanonicalWireCode(wireCode)
                || !outbound.TryAdd(catalogItemId, wireCode)
                || !inbound.TryAdd(wireCode, catalogItemId))
            {
                throw new InvalidDataException("Firebase throwable wire mapping is invalid.");
            }
        }
        lock (_stateGate)
        {
            _throwableWireCodes = outbound;
        }
        _firebase.ConfigureThrowableWireCodes(inbound);
    }

    public async Task ConvergeGrantAsync(
        string minimumAccessRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _sessionInvalidated) != 0,
            this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            _ = await _credentials.ConvergeAsync(
                minimumAccessRevision,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            Volatile.Write(ref _selectorFailedClosed, 1);
            CancelFirebaseWriteFence();
            await _firebase.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Emit(new BackendEvent.Diagnostic(
                "firebase-grant-convergence-failed mode=fail-closed"));
            EmitConnectionStatus();
            SignalSelectionRefresh();
            return;
        }
        if (UsesFirebaseChat && Volatile.Read(ref _selectorFailedClosed) == 0)
        {
            _firebase.RequestReconnect();
        }
    }

    public async ValueTask InvalidateSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sessionInvalidated, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        CancelFirebaseWriteFence();
        await _credentials.ResetAsync(cancellationToken).ConfigureAwait(false);
        await _firebase.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RunWhileConnectedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _sessionInvalidated) != 0)
        {
            return Task.FromResult(false);
        }
        if (UsesFirebaseChat && (!_firebase.IsReady || Volatile.Read(ref _selectorFailedClosed) != 0))
        {
            return Task.FromResult(false);
        }
        return _legacy.RunWhileConnectedAsync(operation, cancellationToken);
    }

    public void RequestReconnect(bool userInitiated = false)
    {
        _legacy.RequestReconnect(userInitiated);
        if (UsesFirebaseChat)
        {
            _firebase.RequestReconnect();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        await _firebase.DisposeAsync().ConfigureAwait(false);
        await _legacy.DisposeAsync().ConfigureAwait(false);
        if (_legacyPump is not null)
        {
            await IgnoreCancellationAsync(_legacyPump).ConfigureAwait(false);
        }
        if (_selectionLoop is not null)
        {
            await IgnoreCancellationAsync(_selectionLoop).ConfigureAwait(false);
        }
        if (_ownsTransients && _transients is IDisposable disposableTransients)
        {
            disposableTransients.Dispose();
        }
        _events.Complete();
        lock (_stateGate)
        {
            CancelFirebaseWriteFenceWithinLock();
        }
        _shutdown.Dispose();
        _selectionGate.Dispose();
        _selectionWake.Dispose();
    }

    private void EnsureLegacyPumpStarted()
    {
        lock (_stateGate)
        {
            _legacyPump ??= PumpLegacyAsync(_shutdown.Token);
        }
    }

    private void EnsureSelectionLoopStarted()
    {
        lock (_stateGate)
        {
            _selectionLoop ??= RefreshSelectionLoopAsync(_shutdown.Token);
        }
    }

    private async Task PumpLegacyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BackendEvent backendEvent in _legacy.ReadEventsAsync(cancellationToken))
            {
                if (UsesFirebaseChat && backendEvent is BackendEvent.CharacterPulsed)
                {
                    Emit(new BackendEvent.Diagnostic("legacy-live-pulse result=firebase-selected"));
                    continue;
                }
                if (UsesFirebaseChat && backendEvent is BackendEvent.CharacterThrown)
                {
                    Emit(new BackendEvent.Diagnostic("legacy-live-throw result=firebase-selected"));
                    continue;
                }
                if (UsesFirebaseChat && backendEvent is BackendEvent.TypingChanged)
                {
                    continue;
                }
                if (backendEvent is BackendEvent.ConnectionChanged)
                {
                    EmitConnectionStatus();
                    continue;
                }
                Emit(backendEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSelectionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan lead = Volatile.Read(ref _selectorRetrying) != 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(
                    TimeSpan.FromSeconds(30).Ticks,
                    _selectionTtl.Ticks / 2));
                TimeSpan delay = _selectionTtl - lead;
                await WaitForSelectionRefreshAsync(delay, cancellationToken).ConfigureAwait(false);
                await RefreshSelectionAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSelectionAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _sessionInvalidated) != 0)
        {
            return;
        }
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _sessionInvalidated) != 0)
            {
                return;
            }
            FirebaseRealtimeRolloutSelection selection;
            try
            {
                selection = forceRefresh
                    ? await _selector.RefreshAsync(cancellationToken).ConfigureAwait(false)
                    : await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                EmitSelectorFailure(exception as FirebaseRealtimeRolloutException);
                _selectionTtl = exception is FirebaseRealtimeRolloutException selectionFailure
                    ? selectionFailure.RetryDelay ?? selectionFailure.RetryAfter ?? s_selectorFailureRetry
                    : s_selectorFailureRetry;
                Volatile.Write(ref _selectorRetrying, 1);
                if (UsesFirebaseChat)
                {
                    Volatile.Write(ref _selectorFailedClosed, 1);
                    CancelFirebaseWriteFence();
                    await _firebase.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    Emit(new BackendEvent.Diagnostic("firebase-selector-failed mode=fail-closed"));
                    EmitConnectionStatus();
                    return;
                }
                Emit(new BackendEvent.Diagnostic("firebase-selector-failed mode=legacy-not-enabled"));
                return;
            }

            if (selection.Failure is { } failure)
                EmitSelectorFailure(failure);
            Volatile.Write(ref _selectorRetrying, selection.Failure is null ? 0 : 1);
            _selectionTtl = selection.CacheTtl;
            bool enabled = selection.Enabled
                && !selection.KillSwitch
                && selection.Transport == FirebaseRealtimeSelectedTransport.FirebaseV2;
            if (!enabled)
            {
                Volatile.Write(ref _firebaseEnabled, 0);
                Volatile.Write(ref _selectorFailedClosed, 0);
                CancelFirebaseWriteFence();
                await _firebase.StopAsync(cancellationToken).ConfigureAwait(false);
                Emit(new BackendEvent.Diagnostic(
                    $"firebase-selector transport=legacy source={selection.Source}"));
                EmitConnectionStatus();
                return;
            }

            Volatile.Write(ref _firebaseEnabled, 1);
            Volatile.Write(ref _selectorFailedClosed, 0);
            // The listener renews its actual credential lease with overlapping streams.
            // An unchanged selector must not tear down healthy streams or in-flight writes.
            await _firebase.StartAsync(_activeRoomId, cancellationToken).ConfigureAwait(false);
            Emit(new BackendEvent.Diagnostic(
                $"firebase-selector transport=v2 source={selection.Source}"));
            EmitConnectionStatus();
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private void OnFirebaseEvent(BackendEvent backendEvent)
    {
        if (backendEvent is BackendEvent.ConnectionChanged firebaseConnection)
        {
            if (firebaseConnection.Status.IsReady
                && UsesFirebaseChat
                && Volatile.Read(ref _selectorFailedClosed) == 0)
            {
                ArmFirebaseWriteFence();
            }
            else
            {
                CancelFirebaseWriteFence();
            }
            bool connectionChanged = EmitConnectionStatus();
            if (firebaseConnection.Status.IsReady
                && !connectionChanged
                && ConnectionStatus.TransportConnected)
            {
                Emit(new BackendEvent.ReconciliationRequired());
            }
            return;
        }
        if (Volatile.Read(ref _selectorFailedClosed) != 0
            && backendEvent is not BackendEvent.Diagnostic
            && backendEvent is not BackendEvent.TypingChanged { Active: false })
        {
            return;
        }
        Emit(backendEvent);
    }

    private void Emit(BackendEvent backendEvent) => _events.TryWrite(backendEvent);

    private bool EmitConnectionStatus()
    {
        lock (_stateGate)
        {
            RealtimeConnectionStatus status = ConnectionStatus;
            if (_lastEmittedConnectionStatus == status)
            {
                return false;
            }
            // Applying a reconciled snapshot synchronizes rooms again. Re-emitting an
            // unchanged status here would request another reconciliation indefinitely.
            _lastEmittedConnectionStatus = status;
            Emit(new BackendEvent.ConnectionChanged(status));
            return true;
        }
    }

    private async Task PublishTransientAsync(
        Guid roomId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string? diagnosticKind = null)
    {
        if (diagnosticKind is not null)
        {
            EmitSendDiagnostic(diagnosticKind, "started");
        }
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _sessionInvalidated) != 0,
                this);
            using FirebaseWriteOperation writeOperation = CreateFirebaseWriteOperation(
                roomId,
                "Firebase realtime transient transport is not ready.",
                cancellationToken);
            await operation(writeOperation.Token).ConfigureAwait(false);
            if (diagnosticKind is not null)
            {
                // RTDB accepting a write does not acknowledge another client's rendering.
                EmitSendDiagnostic(diagnosticKind, "accepted");
            }
        }
        catch (Exception exception)
        {
            if (diagnosticKind is not null)
            {
                EmitSendFailure(diagnosticKind, exception);
            }
            throw;
        }
    }

    private void EmitSelectorFailure(FirebaseRealtimeRolloutException? failure)
    {
        string status = failure?.StatusCode is { } code ? ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        string reason = failure?.FailureCode ?? "selection-failed";
        string retry = failure?.RetryAfter is { } delay
            ? Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        Emit(new BackendEvent.Diagnostic(
            $"firebase-selector-error status={status} code={reason} retry-seconds={retry}"));
    }

    private void EmitSendDiagnostic(string kind, string result) =>
        Emit(new BackendEvent.Diagnostic($"firebase-send kind={kind} result={result}"));

    private void EmitSendFailure(string kind, Exception exception)
    {
        string reason = exception switch
        {
            ChatNotReadyException => "not-ready",
            FirebaseRealtimeChatException
            { Classification: FirebaseRealtimeChatFailureClassification.CommitAmbiguous } => "commit-ambiguous",
            FirebaseRealtimeChatException => "rejected",
            FirebaseRtdbRequestException request => request.FailureKind.ToString().ToLowerInvariant(),
            OperationCanceledException => "canceled",
            InvalidOperationException => "not-ready-or-rejected",
            _ => "transport",
        };
        // Only bounded local categories: never body, identifiers, token, URL or server errors.
        Emit(new BackendEvent.Diagnostic($"firebase-send kind={kind} result=failed reason={reason}"));
    }

    private FirebaseWriteOperation CreateFirebaseWriteOperation(
        Guid roomId,
        string notReadyMessage,
        CancellationToken cancellationToken,
        bool localChatNotReady = false)
    {
        lock (_stateGate)
        {
            if (!UsesFirebaseChat
                || Volatile.Read(ref _selectorFailedClosed) != 0
                || !_firebase.IsReady
                || _activeRoomId != roomId
                || _firebaseWriteFence is null
                || _firebaseWriteFence.IsCancellationRequested)
            {
                if (localChatNotReady)
                    throw new ChatNotReadyException();
                throw new InvalidOperationException(notReadyMessage);
            }
            CancellationToken fenceToken = _firebaseWriteFence.Token;
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token,
                fenceToken);
            return new FirebaseWriteOperation(linkedCancellation, fenceToken);
        }
    }

    private void ArmFirebaseWriteFence()
    {
        lock (_stateGate)
        {
            CancelFirebaseWriteFenceWithinLock();
            _firebaseWriteFence = new CancellationTokenSource();
        }
    }

    private void CancelFirebaseWriteFence()
    {
        lock (_stateGate)
        {
            CancelFirebaseWriteFenceWithinLock();
        }
    }

    private void CancelFirebaseWriteFenceWithinLock()
    {
        CancellationTokenSource? fence = _firebaseWriteFence;
        _firebaseWriteFence = null;
        if (fence is null)
        {
            return;
        }
        fence.Cancel();
        fence.Dispose();
    }

    private void SignalSelectionRefresh()
    {
        try
        {
            _selectionWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task WaitForSelectionRefreshAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task timer = FirebaseRealtimeRetryPolicy.DelayAsync(delay, TimeProvider.System, waitCancellation.Token);
        Task wake = _selectionWake.WaitAsync(waitCancellation.Token);
        Task completed = await Task.WhenAny(timer, wake).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        waitCancellation.Cancel();
    }

    private static bool IsCanonicalWireCode(string value) =>
        value.Length is > 0 and <= 6
        && (value == "0"
            || value[0] is >= '1' and <= '9'
                && value.All(character => character is >= '0' and <= '9'));

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FirebaseWriteOperation(
        CancellationTokenSource cancellation,
        CancellationToken fenceToken) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;
        public CancellationToken FenceToken { get; } = fenceToken;

        public void Dispose() => cancellation.Dispose();
    }
}
