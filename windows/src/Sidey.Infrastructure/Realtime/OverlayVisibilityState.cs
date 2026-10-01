using System.Text.Json.Serialization;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;

namespace Sidey.Infrastructure.Realtime;

internal sealed record OverlayVisibilityRecord(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("visibility_id")] Guid VisibilityId,
    [property: JsonPropertyName("suppressed")] bool Suppressed,
    [property: JsonPropertyName("revision")] long Revision);

internal sealed record OverlayPresenceConnection(
    string Reference,
    string? PreviousReference,
    Guid? VisibilityId,
    PresenceState State);

/// <summary>Server-authored status joined to individual live Presence references.</summary>
internal sealed class OverlayVisibilityState
{
    private const int MaximumRecords = 512;
    private const int MaximumUsersPerRoom = 32;
    private const int MaximumConnectionsPerUser = 32;
    private readonly Dictionary<Guid, OverlayVisibilityRecord> _records = [];
    private readonly Dictionary<Guid, Dictionary<Guid, Dictionary<string, OverlayPresenceConnection>>> _connections = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _overflowed = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _members = [];
    private readonly Dictionary<(Guid RoomId, Guid UserId), bool> _published = [];

    public long Revision => _records.Values.Select(row => row.Revision).DefaultIfEmpty().Max();

    public void Reset()
    {
        _records.Clear();
        _connections.Clear();
        _overflowed.Clear();
        _members.Clear();
        _published.Clear();
    }

    public void SetMembers(IReadOnlyList<Room> rooms)
    {
        var validRooms = rooms.Select(room => room.Id).ToHashSet();
        foreach (Guid roomId in _members.Keys.Where(id => !validRooms.Contains(id)).ToArray())
        {
            _members.Remove(roomId);
            _connections.Remove(roomId);
            _overflowed.Remove(roomId);
        }
        foreach (Room room in rooms)
            _members[room.Id] = [.. room.Members.Select(member => member.UserId)];
        var validUsers = _members.Values.SelectMany(users => users).ToHashSet();
        foreach (Guid id in _records.Keys.Where(id => !validUsers.Contains(_records[id].UserId)).ToArray())
            _records.Remove(id);
        foreach ((Guid RoomId, Guid UserId) key in _published.Keys.Where(key =>
            !_members.TryGetValue(key.RoomId, out HashSet<Guid>? users) || !users.Contains(key.UserId)).ToArray())
            _published.Remove(key);
    }

    public void Merge(IEnumerable<OverlayVisibilityRecord> rows)
    {
        foreach (OverlayVisibilityRecord row in rows)
        {
            if (row.Revision <= 0 || row.UserId == Guid.Empty || row.VisibilityId == Guid.Empty)
                continue;
            if (_records.TryGetValue(row.VisibilityId, out OverlayVisibilityRecord? previous)
                && previous.Revision >= row.Revision)
                continue;
            if (previous is null && _records.Count >= MaximumRecords)
                continue;
            _records[row.VisibilityId] = row;
        }
    }

    public void Reconcile(IEnumerable<OverlayVisibilityRecord> rows, long startedAtRevision)
    {
        OverlayVisibilityRecord[] snapshot = [.. rows];
        var retained = snapshot.Select(row => row.VisibilityId).ToHashSet();
        foreach (Guid id in _records.Keys.Where(id =>
            !retained.Contains(id) && _records[id].Revision <= startedAtRevision).ToArray())
            _records.Remove(id);
        Merge(snapshot);
    }

    public void ResetConnections()
    {
        _connections.Clear();
        _overflowed.Clear();
    }

    public void ResetRoom(Guid roomId)
    {
        _connections.Remove(roomId);
        _overflowed.Remove(roomId);
    }

    public IReadOnlySet<Guid> ConnectedUsers(Guid roomId) =>
        _connections.TryGetValue(roomId, out Dictionary<Guid, Dictionary<string, OverlayPresenceConnection>>? users)
            ? [.. users.Keys] : new HashSet<Guid>();

    public void Update(
        Guid roomId,
        IReadOnlyDictionary<Guid, IReadOnlyList<OverlayPresenceConnection>> joins,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> leaves,
        bool full)
    {
        if (full)
            ResetRoom(roomId);
        if (!_connections.TryGetValue(roomId, out Dictionary<Guid, Dictionary<string, OverlayPresenceConnection>>? users))
        {
            users = [];
            _connections[roomId] = users;
        }
        foreach ((Guid userId, IReadOnlyList<string> references) in leaves)
        {
            if (!users.TryGetValue(userId, out Dictionary<string, OverlayPresenceConnection>? live))
                continue;
            foreach (string reference in references)
                live.Remove(reference);
            if (live.Count == 0)
            {
                users.Remove(userId);
                if (_overflowed.TryGetValue(roomId, out HashSet<Guid>? overflow))
                    overflow.Remove(userId);
            }
        }
        foreach ((Guid userId, IReadOnlyList<OverlayPresenceConnection> arrivals) in joins)
        {
            if (arrivals.Count == 0)
                continue;
            if (!users.TryGetValue(userId, out Dictionary<string, OverlayPresenceConnection>? live))
            {
                if (users.Count >= MaximumUsersPerRoom)
                    continue;
                live = [];
                users[userId] = live;
            }
            foreach (OverlayPresenceConnection connection in arrivals)
            {
                if (connection.PreviousReference is { } previous)
                    live.Remove(previous);
                if (!live.ContainsKey(connection.Reference) && live.Count >= MaximumConnectionsPerUser)
                {
                    if (!_overflowed.TryGetValue(roomId, out HashSet<Guid>? overflow))
                        _overflowed[roomId] = overflow = [];
                    overflow.Add(userId);
                    continue;
                }
                live[connection.Reference] = connection;
            }
        }
    }

    public PresenceState BasePresence(Guid roomId, Guid userId)
    {
        PresenceState[] states = [.. Connections(roomId, userId).Select(value => value.State)];
        if (states.Contains(PresenceState.Online) || states.Contains(PresenceState.Typing))
            return PresenceState.Online;
        if (states.Contains(PresenceState.Away))
            return PresenceState.Away;
        if (states.Contains(PresenceState.Reconnecting))
            return PresenceState.Reconnecting;
        return PresenceState.Offline;
    }

    public bool IsSuppressed(Guid roomId, Guid userId)
    {
        if (_overflowed.TryGetValue(roomId, out HashSet<Guid>? overflow) && overflow.Contains(userId))
            return false;
        OverlayPresenceConnection[] live = [.. Connections(roomId, userId).Where(connection =>
            connection.State is PresenceState.Online or PresenceState.Away or PresenceState.Typing)];
        return live.Length > 0 && live.All(connection =>
            connection.VisibilityId is { } id
            && _records.TryGetValue(id, out OverlayVisibilityRecord? row)
            && row.UserId == userId && row.Suppressed);
    }

    public IReadOnlyList<BackendEvent.OverlaySuppressionChanged> Changes()
    {
        var changes = new List<BackendEvent.OverlaySuppressionChanged>();
        foreach ((Guid roomId, HashSet<Guid> members) in _members)
        {
            foreach (Guid userId in members)
            {
                bool suppressed = IsSuppressed(roomId, userId);
                (Guid RoomId, Guid UserId) key = (roomId, userId);
                if (!_published.TryGetValue(key, out bool previous) || previous != suppressed)
                {
                    _published[key] = suppressed;
                    changes.Add(new BackendEvent.OverlaySuppressionChanged(roomId, userId, suppressed));
                }
            }
        }
        return changes;
    }

    private IEnumerable<OverlayPresenceConnection> Connections(Guid roomId, Guid userId) =>
        _connections.TryGetValue(roomId, out Dictionary<Guid, Dictionary<string, OverlayPresenceConnection>>? users)
            && users.TryGetValue(userId, out Dictionary<string, OverlayPresenceConnection>? live)
            ? live.Values : [];
}
