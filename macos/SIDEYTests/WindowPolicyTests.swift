import AppKit
import SpriteKit
import XCTest
@testable import SIDEY

@MainActor
final class WindowPolicyTests: XCTestCase {
    private func waitForWindowState(_ ready: @escaping @MainActor () -> Bool) async {
        let condition = expectation(
            for: NSPredicate { _, _ in MainActor.assumeIsolated { ready() } },
            evaluatedWith: nil
        )
        await fulfillment(of: [condition], timeout: 2)
    }

    func testRightClickSingleWaitsForDoubleClickWindow() async {
        let single = expectation(description: "single right click")
        var doubles = 0
        let clicks = CharacterRightClickCoordinator(interval: 0.02,
            onSingle: { single.fulfill() }, onDouble: { doubles += 1 })
        clicks.handle(clickCount: 1)
        XCTAssertEqual(doubles, 0)
        await fulfillment(of: [single], timeout: 1)
        XCTAssertEqual(doubles, 0)
    }

    func testRightClickDoubleCancelsSingleAction() async throws {
        var singles = 0
        var doubles = 0
        let clicks = CharacterRightClickCoordinator(interval: 0.02,
            onSingle: { singles += 1 }, onDouble: { doubles += 1 })
        clicks.handle(clickCount: 1)
        clicks.handle(clickCount: 2)
        XCTAssertEqual(doubles, 1)
        try await Task.sleep(for: .milliseconds(60))
        XCTAssertEqual(singles, 0)
        XCTAssertEqual(doubles, 1)
        clicks.handle(clickCount: 3)
        XCTAssertEqual(doubles, 1)
    }

    func testRightClickCancellationPreventsHiddenHotspotAction() async throws {
        var singles = 0
        let clicks = CharacterRightClickCoordinator(interval: 0.02,
            onSingle: { singles += 1 }, onDouble: {})
        clicks.handle(clickCount: 1)
        clicks.cancel()
        try await Task.sleep(for: .milliseconds(60))
        XCTAssertEqual(singles, 0)
    }

    func testPixelWorldRendererUsesThirtyFPSNearestFriendlyContract() {
        let view = SKView()
        PixelWorldRendererPolicy.apply(to: view)

        XCTAssertEqual(view.preferredFramesPerSecond, 30)
        XCTAssertTrue(view.allowsTransparency)
        XCTAssertTrue(view.ignoresSiblingOrder)
        XCTAssertTrue(view.shouldCullNonVisibleNodes)
        XCTAssertTrue(view.isAsynchronous)
    }

    func testAllTwelveRegionPresetsAreCenteredInsideVisibleFrame() {
        let screen = OverlayScreenGeometry(
            identifier: "display:test",
            legacySignature: "2400x1600@2.000",
            name: "테스트",
            visibleFrame: CGRect(x: 100, y: 40, width: 1200, height: 800)
        )

        for edge in OverlayEdge.allCases {
            for span in OverlaySpan.allCases {
                let frame = OverlayRegionLayout.frame(
                    for: OverlayRegionPreference(edge: edge, span: span, screenIdentifier: screen.identifier),
                    on: screen
                )
                XCTAssertTrue(screen.visibleFrame.contains(frame), "\(edge) \(span)")
                if edge.isHorizontal {
                    XCTAssertEqual(frame.midX, screen.visibleFrame.midX, accuracy: 0.001)
                    XCTAssertEqual(frame.width, screen.visibleFrame.width * span.fraction, accuracy: 0.001)
                    XCTAssertEqual(frame.height, 240, accuracy: 0.001)
                } else {
                    XCTAssertEqual(frame.midY, screen.visibleFrame.midY, accuracy: 0.001)
                    XCTAssertEqual(frame.height, screen.visibleFrame.height * span.fraction, accuracy: 0.001)
                    XCTAssertEqual(frame.width, 240, accuracy: 0.001)
                }
            }
        }
    }

    func testAllTwelvePresetsKeepActivityTrackWhileExpandingAClampedRenderFrame() {
        let screen = OverlayScreenGeometry(
            identifier: "display:test",
            legacySignature: "2400x1800@2.000",
            name: "테스트",
            visibleFrame: CGRect(x: 100, y: 40, width: 1200, height: 900)
        )

        for edge in OverlayEdge.allCases {
            for span in OverlaySpan.allCases {
                let frames = OverlayRegionLayout.frames(
                    for: OverlayRegionPreference(
                        edge: edge,
                        span: span,
                        screenIdentifier: screen.identifier
                    ),
                    on: screen
                )
                XCTAssertTrue(screen.visibleFrame.contains(frames.activityFrame), "activity \(edge) \(span)")
                XCTAssertTrue(screen.visibleFrame.contains(frames.renderFrame), "render \(edge) \(span)")
                XCTAssertTrue(frames.renderFrame.contains(frames.activityFrame), "contain \(edge) \(span)")
                XCTAssertEqual(
                    edge.isHorizontal ? frames.renderFrame.height : frames.renderFrame.width,
                    OverlayRegionLayout.reactionRenderDepth,
                    accuracy: 0.001
                )

                let local = frames.localActivityFrame
                XCTAssertEqual(local.size, frames.activityFrame.size)
                XCTAssertTrue(CGRect(origin: .zero, size: frames.renderFrame.size).contains(local))
                if span != .full {
                    let leadingMargin = edge.isHorizontal ? local.minX : local.minY
                    let trailingMargin = edge.isHorizontal
                        ? frames.renderFrame.width - local.maxX
                        : frames.renderFrame.height - local.maxY
                    XCTAssertEqual(
                        leadingMargin,
                        OverlayRegionLayout.reactionTangentMargin,
                        accuracy: 0.001
                    )
                    XCTAssertEqual(
                        trailingMargin,
                        OverlayRegionLayout.reactionTangentMargin,
                        accuracy: 0.001
                    )
                }
            }
        }
    }

    func testRenderLocalHotspotConvertsThroughRenderFrameOrigin() {
        let frames = OverlayRegionFrames(
            activityFrame: CGRect(x: 400, y: 40, width: 400, height: 240),
            renderFrame: CGRect(x: 256, y: 40, width: 688, height: 360)
        )
        let localHotspot = CGRect(x: 300, y: 18, width: 52, height: 52)

        XCTAssertEqual(
            frames.screenFrame(forRenderLocalFrame: localHotspot),
            CGRect(x: 556, y: 58, width: 52, height: 52)
        )
    }

    func testSmallScreenCapsRegionDepthAtOneThirdOfVerticalLength() {
        let screen = OverlayScreenGeometry(
            identifier: "display:small",
            legacySignature: "600x300@1.000",
            name: "작은 화면",
            visibleFrame: CGRect(x: 0, y: 0, width: 600, height: 300)
        )

        for edge in OverlayEdge.allCases {
            let frame = OverlayRegionLayout.frame(
                for: OverlayRegionPreference(edge: edge, span: .full, screenIdentifier: nil),
                on: screen
            )
            let depth = edge.isHorizontal ? frame.height : frame.width
            XCTAssertEqual(depth, 100, accuracy: 0.001)
        }
    }

    func testComposerIsCenteredBelowTheSelectedScreensNotchArea() {
        let visibleFrame = CGRect(x: 100, y: 40, width: 1200, height: 800)

        let frame = OverlayComposerLayout.frame(in: visibleFrame)

        XCTAssertEqual(frame, CGRect(x: 500, y: 774, width: 400, height: 56))
        XCTAssertEqual(frame.midX, visibleFrame.midX, accuracy: 0.001)
        XCTAssertEqual(visibleFrame.maxY - frame.maxY, 10, accuracy: 0.001)
        XCTAssertTrue(visibleFrame.contains(frame))
    }

    func testMissingMonitorFallsBackToPrimaryScreen() throws {
        let primary = OverlayScreenGeometry(
            identifier: "display:primary",
            legacySignature: "1920x1080@1.000",
            name: "주 화면",
            visibleFrame: CGRect(x: 0, y: 0, width: 1920, height: 1040)
        )
        let secondary = OverlayScreenGeometry(
            identifier: "display:secondary",
            legacySignature: "2560x1440@2.000",
            name: "보조 화면",
            visibleFrame: CGRect(x: 1920, y: 0, width: 1280, height: 720)
        )
        let preference = OverlayRegionPreference(
            edge: .right,
            span: .half,
            screenIdentifier: "display:removed"
        )

        XCTAssertEqual(
            OverlayRegionLayout.screen(for: preference, screens: [primary, secondary])?.identifier,
            primary.identifier
        )
    }

    func testLegacyMonitorSignatureStillResolves() {
        let screen = OverlayScreenGeometry(
            identifier: "display:secondary",
            legacySignature: "2560x1440@2.000",
            name: "보조 화면",
            visibleFrame: CGRect(x: 0, y: 0, width: 1280, height: 720)
        )
        let preference = OverlayRegionPreference(
            edge: .bottom,
            span: .full,
            screenIdentifier: "2560x1440@2.000"
        )

        XCTAssertEqual(OverlayRegionLayout.screen(for: preference, screens: [screen]), screen)
    }

    func testPresentationRotationPointsFeetAtSelectedEdge() {
        XCTAssertEqual(OverlayEdge.bottom.presentationRotation, 0, accuracy: 0.001)
        XCTAssertEqual(OverlayEdge.left.presentationRotation, -.pi / 2, accuracy: 0.001)
        XCTAssertEqual(OverlayEdge.right.presentationRotation, .pi / 2, accuracy: 0.001)
        XCTAssertEqual(abs(OverlayEdge.top.presentationRotation), .pi, accuracy: 0.001)
        for edge in OverlayEdge.allCases {
            XCTAssertEqual(edge.presentationRotation + edge.readableContentCounterRotation, 0, accuracy: 0.001)
            XCTAssertEqual(edge.presentationRotation + edge.bubbleCounterRotation, 0, accuracy: 0.001)
        }
    }

    func testOnboardingWindowTransitionsToFullSettingsOnGroupPage() {
        var preferences = AppPreferences.defaults
        preferences.onboardingComplete = false
        let model = AppModel(preferences: preferences)
        let settings = SettingsWindowController(model: model)
        XCTAssertEqual(settings.contentSize, SettingsWindowController.onboardingContentSize)

        model.preferences.onboardingComplete = true
        model.activeSettingsPage = .groups
        settings.transitionFromOnboardingToSettings()

        XCTAssertEqual(model.activeSettingsPage, .groups)
        let actualContentSize = settings.contentSize
        let requestedContentSize = SettingsWindowController.settingsContentSize
        XCTAssertLessThanOrEqual(actualContentSize.width, requestedContentSize.width)
        XCTAssertLessThanOrEqual(actualContentSize.height, requestedContentSize.height)
        XCTAssertGreaterThanOrEqual(actualContentSize.width, 860)
        XCTAssertGreaterThanOrEqual(actualContentSize.height, 640)

        if let window = settings.window,
           let screen = window.screen {
            let requestedFrame = window.frameRect(
                forContentRect: CGRect(origin: .zero, size: requestedContentSize)
            )
            if screen.visibleFrame.width >= requestedFrame.width,
               screen.visibleFrame.height >= requestedFrame.height {
                XCTAssertEqual(actualContentSize, requestedContentSize)
            }
        }

        model.preferences.onboardingComplete = false
        settings.transitionFromSettingsToOnboarding()

        let onboardingSize = settings.contentSize
        XCTAssertLessThanOrEqual(onboardingSize.width, SettingsWindowController.onboardingContentSize.width)
        XCTAssertLessThanOrEqual(onboardingSize.height, SettingsWindowController.onboardingContentSize.height)
        XCTAssertGreaterThanOrEqual(onboardingSize.width, 860)
        XCTAssertGreaterThanOrEqual(onboardingSize.height, 640)
    }

    func testSettingsWindowMovesToTheActiveSpaceWhenPresented() {
        let settings = SettingsWindowController(model: AppModel(preferences: .defaults))

        XCTAssertTrue(settings.window?.collectionBehavior.contains(.moveToActiveSpace) ?? false)
        XCTAssertFalse(settings.window?.collectionBehavior.contains(.canJoinAllSpaces) ?? true)
    }

    func testSettingsCloseFinishesBeforeLifecycleCallback() async {
        let closed = expectation(description: "settings close lifecycle")
        var callbackRan = false
        let settings = SettingsWindowController(
            model: AppModel(preferences: .defaults),
            onClose: {
                callbackRan = true
                closed.fulfill()
            }
        )
        settings.show()

        settings.window?.close()

        XCTAssertFalse(callbackRan)
        XCTAssertFalse(settings.window?.isVisible ?? true)
        await fulfillment(of: [closed], timeout: 1)
        XCTAssertTrue(callbackRan)
    }

    func testReopenedSettingsIgnoresStaleCloseLifecycleCallback() async {
        var closeCallbacks = 0
        let settings = SettingsWindowController(
            model: AppModel(preferences: .defaults),
            onClose: { closeCallbacks += 1 }
        )
        settings.show()

        settings.window?.close()
        settings.show()

        let queueDrained = expectation(description: "main queue drained")
        DispatchQueue.main.async { queueDrained.fulfill() }
        await fulfillment(of: [queueDrained], timeout: 1)
        XCTAssertEqual(closeCallbacks, 0)
        XCTAssertTrue(settings.window?.isVisible ?? false)

        settings.window?.close()
        let cleanupDrained = expectation(description: "settings cleanup drained")
        DispatchQueue.main.async { cleanupDrained.fulfill() }
        await fulfillment(of: [cleanupDrained], timeout: 1)
        XCTAssertEqual(closeCallbacks, 1)
        XCTAssertFalse(settings.window?.isVisible ?? true)
    }

    func testWindowLevelsClickPolicyAndFixedComposerSize() {
        let model = AppModel(preferences: .defaults)
        let userID = UUID()
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID,
            name: "테스트",
            ownerID: userID,
            members: [RoomMember(
                userID: userID,
                nickname: "나",
                characterID: "pixel_hamster",
                presence: .online
            )],
            inviteCodeHint: "TEST",
            inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        let settings = SettingsWindowController(model: model)
        let group = OverlayWindowGroup(model: model)
        group.setVisible(true)

        XCTAssertEqual(settings.window?.level, .normal)
        XCTAssertTrue(settings.window?.styleMask.contains(.fullSizeContentView) ?? false)
        XCTAssertFalse(settings.window?.titlebarAppearsTransparent ?? true)
        XCTAssertEqual(settings.window?.titlebarSeparatorStyle, .automatic)
        XCTAssertEqual(group.worldLevel, .floating)
        XCTAssertEqual(group.interactionLevel, .floating)
        XCTAssertFalse(group.worldCanHide)
        XCTAssertTrue(group.worldIgnoresMouseEvents)
        XCTAssertFalse(group.interactionIgnoresMouseEvents)
        XCTAssertFalse(group.interactionIsVisible)
        XCTAssertFalse(group.interactionIsKeyWindow)
        XCTAssertEqual(group.interactionSize, CGSize(width: 400, height: 56))
        XCTAssertEqual(group.worldSize, group.renderFrame.size)
        XCTAssertTrue(group.renderFrame.contains(group.activityFrame))
        XCTAssertTrue(group.worldIsRendering)
        XCTAssertTrue(group.worldCollectionBehavior.contains(.canJoinAllSpaces))
        XCTAssertTrue(group.worldCollectionBehavior.contains(.fullScreenAuxiliary))
        XCTAssertTrue(group.interactionCollectionBehavior.contains(.moveToActiveSpace))
        XCTAssertFalse(group.interactionCollectionBehavior.contains(.canJoinAllSpaces))
        XCTAssertTrue(settings.window?.childWindows?.isEmpty ?? false)

        group.presentComposer()
        XCTAssertTrue(group.composerVisible)
        XCTAssertTrue(group.interactionIsVisible)
        group.dismissComposer()
        XCTAssertFalse(group.composerVisible)
        XCTAssertFalse(group.interactionIsVisible)
    }

    func testCharacterHotspotIsTheOnlyPointerReceivingOverlayPanel() {
        let controller = CharacterHotspotWindowController(onClick: { _ in })
        controller.setFrame(CGRect(x: 100, y: 100, width: 52, height: 52))
        controller.setVisible(true)

        XCTAssertTrue(controller.isVisible)
        XCTAssertFalse(controller.ignoresMouseEvents)
        XCTAssertEqual(controller.size, CGSize(width: 52, height: 52))

        controller.setVisible(false)
        XCTAssertFalse(controller.isVisible)
    }

    func testCharacterHotspotDoubleClickPreservesComposerVisibilityAndRequestsPulse() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID, name: "테스트", ownerID: UUID(), members: [],
            inviteCodeHint: "TEST", inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        var pulseRequests = 0
        let group = OverlayWindowGroup(
            model: model,
            onCharacterDoubleClick: { pulseRequests += 1 }
        )
        group.setVisible(true)

        group.handleCharacterClick(clickCount: 1)
        XCTAssertTrue(group.composerVisible, "The first click responds immediately")
        group.handleCharacterClick(clickCount: 2)

        XCTAssertEqual(pulseRequests, 1)
        XCTAssertFalse(group.composerVisible)
        group.presentComposer()
        group.handleCharacterClick(clickCount: 1)
        XCTAssertFalse(group.composerVisible, "The first click responds immediately")
        group.handleCharacterClick(clickCount: 2)
        XCTAssertEqual(pulseRequests, 2)
        XCTAssertTrue(group.composerVisible)
        group.setVisible(false)
    }

    func testCharacterSingleClickTogglesComposerImmediately() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(id: roomID, name: "테스트", ownerID: UUID(), members: [], inviteCodeHint: "TEST")]
        model.preferences.activeRoomID = roomID
        let group = OverlayWindowGroup(model: model)
        group.setVisible(true)

        group.handleCharacterClick(clickCount: 1)
        XCTAssertTrue(group.composerVisible)
        group.handleCharacterClick(clickCount: 1)
        XCTAssertFalse(group.composerVisible)
        group.setVisible(false)
    }

    func testExplicitComposerDismissPreventsStaleDoubleClickRestore() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(id: roomID, name: "테스트", ownerID: UUID(), members: [], inviteCodeHint: "TEST")]
        model.preferences.activeRoomID = roomID
        var pulseRequests = 0
        let group = OverlayWindowGroup(
            model: model,
            onCharacterDoubleClick: { pulseRequests += 1 }
        )
        group.setVisible(true)

        group.handleCharacterClick(clickCount: 1)
        group.dismissComposer()
        group.handleCharacterClick(clickCount: 2)

        XCTAssertFalse(group.composerVisible)
        XCTAssertEqual(pulseRequests, 1)
        group.setVisible(false)
    }

    func testStandaloneCharacterDoubleClickDoesNotChangeComposerVisibility() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(id: roomID, name: "테스트", ownerID: UUID(), members: [], inviteCodeHint: "TEST")]
        model.preferences.activeRoomID = roomID
        var pulseRequests = 0
        let group = OverlayWindowGroup(
            model: model,
            onCharacterDoubleClick: { pulseRequests += 1 }
        )
        group.setVisible(true)

        group.handleCharacterClick(clickCount: 2)

        XCTAssertFalse(group.composerVisible)
        XCTAssertEqual(pulseRequests, 1)
        group.setVisible(false)
    }

    func testCharacterClickFocusesMessageFieldAfterMouseEventCompletes() async {
        let model = AppModel(preferences: .defaults)
        let controller = OverlayInteractionWindowController(
            model: model,
            onSend: { _ in },
            onInputActivity: {},
            onTypingChanged: { _ in },
            onCancel: {}
        )
        controller.setVisible(true)

        controller.focusMessageField()
        await waitForWindowState { controller.isKeyWindow && controller.messageFieldIsFirstResponder }

        XCTAssertTrue(controller.isVisible)
        XCTAssertTrue(controller.isKeyWindow)
        XCTAssertTrue(controller.messageFieldIsFirstResponder)
        XCTAssertTrue(controller.usesTransparentSurface)
        controller.setVisible(false)
    }

    func testComposerRequestsDismissWhenAnotherWindowBecomesKey() async {
        let model = AppModel(preferences: .defaults)
        var dismissRequests = 0
        var typingStops = 0
        let scheduler = TestComposerFocusLossScheduler()
        let controller = OverlayInteractionWindowController(
            model: model,
            onSend: { _ in },
            onInputActivity: {},
            onTypingChanged: { if !$0 { typingStops += 1 } },
            onCancel: { dismissRequests += 1 },
            focusLossScheduler: scheduler,
            isApplicationActive: { false }
        )
        let otherWindow = NSWindow(
            contentRect: CGRect(x: 0, y: 0, width: 100, height: 100),
            styleMask: [.titled],
            backing: .buffered,
            defer: false
        )
        defer {
            controller.setVisible(false)
            otherWindow.orderOut(nil)
        }

        controller.setVisible(true)
        controller.focusMessageField()
        await waitForWindowState { controller.isKeyWindow && controller.messageFieldIsFirstResponder }
        XCTAssertTrue(controller.isKeyWindow)

        otherWindow.makeKeyAndOrderFront(nil)
        await waitForWindowState { otherWindow.isKeyWindow }

        XCTAssertEqual(dismissRequests, 0)
        XCTAssertEqual(scheduler.latestDelay, .milliseconds(250))
        XCTAssertGreaterThan(typingStops, 0, "Typing ends as soon as the key window changes")
        XCTAssertTrue(controller.hasPendingFocusLossDismiss)
        scheduler.fireLatest()
        XCTAssertEqual(dismissRequests, 1)
    }

    func testEmojiInsertionCancelsPendingFocusLossDismissAndRestoresComposerFocus() async throws {
        let model = AppModel(preferences: .defaults)
        var dismissRequests = 0
        let scheduler = TestComposerFocusLossScheduler()
        let controller = OverlayInteractionWindowController(
            model: model,
            onSend: { _ in },
            onInputActivity: {},
            onTypingChanged: { _ in },
            onCancel: { dismissRequests += 1 },
            focusLossScheduler: scheduler,
            isApplicationActive: { true }
        )
        let otherWindow = NSWindow(
            contentRect: CGRect(x: 0, y: 0, width: 100, height: 100),
            styleMask: [.titled],
            backing: .buffered,
            defer: false
        )
        defer {
            controller.setVisible(false)
            otherWindow.orderOut(nil)
        }

        controller.setVisible(true)
        controller.focusMessageField()
        await waitForWindowState { controller.isKeyWindow && controller.messageFieldIsFirstResponder }
        let textView = try XCTUnwrap(controller.messageTextView)

        otherWindow.makeKeyAndOrderFront(nil)
        await waitForWindowState { otherWindow.isKeyWindow }
        XCTAssertTrue(controller.hasPendingFocusLossDismiss)

        // Selecting from the character palette can take arbitrarily longer than
        // the initial grace period. SIDEY remains active while its palette owns key.
        scheduler.fireLatest()
        XCTAssertEqual(dismissRequests, 0)
        XCTAssertTrue(controller.hasPendingFocusLossDismiss)
        XCTAssertEqual(scheduler.scheduleCount, 1)

        textView.insertText("👨‍👩‍👧‍👦", replacementRange: textView.selectedRange())
        await waitForWindowState { controller.isKeyWindow && controller.messageFieldIsFirstResponder }
        scheduler.fireLatest()

        XCTAssertEqual(model.draft, "👨‍👩‍👧‍👦")
        XCTAssertEqual(dismissRequests, 0)
        XCTAssertFalse(controller.hasPendingFocusLossDismiss)
        XCTAssertTrue(controller.isVisible)
        XCTAssertTrue(controller.isKeyWindow)
        XCTAssertTrue(controller.messageFieldIsFirstResponder)
    }

    func testComposerDismissesWhenAppDeactivatesAfterCharacterPaletteGrace() async {
        let model = AppModel(preferences: .defaults)
        var dismissRequests = 0
        let applicationActivity = TestApplicationActivityState()
        let scheduler = TestComposerFocusLossScheduler()
        let controller = OverlayInteractionWindowController(
            model: model,
            onSend: { _ in },
            onInputActivity: {},
            onTypingChanged: { _ in },
            onCancel: { dismissRequests += 1 },
            focusLossScheduler: scheduler,
            isApplicationActive: { applicationActivity.isActive }
        )
        let otherWindow = NSWindow(
            contentRect: CGRect(x: 0, y: 0, width: 100, height: 100),
            styleMask: [.titled],
            backing: .buffered,
            defer: false
        )
        defer {
            controller.setVisible(false)
            otherWindow.orderOut(nil)
        }

        controller.setVisible(true)
        controller.focusMessageField()
        await waitForWindowState { controller.isKeyWindow && controller.messageFieldIsFirstResponder }
        otherWindow.makeKeyAndOrderFront(nil)
        await waitForWindowState { otherWindow.isKeyWindow }

        scheduler.fireLatest()
        XCTAssertEqual(dismissRequests, 0)
        XCTAssertTrue(controller.hasPendingFocusLossDismiss)

        applicationActivity.isActive = false
        NotificationCenter.default.post(
            name: NSApplication.didResignActiveNotification,
            object: NSApplication.shared
        )
        XCTAssertEqual(scheduler.scheduleCount, 2)
        scheduler.fireLatest()

        XCTAssertEqual(dismissRequests, 1)
        XCTAssertFalse(controller.hasPendingFocusLossDismiss)
    }

    func testAppDelegatePreventsDefaultSettingsSceneRestorationOnReopen() {
        let delegate = AppDelegate()

        XCTAssertFalse(delegate.applicationShouldHandleReopen(
            NSApplication.shared,
            hasVisibleWindows: false
        ))
    }

    func testComposerDismissPreservesDraftAndStopsTyping() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID, name: "테스트", ownerID: UUID(), members: [],
            inviteCodeHint: "TEST", inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        model.draft = "보존할 초안"
        var typingChanges: [Bool] = []
        let group = OverlayWindowGroup(model: model, onTypingChanged: { typingChanges.append($0) })
        group.setVisible(true)
        group.presentComposer()
        group.dismissComposer()

        XCTAssertEqual(model.draft, "보존할 초안")
        XCTAssertEqual(typingChanges, [false], "Restoring a draft is not a text edit")
    }

    func testComposerDismissesFiveSecondsAfterTheLastSubmittedMessage() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID, name: "테스트", ownerID: UUID(), members: [],
            inviteCodeHint: "TEST", inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        var sentBodies: [String] = []
        let scheduler = TestComposerAutoDismissScheduler()
        let group = OverlayWindowGroup(
            model: model,
            onSend: { sentBodies.append($0) },
            composerAutoDismissScheduler: scheduler
        )
        group.setVisible(true)
        group.presentComposer()

        group.submitComposerMessage("첫 메시지")
        group.submitComposerMessage("마지막 메시지")

        XCTAssertTrue(group.composerVisible)
        XCTAssertEqual(sentBodies, ["첫 메시지", "마지막 메시지"])
        XCTAssertEqual(scheduler.scheduleCount, 2)
        XCTAssertEqual(scheduler.latestDelay, .seconds(5))

        scheduler.fireLatest()
        XCTAssertFalse(group.composerVisible)
        XCTAssertFalse(group.interactionIsVisible)
    }

    func testPresentingComposerAgainCancelsPendingAutoDismissForFailureRecovery() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID, name: "테스트", ownerID: UUID(), members: [],
            inviteCodeHint: "TEST", inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        let scheduler = TestComposerAutoDismissScheduler()
        let group = OverlayWindowGroup(
            model: model,
            composerAutoDismissScheduler: scheduler
        )
        group.setVisible(true)
        group.presentComposer()
        group.submitComposerMessage("실패할 메시지")

        model.draft = "실패할 메시지"
        group.presentComposer()
        scheduler.fireLatest()

        XCTAssertTrue(group.composerVisible)
        XCTAssertEqual(model.draft, "실패할 메시지")
        XCTAssertGreaterThanOrEqual(scheduler.cancelCount, 1)
        group.dismissComposer()
    }

    func testTypingNextMessageCancelsPendingComposerAutoDismiss() {
        let model = AppModel(preferences: .defaults)
        let roomID = UUID()
        model.rooms = [Room(
            id: roomID, name: "테스트", ownerID: UUID(), members: [],
            inviteCodeHint: "TEST", inviteVersion: 1
        )]
        model.preferences.activeRoomID = roomID
        var typingChanges: [Bool] = []
        let scheduler = TestComposerAutoDismissScheduler()
        let group = OverlayWindowGroup(
            model: model,
            onTypingChanged: { typingChanges.append($0) },
            composerAutoDismissScheduler: scheduler
        )
        group.setVisible(true)
        group.presentComposer()
        group.submitComposerMessage("첫 메시지")

        group.composerDidReceiveInput()
        group.composerTypingChanged(true)
        scheduler.fireLatest()

        XCTAssertTrue(group.composerVisible)
        XCTAssertTrue(group.interactionIsVisible)
        XCTAssertEqual(typingChanges, [true])
        XCTAssertGreaterThanOrEqual(scheduler.cancelCount, 1)
        group.dismissComposer()
    }

    func testClosingSettingsDoesNotHidePixelWorld() {
        let model = AppModel(preferences: .defaults)
        let settings = SettingsWindowController(model: model)
        let group = OverlayWindowGroup(model: model)
        group.setVisible(true)
        settings.show()
        settings.close()

        XCTAssertTrue(group.worldIsVisible)
        XCTAssertFalse(settings.window?.isVisible ?? true)
    }

    func testHiddenOverlayReleasesSpriteKitView() {
        let model = AppModel(preferences: .defaults)
        let group = OverlayWindowGroup(model: model)
        group.setVisible(true)
        XCTAssertTrue(group.worldIsRendering)

        group.setVisible(false)
        XCTAssertFalse(group.worldIsVisible)
        XCTAssertFalse(group.worldIsRendering)
    }

    func testRecentHistoryIsNormalIndependentWindow() {
        let model = AppModel(preferences: .defaults)
        let history = HistoryWindowController(model: model)

        XCTAssertEqual(history.window?.level, .normal)
        XCTAssertTrue(history.window?.styleMask.contains(.titled) ?? false)
        XCTAssertFalse(history.window?.collectionBehavior.contains(.canJoinAllSpaces) ?? true)
    }

    func testStatusMenuContainsOnlyPixelWorldOverlayControls() throws {
        let activeRoomID = UUID()
        let otherRoomID = UUID()
        let rooms = [
            Room(id: activeRoomID, name: "작업방", ownerID: UUID(), members: [], inviteCodeHint: "AAAA", inviteVersion: 1),
            Room(id: otherRoomID, name: "친구방", ownerID: UUID(), members: [], inviteCodeHint: "BBBB", inviteVersion: 1)
        ]
        let controller = StatusItemController(onToggleOverlay: {}, onOpenSettings: {}, onQuit: {})
        controller.update(
            overlayVisible: true,
            rooms: rooms,
            activeRoomID: activeRoomID,
            unreadCounts: [otherRoomID: 3],
            quietModeEnabled: true,
            launchAtLogin: true
        )

        let menu = controller.makeMenu()
        XCTAssertNotNil(menu.item(withTitle: L10n.text("status.menu.compose")))
        XCTAssertNil(menu.item(withTitle: "캐릭터 이동 모드"))
        XCTAssertNotNil(menu.item(withTitle: L10n.text("status.menu.history")))
        XCTAssertNil(menu.item(withTitle: "오버레이 잠금 해제"))
        XCTAssertNil(menu.item(withTitle: "오버레이 위치 초기화"))
        XCTAssertNotNil(menu.item(withTitle: L10n.text("status.menu.group_settings")))
        XCTAssertNil(menu.item(withTitle: "업데이트 확인…"))
        XCTAssertEqual(menu.item(withTitle: L10n.text("status.menu.quiet_mode"))?.state, .on)
        XCTAssertEqual(menu.item(withTitle: L10n.text("settings.startup.title"))?.state, .on)
        let groups = try XCTUnwrap(menu.item(withTitle: L10n.text("status.menu.active_group"))?.submenu)
        XCTAssertEqual(groups.item(withTitle: "작업방")?.state, .on)
        let count = NumberFormatter.localizedString(from: NSNumber(value: 3), number: .decimal)
        XCTAssertNotNil(groups.item(withTitle: "친구방" + L10n.format("status.room.unread_count", count)))
    }

    func testStatusItemUsesTemplateHamsterAssetsForReadAndUnreadStates() throws {
        let regular = try XCTUnwrap(StatusItemIconProvider.image(hasUnread: false))
        let unread = try XCTUnwrap(StatusItemIconProvider.image(hasUnread: true))
        XCTAssertTrue(regular.isTemplate)
        XCTAssertTrue(unread.isTemplate)
        XCTAssertEqual(regular.size, CGSize(width: 18, height: 18))
        XCTAssertEqual(unread.size, CGSize(width: 18, height: 18))
        XCTAssertNotEqual(regular.tiffRepresentation, unread.tiffRepresentation)
    }
}

@MainActor
private final class TestApplicationActivityState {
    var isActive = true
}

@MainActor
private final class TestComposerFocusLossScheduler: ComposerFocusLossScheduling {
    private var action: (@MainActor () -> Void)?
    private(set) var latestDelay: Duration?
    private(set) var cancelCount = 0
    private(set) var scheduleCount = 0

    func schedule(after delay: Duration, action: @escaping @MainActor () -> Void) {
        scheduleCount += 1
        latestDelay = delay
        self.action = action
    }

    func cancel() {
        cancelCount += 1
        action = nil
    }

    func fireLatest() {
        let pending = action
        action = nil
        pending?()
    }
}

@MainActor
private final class TestComposerAutoDismissScheduler: ComposerAutoDismissScheduling {
    private var action: (@MainActor () -> Void)?
    private(set) var latestDelay: Duration?
    private(set) var scheduleCount = 0
    private(set) var cancelCount = 0

    func schedule(after delay: Duration, action: @escaping @MainActor () -> Void) {
        latestDelay = delay
        self.action = action
        scheduleCount += 1
    }

    func cancel() {
        action = nil
        cancelCount += 1
    }

    func fireLatest() {
        let pending = action
        action = nil
        pending?()
    }
}
