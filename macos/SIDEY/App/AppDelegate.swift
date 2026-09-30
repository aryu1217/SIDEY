import AppKit
import QuartzCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var coordinator: AppCoordinator?
    #if DEBUG
    private var feedbackRoom: CharacterFeedbackDebugRoom?
    #endif
    #if DEBUG
    private var storeReview: StoreReviewDebugWindow?
    #endif
    private let launchProbe = LaunchPerformanceProbe()
    private var terminationTimeout: Task<Void, Never>?
    private var terminationReplySent = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        let environment = ProcessInfo.processInfo.environment
        if environment["SIDEY_TESTING"] == "1"
            || environment["XCTestConfigurationFilePath"] != nil
            || NSClassFromString("XCTestCase") != nil
            || NSClassFromString("XCTest.XCTestCase") != nil {
            return
        }
        guard BuildReview.shared.validateLaunch() else {
            NSApplication.shared.terminate(nil)
            return
        }
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--store-review") {
            NSApplication.shared.setActivationPolicy(.regular)
            let window = StoreReviewDebugWindow()
            storeReview = window
            window.showWindow(nil)
            NSApplication.shared.activate(ignoringOtherApps: true)
            return
        }
        #endif
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains(CharacterFeedbackDebugRoom.launchArgument) {
            NSApplication.shared.setActivationPolicy(.regular)
            let room = CharacterFeedbackDebugRoom()
            feedbackRoom = room
            room.showWindow(nil)
            NSApplication.shared.activate(ignoringOtherApps: true)
            return
        }
        #endif
        let releaseChannel = AppReleaseChannel.resolve()
        let coordinator: AppCoordinator
        if releaseChannel.isLocalMembershipTest,
           let suiteName = releaseChannel.preferencesSuiteName,
           let defaults = UserDefaults(suiteName: suiteName) {
            coordinator = AppCoordinator(
                preferencesStore: .userDefaults(defaults),
                legacyMigrator: .none,
                releaseChannel: releaseChannel,
                onLandingFirstFrame: { [weak launchProbe] in
                    launchProbe?.markFirstFrame()
                }
            )
        } else if let suiteName = environment["SIDEY_PREFERENCES_SUITE"],
           let defaults = UserDefaults(suiteName: suiteName) {
            coordinator = AppCoordinator(
                preferencesStore: .userDefaults(defaults),
                legacyMigrator: .none,
                releaseChannel: releaseChannel,
                onLandingFirstFrame: { [weak launchProbe] in
                    launchProbe?.markFirstFrame()
                }
            )
        } else if let suiteName = releaseChannel.preferencesSuiteName,
                  let defaults = UserDefaults(suiteName: suiteName) {
            coordinator = AppCoordinator(
                preferencesStore: .userDefaults(defaults),
                legacyMigrator: .none,
                releaseChannel: releaseChannel,
                onLandingFirstFrame: { [weak launchProbe] in
                    launchProbe?.markFirstFrame()
                }
            )
        } else {
            coordinator = AppCoordinator(
                releaseChannel: releaseChannel,
                onLandingFirstFrame: { [weak launchProbe] in
                    launchProbe?.markFirstFrame()
                }
            )
        }
        self.coordinator = coordinator
        coordinator.start()
        BuildReview.shared.observeReadyWindow()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        #if DEBUG
        if feedbackRoom != nil {
            if feedbackRoom?.window?.isVisible != true { feedbackRoom = CharacterFeedbackDebugRoom() }
            feedbackRoom?.showWindow(nil)
            return false
        }
        #endif
        coordinator?.handleManualReopen(
            originatesFromOverlayInteraction: OverlayWindowIdentifier.isInteractionSource(
                sender.currentEvent?.window?.identifier
            )
        )
        // AppCoordinator exclusively owns SIDEY's custom settings window.
        // Prevent AppKit/SwiftUI from restoring the empty Settings scene too.
        return false
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where coordinator?.handleOpenURL(url) == true {
            return
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let shutdown = coordinator?.shutdown() else { return .terminateNow }
        guard terminationTimeout == nil else { return .terminateLater }
        terminationTimeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(3)) }
            catch { return }
            shutdown.cancel()
            self?.finishTermination(sender)
        }
        Task { [weak self] in
            await shutdown.value
            self?.finishTermination(sender)
        }
        return .terminateLater
    }

    private func finishTermination(_ sender: NSApplication) {
        guard !terminationReplySent else { return }
        terminationReplySent = true
        terminationTimeout?.cancel()
        terminationTimeout = nil
        sender.reply(toApplicationShouldTerminate: true)
    }

    func applicationWillTerminate(_ notification: Notification) {
        BuildReview.shared.stop()
        coordinator?.shutdown()
    }
}

@MainActor
private final class LaunchPerformanceProbe {
    private let startedAt = CACurrentMediaTime()
    private let outputURL = ProcessInfo.processInfo.environment["SIDEY_LAUNCH_METRICS_PATH"]
        .map { URL(fileURLWithPath: $0) }
    private var didReport = false

    func markFirstFrame() {
        guard !didReport, let outputURL else { return }
        didReport = true
        let snapshot = LaunchMetricsSnapshot(
            firstLandingFrameMS: (CACurrentMediaTime() - startedAt) * 1_000
        )
        Task.detached(priority: .utility) {
            guard let data = try? JSONEncoder().encode(snapshot) else { return }
            try? data.write(to: outputURL, options: .atomic)
        }
    }
}

private struct LaunchMetricsSnapshot: Codable, Sendable {
    let firstLandingFrameMS: Double
}
