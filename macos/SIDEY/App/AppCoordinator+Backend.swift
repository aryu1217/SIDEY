import AppKit

extension AppCoordinator {
    func startBackend() {
        // Static configuration failures cannot recover in-process. A previous
        // transport factory failure can: Keychain access, rollout selection,
        // and Firebase bootstrap are all retried after authentication or an
        // explicit reconnect, so never let the diagnostic from the last
        // attempt permanently short-circuit this one.
        if let startupError = configurationError {
            model.connectionState = .failed(startupError.localizedDescription)
            model.errorMessage = startupError.localizedDescription
            backendBootstrapState = .failed
            applyRequestedOverlayVisibility()
            refreshStatusItem()
            if launchReason == .loginItem { showSettings() }
            advanceFirstRunTransition()
            return
        }
        guard let backend, let runtimeConfiguration else {
            backendBootstrapState = .failed
            applyRequestedOverlayVisibility()
            refreshStatusItem()
            if launchReason == .loginItem { showSettings() }
            advanceFirstRunTransition()
            return
        }
        let requireExistingSession = model.preferences.onboardingComplete
            || releaseChannel.requiresAppleAuthentication
        backendConnectionStatus = nil
        model.setActiveRoomRealtimeConnected(false)
        model.connectionState = .connecting
        roomSession.bootstrapTask?.cancel()
        roomSession.bootstrapTask = Task { [weak self] in
            guard let self else { return }
            do {
                let snapshot = try await backend.boot(requireExistingSession: requireExistingSession)
                let userID = await backend.currentUserID()
                guard !Task.isCancelled else { return }
                let messagingTransport: RoomMessagingTransportRouter
                if let existing = self.messagingTransport {
                    messagingTransport = existing
                } else {
                    do {
                        let runtime = try await FirebaseV2ProductionFactory.makeRouter(
                            backend: backend,
                            configuration: runtimeConfiguration,
                            keychain: KeychainStore(
                                service: releaseChannel.keychainService,
                                session: keychainAccessSession
                            )
                        )
                        messagingTransport = runtime.router
                        self.messagingTransport = messagingTransport
                        realtimeRolloutMonitor = runtime.rolloutMonitor
                        realtimeRolloutRefreshInterval = runtime.initialRefreshInterval
                        realtimeTransportInitializationError = nil
                    } catch {
                        realtimeTransportInitializationError = error
                        throw error
                    }
                }
                // The activity monitor can have changed before the router
                // existed. Seed it before the first channel synchronization.
                try await messagingTransport.setLocalPresence(model.presence)
                startBackendEventHandling(messagingTransport.events)
                applyBackendSnapshot(snapshot, currentUserID: userID)
                if releaseChannel.requiresAppleAuthentication, userID != nil {
                    await configureAppStoreCommerce(backend: backend)
                }
                refreshCommerceState()
                let reconciliation = try await messagingTransport.synchronize(
                    rooms: snapshot.rooms,
                    activeRoomID: model.activeRoom?.id
                )
                applyBackendReconciliation(reconciliation)
                model.setActiveRoomRealtimeConnected(true)
                model.connectionState = .online
                model.errorMessage = nil
                backendBootstrapState = .ready
                roomSession.bootstrapLastSuccessAt = ContinuousClock().now
                model.preferences.keychainTransitionComplete = true
                startRealtimeRolloutMonitoring()
                applyRequestedOverlayVisibility()
                overlayWindows.refreshThrowHotspots()
                refreshStatusItem()
                persistPreferences()
                advanceFirstRunTransition()
            } catch {
                guard !Task.isCancelled else { return }
                if releaseChannel.requiresAppleAuthentication,
                   error as? SideyBackendError == .sessionRecoveryFailed {
                    model.authenticationRequired = true
                    model.connectionState = .idle
                    model.errorMessage = nil
                    backendBootstrapState = .failed
                    applyRequestedOverlayVisibility()
                    refreshStatusItem()
                    if launchReason == .loginItem { showSettings() }
                    advanceFirstRunTransition()
                    return
                }
                if FirebaseV2BootstrapRecoveryPolicy.isTransient(error) {
                    if let lastSuccess = roomSession.bootstrapLastSuccessAt,
                       lastSuccess.duration(to: ContinuousClock().now) >= .seconds(30) {
                        roomSession.bootstrapRetryCount = 0
                    }
                    roomSession.bootstrapLastSuccessAt = nil
                    roomSession.bootstrapRetryCount += 1
                    let delay = FirebaseV2BootstrapRetryPolicy.delaySeconds(
                        failureCount: roomSession.bootstrapRetryCount,
                        rateLimited: false,
                        retryAfterSeconds: nil,
                        jitter: Double.random(in: 0...1)
                    )
                    model.connectionState = .connecting
                    model.setActiveRoomRealtimeConnected(false)
                    refreshStatusItem()
                    do {
                        try await Task.sleep(for: .seconds(delay))
                    } catch {
                        return
                    }
                    guard !Task.isCancelled else { return }
                    startBackend()
                    return
                }
                let message = error.localizedDescription
                model.connectionState = .failed(message)
                model.errorMessage = L10n.format("backend.connect.failed_detail", message)
                backendBootstrapState = .failed
                applyRequestedOverlayVisibility()
                overlayWindows.refreshThrowHotspots()
                refreshStatusItem()
                if launchReason == .loginItem { showSettings() }
                advanceFirstRunTransition()
            }
        }
    }

    func startRealtimeRolloutMonitoring() {
        guard roomSession.rolloutTask == nil,
              let monitor = realtimeRolloutMonitor,
              let messagingTransport else { return }
        roomSession.rolloutTask = Task { [weak self] in
            guard let self else { return }
            let clock = ContinuousClock()
            var policyDeadline = clock.now.advanced(
                by: min(max(realtimeRolloutRefreshInterval, .seconds(1)), .seconds(300))
            )
            var observedFirebaseLease = false
            var consecutiveFailures = 0
            var lastSuccessAt: ContinuousClock.Instant?
            defer { roomSession.rolloutTask = nil }
            while !Task.isCancelled {
                do {
                    let lease = try await messagingTransport.rolloutLeaseStatus()
                    if lease != nil {
                        observedFirebaseLease = true
                    }
                    let now = clock.now
                    let policyDelay = max(.zero, now.duration(to: policyDeadline))
                    let leaseDelay = RealtimeRolloutTiming.leaseDelay(
                        lease,
                        observedFirebaseLease: observedFirebaseLease,
                        fallback: policyDelay
                    )
                    let wakeDelay = max(
                        .milliseconds(1),
                        min(policyDelay, leaseDelay)
                    )
                    try await Task.sleep(for: wakeDelay)

                    // A Duration snapshot does not count down while this task
                    // sleeps. Re-read the transport so wake-from-sleep and
                    // network stalls cannot consume the lease safety margin.
                    let currentLease = try await messagingTransport.rolloutLeaseStatus()
                    let recoveringFromClosedLease = currentLease == nil
                        && observedFirebaseLease
                    let leaseDue = RealtimeRolloutTiming.isLeaseDue(
                        currentLease,
                        observedFirebaseLease: observedFirebaseLease
                    )
                    let policyDue = clock.now >= policyDeadline
                    guard leaseDue || policyDue else { continue }

                    // Renewal authority is selector-first. Only an explicit
                    // enabled decision can mint/install another Firebase lease.
                    let renewal = try await RealtimeRolloutRenewal.perform(
                        leaseDue: leaseDue,
                        refreshPolicy: { try await monitor.refresh() },
                        refreshLease: {
                            try await messagingTransport.refreshRolloutLease()
                        }
                    )
                    let decision = renewal.decision
                    lastSuccessAt = clock.now
                    policyDeadline = clock.now.advanced(
                        by: min(
                            max(.seconds(decision.cacheTTLSeconds), .seconds(1)),
                            .seconds(300)
                        )
                    )
                    if decision.state == .legacy {
                        stopAllTyping()
                        _ = try await messagingTransport.switchToLegacy()
                        model.connectionState = .online
                        model.setActiveRoomRealtimeConnected(true)
                        refreshStatusItem()
                        return
                    }
                    if renewal.leaseRenewed, recoveringFromClosedLease {
                        model.connectionState = .online
                        model.setActiveRoomRealtimeConnected(true)
                        model.errorMessage = nil
                        refreshStatusItem()
                    }
                } catch {
                    // URLSession and SDK operations can report their own
                    // CancellationError during connection churn. Only the
                    // rollout task's actual cancellation may stop monitoring.
                    if RealtimeRolloutTiming.shouldStopAfterError(
                        error,
                        taskIsCancelled: Task.isCancelled
                    ) {
                        return
                    }
                    // A selector outage or malformed enabled response must not
                    // authorize a downgrade. The active Firebase adapter stays
                    // selected; server dispatch and Rules retain the hard stop.
                    let activeTransport = (try? await messagingTransport.currentSelection())?.active
                    if activeTransport != .firebaseV2 {
                        model.connectionState = .failed(error.localizedDescription)
                        model.setActiveRoomRealtimeConnected(false)
                        model.errorMessage = L10n.format(
                            "realtime.kill_switch.transition_failed",
                            error.localizedDescription
                        )
                        refreshStatusItem()
                        return
                    }
                    if let bootstrapError = error as? FirebaseV2BootstrapClientError,
                       case .server(.rolloutDisabled) = bootstrapError {
                        await messagingTransport.failClosedForRolloutLease()
                        model.connectionState = .failed(error.localizedDescription)
                        model.setActiveRoomRealtimeConnected(false)
                        model.errorMessage = error.localizedDescription
                        refreshStatusItem()
                    }
                    if let lastSuccessAt,
                       lastSuccessAt.duration(to: clock.now) >= .seconds(30) {
                        consecutiveFailures = 0
                    }
                    consecutiveFailures = min(consecutiveFailures + 1, 17)
                    lastSuccessAt = nil
                    let delay = FirebaseV2BootstrapRetryPolicy.delaySeconds(
                        failureCount: consecutiveFailures,
                        rateLimited: false,
                        retryAfterSeconds: nil,
                        jitter: Double.random(in: 0...1)
                    )
                    // Keep the last explicitly enabled Firebase lease selected,
                    // retry within a bounded delay, and let its monotonic hard deadline close
                    // every outbound plane if authority cannot be renewed.
                    do {
                        try await Task.sleep(for: .seconds(delay))
                    } catch {
                        return
                    }
                }
            }
        }
    }

    func startBackendEventHandling(_ events: AsyncStream<BackendEvent>) {
        // Cancelling an AsyncStream consumer terminates the shared stream. Keep
        // this task alive across authentication/bootstrap retries until shutdown.
        guard roomSession.eventTask == nil else { return }
        roomSession.eventTask = Task { [weak self] in
            defer { self?.roomSession.eventTask = nil }
            for await event in events {
                guard !Task.isCancelled, let self else { return }
                self.handleBackendEvent(event)
            }
        }
    }

    func saveProfile() {
        guard let backend else { return }
        let hadProfile = model.hasProfile
        guard !hadProfile || model.hasNicknameChanges else { return }
        let nickname = model.normalizedNicknameDraft
        guard ProfileValidator.isValidNickname(model.nickname) else { return }
        let characterID = PixelCharacterCatalog.canonicalID(for: model.selectedCharacterID)
        guard model.isCharacterSelectable(characterID) else {
            model.errorMessage = L10n.text("profile.error.character_not_owned")
            model.selectedCharacterID = PixelCharacterCatalog.pixelHamsterID
            return
        }
        guard !model.isWorking, model.groupOperation == .idle else { return }
        model.isWorking = true
        model.errorMessage = nil
        model.dismissSuccess()
        Task { [weak self] in
            guard let self else { return }
            defer { model.isWorking = false }
            do {
                let profile = try await backend.upsertProfile(
                    nickname: nickname,
                    characterID: characterID
                )
                model.apply(profile: profile)
                model.presentSuccess(L10n.text(
                    hadProfile ? "profile.success.nickname_updated" : "profile.success.saved"
                ))
                applyRequestedOverlayVisibility()
                refreshStatusItem()
                persistPreferences()
            } catch {
                model.errorMessage = SideyBackendError.normalized(error).localizedDescription
            }
        }
    }

    func setCharacter(_ requestedCharacterID: String) {
        guard let backend,
              let confirmedNickname = model.confirmedNickname,
              !model.isWorking,
              model.groupOperation == .idle,
              commerceSession.characterTask == nil,
              model.beginCharacterEquipmentRequest(characterID: requestedCharacterID)
        else { return }
        let characterID = PixelCharacterCatalog.canonicalID(for: requestedCharacterID)
        let displayName = PixelCharacterCatalog.definition(for: characterID).displayName
        model.isWorking = true
        model.errorMessage = nil
        model.dismissSuccess()
        commerceSession.characterTask = Task { [weak self] in
            guard let self else { return }
            defer {
                model.isWorking = false
                model.endCharacterEquipmentRequest()
                commerceSession.characterTask = nil
            }
            do {
                let profile = try await backend.upsertProfile(
                    nickname: confirmedNickname,
                    characterID: characterID
                )
                guard profile.id == model.currentUserID else {
                    throw SideyBackendError.malformedResponse
                }
                model.apply(profile: profile)
                model.presentSuccess(L10n.format("commerce.character.equip.success", displayName))
                model.errorMessage = nil
                applyRequestedOverlayVisibility()
                refreshStatusItem()
                persistPreferences()
            } catch is CancellationError {
                return
            } catch {
                model.errorMessage = L10n.format(
                    "commerce.character.equip.failed",
                    SideyBackendError.normalized(error).localizedDescription
                )
            }
        }
    }

    func createRoom() {
        guard let backend, let messagingTransport else { return }
        let roomName = model.newRoomName
        let characterID = PixelCharacterCatalog.canonicalID(for: model.selectedCharacterID)
        guard model.isCharacterSelectable(characterID) else {
            model.errorMessage = L10n.text("profile.error.character_not_owned")
            return
        }
        runMutation(groupOperation: .creating) {
            _ = try await backend.upsertProfile(
                nickname: self.model.confirmedNickname ?? self.model.normalizedNicknameDraft,
                characterID: characterID
            )
            let mutation = try await RealtimeGrantAwareMutation.perform(
                selection: try await messagingTransport.currentSelection(),
                requiresEntitlement: false,
                legacy: {
                    try await backend.createRoom(name: roomName)
                },
                firebaseV2: {
                    let grant = try await backend.createRoomV2(name: roomName)
                    return (grant.room, grant.accessRevision)
                },
                converge: { revision, requiresEntitlement in
                    try await messagingTransport.convergeAccessGrant(
                        revision: revision,
                        requiresEntitlement: requiresEntitlement
                    )
                }
            )
            let created = mutation.value
            self.model.lastCreatedInviteCode = created.inviteCode
            if !created.storedInKeychain {
                self.model.errorMessage = L10n.text("group.create.keychain_warning")
            }
            self.model.newRoomName = ""
            if let grantError = mutation.grantError {
                self.model.errorMessage = L10n.format(
                    "group.create.realtime_grant_pending",
                    grantError.localizedDescription
                )
            } else {
                self.model.preferences.activeRoomID = created.roomID
            }
        }
    }

    func joinRoom() {
        guard let backend, let messagingTransport else { return }
        let inviteCode = model.inviteCode
        let characterID = PixelCharacterCatalog.canonicalID(for: model.selectedCharacterID)
        guard model.isCharacterSelectable(characterID) else {
            model.errorMessage = L10n.text("profile.error.character_not_owned")
            return
        }
        runMutation(groupOperation: .joining) {
            _ = try await backend.upsertProfile(
                nickname: self.model.confirmedNickname ?? self.model.normalizedNicknameDraft,
                characterID: characterID
            )
            let mutation = try await RealtimeGrantAwareMutation.perform(
                selection: try await messagingTransport.currentSelection(),
                requiresEntitlement: false,
                legacy: {
                    try await backend.joinRoom(inviteCode: inviteCode)
                },
                firebaseV2: {
                    let grant = try await backend.joinRoomV2(inviteCode: inviteCode)
                    return (grant.room, grant.accessRevision)
                },
                converge: { revision, requiresEntitlement in
                    try await messagingTransport.convergeAccessGrant(
                        revision: revision,
                        requiresEntitlement: requiresEntitlement
                    )
                }
            )
            let joined = mutation.value
            if !joined.storedInKeychain {
                self.model.errorMessage = L10n.text("group.join.keychain_warning")
            }
            self.model.inviteCode = ""
            if let grantError = mutation.grantError {
                self.model.errorMessage = L10n.format(
                    "group.join.realtime_grant_pending",
                    grantError.localizedDescription
                )
            } else {
                self.model.preferences.activeRoomID = joined.roomID
            }
        }
    }

    func renameRoom(_ roomID: UUID, name: String) {
        guard let backend else { return }
        runMutation(successMessage: L10n.text("group.rename.success")) {
            try await backend.renameRoom(roomID, name: name)
        }
    }

    func removeRoomMember(_ roomID: UUID, userID: UUID) {
        guard let backend else { return }
        let nickname = model.rooms
            .first(where: { $0.id == roomID })?
            .members.first(where: { $0.userID == userID })?
            .nickname ?? L10n.text("profile.nickname.unknown_member")
        runMutation(successMessage: L10n.format("group.remove_member.success", nickname)) {
            try await backend.removeRoomMember(roomID, userID: userID)
        }
    }

    func leaveRoom(_ roomID: UUID) {
        if model.activeRoom?.id == roomID { stopAllTyping() }
        guard let backend else { return }
        let roomName = model.rooms.first(where: { $0.id == roomID })?.name
            ?? L10n.text("group.name.unknown")
        runMutation(successMessage: L10n.format("group.leave.success", roomName)) {
            try await backend.leaveRoom(roomID)
        }
    }

    func deleteRoom(_ roomID: UUID) {
        if model.activeRoom?.id == roomID { stopAllTyping() }
        guard let backend else { return }
        let roomName = model.rooms.first(where: { $0.id == roomID })?.name
            ?? L10n.text("group.name.unknown")
        runMutation(successMessage: L10n.format("group.delete.success", roomName)) {
            try await backend.deleteRoom(roomID)
        }
    }

    func selectRoom(_ roomID: UUID) {
        guard model.rooms.contains(where: { $0.id == roomID }), !model.isWorking else { return }
        switch model.groupOperation {
        case .creating, .joining:
            return
        case .switching(let targetRoomID) where targetRoomID == roomID:
            return
        case .idle, .switching:
            break
        }
        if model.activeRoom?.id == roomID, model.groupOperation == .idle { return }
        overlayWindows.dismissComposer()
        overlayWindows.invalidateThrowInteraction()
        stopAllTyping()
        model.errorMessage = nil
        model.historySendError = nil
        roomSession.switchPipeline.request(roomID)
    }

    func loadMessages(from backend: SideyBackend, roomID: UUID?) async throws {
        guard let roomID else {
            model.clearBubbles()
            return
        }
        let messages = try await backend.recentMessages(roomID: roomID)
        model.replaceMessages(roomID: roomID, with: messages)
    }

    func commitRoomSwitch(roomID: UUID, messages: [ChatMessage]) {
        guard model.rooms.contains(where: { $0.id == roomID }) else { return }
        model.replaceMessages(roomID: roomID, with: messages)
        model.preferences.activeRoomID = roomID
        model.markRoomRead(roomID)
        model.clearBubbles()
        model.connectionState = .online
        model.errorMessage = nil
        overlayWindows.invalidateThrowInteraction()
        refreshStatusItem()
        persistPreferences()
    }

    func handleRoomSwitchFailure(_ error: any Error, restoreError: (any Error)?) {
        if let restoreError {
            let message = restoreError.localizedDescription
            model.connectionState = .failed(message)
            model.setActiveRoomRealtimeConnected(false)
            model.errorMessage = L10n.format("backend.realtime_recovery_failed", message)
        } else {
            model.errorMessage = L10n.format(
                "group.switch.failed",
                error.localizedDescription
            )
        }
        refreshStatusItem()
    }

    func sendMessage(_ body: String, source: MessageInputSource = .overlay) {
        guard let messagingTransport else {
            stopAllTyping()
            rejectMessage(body, source: source, message: SideyBackendError.noActiveRoom.localizedDescription)
            return
        }
        guard model.activeRoomRealtimeAvailable else {
            stopAllTyping()
            rejectMessage(
                body,
                source: source,
                message: L10n.text("message.error.realtime_disconnected")
            )
            return
        }
        sendMessage(body, source: source) { roomID, body, messageID in
            await messagingTransport.sendChat(roomID: roomID, body: body, id: messageID)
        }
    }

    func sendMessage(
        _ body: String,
        source: MessageInputSource,
        send: @escaping (UUID, String, UUID) async throws -> ChatMessage
    ) {
        sendMessage(body, source: source) { roomID, body, messageID in
            do {
                return .confirmed(try await send(roomID, body, messageID))
            } catch {
                // Test seams and the legacy path keep their existing terminal
                // failure behavior. Firebase v2 returns a classified outcome.
                return .definitelyRejected(message: error.localizedDescription)
            }
        }
    }

    func sendMessage(
        _ body: String,
        source: MessageInputSource,
        send: @escaping (UUID, String, UUID) async -> RealtimeChatOutcome
    ) {
        stopAllTyping()
        // Guard again at the transport boundary: callers must not send to the
        // previously active room while a room switch is in flight.
        guard model.groupOperation == .idle, !model.isWorking else {
            rejectMessage(
                body,
                source: source,
                message: L10n.text("message.error.wait_for_group_switch")
            )
            return
        }
        guard let roomID = model.activeRoom?.id else {
            rejectMessage(body, source: source, message: SideyBackendError.noActiveRoom.localizedDescription)
            return
        }
        guard let senderID = model.currentUserID else {
            rejectMessage(
                body,
                source: source,
                message: L10n.text("backend.error.current_user_unavailable")
            )
            return
        }
        let messageID = UUID()
        let revealMessage = !model.preferences.quietModeEnabled
        model.historySendError = nil
        model.stageMessage(
            id: messageID,
            roomID: roomID,
            senderID: senderID,
            body: body,
            revealBubble: revealMessage
        )
        if revealMessage { scheduleBubbleExpiry() }
        Task { [weak self] in
            guard let self else { return }
            switch await send(roomID, body, messageID) {
            case .confirmed(let message):
                let revealConfirmation = revealMessage && !model.preferences.quietModeEnabled
                    && model.activeRoom?.id == roomID
                model.confirmMessage(message, revealBubble: revealConfirmation)
                if revealConfirmation { scheduleBubbleExpiry() }
                model.errorMessage = nil
            case .definitelyRejected(let message):
                let errorMessage = L10n.format(
                    "message.send.failed",
                    message
                )
                model.errorMessage = errorMessage
                _ = model.failMessage(id: messageID, roomID: roomID)
                if source == .history {
                    if model.realtimeActiveRoomID == roomID { model.historySendError = errorMessage }
                } else if model.activeRoom?.id == roomID, model.groupOperation == .idle {
                    overlayWindows.presentComposer()
                }
            case .reconciliationPending:
                // The request may already be committed. Preserve this exact
                // UUID as pending so a later authoritative snapshot/event can
                // confirm it. Retrying here could create duplicate messages.
                break
            }
        }
    }

    private func rejectMessage(_ body: String, source: MessageInputSource, message: String) {
        model.errorMessage = message
        if model.draft.isEmpty { model.draft = body }
        if source == .history {
            model.historySendError = message
        } else {
            overlayWindows.presentComposer()
        }
    }

    func runMutation(
        successMessage: String? = nil,
        groupOperation: GroupOperation? = nil,
        _ operation: @escaping @MainActor () async throws -> Void
    ) {
        guard let backend,
              let messagingTransport,
              !model.isWorking,
              model.groupOperation == .idle
        else { return }
        model.isWorking = true
        if let groupOperation { model.groupOperation = groupOperation }
        model.errorMessage = nil
        model.dismissSuccess()
        Task { [weak self] in
            guard let self else { return }
            defer {
                model.isWorking = false
                if let groupOperation, model.groupOperation == groupOperation {
                    model.groupOperation = .idle
                }
            }
            var serverMutationCommitted = false
            do {
                let wasOnboardingComplete = model.preferences.onboardingComplete
                try await operation()
                serverMutationCommitted = true
                let postCommitWarning = model.errorMessage
                let snapshot = try await backend.loadSnapshot()
                // The database mutation is durable once the snapshot loads. Apply it before
                // Realtime work so a transient channel rebuild cannot leave successful room
                // changes looking like failures or invite users to repeat the mutation.
                applyBackendSnapshot(snapshot, currentUserID: model.currentUserID)

                var realtimeWarning: String?
                do {
                    let reconciliation = try await messagingTransport.synchronize(
                        rooms: snapshot.rooms,
                        activeRoomID: model.resolvedActiveRoomID(in: snapshot.rooms)
                    )
                    applyBackendReconciliation(reconciliation)
                    model.connectionState = .online
                } catch is CancellationError where Task.isCancelled {
                    throw CancellationError()
                } catch {
                    model.connectionState = .connecting
                    model.setActiveRoomRealtimeConnected(false)
                    realtimeWarning = L10n.text("mutation.realtime_recovering")
                }
                let warnings = [postCommitWarning, realtimeWarning].compactMap { $0 }
                model.errorMessage = warnings.isEmpty ? nil : warnings.joined(separator: " ")
                if realtimeWarning == nil, let successMessage {
                    model.presentSuccess(successMessage)
                }
                if !wasOnboardingComplete && model.preferences.onboardingComplete {
                    model.activeSettingsPage = .groups
                    settingsWindow.transitionFromOnboardingToSettings()
                } else if wasOnboardingComplete && !model.preferences.onboardingComplete {
                    settingsWindow.transitionFromSettingsToOnboarding()
                }
                applyRequestedOverlayVisibility()
                refreshStatusItem()
                persistPreferences()
            } catch {
                model.dismissSuccess()
                let message = error.localizedDescription
                model.errorMessage = serverMutationCommitted
                    ? L10n.format("mutation.post_commit_sync_failed", message)
                    : message
            }
        }
    }

    func applyBackendSnapshot(_ snapshot: BackendSnapshot, currentUserID: UUID?) {
        if let activeRoomID = model.activeRoom?.id,
           !snapshot.rooms.contains(where: { $0.id == activeRoomID }) {
            overlayWindows.dismissComposer()
            stopAllTyping()
            model.clearBubbles()
        }
        model.apply(snapshot: snapshot, currentUserID: currentUserID)
        migrateTreeMovementIfNeeded()
    }

    func handleBackendEvent(_ event: BackendEvent) {
        switch event {
        case .snapshot(let snapshot):
            applyBackendSnapshot(snapshot, currentUserID: model.currentUserID)
            applyRequestedOverlayVisibility()
            refreshStatusItem()
            persistPreferences()
        case .reconciliation(let reconciliation):
            applyBackendReconciliation(reconciliation)
            applyRequestedOverlayVisibility()
            refreshStatusItem()
            persistPreferences()
        case .message(let message):
            let isActiveRoom = message.roomID == model.activeRoom?.id
            let revealMessage = isActiveRoom && !model.preferences.quietModeEnabled
            let isNew = model.confirmMessage(message, revealBubble: revealMessage)
            guard isNew else { return }
            if message.senderID != model.currentUserID && (!isActiveRoom || model.preferences.quietModeEnabled) {
                model.incrementUnread(in: message.roomID)
            }
            if revealMessage { scheduleBubbleExpiry() }
            refreshStatusItem()
        case .messageDeleted(let roomID, let messageID):
            model.removeMessage(id: messageID, roomID: roomID)
            removeHistoryMessage(id: messageID, roomID: roomID)
        case .messagesInvalidated(let roomID):
            reloadHistory(roomID: roomID)
            guard let backend else { return }
            Task { [weak self] in
                guard let self else { return }
                do {
                    let messages = try await backend.recentMessages(roomID: roomID)
                    model.replaceMessages(roomID: roomID, with: messages)
                } catch {
                    model.errorMessage = L10n.format(
                        "message.retention_sync_failed",
                        SideyBackendError.normalized(error).localizedDescription
                    )
                }
            }
        case .messagesReplaced(let roomID, let messages):
            model.replaceMessages(roomID: roomID, with: messages)
            reloadHistory(roomID: roomID)
        case .presence(let roomID, let userID, let state):
            model.updatePresence(roomID: roomID, userID: userID, state: state)
            overlayWindows.refreshThrowHotspots()
        case .typing(let roomID, let userID, let active):
            model.updateTyping(roomID: roomID, userID: userID, active: active)
        case .characterPulse(let event):
            guard event.roomID == model.activeRoom?.id,
                  model.activeRoom?.members.contains(where: { $0.userID == event.userID }) == true,
                  roomSession.pulseCooldown.accept(
                    roomID: event.roomID,
                    userID: event.userID,
                    uptime: ProcessInfo.processInfo.systemUptime
                  )
            else { return }
            overlayWindows.playCharacterPulse(event)
        case .characterThrow(let event):
            guard event.roomID == model.activeRoom?.id,
                  event.actorUserID != event.targetUserID,
                  model.activeRoom?.members.contains(where: { $0.userID == event.actorUserID }) == true,
                  model.activeRoom?.members.contains(where: { $0.userID == event.targetUserID }) == true,
                  roomSession.throwCooldown.accept(
                    actorUserID: event.actorUserID,
                    uptime: ProcessInfo.processInfo.systemUptime
                  )
            else { return }
            overlayWindows.playCharacterThrow(event)
        case .connection(let status):
            let previousStatus = backendConnectionStatus
            backendConnectionStatus = status
            model.connectionState = status.isReady ? .online : .connecting
            model.setActiveRoomRealtimeConnected(status.activeRoomTransportConnected)
            if previousStatus?.activeRoomTransportConnected == true,
               !status.activeRoomTransportConnected {
                overlayWindows.invalidateThrowInteraction()
            }
            overlayWindows.refreshThrowHotspots()
        case .technicalError(let message):
            model.errorMessage = message
        }
    }

    func applyBackendReconciliation(_ reconciliation: BackendReconciliation) {
        applyBackendSnapshot(reconciliation.snapshot, currentUserID: model.currentUserID)
        if let activeRoomID = reconciliation.activeRoomID {
            model.preferences.activeRoomID = activeRoomID
            model.replaceMessages(roomID: activeRoomID, with: reconciliation.activeMessages)
        }
    }

    func scheduleBubbleExpiry() {
        roomSession.bubbleExpiryTask?.cancel()
        roomSession.bubbleExpiryTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard !Task.isCancelled, let self else { return }
                model.dismissExpiredBubbles()
                if model.activeBubbles.isEmpty { return }
            }
        }
    }

    func localPresenceChanged(_ state: PresenceState) {
        model.presence = state
        guard let messagingTransport else { return }
        Task { [weak self] in
            // Read current state when the task runs; an older queued callback
            // must not overwrite a newer activity edge or a new login session.
            guard let self, self.messagingTransport === messagingTransport else { return }
            try? await messagingTransport.setLocalPresence(model.presence)
        }
    }

    func typingChanged(_ active: Bool, source: MessageInputSource = .overlay) {
        guard model.updateTypingInput(active: active, source: source) else { return }
        guard active, let roomID = model.activeRoom?.id else {
            typingActivity.stop()
            return
        }
        typingActivity.edited(roomID: roomID, hasText: true)
    }

    func stopAllTyping() {
        model.resetTypingInput()
        typingActivity.stop()
    }

    func characterDoubleClicked() {
        if let id = model.currentUserID, model.characterStunState.isStunned(id) { return }
        guard model.activeRoomRealtimeAvailable,
              let room = model.activeRoom,
              let userID = model.currentUserID,
              room.members.contains(where: { $0.userID == userID }),
              roomSession.pulseCooldown.accept(
                roomID: room.id,
                userID: userID,
                uptime: ProcessInfo.processInfo.systemUptime
              )
        else { return }

        let event = CharacterPulseEvent(id: UUID(), roomID: room.id, userID: userID)
        overlayWindows.playCharacterPulse(event)
        guard let messagingTransport else { return }
        Task {
            try? await messagingTransport.publishCharacterPulse(
                roomID: room.id,
                eventID: event.id
            )
        }
    }

    func characterThrowRequested(targetUserID: UUID) {
        if let id = model.currentUserID, model.characterStunState.isStunned(id) { return }
        guard model.activeRoomRealtimeAvailable,
              let room = model.activeRoom,
              let actorUserID = model.currentUserID,
              actorUserID != targetUserID,
              let actor = room.members.first(where: { $0.userID == actorUserID }),
              model.pixelWorldMembers.contains(where: {
                  $0.id == targetUserID && CharacterThrowTargetPolicy.canTarget($0)
              }),
              roomSession.throwCooldown.accept(
                  actorUserID: actorUserID,
                  uptime: ProcessInfo.processInfo.systemUptime
              )
        else { return }

        let event = CharacterThrowEvent(
            id: UUID(),
            roomID: room.id,
            actorUserID: actorUserID,
            targetUserID: targetUserID,
            sourceCharacterID: PixelCharacterCatalog.canonicalID(for: actor.characterID),
            throwableID: model.equippedThrowableID
        )
        overlayWindows.playCharacterThrow(event)
        guard let messagingTransport else { return }
        Task {
            try? await messagingTransport.publishCharacterThrow(
                roomID: room.id,
                eventID: event.id,
                targetUserID: targetUserID
            )
        }
    }

}
