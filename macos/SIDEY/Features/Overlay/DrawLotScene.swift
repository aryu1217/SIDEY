import AppKit
import SpriteKit

/// A paper always belongs to the same participant. Only the paper belonging to
/// the server-selected winner is pulled from the shuffled pile.
@MainActor
final class DrawLotScene: SKScene {
    private static let totalDuration: CGFloat = 4
    private let members: [RoomMember]
    private let winnerIndex: Int?
    private let plans: [PaperPlan]
    private var papers: [PaperNode] = []
    private var progress: CGFloat = 0
    private var paperSize = CGSize(width: 100, height: 100)
    private var startingPositions: [CGPoint] = []

    init(event: DrawEvent, room: Room, size: CGSize) {
        members = event.gameMembers(in: room)
        winnerIndex = members.firstIndex { $0.userID == event.result.winnerUserID }
        var random = DrawGameRandom(eventID: event.id)
        plans = members.map { _ in
            PaperPlan(
                phase: CGFloat(random.next() % 10_000) / 10_000,
                orbit: CGFloat(18 + random.index(31)),
                turns: CGFloat(2 + random.index(3)),
                direction: random.index(2) == 0 ? -1 : 1,
                rotation: CGFloat(random.index(25) - 12) * .pi / 180,
                depth: CGFloat(random.index(1_000)) / 1_000
            )
        }
        super.init(size: size)
        scaleMode = .resizeFill
        backgroundColor = .clear
        isPaused = false
        rebuildLayout()
    }

    required init?(coder: NSCoder) { nil }

    override func didChangeSize(_ oldSize: CGSize) {
        super.didChangeSize(oldSize)
        rebuildLayout()
    }

    func setProgress(_ progress: CGFloat) {
        self.progress = min(1, max(0, progress))
        render()
    }

    private func rebuildLayout() {
        guard !members.isEmpty, size.width > 0, size.height > 0 else { return }
        let columns = members.count <= 4 ? 2
            : min(members.count, max(2, Int(ceil(sqrt(CGFloat(members.count) * size.width / max(size.height, 1))))))
        let rows = Int(ceil(CGFloat(members.count) / CGFloat(columns)))
        let width = min(150, max(52, (size.width - 28) / CGFloat(columns) - 10))
        let height = min(160, max(58, (size.height - 28) / CGFloat(rows) - 10))
        paperSize = CGSize(width: width, height: height)
        let spacingX = min(width + 12, (size.width - width - 16) / CGFloat(max(columns - 1, 1)))
        let spacingY = min(height + 12, (size.height - height - 16) / CGFloat(max(rows - 1, 1)))
        startingPositions = members.indices.map { index in
            let row = index / columns
            let itemsInRow = min(columns, members.count - row * columns)
            let column = index % columns
            return CGPoint(
                x: size.width / 2 + (CGFloat(column) - CGFloat(itemsInRow - 1) / 2) * spacingX,
                y: size.height / 2 + (CGFloat(rows - 1) / 2 - CGFloat(row)) * spacingY
            )
        }

        if papers.count != members.count {
            removeAllChildren()
            papers = members.map { member in
                let paper = PaperNode(member: member)
                addChild(paper)
                return paper
            }
        }
        papers.forEach { $0.setSize(paperSize) }
        render()
    }

    private func render() {
        guard papers.count == members.count, !papers.isEmpty else { return }
        let seconds = progress * Self.totalDuration
        let fold = smooth(min(1, seconds / 0.5))
        let shuffle = min(1, max(0, (seconds - 0.5) / 2))
        let pull = smooth(min(1, max(0, (seconds - 2.5) / 0.8)))
        let unfold = smooth(min(1, max(0, (seconds - 3.3) / 0.7)))
        let center = CGPoint(x: size.width / 2, y: size.height / 2)

        for index in papers.indices {
            let paper = papers[index]
            let plan = plans[index]
            let start = startingPositions[index]
            let end = shuffledPosition(index: index, phase: 1, center: center)
            let isWinner = index == winnerIndex
            let position: CGPoint
            let rotation: CGFloat
            let scale: CGFloat

            if seconds < 0.5 {
                position = start
                rotation = 0
                scale = 1
            } else if seconds < 2.5 {
                let gather = smooth(min(1, shuffle / 0.22))
                let orbit = shuffledPosition(index: index, phase: shuffle, center: center)
                position = interpolate(start, orbit, gather)
                rotation = plan.rotation * gather + plan.direction * .pi * 2 * plan.turns * shuffle
                scale = 1 - 0.18 * gather
            } else {
                let destination = isWinner
                    ? CGPoint(x: center.x, y: center.y - min(12, size.height * 0.04))
                    : CGPoint(x: center.x + plan.direction * min(68, size.width * 0.18),
                              y: center.y + (plan.phase - 0.5) * min(46, size.height * 0.14))
                position = interpolate(end, destination, pull)
                rotation = (plan.rotation + plan.direction * .pi * 2 * plan.turns) * (1 - pull)
                scale = (1 - 0.18 * (1 - pull)) * (isWinner ? 1 + 0.45 * pull : 1 - 0.45 * pull)
            }

            paper.position = position
            paper.zRotation = rotation
            paper.setScale(scale)
            paper.keepCharacterAtLogicalPixelScale(
                parentScale: scale,
                highlighted: isWinner && seconds >= 3.3
            )
            paper.zPosition = isWinner && seconds >= 2.5
                ? 2_000
                : (shuffle > 0 ? CGFloat(Int(plan.depth * 1_000 + shuffle * 17) % 1_000) : CGFloat(index))
            paper.alpha = isWinner ? 1 : 1 - 0.8 * pull
            let angle = isWinner && seconds >= 3.3 ? .pi * (1 - unfold) : .pi * fold
            paper.setFoldAngle(angle)
        }
    }

    private func shuffledPosition(index: Int, phase: CGFloat, center: CGPoint) -> CGPoint {
        let plan = plans[index]
        let angle = 2 * CGFloat.pi * (plan.turns * phase * plan.direction + plan.phase)
        return CGPoint(
            x: center.x + cos(angle) * plan.orbit + (plan.phase - 0.5) * paperSize.width * 0.5,
            y: center.y + sin(angle * 0.77) * plan.orbit * 0.65
        )
    }

    private func interpolate(_ start: CGPoint, _ end: CGPoint, _ fraction: CGFloat) -> CGPoint {
        CGPoint(x: start.x + (end.x - start.x) * fraction,
                y: start.y + (end.y - start.y) * fraction)
    }

    private func smooth(_ value: CGFloat) -> CGFloat {
        let x = min(1, max(0, value))
        return x * x * (3 - 2 * x)
    }
}

private struct PaperPlan {
    let phase: CGFloat
    let orbit: CGFloat
    let turns: CGFloat
    let direction: CGFloat
    let rotation: CGFloat
    let depth: CGFloat
}

@MainActor
private final class PaperNode: SKNode {
    private static let front = NSColor(srgbRed: 1, green: 0.97, blue: 0.86, alpha: 1)
    private static let back = NSColor(srgbRed: 0.79, green: 0.70, blue: 0.57, alpha: 1)
    private static let foldedFront = NSColor(srgbRed: 0.91, green: 0.84, blue: 0.70, alpha: 1)
    private let left = SKShapeNode()
    private let flap = SKShapeNode()
    private let crease = SKShapeNode()
    private let character: DrawCharacterNode
    private var paperSize = CGSize(width: 100, height: 100)
    private var characterScale: CGFloat = 0.5

    init(member: RoomMember) {
        character = DrawCharacterNode(member: member)
        super.init()
        left.fillColor = Self.front
        left.strokeColor = DrawGamePalette.outline
        left.lineWidth = 2
        left.zPosition = 0
        addChild(left)
        flap.strokeColor = DrawGamePalette.outline
        flap.lineWidth = 2
        flap.zPosition = 1
        addChild(flap)
        character.zPosition = 2
        addChild(character)
        crease.strokeColor = DrawGamePalette.outline.withAlphaComponent(0.45)
        crease.lineWidth = 1
        crease.zPosition = 4
        addChild(crease)
    }

    required init?(coder: NSCoder) { nil }

    func setSize(_ size: CGSize) {
        paperSize = size
        let halfWidth = size.width / 2
        left.path = CGPath(rect: CGRect(x: -halfWidth, y: -size.height / 2,
                                        width: halfWidth, height: size.height), transform: nil)
        let creasePath = CGMutablePath()
        creasePath.move(to: CGPoint(x: 0, y: -size.height / 2))
        creasePath.addLine(to: CGPoint(x: 0, y: size.height / 2))
        crease.path = creasePath
        characterScale = size.width >= 88 && size.height >= 88 ? 1 : 0.5
        character.setScale(characterScale)
        character.position = CGPoint(x: 0, y: -size.height * 0.25)
        setFoldAngle(0)
    }

    func setFoldAngle(_ angle: CGFloat) {
        let cosine = cos(angle)
        let edgeX = paperSize.width * 0.5 * cosine
        let perspective = abs(sin(angle)) * paperSize.height * 0.09
        let edgeInset = abs(sin(angle)) * paperSize.height * 0.08
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 0, y: -paperSize.height / 2))
        path.addLine(to: CGPoint(x: edgeX, y: -paperSize.height / 2 + edgeInset + perspective))
        path.addLine(to: CGPoint(x: edgeX, y: paperSize.height / 2 - edgeInset + perspective))
        path.addLine(to: CGPoint(x: 0, y: paperSize.height / 2))
        path.closeSubpath()
        flap.path = path
        flap.fillColor = cosine < 0 ? Self.back : (angle == 0 ? Self.front : Self.foldedFront)
        flap.zPosition = cosine < 0 ? 3 : 1
        // The character is printed inside the paper. The closed back hides it.
        character.alpha = max(0, min(1, cosine * 2))
    }

    func keepCharacterAtLogicalPixelScale(parentScale: CGFloat, highlighted: Bool) {
        let displayScale = highlighted ? max(characterScale, 1) : characterScale
        character.setScale(displayScale / max(parentScale, 0.01))
    }
}
