import AppKit
import SwiftUI

@MainActor
final class HistoryWindowController: NSWindowController, NSWindowDelegate {
    static let contentSize = CGSize(width: 560, height: 600)
    private let model: AppModel
    private let onTypingChanged: (Bool) -> Void
    private(set) var historyStore: MessageHistoryStore

    convenience init(model: AppModel) {
        self.init(model: model, loadPage: { _, _, _ in
            MessageHistoryPage(messages: [], nextCursor: nil)
        })
    }

    init(
        model: AppModel,
        loadPage: @escaping MessageHistoryPageLoader,
        onSend: @escaping (String) -> Void = { _ in },
        onTypingChanged: @escaping (Bool) -> Void = { _ in }
    ) {
        self.model = model
        self.onTypingChanged = onTypingChanged
        let historyStore = MessageHistoryStore(loadPage: loadPage)
        self.historyStore = historyStore
        let window = NSWindow(
            contentRect: CGRect(origin: .zero, size: Self.contentSize),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = L10n.format("history.window.title", AppPresentation.displayName)
        window.level = .normal
        window.collectionBehavior = [.managed]
        window.isReleasedWhenClosed = false
        window.minSize = CGSize(width: 440, height: 300)
        window.center()
        if let screen = window.screen ?? NSScreen.main {
            let visible = screen.visibleFrame.insetBy(dx: 8, dy: 8)
            var frame = window.frame
            frame.size.width = min(frame.width, visible.width)
            frame.size.height = min(frame.height, visible.height)
            frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
            frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
            window.setFrame(frame, display: false)
        }
        super.init(window: window)
        window.contentView = NSHostingView(rootView: OverlayHistoryView(
            model: model,
            history: historyStore,
            onClose: { [weak self] in self?.closeHistory() },
            onSend: { [weak self] body in
                onSend(body)
                DispatchQueue.main.async { [weak self] in self?.focusMessageField() }
            },
            onTypingChanged: onTypingChanged
        ))
        window.delegate = self
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show() {
        historyStore.activate(roomID: model.realtimeActiveRoomID)
        window?.makeKeyAndOrderFront(nil)
        DispatchQueue.main.async { [weak self] in self?.focusMessageField() }
    }

    func closeHistory() {
        onTypingChanged(false)
        historyStore.deactivate()
        window?.orderOut(nil)
    }

    func windowWillClose(_ notification: Notification) {
        onTypingChanged(false)
        historyStore.deactivate()
    }

    func windowDidResignKey(_ notification: Notification) { onTypingChanged(false) }

    private func focusMessageField() {
        guard let window, window.isVisible, window.isKeyWindow else { return }
        window.contentView?.layoutSubtreeIfNeeded()
        func findField(in view: NSView) -> NSView? {
            if view.identifier == NSUserInterfaceItemIdentifier("sidey.message-field") { return view }
            for child in view.subviews {
                if let field = findField(in: child) { return field }
            }
            return nil
        }
        if let content = window.contentView, let field = findField(in: content) {
            window.makeFirstResponder(field)
        }
    }
}
