import Foundation

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
    let userID: UUID
    let label: String

    enum CodingKeys: String, CodingKey {
        case userID = "user_id"
        case label
    }
}

struct DrawResult: Codable, Equatable, Sendable {
    let winnerUserID: UUID?
    let assignments: [DrawAssignment]?

    enum CodingKeys: String, CodingKey {
        case winnerUserID = "winner_user_id"
        case assignments
    }
}

struct DrawEvent: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let creatorID: UUID
    let kind: DrawKind
    let participantIDs: [UUID]
    let ladderLabels: [String]?
    let result: DrawResult
    let createdAt: String
    let iconExpiresAt: String
    let closedAt: String?
    let openedAt: String?
    let iconVisible: Bool

    enum CodingKeys: String, CodingKey {
        case id, kind, result
        case roomID = "room_id"
        case creatorID = "creator_id"
        case participantIDs = "participant_ids"
        case ladderLabels = "ladder_labels"
        case createdAt = "created_at"
        case iconExpiresAt = "icon_expires_at"
        case closedAt = "closed_at"
        case openedAt = "opened_at"
        case iconVisible = "icon_visible"
    }

    var createdDate: Date? { try? PostgresTimestampDecoder.decode(createdAt) }
    var iconExpiryDate: Date? { try? PostgresTimestampDecoder.decode(iconExpiresAt) }

    func resultLines(in room: Room?) -> [String] {
        func name(_ id: UUID) -> String {
            room?.members.first(where: { $0.userID == id })?.nickname ?? id.uuidString
        }
        if let winner = result.winnerUserID {
            return [name(winner)]
        }
        return (result.assignments ?? []).map { "\(name($0.userID)) · \($0.label)" }
    }
}
