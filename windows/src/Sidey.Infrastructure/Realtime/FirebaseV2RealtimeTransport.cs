using System.Runtime.CompilerServices;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Infrastructure.Realtime;

/// <summary>
/// Mixed-version transport. Presence and transient activity stay on the existing private
/// Supabase socket while the server-side selector owns activation of Firebase chat/hints.
/// </summary>
internal sealed class FirebaseV2RealtimeTransport : IRealtimeTransport
{
    private readonly IRealtimeTransport _legacy;
    private readonly IFirebaseRealtimeRolloutSelector _selector;
    private readonly IFirebaseRealtimeChatClient _chat;
    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly IFirebaseRealtimeListener _firebase;
    private readonly RealtimeEventQueue _events = new();
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _stateGate = new();
    private static readonly TimeSpan s_selectorFailureRetry = TimeSpan.FromSeconds(30);
    private Task? _legacyPump;
    private Task? _selectionLoop;
    private Guid? _activeRoomId;
    private TimeSpan _selectionTtl = TimeSpan.FromMinutes(5);
    private int _firebaseEnabled;
    private int _selectorFailedClosed;
    private int _sessionInvalidated;
    private int _disposed;

    public FirebaseV2RealtimeTransport(
        IRealtimeTransport legacy,
        IFirebaseRealtimeRolloutSelector selector,
        IFirebaseRealtimeChatClient chat,
        IFirebaseRealtimeCredentialProvider credentials,
        Func<Action<BackendEvent>, IFirebaseRealtimeListener> createFirebase)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
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
        _activeRoomId = activeRoomId is { } id && roomEpochs.ContainsKey(id) ? id : null;
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
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _sessionInvalidated) != 0,
            this);
        if (!UsesFirebaseChat || Volatile.Read(ref _selectorFailedClosed) != 0)
        {
            throw new InvalidOperationException("Firebase realtime chat is not active.");
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        return await _chat.SendAsync(
            messageId,
            roomId,
            body,
            linkedCancellation.Token).ConfigureAwait(false);
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
            await _firebase.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Emit(new BackendEvent.Diagnostic(
                "firebase-grant-convergence-failed mode=fail-closed"));
            Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
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
        _events.Complete();
        _shutdown.Dispose();
        _selectionGate.Dispose();
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
                if (UsesFirebaseChat
                    && backendEvent is BackendEvent.MessageChanged or BackendEvent.MessagesInvalidated)
                {
                    continue;
                }
                if (backendEvent is BackendEvent.ConnectionChanged)
                {
                    Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
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
                var lead = TimeSpan.FromTicks(Math.Min(
                    TimeSpan.FromSeconds(30).Ticks,
                    _selectionTtl.Ticks / 2));
                TimeSpan delay = _selectionTtl - lead;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
                if (UsesFirebaseChat)
                {
                    _selectionTtl = s_selectorFailureRetry;
                    Volatile.Write(ref _selectorFailedClosed, 1);
                    Emit(new BackendEvent.Diagnostic("firebase-selector-failed mode=fail-closed"));
                    Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
                    return;
                }
                Emit(new BackendEvent.Diagnostic("firebase-selector-failed mode=legacy-not-enabled"));
                return;
            }

            _selectionTtl = selection.CacheTtl;
            bool enabled = selection.Enabled
                && !selection.KillSwitch
                && selection.Transport == FirebaseRealtimeSelectedTransport.FirebaseV2;
            if (!enabled)
            {
                Volatile.Write(ref _firebaseEnabled, 0);
                Volatile.Write(ref _selectorFailedClosed, 0);
                await _firebase.StopAsync(cancellationToken).ConfigureAwait(false);
                Emit(new BackendEvent.Diagnostic(
                    $"firebase-selector transport=legacy source={selection.Source}"));
                Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
                return;
            }

            bool wasEnabled = UsesFirebaseChat;
            Volatile.Write(ref _firebaseEnabled, 1);
            Volatile.Write(ref _selectorFailedClosed, 0);
            await _firebase.StartAsync(_activeRoomId, cancellationToken).ConfigureAwait(false);
            if (forceRefresh && wasEnabled)
            {
                _firebase.RequestReconnect();
            }
            Emit(new BackendEvent.Diagnostic(
                $"firebase-selector transport=v2 source={selection.Source}"));
            Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private void OnFirebaseEvent(BackendEvent backendEvent)
    {
        Emit(backendEvent);
        if (backendEvent is BackendEvent.Diagnostic diagnostic
            && diagnostic.Stage.StartsWith("firebase-listener-ready", StringComparison.Ordinal))
        {
            Emit(new BackendEvent.ConnectionChanged(ConnectionStatus));
            Emit(new BackendEvent.ReconciliationRequired());
        }
    }

    private void Emit(BackendEvent backendEvent) => _events.TryWrite(backendEvent);

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
}
