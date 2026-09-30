@preconcurrency import FirebaseDatabase
import Foundation

private let firebaseV2MaximumSafeInteger: Int64 = 9_007_199_254_740_991

struct FirebaseV2RoomHint: Equatable, Sendable {
    let revision: RealtimeRevision?
    let chatSequence: Int64?
}

struct FirebaseV2InboxSnapshot: Equatable, Sendable {
    let accessRevision: RealtimeRevision?
    let rooms: [UUID: FirebaseV2RoomHint]

    static func decode(_ data: Data) throws -> Self {
        let root = try FirebaseV2JSON.object(data)
        let accessRevision = try root["a"].map(FirebaseV2JSON.revision)
        let rawRooms = try FirebaseV2JSON.dictionary(root["r"], default: [:])
        var rooms: [UUID: FirebaseV2RoomHint] = [:]
        for (rawRoomID, rawHint) in rawRooms {
            guard let roomID = UUID(uuidString: rawRoomID) else { continue }
            let hint = try FirebaseV2JSON.dictionary(rawHint)
            rooms[roomID] = FirebaseV2RoomHint(
                revision: try hint["v"].map(FirebaseV2JSON.revision),
                chatSequence: try hint["n"].map(FirebaseV2JSON.safePositiveInteger)
            )
        }
        return Self(accessRevision: accessRevision, rooms: rooms)
    }
}

enum FirebaseV2InboxAction: Equatable, Sendable {
    case accessRevisionAdvanced(RealtimeRevision)
    case roomRevisionAdvanced(roomID: UUID, revision: RealtimeRevision)
    case chatSequenceAdvanced(roomID: UUID, sequence: Int64, hasGap: Bool)
}

struct FirebaseV2InboxReconciler: Sendable {
    private var baselineEstablished = false
    private var accessRevision: RealtimeRevision?
    private var roomHints: [UUID: FirebaseV2RoomHint] = [:]

    mutating func consume(_ snapshot: FirebaseV2InboxSnapshot) -> [FirebaseV2InboxAction] {
        guard baselineEstablished else {
            baselineEstablished = true
            accessRevision = snapshot.accessRevision
            roomHints = snapshot.rooms
            return []
        }

        var actions: [FirebaseV2InboxAction] = []
        if let next = snapshot.accessRevision,
           accessRevision.map({ next > $0 }) ?? true {
            actions.append(.accessRevisionAdvanced(next))
            accessRevision = next
        }

        // Removed memberships must not keep high-water state indefinitely or
        // make a later rejoin compare against the departed membership baseline.
        roomHints = roomHints.filter { snapshot.rooms[$0.key] != nil }
        for (roomID, next) in snapshot.rooms {
            guard let previous = roomHints[roomID] else {
                roomHints[roomID] = next
                continue
            }
            if let revision = next.revision,
               previous.revision.map({ revision > $0 }) ?? true {
                actions.append(.roomRevisionAdvanced(roomID: roomID, revision: revision))
            }
            if let sequence = next.chatSequence {
                if let oldSequence = previous.chatSequence, sequence > oldSequence {
                    actions.append(.chatSequenceAdvanced(
                        roomID: roomID,
                        sequence: sequence,
                        hasGap: sequence > oldSequence + 1
                    ))
                } else if previous.chatSequence == nil {
                    actions.append(.chatSequenceAdvanced(
                        roomID: roomID,
                        sequence: sequence,
                        hasGap: sequence > 1
                    ))
                }
            }
            roomHints[roomID] = FirebaseV2RoomHint(
                revision: max(previous.revision, next.revision),
                chatSequence: max(previous.chatSequence, next.chatSequence)
            )
        }
        return actions
    }
}

struct FirebaseV2LiveSnapshot: Equatable, Sendable {
    let typingSessions: [UUID: [UUID: FirebaseV2Timestamp]]
    let pulses: [UUID: FirebaseV2Timestamp]
    let throwEvents: [UUID: FirebaseV2ThrowPayload]
    let chatEvent: FirebaseV2ChatEventPayload?

    var typingUserIDs: Set<UUID> { Set(typingSessions.keys) }

    static func decode(_ data: Data) throws -> Self {
        let root = try FirebaseV2JSON.object(data)
        let typingRoot = try FirebaseV2JSON.dictionary(root["t"], default: [:])
        var typingSessions: [UUID: [UUID: FirebaseV2Timestamp]] = [:]
        for (rawUserID, rawSessions) in typingRoot {
            guard let userID = UUID(uuidString: rawUserID) else { continue }
            let sessions = try FirebaseV2JSON.dictionary(rawSessions)
            var decodedSessions: [UUID: FirebaseV2Timestamp] = [:]
            for (rawSessionID, rawTimestamp) in sessions {
                guard let sessionID = UUID(uuidString: rawSessionID) else { continue }
                decodedSessions[sessionID] = try FirebaseV2JSON.timestamp(rawTimestamp)
            }
            if !decodedSessions.isEmpty {
                typingSessions[userID] = decodedSessions
            }
        }

        let pulseRoot = try FirebaseV2JSON.dictionary(root["c"], default: [:])
        var pulses: [UUID: FirebaseV2Timestamp] = [:]
        for (rawUserID, value) in pulseRoot {
            guard let userID = UUID(uuidString: rawUserID) else { continue }
            pulses[userID] = try FirebaseV2JSON.timestamp(value)
        }

        let throwRoot = try FirebaseV2JSON.dictionary(root["x"], default: [:])
        var throwEvents: [UUID: FirebaseV2ThrowPayload] = [:]
        for (rawUserID, value) in throwRoot {
            guard let userID = UUID(uuidString: rawUserID) else { continue }
            throwEvents[userID] = try FirebaseV2JSON.decode(FirebaseV2ThrowPayload.self, value)
        }

        return Self(
            typingSessions: typingSessions,
            pulses: pulses,
            throwEvents: throwEvents,
            chatEvent: try root["e"].map { try FirebaseV2JSON.decode(
                FirebaseV2ChatEventPayload.self,
                $0
            ) }
        )
    }
}

enum FirebaseV2LiveAction: Equatable, Sendable {
    case typing(userID: UUID, active: Bool)
    case pulse(userID: UUID, timestamp: FirebaseV2Timestamp)
    case characterThrow(actorUserID: UUID, payload: FirebaseV2ThrowPayload)
    case chatHint(FirebaseV2ChatEventPayload)
}

struct FirebaseV2LiveReconciler: Sendable {
    static let typingTTLMilliseconds: Int64 = 6_000

    private var baselineEstablished = false
    private var typingUserIDs = Set<UUID>()
    private var typingDeadlineByUserID: [UUID: Int64] = [:]
    private var pulseHighWater: [UUID: FirebaseV2TransientHighWater] = [:]
    private var throwHighWater: [UUID: FirebaseV2TransientHighWater] = [:]
    private var chatSequence: Int64?

    mutating func consume(
        _ snapshot: FirebaseV2LiveSnapshot,
        receivedAtMilliseconds: Int64
    ) -> [FirebaseV2LiveAction] {
        let nextTypingDeadlines = Self.freshTypingDeadlines(
            snapshot.typingSessions,
            receivedAtMilliseconds: receivedAtMilliseconds
        )
        let nextTypingUserIDs = Set(nextTypingDeadlines.keys)
        guard baselineEstablished else {
            baselineEstablished = true
            typingUserIDs = nextTypingUserIDs
            typingDeadlineByUserID = nextTypingDeadlines
            for (userID, timestamp) in snapshot.pulses {
                var tracker = FirebaseV2TransientHighWater()
                _ = tracker.observe(timestamp, receivedAtMilliseconds: receivedAtMilliseconds)
                pulseHighWater[userID] = tracker
            }
            for (userID, payload) in snapshot.throwEvents {
                var tracker = FirebaseV2TransientHighWater()
                _ = tracker.observe(payload.timestamp, receivedAtMilliseconds: receivedAtMilliseconds)
                throwHighWater[userID] = tracker
            }
            chatSequence = snapshot.chatEvent?.sequence
            return nextTypingUserIDs
                .sorted { $0.uuidString < $1.uuidString }
                .map { .typing(userID: $0, active: true) }
        }

        var actions: [FirebaseV2LiveAction] = []
        for userID in nextTypingUserIDs.subtracting(typingUserIDs)
            .sorted(by: { $0.uuidString < $1.uuidString }) {
            actions.append(.typing(userID: userID, active: true))
        }
        for userID in typingUserIDs.subtracting(nextTypingUserIDs)
            .sorted(by: { $0.uuidString < $1.uuidString }) {
            actions.append(.typing(userID: userID, active: false))
        }
        typingUserIDs = nextTypingUserIDs
        typingDeadlineByUserID = nextTypingDeadlines

        for userID in snapshot.pulses.keys.sorted(by: { $0.uuidString < $1.uuidString }) {
            guard let timestamp = snapshot.pulses[userID] else { continue }
            let hadBaseline = pulseHighWater[userID] != nil
            var tracker = pulseHighWater[userID] ?? FirebaseV2TransientHighWater()
            let observation = tracker.observe(
                timestamp,
                receivedAtMilliseconds: receivedAtMilliseconds
            )
            let firstPostBaselineValueIsFresh = !hadBaseline
                && FirebaseV2TransientHighWater.isFresh(
                    timestamp, receivedAtMilliseconds: receivedAtMilliseconds
                )
            if observation == .animate || firstPostBaselineValueIsFresh {
                actions.append(.pulse(userID: userID, timestamp: timestamp))
            }
            pulseHighWater[userID] = tracker
        }
        for userID in snapshot.throwEvents.keys.sorted(by: { $0.uuidString < $1.uuidString }) {
            guard let payload = snapshot.throwEvents[userID] else { continue }
            let hadBaseline = throwHighWater[userID] != nil
            var tracker = throwHighWater[userID] ?? FirebaseV2TransientHighWater()
            let observation = tracker.observe(
                payload.timestamp,
                receivedAtMilliseconds: receivedAtMilliseconds
            )
            let firstPostBaselineValueIsFresh = !hadBaseline
                && FirebaseV2TransientHighWater.isFresh(
                    payload.timestamp, receivedAtMilliseconds: receivedAtMilliseconds
                )
            if observation == .animate || firstPostBaselineValueIsFresh {
                actions.append(.characterThrow(actorUserID: userID, payload: payload))
            }
            throwHighWater[userID] = tracker
        }
        if let event = snapshot.chatEvent,
           chatSequence.map({ event.sequence > $0 }) ?? true {
            actions.append(.chatHint(event))
            chatSequence = event.sequence
        }
        return actions
    }

    /// Expires orphaned connection slots even when Firebase never delivers a
    /// removal snapshot (for example, after a peer crashes during typing).
    mutating func expireTyping(receivedAtMilliseconds: Int64) -> [FirebaseV2LiveAction] {
        let expired = typingDeadlineByUserID.compactMap { userID, deadline in
            deadline <= receivedAtMilliseconds ? userID : nil
        }.sorted { $0.uuidString < $1.uuidString }
        for userID in expired {
            typingDeadlineByUserID.removeValue(forKey: userID)
            typingUserIDs.remove(userID)
        }
        return expired.map { .typing(userID: $0, active: false) }
    }

    var nextTypingExpiryMilliseconds: Int64? {
        typingDeadlineByUserID.values.min()
    }

    private static func freshTypingDeadlines(
        _ sessionsByUserID: [UUID: [UUID: FirebaseV2Timestamp]],
        receivedAtMilliseconds: Int64
    ) -> [UUID: Int64] {
        var deadlines: [UUID: Int64] = [:]
        for (userID, sessions) in sessionsByUserID {
            let freshestTimestamp = sessions.values.map(\.rawValue).max()
            guard let freshestTimestamp else { continue }
            let age = receivedAtMilliseconds - freshestTimestamp
            guard age >= -FirebaseV2TransientHighWater.freshnessWindowMilliseconds,
                  age <= typingTTLMilliseconds else { continue }
            // A future-skewed server value must not keep typing alive longer
            // than one local TTL after receipt.
            deadlines[userID] = min(
                freshestTimestamp + typingTTLMilliseconds,
                receivedAtMilliseconds + typingTTLMilliseconds
            )
        }
        return deadlines
    }
}

protocol FirebaseV2DatabaseValueStreaming: Sendable {
    func values(at path: String) -> AsyncThrowingStream<Data, Error>
}

enum FirebaseV2DatabaseSnapshotSerializationError: Error, Equatable {
    case expectedObject
}

enum FirebaseV2DatabaseSnapshotSerializer {
    static func data(from value: Any?) throws -> Data {
        let object: [String: Any]
        switch value {
        case nil, is NSNull:
            object = [:]
        case let dictionary as [String: Any]:
            object = dictionary
        default:
            throw FirebaseV2DatabaseSnapshotSerializationError.expectedObject
        }
        return try JSONSerialization.data(withJSONObject: object)
    }
}

/// Converts Firebase callback snapshots to immutable JSON bytes before they
/// cross a concurrency boundary. Cancelling the stream removes exactly the
/// observer installed for that stream.
final class FirebaseV2DatabaseValueStream: @unchecked Sendable, FirebaseV2DatabaseValueStreaming {
    private let database: Database

    init(database: Database) {
        self.database = database
    }

    func values(at path: String) -> AsyncThrowingStream<Data, Error> {
        // .value callbacks contain the entire current node. Keep only its
        // latest state; durable sequence gaps are repaired through Postgres.
        let pair = Self.makeSnapshotStream()
        let continuation = pair.continuation
        let reference = database.reference(withPath: path)
        let handle = reference.observe(.value) { snapshot in
            do {
                let data = try FirebaseV2DatabaseSnapshotSerializer.data(
                    from: snapshot.value
                )
                continuation.yield(data)
            } catch {
                continuation.finish(throwing: error)
            }
        } withCancel: { error in
            continuation.finish(throwing: error)
        }
        let observer = FirebaseV2ObserverCancellation(reference: reference, handle: handle)
        continuation.onTermination = { @Sendable _ in
            observer.cancel()
        }
        return pair.stream
    }

    static func makeSnapshotStream() -> (
        stream: AsyncThrowingStream<Data, Error>,
        continuation: AsyncThrowingStream<Data, Error>.Continuation
    ) {
        AsyncThrowingStream.makeStream(bufferingPolicy: .bufferingNewest(1))
    }
}

final class FirebaseV2ObserverCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private let reference: DatabaseReference
    private let handle: DatabaseHandle
    private var cancelled = false

    init(reference: DatabaseReference, handle: DatabaseHandle) {
        self.reference = reference
        self.handle = handle
    }

    func cancel() {
        lock.lock()
        defer { lock.unlock() }
        guard !cancelled else { return }
        cancelled = true
        reference.removeObserver(withHandle: handle)
    }
}

private enum FirebaseV2JSON {
    enum Error: Swift.Error, Equatable {
        case expectedObject
        case invalidRevision
        case invalidSafePositiveInteger
        case invalidTimestamp
    }

    static func object(_ data: Data) throws -> [String: Any] {
        try dictionary(JSONSerialization.jsonObject(with: data))
    }

    static func dictionary(
        _ value: Any?,
        default defaultValue: [String: Any]? = nil
    ) throws -> [String: Any] {
        if value == nil || value is NSNull, let defaultValue { return defaultValue }
        guard let result = value as? [String: Any] else { throw Error.expectedObject }
        return result
    }

    static func revision(_ value: Any) throws -> RealtimeRevision {
        guard let rawValue = value as? String,
              let revision = RealtimeRevision(rawValue: rawValue) else {
            throw Error.invalidRevision
        }
        return revision
    }

    static func safePositiveInteger(_ value: Any) throws -> Int64 {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID() else {
            throw Error.invalidSafePositiveInteger
        }
        let doubleValue = number.doubleValue
        let integer = number.int64Value
        guard doubleValue.isFinite,
              doubleValue == Double(integer),
              (1...firebaseV2MaximumSafeInteger).contains(integer) else {
            throw Error.invalidSafePositiveInteger
        }
        return integer
    }

    static func timestamp(_ value: Any) throws -> FirebaseV2Timestamp {
        let integer = try safePositiveInteger(value)
        guard let timestamp = FirebaseV2Timestamp(rawValue: integer) else {
            throw Error.invalidTimestamp
        }
        return timestamp
    }

    static func decode<T: Decodable>(_ type: T.Type, _ value: Any) throws -> T {
        let data = try JSONSerialization.data(withJSONObject: value)
        return try JSONDecoder().decode(type, from: data)
    }
}

private func max<T: Comparable>(_ lhs: T?, _ rhs: T?) -> T? {
    switch (lhs, rhs) {
    case (.none, .none): nil
    case (.some(let value), .none), (.none, .some(let value)): value
    case (.some(let left), .some(let right)): Swift.max(left, right)
    }
}
