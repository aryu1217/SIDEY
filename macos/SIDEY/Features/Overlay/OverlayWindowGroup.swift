import AppKit

private final class ScreenObserverToken: @unchecked Sendable {
    let value: NSObjectProtocol

    init(_ value: NSObjectProtocol) { self.value = value }

    deinit { NotificationCenter.default.removeObserver(value) }
}

struct OverlayScreenGeometry: Equatable, Sendable {
    let identifier: String
    let legacySignature: String
    let name: String
    let visibleFrame: CGRect
}

struct OverlayRegionFrames: Equatable, Sendable {
    let activityFrame: CGRect
    let renderFrame: CGRect

    var localActivityFrame: CGRect {
        activityFrame.offsetBy(dx: -renderFrame.minX, dy: -renderFrame.minY)
    }

    func screenFrame(forRenderLocalFrame frame: CGRect) -> CGRect {
        frame.offsetBy(dx: renderFrame.minX, dy: renderFrame.minY)
    }
}

@MainActor
protocol ComposerAutoDismissScheduling: AnyObject {
    func schedule(after delay: Duration, action: @escaping @MainActor () -> Void)
    func cancel()
}

@MainActor
private final class TaskComposerAutoDismissScheduler: ComposerAutoDismissScheduling {
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

enum OverlayRegionLayout {
    static let preferredActivityDepth: CGFloat = 240
    static let reactionRenderDepth: CGFloat = 360
    static let reactionTangentMargin: CGFloat = 144

    static func screen(
        for preference: OverlayRegionPreference,
        screens: [OverlayScreenGeometry]
    ) -> OverlayScreenGeometry? {
        guard !screens.isEmpty else { return nil }
        guard let identifier = preference.screenIdentifier else { return screens.first }
        return screens.first(where: {
            $0.identifier == identifier || $0.legacySignature == identifier
        }) ?? screens.first
    }

    static func activityFrame(
        for preference: OverlayRegionPreference,
        on screen: OverlayScreenGeometry
    ) -> CGRect {
        let visible = screen.visibleFrame
        let depth = min(
            preferredActivityDepth,
            visible.height / 3,
            preference.edge.isHorizontal ? visible.height : visible.width
        )
        if preference.edge.isHorizontal {
            let length = visible.width * preference.span.fraction
            return CGRect(
                x: visible.midX - length / 2,
                y: preference.edge == .bottom ? visible.minY : visible.maxY - depth,
                width: length,
                height: depth
            )
        }

        let length = visible.height * preference.span.fraction
        return CGRect(
            x: preference.edge == .left ? visible.minX : visible.maxX - depth,
            y: visible.midY - length / 2,
            width: depth,
            height: length
        )
    }

    static func renderFrame(
        for activityFrame: CGRect,
        edge: OverlayEdge,
        in visibleFrame: CGRect
    ) -> CGRect {
        if edge.isHorizontal {
            let minimumX = max(visibleFrame.minX, activityFrame.minX - reactionTangentMargin)
            let maximumX = min(visibleFrame.maxX, activityFrame.maxX + reactionTangentMargin)
            let depth = min(reactionRenderDepth, visibleFrame.height)
            return CGRect(
                x: minimumX,
                y: edge == .bottom ? visibleFrame.minY : visibleFrame.maxY - depth,
                width: max(0, maximumX - minimumX),
                height: depth
            )
        }

        let minimumY = max(visibleFrame.minY, activityFrame.minY - reactionTangentMargin)
        let maximumY = min(visibleFrame.maxY, activityFrame.maxY + reactionTangentMargin)
        let depth = min(reactionRenderDepth, visibleFrame.width)
        return CGRect(
            x: edge == .left ? visibleFrame.minX : visibleFrame.maxX - depth,
            y: minimumY,
            width: depth,
            height: max(0, maximumY - minimumY)
        )
    }

    static func frames(
        for preference: OverlayRegionPreference,
        on screen: OverlayScreenGeometry
    ) -> OverlayRegionFrames {
        let activity = activityFrame(for: preference, on: screen)
        return OverlayRegionFrames(
            activityFrame: activity,
            renderFrame: renderFrame(
                for: activity,
                edge: preference.edge,
                in: screen.visibleFrame
            )
        )
    }

    /// Compatibility surface for callers that only need the movement/activity area.
    static func frame(
        for preference: OverlayRegionPreference,
        on screen: OverlayScreenGeometry
    ) -> CGRect {
        activityFrame(for: preference, on: screen)
    }
}

@MainActor
final class OverlayWindowGroup {
    static let defaultComposerAutoDismissDelay: Duration = .seconds(5)

    private let model: AppModel
    private let onSend: (String) -> Void
    private let onTypingChanged: (Bool) -> Void
    private let onCharacterDoubleClick: () -> Void
    private let onTargetCharacterClick: (UUID) -> Void
    private let onDrawIconClick: (UUID) -> Void
    private let onRegionChanged: () -> Void
    private let onTreeMovementToggle: () -> Void
    private lazy var worldWindow = PixelWorldWindowController(
        model: model,
        frame: .zero,
        onCharacterFramesChanged: { [weak self] frames in self?.characterFramesChanged(frames) },
        onChatBubbleFramesChanged: { [weak self] frames in self?.chatBubbleFramesChanged(frames) }
    )
    private lazy var interactionWindow = OverlayInteractionWindowController(
        model: model,
        onSend: { [weak self] body in self?.submitComposerMessage(body) },
        onInputActivity: { [weak self] in self?.composerDidReceiveInput() },
        onTypingChanged: { [weak self] active in self?.composerTypingChanged(active) },
        onCancel: { [weak self] in self?.dismissComposer() },
        onFrameChanged: { [weak self] frame, finished in self?.composerFrameChanged(frame, finished: finished) }
    )
    private lazy var hotspotWindow = CharacterHotspotWindowController(
        onClick: { [weak self] clickCount in self?.handleCharacterClick(clickCount: clickCount) },
        onRightClick: { [weak self] in
            guard let self, self.model.selectedCharacterID == PixelCharacterCatalog.pixelTreeID else { return }
            self.onTreeMovementToggle()
        },
        onDoubleRightClick: { [weak self] in self?.activateThrowTargeting() }
    )
    private var targetHotspotWindows: [UUID: CharacterHotspotWindowController] = [:]
    private var drawIconWindows: [UUID: DrawIconBubbleController] = [:]
    private var screenObserver: ScreenObserverToken?
    private(set) var activityFrame: CGRect = .zero
    private(set) var renderFrame: CGRect = .zero
    var currentFrame: CGRect { activityFrame }
    private(set) var currentScreenIdentifier: String?
    private var overlayVisible = false
    private(set) var composerVisible = false
    private var currentUserLocalFrame: CGRect?
    private var characterLocalFrames: [UUID: CGRect] = [:]
    private var chatBubbleLocalFrames: [UUID: CGRect] = [:]
    private var composerVisibilityBeforeCharacterClick: Bool?
    private var throwTargetingTask: Task<Void, Never>?
    private var throwTargetingActive = false
    private let composerAutoDismissDelay: Duration
    private let composerAutoDismissScheduler: any ComposerAutoDismissScheduling

    init(
        model: AppModel,
        onSend: @escaping (String) -> Void = { _ in },
        onTypingChanged: @escaping (Bool) -> Void = { _ in },
        onCharacterDoubleClick: @escaping () -> Void = {},
        onTargetCharacterClick: @escaping (UUID) -> Void = { _ in },
        onDrawIconClick: @escaping (UUID) -> Void = { _ in },
        onRegionChanged: @escaping () -> Void = {},
        onTreeMovementToggle: @escaping () -> Void = {},
        composerAutoDismissDelay: Duration = OverlayWindowGroup.defaultComposerAutoDismissDelay,
        composerAutoDismissScheduler: (any ComposerAutoDismissScheduling)? = nil
    ) {
        self.model = model
        self.onSend = onSend
        self.onTypingChanged = onTypingChanged
        self.onCharacterDoubleClick = onCharacterDoubleClick
        self.onTargetCharacterClick = onTargetCharacterClick
        self.onDrawIconClick = onDrawIconClick
        self.onRegionChanged = onRegionChanged
        self.onTreeMovementToggle = onTreeMovementToggle
        self.composerAutoDismissDelay = composerAutoDismissDelay
        self.composerAutoDismissScheduler = composerAutoDismissScheduler ?? TaskComposerAutoDismissScheduler()
        screenObserver = ScreenObserverToken(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.screensDidChange() }
        })
        refreshAvailableScreens()
    }

    func restore(preference: OverlayRegionPreference) {
        apply(preference: preference, persistFallback: true)
    }

    func setRegionPreference(_ preference: OverlayRegionPreference) {
        model.preferences.overlayRegion = preference
        apply(preference: preference, persistFallback: true)
    }

    func setVisible(_ visible: Bool) {
        if !visible {
            composerVisibilityBeforeCharacterClick = nil
            model.characterImpactAudio.stopAll()
        }
        overlayVisible = visible
        if visible {
            apply(preference: model.preferences.overlayRegion, persistFallback: true)
            worldWindow.orderFront()
            hotspotWindow.setVisible(true)
            refreshThrowHotspots()
            refreshDrawIcons()
        } else {
            dismissComposer()
            hotspotWindow.setVisible(false)
            resetThrowTargeting()
            worldWindow.orderOut()
            drawIconWindows.values.forEach { $0.hide() }
        }
    }

    func focusMessageField() {
        presentComposer()
    }

    func presentComposer() {
        composerVisibilityBeforeCharacterClick = nil
        guard overlayVisible, model.activeRoom != nil else { return }
        cancelComposerAutoDismiss()
        composerVisible = true
        worldWindow.setComposerVisible(true)
        interactionWindow.setVisible(true)
        interactionWindow.focusMessageField()
    }

    func dismissComposer() {
        dismissComposer(sendTypingStop: true)
    }

    func toggleComposer() {
        composerVisible ? dismissComposer() : presentComposer()
    }

    func handleCharacterClick(clickCount: Int) {
        switch clickCount {
        case 1:
            let previousVisibility = composerVisible
            toggleComposer()
            composerVisibilityBeforeCharacterClick = composerVisible == previousVisibility
                ? nil
                : previousVisibility
        case 2:
            let previousVisibility = composerVisibilityBeforeCharacterClick
            composerVisibilityBeforeCharacterClick = nil
            if let previousVisibility, composerVisible != previousVisibility {
                if previousVisibility {
                    presentComposer()
                } else {
                    dismissComposer()
                }
            }
            onCharacterDoubleClick()
        default:
            composerVisibilityBeforeCharacterClick = nil
        }
    }

    func playCharacterPulse(_ event: CharacterPulseEvent) {
        worldWindow.playCharacterPulse(event)
    }

    func playCharacterThrow(_ event: CharacterThrowEvent) {
        worldWindow.playCharacterThrow(event)
    }

    func throwInteractionPreferenceChanged() {
        resetThrowTargeting(clearOnly: !model.preferences.requiresRightClickToThrow)
        refreshThrowHotspots()
    }

    func invalidateThrowInteraction() {
        resetThrowTargeting()
        refreshThrowHotspots()
    }

    func refreshThrowHotspots() {
        let canTarget = overlayVisible
            && model.activeRoomRealtimeAvailable
            && model.activeRoom != nil
            && currentUserLocalFrame != nil
            && (!model.preferences.requiresRightClickToThrow || throwTargetingActive)
        let eligibleIDs: Set<UUID> = canTarget ? Set(model.pixelWorldMembers.compactMap { member -> UUID? in
            guard CharacterThrowTargetPolicy.canTarget(member) else { return nil }
            return member.id
        }) : []

        for id in targetHotspotWindows.keys where !eligibleIDs.contains(id) {
            targetHotspotWindows.removeValue(forKey: id)?.setVisible(false)
        }
        for id in eligibleIDs.prefix(11) {
            let controller = targetHotspotWindows[id] ?? CharacterHotspotWindowController(
                onClick: { [weak self] clickCount in
                    guard clickCount == 1 else { return }
                    self?.onTargetCharacterClick(id)
                }
            )
            targetHotspotWindows[id] = controller
            controller.setFrame(screenFrame(for: characterLocalFrames[id]))
            controller.setVisible(true)
        }
    }

    func refreshDrawIcons() {
        let grouped = Dictionary(grouping: model.visibleDrawEvents, by: \.creatorID)
        for id in drawIconWindows.keys where grouped[id] == nil {
            drawIconWindows.removeValue(forKey: id)?.hide()
        }
        let visibleScreen = screenGeometries.first(where: {
            $0.identifier == currentScreenIdentifier
        })?.visibleFrame
        for (creatorID, events) in grouped {
            let controller = drawIconWindows[creatorID] ?? DrawIconBubbleController(
                events: events,
                onOpen: onDrawIconClick
            )
            drawIconWindows[creatorID] = controller
            controller.update(
                events: events,
                characterFrame: screenFrame(for: characterLocalFrames[creatorID]),
                chatBubbleFrame: screenFrame(for: chatBubbleLocalFrames[creatorID]),
                visibleFrame: visibleScreen,
                visible: overlayVisible
            )
        }
    }

    func submitComposerMessage(_ body: String) {
        guard composerVisible else { return }
        scheduleComposerAutoDismiss()
        onSend(body)
    }

    func composerTypingChanged(_ active: Bool) {
        if active { cancelComposerAutoDismiss() }
        onTypingChanged(active)
    }

    func composerDidReceiveInput() {
        cancelComposerAutoDismiss()
    }

    var worldLevel: NSWindow.Level { worldWindow.level }
    var interactionLevel: NSWindow.Level { interactionWindow.level }
    var worldIsVisible: Bool { worldWindow.isVisible }
    var interactionIsVisible: Bool { interactionWindow.isVisible }
    var hotspotIsVisible: Bool { hotspotWindow.isVisible }
    var hotspotIgnoresMouseEvents: Bool { hotspotWindow.ignoresMouseEvents }
    var hotspotSize: CGSize { hotspotWindow.size }
    var interactionSize: CGSize { interactionWindow.size }
    var worldSize: CGSize { worldWindow.size }
    var worldCanHide: Bool { worldWindow.canHide }
    var worldIgnoresMouseEvents: Bool { worldWindow.ignoresMouseEvents }
    var interactionIgnoresMouseEvents: Bool { interactionWindow.ignoresMouseEvents }
    var interactionIsKeyWindow: Bool { interactionWindow.isKeyWindow }
    var worldIsRendering: Bool { worldWindow.isRendering }
    var worldCollectionBehavior: NSWindow.CollectionBehavior { worldWindow.collectionBehavior }
    var interactionCollectionBehavior: NSWindow.CollectionBehavior { interactionWindow.collectionBehavior }

    private func apply(preference: OverlayRegionPreference, persistFallback: Bool) {
        let screens = screenGeometries
        guard let screen = OverlayRegionLayout.screen(for: preference, screens: screens) else { return }
        var resolved = preference
        let requestedScreenExists = preference.screenIdentifier == nil || screens.contains(where: {
            $0.identifier == preference.screenIdentifier || $0.legacySignature == preference.screenIdentifier
        })
        resolved.screenIdentifier = screen.identifier
        let frames = OverlayRegionLayout.frames(for: resolved, on: screen)
        let geometryChanged = currentScreenIdentifier != screen.identifier
            || activityFrame != frames.activityFrame || renderFrame != frames.renderFrame
        currentScreenIdentifier = screen.identifier
        activityFrame = frames.activityFrame
        renderFrame = frames.renderFrame
        if geometryChanged {
            // Reapplying visibility after a snapshot must not briefly erase
            // valid anchors and hide every draw bubble until the next frame.
            currentUserLocalFrame = nil
            characterLocalFrames.removeAll()
            chatBubbleLocalFrames.removeAll()
            hotspotWindow.setFrame(nil)
            resetThrowTargeting()
        }
        worldWindow.setLayout(
            renderFrame: frames.renderFrame,
            localActivityFrame: frames.localActivityFrame
        )
        interactionWindow.setFrame(ComposerPositionLayout.frame(
            preference: model.preferences.composerPosition, screens: screens, fallback: screen
        ))
        positionHotspot()
        refreshDrawIcons()

        if persistFallback,
           (!requestedScreenExists || model.preferences.overlayRegion != resolved) {
            model.preferences.overlayRegion = resolved
            onRegionChanged()
        }
    }

    private func composerFrameChanged(_ frame: CGRect, finished: Bool) {
        worldWindow.setComposerFrame(frame.offsetBy(dx: -renderFrame.minX, dy: -renderFrame.minY))
        guard finished, let screen = ComposerPositionLayout.screen(for: frame, screens: screenGeometries) else { return }
        let clamped = ComposerPositionLayout.clamp(frame, to: screen.visibleFrame)
        model.preferences.composerPosition = ComposerPositionLayout.preference(frame: clamped, screen: screen)
        interactionWindow.setFrame(clamped)
        onRegionChanged()
    }

    private func screensDidChange() {
        refreshAvailableScreens()
        apply(preference: model.preferences.overlayRegion, persistFallback: true)
    }

    private func refreshAvailableScreens() {
        model.availableScreens = screenGeometries.map {
            OverlayScreenOption(id: $0.identifier, name: $0.name)
        }
    }

    private func dismissComposer(sendTypingStop: Bool) {
        composerVisibilityBeforeCharacterClick = nil
        cancelComposerAutoDismiss()
        guard composerVisible || interactionWindow.isVisible else { return }
        composerVisible = false
        interactionWindow.setVisible(false)
        worldWindow.setComposerVisible(false)
        if sendTypingStop { onTypingChanged(false) }
    }

    private func scheduleComposerAutoDismiss() {
        composerAutoDismissScheduler.schedule(after: composerAutoDismissDelay) { [weak self] in
            self?.dismissComposer(sendTypingStop: true)
        }
    }

    private func cancelComposerAutoDismiss() {
        composerAutoDismissScheduler.cancel()
    }

    private func characterFramesChanged(_ frames: [UUID: CGRect]) {
        characterLocalFrames = frames
        currentUserLocalFrame = model.currentUserID.flatMap { frames[$0] }
        let localFrame = currentUserLocalFrame
        guard localFrame != nil else {
            hotspotWindow.setFrame(nil)
            dismissComposer()
            resetThrowTargeting()
            refreshDrawIcons()
            return
        }
        positionHotspot()
        refreshThrowHotspots()
        refreshDrawIcons()
    }

    private func chatBubbleFramesChanged(_ frames: [UUID: CGRect]) {
        chatBubbleLocalFrames = frames
        refreshDrawIcons()
    }

    private func positionHotspot() {
        guard let localFrame = currentUserLocalFrame else { return }
        hotspotWindow.setFrame(screenFrame(for: localFrame))
    }

    private func screenFrame(for localFrame: CGRect?) -> CGRect? {
        guard let localFrame else { return nil }
        return OverlayRegionFrames(
            activityFrame: activityFrame,
            renderFrame: renderFrame
        ).screenFrame(forRenderLocalFrame: localFrame)
    }

    private func activateThrowTargeting() {
        guard model.preferences.requiresRightClickToThrow,
              overlayVisible,
              model.activeRoomRealtimeAvailable,
              currentUserLocalFrame != nil
        else { return }
        throwTargetingActive = true
        refreshThrowHotspots()
        throwTargetingTask?.cancel()
        throwTargetingTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(for: .seconds(10)) } catch { return }
            guard let self else { return }
            self.throwTargetingActive = false
            self.refreshThrowHotspots()
        }
    }

    private func resetThrowTargeting(clearOnly: Bool = false) {
        throwTargetingTask?.cancel()
        throwTargetingTask = nil
        throwTargetingActive = false
        if !clearOnly {
            targetHotspotWindows.values.forEach { $0.setVisible(false) }
            targetHotspotWindows.removeAll()
        }
    }

    private var screenGeometries: [OverlayScreenGeometry] {
        let screens = NSScreen.screens
        guard let main = NSScreen.main else { return screens.map(\.sideyOverlayGeometry) }
        return ([main] + screens.filter { $0 !== main }).map(\.sideyOverlayGeometry)
    }
}

extension NSScreen {
    var sideyOverlayGeometry: OverlayScreenGeometry {
        let screenNumber = (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
            .map { $0.uint32Value }
        let identifier = screenNumber.map { "display:\($0)" }
            ?? "display:\(localizedName):\(Int(frame.width))x\(Int(frame.height))"
        let pixelWidth = Int((frame.width * backingScaleFactor).rounded())
        let pixelHeight = Int((frame.height * backingScaleFactor).rounded())
        let legacySignature = String(
            format: "%dx%d@%.3f",
            pixelWidth,
            pixelHeight,
            backingScaleFactor
        )
        return OverlayScreenGeometry(
            identifier: identifier,
            legacySignature: legacySignature,
            name: localizedName,
            visibleFrame: visibleFrame
        )
    }
}
