import Foundation

/// A per-transport event subscription over the one authenticated SideyBackend.
/// Transport retirement may finish this subscription without terminating the
/// backend socket needed by the replacement transport.
actor SideyBackendRealtimeAdapter: RoomMessagingTransport, FirebaseV2SupabasePlane {
    nonisolated let kind: RealtimeTransportKind = .legacySupabase
    nonisolated let events: AsyncStream<BackendEvent>

    private let backend: SideyBackend

    init(backend: SideyBackend, events: AsyncStream<BackendEvent>) {
        self.backend = backend
        self.events = events
    }

    func synchronize(
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation {
        try await backend.syncRealtime(rooms: rooms, activeRoomID: activeRoomID)
    }

    func message(id: UUID, roomID: UUID) async throws -> ChatMessage? {
        try await backend.message(id: id, roomID: roomID)
    }

    func reconcilePreservingActiveRoom() async throws -> BackendReconciliation {
        try await backend.reconcilePreservingActiveRoom()
    }

    func repairDroppedEventsPreservingActiveRoom() async throws -> BackendEventGapRepair {
        try await backend.repairDroppedEventsPreservingActiveRoom()
    }

    func recentMessages(roomID: UUID) async throws -> [ChatMessage] {
        try await backend.recentMessages(roomID: roomID)
    }

    func repairDroppedEvents(rooms: [Room], activeRoomID: UUID?) async throws -> BackendEventGapRepair {
        try await backend.repairRealtimeEventGap(rooms: rooms, activeRoomID: activeRoomID)
    }

    func setActiveRoom(_ roomID: UUID?) async throws {
        try await backend.setActiveRoom(roomID)
    }

    func setLocalPresence(_ state: PresenceState) async throws {
        try await backend.setLocalPresence(state)
    }

    func publishTyping(roomID: UUID, event: String) async throws {
        try await backend.broadcastTyping(roomID: roomID, event: event)
    }

    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        try await backend.broadcastCharacterPulse(roomID: roomID, eventID: eventID)
    }

    func publishCharacterThrow(
        roomID: UUID,
        eventID: UUID,
        targetUserID: UUID
    ) async throws {
        try await backend.broadcastCharacterThrow(
            roomID: roomID,
            eventID: eventID,
            targetUserID: targetUserID
        )
    }

    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome {
        do {
            return .confirmed(try await backend.sendMessage(roomID: roomID, body: body, id: id))
        } catch {
            return .definitelyRejected(message: error.localizedDescription)
        }
    }

    /// The shared backend is owned by the router and survives adapter switches.
    func shutdown() async {}
}
