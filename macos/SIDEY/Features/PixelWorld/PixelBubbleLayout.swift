import AppKit

enum PixelBubbleTailAttachment: Equatable, Sendable {
    case bottom
    case top
    case left
    case right
}

struct PixelBubbleLayout: Equatable, Sendable {
    let size: CGSize
    let nodePosition: CGPoint
    let nodeRotation: CGFloat
    let labelRotation: CGFloat
    let tailTip: CGPoint
    let tailAttachment: PixelBubbleTailAttachment
    let bodyFrame: CGRect
    let visualFrame: CGRect
    let totalFrame: CGRect

    static func make(
        text: String,
        isTyping: Bool,
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        edge: OverlayEdge,
        preferredTangentPosition: CGFloat? = nil,
        bodyMinY: CGFloat = 52,
        includesTail: Bool = true,
        leadingDecorationOverflow: CGFloat = 0,
        maximumContentWidth: CGFloat? = nil
    ) -> Self {
        let maximumWidth = min(220, max(24, maximumContentWidth ?? (tangentLength - 16)))
        let size: CGSize
        if isTyping {
            size = CGSize(width: min(42, maximumWidth), height: 30)
        } else {
            size = PixelBubbleMeasurementCache.shared.size(
                text: text,
                maximumWidth: maximumWidth
            )
        }

        let nodeRotation = edge.bubbleCounterRotation
        let bodyRect = CGRect(
            x: -size.width / 2,
            y: -size.height / 2,
            width: size.width,
            height: size.height
        )
        let transform = CGAffineTransform(rotationAngle: nodeRotation)
        let relativeBodyFrame = bodyRect.applying(transform)
        var relativeVisualFrame = relativeBodyFrame
        if leadingDecorationOverflow > 0 {
            let decorationFrame = CGRect(
                x: bodyRect.minX - leadingDecorationOverflow,
                y: bodyRect.maxY - PixelBubbleStyle.decorationSize / 2,
                width: PixelBubbleStyle.decorationSize,
                height: PixelBubbleStyle.decorationSize
            )
            relativeVisualFrame = relativeVisualFrame.union(decorationFrame.applying(transform))
        }

        let tangentSign: CGFloat = switch edge {
        case .bottom, .right: 1
        case .top, .left: -1
        }
        let firstVisualDelta = relativeVisualFrame.minX * tangentSign
        let secondVisualDelta = relativeVisualFrame.maxX * tangentSign
        let minimumVisualDelta = min(firstVisualDelta, secondVisualDelta)
        let maximumVisualDelta = max(firstVisualDelta, secondVisualDelta)
        let minimumCenter = 4 - minimumVisualDelta
        let maximumCenter = tangentLength - 4 - maximumVisualDelta
        let worldCenter = min(
            max(preferredTangentPosition ?? tangentPosition, minimumCenter),
            max(minimumCenter, maximumCenter)
        )
        let localCenterX = (worldCenter - tangentPosition) * tangentSign
        let nodePosition = CGPoint(
            x: localCenterX,
            y: bodyMinY - relativeBodyFrame.minY
        )
        let bodyFrame = relativeBodyFrame.offsetBy(dx: nodePosition.x, dy: nodePosition.y)
        let visualFrame = relativeVisualFrame.offsetBy(dx: nodePosition.x, dy: nodePosition.y)
        let tailTarget = CGPoint(x: 0, y: 44)
        let tailTip = CGPoint(
            x: tailTarget.x - nodePosition.x,
            y: tailTarget.y - nodePosition.y
        ).applying(CGAffineTransform(rotationAngle: -nodeRotation))
        let totalFrame: CGRect
        if includesTail {
            totalFrame = visualFrame.union(CGRect(
                origin: tailTarget,
                size: CGSize(width: 0.001, height: 0.001)
            ))
        } else {
            totalFrame = visualFrame
        }
        let tailAttachment: PixelBubbleTailAttachment = switch edge {
        case .left: .left
        case .right: .right
        case .bottom: .bottom
        case .top: .top
        }
        return Self(
            size: size,
            nodePosition: nodePosition,
            nodeRotation: nodeRotation,
            labelRotation: 0,
            tailTip: tailTip,
            tailAttachment: tailAttachment,
            bodyFrame: bodyFrame,
            visualFrame: visualFrame,
            totalFrame: totalFrame
        )
    }

    var tailTipInPresentation: CGPoint {
        let rotatedTip = tailTip.applying(CGAffineTransform(rotationAngle: nodeRotation))
        return CGPoint(
            x: rotatedTip.x + nodePosition.x,
            y: rotatedTip.y + nodePosition.y
        )
    }

    func bodyTangentRange(at tangentPosition: CGFloat, edge: OverlayEdge) -> ClosedRange<CGFloat> {
        let tangentSign: CGFloat = switch edge {
        case .bottom, .right: 1
        case .top, .left: -1
        }
        let first = tangentPosition + bodyFrame.minX * tangentSign
        let second = tangentPosition + bodyFrame.maxX * tangentSign
        return min(first, second)...max(first, second)
    }

    func visualTangentOffsets(edge: OverlayEdge) -> ClosedRange<CGFloat> {
        let tangentSign: CGFloat = switch edge {
        case .bottom, .right: 1
        case .top, .left: -1
        }
        let first = (visualFrame.minX - nodePosition.x) * tangentSign
        let second = (visualFrame.maxX - nodePosition.x) * tangentSign
        return min(first, second)...max(first, second)
    }
}

struct PixelBubbleStackEntry: Equatable, Sendable {
    let bubble: ActiveBubble
    let layout: PixelBubbleLayout
    let includesTail: Bool
}

enum PixelBubbleStackLayout {
    static let bodySpacing: CGFloat = 6

    static func make(
        bubbles: [ActiveBubble],
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        edge: OverlayEdge,
        inwardNormalLength: CGFloat? = nil
    ) -> [PixelBubbleStackEntry] {
        let ordered = bubbles.sorted(by: ActiveBubble.presentationOrder)
        let visible = Array(ordered.suffix(ActiveBubbleLedger.maximumVisiblePerSender))
        guard !visible.isEmpty else { return [] }
        if !edge.isHorizontal {
            return makeSideEdgeStack(
                visible: visible,
                latestMessageID: ordered.last?.messageID,
                tangentPosition: tangentPosition,
                tangentLength: tangentLength,
                edge: edge,
                inwardNormalLength: inwardNormalLength
            )
        }
        var nextBodyMinY: CGFloat = 52
        var reversedEntries: [PixelBubbleStackEntry] = []

        for bubble in visible.reversed() {
            let isLatest = bubble.messageID == ordered.last?.messageID
            let decorationLeadingOverflow = PixelBubbleTheme.resolve(bubble.bubbleStyleID)
                .decorationAssetName == nil ? 0 : PixelBubbleStyle.decorationLeadingOverflow
            let layout = PixelBubbleLayout.make(
                text: bubble.body,
                isTyping: false,
                tangentPosition: tangentPosition,
                tangentLength: tangentLength,
                edge: edge,
                bodyMinY: nextBodyMinY,
                includesTail: isLatest,
                leadingDecorationOverflow: decorationLeadingOverflow,
                maximumContentWidth: nil
            )
            reversedEntries.append(PixelBubbleStackEntry(
                bubble: bubble,
                layout: layout,
                includesTail: isLatest
            ))
            nextBodyMinY = layout.bodyFrame.maxY + bodySpacing
        }

        return Array(reversedEntries.reversed())
    }

    private static func makeSideEdgeStack(
        visible: [ActiveBubble],
        latestMessageID: UUID?,
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        edge: OverlayEdge,
        inwardNormalLength: CGFloat?
    ) -> [PixelBubbleStackEntry] {
        struct MeasuredBubble {
            let bubble: ActiveBubble
            let includesTail: Bool
            let decorationLeadingOverflow: CGFloat
            let maximumContentWidth: CGFloat?
            let tangentOffsets: ClosedRange<CGFloat>
            let centerOffset: CGFloat
        }

        var previousUpperBound: CGFloat?
        var measuredNewestFirst: [MeasuredBubble] = []
        for bubble in visible.reversed() {
            let includesTail = bubble.messageID == latestMessageID
            let decorationLeadingOverflow = PixelBubbleTheme.resolve(bubble.bubbleStyleID)
                .decorationAssetName == nil ? 0 : PixelBubbleStyle.decorationLeadingOverflow
            let maximumContentWidth = inwardNormalLength.map {
                max(
                    24,
                    min(
                        220,
                        $0 - 56 - (edge == .right ? decorationLeadingOverflow : 0)
                    )
                )
            }
            let probe = PixelBubbleLayout.make(
                text: bubble.body,
                isTyping: false,
                tangentPosition: tangentLength / 2,
                tangentLength: tangentLength,
                edge: edge,
                includesTail: includesTail,
                leadingDecorationOverflow: decorationLeadingOverflow,
                maximumContentWidth: maximumContentWidth
            )
            let tangentOffsets = probe.visualTangentOffsets(edge: edge)
            let centerOffset = previousUpperBound.map {
                $0 + bodySpacing - tangentOffsets.lowerBound
            } ?? 0
            previousUpperBound = centerOffset + tangentOffsets.upperBound
            measuredNewestFirst.append(MeasuredBubble(
                bubble: bubble,
                includesTail: includesTail,
                decorationLeadingOverflow: decorationLeadingOverflow,
                maximumContentWidth: maximumContentWidth,
                tangentOffsets: tangentOffsets,
                centerOffset: centerOffset
            ))
        }

        let groupLowerBound = measuredNewestFirst.map {
            $0.centerOffset + $0.tangentOffsets.lowerBound
        }.min() ?? 0
        let groupUpperBound = measuredNewestFirst.map {
            $0.centerOffset + $0.tangentOffsets.upperBound
        }.max() ?? 0
        let minimumAnchor = 4 - groupLowerBound
        let maximumAnchor = tangentLength - 4 - groupUpperBound
        let anchor = min(
            max(tangentPosition, minimumAnchor),
            max(minimumAnchor, maximumAnchor)
        )

        let newestFirst = measuredNewestFirst.map { measured in
            PixelBubbleStackEntry(
                bubble: measured.bubble,
                layout: PixelBubbleLayout.make(
                    text: measured.bubble.body,
                    isTyping: false,
                    tangentPosition: tangentPosition,
                    tangentLength: tangentLength,
                    edge: edge,
                    preferredTangentPosition: anchor + measured.centerOffset,
                    includesTail: measured.includesTail,
                    leadingDecorationOverflow: measured.decorationLeadingOverflow,
                    maximumContentWidth: measured.maximumContentWidth
                ),
                includesTail: measured.includesTail
            )
        }
        return Array(newestFirst.reversed())
    }

    static func bodyTangentRange(
        for entries: [PixelBubbleStackEntry],
        at tangentPosition: CGFloat,
        edge: OverlayEdge
    ) -> ClosedRange<CGFloat>? {
        let ranges = entries.map { $0.layout.bodyTangentRange(at: tangentPosition, edge: edge) }
        guard let first = ranges.first else { return nil }
        return ranges.dropFirst().reduce(first) { partial, range in
            min(partial.lowerBound, range.lowerBound)...max(partial.upperBound, range.upperBound)
        }
    }
}

private extension ActiveBubble {
    static func presentationOrder(_ lhs: ActiveBubble, _ rhs: ActiveBubble) -> Bool {
        lhs.expiresAt == rhs.expiresAt
            ? lhs.messageID.uuidString < rhs.messageID.uuidString
            : lhs.expiresAt < rhs.expiresAt
    }
}

private final class PixelBubbleMeasurementCache: @unchecked Sendable {
    static let shared = PixelBubbleMeasurementCache()

    private let cache = NSCache<NSString, PixelBubbleMeasuredSize>()

    private init() {
        cache.countLimit = 128
    }

    func size(text: String, maximumWidth: CGFloat) -> CGSize {
        let key = "10.5|\(Int((maximumWidth * 10).rounded()))|\(text)" as NSString
        if let cached = cache.object(forKey: key) { return cached.value }

        let font = NSFont.systemFont(ofSize: 10.5, weight: .medium)
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byCharWrapping
        paragraph.alignment = .center
        let measured = (text as NSString).boundingRect(
            with: CGSize(width: max(8, maximumWidth - 16), height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: [.font: font, .paragraphStyle: paragraph]
        ).integral.size
        let result = CGSize(
            width: min(maximumWidth, max(28, ceil(measured.width) + 16)),
            height: max(28, ceil(measured.height) + 14)
        )
        cache.setObject(PixelBubbleMeasuredSize(result), forKey: key)
        return result
    }
}

private final class PixelBubbleMeasuredSize: NSObject {
    let value: CGSize

    init(_ value: CGSize) {
        self.value = value
    }
}
