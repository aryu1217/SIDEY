import AppKit
import SpriteKit

enum DrawGamePalette {
    static let outline = NSColor(srgbRed: 0.10, green: 0.10, blue: 0.15, alpha: 1)
    static let cream = NSColor(srgbRed: 0.99, green: 0.94, blue: 0.80, alpha: 1)
    static let accent = NSColor(srgbRed: 0.45, green: 0.32, blue: 0.91, alpha: 1)
    static let colors: [NSColor] = [
        NSColor(srgbRed: 0.98, green: 0.69, blue: 0.53, alpha: 1),
        NSColor(srgbRed: 0.52, green: 0.79, blue: 0.72, alpha: 1),
        NSColor(srgbRed: 0.99, green: 0.81, blue: 0.46, alpha: 1),
        NSColor(srgbRed: 0.70, green: 0.67, blue: 0.93, alpha: 1),
        NSColor(srgbRed: 0.63, green: 0.78, blue: 0.97, alpha: 1),
        NSColor(srgbRed: 0.95, green: 0.66, blue: 0.75, alpha: 1)
    ]
}

/// Stable across processes and replay. Swift's Hasher is intentionally not used.
struct DrawGameRandom {
    private(set) var state: UInt64

    init(eventID: UUID) {
        let bytes = withUnsafeBytes(of: eventID.uuid) { Array($0) }
        state = bytes.reduce(0xcbf29ce484222325) { ($0 ^ UInt64($1)) &* 0x100000001b3 }
        if state == 0 { state = 1 }
    }

    mutating func next() -> UInt64 {
        state ^= state >> 12
        state ^= state << 25
        state ^= state >> 27
        return state &* 0x2545f4914f6cdd1d
    }

    mutating func index(_ count: Int) -> Int { Int(next() % UInt64(count)) }
}

/// Uses the same cached sprite frames and nameplate dimensions as the edge characters.
@MainActor
final class DrawCharacterNode: SKNode {
    let sprite = SKSpriteNode()
    private let nickname = SKLabelNode(fontNamed: "AppleSDGothicNeo-SemiBold")
    private let nameplate = SKShapeNode()
    private let textures: PixelCharacterTextures
    private var walking = false

    init(member: RoomMember) {
        textures = PixelCharacterTextureStore.shared.textures(for: member.characterID)
        super.init()
        sprite.size = CGSize(width: 48, height: 48)
        sprite.texture = textures.idle[0]
        sprite.texture?.filteringMode = .nearest
        sprite.position.y = 24
        addChild(sprite)

        nickname.text = member.nickname
        nickname.fontSize = 11
        nickname.fontColor = .white
        nickname.verticalAlignmentMode = .center
        nickname.horizontalAlignmentMode = .center
        // Draw characters stand on their node origin. Keep the nameplate below
        // the 48-point sprite instead of using the edge-overlay label position.
        nickname.position.y = -12
        nickname.zPosition = 2
        addChild(nickname)
        let frame = PixelNameplateLayout.backgroundFrame(nicknameFrame: nickname.frame)
        nameplate.path = CGPath(roundedRect: frame, cornerWidth: 6, cornerHeight: 6, transform: nil)
        nameplate.fillColor = PixelNameplateLayout.backgroundColor
        nameplate.strokeColor = .clear
        nameplate.zPosition = 1
        addChild(nameplate)
    }

    required init?(coder: NSCoder) { nil }

    func setWalking(_ active: Bool, facing: CGFloat = 1) {
        sprite.xScale = facing >= 0 ? 1 : -1
        guard walking != active else { return }
        walking = active
        sprite.removeAction(forKey: "draw-walk")
        if active {
            sprite.run(.repeatForever(.animate(with: textures.walk, timePerFrame: 0.16)), withKey: "draw-walk")
        } else {
            sprite.texture = textures.idle[0]
        }
    }

    func react() {
        sprite.removeAction(forKey: "draw-walk")
        walking = false
        sprite.run(.sequence([
            .animate(with: textures.idle, timePerFrame: 0.12),
            .animate(with: textures.idle.reversed(), timePerFrame: 0.12)
        ]))
    }
}

/// Text-only game entry. It shares the nameplate contrast with participant
/// characters while fitting within a paper, ladder lane, or roulette sector.
@MainActor
final class DrawGameTextNode: SKNode {
    private let label = SKLabelNode(fontNamed: "AppleSDGothicNeo-SemiBold")
    private let background = SKShapeNode()

    init(text: String) {
        super.init()
        label.text = text
        label.fontSize = 14
        label.fontColor = .white
        label.horizontalAlignmentMode = .center
        label.verticalAlignmentMode = .center
        label.zPosition = 1
        background.fillColor = PixelNameplateLayout.backgroundColor
        background.strokeColor = .clear
        addChild(background)
        addChild(label)
        fit(maxWidth: 160)
    }

    required init?(coder: NSCoder) { nil }

    func fit(maxWidth: CGFloat) {
        let available = max(20, maxWidth)
        label.xScale = 1
        label.xScale = min(1, (available - 12) / max(label.frame.width, 1))
        let width = min(available, label.frame.width + 12)
        background.path = CGPath(roundedRect: CGRect(x: -width / 2, y: -12,
                                                      width: width, height: 24),
                                 cornerWidth: 7, cornerHeight: 7, transform: nil)
    }
}

struct DrawGameEntry {
    let label: String
    let member: RoomMember?
}

extension DrawEvent {
    func gameEntries(in room: Room) -> [DrawGameEntry] {
        if let entryLabels {
            return entryLabels.map { DrawGameEntry(label: $0, member: nil) }
        }
        return gameMembers(in: room).map { DrawGameEntry(label: $0.nickname, member: $0) }
    }

    func gameMembers(in room: Room) -> [RoomMember] {
        participantIDs.map { id in
            room.members.first(where: { $0.userID == id }) ?? RoomMember(
                userID: id,
                nickname: String(id.uuidString.prefix(8)),
                characterID: PixelCharacterCatalog.pixelHamsterID,
                presence: .offline
            )
        }
    }
}
