import CoreGraphics
import Foundation

enum OverlayMode: String, Codable, Sendable {
    case locked
    case editing
}

enum OverlayVisibility: String, Codable, Sendable {
    case hidden
    case visible

    init(isVisible: Bool) {
        self = isVisible ? .visible : .hidden
    }

    var isVisible: Bool { self == .visible }
}

enum OverlayEdge: String, Codable, CaseIterable, Identifiable, Sendable {
    case bottom
    case left
    case right
    case top

    var id: String { rawValue }

    var title: String {
        switch self {
        case .bottom: L10n.text("settings.placement.edge.bottom")
        case .left: L10n.text("settings.placement.edge.left")
        case .right: L10n.text("settings.placement.edge.right")
        case .top: L10n.text("settings.placement.edge.top")
        }
    }

    var isHorizontal: Bool { self == .bottom || self == .top }

    /// Pixel art is authored feet-down. Rotate the complete presentation so
    /// the feet point toward the selected screen edge.
    var presentationRotation: CGFloat {
        switch self {
        case .bottom: 0
        case .left: -.pi / 2
        case .right: .pi / 2
        case .top: .pi
        }
    }

    /// Characters rotate toward the screen edge. Keep readable UI upright
    /// by counter-rotating it at its own anchor.
    var readableContentCounterRotation: CGFloat {
        -presentationRotation
    }

    /// Keep the complete speech bubble upright, including its decoration.
    var bubbleCounterRotation: CGFloat {
        -presentationRotation
    }
}

enum OverlaySpan: String, Codable, CaseIterable, Identifiable, Sendable {
    case third
    case half
    case full

    var id: String { rawValue }

    var title: String {
        switch self {
        case .third: "1/3"
        case .half: "1/2"
        case .full: L10n.text("overlay.span.full")
        }
    }

    var fraction: CGFloat {
        switch self {
        case .third: 1.0 / 3.0
        case .half: 1.0 / 2.0
        case .full: 1
        }
    }
}

struct OverlayRegionPreference: Codable, Equatable, Sendable {
    var edge: OverlayEdge
    var span: OverlaySpan
    var screenIdentifier: String?

    static let defaultValue = OverlayRegionPreference(
        edge: .bottom,
        span: .full,
        screenIdentifier: nil
    )
}

struct OverlayScreenOption: Equatable, Identifiable, Sendable {
    let id: String
    let name: String
}

enum PresenceState: String, Codable, CaseIterable, Sendable {
    case online
    case typing
    case away
    case offline
    case reconnecting
}

struct ActiveBubble: Equatable, Identifiable, Sendable {
    var id: UUID { messageID }
    let senderID: UUID
    let messageID: UUID
    let body: String
    let expiresAt: Date
    let bubbleStyleID: String?

    init(
        senderID: UUID,
        messageID: UUID,
        body: String,
        expiresAt: Date,
        bubbleStyleID: String? = nil
    ) {
        self.senderID = senderID
        self.messageID = messageID
        self.body = body
        self.expiresAt = expiresAt
        self.bubbleStyleID = bubbleStyleID
    }
}

struct ActiveBubbleLedger: Equatable, Sendable {
    static let maximumVisiblePerSender = 2
    static let defaultLifetime: TimeInterval = 10

    private(set) var bubbles: [ActiveBubble] = []

    mutating func show(
        senderID: UUID,
        messageID: UUID,
        body: String,
        bubbleStyleID: String? = nil,
        expiresAt: Date = .now.addingTimeInterval(Self.defaultLifetime)
    ) {
        bubbles.removeAll { $0.messageID == messageID }
        bubbles.append(ActiveBubble(
            senderID: senderID,
            messageID: messageID,
            body: body,
            expiresAt: expiresAt,
            bubbleStyleID: bubbleStyleID
        ))
        bubbles.sort { lhs, rhs in
            lhs.expiresAt == rhs.expiresAt
                ? lhs.messageID.uuidString < rhs.messageID.uuidString
                : lhs.expiresAt < rhs.expiresAt
        }
        let senderBubbles = bubbles.filter { $0.senderID == senderID }
        if senderBubbles.count > Self.maximumVisiblePerSender {
            let removedIDs = Set(
                senderBubbles
                    .prefix(senderBubbles.count - Self.maximumVisiblePerSender)
                    .map(\.messageID)
            )
            bubbles.removeAll { removedIDs.contains($0.messageID) }
        }
    }

    mutating func remove(messageID: UUID) {
        bubbles.removeAll { $0.messageID == messageID }
    }

    mutating func removeAll() {
        bubbles.removeAll()
    }

    mutating func prune(at date: Date = .now) {
        bubbles.removeAll { $0.expiresAt <= date }
    }
}

struct PixelWorldMember: Equatable, Identifiable, Sendable {
    let id: UUID
    let nickname: String
    let characterID: String
    let presence: PresenceState
    let isTyping: Bool
    let isCurrentUser: Bool
    let equippedBubbleStyleID: String?
    let treeMovementPaused: Bool

    init(
        id: UUID,
        nickname: String,
        characterID: String,
        presence: PresenceState,
        isTyping: Bool,
        isCurrentUser: Bool,
        equippedBubbleStyleID: String? = nil,
        treeMovementPaused: Bool = false
    ) {
        self.id = id
        self.nickname = nickname
        self.characterID = characterID
        self.presence = presence
        self.isTyping = isTyping
        self.isCurrentUser = isCurrentUser
        self.equippedBubbleStyleID = equippedBubbleStyleID
        self.treeMovementPaused = treeMovementPaused
    }
}

struct CharacterPulseEvent: Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let userID: UUID
}

struct CharacterThrowEvent: Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let actorUserID: UUID
    let targetUserID: UUID
    let sourceCharacterID: String
    let throwableID: String?

    init(
        id: UUID,
        roomID: UUID,
        actorUserID: UUID,
        targetUserID: UUID,
        sourceCharacterID: String,
        throwableID: String? = nil
    ) {
        self.id = id
        self.roomID = roomID
        self.actorUserID = actorUserID
        self.targetUserID = targetUserID
        self.sourceCharacterID = sourceCharacterID
        self.throwableID = throwableID
    }
}

enum CharacterThrowTargetPolicy {
    static func canTarget(_ member: PixelWorldMember) -> Bool {
        !member.isCurrentUser
    }
}

struct CharacterThrowCooldown: Equatable, Sendable {
    static let duration: TimeInterval = 0.5
    private var lastAcceptedUptimeByActor: [UUID: TimeInterval] = [:]

    mutating func accept(actorUserID: UUID, uptime: TimeInterval) -> Bool {
        guard uptime.isFinite else { return false }
        if let last = lastAcceptedUptimeByActor[actorUserID], uptime - last < Self.duration {
            return false
        }
        lastAcceptedUptimeByActor[actorUserID] = uptime
        return true
    }
}

struct CharacterPulseCooldown: Equatable, Sendable {
    static let duration: TimeInterval = 1

    private struct Key: Hashable, Sendable {
        let roomID: UUID
        let userID: UUID
    }

    private var lastAcceptedUptime: [Key: TimeInterval] = [:]

    mutating func accept(roomID: UUID, userID: UUID, uptime: TimeInterval) -> Bool {
        guard uptime.isFinite else { return false }
        let key = Key(roomID: roomID, userID: userID)
        if let last = lastAcceptedUptime[key], uptime - last < Self.duration {
            return false
        }
        lastAcceptedUptime[key] = uptime
        return true
    }
}
