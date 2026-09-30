import Foundation

enum DrawEntryLimits {
    static let maximumLabelLength = 15

    static func isValidLabel(_ raw: String) -> Bool {
        let label = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return (1...maximumLabelLength).contains(label.unicodeScalars.count)
            && !label.contains(where: { "\r\n\t".contains($0) })
    }
}

enum DrawKind: String, Codable, CaseIterable, Sendable {
    case lot
    case ladder
    case roulette

    var title: String {
        switch self {
        case .lot: L10n.text("draw.kind.lot")
        case .ladder: L10n.text("draw.kind.ladder")
        case .roulette: L10n.text("draw.kind.roulette")
        }
    }

    var symbol: String {
        switch self {
        case .lot: "ticket"
        case .ladder: "figure.stairs"
        case .roulette: "circle.hexagongrid"
        }
    }
}

struct DrawAssignment: Codable, Equatable, Sendable {
    let userID: UUID?
    let entryIndex: Int?
    let label: String

    init(userID: UUID? = nil, label: String, entryIndex: Int? = nil) {
        self.userID = userID
        self.entryIndex = entryIndex
        self.label = label
    }

    enum CodingKeys: String, CodingKey {
        case userID = "user_id"
        case entryIndex = "entry_index"
        case label
    }
}

struct DrawResult: Codable, Equatable, Sendable {
    let winnerUserID: UUID?
    let winnerEntryIndex: Int?
    let assignments: [DrawAssignment]?

    init(winnerUserID: UUID?, assignments: [DrawAssignment]?, winnerEntryIndex: Int? = nil) {
        self.winnerUserID = winnerUserID
        self.winnerEntryIndex = winnerEntryIndex
        self.assignments = assignments
    }

    enum CodingKeys: String, CodingKey {
        case winnerUserID = "winner_user_id"
        case winnerEntryIndex = "winner_entry_index"
        case assignments
    }
}

struct DrawEvent: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let creatorID: UUID
    let kind: DrawKind
    let title: String?
    let participantIDs: [UUID]
    let entryLabels: [String]?
    let ladderLabels: [String]?
    let result: DrawResult
    let createdAt: String
    let iconExpiresAt: String
    let closedAt: String?
    let openedAt: String?
    let iconVisible: Bool

    init(id: UUID, roomID: UUID, creatorID: UUID, kind: DrawKind,
         participantIDs: [UUID], ladderLabels: [String]?, result: DrawResult,
         createdAt: String, iconExpiresAt: String, closedAt: String?,
         openedAt: String?, iconVisible: Bool, entryLabels: [String]? = nil, title: String? = nil) {
        self.id = id
        self.roomID = roomID
        self.creatorID = creatorID
        self.kind = kind
        self.title = title
        self.participantIDs = participantIDs
        self.entryLabels = entryLabels
        self.ladderLabels = ladderLabels
        self.result = result
        self.createdAt = createdAt
        self.iconExpiresAt = iconExpiresAt
        self.closedAt = closedAt
        self.openedAt = openedAt
        self.iconVisible = iconVisible
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, result, title
        case roomID = "room_id"
        case creatorID = "creator_id"
        case participantIDs = "participant_ids"
        case entryLabels = "entry_labels"
        case ladderLabels = "ladder_labels"
        case createdAt = "created_at"
        case iconExpiresAt = "icon_expires_at"
        case closedAt = "closed_at"
        case openedAt = "opened_at"
        case iconVisible = "icon_visible"
    }

    var displayTitle: String {
        let text = title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? kind.title : text
    }

    var createdDate: Date? { try? PostgresTimestampDecoder.decode(createdAt) }
    var iconExpiryDate: Date? { try? PostgresTimestampDecoder.decode(iconExpiresAt) }
    var isFreeEntryMode: Bool { entryLabels != nil }
    var gameEntryCount: Int { entryLabels?.count ?? participantIDs.count }

    func resultLines(in room: Room?) -> [String] {
        func name(_ id: UUID) -> String {
            room?.members.first(where: { $0.userID == id })?.nickname ?? id.uuidString
        }
        if let winner = result.winnerUserID {
            return [name(winner)]
        }
        if let winnerIndex = result.winnerEntryIndex,
           let labels = entryLabels, labels.indices.contains(winnerIndex) {
            return [labels[winnerIndex]]
        }
        return (result.assignments ?? []).map { assignment in
            if let id = assignment.userID {
                return "\(name(id)) · \(assignment.label)"
            }
            if let index = assignment.entryIndex,
               let labels = entryLabels, labels.indices.contains(index) {
                return "\(labels[index]) · \(assignment.label)"
            }
            return assignment.label
        }
    }
}
