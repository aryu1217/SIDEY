using Sidey.Core.Abstractions;
using Sidey.Core.Domain;

namespace Sidey.Infrastructure.Backend;

public sealed record RecoveredMessagesReceived(
    Guid UserId, Guid RoomId, long RoomEpoch, IReadOnlyList<ChatMessage> Messages,
    long FetchStartedTimestamp) : BackendEvent;
