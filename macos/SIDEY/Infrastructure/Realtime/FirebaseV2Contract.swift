import Foundation

struct RealtimeRevision: RawRepresentable, Codable, Comparable, Hashable, Sendable {
    let rawValue: String

    init?(rawValue: String) {
        guard rawValue.utf8.count == 20,
              rawValue.utf8.allSatisfy({ (48...57).contains($0) })
        else { return nil }
        self.rawValue = rawValue
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(String.self)
        guard let revision = Self(rawValue: value) else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Realtime revision must be exactly 20 ASCII decimal digits."
            )
        }
        self = revision
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }

    static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

struct FirebaseV2WireCode: RawRepresentable, Codable, Hashable, Sendable {
    let rawValue: String

    init?(rawValue: String) {
        let bytes = rawValue.utf8
        guard (1...6).contains(bytes.count),
              bytes.allSatisfy({ (48...57).contains($0) }),
              rawValue == "0" || bytes.first != 48
        else { return nil }
        self.rawValue = rawValue
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(String.self)
        guard let code = Self(rawValue: value) else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Wire code must be a canonical decimal string from 0 through 999999."
            )
        }
        self = code
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

struct FirebaseV2CustomToken: Codable, Equatable, Sendable, CustomStringConvertible,
    CustomDebugStringConvertible {
    let value: String

    init(value: String) throws {
        guard !value.isEmpty else { throw FirebaseV2ContractError.emptyCustomToken }
        self.value = value
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(String.self)
        guard !value.isEmpty else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Firebase custom token cannot be empty."
            )
        }
        self.value = value
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(value)
    }

    var description: String { "<redacted Firebase custom token>" }
    var debugDescription: String { description }
}

enum FirebaseV2ContractError: Error, Equatable {
    case emptyCustomToken
    case invalidRolloutLease
}

struct FirebaseV2RolloutLeaseSchedule: Equatable, Sendable {
    static let maximumLifetimeMilliseconds: Int64 = 1_800_000
    static let refreshLeadMilliseconds: Int64 = 30_000
    static let maximumSafeInteger: Int64 = 9_007_199_254_740_991

    let refreshAfterMilliseconds: Int64
    let expiresAtMilliseconds: Int64
    let referenceMilliseconds: Int64?

    init(
        refreshAfterMilliseconds: Int64,
        expiresAtMilliseconds: Int64,
        referenceMilliseconds: Int64? = nil
    ) throws {
        guard (1...Self.maximumSafeInteger).contains(refreshAfterMilliseconds),
              (1...Self.maximumSafeInteger).contains(expiresAtMilliseconds),
              refreshAfterMilliseconds < expiresAtMilliseconds,
              expiresAtMilliseconds - refreshAfterMilliseconds <= Self.refreshLeadMilliseconds,
              (referenceMilliseconds.map {
                  (1...refreshAfterMilliseconds).contains($0)
              } ?? true)
        else {
            throw FirebaseV2ContractError.invalidRolloutLease
        }
        self.refreshAfterMilliseconds = refreshAfterMilliseconds
        self.expiresAtMilliseconds = expiresAtMilliseconds
        self.referenceMilliseconds = referenceMilliseconds
    }
}

struct FirebaseV2BootstrapRequest: Codable, Equatable, Sendable {
    let minimumAccessRevision: RealtimeRevision?

    init(minimumAccessRevision: RealtimeRevision? = nil) {
        self.minimumAccessRevision = minimumAccessRevision
    }

    private enum CodingKeys: String, CodingKey {
        case minimumAccessRevision
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        minimumAccessRevision = container.contains(.minimumAccessRevision)
            ? try container.decode(RealtimeRevision.self, forKey: .minimumAccessRevision)
            : nil
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encodeIfPresent(minimumAccessRevision, forKey: .minimumAccessRevision)
    }
}

enum FirebaseV2PermissionSync: String, Codable, Equatable, Sendable {
    case eventDriven = "event-driven"
}

struct FirebaseV2BootstrapSuccess: Codable, Equatable, Sendable {
    let protocolVersion: Int
    let databaseURL: URL
    let firebaseApiKey: String
    let customToken: FirebaseV2CustomToken
    let permissionSync: FirebaseV2PermissionSync
    let authTokenLifetimeSeconds: Int
    let refreshAfter: Int64
    let rolloutLeaseExpiresAt: Int64
    private(set) var rolloutLeaseSchedule: FirebaseV2RolloutLeaseSchedule
    let accessRevision: RealtimeRevision
    let rooms: [UUID]
    let wireItems: [FirebaseV2WireCode]

    private enum CodingKeys: String, CodingKey, CaseIterable {
        case protocolVersion
        case databaseURL
        case firebaseApiKey
        case customToken
        case permissionSync
        case authTokenLifetimeSeconds
        case refreshAfter
        case rolloutLeaseExpiresAt
        case accessRevision
        case rooms
        case wireItems
    }

    init(from decoder: Decoder) throws {
        let dynamic = try decoder.container(keyedBy: FirebaseV2DynamicCodingKey.self)
        guard Set(dynamic.allKeys.map(\.stringValue)) == Set(CodingKeys.allCases.map(\.stringValue)) else {
            throw DecodingError.dataCorrupted(.init(
                codingPath: decoder.codingPath,
                debugDescription: "Bootstrap response keys do not match the Firebase v2 contract."
            ))
        }
        let container = try decoder.container(keyedBy: CodingKeys.self)
        protocolVersion = try container.decode(Int.self, forKey: .protocolVersion)
        databaseURL = try container.decode(URL.self, forKey: .databaseURL)
        firebaseApiKey = try container.decode(String.self, forKey: .firebaseApiKey)
        customToken = try container.decode(FirebaseV2CustomToken.self, forKey: .customToken)
        permissionSync = try container.decode(FirebaseV2PermissionSync.self, forKey: .permissionSync)
        authTokenLifetimeSeconds = try container.decode(Int.self, forKey: .authTokenLifetimeSeconds)
        refreshAfter = try container.decode(Int64.self, forKey: .refreshAfter)
        rolloutLeaseExpiresAt = try container.decode(Int64.self, forKey: .rolloutLeaseExpiresAt)
        rolloutLeaseSchedule = try FirebaseV2RolloutLeaseSchedule(
            refreshAfterMilliseconds: refreshAfter,
            expiresAtMilliseconds: rolloutLeaseExpiresAt
        )
        accessRevision = try container.decode(RealtimeRevision.self, forKey: .accessRevision)
        rooms = try container.decode([UUID].self, forKey: .rooms)
        wireItems = try container.decode([FirebaseV2WireCode].self, forKey: .wireItems)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(protocolVersion, forKey: .protocolVersion)
        try container.encode(databaseURL, forKey: .databaseURL)
        try container.encode(firebaseApiKey, forKey: .firebaseApiKey)
        try container.encode(customToken, forKey: .customToken)
        try container.encode(permissionSync, forKey: .permissionSync)
        try container.encode(authTokenLifetimeSeconds, forKey: .authTokenLifetimeSeconds)
        try container.encode(refreshAfter, forKey: .refreshAfter)
        try container.encode(rolloutLeaseExpiresAt, forKey: .rolloutLeaseExpiresAt)
        try container.encode(accessRevision, forKey: .accessRevision)
        try container.encode(rooms, forKey: .rooms)
        try container.encode(wireItems, forKey: .wireItems)
    }

    mutating func anchorRolloutLeaseSchedule(
        receivedAtMilliseconds: Int64,
        serverNowMilliseconds: Int64
    ) throws {
        let remainingLifetime = rolloutLeaseExpiresAt - serverNowMilliseconds
        // HTTP Date has one-second precision. Permit only that rounding window;
        // the server still owns the exact thirty-minute maximum.
        guard remainingLifetime > 0,
              remainingLifetime <= FirebaseV2RolloutLeaseSchedule.maximumLifetimeMilliseconds
                + 1_000 else {
            throw FirebaseV2ContractError.invalidRolloutLease
        }
        let remainingUntilRefresh = max(0, refreshAfter - serverNowMilliseconds)
        rolloutLeaseSchedule = try FirebaseV2RolloutLeaseSchedule(
            refreshAfterMilliseconds: receivedAtMilliseconds + min(
                remainingUntilRefresh,
                remainingLifetime - 1
            ),
            expiresAtMilliseconds: receivedAtMilliseconds + remainingLifetime,
            referenceMilliseconds: receivedAtMilliseconds
        )
    }

}

private struct FirebaseV2DynamicCodingKey: CodingKey {
    let stringValue: String
    let intValue: Int? = nil

    init?(stringValue: String) {
        self.stringValue = stringValue
    }

    init?(intValue: Int) {
        return nil
    }
}

enum FirebaseV2BootstrapFailure: Equatable, Sendable {
    case invalidArgument
    case authenticationRequired
    case methodNotAllowed
    case grantNotConverged
    case rolloutDisabled
    case rateLimited
    case unavailable
    case unexpectedStatus(Int)

    init(statusCode: Int) {
        switch statusCode {
        case 400:
            self = .invalidArgument
        case 401:
            self = .authenticationRequired
        case 405:
            self = .methodNotAllowed
        case 409:
            self = .grantNotConverged
        case 429:
            self = .rateLimited
        case 503:
            self = .unavailable
        default:
            self = .unexpectedStatus(statusCode)
        }
    }

    init(statusCode: Int, errorCode: String?) {
        if statusCode == 409, errorCode == "realtime_rollout_disabled" {
            self = .rolloutDisabled
        } else {
            self.init(statusCode: statusCode)
        }
    }
}

enum FirebaseV2Path {
    static let liveRoot = "/v2/l"
    static let inboxRoot = "/v2/n"

    static func liveRoom(_ roomID: UUID) -> String {
        "\(liveRoot)/\(component(roomID))"
    }

    static func inbox(userID: UUID) -> String {
        "\(inboxRoot)/\(component(userID))"
    }

    static func typing(roomID: UUID, userID: UUID, sessionID: UUID) -> String {
        "\(liveRoom(roomID))/t/\(component(userID))/\(component(sessionID))"
    }

    static func pulse(roomID: UUID, userID: UUID) -> String {
        "\(liveRoom(roomID))/c/\(component(userID))"
    }

    static func characterThrow(roomID: UUID, userID: UUID) -> String {
        "\(liveRoom(roomID))/x/\(component(userID))"
    }

    static func chatEvent(roomID: UUID) -> String {
        "\(liveRoom(roomID))/e"
    }

    private static func component(_ id: UUID) -> String {
        id.uuidString.lowercased()
    }
}

struct FirebaseV2Timestamp: RawRepresentable, Codable, Comparable, Hashable, Sendable {
    let rawValue: Int64

    init?(rawValue: Int64) {
        guard rawValue > 0 else { return nil }
        self.rawValue = rawValue
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(Int64.self)
        guard let timestamp = Self(rawValue: value) else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Firebase server timestamp must be positive."
            )
        }
        self = timestamp
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }

    static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

struct FirebaseV2TypingPayload: Codable, Equatable, Sendable {
    let timestamp: FirebaseV2Timestamp

    init(timestamp: FirebaseV2Timestamp) {
        self.timestamp = timestamp
    }

    init(from decoder: Decoder) throws {
        timestamp = try FirebaseV2Timestamp(from: decoder)
    }

    func encode(to encoder: Encoder) throws {
        try timestamp.encode(to: encoder)
    }
}

struct FirebaseV2PulsePayload: Codable, Equatable, Sendable {
    let timestamp: FirebaseV2Timestamp

    init(timestamp: FirebaseV2Timestamp) {
        self.timestamp = timestamp
    }

    init(from decoder: Decoder) throws {
        timestamp = try FirebaseV2Timestamp(from: decoder)
    }

    func encode(to encoder: Encoder) throws {
        try timestamp.encode(to: encoder)
    }
}

struct FirebaseV2ThrowPayload: Codable, Equatable, Sendable {
    let wireCode: FirebaseV2WireCode
    let timestamp: FirebaseV2Timestamp
    let targetUserID: UUID

    private enum CodingKeys: String, CodingKey {
        case wireCode = "k"
        case timestamp = "t"
        case targetUserID = "u"
    }
}

struct FirebaseV2ChatEventPayload: Codable, Equatable, Sendable {
    let body: String
    let messageID: UUID
    let bubbleWireCode: FirebaseV2WireCode?
    let sequence: Int64
    let senderUserID: UUID
    let timestamp: FirebaseV2Timestamp

    private enum CodingKeys: String, CodingKey {
        case body = "b"
        case messageID = "i"
        case bubbleWireCode = "k"
        case sequence = "n"
        case senderUserID = "s"
        case timestamp = "t"
    }
}

enum FirebaseV2TransientObservation: Equatable, Sendable {
    case baseline
    case animate
    case duplicateOrStale
    case outsideFreshnessWindow
}

struct FirebaseV2TransientHighWater: Equatable, Sendable {
    static let freshnessWindowMilliseconds: Int64 = 5_000
    // RTDB's server offset is an estimate affected by network latency. A small
    // future value is still a live interaction, not a reason to discard it.
    static let futureToleranceMilliseconds: Int64 = 1_000

    static func isFresh(
        _ timestamp: FirebaseV2Timestamp,
        receivedAtMilliseconds: Int64
    ) -> Bool {
        let (age, overflow) = receivedAtMilliseconds.subtractingReportingOverflow(timestamp.rawValue)
        return !overflow
            && (-futureToleranceMilliseconds...freshnessWindowMilliseconds).contains(age)
    }

    private(set) var latestTimestamp: FirebaseV2Timestamp?

    mutating func observe(
        _ timestamp: FirebaseV2Timestamp,
        receivedAtMilliseconds: Int64
    ) -> FirebaseV2TransientObservation {
        guard let latestTimestamp else {
            self.latestTimestamp = timestamp
            return .baseline
        }
        guard timestamp > latestTimestamp else { return .duplicateOrStale }

        self.latestTimestamp = timestamp
        guard Self.isFresh(timestamp, receivedAtMilliseconds: receivedAtMilliseconds) else {
            return .outsideFreshnessWindow
        }
        return .animate
    }
}
