using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Realtime;

namespace Sidey.Infrastructure.Realtime;

internal sealed record RealtimePresenceIntent(
    IReadOnlyDictionary<Guid, long> RoomEpochs,
    Guid? ActiveRoomId,
    PresenceState LocalPresence);

internal sealed class SupabaseRealtimeTransport : IRealtimeTransport
{
    private static readonly TimeSpan s_unhealthyAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_authorizationRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SupabaseRuntimeConfiguration _configuration;
    private readonly IAuthSessionAccessor _sessions;
    private readonly Func<Uri, CancellationToken, Task<ClientWebSocket>> _connectSocket;
    private readonly RealtimeEventQueue _events = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CoalescingPublicationQueue<RealtimePresenceIntent> _presenceQueue;
    private readonly ExpiringLeaseRegistry<(Guid RoomId, Guid UserId)> _typingExpiries;
    private readonly INetworkAvailabilityMonitor _networkMonitor;
    private readonly TimeSpan _watchdogInterval;
    private readonly TimeSpan _authorizationRefreshInterval;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingReplies = [];
    private readonly ConcurrentDictionary<string, string> _joinReferences = [];
    private readonly ConcurrentDictionary<Guid, IReadOnlySet<Guid>> _presentUsersByRoom = [];
    private readonly Lock _recoveryGate = new();
    private IReadOnlyDictionary<Guid, long> _desiredRoomEpochs = new Dictionary<Guid, long>();
    private Guid? _activeRoomId;
    private PresenceState _localPresence = PresenceState.Online;
    private RealtimeSocketSession? _socketSession;
    private Task? _watchdogTask;
    private Task _recoveryTask = Task.CompletedTask;
    private CancellationTokenSource? _recoveryCancellation;
    private long _reference;
    private long _connectionGeneration;
    private long _lastReceiveTimestamp = Stopwatch.GetTimestamp();
    private long _lastConnectionHealthTimestamp = Stopwatch.GetTimestamp();
    private long _lastAuthorizationRefreshTimestamp = Stopwatch.GetTimestamp();
    private int _networkAvailable;
    private RealtimeConnectionStatus _lastEmittedConnectionStatus =
        RealtimeConnectionStatus.Disconnected;
    private int _recovering;
    private int _hasSynchronized;
    private int _recoveryPaused;
    private int _authorizationFailures;
    private long _lastRecoveryHint;
    private string? _socketAccessToken;
    private long _socketAccessTokenExpiresAtUtcTicks;

    public SupabaseRealtimeTransport(
        SupabaseRuntimeConfiguration configuration,
        IAuthSessionAccessor sessions,
        INetworkAvailabilityMonitor? networkMonitor = null,
        Func<Uri, CancellationToken, Task<ClientWebSocket>>? connectSocket = null,
        TimeSpan? watchdogInterval = null,
        TimeSpan? authorizationRefreshInterval = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _connectSocket = connectSocket ?? ConnectSocketAsync;
        _presenceQueue = new CoalescingPublicationQueue<RealtimePresenceIntent>(PublishPresenceBatchAsync);
        _typingExpiries = new ExpiringLeaseRegistry<(Guid RoomId, Guid UserId)>(
            TypingLease.RemoteExpiry,
            key => Emit(new BackendEvent.TypingChanged(key.RoomId, key.UserId, false)));
        _networkMonitor = networkMonitor ?? new SystemNetworkAvailabilityMonitor();
        _watchdogInterval = watchdogInterval ?? RealtimeRecoveryPolicy.WatchdogInterval;
        _authorizationRefreshInterval = authorizationRefreshInterval ?? s_authorizationRefreshInterval;
        _networkAvailable = _networkMonitor.IsAvailable ? 1 : 0;
        _networkMonitor.AvailabilityChanged += OnNetworkAvailabilityChanged;
        _networkMonitor.PathChanged += OnNetworkPathChanged;
        _networkMonitor.Start();
    }

    public RealtimeConnectionStatus ConnectionStatus =>
        Volatile.Read(ref _lastEmittedConnectionStatus);
    public bool IsRecoveryPaused => Volatile.Read(ref _recoveryPaused) != 0;

    private event Action<RealtimeConnectionStatus>? ConnectionStatusChanged;

    public async Task<bool> RunWhileConnectedAsync(
        Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnConnectionChanged(RealtimeConnectionStatus status)
        {
            if (!status.TransportConnected)
            {
                try
                { interrupted.Cancel(); }
                catch (ObjectDisposedException) { } // A callback can race unsubscription at completion.
            }
        }
        ConnectionStatusChanged += OnConnectionChanged;
        try
        {
            if (!ConnectionStatus.TransportConnected)
                return false;
            await operation(interrupted.Token).ConfigureAwait(false);
            return !interrupted.IsCancellationRequested && ConnectionStatus.TransportConnected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            ConnectionStatusChanged -= OnConnectionChanged;
        }
    }

    public async IAsyncEnumerable<BackendEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
        ArgumentNullException.ThrowIfNull(roomEpochs);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = roomEpochs.ToDictionary(pair => pair.Key, pair => pair.Value);
            Emit(new BackendEvent.Diagnostic(
                $"realtime-synchronization-started rooms={desired.Count}"));
            RealtimeRoomSubscriptionDelta delta = RealtimeEpochSubscriptionPlan.CreateDelta(_desiredRoomEpochs, desired);
            bool roomsChanged = !_desiredRoomEpochs.OrderBy(pair => pair.Key).SequenceEqual(desired.OrderBy(pair => pair.Key));
            _desiredRoomEpochs = desired;
            Volatile.Write(ref _hasSynchronized, 1);
            if (roomsChanged)
            {
                Volatile.Write(ref _recoveryPaused, 0);
                Interlocked.Exchange(ref _authorizationFailures, 0);
            }
            foreach (Guid roomId in _presentUsersByRoom.Keys.Where(id => !desired.ContainsKey(id)))
            {
                _presentUsersByRoom.TryRemove(roomId, out _);
            }
            _activeRoomId = activeRoomId is { } id && desired.ContainsKey(id) ? id : null;
            _localPresence = localPresence;
            if (!IsNetworkAvailable || Volatile.Read(ref _recoveryPaused) != 0)
            {
                Emit(new BackendEvent.Diagnostic("realtime-network-unavailable synchronization=deferred"));
                EmitDisconnected();
                return;
            }
            bool openedNewSocket = _socketSession?.Socket.State != WebSocketState.Open;
            try
            {
                await EnsureConnectedWithinGateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException
                or UnauthorizedAccessException or OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && !_shutdown.IsCancellationRequested)
            {
                if (PauseForNonRetryableFailure(exception))
                    return;
                Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
                EmitDisconnected();
                ScheduleRecovery();
                return;
            }
            IEnumerable<RealtimeRoomDescriptor> leaves = openedNewSocket
                ? []
                : delta.Leaves;
            foreach (RealtimeRoomDescriptor descriptor in leaves)
            {
                await SendAsync(descriptor.PhoenixTopic, "phx_leave", new { }, cancellationToken)
                    .ConfigureAwait(false);
                _joinReferences.TryRemove(descriptor.PhoenixTopic, out _);
            }

            IEnumerable<RealtimeRoomDescriptor> joins = openedNewSocket
                ? desired
                    .OrderBy(pair => pair.Key)
                    .SelectMany(pair => RealtimeEpochSubscriptionPlan.Descriptors(
                        pair.Key,
                        pair.Value))
                : delta.Joins;
            try
            {
                await Task.WhenAll(joins.Select(descriptor => JoinTopicAsync(descriptor, cancellationToken)))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or TimeoutException
                or RealtimeSubscriptionException or HttpRequestException or UnauthorizedAccessException
                && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                if (PauseForNonRetryableFailure(exception))
                    return;
                Emit(new BackendEvent.Diagnostic($"realtime-initial-subscription-failed {ConnectionFailureMessage(exception)}"));
                Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
                _socketSession?.Socket.Abort();
                EmitDisconnected();
                ScheduleRecovery();
                return;
            }
            EmitConnectionStatus(CurrentTransportStatus());
            Interlocked.Exchange(ref _authorizationFailures, 0);
            Emit(new BackendEvent.Diagnostic(
                $"realtime-synchronization-completed rooms={desired.Count}"));
        }
        finally
        {
            _connectionGate.Release();
        }

        await _presenceQueue.SubmitAsync(
            new RealtimePresenceIntent(_desiredRoomEpochs, _activeRoomId, _localPresence),
            cancellationToken).ConfigureAwait(false);
    }

    public Task PublishPresenceAsync(
        Guid roomId,
        PresenceState state,
        CancellationToken cancellationToken)
    {
        if (roomId == _activeRoomId)
        {
            _localPresence = state;
        }

        return _presenceQueue.SubmitAsync(
            new RealtimePresenceIntent(_desiredRoomEpochs, _activeRoomId, _localPresence),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _networkMonitor.AvailabilityChanged -= OnNetworkAvailabilityChanged;
        _networkMonitor.PathChanged -= OnNetworkPathChanged;
        _networkMonitor.Dispose();
        await TryLeaveTopicsBeforeShutdownAsync().ConfigureAwait(false);
        _shutdown.Cancel();
        CancellationTokenSource? recoveryCancellation;
        lock (_recoveryGate)
        {
            recoveryCancellation = _recoveryCancellation;
        }
        CancelRecoverySafely(recoveryCancellation);
        foreach (TaskCompletionSource<bool> reply in _pendingReplies.Values)
        {
            reply.TrySetCanceled();
        }
        _pendingReplies.Clear();
        _joinReferences.Clear();
        _presentUsersByRoom.Clear();
        await _presenceQueue.DisposeAsync().ConfigureAwait(false);
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectSocketWithinGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }

        if (_watchdogTask is not null)
        {
            try
            {
                await _watchdogTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task recoveryTask;
        lock (_recoveryGate)
        {
            recoveryTask = _recoveryTask;
        }
        await recoveryTask.ConfigureAwait(false);
        await _typingExpiries.DisposeAsync().ConfigureAwait(false);

        _events.Complete();
        _shutdown.Dispose();
        _connectionGate.Dispose();
        _sendGate.Dispose();
    }

    private async Task TryLeaveTopicsBeforeShutdownAsync()
    {
        if (_socketSession?.Socket.State != WebSocketState.Open)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            foreach (string? topic in _joinReferences.Keys.Order(StringComparer.Ordinal))
            {
                await SendAsync(topic, "phx_leave", new { }, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Emit(new BackendEvent.Diagnostic(
                $"realtime-shutdown-leave-failed {ConnectionFailureMessage(exception)}"));
        }
    }

    private async Task EnsureConnectedWithinGateAsync(CancellationToken cancellationToken)
    {
        if (_socketSession?.Socket.State == WebSocketState.Open)
        {
            return;
        }

        // An initial ConnectAsync has no installed socket to abort yet. Cancel it on path loss too.
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        void OnAvailabilityChanged(bool available)
        {
            if (!available)
                CancelRecoverySafely(interrupted);
        }
        _networkMonitor.AvailabilityChanged += OnAvailabilityChanged;
        ClientWebSocket socket;
        try
        {
            if (!IsNetworkAvailable)
                interrupted.Cancel();
            _ = await _sessions.GetStoredSessionAsync(interrupted.Token).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException(I18n.Get("auth.sessionMissing"));
            var builder = new UriBuilder(_configuration.Url)
            {
                Scheme = "wss",
                Port = -1,
                Path = "/realtime/v1/websocket",
                Query = $"apikey={Uri.EscapeDataString(_configuration.PublishableKey)}&vsn=1.0.0",
            };
            Emit(new BackendEvent.Diagnostic("realtime-websocket-connect-started"));
            socket = await _connectSocket(builder.Uri, interrupted.Token).ConfigureAwait(false);
            Emit(new BackendEvent.Diagnostic("realtime-websocket-connect-completed"));
        }
        finally { _networkMonitor.AvailabilityChanged -= OnAvailabilityChanged; }

        await DisconnectSocketWithinGateAsync().ConfigureAwait(false);
        _joinReferences.Clear();
        long generation = Interlocked.Increment(ref _connectionGeneration);
        Volatile.Write(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());
        _socketSession = new RealtimeSocketSession(
            socket,
            activeSocket => ReceiveLoopAsync(activeSocket, generation));
        _watchdogTask ??= Task.Run(WatchdogLoopAsync, CancellationToken.None);
    }

    private static async Task<ClientWebSocket> ConnectSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task DisconnectSocketWithinGateAsync()
    {
        RealtimeSocketSession? session = Interlocked.Exchange(ref _socketSession, null);
        Volatile.Write(ref _socketAccessToken, null);
        Volatile.Write(ref _socketAccessTokenExpiresAtUtcTicks, 0);
        Interlocked.Increment(ref _connectionGeneration);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _joinReferences.Clear();
    }

    private static string ConnectionFailureMessage(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }
        string category = current switch
        {
            SocketException socket when socket.SocketErrorCode is
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns",
            SocketException => "socket",
            AuthenticationException => "tls",
            TimeoutException => "timeout",
            TaskCanceledException => "timeout",
            HttpRequestException => "http",
            WebSocketException => "websocket",
            RealtimeSubscriptionException subscription =>
                $"subscription-{subscription.FailureKind.ToString().ToLowerInvariant()}",
            _ => "unknown",
        };
        string detail = current is SocketException socketException
            ? $" socket={socketException.SocketErrorCode} native={socketException.NativeErrorCode}"
            : current is HttpRequestException { StatusCode: { } statusCode }
                ? $" status={(int)statusCode}"
                : string.Empty;
        return $"Realtime WebSocket connection failed: category={category} type={current.GetType().Name}{detail}";
    }

    private async Task JoinTopicAsync(
        RealtimeRoomDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession session = await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(I18n.Get("auth.sessionMissing"));
        bool isEphemeral = descriptor.Kind == RealtimeTopicKind.Ephemeral;
        string topicKind = isEphemeral ? "ephemeral" : "database";
        Emit(new BackendEvent.Diagnostic(
            $"realtime-topic-subscribe-started kind={topicKind}"));
        object config;
        if (isEphemeral)
        {
            config = new
            {
                broadcast = new { ack = false, self = false },
                presence = new
                {
                    key = session.UserId.ToString("D"),
                    enabled = true,
                },
                postgres_changes = Array.Empty<object>(),
                @private = true,
            };
        }
        else
        {
            config = new
            {
                broadcast = new { ack = false, self = false },
                postgres_changes = Array.Empty<object>(),
                @private = true,
            };
        }
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string reference = await SendAsync(
            descriptor.PhoenixTopic,
            "phx_join",
            new
            {
                config,
                access_token = session.AccessToken,
            },
            cancellationToken,
            reply).ConfigureAwait(false);
        try
        {
            await reply.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _joinReferences[descriptor.PhoenixTopic] = reference;
        }
        catch
        {
            _joinReferences.TryRemove(
                new KeyValuePair<string, string>(descriptor.PhoenixTopic, reference));
            Emit(new BackendEvent.Diagnostic(
                $"realtime-topic-subscribe-failed kind={topicKind}"));
            throw;
        }
        finally
        {
            _pendingReplies.TryRemove(reference, out _);
        }
        Emit(new BackendEvent.Diagnostic(
            $"realtime-topic-subscribe-completed kind={topicKind}"));
        Volatile.Write(ref _socketAccessToken, session.AccessToken);
        Volatile.Write(ref _socketAccessTokenExpiresAtUtcTicks, session.ExpiresAt.UtcTicks);
    }

    private async Task RefreshChannelAuthorizationAsync(
        StoredSupabaseSession session,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
            Volatile.Read(ref _socketAccessToken),
            session.AccessToken,
            StringComparison.Ordinal))
        {
            return;
        }

        string[] joinedTopics = [.. _joinReferences.Keys.Order(StringComparer.Ordinal)];
        foreach (string topic in joinedTopics)
        {
            await SendAsync(
                topic,
                "access_token",
                new { access_token = session.AccessToken },
                cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _socketAccessToken, session.AccessToken);
        Volatile.Write(ref _socketAccessTokenExpiresAtUtcTicks, session.ExpiresAt.UtcTicks);
        Emit(new BackendEvent.Diagnostic(
            $"realtime-channel-authorization-refreshed topics={joinedTopics.Length}"));
    }

    private async Task PublishPresenceBatchAsync(
        RealtimePresenceIntent intent,
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession session = await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(I18n.Get("auth.sessionMissing"));
        foreach (KeyValuePair<Guid, long> room in intent.RoomEpochs.OrderBy(pair => pair.Key))
        {
            PresenceState state = PresencePublicationPlan.StateFor(
                room.Key,
                intent.ActiveRoomId,
                intent.LocalPresence);
            var topic = new RealtimeRoomDescriptor(
                room.Key,
                room.Value,
                RealtimeTopicKind.Ephemeral);
            await SendAsync(
                topic.PhoenixTopic,
                "presence",
                new
                {
                    type = "presence",
                    @event = "track",
                    payload = new
                    {
                        user_id = session.UserId,
                        state = state.ToString().ToLowerInvariant(),
                        online_at = DateTimeOffset.UtcNow.ToString("O"),
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> SendAsync(
        string topic,
        string eventName,
        object payload,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? reply = null)
    {
        ClientWebSocket? socket = _socketSession?.Socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new WebSocketException(I18n.Get("backend.realtimeUnavailable"));
        }

        string reference = Interlocked.Increment(ref _reference).ToString();
        string? joinReference = eventName == "phx_join"
            ? reference
            : _joinReferences.GetValueOrDefault(topic);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                join_ref = joinReference,
                topic,
                @event = eventName,
                payload,
                @ref = reference,
            },
            s_jsonOptions);
        if (reply is not null && !_pendingReplies.TryAdd(reference, reply))
        {
            throw new InvalidOperationException("Supabase Realtime reference collision.");
        }
        bool sendGateEntered = false;
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            sendGateEntered = true;
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (reply is not null)
            {
                _pendingReplies.TryRemove(reference, out _);
            }
            throw;
        }
        finally
        {
            if (sendGateEntered)
                _sendGate.Release();
        }
        return reference;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, long generation)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (!_shutdown.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                int count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(
                        buffer.AsMemory(count),
                        _shutdown.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string closeCode = socket.CloseStatus is { } status
                            ? ((int)status).ToString()
                            : "unknown";
                        Emit(new BackendEvent.Diagnostic(
                            $"realtime-websocket-closed code={closeCode}"));
                        throw new WebSocketException("Supabase Realtime closed the connection.");
                    }

                    count += result.Count;
                    if (count == buffer.Length && !result.EndOfMessage)
                    {
                        throw new InvalidDataException("Supabase Realtime message exceeded 64 KiB.");
                    }
                }
                while (!result.EndOfMessage);

                Volatile.Write(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());
                HandleMessage(buffer.AsMemory(0, count));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _connectionGeneration) || _shutdown.IsCancellationRequested)
            {
                return;
            }

            Emit(new BackendEvent.Diagnostic($"realtime-receive-failed {ConnectionFailureMessage(exception)}"));
            socket.Abort();
            foreach (string reference in _pendingReplies.Keys)
            {
                if (_pendingReplies.TryRemove(reference, out TaskCompletionSource<bool>? reply))
                    reply.TrySetException(new WebSocketException("Realtime connection was lost before its reply.", exception));
            }
            if (IsRecoveryPaused)
            {
                EmitDisconnected();
                return;
            }
            if (!RealtimeUserErrorMessage.IsExpectedLocalAbort(exception))
            {
                Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
            }
            EmitDisconnected();
            ScheduleRecovery();
        }
    }

    private async Task WatchdogLoopAsync()
    {
        using var timer = new PeriodicTimer(_watchdogInterval);
        while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
        {
            if (!IsNetworkAvailable || IsRecoveryPaused)
            {
                continue;
            }

            ClientWebSocket? socket = _socketSession?.Socket;
            if (socket?.State != WebSocketState.Open)
            {
                EmitDisconnected();
                ScheduleRecovery();
                continue;
            }

            TimeSpan silence = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReceiveTimestamp));
            if (silence >= s_unhealthyAfter)
            {
                Emit(new BackendEvent.Diagnostic(
                    $"realtime-heartbeat-timeout silence-ms={(long)silence.TotalMilliseconds}"));
                socket.Abort();
                Emit(new BackendEvent.TechnicalError(
                    I18n.Get("connection.serviceUnavailable")));
                EmitDisconnected();
                ScheduleRecovery();
                continue;
            }

            if (Stopwatch.GetElapsedTime(_lastConnectionHealthTimestamp) >= TimeSpan.FromMinutes(1))
            {
                _lastConnectionHealthTimestamp = Stopwatch.GetTimestamp();
                Emit(new BackendEvent.Diagnostic(
                    $"realtime-health silence-ms={(long)silence.TotalMilliseconds} "
                    + $"event-queue-size={_events.Count}"));
            }

            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastAuthorizationRefreshTimestamp))
                >= _authorizationRefreshInterval)
            {
                try
                {
                    StoredSupabaseSession session = await _sessions.GetStoredSessionAsync(_shutdown.Token)
                        .ConfigureAwait(false)
                        ?? throw new UnauthorizedAccessException(I18n.Get("auth.sessionMissing"));
                    await RefreshChannelAuthorizationAsync(session, _shutdown.Token).ConfigureAwait(false);
                    Volatile.Write(ref _lastAuthorizationRefreshTimestamp, Stopwatch.GetTimestamp());
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException
                    or TaskCanceledException
                    or UnauthorizedAccessException)
                {
                    if (PauseForNonRetryableFailure(exception))
                    {
                        continue;
                    }

                    Emit(new BackendEvent.Diagnostic(
                        $"realtime-channel-authorization-refresh-deferred {ConnectionFailureMessage(exception)}"));
                    if (Volatile.Read(ref _socketAccessTokenExpiresAtUtcTicks) <= DateTimeOffset.UtcNow.UtcTicks)
                    {
                        Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
                        socket.Abort();
                        EmitDisconnected();
                        ScheduleRecovery();
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
                    socket.Abort();
                    continue;
                }
            }

            try
            {
                await SendAsync("phoenix", "heartbeat", new { }, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
                socket.Abort();
            }
        }
    }

    private async Task RecoverAsync(bool immediate, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _recovering, 1) != 0)
        {
            return;
        }

        try
        {
            for (int attempt = 1; !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (!IsNetworkAvailable || Volatile.Read(ref _recoveryPaused) != 0)
                {
                    return;
                }

                TimeSpan delay = immediate && attempt == 1
                    ? RealtimeRecoveryPolicy.PathRecoveryDebounce
                    : RealtimeRecoveryPolicy.ConnectionDelayForAttempt(attempt, Random.Shared.NextDouble());
                Emit(new BackendEvent.Diagnostic(
                    $"realtime-reconnect-scheduled attempt={attempt} delay-ms={(long)delay.TotalMilliseconds}"));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (!IsNetworkAvailable || Volatile.Read(ref _recoveryPaused) != 0)
                {
                    return;
                }
                Emit(new BackendEvent.Diagnostic(
                    $"realtime-reconnect-started attempt={attempt}"));
                await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await DisconnectSocketWithinGateAsync().ConfigureAwait(false);
                    await EnsureConnectedWithinGateAsync(cancellationToken).ConfigureAwait(false);
                    await Task.WhenAll(_desiredRoomEpochs.OrderBy(pair => pair.Key)
                        .SelectMany(room => RealtimeEpochSubscriptionPlan.Descriptors(room.Key, room.Value))
                        .Select(descriptor => JoinTopicAsync(descriptor, cancellationToken)))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (PauseForNonRetryableFailure(exception))
                        return;
                    Emit(new BackendEvent.Diagnostic(
                        $"realtime-reconnect-failed attempt={attempt} {ConnectionFailureMessage(exception)}"));
                    continue;
                }
                finally
                {
                    _connectionGate.Release();
                }

                try
                {
                    await _presenceQueue.SubmitAsync(
                        new RealtimePresenceIntent(
                            _desiredRoomEpochs,
                            _activeRoomId,
                            _localPresence),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (PauseForNonRetryableFailure(exception))
                        return;
                    Emit(new BackendEvent.Diagnostic(
                        $"realtime-reconnect-failed attempt={attempt} stage=presence {ConnectionFailureMessage(exception)}"));
                    EmitDisconnected();
                    continue;
                }
                Emit(new BackendEvent.Diagnostic(
                    $"realtime-reconnect-completed attempt={attempt}"));
                EmitConnectionStatus(CurrentTransportStatus());
                Interlocked.Exchange(ref _authorizationFailures, 0);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private RealtimeConnectionStatus CurrentTransportStatus()
    {
        bool socketConnected = IsNetworkAvailable && _socketSession?.Socket.State == WebSocketState.Open;
        bool transportConnected = socketConnected
            && _desiredRoomEpochs.All(room => RealtimeEpochSubscriptionPlan
                .Descriptors(room.Key, room.Value)
                .All(descriptor => _joinReferences.ContainsKey(descriptor.PhoenixTopic)));
        bool activeRoomConnected = transportConnected
            && _activeRoomId is { } activeRoomId
            && _desiredRoomEpochs.TryGetValue(activeRoomId, out long epoch)
            && RealtimeEpochSubscriptionPlan.Descriptors(activeRoomId, epoch)
                .All(descriptor => _joinReferences.ContainsKey(descriptor.PhoenixTopic));
        return new RealtimeConnectionStatus(
            transportConnected,
            activeRoomConnected,
            RecoveryReconciled: false);
    }

    private void EmitDisconnected() => EmitConnectionStatus(RealtimeConnectionStatus.Disconnected);

    private void EmitConnectionStatus(RealtimeConnectionStatus status)
    {
        if (status == Volatile.Read(ref _lastEmittedConnectionStatus))
        {
            return;
        }

        Volatile.Write(ref _lastEmittedConnectionStatus, status);
        ConnectionStatusChanged?.Invoke(status);
        Emit(new BackendEvent.ConnectionChanged(status));
    }

    private void HandleMessage(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("event", out JsonElement replyEvent)
            && replyEvent.GetString() == "phx_reply"
            && root.TryGetProperty("ref", out JsonElement replyReference)
            && replyReference.GetString() is { } reference
            && _pendingReplies.TryRemove(reference, out TaskCompletionSource<bool>? completion))
        {
            bool succeeded = root.TryGetProperty("payload", out JsonElement replyPayload)
                && replyPayload.TryGetProperty("status", out JsonElement status)
                && status.GetString() == "ok";
            if (succeeded)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(
                    RealtimeSubscriptionException.FromServerPayload(replyPayload));
            }
            return;
        }

        if (!root.TryGetProperty("topic", out JsonElement topicElement)
            || !RealtimeRoomDescriptor.TryParsePhoenixTopic(
                topicElement.GetString(),
                out RealtimeRoomDescriptor descriptor)
            || !_desiredRoomEpochs.TryGetValue(descriptor.RoomId, out long expectedEpoch)
            || expectedEpoch != descriptor.Epoch
            || !root.TryGetProperty("event", out JsonElement eventElement))
        {
            return;
        }

        string? eventName = eventElement.GetString();
        JsonElement payload = root.TryGetProperty("payload", out JsonElement value) ? value : default;
        bool systemFailure = eventName == "system"
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("status", out JsonElement systemStatus)
            && systemStatus.GetString() == "error";
        if (eventName is "phx_close" or "phx_error" || systemFailure)
        {
            HandleChannelTermination(descriptor, eventName, payload);
            return;
        }
        if (eventName == "presence_diff" && descriptor.Kind == RealtimeTopicKind.Ephemeral)
        {
            HandlePresence(descriptor.RoomId, payload);
            return;
        }
        if (eventName == "presence_state" && descriptor.Kind == RealtimeTopicKind.Ephemeral)
        {
            HandlePresenceState(descriptor.RoomId, payload);
            return;
        }

        if (eventName != "broadcast" || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? broadcastEvent = payload.TryGetProperty("event", out JsonElement broadcastEventElement)
            ? broadcastEventElement.GetString()
            : null;
        JsonElement inner = payload.TryGetProperty("payload", out JsonElement innerPayload) ? innerPayload : payload;
        if (!TryGuid(inner, "room_id", out Guid payloadRoomId)
            || payloadRoomId != descriptor.RoomId)
        {
            return;
        }

        if (descriptor.Kind == RealtimeTopicKind.Database)
        {
            HandleDatabaseBroadcast(descriptor.RoomId, broadcastEvent, inner);
            return;
        }

        switch (broadcastEvent)
        {
            case "typing_start":
                HandleTyping(descriptor.RoomId, inner, active: true);
                break;
            case "typing_stop":
                HandleTyping(descriptor.RoomId, inner, active: false);
                break;
            case "character_pulse":
                if (TryGuid(inner, "user_id", out Guid pulseUserId)
                    && TryGuid(inner, "event_id", out Guid pulseId))
                {
                    Emit(new BackendEvent.CharacterPulsed(
                        new CharacterPulseEvent(pulseId, descriptor.RoomId, pulseUserId)));
                }
                break;
            case "character_throw":
                if (inner.TryGetProperty("schema_version", out JsonElement schemaVersion)
                    && schemaVersion.TryGetInt32(out int version)
                    && version == 1
                    && TryGuid(inner, "event_id", out Guid throwId)
                    && TryGuid(inner, "actor_user_id", out Guid actorUserId)
                    && TryGuid(inner, "target_user_id", out Guid targetUserId)
                    && actorUserId != targetUserId
                    && inner.TryGetProperty("source_character_id", out JsonElement sourceCharacterElement)
                    && sourceCharacterElement.ValueKind == JsonValueKind.String
                    && sourceCharacterElement.GetString() is { Length: > 0 and <= 40 } sourceCharacterId
                    && IsValidCharacterId(sourceCharacterId))
                {
                    Emit(new BackendEvent.CharacterThrown(new CharacterThrowEvent(
                        throwId,
                        descriptor.RoomId,
                        actorUserId,
                        targetUserId,
                        sourceCharacterId,
                        ParseThrowableAssetId(inner))));
                }
                break;
        }
    }

    private void HandleChannelTermination(
        RealtimeRoomDescriptor descriptor,
        string? eventName,
        JsonElement payload)
    {
        if (!_joinReferences.TryRemove(descriptor.PhoenixTopic, out _))
        {
            return;
        }

        Emit(new BackendEvent.Diagnostic(
            $"realtime-channel-terminated kind={descriptor.Kind.ToString().ToLowerInvariant()} event={eventName}"));
        EmitConnectionStatus(CurrentTransportStatus());
        var failure = RealtimeSubscriptionException.FromServerPayload(payload);
        if (PauseForNonRetryableFailure(failure))
        {
            return;
        }

        Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(failure)));
        ScheduleRecovery();
    }

    private static bool IsValidCharacterId(string value)
    {
        foreach (char character in value)
        {
            if (character != '_'
                && (character < 'a' || character > 'z')
                && (character < '0' || character > '9'))
            {
                return false;
            }
        }
        return true;
    }

    internal static string? ParseThrowableAssetId(JsonElement element)
    {
        if (!element.TryGetProperty("throwable_id", out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        // Current broadcasts contain render asset IDs (for example "pork");
        // older broadcasts contain catalog IDs ("throwable_pork"). Only bundled
        // catalog/asset mappings are used, with the common ball for unknown IDs.
        return CosmeticCatalog.ResolveThrowableAssetId(value.GetString());
    }

    private void HandleDatabaseBroadcast(
        Guid roomId,
        string? eventName,
        JsonElement payload)
    {
        switch (eventName)
        {
            case "message_changed":
                bool hasOperation = payload.TryGetProperty("operation", out JsonElement operationElement);
                string? operation = hasOperation ? operationElement.GetString() : null;
                if (TryGuid(payload, "message_id", out Guid messageId)
                    && operation is "INSERT" or "UPDATE" or "DELETE")
                {
                    Emit(new BackendEvent.MessageChanged(
                        roomId,
                        messageId,
                        operation));
                }
                break;
            case "messages_pruned":
                Emit(new BackendEvent.MessagesInvalidated(roomId));
                break;
            case "structure_changed":
                if (payload.TryGetProperty("entity", out JsonElement entity)
                    && entity.GetString() is "profiles" or "rooms" or "room_members"
                    && payload.TryGetProperty("operation", out JsonElement structuralOperation)
                    && structuralOperation.GetString() is "INSERT" or "UPDATE" or "DELETE")
                {
                    Emit(new BackendEvent.RoomStructureChanged(roomId));
                }
                break;
        }
    }

    private void HandlePresence(Guid roomId, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var joined = new Dictionary<Guid, PresenceState>();
        if (payload.TryGetProperty("joins", out JsonElement joins) && joins.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in joins.EnumerateObject())
            {
                if (!Guid.TryParse(property.Name, out Guid userId))
                {
                    continue;
                }

                joined[userId] = ParsePresenceState(property.Value);
            }
        }

        var left = new HashSet<Guid>();
        if (payload.TryGetProperty("leaves", out JsonElement leaves) && leaves.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in leaves.EnumerateObject())
            {
                if (Guid.TryParse(property.Name, out Guid userId))
                {
                    left.Add(userId);
                }
            }
        }

        foreach (PresenceUpdate update in PresenceChangePlan.Updates(joined, left))
        {
            Emit(new BackendEvent.PresenceChanged(
                roomId,
                update.UserId,
                update.State));
        }

        IReadOnlySet<Guid> previous = _presentUsersByRoom.GetValueOrDefault(
            roomId,
            new HashSet<Guid>());
        _presentUsersByRoom[roomId] = previous
            .Except(left)
            .Concat(joined.Keys)
            .ToHashSet();
    }

    private void HandlePresenceState(Guid roomId, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var current = new Dictionary<Guid, PresenceState>();
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (Guid.TryParse(property.Name, out Guid userId))
            {
                current[userId] = ParsePresenceState(property.Value);
            }
        }

        IReadOnlySet<Guid> previous = _presentUsersByRoom.GetValueOrDefault(
            roomId,
            new HashSet<Guid>());
        _presentUsersByRoom[roomId] = current.Keys.ToHashSet();
        foreach (PresenceUpdate update in PresenceSnapshotPlan.Updates(current, previous))
        {
            Emit(new BackendEvent.PresenceChanged(
                roomId,
                update.UserId,
                update.State));
        }
    }

    private static PresenceState ParsePresenceState(JsonElement value)
    {
        if (value.TryGetProperty("metas", out JsonElement metas)
            && metas.ValueKind == JsonValueKind.Array
            && metas.GetArrayLength() > 0)
        {
            return PresenceAggregatePlan.MostAvailable(
                metas.EnumerateArray().Select(ParsePresenceMeta));
        }

        return ParsePresenceMeta(value);
    }

    private static PresenceState ParsePresenceMeta(JsonElement value)
    {
        return value.TryGetProperty("state", out JsonElement stateElement)
            && Enum.TryParse<PresenceState>(stateElement.GetString(), true, out PresenceState parsed)
                ? parsed
                : PresenceState.Online;
    }

    private void HandleTyping(Guid roomId, JsonElement payload, bool active)
    {
        if (!TryGuid(payload, "user_id", out Guid userId))
        {
            return;
        }

        (Guid roomId, Guid userId) key = (roomId, userId);
        _typingExpiries.Cancel(key);

        Emit(new BackendEvent.TypingChanged(roomId, userId, active));
        if (!active)
        {
            return;
        }

        _typingExpiries.Restart(key);
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && Guid.TryParse(property.GetString(), out value);
    }

    private void Emit(BackendEvent backendEvent)
    {
        if (!_events.TryWrite(backendEvent))
        {
            ScheduleRecovery();
        }
    }

    private bool IsNetworkAvailable => Volatile.Read(ref _networkAvailable) != 0;

    private bool PauseForNonRetryableFailure(Exception exception)
    {
        bool authorizationFailure = exception is UnauthorizedAccessException;
        bool configurationFailure = false;
        bool subscriptionAuthorizationFailure = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            subscriptionAuthorizationFailure |= current is RealtimeSubscriptionException
            {
                FailureKind: RealtimeSubscriptionFailureKind.Authorization,
            };
            configurationFailure |= current is RealtimeSubscriptionException
            {
                FailureKind: RealtimeSubscriptionFailureKind.Configuration,
            } or HttpRequestException
            {
                StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            };
            authorizationFailure |= current is HttpRequestException
            {
                StatusCode: HttpStatusCode.Unauthorized,
            };
        }

        bool pause = configurationFailure || authorizationFailure;
        if (!pause && subscriptionAuthorizationFailure)
        {
            pause = Interlocked.Increment(ref _authorizationFailures) >= 2;
        }
        if (!pause)
            return false;

        Volatile.Write(ref _recoveryPaused, 1);
        EmitDisconnected();
        try
        { _socketSession?.Socket.Abort(); }
        catch (ObjectDisposedException) { }
        string reason = configurationFailure ? "configuration" : "authorization";
        Emit(new BackendEvent.Diagnostic($"realtime-reconnect-paused reason={reason}"));
        Emit(new BackendEvent.TechnicalError(RealtimeUserErrorMessage.From(exception)));
        return true;
    }

    public void RequestReconnect(bool userInitiated = false)
    {
        if (_shutdown.IsCancellationRequested)
            return;
        if (userInitiated)
        {
            Volatile.Write(ref _recoveryPaused, 0);
            Interlocked.Exchange(ref _authorizationFailures, 0);
        }
        _networkMonitor.Refresh();
        // Resume must discard a socket that still appears open after sleep.
        EmitDisconnected();
        ScheduleRecovery(immediate: true);
    }

    private void OnNetworkPathChanged()
    {
        // NCSI can change on a healthy connection; do not tear down a working socket.
        if (!ConnectionStatus.TransportConnected)
            ScheduleRecovery(immediate: true);
    }

    private void OnNetworkAvailabilityChanged(bool isAvailable)
    {
        int next = isAvailable ? 1 : 0;
        if (Interlocked.Exchange(ref _networkAvailable, next) == next
            || _shutdown.IsCancellationRequested)
        {
            return;
        }

        Emit(new BackendEvent.Diagnostic(
            isAvailable ? "realtime-network-available" : "realtime-network-unavailable"));
        if (isAvailable)
        {
            ScheduleRecovery(immediate: true);
            return;
        }

        CancellationTokenSource? recoveryCancellation;
        lock (_recoveryGate)
        {
            recoveryCancellation = _recoveryCancellation;
        }
        CancelRecoverySafely(recoveryCancellation);
        try
        {
            _socketSession?.Socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
        EmitDisconnected();
    }

    private void ScheduleRecovery(bool immediate = false)
    {
        CancellationTokenSource? superseded;
        lock (_recoveryGate)
        {
            if (_shutdown.IsCancellationRequested || !IsNetworkAvailable
                || Volatile.Read(ref _hasSynchronized) == 0 || Volatile.Read(ref _recoveryPaused) != 0)
            {
                return;
            }

            if (!_recoveryTask.IsCompleted && !immediate)
            {
                return;
            }

            if (immediate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastRecoveryHint != 0 && !_recoveryTask.IsCompleted
                    && _recoveryCancellation?.IsCancellationRequested == false
                    && Stopwatch.GetElapsedTime(_lastRecoveryHint, now) < TimeSpan.FromSeconds(1))
                    return;
                _lastRecoveryHint = now;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            if (_recoveryTask.IsCompleted)
            {
                _recoveryCancellation?.Dispose();
                _recoveryCancellation = cancellation;
                _recoveryTask = RunScheduledRecoveryAsync(
                    immediate,
                    cancellation);
                return;
            }

            superseded = _recoveryCancellation;
            Task previous = _recoveryTask;
            _recoveryCancellation = cancellation;
            _recoveryTask = RunScheduledRecoveryAfterAsync(
                previous,
                immediate,
                cancellation);
        }
        CancelRecoverySafely(superseded);
    }

    private static void CancelRecoverySafely(CancellationTokenSource? cancellation)
    {
        try
        { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // The previous recovery may finish after releasing _recoveryGate.
    }

    private async Task RunScheduledRecoveryAfterAsync(
        Task previous,
        bool immediate,
        CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await RunScheduledRecoveryAsync(immediate, cancellation).ConfigureAwait(false);
    }

    private async Task RunScheduledRecoveryAsync(
        bool immediate,
        CancellationTokenSource cancellation)
    {
        try
        {
            await RecoverAsync(immediate, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_recoveryGate)
            {
                if (ReferenceEquals(_recoveryCancellation, cancellation))
                {
                    _recoveryCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }
}
