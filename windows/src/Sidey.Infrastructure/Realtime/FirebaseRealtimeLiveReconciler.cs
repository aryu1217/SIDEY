namespace Sidey.Infrastructure.Realtime;

internal abstract record FirebaseRealtimeLiveAction
{
    public sealed record Typing(Guid UserId, bool Active) : FirebaseRealtimeLiveAction;
    public sealed record Pulse(Guid UserId) : FirebaseRealtimeLiveAction;
    public sealed record Throw(Guid ActorUserId, FirebaseRealtimeThrow Payload) : FirebaseRealtimeLiveAction;
}

internal sealed class FirebaseRealtimeLiveReconciler
{
    internal const long FreshnessWindowMilliseconds = 5_000;
    internal const long TypingTtlMilliseconds = 6_000;

    private bool _baselineEstablished;
    private HashSet<Guid> _typingUserIds = [];
    private Dictionary<Guid, long> _typingDeadlines = [];
    private readonly Dictionary<Guid, long> _pulseHighWater = [];
    private readonly Dictionary<Guid, long> _throwHighWater = [];

    public IReadOnlyList<FirebaseRealtimeLiveAction> Consume(
        FirebaseRealtimeRoomPayload snapshot,
        long receivedAtMilliseconds)
    {
        Dictionary<Guid, long> nextDeadlines = FreshTypingDeadlines(
            snapshot.Typing,
            receivedAtMilliseconds);
        HashSet<Guid> nextTyping = [.. nextDeadlines.Keys];
        if (!_baselineEstablished)
        {
            _baselineEstablished = true;
            _typingUserIds = nextTyping;
            _typingDeadlines = nextDeadlines;
            foreach ((Guid userId, long timestamp) in snapshot.CharacterPulses)
            {
                _pulseHighWater[userId] = timestamp;
            }
            foreach ((Guid userId, FirebaseRealtimeThrow payload) in snapshot.CharacterThrows)
            {
                _throwHighWater[userId] = payload.Timestamp;
            }
            return [.. nextTyping
                .OrderBy(userId => userId)
                .Select(userId => new FirebaseRealtimeLiveAction.Typing(userId, true))];
        }

        List<FirebaseRealtimeLiveAction> actions = [];
        actions.AddRange(nextTyping.Except(_typingUserIds)
            .OrderBy(userId => userId)
            .Select(userId => new FirebaseRealtimeLiveAction.Typing(userId, true)));
        actions.AddRange(_typingUserIds.Except(nextTyping)
            .OrderBy(userId => userId)
            .Select(userId => new FirebaseRealtimeLiveAction.Typing(userId, false)));
        _typingUserIds = nextTyping;
        _typingDeadlines = nextDeadlines;

        foreach ((Guid userId, long timestamp) in snapshot.CharacterPulses.OrderBy(entry => entry.Key))
        {
            bool hadValue = _pulseHighWater.TryGetValue(userId, out long highWater);
            if (!hadValue || timestamp > highWater)
            {
                _pulseHighWater[userId] = timestamp;
                if (IsFresh(timestamp, receivedAtMilliseconds))
                {
                    actions.Add(new FirebaseRealtimeLiveAction.Pulse(userId));
                }
            }
        }
        foreach ((Guid userId, FirebaseRealtimeThrow payload) in snapshot.CharacterThrows.OrderBy(entry => entry.Key))
        {
            bool hadValue = _throwHighWater.TryGetValue(userId, out long highWater);
            if (!hadValue || payload.Timestamp > highWater)
            {
                _throwHighWater[userId] = payload.Timestamp;
                if (IsFresh(payload.Timestamp, receivedAtMilliseconds))
                {
                    actions.Add(new FirebaseRealtimeLiveAction.Throw(userId, payload));
                }
            }
        }
        return actions;
    }

    public IReadOnlyList<FirebaseRealtimeLiveAction.Typing> ExpireTyping(long receivedAtMilliseconds)
    {
        Guid[] expired = [.. _typingDeadlines
            .Where(entry => entry.Value <= receivedAtMilliseconds)
            .Select(entry => entry.Key)
            .OrderBy(userId => userId)];
        foreach (Guid userId in expired)
        {
            _typingDeadlines.Remove(userId);
            _typingUserIds.Remove(userId);
        }
        return [.. expired.Select(userId => new FirebaseRealtimeLiveAction.Typing(userId, false))];
    }

    public long? NextTypingExpiryMilliseconds =>
        _typingDeadlines.Count == 0 ? null : _typingDeadlines.Values.Min();

    private static Dictionary<Guid, long> FreshTypingDeadlines(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, long>> sessionsByUser,
        long receivedAtMilliseconds)
    {
        Dictionary<Guid, long> deadlines = [];
        foreach ((Guid userId, IReadOnlyDictionary<Guid, long> sessions) in sessionsByUser)
        {
            if (sessions.Count == 0)
            {
                continue;
            }
            long freshest = sessions.Values.Max();
            long age = receivedAtMilliseconds - freshest;
            if (age < -FreshnessWindowMilliseconds || age > TypingTtlMilliseconds)
            {
                continue;
            }
            deadlines[userId] = Math.Min(
                checked(freshest + TypingTtlMilliseconds),
                checked(receivedAtMilliseconds + TypingTtlMilliseconds));
        }
        return deadlines;
    }

    private static bool IsFresh(long timestamp, long receivedAtMilliseconds)
    {
        long age = receivedAtMilliseconds - timestamp;
        return age is >= 0 and <= FreshnessWindowMilliseconds;
    }
}
