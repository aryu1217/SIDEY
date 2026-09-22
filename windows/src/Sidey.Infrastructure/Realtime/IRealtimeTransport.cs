using Sidey.Core.Abstractions;
using Sidey.Core.Domain;

namespace Sidey.Infrastructure.Realtime;

internal interface IRealtimeTransport : IAsyncDisposable
{
    public RealtimeConnectionStatus ConnectionStatus { get; }
    public bool IsRecoveryPaused { get; }
    public bool UsesFirebaseChat => false;

    public IAsyncEnumerable<BackendEvent> ReadEventsAsync(CancellationToken cancellationToken);

    public Task SynchronizeAsync(
        IReadOnlyDictionary<Guid, long> roomEpochs,
        Guid? activeRoomId,
        PresenceState localPresence,
        CancellationToken cancellationToken);

    public Task PublishPresenceAsync(
        Guid roomId,
        PresenceState state,
        CancellationToken cancellationToken);

    public Task<FirebaseRealtimeChatResult?> PublishChatAsync(
        Guid messageId,
        Guid roomId,
        string body,
        CancellationToken cancellationToken) =>
        Task.FromException<FirebaseRealtimeChatResult?>(
            new InvalidOperationException("Firebase realtime chat is not active."));

    public Task ConvergeGrantAsync(
        string minimumAccessRevision,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask InvalidateSessionAsync(
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public Task<bool> RunWhileConnectedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);

    public void RequestReconnect(bool userInitiated = false);
}
