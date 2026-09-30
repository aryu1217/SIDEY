import AppKit
import SwiftUI

private final class InteractiveOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

private final class CharacterHotspotPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
protocol ComposerFocusLossScheduling: AnyObject {
    func schedule(after delay: Duration, action: @escaping @MainActor () -> Void)
    func cancel()
}

@MainActor
private final class TaskComposerFocusLossScheduler: ComposerFocusLossScheduling {
    private var task: Task<Void, Never>?

    func schedule(after delay: Duration, action: @escaping @MainActor () -> Void) {
        cancel()
        task = Task { @MainActor in
            do {
                try await Task.sleep(for: delay)
            } catch {
                return
            }
            action()
        }
    }

    func cancel() {
        task?.cancel()
        task = nil
    }
}

@MainActor
final class CharacterRightClickCoordinator {
    private let interval: TimeInterval
    private let onSingle: () -> Void
    private let onDouble: () -> Void
    private var pending: Task<Void, Never>?

    init(interval: TimeInterval = NSEvent.doubleClickInterval,
         onSingle: @escaping () -> Void, onDouble: @escaping () -> Void) {
        self.interval = interval
        self.onSingle = onSingle
        self.onDouble = onDouble
    }

    func handle(clickCount: Int) {
        cancel()
        if clickCount == 2 {
            onDouble()
        } else if clickCount == 1 {
            pending = Task { @MainActor [weak self, interval] in
                do { try await Task.sleep(for: .seconds(interval)) } catch { return }
                guard let self else { return }
                self.pending = nil
                self.onSingle()
            }
        }
    }

    func cancel() {
        pending?.cancel()
        pending = nil
    }

    deinit { pending?.cancel() }
}

private final class CharacterHotspotView: NSView {
    let onClick: (Int) -> Void
    let rightClicks: CharacterRightClickCoordinator

    init(onClick: @escaping (Int) -> Void, onRightClick: @escaping () -> Void, onDoubleRightClick: @escaping () -> Void) {
        self.onClick = onClick
        self.rightClicks = CharacterRightClickCoordinator(onSingle: onRightClick, onDouble: onDoubleRightClick)
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    override func mouseDown(with event: NSEvent) { onClick(event.clickCount) }
    override func rightMouseDown(with event: NSEvent) { rightClicks.handle(clickCount: event.clickCount) }
}

enum OverlayWindowIdentifier {
    static let composer = NSUserInterfaceItemIdentifier("sidey.overlay.composer")
    static let characterHotspot = NSUserInterfaceItemIdentifier("sidey.overlay.character-hotspot")

    static func isInteractionSource(_ identifier: NSUserInterfaceItemIdentifier?) -> Bool {
        identifier == composer || identifier == characterHotspot
    }
}

@MainActor
final class PixelWorldWindowController {
    private let panel: NSPanel
    private let model: AppModel
    private var hostingView: NSHostingView<PixelWorldView>?
    private var composerVisible = false
    private var composerFrame: CGRect?
    private var characterPulse: CharacterPulseEvent?
    private var characterThrow: CharacterThrowEvent?
    private var localActivityFrame: CGRect = .zero
    private let onCharacterFramesChanged: ([UUID: CGRect]) -> Void
    private let onChatBubbleFramesChanged: ([UUID: CGRect]) -> Void

    init(
        model: AppModel,
        frame: CGRect,
        onCharacterFramesChanged: @escaping ([UUID: CGRect]) -> Void = { _ in },
        onChatBubbleFramesChanged: @escaping ([UUID: CGRect]) -> Void = { _ in }
    ) {
        self.model = model
        self.onCharacterFramesChanged = onCharacterFramesChanged
        self.onChatBubbleFramesChanged = onChatBubbleFramesChanged
        panel = NSPanel(
            contentRect: frame,
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
        panel.ignoresMouseEvents = true
        panel.isReleasedWhenClosed = false
    }

    func setLayout(renderFrame: CGRect, localActivityFrame: CGRect) {
        self.localActivityFrame = localActivityFrame
        panel.setFrame(renderFrame, display: true)
        hostingView?.rootView = makeRootView()
    }

    func orderFront() {
        if hostingView == nil {
            let view = NSHostingView(rootView: makeRootView())
            hostingView = view
            panel.contentView = view
        }
        panel.orderFrontRegardless()
    }

    func setComposerVisible(_ visible: Bool) {
        guard composerVisible != visible else { return }
        composerVisible = visible
        hostingView?.rootView = makeRootView()
    }

    func setComposerFrame(_ frame: CGRect) {
        guard composerFrame != frame else { return }
        composerFrame = frame
        hostingView?.rootView = makeRootView()
    }

    func playCharacterPulse(_ event: CharacterPulseEvent) {
        guard event.roomID == model.activeRoom?.id, let hostingView else { return }
        characterPulse = event
        hostingView.rootView = makeRootView()
    }

    func playCharacterThrow(_ event: CharacterThrowEvent) {
        guard event.roomID == model.activeRoom?.id, let hostingView else { return }
        characterThrow = event
        hostingView.rootView = makeRootView()
    }

    func orderOut() {
        model.characterStunState.reset()
        model.characterImpactAudio.stopAll()
        panel.orderOut(nil)
        panel.contentView = nil
        hostingView = nil
        characterPulse = nil
        characterThrow = nil
    }

    var level: NSWindow.Level { panel.level }
    var isVisible: Bool { panel.isVisible }
    var canHide: Bool { panel.canHide }
    var ignoresMouseEvents: Bool { panel.ignoresMouseEvents }
    var isRendering: Bool { hostingView != nil }
    var size: CGSize { panel.frame.size }
    var collectionBehavior: NSWindow.CollectionBehavior { panel.collectionBehavior }

    private func makeRootView() -> PixelWorldView {
        PixelWorldView(
            model: model,
            activityFrame: localActivityFrame,
            composerVisible: composerVisible,
            composerFrame: composerFrame,
            characterPulse: characterPulse,
            characterThrow: characterThrow,
            onCharacterFramesChanged: onCharacterFramesChanged,
            onChatBubbleFramesChanged: onChatBubbleFramesChanged
        )
    }
}

enum OverlayComposerLayout {
    static let panelSize = CGSize(width: 400, height: 56)
    static let topInset: CGFloat = 10

    static func frame(in visibleFrame: CGRect) -> CGRect {
        CGRect(
            x: visibleFrame.midX - panelSize.width / 2,
            y: visibleFrame.maxY - panelSize.height - topInset,
            width: panelSize.width,
            height: panelSize.height
        )
    }
}

@MainActor
final class OverlayInteractionWindowController: NSObject, NSWindowDelegate {
    static let panelSize = OverlayComposerLayout.panelSize
    static let focusLossDismissDelay: Duration = .milliseconds(250)
    private let panel: NSPanel
    private let onDismissRequested: () -> Void
    private let onTypingEnded: () -> Void
    private let focusLossScheduler: any ComposerFocusLossScheduling
    private let isApplicationActive: @MainActor () -> Bool
    private let onFrameChanged: (CGRect, Bool) -> Void
    private var settingFrame = false
    private var focusRequestID = 0
    private var isProgrammaticallyHiding = false
    private(set) var hasPendingFocusLossDismiss = false

    init(
        model: AppModel,
        onSend: @escaping (String) -> Void,
        onInputActivity: @escaping () -> Void,
        onTypingChanged: @escaping (Bool) -> Void,
        onCancel: @escaping () -> Void,
        onFrameChanged: @escaping (CGRect, Bool) -> Void = { _, _ in },
        focusLossScheduler: (any ComposerFocusLossScheduling)? = nil,
        isApplicationActive: @escaping @MainActor () -> Bool = { NSApplication.shared.isActive }
    ) {
        onDismissRequested = onCancel
        onTypingEnded = { onTypingChanged(false) }
        self.focusLossScheduler = focusLossScheduler ?? TaskComposerFocusLossScheduler()
        self.isApplicationActive = isApplicationActive
        self.onFrameChanged = onFrameChanged
        panel = InteractiveOverlayPanel(
            contentRect: CGRect(origin: .zero, size: Self.panelSize),
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        super.init()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(applicationDidResignActive(_:)),
            name: NSApplication.didResignActiveNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(self, selector: #selector(dragEnded(_:)),
            name: .sideyComposerDragEnded, object: panel)
        panel.identifier = OverlayWindowIdentifier.composer
        panel.delegate = self
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.level = .floating
        // The world and character hotspots exist on every Space, but the
        // focusable composer belongs on the Space where the click happened.
        // moveToActiveSpace prevents a previously hidden panel from remaining
        // attached to Desktop 1 when it is opened from Desktop 2.
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.isReleasedWhenClosed = false
        let hostingView = NSHostingView(
            rootView: OverlayComposerView(
                model: model,
                onSend: onSend,
                onInputActivity: { [weak self] in
                    self?.inputDidChange()
                    onInputActivity()
                },
                onTypingChanged: onTypingChanged,
                onCancel: onCancel
            )
        )
        hostingView.wantsLayer = true
        hostingView.layer?.isOpaque = false
        hostingView.layer?.backgroundColor = NSColor.clear.cgColor
        panel.contentView = hostingView
    }

    func setScreenFrame(_ visibleFrame: CGRect) {
        setFrame(OverlayComposerLayout.frame(in: visibleFrame))
    }

    func setFrame(_ frame: CGRect) {
        settingFrame = true
        panel.setFrame(frame, display: panel.isVisible)
        settingFrame = false
        onFrameChanged(panel.frame, false)
    }

    func windowDidMove(_ notification: Notification) {
        guard !settingFrame else { return }
        onFrameChanged(panel.frame, false)
    }

    @objc private func dragEnded(_ notification: Notification) {
        onFrameChanged(panel.frame, true)
    }

    var frame: CGRect { panel.frame }

    func setVisible(_ visible: Bool) {
        if visible {
            // Showing the composer must not steal focus from the current app.
            panel.orderFrontRegardless()
        } else {
            cancelPendingFocusLossDismiss()
            focusRequestID &+= 1
            isProgrammaticallyHiding = true
            panel.orderOut(nil)
            isProgrammaticallyHiding = false
        }
    }

    func windowDidResignKey(_ notification: Notification) {
        guard !isProgrammaticallyHiding, panel.isVisible else { return }
        onTypingEnded()
        scheduleFocusLossDismissCheck()
    }

    @objc private func applicationDidResignActive(_ notification: Notification) {
        guard hasPendingFocusLossDismiss, panel.isVisible, !panel.isKeyWindow else { return }
        scheduleFocusLossDismissCheck()
    }

    private func scheduleFocusLossDismissCheck() {
        hasPendingFocusLossDismiss = true
        focusLossScheduler.schedule(after: Self.focusLossDismissDelay) { [weak self] in
            guard let self, self.hasPendingFocusLossDismiss else { return }
            guard self.panel.isVisible else {
                self.cancelPendingFocusLossDismiss()
                return
            }
            guard !self.panel.isKeyWindow else {
                self.cancelPendingFocusLossDismiss()
                return
            }
            if self.isApplicationActive() {
                // The macOS character palette becomes the key window while SIDEY
                // remains active. Wait without polling until it inserts text,
                // returns focus, or the application deactivates.
                self.focusLossScheduler.cancel()
                return
            }
            self.hasPendingFocusLossDismiss = false
            self.onDismissRequested()
        }
    }

    func windowDidBecomeKey(_ notification: Notification) {
        guard hasPendingFocusLossDismiss else { return }
        cancelPendingFocusLossDismiss()
        requestMessageFieldFocus(activateApplication: false)
    }

    func focusMessageField() {
        cancelPendingFocusLossDismiss()
        requestMessageFieldFocus(activateApplication: true)
    }

    private func requestMessageFieldFocus(activateApplication: Bool) {
        if activateApplication {
            NSApplication.shared.activate(ignoringOtherApps: true)
        }
        panel.makeKeyAndOrderFront(nil)
        focusRequestID &+= 1
        let requestID = focusRequestID

        // Character clicks arrive from a non-activating hotspot panel. Defer
        // first-responder assignment until that mouse event has completed and
        // SwiftUI has attached the representable NSTextView to the view tree.
        DispatchQueue.main.async { [weak self] in
            self?.completeMessageFieldFocus(requestID: requestID, attemptsRemaining: 2)
        }
    }

    private func completeMessageFieldFocus(requestID: Int, attemptsRemaining: Int) {
        guard requestID == focusRequestID, panel.isVisible else { return }
        panel.contentView?.layoutSubtreeIfNeeded()
        if let field = panel.contentView?.firstDescendant(
            withIdentifier: NSUserInterfaceItemIdentifier("sidey.message-field")
        ) {
            panel.makeKeyAndOrderFront(nil)
            if panel.makeFirstResponder(field) { return }
        }

        guard attemptsRemaining > 0 else { return }
        DispatchQueue.main.async { [weak self] in
            self?.completeMessageFieldFocus(
                requestID: requestID,
                attemptsRemaining: attemptsRemaining - 1
            )
        }
    }

    private func inputDidChange() {
        let shouldRestoreFocus = hasPendingFocusLossDismiss || !panel.isKeyWindow
        guard shouldRestoreFocus else { return }
        cancelPendingFocusLossDismiss()
        requestMessageFieldFocus(activateApplication: true)
    }

    private func cancelPendingFocusLossDismiss() {
        focusLossScheduler.cancel()
        hasPendingFocusLossDismiss = false
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    var level: NSWindow.Level { panel.level }
    var isVisible: Bool { panel.isVisible }
    var size: CGSize { panel.frame.size }
    var ignoresMouseEvents: Bool { panel.ignoresMouseEvents }
    var isKeyWindow: Bool { panel.isKeyWindow }
    var usesTransparentSurface: Bool {
        guard
            let layer = panel.contentView?.layer,
            let layerBackground = layer.backgroundColor,
            let layerColor = NSColor(cgColor: layerBackground)
        else { return false }

        return !panel.isOpaque
            && panel.backgroundColor.alphaComponent < 0.001
            && !layer.isOpaque
            && layerColor.alphaComponent < 0.001
    }
    var messageFieldIsFirstResponder: Bool {
        (panel.firstResponder as? NSView)?.identifier
            == NSUserInterfaceItemIdentifier("sidey.message-field")
    }
    var messageTextView: NSTextView? {
        panel.contentView?.firstDescendant(
            withIdentifier: NSUserInterfaceItemIdentifier("sidey.message-field")
        ) as? NSTextView
    }
    var collectionBehavior: NSWindow.CollectionBehavior { panel.collectionBehavior }
}

@MainActor
final class CharacterHotspotWindowController {
    static let panelSize = CGSize(width: 52, height: 52)
    private let panel: NSPanel
    private var requestedVisible = false
    private var hasFrame = false

    init(
        onClick: @escaping (Int) -> Void,
        onRightClick: @escaping () -> Void = {},
        onDoubleRightClick: @escaping () -> Void = {}
    ) {
        panel = CharacterHotspotPanel(
            contentRect: CGRect(origin: .zero, size: Self.panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.identifier = OverlayWindowIdentifier.characterHotspot
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.ignoresMouseEvents = false
        panel.isReleasedWhenClosed = false
        panel.contentView = CharacterHotspotView(onClick: onClick, onRightClick: onRightClick, onDoubleRightClick: onDoubleRightClick)
    }

    func setFrame(_ frame: CGRect?) {
        guard let frame else {
            hasFrame = false
            (panel.contentView as? CharacterHotspotView)?.rightClicks.cancel()
            panel.orderOut(nil)
            return
        }
        hasFrame = true
        panel.setFrame(frame, display: false)
        if requestedVisible { panel.orderFrontRegardless() }
    }

    func setVisible(_ visible: Bool) {
        requestedVisible = visible
        if !visible { (panel.contentView as? CharacterHotspotView)?.rightClicks.cancel() }
        if visible, hasFrame {
            panel.orderFrontRegardless()
        } else {
            panel.orderOut(nil)
        }
    }

    var isVisible: Bool { panel.isVisible }
    var ignoresMouseEvents: Bool { panel.ignoresMouseEvents }
    var size: CGSize { panel.frame.size }
}

private extension NSView {
    func firstDescendant(withIdentifier identifier: NSUserInterfaceItemIdentifier) -> NSView? {
        if self.identifier == identifier { return self }
        for subview in subviews {
            if let match = subview.firstDescendant(withIdentifier: identifier) { return match }
        }
        return nil
    }
}
