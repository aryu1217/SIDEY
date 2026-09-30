import AppKit
import SwiftUI

private final class DrawIconPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class DrawIconBubbleController {
    private static func size(for count: Int) -> CGSize {
        CGSize(width: CGFloat(count * 34 + max(0, count - 1) * 4 + 12), height: 48)
    }
    private let panel: DrawIconPanel
    private let hostingView: NSHostingView<DrawIconBubbleView>
    private let onOpen: (UUID) -> Void
    private var events: [DrawEvent]
    private var previousChatBubbleSize: CGSize?
    private var lastRequestedFrame: CGRect?
    private var frameTransitionTask: Task<Void, Never>?
    private var transitionStartFrame: CGRect?
    private var transitionStartedAt: TimeInterval = 0

    init(events: [DrawEvent], onOpen: @escaping (UUID) -> Void) {
        self.events = events
        self.onOpen = onOpen
        panel = DrawIconPanel(
            contentRect: CGRect(origin: .zero, size: Self.size(for: events.count)),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.isReleasedWhenClosed = false
        hostingView = NSHostingView(rootView: DrawIconBubbleView(events: events, onOpen: onOpen))
        panel.contentView = hostingView
    }

    func update(
        events: [DrawEvent],
        characterFrame: CGRect?,
        chatBubbleFrame: CGRect?,
        visibleFrame: CGRect?,
        visible: Bool
    ) {
        if self.events != events {
            self.events = events
            hostingView.rootView = DrawIconBubbleView(events: events, onOpen: onOpen)
        }
        guard visible, !events.isEmpty, let characterFrame, let visibleFrame else {
            hide()
            return
        }
        let size = Self.size(for: events.count)
        let centeredX = characterFrame.midX - size.width / 2
        var x = min(max(centeredX, visibleFrame.minX), visibleFrame.maxX - size.width)
        let restingY = characterFrame.maxY + 4
        let idealY = max(restingY, chatBubbleFrame.map { $0.maxY + 6 } ?? restingY)
        let y = min(max(idealY, visibleFrame.minY), visibleFrame.maxY - size.height)
        if let chatBubbleFrame,
           CGRect(origin: CGPoint(x: x, y: y), size: size).intersects(chatBubbleFrame) {
            // A tall chat stack can leave no room above it. Keep the game
            // bubble beside that stack instead of covering the message.
            let alternatives = [chatBubbleFrame.minX - size.width - 6, chatBubbleFrame.maxX + 6]
                .filter { $0 >= visibleFrame.minX && $0 + size.width <= visibleFrame.maxX }
                .sorted { abs($0 - centeredX) < abs($1 - centeredX) }
            if let alternative = alternatives.first(where: {
                !CGRect(origin: CGPoint(x: $0, y: y), size: size).intersects(chatBubbleFrame)
            }) {
                x = alternative
            }
        }
        let frame = CGRect(origin: CGPoint(x: x, y: y), size: size)
        let chatBubbleSize = chatBubbleFrame.map {
            CGSize(width: $0.width.rounded(), height: $0.height.rounded())
        }
        if lastRequestedFrame != frame {
            let chatLayoutChanged = chatBubbleSize != previousChatBubbleSize
            lastRequestedFrame = frame
            if panel.isVisible, !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
               chatLayoutChanged || frameTransitionTask != nil {
                if chatLayoutChanged || frameTransitionTask == nil {
                    transitionStartFrame = panel.frame
                    transitionStartedAt = ProcessInfo.processInfo.systemUptime
                }
                startFrameTransitionIfNeeded()
            } else {
                cancelFrameTransition()
                panel.setFrame(frame, display: true)
            }
        }
        previousChatBubbleSize = chatBubbleSize
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    func hide() {
        cancelFrameTransition()
        lastRequestedFrame = nil
        previousChatBubbleSize = nil
        if panel.isVisible { panel.orderOut(nil) }
    }

    private func startFrameTransitionIfNeeded() {
        guard frameTransitionTask == nil else { return }
        // Character and chat geometry arrive separately on every scene update.
        // Keep one writer for the panel frame: new reports update the target,
        // rather than fighting an in-flight AppKit frame animation.
        frameTransitionTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard self?.advanceFrameTransition() == true else { return }
                do { try await Task.sleep(for: .seconds(1.0 / 30)) }
                catch { return }
            }
        }
    }

    private func advanceFrameTransition() -> Bool {
        guard let start = transitionStartFrame, let target = lastRequestedFrame else {
            frameTransitionTask = nil
            return false
        }
        let progress = min(1, max(0, (ProcessInfo.processInfo.systemUptime - transitionStartedAt) / 0.22))
        let eased = CGFloat(progress * progress * (3 - 2 * progress))
        let frame = CGRect(
            x: start.minX + (target.minX - start.minX) * eased,
            y: start.minY + (target.minY - start.minY) * eased,
            width: target.width,
            height: target.height
        )
        panel.setFrame(frame, display: true)
        if progress == 1 {
            transitionStartFrame = nil
            frameTransitionTask = nil
            return false
        }
        return true
    }

    private func cancelFrameTransition() {
        frameTransitionTask?.cancel()
        frameTransitionTask = nil
        transitionStartFrame = nil
    }
}

private struct DrawIconBubbleView: View {
    let events: [DrawEvent]
    let onOpen: (UUID) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 4) {
                ForEach(events) { event in
                    Button { onOpen(event.id) } label: {
                        DrawPixelGameIcon(kind: event.kind)
                            .frame(width: 34, height: 32)
                    }
                    .buttonStyle(.plain)
                    .help(event.displayTitle)
                    .accessibilityLabel(event.displayTitle)
                }
            }
            .padding(.horizontal, 6)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
            .overlay {
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(.white.opacity(0.7), lineWidth: 1)
            }
            Image(systemName: "triangle.fill")
                .font(.system(size: 8))
                .rotationEffect(.degrees(180))
                .foregroundStyle(.regularMaterial)
                .offset(y: -1)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Each tile is one logical pixel. A 24 pt canvas maps it to an integer number
/// of backing pixels at the system display scale, without interpolation.
struct DrawPixelGameIcon: View {
    let kind: DrawKind

    var body: some View {
        Canvas(opaque: false, rendersAsynchronously: false) { context, _ in
            for y in 0..<24 {
                for x in 0..<24 {
                    guard let color = Self.pixel(kind: kind, x: x, y: y) else { continue }
                    context.fill(
                        Path(CGRect(x: CGFloat(x), y: CGFloat(y), width: 1, height: 1)),
                        with: .color(color)
                    )
                }
            }
        }
        .frame(width: 24, height: 24)
        .accessibilityHidden(true)
    }

    private static let ink = Color(red: 0.20, green: 0.17, blue: 0.31)
    private static let light = Color(red: 1.00, green: 0.97, blue: 0.85)
    private static let gold = Color(red: 1.00, green: 0.74, blue: 0.28)
    private static let coral = Color(red: 0.96, green: 0.35, blue: 0.43)
    private static let violet = Color(red: 0.49, green: 0.37, blue: 0.91)
    private static let teal = Color(red: 0.22, green: 0.73, blue: 0.79)

    private static func pixel(kind: DrawKind, x: Int, y: Int) -> Color? {
        switch kind {
        case .lot:
            guard (3...20).contains(x), (4...19).contains(y),
                  !(x < 5 && (y < 6 || y > 17)),
                  !(x > 18 && (y < 6 || y > 17)) else { return nil }
            if x == 3 || x == 20 || y == 4 || y == 19 ||
                (x == 4 || x == 19) && (y == 5 || y == 18) { return ink }
            if (7...9).contains(y) { return coral }
            if y == 10 { return ink }
            if (x == 8 || x == 16) && (12...17).contains(y) { return gold }
            if (11...13).contains(x) && (12...16).contains(y) { return gold }
            return light
        case .ladder:
            guard (3...20).contains(x), (2...21).contains(y) else { return nil }
            if (x == 4 || x == 19) && (3...20).contains(y) { return ink }
            if (x == 5 || x == 18) && (3...20).contains(y) { return violet }
            if (6...17).contains(x) && [5, 9, 13, 17].contains(y) { return ink }
            if (6...17).contains(x) && [6, 10, 14, 18].contains(y) { return gold }
            if (4...6).contains(x) && (y == 2 || y == 21) { return ink }
            if (17...19).contains(x) && (y == 2 || y == 21) { return ink }
            return nil
        case .roulette:
            if (10...13).contains(x) && (0...3).contains(y) {
                return y == 0 ? ink : gold
            }
            if (6...17).contains(x) && y == 22 { return ink }
            if (8...15).contains(x) && y == 21 { return violet }
            let dx = Double(x) - 11.5
            let dy = Double(y) - 12
            let distance = dx * dx + dy * dy
            guard distance <= 90 else { return nil }
            if distance >= 68 { return ink }
            if distance <= 2 { return gold }
            if distance <= 5 { return ink }
            let sector = Int((atan2(dy, dx) + .pi) / (.pi / 3))
            return [coral, light, teal, violet, gold, light][min(sector, 5)]
        }
    }
}
