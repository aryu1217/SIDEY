import AppKit

@MainActor
final class AppCoordinator {
    let model: AppModel

    lazy var typingActivity = makeTypingActivity()

    private func makeTypingActivity() -> TypingActivityController {
        TypingActivityController(
            now: { ProcessInfo.processInfo.systemUptime },
            automaticallySchedule: true,
            publish: { [weak self] roomID, event in
                try await self?.messagingTransport?.publishTyping(roomID: roomID, event: event)
            },
            localTyping: { [weak self] roomID, active in
                guard let self, let userID = model.currentUserID else { return }
                model.updateTyping(roomID: roomID, userID: userID, active: active)
            }
        )
    }

    let preferencesStore: PreferencesStore
    private let legacyMigrator: LegacySettingsMigrator
    let releaseChannel: AppReleaseChannel
    var backend: SideyBackend?
    var messagingTransport: RoomMessagingTransportRouter?
    var realtimeRolloutMonitor: RealtimeRolloutMonitor?
    var realtimeRolloutRefreshInterval: Duration = .seconds(300)
    let runtimeConfiguration: RuntimeConfiguration?
    let configurationError: Error?
    var realtimeTransportInitializationError: Error?
    let keychainAccessSession: KeychainAccessSession
    let launchReason: LaunchReason
    private let onLandingFirstFrame: () -> Void
    private let launchAtLoginController: LaunchAtLoginController
    lazy var overlayWindows = OverlayWindowGroup(
        model: model,
        onSend: { [weak self] body in self?.sendMessage(body) },
        onTypingChanged: { [weak self] active in self?.typingChanged(active) },
        onCharacterDoubleClick: { [weak self] in self?.characterDoubleClicked() },
        onTargetCharacterClick: { [weak self] userID in self?.characterThrowRequested(targetUserID: userID) },
        onDrawIconClick: { [weak self] eventID in self?.openDrawEvent(eventID) },
        onRegionChanged: { [weak self] in self?.persistPreferences() },
        onTreeMovementToggle: { [weak self] in self?.toggleTreeMovement() }
    )
    private var historyWindowStorage: HistoryWindowController?
    private var historyWindow: HistoryWindowController {
        if let historyWindowStorage { return historyWindowStorage }
        let historyWindow = makeHistoryWindow()
        historyWindowStorage = historyWindow
        return historyWindow
    }
    lazy var settingsWindow = SettingsWindowController(
        model: model,
        actions: SettingsActions(
            onOverlayVisibilityChanged: { [weak self] visible in self?.setOverlayVisible(visible) },
            onOverlayRegionChanged: { [weak self] preference in self?.setOverlayRegion(preference) },
            onShowOfflineMembersChanged: { [weak self] visible in self?.setShowOfflineMembers(visible) },
            onRequiresRightClickToThrowChanged: { [weak self] required in
                self?.setRequiresRightClickToThrow(required)
            },
            onQuietModeChanged: { [weak self] enabled in self?.setQuietMode(enabled) },
            onLaunchAtLoginChanged: { [weak self] enabled in self?.setLaunchAtLogin(enabled) },
            onPurchase: { [weak self] productID in self?.purchase(productID: productID) },
            onRefreshCommerceState: { [weak self] productID in
                self?.refreshCommerceState(productID: productID)
            },
            onSetEquippedCosmetic: { [weak self] kind, catalogItemID in
                self?.setEquippedCosmetic(kind: kind, catalogItemID: catalogItemID)
            },
            onRestorePurchases: { [weak self] in self?.restoreAppStorePurchases() },
            onSignInWithApple: { [weak self] payload in self?.signInWithApple(payload) },
            onDeleteAccount: { [weak self] payload in self?.deleteAccount(payload) },
            onSaveProfile: { [weak self] in self?.saveProfile() },
            onSetCharacter: { [weak self] characterID in self?.setCharacter(characterID) },
            onCreateRoom: { [weak self] in self?.createRoom() },
            onJoinRoom: { [weak self] in self?.joinRoom() },
            onSelectRoom: { [weak self] roomID in self?.selectRoom(roomID) },
            onCopyInviteCode: { [weak self] roomID in
                await self?.copyInviteCode(roomID: roomID) ?? false
            },
            onRotateInviteCode: { [weak self] roomID in self?.rotateInviteCode(roomID: roomID) },
            onRenameRoom: { [weak self] roomID, name in self?.renameRoom(roomID, name: name) },
            onRemoveRoomMember: { [weak self] roomID, userID in self?.removeRoomMember(roomID, userID: userID) },
            onLeaveRoom: { [weak self] roomID in self?.leaveRoom(roomID) },
            onDeleteRoom: { [weak self] roomID in self?.deleteRoom(roomID) },
            onCreateDraw: { [weak self] kind, participants, entries, labels in
                self?.createDrawEvent(kind: kind, participants: participants,
                                      entryLabels: entries, ladderLabels: labels)
            },
            onReplayDraw: { [weak self] id in self?.replayDrawEvent(id) },
            onCloseDraw: { [weak self] id in self?.closeDrawEvent(id) },
            onDeleteDraw: { [weak self] id in self?.deleteDrawEvent(id) },
            onRefreshDraws: { [weak self] in self?.refreshDrawEvents() },
            onCharacterSoundEffectsChanged: { [weak self] enabled in
                guard let self else { return }
                self.model.preferences.characterSoundEffectsEnabled = enabled
                self.model.characterImpactAudio.isEnabled = enabled
                self.persistPreferences()
            },
            onCharacterImpact: { [weak self] id, time in
                guard let self else { return }
                self.model.characterImpactAudio.isEnabled = self.model.preferences.characterSoundEffectsEnabled
                self.model.characterImpactAudio.play(objectID: id, at: time)
            },
            onStopCharacterSounds: { [weak self] in
                self?.model.characterImpactAudio.stopAll()
            },
            onGlobalShortcutChanged: { [weak self] action, binding in
                self?.setGlobalShortcut(action, binding: binding)
            },
            onReconnect: { [weak self] in self?.reconnectBackend() }
        ),
        onClose: { [weak self] in self?.settingsDidClose() }
    )
    private lazy var landingWindow = LandingWindowController(
        onSkip: { [weak self] in self?.finishLanding() },
        onFirstFrame: onLandingFirstFrame
    )
    private lazy var statusItemController = StatusItemController(
        onToggleOverlay: { [weak self] in self?.toggleOverlay() },
        onFocusMessage: { [weak self] in self?.toggleMessageComposer() },
        onSelectRoom: { [weak self] roomID in self?.selectRoom(roomID) },
        onToggleQuietMode: { [weak self] in self?.setQuietMode(!(self?.model.preferences.quietModeEnabled ?? false)) },
        onOpenHistory: { [weak self] in self?.showHistory() },
        onOpenStore: { [weak self] in self?.showStore() },
        onToggleLaunchAtLogin: { [weak self] in self?.setLaunchAtLogin(!(self?.model.launchAtLogin ?? false)) },
        onOpenGroupSettings: { [weak self] in self?.showGroupSettings() },
        onOpenSettings: { [weak self] in self?.showSettings() },
        onQuit: { NSApplication.shared.terminate(nil) }
    )
    private lazy var globalShortcuts = GlobalShortcutController(
        configuration: model.preferences.globalShortcuts,
        onAction: { [weak self] action in self?.handleGlobalShortcut(action) },
        onStatusChanged: { [weak self] statuses in
            self?.model.globalShortcutStatuses = statuses
            self?.refreshStatusItem()
        }
    )
    let commerceSession = CommerceSession()
    let roomSession = RoomSessionLifetime()
    var drawRefreshTask: Task<Void, Never>?
    var drawRefreshGeneration = 0
    var drawOpenGeneration = 0
    var drawResultWindowStorage: DrawResultWindowController?
    var drawResultWindow: DrawResultWindowController {
        if let drawResultWindowStorage { return drawResultWindowStorage }
        let controller = DrawResultWindowController()
        drawResultWindowStorage = controller
        return controller
    }
    var treeMovementTask: Task<Void, Never>?
    private var landingTask: Task<Void, Never>?
    private var landingDidComplete = false
    private var didCompleteFirstRunTransition = false
    private var settingsWindowPresented = false
    var backendBootstrapState: BackendBootstrapState = .pending
    var backendConnectionStatus: BackendConnectionStatus?
    private lazy var activityMonitor = SystemActivityMonitor { [weak self] state in
        self?.localPresenceChanged(state)
    }
    private let mainThreadProbe = MainThreadPerformanceProbe()

    init(
        preferencesStore: PreferencesStore = .live,
        legacyMigrator: LegacySettingsMigrator = .live,
        keychainAccessSession: KeychainAccessSession = .shared,
        releaseChannel: AppReleaseChannel = .resolve(),
        arguments: [String] = ProcessInfo.processInfo.arguments,
        onLandingFirstFrame: @escaping () -> Void = {}
    ) {
        self.releaseChannel = releaseChannel
        self.launchAtLoginController = LaunchAtLoginController(mode: releaseChannel.loginItemMode)
        self.preferencesStore = preferencesStore
        self.legacyMigrator = legacyMigrator
        self.keychainAccessSession = keychainAccessSession
        self.onLandingFirstFrame = onLandingFirstFrame
        let preferences = legacyMigrator.migrateIfNeeded(preferencesStore)
        self.launchReason = LaunchRouter.reason(
            hasShownNativeLanding: preferences.hasShownNativeLanding,
            arguments: arguments
        )
        self.model = AppModel(preferences: preferences)
        do {
            let configuration = try RuntimeConfiguration.resolve(releaseChannel: releaseChannel)
            self.runtimeConfiguration = configuration
            self.configurationError = nil
        } catch {
            self.runtimeConfiguration = nil
            self.configurationError = error
        }
        self.roomSession.switchPipeline = RoomSwitchPipeline(
            debounce: .milliseconds(150),
            performSwitch: { [weak self] roomID in
                guard let self,
                      let backend = self.backend,
                      let messagingTransport = self.messagingTransport
                else {
                    throw SideyBackendError.realtimeUnavailable
                }
                async let messages = backend.recentMessages(roomID: roomID)
                try await messagingTransport.setActiveRoom(roomID)
                return try await messages
            },
            restoreCommittedRoom: { [weak self] in
                guard let self, let messagingTransport = self.messagingTransport else {
                    throw SideyBackendError.realtimeUnavailable
                }
                try await messagingTransport.setActiveRoom(self.model.activeRoom?.id)
            },
            operationChanged: { [weak self] operation in
                self?.model.groupOperation = operation
            },
            committed: { [weak self] roomID, messages in
                self?.commitRoomSwitch(roomID: roomID, messages: messages)
            },
            failed: { [weak self] _, error, restoreError in
                self?.handleRoomSwitchFailure(error, restoreError: restoreError)
            }
        )
    }

    func start() {
        keychainAccessSession.setAccessDeniedHandler { _ in
            Task { @MainActor in
                NSApplication.shared.terminate(nil)
            }
        }
        if !model.preferences.keychainTransitionComplete,
           !KeychainTransitionNotice.present() {
            keychainAccessSession.denyFurtherAccess()
            return
        }
        if let runtimeConfiguration {
            let backend = SideyBackend(
                configuration: runtimeConfiguration,
                keychain: KeychainStore(
                    service: releaseChannel.keychainService,
                    session: keychainAccessSession
                )
            )
            self.backend = backend
            // The authenticated remote rollout policy owns transport selection.
            // Construction therefore happens after `backend.boot` restores and
            // validates the exact Supabase account/session.
            messagingTransport = nil
            realtimeTransportInitializationError = nil
        }

        mainThreadProbe.start()
        statusItemController.install()
        globalShortcuts.install()
        overlayWindows.restore(preference: model.preferences.overlayRegion)
        model.launchAtLogin = launchAtLoginController.isEnabled
        model.preferences.launchAtLogin = model.launchAtLogin

        // Overlay rendering is gated on both completed onboarding and a restored
        // backend session. Keep the user's persisted visibility request intact
        // while the runtime windows remain hidden during bootstrap.
        applyRequestedOverlayVisibility()

        switch launchReason {
        case .firstRun:
            refreshStatusItem()
            showLanding()
        case .manual:
            refreshStatusItem()
            showSettings()
        case .loginItem:
            refreshStatusItem()
            applyActivationPolicyForCurrentLifecycle()
        }
        activityMonitor.start()
        startBackend()
    }

    func handleManualReopen(originatesFromOverlayInteraction: Bool = false) {
        guard ManualReopenPolicy.shouldOpenSettings(
            hasShownNativeLanding: model.preferences.hasShownNativeLanding,
            composerVisible: overlayWindows.composerVisible,
            originatesFromOverlayInteraction: originatesFromOverlayInteraction
        ) else { return }
        showSettings()
    }

    private var shutdownTask: Task<Void, Never>?

    @discardableResult
    func shutdown() -> Task<Void, Never>? {
        if let shutdownTask { return shutdownTask }
        globalShortcuts.uninstall()
        cancelTreeMovementRequests()
        model.treeMovement.reset()
        landingTask?.cancel()
        stopAllTyping()
        roomSession.cancel()
        drawRefreshTask?.cancel()
        drawRefreshTask = nil
        drawResultWindowStorage?.close()
        commerceSession.cancel(model: model)
        activityMonitor.stop()
        mainThreadProbe.stop()
        let typingActivity = typingActivity
        let backend = backend
        let messagingTransport = messagingTransport
        shutdownTask = Task {
            await typingActivity.shutdown()
            if let messagingTransport {
                await messagingTransport.shutdown()
            } else {
                await backend?.shutdown()
            }
        }
        keychainAccessSession.setAccessDeniedHandler(nil)
        persistPreferences()
        return shutdownTask
    }

    private func showLanding() {
        NSApplication.shared.setActivationPolicy(.regular)
        landingWindow.show()
        NSApplication.shared.activate(ignoringOtherApps: true)
        landingTask?.cancel()
        landingTask = Task { [weak self] in
            let configured = ProcessInfo.processInfo.environment["SIDEY_LANDING_DURATION_SECONDS"]
                .flatMap(Double.init) ?? 2
            try? await Task.sleep(for: .seconds(max(0.1, configured)))
            guard !Task.isCancelled else { return }
            self?.finishLanding()
        }
    }

    private func finishLanding() {
        guard launchReason == .firstRun, !landingDidComplete else { return }
        landingDidComplete = true
        landingTask?.cancel()
        landingTask = nil
        model.preferences.hasShownNativeLanding = true
        persistPreferences()
        advanceFirstRunTransition()
    }

    func advanceFirstRunTransition() {
        guard launchReason == .firstRun, !didCompleteFirstRunTransition else { return }
        let destination = FirstRunTransition.destination(
            landingCompleted: landingDidComplete,
            onboardingComplete: model.preferences.onboardingComplete,
            backendState: backendBootstrapState
        )
        switch destination {
        case .waiting:
            if landingDidComplete, model.preferences.onboardingComplete {
                landingWindow.setRestoringSession(true)
            }
        case .onboarding:
            didCompleteFirstRunTransition = true
            landingWindow.close()
            showSettings()
        case .overlay:
            didCompleteFirstRunTransition = true
            landingWindow.close()
            applyActivationPolicyForCurrentLifecycle()
            applyRequestedOverlayVisibility()
        case .recovery:
            didCompleteFirstRunTransition = true
            landingWindow.close()
            showSettings()
        }
    }

    func showSettings() {
        settingsWindowPresented = true
        applyActivationPolicyForCurrentLifecycle()
        settingsWindow.show()
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    private func showGroupSettings() {
        model.activeSettingsPage = .groups
        showSettings()
    }

    private func showStore() {
        model.activeSettingsPage = .store
        showSettings()
    }

    func handleOpenURL(_ url: URL) -> Bool {
        return false
    }

    private func settingsDidClose() {
        settingsWindowPresented = false
        applyActivationPolicyForCurrentLifecycle()
    }

    func applyActivationPolicyForCurrentLifecycle() {
        let policy: NSApplication.ActivationPolicy = DockVisibilityPolicy.shouldShowDockIcon(
            firstRunPresentationActive: launchReason == .firstRun && !didCompleteFirstRunTransition,
            settingsWindowPresented: settingsWindowPresented
        ) ? .regular : .accessory
        NSApplication.shared.setActivationPolicy(policy)
    }

    private func toggleOverlay() {
        setOverlayVisible(!model.overlayVisible)
    }

    func handleGlobalShortcut(_ action: GlobalShortcutAction) {
        switch action {
        case .toggleQuietMode:
            setQuietMode(!model.preferences.quietModeEnabled)
        case .toggleComposer:
            toggleMessageComposer()
        case .openHistory:
            showHistory()
        case .toggleOverlay:
            toggleOverlay()
        }
    }

    func toggleMessageComposer() {
        if overlayWindows.composerVisible {
            overlayWindows.dismissComposer()
            return
        }
        if !model.overlayVisible { setOverlayVisible(true) }
        overlayWindows.focusMessageField()
    }

    private func markActiveRoomRead() {
        guard let roomID = model.activeRoom?.id else { return }
        model.markRoomRead(roomID)
        refreshStatusItem()
    }

    private func setOverlayVisible(_ visible: Bool) {
        model.setOverlayVisibility(OverlayVisibility(isVisible: visible))
        applyRequestedOverlayVisibility()
        refreshStatusItem()
        persistPreferences()
    }

    func applyRequestedOverlayVisibility() {
        overlayWindows.setVisible(OverlayRevealPolicy.isVisible(
            requested: model.preferences.overlayVisible,
            onboardingComplete: model.preferences.onboardingComplete,
            backendState: backendBootstrapState
        ))
    }

    private func setOverlayRegion(_ preference: OverlayRegionPreference) {
        overlayWindows.setRegionPreference(preference)
        drawResultWindowStorage?.updateScreen(overlayWindows.currentScreenIdentifier)
        persistPreferences()
    }

    private func setShowOfflineMembers(_ visible: Bool) {
        model.preferences.showOfflineMembers = visible
        overlayWindows.refreshThrowHotspots()
        persistPreferences()
    }

    private func setRequiresRightClickToThrow(_ required: Bool) {
        model.preferences.requiresRightClickToThrow = required
        overlayWindows.throwInteractionPreferenceChanged()
        persistPreferences()
    }

    private func setQuietMode(_ enabled: Bool) {
        model.preferences.quietModeEnabled = enabled
        if enabled { model.clearBubbles() }
        refreshStatusItem()
        persistPreferences()
    }

    func setGlobalShortcut(_ action: GlobalShortcutAction, binding: GlobalShortcutBinding) {
        guard globalShortcuts.update(action, binding: binding) else {
            refreshStatusItem()
            return
        }
        model.preferences.globalShortcuts[action] = binding
        persistPreferences()
        refreshStatusItem()
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            try launchAtLoginController.setEnabled(enabled)
            model.launchAtLogin = launchAtLoginController.isEnabled
            model.preferences.launchAtLogin = model.launchAtLogin
            model.errorMessage = nil
            refreshStatusItem()
            persistPreferences()
        } catch {
            model.launchAtLogin = launchAtLoginController.isEnabled
            model.errorMessage = L10n.format(
                "launch_at_login.update_failed",
                error.localizedDescription
            )
            refreshStatusItem()
        }
    }

    private func copyInviteCode(roomID: UUID) async -> Bool {
        guard model.rooms.first(where: { $0.id == roomID })?.inviteCodeReady == true else {
            model.dismissSuccess()
            model.errorMessage = L10n.text("invite.error.revoked")
            return false
        }
        guard let backend else {
            model.dismissSuccess()
            model.errorMessage = L10n.text("invite.error.backend_unavailable")
            return false
        }
        do {
            guard let inviteCode = try await backend.storedInviteCode(roomID: roomID),
                  !inviteCode.isEmpty
            else {
                model.dismissSuccess()
                model.errorMessage = L10n.text("invite.error.not_stored_on_device")
                return false
            }
            NSPasteboard.general.clearContents()
            guard NSPasteboard.general.setString(inviteCode, forType: .string) else {
                model.dismissSuccess()
                model.errorMessage = L10n.text("invite.error.clipboard_failed")
                return false
            }
            model.errorMessage = nil
            model.dismissSuccess()
            return true
        } catch {
            model.dismissSuccess()
            model.errorMessage = L10n.format(
                "invite.error.read_failed",
                SideyBackendError.normalized(error).localizedDescription
            )
            return false
        }
    }

    private func rotateInviteCode(roomID: UUID) {
        guard let backend else { return }
        runMutation(successMessage: L10n.text("invite.rotate.success")) {
            let created = try await backend.rotateInviteCode(roomID: roomID)
            self.model.lastCreatedInviteCode = created.inviteCode
            if !created.storedInKeychain {
                self.model.errorMessage = L10n.text("invite.rotate.keychain_warning")
            }
        }
    }

    func refreshStatusItem() {
        statusItemController.update(
            overlayVisible: model.overlayVisible,
            rooms: model.rooms,
            activeRoomID: model.activeRoom?.id,
            unreadCounts: model.unreadCounts,
            quietModeEnabled: model.preferences.quietModeEnabled,
            launchAtLogin: model.launchAtLogin,
            globalShortcuts: model.preferences.globalShortcuts,
            globalShortcutStatuses: model.globalShortcutStatuses
        )
    }

    func persistPreferences() {
        preferencesStore.save(model.preferences)
    }

    private func makeHistoryWindow() -> HistoryWindowController {
        HistoryWindowController(
            model: model,
            loadPage: { [weak self] roomID, cursor, pageSize in
                guard let backend = self?.backend else {
                    throw SideyBackendError.realtimeUnavailable
                }
                return try await backend.historyPage(
                    roomID: roomID,
                    before: cursor,
                    pageSize: pageSize
                )
            },
            onSend: { [weak self] in self?.sendMessage($0, source: .history) },
            onTypingChanged: { [weak self] in self?.typingChanged($0, source: .history) }
        )
    }

    private func showHistory() {
        markActiveRoomRead()
        applyActivationPolicyForCurrentLifecycle()
        historyWindow.show()
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func removeHistoryMessage(id: UUID, roomID: UUID) {
        historyWindowStorage?.historyStore.remove(messageID: id, roomID: roomID)
    }

    func reloadHistory(roomID: UUID) {
        historyWindowStorage?.historyStore.reload(roomID: roomID)
    }

}
