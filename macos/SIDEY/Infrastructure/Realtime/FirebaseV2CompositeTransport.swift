import FirebaseAuth
import Foundation

struct FirebaseV2CompositeSession: Sendable {
    let identity: RealtimeCredentialIdentity
    let bootstrap: FirebaseV2BootstrapSuccess
}

protocol FirebaseV2CompositeCredentialEstablishing: Sendable {
    var invalidations: AsyncStream<Void> { get }

    func establish(
        trigger: RealtimeBootstrapTrigger,
        minimumAccessRevision: RealtimeRevision?
    ) async throws -> FirebaseV2CompositeSession

    func invalidate() async
}

/// Joins the existing bootstrap, Firebase Auth, and monotonic credential-lifetime
/// components without persisting Firebase credentials outside the SDK.
///
/// This type is intentionally not wired into `AppCoordinator` until the final
/// backend handoff and M0 read-back exist.
final class FirebaseV2CompositeCredentialEstablisher: @unchecked Sendable,
    FirebaseV2CompositeCredentialEstablishing {
    typealias SessionProvider = @Sendable () async throws -> FirebaseV2SupabaseSession

    private let bootstrapClient: FirebaseV2BootstrapClient
    private let authAdapter: FirebaseV2AuthAdapter
    private let sessionProvider: SessionProvider
    private let lifecycle: RealtimeCredentialLifecycle
    private let operationGate = RealtimeCredentialOperationGate()
    private let clock = ContinuousClock()
    let invalidations: AsyncStream<Void>
    private let invalidationContinuation: AsyncStream<Void>.Continuation

    @MainActor
    init(
        bootstrapClient: FirebaseV2BootstrapClient,
        auth: Auth,
        sessionProvider: @escaping SessionProvider,
        lifecycle: RealtimeCredentialLifecycle = RealtimeCredentialLifecycle()
    ) {
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        self.bootstrapClient = bootstrapClient
        self.authAdapter = FirebaseV2AuthAdapter(auth: auth) { _ in
            pair.continuation.yield(())
        }
        self.sessionProvider = sessionProvider
        self.lifecycle = lifecycle
        self.invalidations = pair.stream
        self.invalidationContinuation = pair.continuation
    }

    func establish(
        trigger: RealtimeBootstrapTrigger,
        minimumAccessRevision: RealtimeRevision?
    ) async throws -> FirebaseV2CompositeSession {
        try await operationGate.perform { [self] in
            let authAttemptID = UUID()
            do {
                try Task.checkCancellation()
                let session = try await sessionProvider()
                let attempt = try await lifecycle.beginBootstrap(
                    trigger: trigger,
                    identity: session.identity
                )
                let bootstrap = try await bootstrapClient.bootstrap(
                    accessToken: session.accessToken,
                    minimumAccessRevision: minimumAccessRevision
                )
                let authenticatedIdentity = try await authAdapter.signIn(
                    customToken: bootstrap.customToken,
                    expectedIdentity: session.identity,
                    expectedRolloutLeaseExpiresAt: bootstrap.rolloutLeaseExpiresAt,
                    attemptID: authAttemptID
                )
                let now = clock.now
                let lease = RealtimeCredentialLease(
                    identity: authenticatedIdentity,
                    handle: UUID(),
                    receivedAt: now,
                    expiresIn: .seconds(bootstrap.authTokenLifetimeSeconds)
                )
                try await lifecycle.replaceCredential(
                    lease,
                    for: attempt,
                    evaluatedAt: now,
                    commit: { _ in }
                )
                return FirebaseV2CompositeSession(
                    identity: authenticatedIdentity,
                    bootstrap: bootstrap
                )
            } catch {
                let invalidatedInstalledCredential =
                    await authAdapter.signOut(ifOwnedBy: authAttemptID)
                if invalidatedInstalledCredential {
                    invalidationContinuation.yield(())
                }
                if trigger != .explicitRecovery && trigger != .rolloutLeaseRefresh {
                    try? await authAdapter.signOut()
                    await lifecycle.invalidateForLogout()
                }
                throw error
            }
        }
    }

    func invalidate() async {
        // Revoke the lifecycle generation immediately. Waiting for an in-flight
        // network/Auth operation to release the gate first would let logout
        // commit an otherwise stale credential while shutdown is pending.
        await lifecycle.invalidateForLogout()
        await operationGate.perform { [self] in
            try? await authAdapter.signOut()
            invalidationContinuation.finish()
        }
    }
}

/// Supabase duties that remain active in Firebase v2 mode.
///
/// A conformer owns authoritative Postgres reconciliation and Presence. The
/// transient methods remain on this boundary only because the same conformer is
/// reused when the router switches back to the legacy transport; a selected
/// Firebase v2 transport never calls or forwards those Supabase transient paths.
protocol FirebaseV2SupabasePlane: Actor {
    nonisolated var events: AsyncStream<BackendEvent> { get }

    func synchronize(
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation
    func setActiveRoom(_ roomID: UUID?) async throws
    func setLocalPresence(_ state: PresenceState) async throws
    func publishTyping(roomID: UUID, event: String) async throws
    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws
    func publishCharacterThrow(
        roomID: UUID,
        eventID: UUID,
        targetUserID: UUID
    ) async throws
    func shutdown() async
}

enum FirebaseV2CompositeTransportError: LocalizedError, Equatable {
    case shutDown
    case roomNotAuthorized
    case accessGrantClosed
    case liveListenerUnavailable
    case rolloutLeaseDidNotAdvance

    var errorDescription: String? {
        switch self {
        case .shutDown:
            L10n.text("firebase.transport.error.shut_down")
        case .roomNotAuthorized:
            L10n.text("firebase.transport.error.room_not_authorized")
        case .accessGrantClosed:
            L10n.text("firebase.transport.error.access_grant_closed")
        case .liveListenerUnavailable:
            L10n.text("firebase.transport.error.live_listener_unavailable")
        case .rolloutLeaseDidNotAdvance:
            L10n.text("firebase.auth.error.rollout_lease_expired")
        }
    }
}

struct FirebaseV2CompositeDiagnostics: Equatable, Sendable {
    let activated: Bool
    let activeRoomID: UUID?
    let inboxListenerCount: Int
    let liveListenerCount: Int
    let grantOpen: Bool
    let serverTimeOffsetMilliseconds: Int64
    let shutDown: Bool
}

/// Firebase v2 transport selected only after the authenticated Supabase
/// rollout RPC confirms this exact protocol and contract hash. The production
/// router retains the shared Supabase socket for Presence, authoritative
/// reconciliation, and a remote kill-switch transition.
actor FirebaseV2CompositeTransport: RoomMessagingTransport {
    nonisolated let kind: RealtimeTransportKind = .firebaseV2
    nonisolated let events: AsyncStream<BackendEvent>

    private let eventContinuation: AsyncStream<BackendEvent>.Continuation
    private let credentials: any FirebaseV2CompositeCredentialEstablishing
    private let supabasePlane: any FirebaseV2SupabasePlane
    private let databaseValues: any FirebaseV2DatabaseValueStreaming
    private let databaseWrites: any FirebaseV2DatabaseWriteTransport
    private let chatClient: FirebaseV2ChatClient
    private let throwableCatalogIDByWireCode: [FirebaseV2WireCode: String]
    private let throwableWireCodeByCatalogID: [String: FirebaseV2WireCode]
    private let nowMilliseconds: @Sendable () -> Int64
    private let serverTimeOffsets: (any FirebaseV2ServerTimeOffsetStreaming)?
    private var serverClock = FirebaseV2ServerClock()
    private var serverClockTask: Task<Void, Never>?
    private let liveReadinessTimeout: Duration
    private let credentialReplacementGate = RealtimeCredentialOperationGate()
    private let clock = ContinuousClock()

    private var session: FirebaseV2CompositeSession?
    private var currentRooms: [Room] = []
    private var currentReconciliation: BackendReconciliation?
    private var transientWriter: FirebaseV2TransientWriter?
    private var activeRoomState = RealtimeActiveRoomState()
    private var grantBarrier = RealtimeGrantBarrier()
    private var inboxReconciler = FirebaseV2InboxReconciler()
    private var liveReconciler = FirebaseV2LiveReconciler()
    private var inboxTask: Task<Void, Never>?
    private var liveTasksByRoom: [UUID: Task<Void, Never>] = [:]
    private var latestLiveDataByRoom: [UUID: Data] = [:]
    private var supabaseEventTask: Task<Void, Never>?
    private var typingExpiryTask: Task<Void, Never>?
    private var credentialInvalidationTask: Task<Void, Never>?
    private var rolloutLeaseExpiryTask: Task<Void, Never>?
    private var retirementTask: Task<Void, Never>?
    private var rolloutLeaseRefreshDeadline: ContinuousClock.Instant?
    private var rolloutLeaseExpiryDeadline: ContinuousClock.Instant?
    private var liveReadinessContinuation: AsyncThrowingStream<Void, Error>.Continuation?
    private var pendingInitialLiveActions: [FirebaseV2LiveAction] = []
    private var inboxListenerGeneration: UInt64 = 0
    private var listenerGeneration: UInt64 = 0
    private var committedListenerGeneration: UInt64?
    private var roomTransitionGeneration: UInt64 = 0
    private var rolloutLeaseGeneration: UInt64 = 0
    private var isReconcilingHint = false
    private var needsAnotherHintReconciliation = false
    private var pendingMinimumAccessRevision: RealtimeRevision?
    private var isShutDown = false

    init(
        credentials: any FirebaseV2CompositeCredentialEstablishing,
        supabasePlane: any FirebaseV2SupabasePlane,
        databaseValues: any FirebaseV2DatabaseValueStreaming,
        databaseWrites: any FirebaseV2DatabaseWriteTransport,
        chatClient: FirebaseV2ChatClient,
        throwableCatalogIDByWireCode: [FirebaseV2WireCode: String],
        serverTimeOffsets: (any FirebaseV2ServerTimeOffsetStreaming)? = nil,
        liveReadinessTimeout: Duration = .seconds(10),
        nowMilliseconds: @escaping @Sendable () -> Int64 = {
            Int64(Date().timeIntervalSince1970 * 1_000)
        }
    ) {
        let pair = AsyncStream<BackendEvent>.makeStream(
            bufferingPolicy: .bufferingNewest(256)
        )
        self.events = pair.stream
        self.eventContinuation = pair.continuation
        self.credentials = credentials
        self.supabasePlane = supabasePlane
        self.databaseValues = databaseValues
        self.databaseWrites = databaseWrites
        self.chatClient = chatClient
        self.throwableCatalogIDByWireCode = throwableCatalogIDByWireCode
        self.throwableWireCodeByCatalogID = Dictionary(
            uniqueKeysWithValues: throwableCatalogIDByWireCode.map { ($0.value, $0.key) }
        )
        self.liveReadinessTimeout = liveReadinessTimeout
        self.nowMilliseconds = nowMilliseconds
        self.serverTimeOffsets = serverTimeOffsets
    }

    func synchronize(
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation {
        try requireRunning()
        _ = try await activateIfNeeded()
        let reconciliation = try await supabasePlane.synchronize(
            rooms: rooms,
            activeRoomID: activeRoomID
        )
        currentRooms = reconciliation.snapshot.rooms
        currentReconciliation = reconciliation
        pruneLiveListeners(authorizedRoomIDs: Set(currentRooms.map(\.id)))

        // A create/join/equip mutation may already be committed even when its
        // credential refresh failed. Keep that exact requested revision closed
        // and retry only grant convergence on the next normal synchronization;
        // never replay the mutation itself.
        let pendingGrantRevision = grantBarrier.pendingRevision
        if let pendingGrantRevision,
           let currentAccessRevision = session?.bootstrap.accessRevision,
           currentAccessRevision < pendingGrantRevision {
            try await recoverCredentials(minimumAccessRevision: pendingGrantRevision)
        }
        guard let synchronizedSession = session else {
            throw FirebaseV2CompositeTransportError.accessGrantClosed
        }
        try validateRoom(activeRoomID, session: synchronizedSession)
        startSupabaseEventForwardingIfNeeded()
        startInboxListenerIfNeeded(for: synchronizedSession.identity.accountID)
        try await replaceActiveRoom(activeRoomID, session: synchronizedSession)
        if let pendingGrantRevision {
            grantBarrier.acknowledge(
                revision: pendingGrantRevision,
                membershipValid: true,
                entitlementValid: true
            )
        }
        startWarmLiveListeners(session: synchronizedSession)
        return reconciliation
    }

    func setActiveRoom(_ roomID: UUID?) async throws {
        try requireRunning()
        guard let session else { throw FirebaseV2CompositeTransportError.accessGrantClosed }
        try validateRoom(roomID, session: session)
        try await replaceActiveRoom(roomID, session: session)
    }

    func setLocalPresence(_ state: PresenceState) async throws {
        try requireReady()
        try await supabasePlane.setLocalPresence(state)
    }

    func publishTyping(roomID: UUID, event: String) async throws {
        try requireReady(roomID: roomID)
        guard let transientWriter else {
            throw FirebaseV2CompositeTransportError.liveListenerUnavailable
        }
        try await transientWriter.publishTyping(roomID: roomID, event: event)
    }

    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        try requireReady(roomID: roomID)
        guard let transientWriter else {
            throw FirebaseV2CompositeTransportError.liveListenerUnavailable
        }
        try await transientWriter.publishPulse(roomID: roomID)
    }

    func publishCharacterThrow(
        roomID: UUID,
        eventID: UUID,
        targetUserID: UUID
    ) async throws {
        try requireReady(roomID: roomID)
        guard let transientWriter else {
            throw FirebaseV2CompositeTransportError.liveListenerUnavailable
        }
        let catalogItemID = currentReconciliation?.snapshot.profile?.equippedThrowableID
            ?? "patch_soft_ball"
        try await transientWriter.publishThrow(
            roomID: roomID,
            targetUserID: targetUserID,
            catalogItemID: catalogItemID
        )
    }

    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome {
        do {
            try requireReady(roomID: roomID)
        } catch {
            return .definitelyRejected(message: error.localizedDescription)
        }
        return await chatClient.send(roomID: roomID, body: body, messageID: id)
    }

    func shutdown() async {
        await retire(shutdownSupabase: true)
    }

    func retireForTransportSwitch() async {
        await retire(shutdownSupabase: false)
    }

    private func retire(
        shutdownSupabase: Bool,
        withdrawPresence: Bool = false
    ) async {
        if let retirementTask {
            await retirementTask.value
            return
        }
        guard !isShutDown else { return }
        isShutDown = true
        inboxListenerGeneration &+= 1
        listenerGeneration &+= 1
        roomTransitionGeneration &+= 1
        inboxTask?.cancel()
        serverClockTask?.cancel()
        serverClockTask = nil
        cancelAllLiveListeners()
        supabaseEventTask?.cancel()
        typingExpiryTask?.cancel()
        credentialInvalidationTask?.cancel()
        rolloutLeaseGeneration &+= 1
        rolloutLeaseExpiryTask?.cancel()
        liveReadinessContinuation?.finish(throwing: CancellationError())
        inboxTask = nil
        supabaseEventTask = nil
        typingExpiryTask = nil
        credentialInvalidationTask = nil
        rolloutLeaseExpiryTask = nil
        rolloutLeaseRefreshDeadline = nil
        rolloutLeaseExpiryDeadline = nil
        liveReadinessContinuation = nil
        pendingInitialLiveActions = []
        transientWriter = nil
        session = nil
        activeRoomState.invalidateAll()
        grantBarrier.revokeSession()
        let credentials = self.credentials
        let supabasePlane = self.supabasePlane
        let eventContinuation = self.eventContinuation
        let task = Task {
            if shutdownSupabase {
                await supabasePlane.shutdown()
            } else if withdrawPresence {
                try? await supabasePlane.setActiveRoom(nil)
            }
            await credentials.invalidate()
            eventContinuation.finish()
        }
        retirementTask = task
        await task.value
    }

    func diagnostics() -> FirebaseV2CompositeDiagnostics {
        FirebaseV2CompositeDiagnostics(
            activated: session != nil,
            activeRoomID: activeRoomState.committedRealtimeActiveRoomID,
            inboxListenerCount: inboxTask == nil ? 0 : 1,
            liveListenerCount: liveTasksByRoom.count,
            grantOpen: grantBarrier.isOpen,
            serverTimeOffsetMilliseconds: serverClock.offsetMilliseconds,
            shutDown: isShutDown
        )
    }

    func rolloutLeaseStatus() -> RealtimeRolloutLeaseStatus? {
        guard !isShutDown,
              let rolloutLeaseRefreshDeadline,
              let rolloutLeaseExpiryDeadline else { return nil }
        let now = clock.now
        return RealtimeRolloutLeaseStatus(
            refreshIn: max(.zero, now.duration(to: rolloutLeaseRefreshDeadline)),
            expiresIn: max(.zero, now.duration(to: rolloutLeaseExpiryDeadline))
        )
    }

    func refreshRolloutLease() async throws {
        try requireRunning()
        try await credentialReplacementGate.perform { [self] in
            try await replaceCredentialSession(
                trigger: .rolloutLeaseRefresh,
                minimumAccessRevision: nil,
                requireExtendedLease: true
            )
        }
    }

    func failClosedForRolloutLease() async {
        await failClosed(
            message: L10n.text("firebase.bootstrap.error.rollout_disabled")
        )
    }

    func beginGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) {
        grantBarrier.request(
            revision: revision,
            requiresEntitlement: requiresEntitlement
        )
    }

    func convergeGrant(
        revision: RealtimeRevision,
        membershipValid: Bool,
        entitlementValid: Bool
    ) async throws {
        try requireRunning()
        guard let session else {
            throw FirebaseV2CompositeTransportError.accessGrantClosed
        }
        if session.bootstrap.accessRevision < revision {
            try await recoverCredentials(minimumAccessRevision: revision)
        }
        grantBarrier.acknowledge(
            revision: revision,
            membershipValid: membershipValid,
            entitlementValid: entitlementValid
        )
        if let currentSession = self.session {
            startWarmLiveListeners(session: currentSession)
        }
    }

    func convergeAccessGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) async throws {
        beginGrant(
            revision: revision,
            requiresEntitlement: requiresEntitlement
        )
        try await convergeGrant(
            revision: revision,
            membershipValid: true,
            entitlementValid: true
        )
    }

    func revokeMembershipGrant() {
        grantBarrier.revokeMembership()
        retireActiveRoomResources()
    }

    func revokeEntitlementGrant() {
        grantBarrier.revokeEntitlement()
    }

    private func activateIfNeeded() async throws -> FirebaseV2CompositeSession {
        if let session { return session }
        let established = try await credentials.establish(
            trigger: .sessionRestore,
            minimumAccessRevision: nil
        )
        session = established
        startServerClockIfNeeded()
        scheduleRolloutLeaseExpiry(for: established)
        startCredentialInvalidationMonitoringIfNeeded()
        grantBarrier.request(
            revision: established.bootstrap.accessRevision,
            requiresEntitlement: false
        )
        grantBarrier.acknowledge(
            revision: established.bootstrap.accessRevision,
            membershipValid: true,
            entitlementValid: true
        )
        return established
    }

    private func startServerClockIfNeeded() {
        guard serverClockTask == nil, let serverTimeOffsets else { return }
        let offsets = serverTimeOffsets.offsets()
        serverClockTask = Task { [weak self] in
            for await offset in offsets {
                guard !Task.isCancelled else { return }
                await self?.updateServerClock(offset)
            }
        }
    }

    private func updateServerClock(_ offsetMilliseconds: Int64) {
        guard !isShutDown else { return }
        serverClock.offsetMilliseconds = offsetMilliseconds
        // Typing deadlines use the same estimated server time as live events.
        // An offset update must also replace the old local timer delay.
        if let roomID = activeRoomState.committedRealtimeActiveRoomID {
            scheduleTypingExpiry(roomID: roomID, generation: listenerGeneration)
        }
    }

    private func serverNowMilliseconds() -> Int64 {
        serverClock.estimatedTime(localMilliseconds: nowMilliseconds())
    }

    private func startCredentialInvalidationMonitoringIfNeeded() {
        guard credentialInvalidationTask == nil else { return }
        let stream = credentials.invalidations
        credentialInvalidationTask = Task { [weak self] in
            for await _ in stream {
                guard !Task.isCancelled else { return }
                await self?.credentialWasInvalidated()
                return
            }
        }
    }

    private func credentialWasInvalidated() async {
        guard !isShutDown else { return }
        await failClosed(message: L10n.text("firebase.auth.error.login_mismatch"))
    }

    private func scheduleRolloutLeaseExpiry(for session: FirebaseV2CompositeSession) {
        rolloutLeaseGeneration &+= 1
        let generation = rolloutLeaseGeneration
        rolloutLeaseExpiryTask?.cancel()
        let schedule = session.bootstrap.rolloutLeaseSchedule
        let referenceMilliseconds = schedule.referenceMilliseconds ?? nowMilliseconds()
        let refreshDelayMilliseconds = max(
            0,
            schedule.refreshAfterMilliseconds - referenceMilliseconds
        )
        let expiryDelayMilliseconds = max(
            0,
            schedule.expiresAtMilliseconds - referenceMilliseconds
        )
        let installedAt = clock.now
        rolloutLeaseRefreshDeadline = installedAt.advanced(
            by: .milliseconds(refreshDelayMilliseconds)
        )
        let expiryDeadline = installedAt.advanced(
            by: .milliseconds(expiryDelayMilliseconds)
        )
        rolloutLeaseExpiryDeadline = expiryDeadline
        let clock = self.clock
        rolloutLeaseExpiryTask = Task { [weak self] in
            do {
                try await clock.sleep(until: expiryDeadline)
            } catch {
                return
            }
            await self?.rolloutLeaseExpired(generation: generation)
        }
    }

    private func rolloutLeaseExpired(generation: UInt64) async {
        guard generation == rolloutLeaseGeneration, !isShutDown else { return }
        await failClosedForRolloutLease()
    }

    private func cancelAllLiveListeners() {
        for task in liveTasksByRoom.values { task.cancel() }
        liveTasksByRoom.removeAll()
        latestLiveDataByRoom.removeAll()
    }

    private func pruneLiveListeners(authorizedRoomIDs: Set<UUID>) {
        for roomID in Array(liveTasksByRoom.keys)
        where !authorizedRoomIDs.contains(roomID) {
            liveTasksByRoom.removeValue(forKey: roomID)?.cancel()
            latestLiveDataByRoom.removeValue(forKey: roomID)
        }
    }

    private func startWarmLiveListeners(session: FirebaseV2CompositeSession) {
        guard !isShutDown, grantBarrier.isOpen else { return }
        for room in currentRooms.prefix(5)
        where session.bootstrap.rooms.contains(room.id) {
            startLiveListenerIfNeeded(for: room.id)
        }
    }

    private func startLiveListenerIfNeeded(for roomID: UUID) {
        guard liveTasksByRoom[roomID] == nil else { return }
        let generation = listenerGeneration
        let stream = databaseValues.values(at: FirebaseV2Path.liveRoom(roomID))
        liveTasksByRoom[roomID] = Task { [weak self] in
            do {
                for try await data in stream {
                    guard !Task.isCancelled else { return }
                    await self?.receiveLiveData(
                        data,
                        roomID: roomID,
                        generation: generation
                    )
                }
                guard !Task.isCancelled else { return }
                await self?.listenerFailed(
                    generation: generation,
                    roomID: roomID
                )
            } catch is CancellationError {
                return
            } catch {
                await self?.listenerFailed(
                    generation: generation,
                    roomID: roomID
                )
            }
        }
    }

    private func replaceActiveRoom(
        _ roomID: UUID?,
        session: FirebaseV2CompositeSession,
        forceListenerRestart: Bool = false
    ) async throws {
        roomTransitionGeneration &+= 1
        let transitionGeneration = roomTransitionGeneration
        let operation = activeRoomState.request(roomID)
        try await supabasePlane.setActiveRoom(roomID)
        try requireRunning()
        guard transitionGeneration == roomTransitionGeneration else {
            throw CancellationError()
        }

        // Authoritative reconciliation can run for chat and room hints while
        // the active room is unchanged. Keep its healthy RTDB listener alive:
        // cancelling it on every hint creates a gap for short-lived throws.
        if !forceListenerRestart,
           let roomID,
           activeRoomState.committedRealtimeActiveRoomID == roomID,
           committedListenerGeneration == listenerGeneration,
           liveTasksByRoom[roomID] != nil,
           liveReadinessContinuation == nil {
            guard activeRoomState.commit(operation) else { throw CancellationError() }
            transientWriter = makeTransientWriter(roomID: roomID, session: session)
            return
        }

        if forceListenerRestart {
            listenerGeneration &+= 1
            cancelAllLiveListeners()
        }
        let generation = listenerGeneration
        typingExpiryTask?.cancel()
        liveReadinessContinuation?.finish(throwing: CancellationError())
        typingExpiryTask = nil
        liveReadinessContinuation = nil
        pendingInitialLiveActions = []
        liveReconciler = FirebaseV2LiveReconciler()
        guard let roomID else {
            transientWriter = nil
            _ = activeRoomState.commit(operation)
            return
        }

        let readiness = AsyncThrowingStream<Void, Error>.makeStream(
            bufferingPolicy: .bufferingNewest(1)
        )
        liveReadinessContinuation = readiness.continuation
        startLiveListenerIfNeeded(for: roomID)
        if let latestData = latestLiveDataByRoom[roomID] {
            await receiveLiveData(latestData, roomID: roomID, generation: generation)
        }
        do {
            try await waitForLiveReadiness(readiness.stream, generation: generation)
        } catch {
            if generation == listenerGeneration,
               transitionGeneration == roomTransitionGeneration {
                liveTasksByRoom.removeValue(forKey: roomID)?.cancel()
                latestLiveDataByRoom.removeValue(forKey: roomID)
                liveReadinessContinuation = nil
                pendingInitialLiveActions = []
            }
            throw error
        }
        guard transitionGeneration == roomTransitionGeneration else {
            throw CancellationError()
        }
        guard activeRoomState.commit(operation) else {
            throw CancellationError()
        }
        committedListenerGeneration = generation
        transientWriter = makeTransientWriter(roomID: roomID, session: session)
        await drainPendingInitialLiveActions(roomID: roomID, generation: generation)
        if let latestData = latestLiveDataByRoom[roomID] {
            await receiveLiveData(latestData, roomID: roomID, generation: generation)
        }
    }

    private func waitForLiveReadiness(
        _ readiness: AsyncThrowingStream<Void, Error>,
        generation: UInt64
    ) async throws {
        let timeout = liveReadinessTimeout
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                var iterator = readiness.makeAsyncIterator()
                guard try await iterator.next() != nil else {
                    throw FirebaseV2CompositeTransportError.liveListenerUnavailable
                }
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw FirebaseV2CompositeTransportError.liveListenerUnavailable
            }
            _ = try await group.next()
            group.cancelAll()
        }
        guard generation == listenerGeneration else { throw CancellationError() }
    }

    private func startInboxListenerIfNeeded(for accountID: UUID) {
        guard inboxTask == nil else { return }
        inboxListenerGeneration &+= 1
        let generation = inboxListenerGeneration
        let stream = databaseValues.values(at: FirebaseV2Path.inbox(userID: accountID))
        inboxTask = Task { [weak self] in
            do {
                for try await data in stream {
                    guard !Task.isCancelled else { return }
                    await self?.receiveInboxData(data, generation: generation)
                }
                guard !Task.isCancelled else { return }
                await self?.inboxListenerFailed(generation: generation)
            } catch is CancellationError {
                return
            } catch {
                await self?.inboxListenerFailed(generation: generation)
            }
        }
    }

    private func startSupabaseEventForwardingIfNeeded() {
        guard supabaseEventTask == nil else { return }
        let stream = supabasePlane.events
        supabaseEventTask = Task { [weak self] in
            for await event in stream {
                guard !Task.isCancelled else { return }
                await self?.receiveSupabaseEvent(event)
            }
        }
    }

    private func receiveSupabaseEvent(_ event: BackendEvent) {
        guard !isShutDown else { return }
        switch event {
        case .typing, .characterPulse, .characterThrow:
            // Old clients still use Supabase Broadcast, but a session selected
            // for Firebase v2 consumes exactly one transient plane. A backend
            // bridge may mirror events for old clients; forwarding that mirror
            // here would replay the same interaction in the new client.
            break
        case .snapshot(let snapshot):
            currentRooms = snapshot.rooms
            failClosedIfActiveRoomWasRevoked()
            pruneLiveListeners(authorizedRoomIDs: Set(currentRooms.map(\.id)))
            if let session { startWarmLiveListeners(session: session) }
            eventContinuation.yield(event)
        case .reconciliation(let reconciliation):
            currentRooms = reconciliation.snapshot.rooms
            currentReconciliation = reconciliation
            failClosedIfActiveRoomWasRevoked()
            pruneLiveListeners(authorizedRoomIDs: Set(currentRooms.map(\.id)))
            if let session { startWarmLiveListeners(session: session) }
            eventContinuation.yield(event)
        default:
            eventContinuation.yield(event)
        }
    }

    private func receiveInboxData(_ data: Data, generation: UInt64) async {
        guard !isShutDown,
              generation == inboxListenerGeneration,
              let snapshot = try? FirebaseV2InboxSnapshot.decode(data) else { return }
        let actions = inboxReconciler.consume(snapshot)
        guard !actions.isEmpty else { return }
        let accessRevisions: [RealtimeRevision] = actions.compactMap { action in
            guard case .accessRevisionAdvanced(let revision) = action else { return nil }
            return revision
        }
        let minimumAccessRevision = accessRevisions.max()
        await reconcileAuthoritativeHint(minimumAccessRevision: minimumAccessRevision)
    }

    private func receiveLiveData(
        _ data: Data,
        roomID: UUID,
        generation: UInt64
    ) async {
        guard !isShutDown,
              generation == listenerGeneration,
              liveTasksByRoom[roomID] != nil,
              let snapshot = try? FirebaseV2LiveSnapshot.decode(data) else { return }
        latestLiveDataByRoom[roomID] = data
        guard roomID == activeRoomState.desiredActiveRoomID,
              liveReadinessContinuation != nil
                || (roomID == activeRoomState.committedRealtimeActiveRoomID
                    && committedListenerGeneration == generation)
        else { return }
        let actions = liveReconciler.consume(
            snapshot,
            receivedAtMilliseconds: serverNowMilliseconds()
        )
        if liveReadinessContinuation != nil {
            pendingInitialLiveActions.append(contentsOf: actions)
            liveReadinessContinuation?.yield(())
            liveReadinessContinuation?.finish()
            liveReadinessContinuation = nil
            return
        }
        var requiresAuthoritativeReconciliation = false
        for action in actions {
            requiresAuthoritativeReconciliation = handleLiveAction(
                action,
                roomID: roomID
            ) || requiresAuthoritativeReconciliation
        }
        scheduleTypingExpiry(roomID: roomID, generation: generation)
        if requiresAuthoritativeReconciliation {
            await reconcileAuthoritativeHint()
        }
    }

    private func drainPendingInitialLiveActions(
        roomID: UUID,
        generation: UInt64
    ) async {
        let actions = pendingInitialLiveActions
        pendingInitialLiveActions = []
        var requiresAuthoritativeReconciliation = false
        for action in actions {
            requiresAuthoritativeReconciliation = handleLiveAction(
                action,
                roomID: roomID
            ) || requiresAuthoritativeReconciliation
        }
        scheduleTypingExpiry(roomID: roomID, generation: generation)
        if requiresAuthoritativeReconciliation {
            await reconcileAuthoritativeHint()
        }
    }

    @discardableResult
    private func handleLiveAction(_ action: FirebaseV2LiveAction, roomID: UUID) -> Bool {
        switch action {
        case .typing(let userID, let active):
            guard userID != session?.identity.accountID else { return false }
            eventContinuation.yield(.typing(
                roomID: roomID,
                userID: userID,
                active: active
            ))
            return false
        case .pulse(let userID, _):
            guard userID != session?.identity.accountID else { return false }
            eventContinuation.yield(.characterPulse(CharacterPulseEvent(
                id: UUID(),
                roomID: roomID,
                userID: userID
            )))
            return false
        case .characterThrow(let actorUserID, let payload):
            guard actorUserID != session?.identity.accountID,
                  actorUserID != payload.targetUserID,
                  let room = currentRooms.first(where: { $0.id == roomID }),
                  let actor = room.members.first(where: { $0.userID == actorUserID }),
                  let throwableID = throwableCatalogIDByWireCode[payload.wireCode]
            else { return false }
            eventContinuation.yield(.characterThrow(CharacterThrowEvent(
                id: UUID(),
                roomID: roomID,
                actorUserID: actorUserID,
                targetUserID: payload.targetUserID,
                sourceCharacterID: PixelCharacterCatalog.canonicalID(for: actor.characterID),
                throwableID: throwableID
            )))
            return false
        case .chatHint:
            // RTDB chat bodies are delivery hints only. Postgres remains the
            // source of truth and is re-read before emitting a durable message.
            return true
        }
    }

    private func scheduleTypingExpiry(roomID: UUID, generation: UInt64) {
        typingExpiryTask?.cancel()
        guard let deadline = liveReconciler.nextTypingExpiryMilliseconds else {
            typingExpiryTask = nil
            return
        }
        let delay = max(0, deadline - serverNowMilliseconds())
        typingExpiryTask = Task { [weak self] in
            do {
                try await Task.sleep(for: .milliseconds(delay))
            } catch {
                return
            }
            await self?.expireTyping(roomID: roomID, generation: generation)
        }
    }

    private func expireTyping(roomID: UUID, generation: UInt64) {
        guard generation == listenerGeneration,
              roomID == activeRoomState.committedRealtimeActiveRoomID else { return }
        let actions = liveReconciler.expireTyping(
            receivedAtMilliseconds: serverNowMilliseconds()
        )
        for action in actions {
            _ = handleLiveAction(action, roomID: roomID)
        }
        scheduleTypingExpiry(roomID: roomID, generation: generation)
    }

    private func reconcileAuthoritativeHint(
        minimumAccessRevision: RealtimeRevision? = nil
    ) async {
        guard !isShutDown else { return }
        if let minimumAccessRevision {
            pendingMinimumAccessRevision = pendingMinimumAccessRevision.map {
                max($0, minimumAccessRevision)
            } ?? minimumAccessRevision
        }
        if isReconcilingHint {
            needsAnotherHintReconciliation = true
            return
        }
        isReconcilingHint = true
        repeat {
            needsAnotherHintReconciliation = false
            let requestedAccessRevision = pendingMinimumAccessRevision
            pendingMinimumAccessRevision = nil
            do {
                if let requestedAccessRevision,
                   let currentAccessRevision = session?.bootstrap.accessRevision,
                   currentAccessRevision < requestedAccessRevision {
                    try await recoverCredentials(
                        minimumAccessRevision: requestedAccessRevision
                    )
                }
                let reconciliation = try await supabasePlane.synchronize(
                    rooms: currentRooms,
                    activeRoomID: activeRoomState.committedRealtimeActiveRoomID
                )
                currentRooms = reconciliation.snapshot.rooms
                currentReconciliation = reconciliation
                failClosedIfActiveRoomWasRevoked()
                eventContinuation.yield(.reconciliation(reconciliation))
            } catch is CancellationError {
                break
            } catch {
                if requestedAccessRevision != nil {
                    isReconcilingHint = false
                    // The selected Firebase adapter must fail closed when a
                    // newer grant cannot be verified. The shared Supabase
                    // backend still owns Presence and the router's recovery
                    // path, so retiring it here would require an app restart.
                    await failClosed(message: L10n.text(
                        "firebase.transport.error.reconciliation_failed"
                    ))
                    return
                }
                eventContinuation.yield(.technicalError(
                    L10n.text("firebase.transport.error.reconciliation_failed")
                ))
            }
        } while (needsAnotherHintReconciliation || pendingMinimumAccessRevision != nil)
            && !isShutDown
        isReconcilingHint = false
    }

    private func recoverCredentials(
        minimumAccessRevision: RealtimeRevision
    ) async throws {
        try await credentialReplacementGate.perform { [self] in
            try await replaceCredentialSession(
                trigger: .explicitRecovery,
                minimumAccessRevision: minimumAccessRevision,
                requireExtendedLease: false
            )
        }
    }

    private func replaceCredentialSession(
        trigger: RealtimeBootstrapTrigger,
        minimumAccessRevision: RealtimeRevision?,
        requireExtendedLease: Bool
    ) async throws {
        try requireRunning()
        guard let previousSession = session else {
            throw FirebaseV2CompositeTransportError.accessGrantClosed
        }
        let requiredAccessRevision = [
            minimumAccessRevision,
            pendingMinimumAccessRevision,
            grantBarrier.pendingRevision,
            previousSession.bootstrap.accessRevision
        ].compactMap { $0 }.max()
        let recovered = try await credentials.establish(
            trigger: trigger,
            minimumAccessRevision: requiredAccessRevision
        )
        do {
            guard recovered.identity == previousSession.identity else {
                throw RealtimeCredentialLifecycleError.identityMismatch
            }
            if let requiredAccessRevision {
                guard recovered.bootstrap.accessRevision >= requiredAccessRevision else {
                    throw FirebaseV2BootstrapClientError.unexpectedContract
                }
            }
            if requireExtendedLease {
                guard recovered.bootstrap.rolloutLeaseExpiresAt
                    > previousSession.bootstrap.rolloutLeaseExpiresAt else {
                    throw FirebaseV2CompositeTransportError.rolloutLeaseDidNotAdvance
                }
            }
            let activeRoomID = activeRoomState.committedRealtimeActiveRoomID
            try validateRoom(activeRoomID, session: recovered)

            // Firebase Auth is already replaced at this point. Publish the new
            // session and its hard deadline before rebuilding listeners so an
            // old expiry task cannot retire a successfully renewed credential.
            session = recovered
            scheduleRolloutLeaseExpiry(for: recovered)
            restartInboxListener()
            try await replaceActiveRoom(
                activeRoomID,
                session: recovered,
                forceListenerRestart: true
            )
            startWarmLiveListeners(session: recovered)
        } catch {
            // Once a new custom-token sign-in succeeds, continuing with the old
            // grant/listeners would mix credential generations. There is no
            // safe rollback credential, so close every outbound plane.
            await failClosedForRolloutLease()
            throw error
        }
    }

    private func restartInboxListener() {
        guard let identity = session?.identity else { return }
        let previousInboxTask = inboxTask
        previousInboxTask?.cancel()
        inboxTask = nil
        inboxReconciler = FirebaseV2InboxReconciler()
        startInboxListenerIfNeeded(for: identity.accountID)
    }

    private func inboxListenerFailed(generation: UInt64) async {
        guard !isShutDown,
              generation == inboxListenerGeneration else { return }
        // A terminal RTDB listener means Firebase can no longer prove that
        // this session still has room access. Close the complete composite
        // transport so chat and Firebase transients cannot continue under a
        // stale grant. Preserve the shared Supabase backend only so the
        // authenticated remote kill-switch can still rebuild the legacy
        // adapter.
        await failClosed(message: L10n.text("firebase.transport.error.listener_failed"))
    }

    private func listenerFailed(generation: UInt64, roomID: UUID) async {
        guard !isShutDown,
              generation == listenerGeneration,
              liveTasksByRoom.removeValue(forKey: roomID) != nil else { return }
        latestLiveDataByRoom.removeValue(forKey: roomID)
        guard roomID == activeRoomState.desiredActiveRoomID else { return }
        liveReadinessContinuation?.finish(
            throwing: FirebaseV2CompositeTransportError.liveListenerUnavailable
        )
        liveReadinessContinuation = nil
        await failClosed(message: L10n.text("firebase.transport.error.listener_failed"))
    }

    private func failClosed(message: String) async {
        guard !isShutDown else { return }
        eventContinuation.yield(.connection(BackendConnectionStatus(
            transportConnected: false,
            recoveryReconciled: false,
            activeRoomTransportConnected: false
        )))
        eventContinuation.yield(.technicalError(message))

        // Close Firebase credentials/listeners before any network wait. The
        // shared Supabase plane must survive so selector-driven recovery and a
        // remote legacy switch remain possible.
        // A Firebase-only failure must not leave this client advertised as
        // online forever through the still-running Supabase Presence socket.
        // Local Firebase state has already closed synchronously above;
        // synchronization restores the committed room after recovery.
        await retire(shutdownSupabase: false, withdrawPresence: true)
    }

    private func failClosedIfActiveRoomWasRevoked() {
        guard let roomID = activeRoomState.committedRealtimeActiveRoomID,
              !currentRooms.contains(where: { $0.id == roomID }) else { return }
        grantBarrier.revokeMembership()
        retireActiveRoomResources()
    }

    private func retireActiveRoomResources() {
        roomTransitionGeneration &+= 1
        listenerGeneration &+= 1
        cancelAllLiveListeners()
        typingExpiryTask?.cancel()
        liveReadinessContinuation?.finish(throwing: CancellationError())
        typingExpiryTask = nil
        liveReadinessContinuation = nil
        pendingInitialLiveActions = []
        transientWriter = nil
        liveReconciler = FirebaseV2LiveReconciler()
        activeRoomState.invalidateAll()
    }

    private func validateRoom(
        _ roomID: UUID?,
        session: FirebaseV2CompositeSession
    ) throws {
        guard let roomID else { return }
        let authoritativeMembership = currentRooms.contains(where: { $0.id == roomID })
        guard authoritativeMembership,
              session.bootstrap.rooms.contains(roomID) else {
            throw FirebaseV2CompositeTransportError.roomNotAuthorized
        }
    }

    private func requireRunning() throws {
        guard !isShutDown else { throw FirebaseV2CompositeTransportError.shutDown }
    }

    private func requireReady(roomID: UUID? = nil) throws {
        try requireRunning()
        guard session != nil, grantBarrier.isOpen else {
            throw FirebaseV2CompositeTransportError.accessGrantClosed
        }
        if let roomID,
           (activeRoomState.committedRealtimeActiveRoomID != roomID
               || activeRoomState.desiredActiveRoomID != roomID) {
            throw FirebaseV2CompositeTransportError.roomNotAuthorized
        }
    }

    private func makeTransientWriter(
        roomID: UUID,
        session: FirebaseV2CompositeSession
    ) -> FirebaseV2TransientWriter {
        var authorizedWireCodes = Set(session.bootstrap.wireItems)
        if let defaultWireCode = FirebaseV2WireCode(rawValue: "0") {
            authorizedWireCodes.insert(defaultWireCode)
        }
        return FirebaseV2TransientWriter(
            transport: databaseWrites,
            context: FirebaseV2TransientContext(
                identity: session.identity,
                activeRoomID: roomID,
                throwableWireCodesByCatalogID: throwableWireCodeByCatalogID,
                authorizedWireCodes: authorizedWireCodes
            )
        )
    }
}

// SideyBackend retains its membership- and epoch-authorized legacy transient
// RPCs so the router can rebuild the Supabase adapter when the selector turns
// Firebase v2 off. The Firebase composite itself only uses this conformance for
// authoritative reconciliation and Presence.
extension SideyBackend: FirebaseV2SupabasePlane {}
