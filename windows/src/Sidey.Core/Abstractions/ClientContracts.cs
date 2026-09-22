using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Overlay;

namespace Sidey.Core.Abstractions;

public sealed record AuthSession(
    Guid UserId,
    DateTimeOffset ExpiresAt);

public interface IAuthService
{
    public Task<AuthSession?> RestoreSessionAsync(CancellationToken cancellationToken = default);
    public Task<AuthSession> CreateAnonymousSessionAsync(CancellationToken cancellationToken = default);
    public Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionRecoveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class AnonymousSessionBootstrapper
{
    public static async Task<AuthSession> RestoreOrCreateAsync(
        IAuthService auth,
        bool hasStoredSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auth);
        AuthSession? restored;
        try
        {
            restored = await auth.RestoreSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (hasStoredSession)
        {
            throw new SessionRecoveryException(
                I18n.Get("auth.restoreFailed"),
                exception);
        }

        if (restored is not null)
        {
            return restored;
        }

        if (hasStoredSession)
        {
            throw new SessionRecoveryException(
                I18n.Get("auth.restoreFailed"));
        }

        return await auth.CreateAnonymousSessionAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record BackendSnapshot(
    Profile? Profile,
    IReadOnlyList<Room> Rooms,
    Guid CurrentUserId,
    IReadOnlySet<string> ActiveEntitlementKeys);

public sealed record CreateRoomResult(Room Room, string InviteCode);

public sealed record MessageHistoryCursor(DateTimeOffset CreatedAt, Guid Id);

public sealed record MessageHistoryPage(
    IReadOnlyList<ChatMessage> Messages,
    MessageHistoryCursor? NextCursor);

public sealed class ChatCommitAmbiguousException : Exception
{
    public ChatCommitAmbiguousException(Guid messageId, Guid roomId, Exception innerException)
        : base("Chat commit outcome is ambiguous and will be reconciled.", innerException)
    {
        MessageId = messageId;
        RoomId = roomId;
    }

    public Guid MessageId { get; }
    public Guid RoomId { get; }
}

public sealed record CommerceCheckout(Guid OrderId, Uri CheckoutUri);

public sealed record RealtimeConnectionStatus(
    bool TransportConnected,
    bool ActiveRoomTransportConnected,
    bool RecoveryReconciled)
{
    public static RealtimeConnectionStatus Disconnected { get; } = new(false, false, false);

    public bool IsReady => TransportConnected && RecoveryReconciled;

    public RealtimeConnectionStatus WithRecoveryReconciled(bool reconciled) => this with
    {
        RecoveryReconciled = TransportConnected && reconciled,
    };
}

public abstract record BackendEvent
{
    public sealed record SnapshotReceived(BackendSnapshot Snapshot) : BackendEvent;
    public sealed record MessageReceived(ChatMessage Message) : BackendEvent;
    public sealed record MessageDeleted(Guid RoomId, Guid MessageId) : BackendEvent;
    public sealed record MessagesReplaced(Guid RoomId, IReadOnlyList<ChatMessage> Messages) : BackendEvent;
    public sealed record MessageChanged(Guid RoomId, Guid MessageId, string Operation) : BackendEvent;
    public sealed record MessagesInvalidated(Guid RoomId) : BackendEvent;
    public sealed record PresenceChanged(Guid RoomId, Guid UserId, PresenceState State) : BackendEvent;
    public sealed record TypingChanged(Guid RoomId, Guid UserId, bool Active) : BackendEvent;
    public sealed record CharacterPulsed(CharacterPulseEvent Pulse) : BackendEvent;
    public sealed record CharacterThrown(CharacterThrowEvent Throw) : BackendEvent;
    public sealed record RoomStructureChanged(Guid RoomId) : BackendEvent;
    public sealed record ConnectionChanged(RealtimeConnectionStatus Status) : BackendEvent;
    public sealed record ReconciliationRequired : BackendEvent;
    public sealed record Diagnostic(string Stage) : BackendEvent;
    public sealed record TechnicalError(string Message) : BackendEvent;
}

public interface IBackendGateway
{
    public Task<BackendSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default);
    public Task<Profile> SetTreeMovementPausedAsync(
        bool paused, long expectedRevision, CancellationToken cancellationToken = default);
    public Task<Profile> SaveProfileAsync(string nickname, string characterId, CancellationToken cancellationToken = default);
    public Task<Profile> SetEquippedCosmeticAsync(
        CommerceProductKind kind,
        string? catalogItemId,
        CancellationToken cancellationToken = default);
    public Task<CreateRoomResult> CreateRoomAsync(string name, CancellationToken cancellationToken = default);
    public Task<Room> JoinRoomAsync(string inviteCode, CancellationToken cancellationToken = default);
    public Task LeaveRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
    public Task RenameRoomAsync(Guid roomId, string name, CancellationToken cancellationToken = default);
    public Task<string> RotateInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default);
    public Task RemoveRoomMemberAsync(Guid roomId, Guid userId, CancellationToken cancellationToken = default);
    public Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
    public Task DeleteOwnAccountAsync(CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<ChatMessage>> FetchRecentMessagesAsync(Guid roomId, CancellationToken cancellationToken = default);
    public Task<MessageHistoryPage> FetchMessagePageAsync(
        Guid roomId,
        MessageHistoryCursor? before,
        int limit = 50,
        CancellationToken cancellationToken = default);
    public Task<ChatMessage> SendMessageAsync(Guid id, Guid roomId, string body, CancellationToken cancellationToken = default);
    public Task PublishPresenceAsync(Guid roomId, PresenceState state, CancellationToken cancellationToken = default);
    public Task BroadcastTypingAsync(Guid roomId, bool active, bool keepalive, CancellationToken cancellationToken = default);
    public Task BroadcastCharacterPulseAsync(Guid roomId, Guid eventId, CancellationToken cancellationToken = default);
    public Task BroadcastCharacterThrowAsync(
        Guid roomId,
        Guid eventId,
        Guid targetUserId,
        CancellationToken cancellationToken = default);
    public Task SynchronizeRealtimeRoomsAsync(
        IReadOnlyDictionary<Guid, long> roomEpochs,
        Guid? activeRoomId,
        PresenceState localPresence,
        CancellationToken cancellationToken = default);
    public IAsyncEnumerable<BackendEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}

public interface IOverlayHost
{
    public bool IsVisible { get; }
    public void Apply(WorldSnapshot snapshot);
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);
}

public interface IActivityMonitor : IAsyncDisposable
{
    public IAsyncEnumerable<PresenceState> ObserveAsync(CancellationToken cancellationToken = default);
}

public interface IMonitorService
{
    public IReadOnlyList<MonitorGeometry> GetMonitors();
}

public enum CredentialKey
{
    SupabaseSession,
    FirebaseRealtimeSession,
}

public interface ICredentialStore
{
    public ValueTask<string?> ReadAsync(CredentialKey key, CancellationToken cancellationToken = default);
    public ValueTask WriteAsync(CredentialKey key, string value, CancellationToken cancellationToken = default);
    public ValueTask DeleteAsync(CredentialKey key, CancellationToken cancellationToken = default);
    public ValueTask<string?> ReadInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default);
    public ValueTask WriteInviteCodeAsync(Guid roomId, string inviteCode, CancellationToken cancellationToken = default);
    public ValueTask DeleteInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default);
}

public interface IPreferencesStore
{
    public ValueTask<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);
    public ValueTask SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}
