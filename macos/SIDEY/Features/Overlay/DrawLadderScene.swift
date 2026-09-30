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
        let count = event.gameEntryCount
        let labels = event.ladderLabels ?? []
        let assignmentsByUser = Dictionary(event.result.assignments?.compactMap { assignment in
            assignment.userID.map { ($0, assignment.label) }
        } ?? [], uniquingKeysWith: { first, _ in first })
        let assignmentsByEntry = Dictionary(event.result.assignments?.compactMap { assignment in
            assignment.entryIndex.map { ($0, assignment.label) }
        } ?? [], uniquingKeysWith: { first, _ in first })
        var claimed = Set<Int>()
        var destinations = [Int]()
        for index in 0..<count {
            let assigned = event.isFreeEntryMode
                ? assignmentsByEntry[index]
                : assignmentsByUser[participantIDs[index]]
            let matching = assigned.flatMap { assigned in
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

        // Scramble first, then route to the server's assigned slots. The extra
        // crossings make even identity assignments travel through the ladder.
        var random = DrawGameRandom(eventID: event.id)
        var generated: [DrawLadderRung] = []
        var order = Array(0..<count)
        let scrambleRows = 4 + random.index(3)
        for row in 0..<scrambleRows {
            let firstLane = count == 2 ? 0 : row % 2
            let eligible = stride(from: firstLane, to: count - 1, by: 2).map { $0 }
            let selected = eligible.filter { _ in random.index(3) != 0 }
            let lanes = selected.isEmpty ? [eligible[random.index(eligible.count)]] : selected
            for lane in lanes {
                generated.append(DrawLadderRung(row: row, leftLane: lane))
                order.swapAt(lane, lane + 1)
            }
        }
        // Odd-even transposition sorts by assigned bottom slot. Each row's
        // exchanges are disjoint, so every crossing has a visible rung.
        for phase in 0..<count {
            let firstLane = phase % 2
            var lane = firstLane
            while lane + 1 < count {
                if destinations[order[lane]] > destinations[order[lane + 1]] {
                    order.swapAt(lane, lane + 1)
                    generated.append(DrawLadderRung(row: scrambleRows + phase, leftLane: lane))
                }
                lane += 2
            }
        }
        rungs = generated
        rowCount = scrambleRows + count
    }
}

@MainActor
final class DrawLadderScene: SKScene {
    static let controlsAnchorY: CGFloat = 8
    private static let railEdge = NSColor(srgbRed: 0.39, green: 0.35, blue: 0.49, alpha: 1)
    private static let railFill = NSColor(srgbRed: 0.89, green: 0.86, blue: 0.97, alpha: 1)
    private struct Segment {
        let from: CGPoint
        let to: CGPoint
        var length: CGFloat { hypot(to.x - from.x, to.y - from.y) }
    }

    private let event: DrawEvent
    private let entries: [DrawGameEntry]
    private let layout: DrawLadderLayout
    private var gameNodes: [SKNode] = []
    private var routes: [[Segment]] = []
    private var destinationMarkers: [SKShapeNode] = []
    private let rungOutline = SKShapeNode()
    private let rungInner = SKShapeNode()
    private let highlight = SKShapeNode()
    private var progress: CGFloat = 0

    init(event: DrawEvent, room: Room, size: CGSize) {
        self.event = event
        entries = event.gameEntries(in: room)
        layout = DrawLadderLayout(event: event, participantIDs: event.participantIDs)
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
        rungOutline.isHidden = progress == 0
        rungInner.isHidden = progress == 0
        let count = gameNodes.count
        guard count > 0 else { return }
        let total = CGFloat(count) * 2.2 + CGFloat(max(0, count - 1)) * 0.2
        let elapsed = progress * total
        var activeRoute: [Segment] = []
        var activeDistance: CGFloat = 0

        for index in gameNodes.indices {
            let start = CGFloat(index) * 2.4
            let fraction = min(max((elapsed - start) / 2.2, 0), 1)
            let route = routes[index]
            let distance = route.reduce(CGFloat.zero) { $0 + $1.length } * fraction
            let (point, facing, horizontal) = location(on: route, distance: distance)
            let node = gameNodes[index]
            node.position = point
            let moving = fraction > 0 && fraction < 1
            let destination = layout.destinationForParticipant[index]
            if destinationMarkers.indices.contains(destination) {
                let marker = destinationMarkers[destination]
                marker.strokeColor = fraction == 1 ? DrawGamePalette.accent : Self.railEdge
                marker.lineWidth = fraction == 1 ? 2.5 : 1.5
            }
            if let character = node as? DrawCharacterNode {
                character.setWalking(moving, facing: horizontal ? facing : 1)
                if previous < progress, progress - previous < 0.2,
                   previous * total < start + 2.2, elapsed >= start + 2.2 {
                    character.react()
                }
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
        gameNodes.removeAll()
        routes.removeAll()
        destinationMarkers.removeAll()
        guard !entries.isEmpty, size.width > 0, size.height > 0 else { return }

        let count = entries.count
        let side = min(90, max(28, size.width / CGFloat(max(2, count * 2))))
        let width = max(1, size.width - side * 2)
        let spacing = count == 1 ? 0 : width / CGFloat(count - 1)
        let lanes = (0..<count).map { count == 1 ? size.width / 2 : side + CGFloat($0) * spacing }
        let top = max(100, size.height - 80)
        let bottom: CGFloat = min(112, max(78, top - 80))
        let usable = max(1, top - bottom)
        func rowY(_ row: Int) -> CGFloat {
            top - usable * CGFloat(row + 1) / CGFloat(layout.rowCount + 1)
        }

        let railPath = CGMutablePath()
        for x in lanes {
            railPath.move(to: CGPoint(x: x, y: top))
            railPath.addLine(to: CGPoint(x: x, y: bottom))
        }
        let rungPath = CGMutablePath()
        for rung in layout.rungs {
            let y = rowY(rung.row)
            rungPath.move(to: CGPoint(x: lanes[rung.leftLane], y: y))
            rungPath.addLine(to: CGPoint(x: lanes[rung.leftLane + 1], y: y))
        }
        let railOutline = SKShapeNode(path: railPath)
        railOutline.strokeColor = Self.railEdge
        railOutline.lineWidth = 7
        railOutline.lineCap = .round
        railOutline.zPosition = 0
        addChild(railOutline)
        let railInner = SKShapeNode(path: railPath)
        railInner.strokeColor = Self.railFill
        railInner.lineWidth = 3
        railInner.lineCap = .round
        railInner.zPosition = 1
        addChild(railInner)
        rungOutline.path = rungPath
        rungOutline.name = "ladder-rungs"
        rungOutline.isHidden = progress == 0
        rungOutline.strokeColor = Self.railEdge
        rungOutline.lineWidth = 7
        rungOutline.lineCap = .round
        rungOutline.zPosition = 0
        addChild(rungOutline)
        rungInner.path = rungPath
        rungInner.isHidden = progress == 0
        rungInner.strokeColor = Self.railFill
        rungInner.lineWidth = 3
        rungInner.lineCap = .round
        rungInner.zPosition = 1
        addChild(rungInner)
        highlight.strokeColor = DrawGamePalette.accent
        highlight.lineWidth = 5
        highlight.lineCap = .round
        highlight.fillColor = .clear
        highlight.zPosition = 2
        addChild(highlight)

        let labelWidth = min(170, max(24, spacing - 10))
        for index in 0..<count {
            let start = SKShapeNode(ellipseOf: CGSize(width: min(26, max(12, spacing * 0.38)), height: 8))
            start.position = CGPoint(x: lanes[index], y: top)
            start.fillColor = DrawGamePalette.cream
            start.strokeColor = Self.railEdge
            start.lineWidth = 1.5
            start.zPosition = 2
            addChild(start)

            let destination = SKShapeNode(ellipseOf: CGSize(width: min(44, max(18, spacing * 0.6)), height: 12))
            destination.position = CGPoint(x: lanes[index], y: bottom - 2)
            destination.fillColor = DrawGamePalette.colors[index % DrawGamePalette.colors.count]
            destination.strokeColor = Self.railEdge
            destination.lineWidth = 1.5
            destination.zPosition = 2
            addChild(destination)
            destinationMarkers.append(destination)

            let label = DrawGameTextNode(text: event.ladderLabels?[safe: index] ?? "", fontSize: 12,
                                         color: DrawGamePalette.outline, showsBackground: false)
            label.fit(maxWidth: labelWidth, maxHeight: 44)
            let labelHeight = max(30, min(62, label.calculateAccumulatedFrame().height + 8))
            let center = CGPoint(x: lanes[index], y: Self.controlsAnchorY + labelHeight / 2)
            let tag = SKShapeNode(rectOf: CGSize(width: labelWidth, height: labelHeight), cornerRadius: 8)
            tag.position = center
            tag.fillColor = DrawGamePalette.cream
            tag.strokeColor = Self.railEdge
            tag.lineWidth = 1
            tag.zPosition = 2
            addChild(tag)
            label.position = center
            label.zPosition = 3
            addChild(label)
        }

        for (index, entry) in entries.enumerated() {
            let node: SKNode
            if let member = entry.member {
                let character = DrawCharacterNode(member: member)
                // The shared 48-point sprite is 2x its 24-pixel source. Half
                // size is still an integer 1x scale on crowded ladders.
                character.setScale(spacing < 56 ? 0.5 : 1)
                node = character
            } else {
                let text = DrawGameTextNode(text: entry.label)
                text.fit(maxWidth: max(24, spacing - 6))
                node = text
            }
            node.zPosition = 4
            addChild(node)
            gameNodes.append(node)
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
