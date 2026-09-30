import XCTest
@testable import SIDEY

final class RealtimeTransportTests: XCTestCase {
    func testTransportPreferenceRejectsUnknownValues() throws {
        XCTAssertNil(try RealtimeTransportPreference.resolve(nil))
        XCTAssertEqual(try RealtimeTransportPreference.resolve("legacy"), .legacySupabase)
        XCTAssertEqual(try RealtimeTransportPreference.resolve(" firebase-v2 "), .firebaseV2)
        XCTAssertThrowsError(try RealtimeTransportPreference.resolve("automatic"))
    }

    func testMissingConfigurationKeepsLegacyTransport() {
        let selection = RealtimeTransportSelection.resolve(
            requested: nil,
            firebaseV2Allowed: true
        )

        XCTAssertEqual(selection.active, .legacySupabase)
        XCTAssertEqual(selection.fallbackReason, .configurationMissing)
    }

    func testFirebaseBootstrapNotAllowedKeepsLegacyTransport() {
        let selection = RealtimeTransportSelection.resolve(
            requested: .firebaseV2,
            firebaseV2Allowed: false
        )

        XCTAssertEqual(selection.active, .legacySupabase)
        XCTAssertEqual(selection.fallbackReason, .firebaseBootstrapNotAllowed)
    }

    func testRouterOverflowRepairsSelectedTransportAndPublishesAtomicBaseline() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase }, eventBufferLimit: 1)
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        for _ in 0..<4 { await firebase.emit(.messagesInvalidated(roomID: UUID())) }
        let deadline = ContinuousClock().now.advanced(by: .seconds(3))
        while await firebase.operationCount < 2, ContinuousClock().now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        var iterator = router.events.makeAsyncIterator()
        var repaired = false
        while !repaired, let event = await iterator.next() {
            if case .eventGapRepaired(let baseline) = event {
                XCTAssertTrue(baseline.reconciliation.snapshot.rooms.isEmpty)
                repaired = true
            }
        }
        XCTAssertTrue(repaired)
        await router.shutdown()
    }

    func testRouterPublishesThroughOnlySelectedTransport() async throws {
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacy },
            makeFirebaseV2: { firebase }
        )

        try await router.publishTyping(roomID: UUID(), event: "typing_start")

        let legacyOperationCount = await legacy.operationCount
        let firebaseOperationCount = await firebase.operationCount
        let diagnostics = await router.diagnostics()
        XCTAssertEqual(legacyOperationCount, 0)
        XCTAssertEqual(firebaseOperationCount, 1)
        XCTAssertEqual(diagnostics.lastSuccessfulProtocol, .firebaseV2)
    }

    func testRouterForwardsOnlyTheSelectedTransportEventStream() async throws {
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacy },
            makeFirebaseV2: { firebase }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        let eventTask = Task {
            var iterator = router.events.makeAsyncIterator()
            return await iterator.next()
        }

        await legacy.emit(.technicalError("legacy"))
        await firebase.emit(.technicalError("firebase"))

        guard case .technicalError(let message) = await eventTask.value else {
            return XCTFail("The selected transport event must be forwarded")
        }
        XCTAssertEqual(message, "firebase")
    }

    func testRouterRoutesEveryOperationAndShutdownExactlyOnce() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { FakeRealtimeTransport(kind: .legacySupabase) },
            makeFirebaseV2: { firebase }
        )
        let roomID = UUID()
        let eventID = UUID()

        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        try await router.setActiveRoom(roomID)
        try await router.setLocalPresence(.away)
        try await router.publishTyping(roomID: roomID, event: "typing_start")
        try await router.publishCharacterPulse(roomID: roomID, eventID: eventID)
        try await router.publishCharacterThrow(
            roomID: roomID,
            eventID: eventID,
            targetUserID: UUID()
        )
        _ = await router.sendChat(roomID: roomID, body: "hello", id: UUID())
        await router.shutdown()

        let operationCount = await firebase.operationCount
        let shutdownCount = await firebase.shutdownCount
        XCTAssertEqual(operationCount, 7)
        XCTAssertEqual(shutdownCount, 1)
    }

    @MainActor
    func testFirebaseMutationRequestsExactGrantBeforeReturningValueAsReady() async throws {
        let revision = try XCTUnwrap(
            RealtimeRevision(rawValue: "00000000000000000042")
        )
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { FakeRealtimeTransport(kind: .legacySupabase) },
            makeFirebaseV2: { firebase }
        )
        var mutationCallCount = 0

        let result = try await RealtimeGrantAwareMutation.perform(
            selection: try await router.currentSelection(),
            requiresEntitlement: true,
            legacy: {
                XCTFail("Firebase selection must not call the legacy mutation")
                return "legacy"
            },
            firebaseV2: {
                mutationCallCount += 1
                return ("firebase", revision)
            },
            converge: { requestedRevision, requiresEntitlement in
                XCTAssertEqual(mutationCallCount, 1)
                try await router.convergeAccessGrant(
                    revision: requestedRevision,
                    requiresEntitlement: requiresEntitlement
                )
            }
        )

        guard case .ready(let value) = result else {
            return XCTFail("Value must not become ready before grant convergence")
        }
        XCTAssertEqual(value, "firebase")
        let requests = await firebase.grantRequests
        XCTAssertEqual(requests, [FakeGrantRequest(
            revision: revision,
            requiresEntitlement: true
        )])
    }

    @MainActor
    func testLegacyMutationKeepsExistingContractAndSkipsV2Grant() async throws {
        let legacySelection = RealtimeTransportSelection.resolve(
            requested: .legacySupabase,
            firebaseV2Allowed: true
        )
        var legacyCallCount = 0
        var firebaseCallCount = 0
        var convergenceCallCount = 0

        let result = try await RealtimeGrantAwareMutation.perform(
            selection: legacySelection,
            requiresEntitlement: false,
            legacy: {
                legacyCallCount += 1
                return "legacy"
            },
            firebaseV2: {
                firebaseCallCount += 1
                return (
                    "firebase",
                    try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000042"))
                )
            },
            converge: { _, _ in
                convergenceCallCount += 1
            }
        )

        guard case .ready(let value) = result else {
            return XCTFail("Legacy mutation must remain ready without a Firebase grant")
        }
        XCTAssertEqual(value, "legacy")
        XCTAssertEqual(legacyCallCount, 1)
        XCTAssertEqual(firebaseCallCount, 0)
        XCTAssertEqual(convergenceCallCount, 0)
    }

    @MainActor
    func testFirebaseMutationDoesNotReportCommittedValueAsReadyWhenGrantFails() async throws {
        let revision = try XCTUnwrap(
            RealtimeRevision(rawValue: "00000000000000000043")
        )
        var mutationCallCount = 0
        var convergenceCallCount = 0

        let result = try await RealtimeGrantAwareMutation.perform(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            requiresEntitlement: false,
            legacy: {
                XCTFail("Firebase selection must not call the legacy mutation")
                return "legacy"
            },
            firebaseV2: {
                mutationCallCount += 1
                return ("committed", revision)
            },
            converge: { requestedRevision, requiresEntitlement in
                convergenceCallCount += 1
                XCTAssertEqual(requestedRevision, revision)
                XCTAssertFalse(requiresEntitlement)
                throw FakeGrantConvergenceError.failed
            }
        )

        guard case .committedPendingGrant(let value, _) = result else {
            return XCTFail("A committed mutation must remain pending when grant convergence fails")
        }
        XCTAssertEqual(value, "committed")
        XCTAssertEqual(mutationCallCount, 1)
        XCTAssertEqual(convergenceCallCount, 1)
    }

    func testPermissionFailureDoesNotDowngradeToLegacy() async throws {
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)
        let firebase = FakeRealtimeTransport(
            kind: .firebaseV2,
            failure: FakeTransportError.permissionDenied
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacy },
            makeFirebaseV2: { firebase }
        )

        do {
            try await router.publishTyping(roomID: UUID(), event: "typing_start")
            XCTFail("Permission denial must remain visible to the selected transport")
        } catch FakeTransportError.permissionDenied {
            // Expected. A permission denial must not be retried through legacy.
        }

        let legacyOperationCount = await legacy.operationCount
        let firebaseOperationCount = await firebase.operationCount
        let diagnostics = await router.diagnostics()
        XCTAssertEqual(legacyOperationCount, 0)
        XCTAssertEqual(firebaseOperationCount, 1)
        XCTAssertEqual(diagnostics.selection.active, .firebaseV2)
        XCTAssertNil(diagnostics.lastSuccessfulProtocol)
    }

    func testRouterRejectsASelectionWithoutItsAdapter() {
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)

        XCTAssertThrowsError(try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacy }
        )) { error in
            XCTAssertEqual(
                error as? RoomMessagingTransportRouterError,
                .selectedAdapterUnavailable
            )
        }
    }

    func testRouterConstructsOnlyTheSelectedAdapter() throws {
        let legacyFactory = FakeTransportFactory(kind: .legacySupabase)
        let firebaseFactory = FakeTransportFactory(kind: .firebaseV2)

        _ = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacyFactory.make() },
            makeFirebaseV2: { firebaseFactory.make() }
        )

        XCTAssertEqual(legacyFactory.creationCount, 0)
        XCTAssertEqual(firebaseFactory.creationCount, 1)
    }

    func testRouterRejectsAdapterKindMismatch() {
        XCTAssertThrowsError(try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { FakeRealtimeTransport(kind: .legacySupabase) },
            makeFirebaseV2: { FakeRealtimeTransport(kind: .legacySupabase) }
        )) { error in
            XCTAssertEqual(
                error as? RoomMessagingTransportRouterError,
                .selectedAdapterKindMismatch(expected: .firebaseV2, actual: .legacySupabase)
            )
        }
    }

    func testRouterPropagatesSelectedAdapterConstructionFailure() {
        XCTAssertThrowsError(try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { FakeRealtimeTransport(kind: .legacySupabase) },
            makeFirebaseV2: { throw FakeTransportError.permissionDenied }
        )) { error in
            XCTAssertEqual(error as? FakeTransportError, .permissionDenied)
        }
    }

    func testRouterPreservesReconciliationPendingWithoutReportingChatSuccess() async throws {
        let firebase = FakeRealtimeTransport(
            kind: .firebaseV2,
            chatOutcome: .reconciliationPending
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { FakeRealtimeTransport(kind: .legacySupabase) },
            makeFirebaseV2: { firebase }
        )

        let outcome = await router.sendChat(roomID: UUID(), body: "hello", id: UUID())
        let diagnostics = await router.diagnostics()

        XCTAssertEqual(outcome, .reconciliationPending)
        XCTAssertNil(diagnostics.lastSuccessfulProtocol)
    }

    func testKillSwitchCreatesFreshLegacyAdapterOnlyAtTransitionAndCommitsAfterSync() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let legacyFactory = FakeTransportFactory(kind: .legacySupabase)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase },
            makeLegacyForSwitch: { legacyFactory.make() }
        )
        let roomID = UUID()
        _ = try await router.synchronize(
            rooms: [Room(id: roomID, name: "친구", ownerID: UUID(), members: [], inviteCodeHint: "TEST")],
            activeRoomID: roomID
        )
        XCTAssertEqual(legacyFactory.creationCount, 0)

        _ = try await router.switchToLegacy()

        XCTAssertEqual(legacyFactory.creationCount, 1)
        let selection = try await router.currentSelection()
        XCTAssertEqual(selection.active, .legacySupabase)
        let retirementCount = await firebase.retirementCount
        XCTAssertEqual(retirementCount, 1)
    }

    @MainActor
    func testMissingFirebaseLeaseRebuildsTransportAndRestoresOperations() async throws {
        let expired = FakeRealtimeTransport(kind: .firebaseV2)
        let replacement = FakeRealtimeTransport(kind: .firebaseV2)
        let roomID = UUID()
        let rooms = [
            Room(
                id: roomID,
                name: "친구",
                ownerID: UUID(),
                members: [],
                inviteCodeHint: "TEST"
            ),
        ]
        var recoveryCreationCount = 0
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { expired },
            makeFirebaseV2ForRecovery: {
                recoveryCreationCount += 1
                return replacement
            }
        )
        _ = try await router.synchronize(rooms: rooms, activeRoomID: roomID)
        var events = router.events.makeAsyncIterator()

        try await router.refreshRolloutLease()
        guard case .reconciliation = await events.next() else {
            return XCTFail("Recovery must publish its reconciled snapshot")
        }
        await replacement.emit(.technicalError("replacement-event"))
        guard case .technicalError(let forwardedMessage) = await events.next() else {
            return XCTFail("Replacement events must reach the stable router stream")
        }
        try await router.publishCharacterPulse(roomID: roomID, eventID: UUID())
        let chat = await router.sendChat(roomID: roomID, body: "복구", id: UUID())

        let expiredRetirementCount = await expired.retirementCount
        let replacementOperationCount = await replacement.operationCount
        XCTAssertEqual(recoveryCreationCount, 1)
        XCTAssertEqual(expiredRetirementCount, 1)
        XCTAssertEqual(replacementOperationCount, 3)
        XCTAssertEqual(forwardedMessage, "replacement-event")
        guard case .confirmed = chat else {
            return XCTFail("Recovered Firebase transport must accept chat")
        }
        let diagnostics = await router.diagnostics()
        XCTAssertEqual(diagnostics.selection.active, .firebaseV2)
        XCTAssertEqual(diagnostics.lastSuccessfulProtocol, .firebaseV2)
    }

    func testPresenceDetectedBeforeBootstrapIsPublishedAfterSynchronization() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase }
        )

        try await router.setLocalPresence(.away)
        let beforeBootstrap = await firebase.publishedPresence
        XCTAssertTrue(beforeBootstrap.isEmpty)

        _ = try await router.synchronize(rooms: [], activeRoomID: nil)

        let afterBootstrap = await firebase.publishedPresence
        XCTAssertEqual(afterBootstrap, [.away])
        await router.shutdown()
    }

    func testInitialPresenceFailureDoesNotBlockReconciliationAndRetriesTheSeededState() async throws {
        let retryScheduled = expectation(description: "initial presence retry scheduled")
        let awayPublished = expectation(description: "seeded away retried successfully")
        let retryClock = PresenceRetryClock(onSleep: { retryScheduled.fulfill() })
        let firebase = FakeRealtimeTransport(
            kind: .firebaseV2,
            presenceFailureCount: 1,
            onPresencePublished: { _ in awayPublished.fulfill() }
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase },
            presenceRetrySleep: { try await retryClock.sleep($0) }
        )
        let roomID = UUID()
        let rooms = [
            Room(id: roomID, name: "친구", ownerID: UUID(), members: [], inviteCodeHint: "TEST"),
        ]
        try await router.setLocalPresence(.away)

        let reconciliation = try await router.synchronize(rooms: rooms, activeRoomID: roomID)

        XCTAssertEqual(reconciliation.snapshot.rooms.map(\.id), [roomID])
        XCTAssertEqual(reconciliation.activeRoomID, roomID)
        await fulfillment(of: [retryScheduled], timeout: 2)
        let beforeRetry = await firebase.publishedPresence
        XCTAssertTrue(beforeRetry.isEmpty)
        await retryClock.advance()
        await fulfillment(of: [awayPublished], timeout: 2)

        let attempts = await firebase.presenceAttempts
        let published = await firebase.publishedPresence
        XCTAssertEqual(attempts, [.away, .away])
        XCTAssertEqual(published, [.away])
        await router.shutdown()
    }

    func testWakeDuringFirebaseRecoveryPublishesLatestPresenceWithoutMoreActivity() async throws {
        let expired = FakeRealtimeTransport(kind: .firebaseV2)
        let replacement = FakeRealtimeTransport(kind: .firebaseV2)
        let factoryEntered = expectation(description: "replacement creation started")
        let factoryGate = PresenceTestGate()
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { expired },
            makeFirebaseV2ForRecovery: {
                factoryEntered.fulfill()
                await factoryGate.wait()
                return replacement
            }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        try await router.setLocalPresence(.away)

        let recovery = Task { try await router.refreshRolloutLease() }
        await fulfillment(of: [factoryEntered], timeout: 2)
        try await router.setLocalPresence(.online)
        try await router.setLocalPresence(.away)
        try await router.setLocalPresence(.online)
        await factoryGate.open()
        try await recovery.value

        let expiredPresence = await expired.publishedPresence
        let replacementPresence = await replacement.publishedPresence
        XCTAssertEqual(expiredPresence, [.away])
        XCTAssertEqual(replacementPresence, [.online])
        let selection = try await router.currentSelection()
        XCTAssertEqual(selection.active, .firebaseV2)
        await router.shutdown()
    }

    func testLatePresenceCompletionFromRetiredTransportCannotClearNewerIntent() async throws {
        let expired = FakeRealtimeTransport(kind: .firebaseV2)
        let replacement = FakeRealtimeTransport(kind: .firebaseV2)
        let publicationEntered = expectation(description: "old online publication suspended")
        let publicationGate = PresenceTestGate()
        let factoryEntered = expectation(description: "replacement creation started")
        let factoryGate = PresenceTestGate()
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { expired },
            makeFirebaseV2ForRecovery: {
                factoryEntered.fulfill()
                await factoryGate.wait()
                return replacement
            }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        try await router.setLocalPresence(.away)
        await expired.pauseNextPresencePublication(at: publicationGate) {
            publicationEntered.fulfill()
        }
        let oldPublication = Task { try await router.setLocalPresence(.online) }
        await fulfillment(of: [publicationEntered], timeout: 2)

        let recovery = Task { try await router.refreshRolloutLease() }
        await fulfillment(of: [factoryEntered], timeout: 2)
        try await router.setLocalPresence(.away)
        // The server completion deliberately ignores retirement/cancellation.
        await publicationGate.open()
        try await oldPublication.value
        await factoryGate.open()
        try await recovery.value

        let oldPublished = await expired.publishedPresence
        let replacementPublished = await replacement.publishedPresence
        XCTAssertEqual(oldPublished, [.away, .online])
        XCTAssertEqual(replacementPublished, [.away])
        await router.shutdown()
    }

    func testFailedPresencePublicationRetriesWithoutAnotherActivityChange() async throws {
        let retryScheduled = expectation(description: "presence retry scheduled")
        let onlinePublished = expectation(description: "online retried successfully")
        let retryClock = PresenceRetryClock(onSleep: { retryScheduled.fulfill() })
        let legacyFactory = FakeTransportFactory(kind: .legacySupabase)
        let firebase = FakeRealtimeTransport(
            kind: .firebaseV2,
            presenceFailureCount: 1,
            onPresencePublished: { _ in onlinePublished.fulfill() }
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacyFactory.make() },
            makeFirebaseV2: { firebase },
            makeLegacyForSwitch: { legacyFactory.make() },
            presenceRetrySleep: { try await retryClock.sleep($0) }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)

        try await router.setLocalPresence(.online)
        await fulfillment(of: [retryScheduled], timeout: 2)
        let beforeRetry = await firebase.publishedPresence
        XCTAssertTrue(beforeRetry.isEmpty)
        await retryClock.advance()
        await fulfillment(of: [onlinePublished], timeout: 2)

        let attempts = await firebase.presenceAttempts
        let published = await firebase.publishedPresence
        XCTAssertEqual(attempts, [.online, .online])
        XCTAssertEqual(published, [.online])
        XCTAssertEqual(legacyFactory.creationCount, 0)
        await router.shutdown()
    }

    func testClosedGrantRetainsPresenceUntilConvergenceWithoutLegacyFallback() async throws {
        let retryScheduled = expectation(description: "closed grant defers presence")
        let retryClock = PresenceRetryClock(onSleep: { retryScheduled.fulfill() })
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)
        let firebase = FakeRealtimeTransport(kind: .firebaseV2, presenceGrantClosed: true)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeLegacy: { legacy },
            makeFirebaseV2: { firebase },
            presenceRetrySleep: { try await retryClock.sleep($0) }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)

        try await router.setLocalPresence(.online)
        await fulfillment(of: [retryScheduled], timeout: 2)
        let beforeConvergence = await firebase.publishedPresence
        XCTAssertTrue(beforeConvergence.isEmpty)
        try await router.convergeAccessGrant(
            revision: XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000042")),
            requiresEntitlement: false
        )

        let afterConvergence = await firebase.publishedPresence
        let legacyOperations = await legacy.operationCount
        XCTAssertEqual(afterConvergence, [.online])
        XCTAssertEqual(legacyOperations, 0)
        await router.shutdown()
    }

    func testShutdownCancelsPendingPresenceRetryAndRejectsLaterUpdates() async throws {
        let retryScheduled = expectation(description: "presence retry scheduled")
        let retryCancelled = expectation(description: "presence retry cancelled")
        let retryClock = PresenceRetryClock(
            onSleep: { retryScheduled.fulfill() },
            onCancel: { retryCancelled.fulfill() }
        )
        let firebase = FakeRealtimeTransport(kind: .firebaseV2, presenceFailureCount: 1)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase },
            presenceRetrySleep: { try await retryClock.sleep($0) }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        try await router.setLocalPresence(.online)
        await fulfillment(of: [retryScheduled], timeout: 2)

        await router.shutdown()
        await fulfillment(of: [retryCancelled], timeout: 2)
        await retryClock.advance()
        do {
            try await router.setLocalPresence(.away)
            XCTFail("Presence updates after shutdown must be rejected")
        } catch is CancellationError {
            // The session no longer accepts desired presence.
        }

        let attempts = await firebase.presenceAttempts
        XCTAssertEqual(attempts, [.online])
    }

    func testShutdownDuringReplacementCreationCannotPublishOrReviveTheRouter() async throws {
        let expired = FakeRealtimeTransport(kind: .firebaseV2)
        let replacement = FakeRealtimeTransport(kind: .firebaseV2)
        let factoryEntered = expectation(description: "replacement creation started")
        let factoryGate = PresenceTestGate()
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { expired },
            makeFirebaseV2ForRecovery: {
                factoryEntered.fulfill()
                await factoryGate.wait()
                return replacement
            }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)
        try await router.setLocalPresence(.away)
        let recovery = Task { try await router.refreshRolloutLease() }
        await fulfillment(of: [factoryEntered], timeout: 2)
        try await router.setLocalPresence(.online)

        await router.shutdown()
        await factoryGate.open()
        do {
            try await recovery.value
            XCTFail("A replacement created after shutdown must not become ready")
        } catch {
            // Cancellation or an unavailable topology both keep the session closed.
        }

        let replacementPresence = await replacement.publishedPresence
        let replacementRetirements = await replacement.retirementCount
        XCTAssertTrue(replacementPresence.isEmpty)
        XCTAssertEqual(replacementRetirements, 1)
        await assertThrowsAsync(RoomMessagingTransportRouterError.transportTopologyUnavailable) {
            _ = try await router.currentSelection()
        }
    }

    @MainActor
    func testLegacySelectionNeverRebuildsFirebase() async throws {
        let legacy = FakeRealtimeTransport(kind: .legacySupabase)
        var recoveryCreationCount = 0
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .legacySupabase, firebaseV2Allowed: false),
            makeLegacy: { legacy },
            makeFirebaseV2ForRecovery: {
                recoveryCreationCount += 1
                return FakeRealtimeTransport(kind: .firebaseV2)
            }
        )

        try await router.refreshRolloutLease()

        let legacyRetirementCount = await legacy.retirementCount
        XCTAssertEqual(recoveryCreationCount, 0)
        XCTAssertEqual(legacyRetirementCount, 0)
    }

    @MainActor
    func testFailedFirebaseRecoveryCanRetryWithFreshGeneration() async throws {
        let expired = FakeRealtimeTransport(kind: .firebaseV2)
        let failing = FakeRealtimeTransport(
            kind: .firebaseV2,
            failure: FakeTransportError.permissionDenied
        )
        let recovered = FakeRealtimeTransport(kind: .firebaseV2)
        var recoveryAttempt = 0
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { expired },
            makeFirebaseV2ForRecovery: {
                recoveryAttempt += 1
                return recoveryAttempt == 1 ? failing : recovered
            }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)

        do {
            try await router.refreshRolloutLease()
            XCTFail("The first replacement synchronization must fail")
        } catch FakeTransportError.permissionDenied {
            // The router must return to a retryable Firebase state.
        } catch {
            XCTFail("Unexpected recovery error: \(error)")
        }
        try await router.refreshRolloutLease()
        try await router.publishTyping(roomID: UUID(), event: "typing_start")

        let recoveredOperationCount = await recovered.operationCount
        let selection = try await router.currentSelection()
        XCTAssertEqual(recoveryAttempt, 2)
        XCTAssertEqual(recoveredOperationCount, 2)
        XCTAssertEqual(selection.active, .firebaseV2)
    }

    func testKillSwitchSyncFailureBlocksAllTransportOperations() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let failingLegacy = FakeRealtimeTransport(
            kind: .legacySupabase,
            failure: FakeTransportError.permissionDenied
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase },
            makeLegacyForSwitch: { failingLegacy }
        )
        _ = try await router.synchronize(rooms: [], activeRoomID: nil)

        await assertThrowsAsync(FakeTransportError.permissionDenied) {
            _ = try await router.switchToLegacy()
        }
        await assertThrowsAsync(RoomMessagingTransportRouterError.transportTopologyUnavailable) {
            try await router.publishTyping(roomID: UUID(), event: "typing_start")
        }
        let legacyOperationCount = await failingLegacy.operationCount
        XCTAssertEqual(legacyOperationCount, 1)
    }

    func testBlockedKillSwitchCommitsLegacyOnlyAfterExplicitSynchronizationRetry() async throws {
        let firebase = FakeRealtimeTransport(kind: .firebaseV2)
        let legacy = FakeRealtimeTransport(
            kind: .legacySupabase,
            failure: FakeTransportError.permissionDenied,
            failureCount: 1
        )
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .firebaseV2, firebaseV2Allowed: true),
            makeFirebaseV2: { firebase },
            makeLegacyForSwitch: { legacy }
        )
        let roomID = UUID()
        let rooms = [
            Room(
                id: roomID,
                name: "친구",
                ownerID: UUID(),
                members: [],
                inviteCodeHint: "TEST"
            ),
        ]
        _ = try await router.synchronize(rooms: rooms, activeRoomID: roomID)

        await assertThrowsAsync(FakeTransportError.permissionDenied) {
            _ = try await router.switchToLegacy()
        }
        _ = try await router.synchronize(rooms: rooms, activeRoomID: roomID)

        let selection = try await router.currentSelection()
        XCTAssertEqual(selection.active, .legacySupabase)
        try await router.publishTyping(roomID: roomID, event: "typing_start")
        let legacyOperationCount = await legacy.operationCount
        XCTAssertEqual(legacyOperationCount, 3)
    }

    @MainActor
    func testReconciliationPendingKeepsTheOriginalUUIDPending() async throws {
        let roomID = UUID()
        let userID = UUID()
        let coordinator = AppCoordinator(
            preferencesStore: PreferencesStore(load: { .defaults }, save: { _ in }),
            legacyMigrator: .none,
            keychainAccessSession: KeychainAccessSession(),
            releaseChannel: .appStore,
            arguments: []
        )
        coordinator.model.currentUserID = userID
        coordinator.model.rooms = [
            Room(id: roomID, name: "친구", ownerID: userID, members: [], inviteCodeHint: "TEST")
        ]
        coordinator.model.preferences.activeRoomID = roomID
        let classified = expectation(description: "chat outcome classified")
        var submittedID: UUID?

        coordinator.sendMessage("확인 중", source: .history) { room, _, id -> RealtimeChatOutcome in
            XCTAssertEqual(room, roomID)
            submittedID = id
            classified.fulfill()
            return .reconciliationPending
        }

        await fulfillment(of: [classified], timeout: 2)
        for _ in 0..<10 { await Task.yield() }
        let pending = try XCTUnwrap(coordinator.model.messageOutbox.entries.first)
        XCTAssertEqual(pending.id, submittedID)
        XCTAssertEqual(pending.roomID, roomID)
        XCTAssertEqual(pending.state, .pending)
        XCTAssertNil(coordinator.model.historySendError)
        XCTAssertFalse(coordinator.overlayWindows.composerVisible)
    }

    @MainActor
    func testDisconnectedProductionSendDoesNotStageOrCallTransport() async throws {
        let transport = FakeRealtimeTransport(kind: .legacySupabase)
        let router = try RoomMessagingTransportRouter(
            selection: .resolve(requested: .legacySupabase, firebaseV2Allowed: true),
            makeLegacy: { transport }
        )
        let roomID = UUID()
        let userID = UUID()
        let coordinator = AppCoordinator(
            preferencesStore: PreferencesStore(load: { .defaults }, save: { _ in }),
            legacyMigrator: .none,
            keychainAccessSession: KeychainAccessSession(),
            releaseChannel: .appStore,
            arguments: []
        )
        coordinator.messagingTransport = router
        coordinator.model.currentUserID = userID
        coordinator.model.rooms = [
            Room(id: roomID, name: "친구", ownerID: userID, members: [], inviteCodeHint: "TEST")
        ]
        coordinator.model.preferences.activeRoomID = roomID
        coordinator.model.connectionState = .connecting
        coordinator.model.setActiveRoomRealtimeConnected(false)

        coordinator.sendMessage("로컬에만 보이면 안 됨", source: .history)

        let operationCount = await transport.operationCount
        XCTAssertEqual(operationCount, 0)
        XCTAssertTrue(coordinator.model.messageOutbox.entries.isEmpty)
        XCTAssertNotNil(coordinator.model.historySendError)
    }
}

private func assertThrowsAsync<E: Error & Equatable>(
    _ expected: E,
    operation: () async throws -> Void,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        try await operation()
        XCTFail("Expected \(expected)", file: file, line: line)
    } catch {
        XCTAssertEqual(error as? E, expected, file: file, line: line)
    }
}

private final class FakeTransportFactory: @unchecked Sendable {
    private let kind: RealtimeTransportKind
    private let lock = NSLock()
    private var storedCreationCount = 0

    var creationCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return storedCreationCount
    }

    init(kind: RealtimeTransportKind) {
        self.kind = kind
    }

    func make() -> FakeRealtimeTransport {
        lock.lock()
        storedCreationCount += 1
        lock.unlock()
        return FakeRealtimeTransport(kind: kind)
    }
}

private enum FakeTransportError: Error {
    case permissionDenied
}

private enum FakeGrantConvergenceError: Error {
    case failed
}

private struct FakeGrantRequest: Equatable, Sendable {
    let revision: RealtimeRevision
    let requiresEntitlement: Bool
}

private actor FakeRealtimeTransport: RoomMessagingTransport {
    nonisolated let kind: RealtimeTransportKind
    nonisolated let events: AsyncStream<BackendEvent>

    private let failure: (any Error)?
    private var remainingFailureCount: Int
    private let chatOutcome: RealtimeChatOutcome?
    private let eventContinuation: AsyncStream<BackendEvent>.Continuation
    private(set) var operationCount = 0
    private(set) var shutdownCount = 0
    private(set) var retirementCount = 0
    private(set) var grantRequests: [FakeGrantRequest] = []
    private(set) var presenceAttempts: [PresenceState] = []
    private(set) var publishedPresence: [PresenceState] = []
    private var presenceFailureCount: Int
    private var presenceGrantClosed: Bool
    private let onPresencePublished: (@Sendable (PresenceState) -> Void)?
    private var beforeNextPresencePublication: (@Sendable () async -> Void)?

    init(
        kind: RealtimeTransportKind,
        failure: (any Error)? = nil,
        failureCount: Int = .max,
        chatOutcome: RealtimeChatOutcome? = nil,
        presenceFailureCount: Int = 0,
        presenceGrantClosed: Bool = false,
        onPresencePublished: (@Sendable (PresenceState) -> Void)? = nil
    ) {
        self.kind = kind
        self.failure = failure
        self.remainingFailureCount = failure == nil ? 0 : failureCount
        self.chatOutcome = chatOutcome
        self.presenceFailureCount = presenceFailureCount
        self.presenceGrantClosed = presenceGrantClosed
        self.onPresencePublished = onPresencePublished
        let pair = AsyncStream<BackendEvent>.makeStream(bufferingPolicy: .bufferingNewest(8))
        self.events = pair.stream
        self.eventContinuation = pair.continuation
    }

    func synchronize(rooms: [Room], activeRoomID: UUID?) async throws -> BackendReconciliation {
        try recordOperation()
        return BackendReconciliation(
            snapshot: BackendSnapshot(profile: nil, rooms: rooms),
            activeRoomID: activeRoomID,
            activeMessages: []
        )
    }

    func setActiveRoom(_ roomID: UUID?) async throws { try recordOperation() }
    func setLocalPresence(_ state: PresenceState) async throws {
        presenceAttempts.append(state)
        try recordOperation()
        guard !presenceGrantClosed else { throw FakeTransportError.permissionDenied }
        if presenceFailureCount > 0 {
            presenceFailureCount -= 1
            throw FakeTransportError.permissionDenied
        }
        if let beforePublication = beforeNextPresencePublication {
            beforeNextPresencePublication = nil
            await beforePublication()
        }
        publishedPresence.append(state)
        onPresencePublished?(state)
    }

    func pauseNextPresencePublication(
        at gate: PresenceTestGate,
        onEnter: @escaping @Sendable () -> Void
    ) {
        beforeNextPresencePublication = {
            onEnter()
            await gate.wait()
        }
    }

    func publishTyping(roomID: UUID, event: String) async throws { try recordOperation() }
    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws { try recordOperation() }
    func publishCharacterThrow(roomID: UUID, eventID: UUID, targetUserID: UUID) async throws {
        try recordOperation()
    }
    func convergeAccessGrant(
        revision: RealtimeRevision,
        requiresEntitlement: Bool
    ) async throws {
        try recordOperation()
        presenceGrantClosed = false
        grantRequests.append(FakeGrantRequest(
            revision: revision,
            requiresEntitlement: requiresEntitlement
        ))
    }
    func sendChat(roomID: UUID, body: String, id: UUID) async -> RealtimeChatOutcome {
        do {
            try recordOperation()
            return chatOutcome ?? .confirmed(ChatMessage(
                id: id,
                roomID: roomID,
                senderID: UUID(),
                body: body,
                createdAt: .now
            ))
        } catch {
            return .definitelyRejected(message: error.localizedDescription)
        }
    }
    func shutdown() async {
        shutdownCount += 1
        eventContinuation.finish()
    }

    func retireForTransportSwitch() async {
        retirementCount += 1
        eventContinuation.finish()
    }

    func emit(_ event: BackendEvent) {
        eventContinuation.yield(event)
    }

    private func recordOperation() throws {
        operationCount += 1
        if let failure, remainingFailureCount > 0 {
            remainingFailureCount -= 1
            throw failure
        }
    }
}

private actor PresenceTestGate {
    private var isOpen = false
    private var continuation: CheckedContinuation<Void, Never>?

    func wait() async {
        guard !isOpen else { return }
        await withCheckedContinuation { continuation = $0 }
    }

    func open() {
        isOpen = true
        continuation?.resume()
        continuation = nil
    }
}

private actor PresenceRetryClock {
    private let onSleep: @Sendable () -> Void
    private let onCancel: @Sendable () -> Void
    private var continuation: CheckedContinuation<Void, any Error>?

    init(
        onSleep: @escaping @Sendable () -> Void,
        onCancel: @escaping @Sendable () -> Void = {}
    ) {
        self.onSleep = onSleep
        self.onCancel = onCancel
    }

    func sleep(_ duration: Duration) async throws {
        try Task.checkCancellation()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                onSleep()
            }
        } onCancel: {
            Task { await self.cancel() }
        }
    }

    func advance() {
        continuation?.resume()
        continuation = nil
    }

    private func cancel() {
        continuation?.resume(throwing: CancellationError())
        continuation = nil
        onCancel()
    }
}
