import AppKit
import SpriteKit

/// A paper always belongs to the same entry. Only the paper belonging to the
/// server-selected winner is pulled from the shuffled pile.
@MainActor
final class DrawLotScene: SKScene {
    private static let totalDuration: CGFloat = 4
    private let entries: [DrawGameEntry]
    private let winnerIndex: Int?
    private let plans: [PaperPlan]
    private var papers: [PaperNode] = []
    private var progress: CGFloat = 0
    private var paperSize = CGSize(width: 100, height: 100)
    private var startingPositions: [CGPoint] = []

    init(event: DrawEvent, room: Room, size: CGSize) {
        let entries = event.gameEntries(in: room)
        self.entries = entries
        winnerIndex = event.isFreeEntryMode
            ? event.result.winnerEntryIndex.flatMap { entries.indices.contains($0) ? $0 : nil }
            : entries.firstIndex { $0.member?.userID == event.result.winnerUserID }
        var random = DrawGameRandom(eventID: event.id)
        plans = entries.map { _ in
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

    static func controlsAnchorY(entryCount: Int, size: CGSize, completed: Bool) -> CGFloat {
        let grid = DrawLotGridLayout(entryCount: entryCount, size: size)
        if completed {
            return size.height / 2 - min(12, size.height * 0.04) - grid.paperSize.height * 1.45 / 2
        }
        return (grid.positions.map(\.y).min() ?? size.height / 2) - grid.paperSize.height / 2
    }

    private func rebuildLayout() {
        guard !entries.isEmpty, size.width > 0, size.height > 0 else { return }
        let grid = DrawLotGridLayout(entryCount: entries.count, size: size)
        paperSize = grid.paperSize
        startingPositions = grid.positions

        if papers.count != entries.count {
            removeAllChildren()
            papers = entries.map { entry in
                let paper = PaperNode(entry: entry)
                addChild(paper)
                return paper
            }
        }
        papers.forEach { $0.setSize(paperSize) }
        render()
    }

    private func render() {
        guard papers.count == entries.count, !papers.isEmpty else { return }
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

private struct DrawLotGridLayout {
    let paperSize: CGSize
    let positions: [CGPoint]

    init(entryCount: Int, size: CGSize) {
        let gap: CGFloat = 18
        let inset: CGFloat = 24
        // Choose a balanced grid of cards rather than a strip of tiny labels.
        let columns = (1...max(1, min(4, entryCount))).max { lhs, rhs in
            func cardScale(columns: Int) -> CGFloat {
                let rows = max(1, Int(ceil(CGFloat(entryCount) / CGFloat(columns))))
                let width = (size.width - inset * 2 - CGFloat(columns - 1) * gap) / CGFloat(columns)
                let height = (size.height - inset * 2 - CGFloat(rows - 1) * gap) / CGFloat(rows)
                return min(width / 172, height / 152)
            }
            return cardScale(columns: lhs) < cardScale(columns: rhs)
        } ?? 1
        let rows = max(1, Int(ceil(CGFloat(entryCount) / CGFloat(columns))))
        let width = min(172, max(48, (size.width - inset * 2 - CGFloat(columns - 1) * gap) / CGFloat(columns)))
        let height = min(152, max(52, (size.height - inset * 2 - CGFloat(rows - 1) * gap) / CGFloat(rows)))
        self.paperSize = CGSize(width: width, height: height)
        let spacingX = width + gap
        let spacingY = height + gap
        positions = (0..<entryCount).map { index in
            let row = index / columns
            let itemsInRow = min(columns, entryCount - row * columns)
            let column = index % columns
            return CGPoint(
                x: size.width / 2 + (CGFloat(column) - CGFloat(itemsInRow - 1) / 2) * spacingX,
                y: size.height / 2 + (CGFloat(rows - 1) / 2 - CGFloat(row)) * spacingY
            )
        }
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
    private static let front = NSColor(srgbRed: 1, green: 0.985, blue: 0.94, alpha: 1)
    private static let back = NSColor(srgbRed: 0.83, green: 0.79, blue: 0.96, alpha: 1)
    private static let foldedFront = NSColor(srgbRed: 0.94, green: 0.91, blue: 0.84, alpha: 1)
    private static let edge = NSColor(srgbRed: 0.50, green: 0.43, blue: 0.62, alpha: 1)
    private let left = SKShapeNode()
    private let flap = SKShapeNode()
    private let leftEdge = SKShapeNode()
    private let flapEdge = SKShapeNode()
    private let crease = SKShapeNode()
    private let corner = SKShapeNode()
    private let seal = SKShapeNode()
    private let content = SKNode()
    private let label: DrawGameTextNode
    private let character: DrawCharacterNode?
    private var paperSize = CGSize(width: 100, height: 100)
    private var characterScale: CGFloat = 0.5

    init(entry: DrawGameEntry) {
        character = entry.member.map { DrawCharacterNode(member: $0, showsNameplate: false) }
        label = DrawGameTextNode(text: entry.label, fontSize: 17,
                                 color: DrawGamePalette.outline, showsBackground: false)
        super.init()
        if let character { content.addChild(character) }
        content.addChild(label)
        left.fillColor = Self.front
        left.strokeColor = .clear
        left.zPosition = 0
        addChild(left)
        flap.strokeColor = .clear
        flap.zPosition = 1
        addChild(flap)
        for edge in [leftEdge, flapEdge] {
            edge.strokeColor = Self.edge
            edge.lineWidth = 1.5
            edge.lineCap = .round
            edge.fillColor = .clear
            addChild(edge)
        }
        leftEdge.zPosition = 0.5
        flapEdge.zPosition = 1.5
        content.zPosition = 2
        addChild(content)
        crease.strokeColor = Self.edge.withAlphaComponent(0.2)
        crease.lineWidth = 1
        crease.zPosition = 1.6
        addChild(crease)
        corner.fillColor = Self.foldedFront
        corner.strokeColor = Self.edge.withAlphaComponent(0.4)
        corner.lineWidth = 1
        corner.zPosition = 0
        content.addChild(corner)
        seal.fillColor = Self.front
        seal.strokeColor = .clear
        seal.zPosition = 4
        addChild(seal)
    }

    required init?(coder: NSCoder) { nil }

    func setSize(_ size: CGSize) {
        paperSize = size
        let halfWidth = size.width / 2
        let halfHeight = size.height / 2
        let radius = min(10, size.width * 0.08)
        let leftPath = CGMutablePath()
        leftPath.move(to: CGPoint(x: 0, y: -halfHeight))
        leftPath.addLine(to: CGPoint(x: -halfWidth + radius, y: -halfHeight))
        leftPath.addQuadCurve(to: CGPoint(x: -halfWidth, y: -halfHeight + radius),
                              control: CGPoint(x: -halfWidth, y: -halfHeight))
        leftPath.addLine(to: CGPoint(x: -halfWidth, y: halfHeight - radius))
        leftPath.addQuadCurve(to: CGPoint(x: -halfWidth + radius, y: halfHeight),
                              control: CGPoint(x: -halfWidth, y: halfHeight))
        leftPath.addLine(to: CGPoint(x: 0, y: halfHeight))
        leftEdge.path = leftPath.copy()
        leftPath.closeSubpath()
        left.path = leftPath
        let creasePath = CGMutablePath()
        for y in stride(from: -halfHeight + 8, through: halfHeight - 8, by: 7) {
            creasePath.move(to: CGPoint(x: 0, y: y))
            creasePath.addLine(to: CGPoint(x: 0, y: min(y + 3, halfHeight - 8)))
        }
        crease.path = creasePath
        let dogEar = min(14, size.width * 0.1)
        let cornerPath = CGMutablePath()
        cornerPath.move(to: CGPoint(x: halfWidth - dogEar - 3, y: halfHeight - 3))
        cornerPath.addLine(to: CGPoint(x: halfWidth - 3, y: halfHeight - dogEar - 3))
        cornerPath.addLine(to: CGPoint(x: halfWidth - dogEar - 3, y: halfHeight - dogEar - 3))
        cornerPath.closeSubpath()
        corner.path = cornerPath
        let sealRadius = min(10, size.width * 0.07)
        let sealPath = CGMutablePath()
        for index in 0..<8 {
            let angle = CGFloat(index) * .pi / 4
            let radius = index.isMultiple(of: 2) ? sealRadius : sealRadius * 0.42
            let point = CGPoint(x: cos(angle) * radius, y: sin(angle) * radius)
            if index == 0 { sealPath.move(to: point) } else { sealPath.addLine(to: point) }
        }
        sealPath.closeSubpath()
        seal.path = sealPath
        if let character {
            characterScale = size.width >= 100 && size.height >= 120 ? 1 : 0.5
            character.setScale(characterScale)
            let spriteHeight = 48 * characterScale
            let textHeight = max(22, size.height - spriteHeight - 32)
            label.fit(maxWidth: size.width - 20, maxHeight: textHeight)
            character.position = CGPoint(x: 0, y: size.height / 2 - 14 - spriteHeight)
            label.position = CGPoint(x: 0, y: -size.height / 2 + 14 + textHeight / 2)
        } else {
            label.fit(maxWidth: size.width - 28, maxHeight: size.height - 32)
            label.position = .zero
        }
        setFoldAngle(0)
    }

    func setFoldAngle(_ angle: CGFloat) {
        let cosine = cos(angle)
        let edgeX = paperSize.width * 0.5 * cosine
        let perspective = abs(sin(angle)) * paperSize.height * 0.09
        let edgeInset = abs(sin(angle)) * paperSize.height * 0.08
        let halfHeight = paperSize.height / 2
        let cornerRadius = min(10, paperSize.width * 0.08)
        let horizontalRadius = cornerRadius * cosine
        let lowerEdge = -halfHeight + edgeInset + perspective
        let upperEdge = halfHeight - edgeInset + perspective
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 0, y: -halfHeight))
        path.addLine(to: CGPoint(x: edgeX - horizontalRadius, y: lowerEdge))
        path.addQuadCurve(to: CGPoint(x: edgeX, y: lowerEdge + cornerRadius),
                          control: CGPoint(x: edgeX, y: lowerEdge))
        path.addLine(to: CGPoint(x: edgeX, y: upperEdge - cornerRadius))
        path.addQuadCurve(to: CGPoint(x: edgeX - horizontalRadius, y: upperEdge),
                          control: CGPoint(x: edgeX, y: upperEdge))
        path.addLine(to: CGPoint(x: 0, y: halfHeight))
        flapEdge.path = path.copy()
        path.closeSubpath()
        flap.path = path
        if cosine < 0 { flapEdge.path = path }
        flap.fillColor = cosine < 0 ? Self.back : (angle == 0 ? Self.front : Self.foldedFront)
        flap.zPosition = cosine < 0 ? 3 : 1
        flapEdge.zPosition = cosine < 0 ? 3.5 : 1.5
        seal.position = CGPoint(x: edgeX / 2, y: perspective / 2)
        seal.xScale = max(0.01, abs(cosine))
        seal.alpha = max(0, -cosine)
        crease.alpha = max(0, cosine)
        // The character is printed inside the paper. The closed back hides it.
        content.alpha = max(0, min(1, cosine * 2))
    }

    func keepCharacterAtLogicalPixelScale(parentScale: CGFloat, highlighted: Bool) {
        guard let character else { return }
        let displayScale = highlighted ? max(characterScale, 1) : characterScale
        character.setScale(displayScale / max(parentScale, 0.01))
    }
}
