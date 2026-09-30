import Foundation
import OSLog

enum RealtimeTransportKind: String, Equatable, Sendable {
    case legacySupabase = "legacy"
    case firebaseV2 = "firebase-v2"
}

enum RealtimeTransportConfigurationError: Error, Equatable {
    case unsupportedValue
}

enum RealtimeTransportPreference {
    static func resolve(_ rawValue: String?) throws -> RealtimeTransportKind? {
        guard let rawValue else { return nil }
        let normalized = rawValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalized.isEmpty else { return nil }
        guard let kind = RealtimeTransportKind(rawValue: normalized) else {
            throw RealtimeTransportConfigurationError.unsupportedValue
        }
        return kind
    }
}

enum RealtimeTransportFallbackReason: String, Equatable, Sendable {
    case configurationMissing
    case firebaseBootstrapNotAllowed
}

struct RealtimeRolloutLeaseStatus: Equatable, Sendable {
    let refreshIn: Duration
    let expiresIn: Duration
}

struct RealtimeTransportSelection: Equatable, Sendable {
    let requested: RealtimeTransportKind?
    let active: RealtimeTransportKind
    let fallbackReason: RealtimeTransportFallbackReason?

    /// Resolves startup policy before any adapter operation begins.
    /// `firebaseV2Allowed` is a rollout/contract gate, never a network-health signal.
    static func resolve(
        requested: RealtimeTransportKind?,
        firebaseV2Allowed: Bool
    ) -> Self {
        guard let requested else {
            return Self(
                requested: nil,
                active: .legacySupabase,
                fallbackReason: .configurationMissing
            )
        }
        guard requested == .firebaseV2 else {
            return Self(requested: requested, active: .legacySupabase, fallbackReason: nil)
        }
        guard firebaseV2Allowed else {
            return Self(
                requested: requested,
                active: .legacySupabase,
                fallbackReason: .firebaseBootstrapNotAllowed
            )
        }
        return Self(requested: requested, active: .firebaseV2, fallbackReason: nil)
    }
}

struct RealtimeTransportDiagnostics: Equatable, Sendable {
    let selection: RealtimeTransportSelection
    let lastSuccessfulProtocol: RealtimeTransportKind?
}

/// The authoritative outcome of one chat UUID submission.
///
/// Firebase v2 adapters must translate transport failures whose commit state is
/// unknown to `reconciliationPending`. The coordinator intentionally keeps the
/// existing pending outbox entry for that UUID and never resends it
/// automatically. Only a server response that proves rejection may use
/// `definitelyRejected`.
enum RealtimeChatOutcome: Equatable, Sendable {
    case confirmed(ChatMessage)
    case definitelyRejected(message: String)
    case reconciliationPending
}

enum RoomMessagingTransportRouterError: Error, Equatable {
    case selectedAdapterUnavailable
    case legacyAdapterUnavailable
    case transportTopologyUnavailable
    case selectedAdapterKindMismatch(
        expected: RealtimeTransportKind,
        actual: RealtimeTransportKind
    )
}

/// Application-facing composite realtime boundary.
///
/// A Firebase v2 implementation must keep Supabase Presence and authoritative
/// reconciliation, merge their domain events with Firebase delivery events,
/// and expose only that single merged stream. When Firebase v2 is selected,
/// typing, pulse, and throw publish and receive only through compact RTDB;
/// legacy Supabase transient mirrors are ignored. The legacy adapter resumes
/// Supabase transient ownership only after an explicit selector rollback.
protocol RoomMessagingTransport: Actor {
    nonisolated var kind: RealtimeTransportKind { get }
    nonisolated var events: AsyncStream<BackendEvent> { get }

    func synchronize(rooms: [Room], activeRoomID: UUID?) async throws -> BackendReconciliation
    func repairDroppedEvents(rooms: [Room], activeRoomID: UUID?) async throws -> BackendEventGapRepair
    func setActiveRoom(_ roomID: UUID?) async throws
    func setLocalPresence(_ state: PresenceState) async throws
    func publishTyping(roomID: UUID, event: String) async throws
    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws
    func publishCharacterThrow(roomID: UUID, eventID: UUID, targetUserID: UUID) async throws
    func convergeAccessGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) async throws
    func rolloutLeaseStatus() -> RealtimeRolloutLeaseStatus?
    func refreshRolloutLease() async throws
    func failClosedForRolloutLease() async
    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome
    func retireForTransportSwitch() async
    func shutdown() async
}

extension RoomMessagingTransport {
    func repairDroppedEvents(rooms: [Room], activeRoomID: UUID?) async throws -> BackendEventGapRepair {
        let result = try await synchronize(rooms: rooms, activeRoomID: activeRoomID)
        return BackendEventGapRepair(reconciliation: result,
            messagesByRoom: result.activeRoomID.map { [$0: result.activeMessages] } ?? [:], presence: [])
    }

    /// Legacy Supabase transport has no separate Firebase credential grant.
    func convergeAccessGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) async throws {}

    func rolloutLeaseStatus() -> RealtimeRolloutLeaseStatus? { nil }

    func refreshRolloutLease() async throws {}

    func failClosedForRolloutLease() async {}

    func retireForTransportSwitch() async {}
}

enum RealtimeGrantAwareMutationResult<Value> {
    case ready(Value)
    case committedPendingGrant(value: Value, error: any Error)

    var value: Value {
        switch self {
        case .ready(let value), .committedPendingGrant(let value, _):
            value
        }
    }

    var grantError: (any Error)? {
        guard case .committedPendingGrant(_, let error) = self else { return nil }
        return error
    }
}

enum RealtimeGrantAwareMutation {
    /// Runs exactly one mutation contract. A Firebase v2 value is not returned
    /// as ready until its exact access revision has been requested and
    /// acknowledged by the selected transport. A convergence failure is kept
    /// distinct from a mutation failure because the server write is committed.
    @MainActor
    static func perform<Value>(
        selection: RealtimeTransportSelection,
        requiresEntitlement: Bool,
        legacy: () async throws -> Value,
        firebaseV2: () async throws -> (value: Value, revision: RealtimeRevision),
        converge: (RealtimeRevision, Bool) async throws -> Void
    ) async throws -> RealtimeGrantAwareMutationResult<Value> {
        guard selection.active == .firebaseV2 else {
            return .ready(try await legacy())
        }
        let grant = try await firebaseV2()
        do {
            try await converge(grant.revision, requiresEntitlement)
            return .ready(grant.value)
        } catch {
            return .committedPendingGrant(value: grant.value, error: error)
        }
    }
}

actor RoomMessagingTransportRouter {
    private enum OperationalState: Equatable {
        case running
        case switching
        case blocked
    }

    nonisolated let events: AsyncStream<BackendEvent>

    private let eventContinuation: AsyncStream<BackendEvent>.Continuation
    private var selection: RealtimeTransportSelection
    private var transport: any RoomMessagingTransport
    private let makeFirebaseV2ForRecovery:
        (@MainActor @Sendable () async throws -> any RoomMessagingTransport)?
    private let makeLegacyForSwitch: (@Sendable () async throws -> any RoomMessagingTransport)?
    private let shutdownShared: (@Sendable () async -> Void)?
    private let logger = Logger(subsystem: "app.sidey.desktop", category: "RealtimeTransport")
    private var lastSuccessfulProtocol: RealtimeTransportKind?
    private var eventTask: Task<Void, Never>?
    private var eventGeneration: UInt64 = 0
    private var eventRepairTask: Task<Void, Never>?
    private var eventRepairDirty = false
    private var publishingEventRepair = false
    private var synchronizedRooms: [Room]?
    private var synchronizedActiveRoomID: UUID?
    private var isShutDown = false
    private var operationalState: OperationalState = .running
    private var blockedLegacyTransport: (any RoomMessagingTransport)?

    private struct PresencePublication: Sendable {
        let state: PresenceState
        let generation: UInt64
        let transport: any RoomMessagingTransport
    }

    // Activity is desired session state, not a transient event. Retain it even
    // while an adapter's credential/topology gate rejects publications.
    private var desiredPresence: PresenceState?
    private var presenceRevision: UInt64 = 0
    private var publishedPresenceRevision: UInt64?
    private var presenceGeneration: UInt64 = 0
    private var presenceRetryTask: Task<Void, Never>?
    private let presenceRetrySleep: @Sendable (Duration) async throws -> Void
    private lazy var presencePublications = PresencePublicationQueue<PresencePublication> {
        [weak self] publication in
        guard let self else { throw CancellationError() }
        try await self.performPresencePublication(publication)
    }

    init(
        selection: RealtimeTransportSelection,
        makeLegacy: (@Sendable () throws -> any RoomMessagingTransport)? = nil,
        makeFirebaseV2: (@Sendable () throws -> any RoomMessagingTransport)? = nil,
        makeFirebaseV2ForRecovery:
            (@MainActor @Sendable () async throws -> any RoomMessagingTransport)? = nil,
        makeLegacyForSwitch: (@Sendable () async throws -> any RoomMessagingTransport)? = nil,
        shutdownShared: (@Sendable () async -> Void)? = nil,
        eventBufferLimit: Int = 256,
        presenceRetrySleep: @escaping @Sendable (Duration) async throws -> Void = {
            try await Task.sleep(for: $0)
        }
    ) throws {
        self.selection = selection
        let selectedTransport: any RoomMessagingTransport
        if selection.active == .firebaseV2 {
            guard let makeFirebaseV2 else {
                throw RoomMessagingTransportRouterError.selectedAdapterUnavailable
            }
            selectedTransport = try makeFirebaseV2()
        } else {
            guard let makeLegacy else {
                throw RoomMessagingTransportRouterError.selectedAdapterUnavailable
            }
            selectedTransport = try makeLegacy()
        }
        guard selectedTransport.kind == selection.active else {
            throw RoomMessagingTransportRouterError.selectedAdapterKindMismatch(
                expected: selection.active,
                actual: selectedTransport.kind
            )
        }
        self.transport = selectedTransport
        self.makeFirebaseV2ForRecovery = makeFirebaseV2ForRecovery
        self.makeLegacyForSwitch = makeLegacyForSwitch
        self.shutdownShared = shutdownShared
        self.presenceRetrySleep = presenceRetrySleep
        let pair = AsyncStream<BackendEvent>.makeStream(
            bufferingPolicy: .bufferingNewest(max(1, eventBufferLimit))
        )
        self.events = pair.stream
        self.eventContinuation = pair.continuation
        let fallback = selection.fallbackReason?.rawValue ?? "none"
        logger.info(
            "Selected realtime protocol: \(selection.active.rawValue, privacy: .public), fallback: \(fallback, privacy: .public)"
        )
    }

    func currentSelection() throws -> RealtimeTransportSelection {
        guard operationalState == .running, !isShutDown else {
            throw RoomMessagingTransportRouterError.transportTopologyUnavailable
        }
        return selection
    }

    func diagnostics() -> RealtimeTransportDiagnostics {
        RealtimeTransportDiagnostics(
            selection: selection,
            lastSuccessfulProtocol: lastSuccessfulProtocol
        )
    }

    func synchronize(rooms: [Room], activeRoomID: UUID?) async throws -> BackendReconciliation {
        guard !isShutDown else { throw CancellationError() }
        if operationalState == .blocked, let blockedLegacyTransport {
            return try await finishLegacySwitch(
                blockedLegacyTransport,
                rooms: rooms,
                activeRoomID: activeRoomID
            )
        }
        try requireRunning()
        startEventForwardingIfNeeded()
        let reconciliation = try await transport.synchronize(
            rooms: rooms,
            activeRoomID: activeRoomID
        )
        synchronizedRooms = reconciliation.snapshot.rooms
        synchronizedActiveRoomID = reconciliation.activeRoomID
        try await restorePresence(on: transport)
        recordSuccess(operation: "synchronize")
        return reconciliation
    }

    func setActiveRoom(_ roomID: UUID?) async throws {
        try requireRunning()
        startEventForwardingIfNeeded()
        try await transport.setActiveRoom(roomID)
        synchronizedActiveRoomID = roomID
        try await restorePresence(on: transport)
        recordSuccess(operation: "set-active-room")
    }

    /// Accepts the latest intent. Temporary publication failures are retried
    /// here; they must not turn a local activity update into an app-wide error.
    func setLocalPresence(_ state: PresenceState) async throws {
        guard !isShutDown else { throw CancellationError() }
        desiredPresence = state == .away ? .away : .online
        presenceRevision &+= 1
        guard operationalState == .running, synchronizedRooms != nil else { return }
        do {
            try await replayPresence(on: transport)
        } catch {
            schedulePresenceRetry()
        }
    }

    private func restorePresence(on target: any RoomMessagingTransport) async throws {
        let generation = presenceGeneration
        do {
            try await replayPresence(on: target)
        } catch {
            guard !isShutDown, generation == presenceGeneration else {
                throw CancellationError()
            }
            // Bootstrap/lease monitoring must still complete after an isolated
            // Presence failure. The latest intent stays dirty until retried.
            schedulePresenceRetry()
        }
    }

    private func performPresencePublication(_ publication: PresencePublication) async throws {
        guard !isShutDown, publication.generation == presenceGeneration else {
            throw CancellationError()
        }
        try Task.checkCancellation()
        try await publication.transport.setLocalPresence(publication.state)
        guard !isShutDown, publication.generation == presenceGeneration else {
            throw CancellationError()
        }
        try Task.checkCancellation()
    }

    /// Also used with a synchronized replacement before it becomes the active
    /// adapter. Its own access/lease fence still owns permission to publish.
    private func replayPresence(on target: any RoomMessagingTransport) async throws {
        let generation = presenceGeneration
        publishedPresenceRevision = nil
        while let state = desiredPresence {
            guard !isShutDown, generation == presenceGeneration else {
                throw CancellationError()
            }
            let revision = presenceRevision
            do {
                try await presencePublications.submit(PresencePublication(
                    state: state,
                    generation: generation,
                    transport: target
                ))
            } catch {
                schedulePresenceRetry()
                throw error
            }
            guard !isShutDown, generation == presenceGeneration else {
                throw CancellationError()
            }
            // A coalesced request can complete for a newer value. Never mark
            // a revision clean until the latest intent has been published.
            guard revision == presenceRevision else { continue }
            publishedPresenceRevision = revision
            presenceRetryTask?.cancel()
            presenceRetryTask = nil
            recordSuccess(operation: "presence")
            return
        }
        guard !isShutDown else { throw CancellationError() }
    }

    private func schedulePresenceRetry() {
        guard presenceRetryTask == nil, !isShutDown,
              operationalState == .running, synchronizedRooms != nil,
              desiredPresence != nil,
              publishedPresenceRevision != presenceRevision else { return }
        let generation = presenceGeneration
        presenceRetryTask = Task { [weak self, presenceRetrySleep] in
            var attempt = 1
            while !Task.isCancelled {
                do {
                    try await presenceRetrySleep(.seconds(
                        RealtimeRecoveryPolicy.delay(forAttempt: attempt)
                    ))
                    guard !Task.isCancelled, let self else { return }
                    guard await self.retryPresence(generation: generation) else { return }
                    attempt = min(attempt + 1, 6)
                } catch { return }
            }
        }
    }

    /// Returns whether the same pending intent still needs a delayed retry.
    private func retryPresence(generation: UInt64) async -> Bool {
        guard !isShutDown, generation == presenceGeneration,
              operationalState == .running,
              publishedPresenceRevision != presenceRevision else { return false }
        do {
            try await replayPresence(on: transport)
            return false
        } catch {
            return !isShutDown && generation == presenceGeneration
                && operationalState == .running && !Task.isCancelled
        }
    }

    private func invalidatePresencePublication() {
        presenceGeneration &+= 1
        publishedPresenceRevision = nil
        presenceRetryTask?.cancel()
        presenceRetryTask = nil
    }

    func publishTyping(roomID: UUID, event: String) async throws {
        try requireRunning()
        startEventForwardingIfNeeded()
        try await transport.publishTyping(roomID: roomID, event: event)
        recordSuccess(operation: "typing")
    }

    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        try requireRunning()
        startEventForwardingIfNeeded()
        try await transport.publishCharacterPulse(roomID: roomID, eventID: eventID)
        recordSuccess(operation: "pulse")
    }

    func publishCharacterThrow(roomID: UUID, eventID: UUID, targetUserID: UUID) async throws {
        try requireRunning()
        startEventForwardingIfNeeded()
        try await transport.publishCharacterThrow(
            roomID: roomID,
            eventID: eventID,
            targetUserID: targetUserID
        )
        recordSuccess(operation: "throw")
    }

    func convergeAccessGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) async throws {
        try requireRunning()
        startEventForwardingIfNeeded()
        try await transport.convergeAccessGrant(
            revision: revision,
            requiresEntitlement: requiresEntitlement
        )
        try await restorePresence(on: transport)
        recordSuccess(operation: "grant")
    }

    func rolloutLeaseStatus() async throws -> RealtimeRolloutLeaseStatus? {
        try requireRunning()
        return await transport.rolloutLeaseStatus()
    }

    func refreshRolloutLease() async throws {
        try requireRunning()
        guard selection.active == .firebaseV2 else { return }
        guard await transport.rolloutLeaseStatus() != nil else {
            try await recoverFirebaseV2()
            return
        }
        try await transport.refreshRolloutLease()
        try await restorePresence(on: transport)
        recordSuccess(operation: "rollout-lease-refresh")
    }

    /// Rebuilds a Firebase transport after its hard rollout lease closed every
    /// credential and listener. The router and shared Supabase backend stay
    /// alive so the app event stream does not need an application restart.
    private func recoverFirebaseV2() async throws {
        try requireRunning()
        guard selection.active == .firebaseV2,
              let makeFirebaseV2ForRecovery else {
            throw RoomMessagingTransportRouterError.selectedAdapterUnavailable
        }
        guard let rooms = synchronizedRooms else {
            throw RoomMessagingTransportRouterError.transportTopologyUnavailable
        }

        operationalState = .switching
        invalidatePresencePublication()
        stopEventForwarding()
        await transport.retireForTransportSwitch()
        guard !isShutDown else { throw CancellationError() }

        let replacement: any RoomMessagingTransport
        do {
            replacement = try await makeFirebaseV2ForRecovery()
            guard !isShutDown else {
                await replacement.retireForTransportSwitch()
                throw CancellationError()
            }
        } catch {
            operationalState = .running
            throw error
        }
        guard replacement.kind == .firebaseV2 else {
            await replacement.retireForTransportSwitch()
            operationalState = .running
            throw RoomMessagingTransportRouterError.selectedAdapterKindMismatch(
                expected: .firebaseV2,
                actual: replacement.kind
            )
        }

        do {
            let reconciliation = try await replacement.synchronize(
                rooms: rooms,
                activeRoomID: synchronizedActiveRoomID
            )
            try await restorePresence(on: replacement)
            transport = replacement
            synchronizedRooms = reconciliation.snapshot.rooms
            synchronizedActiveRoomID = reconciliation.activeRoomID
            operationalState = .running
            schedulePresenceRetry()
            startEventForwardingIfNeeded()
            recordSuccess(operation: "rollout-lease-recovery")
            emit(.reconciliation(reconciliation))
        } catch {
            // Synchronization can already have published Supabase Presence.
            // Fail-closing the replacement withdraws it before the next retry.
            await replacement.failClosedForRolloutLease()
            operationalState = .running
            throw error
        }
    }

    func failClosedForRolloutLease() async {
        guard !isShutDown, selection.active == .firebaseV2 else { return }
        invalidatePresencePublication()
        await transport.failClosedForRolloutLease()
    }

    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome {
        do {
            try requireRunning()
        } catch {
            return .definitelyRejected(message: error.localizedDescription)
        }
        startEventForwardingIfNeeded()
        let result = await transport.sendChat(roomID: roomID, body: body, id: id)
        if case .confirmed = result {
            recordSuccess(operation: "chat")
        }
        return result
    }

    /// Applies an explicit remote kill-switch without rebuilding the shared
    /// Supabase session/socket. Firebase listeners and Auth retire first; only
    /// then is the legacy adapter synchronized to the last authoritative state.
    @discardableResult
    func switchToLegacy() async throws -> BackendReconciliation? {
        guard selection.active == .firebaseV2 else { return nil }
        guard let makeLegacyForSwitch else {
            throw RoomMessagingTransportRouterError.legacyAdapterUnavailable
        }
        guard let synchronizedRooms else {
            throw RoomMessagingTransportRouterError.transportTopologyUnavailable
        }

        guard !isShutDown, operationalState != .switching else {
            throw RoomMessagingTransportRouterError.transportTopologyUnavailable
        }
        operationalState = .switching
        invalidatePresencePublication()
        await transport.retireForTransportSwitch()
        stopEventForwarding()
        let legacyTransport: any RoomMessagingTransport
        do {
            legacyTransport = try await makeLegacyForSwitch()
            guard !isShutDown else {
                await legacyTransport.retireForTransportSwitch()
                throw CancellationError()
            }
        } catch {
            operationalState = .blocked
            emit(.technicalError(
                L10n.text("realtime.kill_switch.error.legacy_connection_creation_failed")
            ))
            throw error
        }
        guard legacyTransport.kind == .legacySupabase else {
            operationalState = .blocked
            throw RoomMessagingTransportRouterError.selectedAdapterKindMismatch(
                expected: .legacySupabase,
                actual: legacyTransport.kind
            )
        }
        do {
            return try await finishLegacySwitch(
                legacyTransport,
                rooms: synchronizedRooms,
                activeRoomID: synchronizedActiveRoomID
            )
        } catch {
            blockedLegacyTransport = legacyTransport
            operationalState = .blocked
            emit(.technicalError(
                L10n.text("realtime.kill_switch.error.legacy_connection_recovery_failed")
            ))
            throw error
        }
    }

    private func finishLegacySwitch(
        _ legacyTransport: any RoomMessagingTransport,
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation {
        let reconciliation = try await legacyTransport.synchronize(
            rooms: rooms,
            activeRoomID: activeRoomID
        )
        try await restorePresence(on: legacyTransport)
        transport = legacyTransport
        selection = RealtimeTransportSelection.resolve(
            requested: .legacySupabase,
            firebaseV2Allowed: false
        )
        synchronizedRooms = reconciliation.snapshot.rooms
        synchronizedActiveRoomID = reconciliation.activeRoomID
        blockedLegacyTransport = nil
        operationalState = .running
        schedulePresenceRetry()
        startEventForwardingIfNeeded()
        recordSuccess(operation: "kill-switch-to-legacy")
        emit(.reconciliation(reconciliation))
        return reconciliation
    }

    func shutdown() async {
        guard !isShutDown else { return }
        isShutDown = true
        invalidatePresencePublication()
        desiredPresence = nil
        await presencePublications.cancel()
        stopEventForwarding()
        await transport.retireForTransportSwitch()
        if let shutdownShared {
            await shutdownShared()
        } else {
            await transport.shutdown()
        }
        eventContinuation.finish()
    }

    private func startEventForwardingIfNeeded() {
        guard eventTask == nil, !isShutDown else { return }
        eventGeneration &+= 1
        let generation = eventGeneration
        let stream = transport.events
        eventTask = Task { [weak self] in
            for await event in stream {
                guard !Task.isCancelled else { return }
                await self?.forward(event, generation: generation)
            }
        }
    }

    private func stopEventForwarding() {
        eventGeneration &+= 1
        eventRepairTask?.cancel()
        eventRepairTask = nil
        eventRepairDirty = false
        publishingEventRepair = false
        eventTask?.cancel()
        eventTask = nil
    }

    private func forward(_ event: BackendEvent, generation: UInt64) {
        guard generation == eventGeneration, !isShutDown else { return }
        if case .connection(let status) = event, status.isReady {
            // Channel recovery may have replayed the backend's older state.
            // Wake the retained intent without requiring another input edge.
            publishedPresenceRevision = nil
            schedulePresenceRetry()
        }
        emit(event)
    }

    private func emit(_ event: BackendEvent, repairBaseline: Bool = false) {
        if publishingEventRepair, !repairBaseline, event.requiresStateRepair {
            eventRepairDirty = true
        }
        if case .dropped(let dropped) = eventContinuation.yield(event),
           !repairBaseline, dropped.requiresStateRepair, !isShutDown {
            eventRepairDirty = true
            guard eventRepairTask == nil else { return }
            let generation = eventGeneration
            eventRepairTask = Task { [weak self] in
                await self?.repairEventQueue(generation: generation)
            }
        }
    }

    private func repairEventQueue(generation: UInt64) async {
        defer {
            if generation == eventGeneration { eventRepairTask = nil; publishingEventRepair = false }
        }
        var failures = 0
        while eventRepairDirty, generation == eventGeneration, !Task.isCancelled, !isShutDown {
            do {
                let delay = failures == 0 ? 1.0 : FirebaseV2BootstrapRetryPolicy.delaySeconds(
                    failureCount: failures, rateLimited: false, retryAfterSeconds: nil,
                    jitter: Double.random(in: 0...1)
                )
                try await Task.sleep(for: .seconds(delay))
                guard operationalState == .running, let rooms = synchronizedRooms else { return }
                eventRepairDirty = false
                publishingEventRepair = true
                let repaired = try await transport.repairDroppedEvents(
                    rooms: rooms, activeRoomID: synchronizedActiveRoomID
                )
                guard generation == eventGeneration, !isShutDown else { return }
                synchronizedRooms = repaired.reconciliation.snapshot.rooms
                synchronizedActiveRoomID = repaired.reconciliation.activeRoomID
                emit(.eventGapRepaired(repaired), repairBaseline: true)
                publishingEventRepair = false
                failures = eventRepairDirty ? failures + 1 : 0
            } catch is CancellationError {
                guard !Task.isCancelled, generation == eventGeneration, !isShutDown else { return }
                publishingEventRepair = false
                eventRepairDirty = true
                failures += 1
            } catch {
                guard generation == eventGeneration, !isShutDown, !Task.isCancelled else { return }
                publishingEventRepair = false
                eventRepairDirty = true
                failures += 1
            }
        }
    }

    private func requireRunning() throws {
        guard operationalState == .running, !isShutDown else {
            throw RoomMessagingTransportRouterError.transportTopologyUnavailable
        }
    }

    private func recordSuccess(operation: String) {
        lastSuccessfulProtocol = transport.kind
        let protocolName = transport.kind.rawValue
        logger.debug(
            "Realtime operation succeeded: \(operation, privacy: .public), protocol: \(protocolName, privacy: .public)"
        )
    }
}

extension SideyBackend: RoomMessagingTransport {
    nonisolated var kind: RealtimeTransportKind { .legacySupabase }

    func synchronize(
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation {
        try await syncRealtime(rooms: rooms, activeRoomID: activeRoomID)
    }

    func publishTyping(roomID: UUID, event: String) async throws {
        try await broadcastTyping(roomID: roomID, event: event)
    }

    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        try await broadcastCharacterPulse(roomID: roomID, eventID: eventID)
    }

    func publishCharacterThrow(
        roomID: UUID,
        eventID: UUID,
        targetUserID: UUID
    ) async throws {
        try await broadcastCharacterThrow(
            roomID: roomID,
            eventID: eventID,
            targetUserID: targetUserID
        )
    }

    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome {
        do {
            return .confirmed(try await sendMessage(roomID: roomID, body: body, id: id))
        } catch {
            // Preserve the legacy adapter's existing terminal-failure behavior.
            // Firebase v2 owns the stricter ambiguous-commit classification.
            return .definitelyRejected(message: error.localizedDescription)
        }
    }
}
