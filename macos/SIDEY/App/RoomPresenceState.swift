import Foundation
import Observation

@MainActor
@Observable
final class RoomPresenceState {
    private var basePresence: [MemberPresenceKey: PresenceState] = [:]
    private var typingMembers: Set<MemberPresenceKey> = []
    private var suppressedMembers: Set<MemberPresenceKey> = []
    private(set) var activeRoomTransportConnected = false

    func isOverlaySuppressed(roomID: UUID, userID: UUID) -> Bool {
        suppressedMembers.contains(MemberPresenceKey(roomID: roomID, userID: userID))
    }

    func updateOverlaySuppression(roomID: UUID, userID: UUID, suppressed: Bool) {
        let key = MemberPresenceKey(roomID: roomID, userID: userID)
        if suppressed { suppressedMembers.insert(key) } else { suppressedMembers.remove(key) }
    }

    func clearOverlaySuppression() { suppressedMembers.removeAll() }

    func isTyping(roomID: UUID, userID: UUID) -> Bool {
        typingMembers.contains(MemberPresenceKey(roomID: roomID, userID: userID))
    }

    func baseState(roomID: UUID, userID: UUID) -> PresenceState? {
        basePresence[MemberPresenceKey(roomID: roomID, userID: userID)]
    }

    func reconcile(_ incomingRooms: [Room]) -> [Room] {
        var updatedRooms = incomingRooms
        let previousBasePresence = basePresence
        let previousTypingMembers = typingMembers
        for roomIndex in updatedRooms.indices {
            for memberIndex in updatedRooms[roomIndex].members.indices {
                let member = updatedRooms[roomIndex].members[memberIndex]
                let key = MemberPresenceKey(roomID: updatedRooms[roomIndex].id, userID: member.userID)
                if let state = previousBasePresence[key] {
                    updatedRooms[roomIndex].members[memberIndex].presence = previousTypingMembers.contains(key)
                        ? .typing
                        : state
                }
            }
        }
        let validKeys = Set(updatedRooms.flatMap { room in
            room.members.map { member in
                MemberPresenceKey(roomID: room.id, userID: member.userID)
            }
        })
        basePresence = Dictionary(uniqueKeysWithValues: updatedRooms.flatMap { room in
            room.members.map { member in
                let key = MemberPresenceKey(roomID: room.id, userID: member.userID)
                return (key, previousBasePresence[key] ?? member.presence)
            }
        })
        typingMembers = previousTypingMembers.intersection(validKeys)
        suppressedMembers.formIntersection(validKeys)
        return updatedRooms
    }

    func updatePresence(roomID: UUID, userID: UUID, state: PresenceState, rooms: inout [Room]) {
        let key = MemberPresenceKey(roomID: roomID, userID: userID)
        basePresence[key] = state
        if state == .offline {
            typingMembers.remove(key)
            suppressedMembers.remove(key)
        }
        guard let roomIndex = rooms.firstIndex(where: { $0.id == roomID }),
              let memberIndex = rooms[roomIndex].members.firstIndex(where: { $0.userID == userID })
        else { return }
        rooms[roomIndex].members[memberIndex].presence = typingMembers.contains(key) ? .typing : state
    }

    func updateTyping(roomID: UUID, userID: UUID, active: Bool, rooms: inout [Room]) {
        let key = MemberPresenceKey(roomID: roomID, userID: userID)
        if active {
            typingMembers.insert(key)
        } else {
            typingMembers.remove(key)
        }
        guard let roomIndex = rooms.firstIndex(where: { $0.id == roomID }),
              let memberIndex = rooms[roomIndex].members.firstIndex(where: { $0.userID == userID })
        else { return }
        if active {
            rooms[roomIndex].members[memberIndex].presence = .typing
        } else {
            rooms[roomIndex].members[memberIndex].presence = basePresence[key] ?? .online
        }
    }

    func setConnected(_ connected: Bool, activeRoomID: UUID?, currentUserID: UUID?, rooms: inout [Room]) {
        activeRoomTransportConnected = connected
        // Typing is a transient Broadcast lease. A disconnect can lose the
        // matching typing_stop event, so never carry typing across reconnect.
        guard let activeRoomID,
              let roomIndex = rooms.firstIndex(where: { $0.id == activeRoomID })
        else { return }
        if !connected {
            typingMembers = typingMembers.filter { $0.roomID != activeRoomID }
            suppressedMembers = suppressedMembers.filter { $0.roomID != activeRoomID }
        }
        for memberIndex in rooms[roomIndex].members.indices {
            let member = rooms[roomIndex].members[memberIndex]
            let key = MemberPresenceKey(roomID: activeRoomID, userID: member.userID)
            if connected {
                rooms[roomIndex].members[memberIndex].presence = typingMembers.contains(key)
                    ? .typing
                    : (basePresence[key] ?? .offline)
            } else if member.presence != .offline {
                rooms[roomIndex].members[memberIndex].presence = .reconnecting
                if member.userID != currentUserID {
                    // Presence state is a lease on this active room's channel.
                    // Do not invalidate unrelated rooms during a selective swap.
                    basePresence[key] = .offline
                }
            }
        }
    }
}

private struct MemberPresenceKey: Hashable {
    let roomID: UUID
    let userID: UUID
}
