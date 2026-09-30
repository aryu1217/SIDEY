import AppKit
import SwiftUI

private final class DrawIconPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class DrawIconBubbleController {
    private static func size(for count: Int) -> CGSize {
        CGSize(width: count == 1 ? 55 : 104, height: 50)
    }
    private let panel: DrawIconPanel
    private let hostingView: NSHostingView<DrawIconBubbleView>
    private let onOpen: (UUID) -> Void
    private var events: [DrawEvent]

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

    func update(events: [DrawEvent], characterFrame: CGRect?, visibleFrame: CGRect?, visible: Bool) {
        if self.events != events {
            self.events = events
            hostingView.rootView = DrawIconBubbleView(events: events, onOpen: onOpen)
        }
        guard visible, !events.isEmpty, let characterFrame, let visibleFrame else {
            panel.orderOut(nil)
            return
        }
        let size = Self.size(for: events.count)
        let x = min(max(characterFrame.midX - size.width / 2, visibleFrame.minX),
                    visibleFrame.maxX - size.width)
        let idealY = characterFrame.maxY + 110
        let y = min(max(idealY, visibleFrame.minY), visibleFrame.maxY - size.height)
        let frame = CGRect(origin: CGPoint(x: x, y: y), size: size)
        if panel.frame != frame { panel.setFrame(frame, display: true) }
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    func hide() { panel.orderOut(nil) }
}

private struct DrawIconBubbleView: View {
    let events: [DrawEvent]
    let onOpen: (UUID) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                ForEach(events) { event in
                    Button { onOpen(event.id) } label: {
                        Image(systemName: event.kind.symbol)
                            .font(.system(size: 17, weight: .bold))
                            .frame(width: 33, height: 30)
                    }
                    .buttonStyle(.plain)
                    .help(event.kind.title)
                    .accessibilityLabel(event.kind.title)
                }
            }
            .padding(.horizontal, 7)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 11))
            Image(systemName: "triangle.fill")
                .font(.system(size: 10))
                .rotationEffect(.degrees(180))
                .foregroundStyle(.regularMaterial)
                .offset(y: -2)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
