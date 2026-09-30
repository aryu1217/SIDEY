import AppKit
import SpriteKit

/// A rung exchanges the occupants of two neighboring lanes. Rows can contain
/// several rungs, provided they do not share a lane.
struct DrawLadderRung: Equatable {
    let row: Int
    let leftLane: Int
}

struct DrawLadderLayout {
    let destinationForParticipant: [Int]
    let rungs: [DrawLadderRung]
    let rowCount: Int

    init(event: DrawEvent, participantIDs: [UUID]) {
        let count = participantIDs.count
        let labels = event.ladderLabels ?? []
        let assignments = Dictionary(event.result.assignments?.map { ($0.userID, $0.label) } ?? [],
                                     uniquingKeysWith: { first, _ in first })
        var claimed = Set<Int>()
        var destinations = [Int]()
        for id in participantIDs {
            let matching = assignments[id].flatMap { assigned in
                labels.indices.first { !claimed.contains($0) && labels[$0] == assigned }
            }
            let slot = matching ?? (0..<count).first { !claimed.contains($0) } ?? destinations.count
            destinations.append(slot)
            claimed.insert(slot)
        }
        self.destinationForParticipant = destinations

        guard count > 1, destinations.count == count else {
            rungs = []
            rowCount = 0
            return
        }

        // A neutral pair supplies a real route even when the server permutation
        // is the identity. The event ID chooses the lane reproducibly.
        var random = DrawGameRandom(eventID: event.id)
        let neutralLane = random.index(count - 1)
        var generated = [DrawLadderRung(row: 0, leftLane: neutralLane),
                         DrawLadderRung(row: 1, leftLane: neutralLane)]
        var order = Array(0..<count)
        var row = 2
        // Odd-even transposition sorts by assigned bottom slot. Each row's
        // exchanges are disjoint, so every crossing has a visible rung.
        for phase in 0..<count {
            let firstLane = phase % 2
            var lane = firstLane
            while lane + 1 < count {
                if destinations[order[lane]] > destinations[order[lane + 1]] {
                    order.swapAt(lane, lane + 1)
                    generated.append(DrawLadderRung(row: row, leftLane: lane))
                }
                lane += 2
            }
            row += 1
        }
        rungs = generated
        rowCount = row
    }
}

@MainActor
final class DrawLadderScene: SKScene {
    private struct Segment {
        let from: CGPoint
        let to: CGPoint
        var length: CGFloat { hypot(to.x - from.x, to.y - from.y) }
    }

    private let event: DrawEvent
    private let members: [RoomMember]
    private let layout: DrawLadderLayout
    private var characters: [DrawCharacterNode] = []
    private var routes: [[Segment]] = []
    private let highlight = SKShapeNode()
    private var progress: CGFloat = 0

    init(event: DrawEvent, room: Room, size: CGSize) {
        self.event = event
        members = event.gameMembers(in: room)
        layout = DrawLadderLayout(event: event, participantIDs: members.map(\.userID))
        super.init(size: size)
        backgroundColor = .clear
        scaleMode = .resizeFill
        rebuild()
    }

    required init?(coder: NSCoder) { nil }

    override func didChangeSize(_ oldSize: CGSize) {
        super.didChangeSize(oldSize)
        if oldSize != size { rebuild() }
    }

    func setProgress(_ value: CGFloat) {
        let previous = progress
        progress = min(max(value, 0), 1)
        let count = characters.count
        guard count > 0 else { return }
        let total = CGFloat(count) * 2.2 + CGFloat(max(0, count - 1)) * 0.2
        let elapsed = progress * total
        var activeRoute: [Segment] = []
        var activeDistance: CGFloat = 0

        for index in characters.indices {
            let start = CGFloat(index) * 2.4
            let fraction = min(max((elapsed - start) / 2.2, 0), 1)
            let route = routes[index]
            let distance = route.reduce(CGFloat.zero) { $0 + $1.length } * fraction
            let (point, facing, horizontal) = location(on: route, distance: distance)
            let character = characters[index]
            character.position = point
            let moving = fraction > 0 && fraction < 1
            character.setWalking(moving, facing: horizontal ? facing : 1)
            if previous < progress, progress - previous < 0.2,
               previous * total < start + 2.2, elapsed >= start + 2.2 {
                character.react()
            }
            if moving {
                activeRoute = route
                activeDistance = distance
            }
        }
        drawHighlight(route: activeRoute, distance: activeDistance)
    }

    private func rebuild() {
        removeAllChildren()
        characters.removeAll()
        routes.removeAll()
        guard !members.isEmpty else { return }

        let count = members.count
        let side: CGFloat = 36
        let width = max(1, size.width - side * 2)
        let spacing = count == 1 ? 0 : width / CGFloat(count - 1)
        let lanes = (0..<count).map { side + CGFloat($0) * spacing }
        let top = max(100, size.height - 80)
        let bottom: CGFloat = min(78, max(38, top - 50))
        let usable = max(1, top - bottom)
        func rowY(_ row: Int) -> CGFloat {
            top - usable * CGFloat(row + 1) / CGFloat(layout.rowCount + 1)
        }

        let ladderPath = CGMutablePath()
        for x in lanes {
            ladderPath.move(to: CGPoint(x: x, y: top))
            ladderPath.addLine(to: CGPoint(x: x, y: bottom))
        }
        for rung in layout.rungs {
            let y = rowY(rung.row)
            ladderPath.move(to: CGPoint(x: lanes[rung.leftLane], y: y))
            ladderPath.addLine(to: CGPoint(x: lanes[rung.leftLane + 1], y: y))
        }
        let outline = SKShapeNode(path: ladderPath)
        outline.strokeColor = DrawGamePalette.outline
        outline.lineWidth = 7
        outline.lineCap = .round
        outline.zPosition = 0
        addChild(outline)
        let inner = SKShapeNode(path: ladderPath)
        inner.strokeColor = DrawGamePalette.cream
        inner.lineWidth = 3
        inner.lineCap = .round
        inner.zPosition = 1
        addChild(inner)
        highlight.strokeColor = DrawGamePalette.accent
        highlight.lineWidth = 5
        highlight.lineCap = .round
        highlight.fillColor = .clear
        highlight.zPosition = 2
        addChild(highlight)

        let textSize = min(CGFloat(12), max(8, spacing * 0.28))
        for index in 0..<count {
            let label = SKLabelNode(fontNamed: "AppleSDGothicNeo-SemiBold")
            let destination = event.ladderLabels?[safe: index] ?? ""
            label.text = destination
            label.fontSize = textSize
            label.fontColor = DrawGamePalette.cream
            label.horizontalAlignmentMode = .center
            label.verticalAlignmentMode = .center
            label.position = CGPoint(x: lanes[index], y: max(16, bottom - 54))
            label.zPosition = 3
            // Long user-entered labels may be identical and must remain in
            // their distinct server slots. Scale to fit rather than truncate.
            if spacing > 0, label.frame.width > spacing - 4 {
                label.xScale = max(0.35, (spacing - 4) / label.frame.width)
            }
            addChild(label)
        }

        for index in members.indices {
            let character = DrawCharacterNode(member: members[index])
            // The shared 48-point sprite is 2x its 24-pixel source. Half size
            // is still an integer 1x pixel scale on crowded ladders.
            let characterScale: CGFloat = spacing < 56 ? 0.5 : 1
            character.setScale(characterScale)
            character.zPosition = 4
            addChild(character)
            characters.append(character)
            routes.append(makeRoute(startLane: index, lanes: lanes, top: top, bottom: bottom, rowY: rowY))
        }
        setProgress(progress)
    }

    private func makeRoute(startLane: Int, lanes: [CGFloat], top: CGFloat, bottom: CGFloat,
                           rowY: (Int) -> CGFloat) -> [Segment] {
        var lane = startLane
        var previous = CGPoint(x: lanes[lane], y: top)
        var route: [Segment] = []
        for row in 0..<layout.rowCount {
            let y = rowY(row)
            let vertical = CGPoint(x: lanes[lane], y: y)
            route.append(Segment(from: previous, to: vertical))
            previous = vertical
            if let rung = layout.rungs.first(where: { $0.row == row &&
                ($0.leftLane == lane || $0.leftLane + 1 == lane) }) {
                lane += rung.leftLane == lane ? 1 : -1
                let across = CGPoint(x: lanes[lane], y: y)
                route.append(Segment(from: previous, to: across))
                previous = across
            }
        }
        route.append(Segment(from: previous, to: CGPoint(x: lanes[lane], y: bottom)))
        return route
    }

    private func location(on route: [Segment], distance: CGFloat) -> (CGPoint, CGFloat, Bool) {
        var remaining = distance
        for (index, segment) in route.enumerated() {
            if remaining <= segment.length || index == route.count - 1 {
                let portion = segment.length > 0 ? min(max(remaining / segment.length, 0), 1) : 1
                let point = CGPoint(x: segment.from.x + (segment.to.x - segment.from.x) * portion,
                                    y: segment.from.y + (segment.to.y - segment.from.y) * portion)
                let horizontal = segment.from.y == segment.to.y
                return (point, segment.to.x >= segment.from.x ? 1 : -1, horizontal)
            }
            remaining -= segment.length
        }
        return (route.last?.to ?? .zero, 1, false)
    }

    private func drawHighlight(route: [Segment], distance: CGFloat) {
        let path = CGMutablePath()
        guard let first = route.first else {
            highlight.path = nil
            return
        }
        path.move(to: first.from)
        var remaining = distance
        for segment in route {
            if remaining >= segment.length {
                path.addLine(to: segment.to)
                remaining -= segment.length
            } else {
                let portion = segment.length > 0 ? remaining / segment.length : 0
                path.addLine(to: CGPoint(x: segment.from.x + (segment.to.x - segment.from.x) * portion,
                                         y: segment.from.y + (segment.to.y - segment.from.y) * portion))
                break
            }
        }
        highlight.path = path
    }
}

private extension Collection {
    subscript(safe index: Index) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
