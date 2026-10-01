import Foundation

/// Server-authored state; the opaque ID identifies a login session, not a credential.
struct OverlayVisibilityRecord: Codable, Equatable, Sendable {
    let userID: UUID
    let visibilityID: UUID
    let suppressed: Bool
    let revision: Int64

    enum CodingKeys: String, CodingKey {
        case userID = "user_id", visibilityID = "visibility_id", suppressed, revision
    }
}

struct OverlayVisibilityParameters: Encodable, Sendable {
    let suppressed: Bool
    enum CodingKeys: String, CodingKey { case suppressed = "p_suppressed" }
}

/// Join server-authorized status with live Presence references. An old/unknown client
/// is deliberately treated as potentially viewing, never as confirmed suppression.
struct OverlayVisibilityState: Sendable {
    struct Connection: Equatable, Sendable {
        let reference: String
        let previousReference: String?
        let visibilityID: UUID?
        let state: PresenceState
    }
    private var records: [UUID: OverlayVisibilityRecord] = [:]
    private var connections: [UUID: [UUID: [String: Connection]]] = [:]
    private var overflowedUsers: [UUID: Set<UUID>] = [:]
    private var published: [UUID: [UUID: Bool]] = [:]
    var revision: Int64 { records.values.map(\.revision).max() ?? 0 }

    func connectedUserIDs(roomID: UUID) -> Set<UUID> {
        Set(connections[roomID]?.keys.map { $0 } ?? [])
    }

    mutating func reconcile(_ rows: [OverlayVisibilityRecord], startedAtRevision: Int64) {
        let retained = Set(rows.map(\.visibilityID))
        records = records.filter { retained.contains($0.key) || $0.value.revision > startedAtRevision }
        merge(rows)
    }

    mutating func merge(_ rows: [OverlayVisibilityRecord]) {
        for row in rows where row.revision > 0 {
            if let previous = records[row.visibilityID], previous.revision >= row.revision { continue }
            guard records[row.visibilityID] != nil || records.count < 512 else { continue }
            records[row.visibilityID] = row
        }
    }

    mutating func retain(userIDs: Set<UUID>) {
        records = records.filter { userIDs.contains($0.value.userID) }
    }

    mutating func resetRoom(_ roomID: UUID) {
        connections.removeValue(forKey: roomID)
        overflowedUsers.removeValue(forKey: roomID)
    }

    mutating func resetConnections() {
        connections.removeAll()
        overflowedUsers.removeAll()
    }

    mutating func update(roomID: UUID, joins: [UUID: [Connection]], leaves: [UUID: [String]], full: Bool) {
        if full { resetRoom(roomID) }
        for (userID, references) in leaves {
            for reference in references { connections[roomID]?[userID]?.removeValue(forKey: reference) }
            if connections[roomID]?[userID]?.isEmpty == true {
                connections[roomID]?.removeValue(forKey: userID)
                overflowedUsers[roomID]?.remove(userID)
            }
        }
        for (userID, arrivals) in joins {
            // Presence may precede the membership snapshot. Retain a bounded
            // set of early arrivals instead of discarding their only join.
            guard connections[roomID]?[userID] != nil
                    || (connections[roomID]?.count ?? 0) < 32 else { continue }
            for connection in arrivals {
                if let previous = connection.previousReference {
                    connections[roomID]?[userID]?.removeValue(forKey: previous)
                }
                guard connections[roomID]?[userID]?[connection.reference] != nil
                        || (connections[roomID]?[userID]?.count ?? 0) < 32 else {
                    overflowedUsers[roomID, default: []].insert(userID)
                    continue
                }
                connections[roomID, default: [:]][userID, default: [:]][connection.reference] = connection
            }
        }
    }

    func isSuppressed(roomID: UUID, userID: UUID) -> Bool {
        guard overflowedUsers[roomID]?.contains(userID) != true else { return false }
        let live = (connections[roomID]?[userID] ?? [:]).values.filter {
            $0.state == .online || $0.state == .away || $0.state == .typing
        }
        return !live.isEmpty && live.allSatisfy { connection in
            guard let id = connection.visibilityID, let row = records[id], row.userID == userID else { return false }
            return row.suppressed
        }
    }

    func basePresence(roomID: UUID, userID: UUID) -> PresenceState {
        let states = (connections[roomID]?[userID] ?? [:]).values.map(\.state)
        if states.contains(.online) || states.contains(.typing) { return .online }
        if states.contains(.away) { return .away }
        if states.contains(.reconnecting) { return .reconnecting }
        return .offline
    }

    func events(rooms: [Room]) -> [BackendEvent] {
        rooms.flatMap { room in room.members.map { member in
            .overlaySuppression(roomID: room.id, userID: member.userID,
                                suppressed: isSuppressed(roomID: room.id, userID: member.userID))
        } }
    }

    /// Live updates emit only changed members; subscriber/recovery baselines use
    /// events(rooms:) so a replaced or overflowed consumer still receives all state.
    mutating func changes(rooms: [Room]) -> [BackendEvent] {
        var next: [UUID: [UUID: Bool]] = [:]
        var changes: [BackendEvent] = []
        for room in rooms {
            for member in room.members {
                let suppressed = isSuppressed(roomID: room.id, userID: member.userID)
                next[room.id, default: [:]][member.userID] = suppressed
                if published[room.id]?[member.userID] != suppressed {
                    changes.append(.overlaySuppression(roomID: room.id, userID: member.userID, suppressed: suppressed))
                }
            }
        }
        published = next
        return changes
    }
}
