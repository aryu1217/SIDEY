import AppKit
import SpriteKit

/// The wheel and its contents share one rotation. The winner is supplied by the event;
/// the event ID determines only the reproducible number of full turns.
@MainActor
final class DrawRouletteScene: SKScene {
    private let entries: [DrawGameEntry]
    private let winnerIndex: Int?
    private let totalRotation: CGFloat
    private let wheel = SKNode()
    private let pointer = SKShapeNode()
    private let winnerDisplay = SKNode()
    private var winnerOutline: SKShapeNode?
    private var progress: CGFloat = 0
    private var lastCrossing = 0

    init(event: DrawEvent, room: Room, size: CGSize) {
        let gameEntries = event.gameEntries(in: room)
        let selectedIndex = event.isFreeEntryMode
            ? event.result.winnerEntryIndex.flatMap { gameEntries.indices.contains($0) ? $0 : nil }
            : gameEntries.firstIndex { $0.member?.userID == event.result.winnerUserID }
        entries = gameEntries
        winnerIndex = selectedIndex
        var random = DrawGameRandom(eventID: event.id)
        let turns = CGFloat(4 + random.index(3))
        if let selectedIndex, !gameEntries.isEmpty {
            let step = 2 * CGFloat.pi / CGFloat(gameEntries.count)
            let finalPhase = (2 * CGFloat.pi - CGFloat(selectedIndex) * step)
                .truncatingRemainder(dividingBy: 2 * CGFloat.pi)
            totalRotation = turns * 2 * .pi + finalPhase
        } else {
            totalRotation = turns * 2 * .pi
        }
        super.init(size: size)
        backgroundColor = .clear
        scaleMode = .resizeFill
        addChild(wheel)
        addChild(pointer)
        addChild(winnerDisplay)
        layoutWheel()
    }

    required init?(coder: NSCoder) { nil }

    override func didChangeSize(_ oldSize: CGSize) {
        super.didChangeSize(oldSize)
        layoutWheel()
    }

    func setProgress(_ value: CGFloat) {
        let next = min(1, max(0, value))
        progress = next
        // Smoothstep starts and ends with zero angular velocity; the final angle is
        // computed directly from the server winner, so there is no finishing snap.
        let eased = next * next * (3 - 2 * next)
        let angle = totalRotation * eased
        wheel.zRotation = angle
        winnerOutline?.isHidden = next < 1
        winnerDisplay.isHidden = next < 1
        if next == 0 || next == 1 {
            pointer.removeAction(forKey: "tick")
            pointer.zRotation = 0
        }

        if entries.count > 1 {
            let step = 2 * CGFloat.pi / CGFloat(entries.count)
            let crossing = Int(floor((angle + step / 2) / step))
            if crossing > lastCrossing && next < 1 {
                pointer.removeAction(forKey: "tick")
                pointer.run(.sequence([
                    .rotate(toAngle: -0.17, duration: 0.055, shortestUnitArc: true),
                    .rotate(toAngle: 0, duration: 0.11, shortestUnitArc: true)
                ]), withKey: "tick")
            }
            lastCrossing = crossing
        }
    }

    private func layoutWheel() {
        wheel.removeAllChildren()
        winnerDisplay.removeAllChildren()
        winnerOutline = nil
        guard entries.count >= 2 else { return }

        let radius = max(32, min(size.width * 0.37, size.height * 0.36))
        let center = CGPoint(x: size.width / 2, y: size.height / 2 - 7)
        let step = 2 * CGFloat.pi / CGFloat(entries.count)
        // DrawCharacterNode is 2x the logical sprite. These scales keep it at
        // either 24 or 48 points, never a fractional pixel enlargement.
        let contentScale: CGFloat = entries.count <= 4 && radius >= 120 ? 1 : 0.5
        wheel.position = center

        for (index, entry) in entries.enumerated() {
            let middle = CGFloat.pi / 2 + CGFloat(index) * step
            let path = sectorPath(radius: radius, start: middle - step / 2, end: middle + step / 2)
            let sector = SKShapeNode(path: path)
            sector.fillColor = DrawGamePalette.colors[index % DrawGamePalette.colors.count]
            sector.strokeColor = DrawGamePalette.outline
            sector.lineWidth = 2
            sector.zPosition = 0
            wheel.addChild(sector)

            let contentRadius = radius * (entries.count >= 8 ? 0.72 : 0.58)
            let content: SKNode
            if let member = entry.member {
                let character = DrawCharacterNode(member: member)
                character.setScale(contentScale)
                content = character
            } else {
                let text = DrawGameTextNode(text: entry.label)
                text.fit(maxWidth: min(130, max(24, 2 * contentRadius * sin(step / 2) - 6)))
                content = text
            }
            content.position = CGPoint(x: (cos(middle) * contentRadius).rounded(),
                                       y: (sin(middle) * contentRadius).rounded())
            content.zPosition = 2
            wheel.addChild(content)

            if index == winnerIndex {
                let outline = SKShapeNode(path: path)
                outline.fillColor = .clear
                outline.strokeColor = DrawGamePalette.accent
                outline.lineWidth = 5
                outline.zPosition = 3
                outline.isHidden = progress < 1
                wheel.addChild(outline)
                winnerOutline = outline
            }
        }

        let rim = SKShapeNode(circleOfRadius: radius)
        rim.fillColor = .clear
        rim.strokeColor = DrawGamePalette.outline
        rim.lineWidth = 5
        rim.zPosition = 4
        wheel.addChild(rim)

        let hub = SKShapeNode(circleOfRadius: max(12, radius * 0.12))
        hub.fillColor = DrawGamePalette.cream
        hub.strokeColor = DrawGamePalette.outline
        hub.lineWidth = 3
        hub.zPosition = 5
        wheel.addChild(hub)

        let pointerPath = CGMutablePath()
        pointerPath.move(to: CGPoint(x: -11, y: 0))
        pointerPath.addLine(to: CGPoint(x: 11, y: 0))
        pointerPath.addLine(to: CGPoint(x: 0, y: -21))
        pointerPath.closeSubpath()
        pointer.path = pointerPath
        pointer.position = CGPoint(x: center.x, y: center.y + radius + 23)
        pointer.fillColor = DrawGamePalette.cream
        pointer.strokeColor = DrawGamePalette.outline
        pointer.lineWidth = 3
        pointer.zPosition = 10

        if let winnerIndex, entries.indices.contains(winnerIndex) {
            let entry = entries[winnerIndex]
            let content: SKNode
            if let member = entry.member {
                let character = DrawCharacterNode(member: member)
                character.setScale(radius >= 120 ? 1 : 0.5)
                content = character
            } else {
                let text = DrawGameTextNode(text: entry.label)
                text.fit(maxWidth: min(220, size.width - 32))
                content = text
            }
            content.position = CGPoint(x: center.x,
                                       y: center.y - radius - (entry.member == nil ? 24 : 18))
            winnerDisplay.addChild(content)
        }
        winnerDisplay.zPosition = 20
        winnerDisplay.isHidden = progress < 1
        wheel.zRotation = totalRotation * progress * progress * (3 - 2 * progress)
    }

    private func sectorPath(radius: CGFloat, start: CGFloat, end: CGFloat) -> CGPath {
        let path = CGMutablePath()
        path.move(to: .zero)
        path.addLine(to: CGPoint(x: cos(start) * radius, y: sin(start) * radius))
        path.addArc(center: .zero, radius: radius, startAngle: start, endAngle: end, clockwise: false)
        path.closeSubpath()
        return path
    }
}
