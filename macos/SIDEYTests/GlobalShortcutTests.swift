import AppKit
import Carbon
import XCTest
@testable import SIDEY

@MainActor
final class GlobalShortcutTests: XCTestCase {
    func testDefaultBindingsUseUniqueControlOptionCommandFourKeyChords() {
        let configuration = GlobalShortcutConfiguration.defaults
        XCTAssertEqual(Set(GlobalShortcutAction.allCases.map { configuration[$0] }).count, 4)
        XCTAssertEqual(configuration[.toggleOverlay].displayShortcut, "⌃⌥⌘H")
        XCTAssertEqual(configuration[.toggleQuietMode].displayShortcut, "⌃⌥⌘M")
        XCTAssertEqual(configuration[.toggleComposer].displayShortcut, "⌃⌥⌘I")
        XCTAssertEqual(configuration[.openHistory].displayShortcut, "⌃⌥⌘R")
        XCTAssertEqual(
            configuration[.toggleOverlay].modifierMask,
            UInt32(controlKey | optionKey | cmdKey)
        )
        XCTAssertEqual(configuration[.toggleOverlay].keyCode, UInt32(kVK_ANSI_H))
        XCTAssertEqual(
            GlobalShortcutAction.allCases.map { configuration[$0].descriptiveShortcut },
            [
                "Control + Option + Command + M",
                "Control + Option + Command + I",
                "Control + Option + Command + R",
                "Control + Option + Command + H"
            ]
        )
    }

    func testHoldingShortcutRunsOnceUntilReleaseAndDifferentShortcutsRemainIndependent() {
        let registrar = FakeGlobalShortcutRegistrar()
        var actions: [GlobalShortcutAction] = []
        let controller = GlobalShortcutController(registrar: registrar, onAction: { actions.append($0) })
        controller.install()

        registrar.send(.toggleComposer, pressed: true)
        registrar.send(.toggleComposer, pressed: true)
        registrar.send(.toggleOverlay, pressed: true)
        registrar.send(.toggleComposer, pressed: true)
        registrar.send(.toggleComposer, pressed: false)
        registrar.send(.toggleComposer, pressed: true)

        XCTAssertEqual(actions, [.toggleComposer, .toggleOverlay, .toggleComposer])
        controller.uninstall()
    }

    func testConflictOnlyDisablesItsOwnShortcutAndPublishesActionableNotice() {
        let registrar = FakeGlobalShortcutRegistrar()
        registrar.registrationResults[.toggleQuietMode] = OSStatus(eventHotKeyExistsErr)
        var actions: [GlobalShortcutAction] = []
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { actions.append($0) }, onStatusChanged: { statuses = $0 }
        )
        controller.install()
        registrar.send(.toggleQuietMode, pressed: true)
        registrar.send(.toggleOverlay, pressed: true)

        XCTAssertEqual(actions, [.toggleOverlay])
        XCTAssertEqual(statuses[.toggleQuietMode], .unavailable(OSStatus(eventHotKeyExistsErr)))
        XCTAssertEqual(
            statuses[.toggleQuietMode]?.notice,
            L10n.text("shortcut.error.used_by_other_app")
        )
        XCTAssertEqual(statuses[.toggleComposer], .registered)
        XCTAssertEqual(statuses[.openHistory], .registered)
        XCTAssertEqual(statuses[.toggleOverlay], .registered)
        controller.uninstall()
    }

    func testHandlerFailureReportsEveryShortcutWithoutAttemptingRegistrations() {
        let registrar = FakeGlobalShortcutRegistrar()
        registrar.handlerResult = OSStatus(eventInternalErr)
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { _ in XCTFail("실패한 등록에서 실행됨") },
            onStatusChanged: { statuses = $0 }
        )
        controller.install()
        registrar.send(.openHistory, pressed: true)

        XCTAssertTrue(registrar.registered.isEmpty)
        XCTAssertEqual(statuses.count, GlobalShortcutAction.allCases.count)
        XCTAssertTrue(statuses.values.allSatisfy { $0 == .unavailable(OSStatus(eventInternalErr)) })
        XCTAssertTrue(statuses.values.allSatisfy {
            $0.notice == L10n.text("shortcut.error.registration_failed")
        })
        controller.uninstall()
    }

    func testChangeAfterHandlerFailureIsRejectedWithoutRegisteringAnUnusableShortcut() {
        let registrar = FakeGlobalShortcutRegistrar()
        registrar.handlerResult = OSStatus(eventInternalErr)
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar,
            onAction: { _ in },
            onStatusChanged: { statuses = $0 }
        )
        controller.install()

        XCTAssertFalse(controller.update(.toggleOverlay, binding: .init(key: .o)))
        XCTAssertTrue(registrar.replacements.isEmpty)
        XCTAssertEqual(
            statuses[.toggleOverlay],
            .changeRejected(.unavailable(OSStatus(eventInternalErr)))
        )
        controller.uninstall()
    }

    func testLiveChangeUsesConfiguredBindingAndClearsHeldState() {
        let registrar = FakeGlobalShortcutRegistrar()
        var actions: [GlobalShortcutAction] = []
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { actions.append($0) }, onStatusChanged: { statuses = $0 }
        )
        controller.install()
        registrar.send(.toggleOverlay, pressed: true)

        let replacement = GlobalShortcutBinding(key: .o, modifiers: [.control, .shift])
        XCTAssertTrue(controller.update(.toggleOverlay, binding: replacement))
        registrar.send(.toggleOverlay, pressed: true)

        XCTAssertEqual(registrar.replacements.last?.action, .toggleOverlay)
        XCTAssertEqual(registrar.replacements.last?.binding, replacement)
        XCTAssertEqual(actions, [.toggleOverlay, .toggleOverlay])
        XCTAssertEqual(statuses[.toggleOverlay], .registered)
        controller.uninstall()
    }

    func testDuplicateAndRegistrationFailureKeepExistingBindingOperational() {
        let registrar = FakeGlobalShortcutRegistrar()
        var actions: [GlobalShortcutAction] = []
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { actions.append($0) }, onStatusChanged: { statuses = $0 }
        )
        controller.install()

        XCTAssertFalse(controller.update(.toggleOverlay, binding: .init(key: .m)))
        XCTAssertEqual(
            statuses[.toggleOverlay],
            .changeRejected(.duplicate(.toggleQuietMode))
        )
        XCTAssertTrue(registrar.replacements.isEmpty)

        registrar.replacementResults[.toggleOverlay] = OSStatus(eventHotKeyExistsErr)
        XCTAssertFalse(controller.update(.toggleOverlay, binding: .init(key: .o)))
        XCTAssertEqual(
            statuses[.toggleOverlay],
            .changeRejected(.unavailable(OSStatus(eventHotKeyExistsErr)))
        )
        registrar.send(.toggleOverlay, pressed: true)
        XCTAssertEqual(actions, [.toggleOverlay])
        controller.uninstall()
    }

    func testInvalidModifierChangeKeepsExistingBinding() {
        let registrar = FakeGlobalShortcutRegistrar()
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { _ in }, onStatusChanged: { statuses = $0 }
        )
        controller.install()

        XCTAssertFalse(controller.update(
            .toggleComposer,
            binding: GlobalShortcutBinding(key: .c, modifiers: [])
        ))
        XCTAssertEqual(statuses[.toggleComposer], .changeRejected(.invalid))
        XCTAssertTrue(registrar.replacements.isEmpty)
        controller.uninstall()
    }

    func testInstallIsIdempotentAndUninstallIgnoresLateEventsAndResetsHeldKey() {
        let registrar = FakeGlobalShortcutRegistrar()
        var actions: [GlobalShortcutAction] = []
        var statuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
        let controller = GlobalShortcutController(
            registrar: registrar, onAction: { actions.append($0) }, onStatusChanged: { statuses = $0 }
        )
        controller.install()
        controller.install()
        XCTAssertEqual(registrar.registered.map(\.action), GlobalShortcutAction.allCases)
        registrar.send(.toggleComposer, pressed: true)
        controller.uninstall()
        controller.uninstall()
        registrar.send(.toggleComposer, pressed: false)
        registrar.send(.toggleComposer, pressed: true)
        XCTAssertEqual(registrar.unregisterCount, 1)
        XCTAssertTrue(statuses.isEmpty)
        XCTAssertEqual(actions, [.toggleComposer])

        controller.install()
        registrar.send(.toggleComposer, pressed: true)
        XCTAssertEqual(actions, [.toggleComposer, .toggleComposer])
        controller.uninstall()
    }

    func testReleasingControllerUnregistersNativeResources() {
        let registrar = FakeGlobalShortcutRegistrar()
        var controller: GlobalShortcutController? = GlobalShortcutController(
            registrar: registrar, onAction: { _ in XCTFail("종료한 컨트롤러에서 실행됨") }
        )
        weak var releasedController = controller
        controller?.install()
        controller = nil
        XCTAssertNil(releasedController)
        XCTAssertEqual(registrar.unregisterCount, 1)
        registrar.send(.toggleComposer, pressed: true)
    }

    func testMenuShowsNativeShortcutColumnAndConflictTooltip() throws {
        var configuration = GlobalShortcutConfiguration.defaults
        configuration[.toggleComposer] = GlobalShortcutBinding(key: .c, modifiers: [.command, .shift])
        let controller = StatusItemController(onToggleOverlay: {}, onOpenSettings: {}, onQuit: {})
        controller.update(
            overlayVisible: true,
            globalShortcuts: configuration,
            globalShortcutStatuses: [
                .toggleQuietMode: .unavailable(OSStatus(eventHotKeyExistsErr)),
                .toggleComposer: .registered,
                .openHistory: .registered,
                .toggleOverlay: .registered
            ]
        )
        let menu = controller.makeMenu()
        for (title, action) in [
            (L10n.text("status.menu.overlay.hide"), GlobalShortcutAction.toggleOverlay),
            (L10n.text("status.menu.compose"), .toggleComposer),
            (L10n.text("status.menu.quiet_mode"), .toggleQuietMode),
            (L10n.text("status.menu.history"), .openHistory)
        ] {
            let item = try XCTUnwrap(menu.item(withTitle: title))
            XCTAssertEqual(item.keyEquivalent, configuration[action].key.rawValue.lowercased())
            XCTAssertNil(item.attributedTitle)
        }
        let quiet = try XCTUnwrap(menu.item(withTitle: L10n.text("status.menu.quiet_mode")))
        XCTAssertEqual(quiet.toolTip, L10n.text("shortcut.error.used_by_other_app"))
        let composer = try XCTUnwrap(menu.item(withTitle: L10n.text("status.menu.compose")))
        XCTAssertEqual(composer.toolTip, configuration[.toggleComposer].descriptiveShortcut)
        XCTAssertEqual(composer.keyEquivalentModifierMask, [.command, .shift])
    }
}

@MainActor
private final class FakeGlobalShortcutRegistrar: GlobalShortcutRegistering {
    struct Request: Equatable {
        let action: GlobalShortcutAction
        let binding: GlobalShortcutBinding
    }

    var handlerResult: OSStatus = noErr
    var registrationResults: [GlobalShortcutAction: OSStatus] = [:]
    var replacementResults: [GlobalShortcutAction: OSStatus] = [:]
    var registered: [Request] = []
    var replacements: [Request] = []
    var unregisterCount = 0
    private var handler: ((GlobalShortcutAction, Bool) -> Void)?

    func installHandler(_ handler: @escaping (GlobalShortcutAction, Bool) -> Void) -> OSStatus {
        self.handler = handler
        return handlerResult
    }

    func register(_ action: GlobalShortcutAction, binding: GlobalShortcutBinding) -> OSStatus {
        registered.append(Request(action: action, binding: binding))
        return registrationResults[action] ?? noErr
    }

    func replace(_ action: GlobalShortcutAction, binding: GlobalShortcutBinding) -> OSStatus {
        replacements.append(Request(action: action, binding: binding))
        return replacementResults[action] ?? noErr
    }

    func unregisterAll() {
        unregisterCount += 1
        // Keep the callback to simulate an already enqueued native event after teardown.
    }

    func send(_ action: GlobalShortcutAction, pressed: Bool) {
        handler?(action, pressed)
    }
}
