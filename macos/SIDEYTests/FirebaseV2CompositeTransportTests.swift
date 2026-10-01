import FirebaseFunctions
import XCTest
@testable import SIDEY

final class FirebaseV2CompositeTransportTests: XCTestCase {
    func testFirebaseSelectionPublishesTransientsOnlyThroughRTDB() async throws {
        let fixture = try Fixture()

        let reconciliation = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        XCTAssertEqual(reconciliation.activeRoomID, fixture.room.id)

        try await fixture.transport.publishTyping(
            roomID: fixture.room.id,
            event: "typing_start"
        )
        let pulseID = UUID()
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id,
            eventID: pulseID
        )
        let throwID = UUID()
        try await fixture.transport.publishCharacterThrow(
            roomID: fixture.room.id,
            eventID: throwID,
            targetUserID: fixture.friendID
        )
        let chatID = UUID()
        let chat = await fixture.transport.sendChat(
            roomID: fixture.room.id,
            body: "hello",
            id: chatID
        )

        let establishCount = await fixture.credentials.establishCount
        let transientPublications = await fixture.supabase.transientPublications
        let directRTDBWrites = fixture.values.writeOperations
        let chatCallCount = await fixture.chatCaller.callCount
        XCTAssertEqual(establishCount, 1)
        XCTAssertEqual(
            fixture.values.paths,
            [
                FirebaseV2Path.inbox(userID: fixture.ownID),
                FirebaseV2Path.liveRoom(fixture.room.id),
            ]
        )
        XCTAssertTrue(transientPublications.isEmpty)
        XCTAssertEqual(directRTDBWrites, [
            .setServerTimestamp(path: FirebaseV2Path.typing(
                roomID: fixture.room.id,
                userID: fixture.ownID,
                sessionID: fixture.sessionID
            )),
            .setServerTimestamp(path: FirebaseV2Path.pulse(
                roomID: fixture.room.id,
                userID: fixture.ownID
            )),
            .setThrow(
                path: FirebaseV2Path.characterThrow(
                    roomID: fixture.room.id,
                    userID: fixture.ownID
                ),
                targetUserID: fixture.friendID,
                wireCode: fixture.throwWireCode
            ),
        ])
        guard case .confirmed(let message) = chat else {
            return XCTFail("Firebase callable result must be returned")
        }
        XCTAssertEqual(message.id, chatID)
        XCTAssertEqual(chatCallCount, 1)

        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.inboxListenerCount, 1)
        XCTAssertEqual(diagnostics.liveListenerCount, 1)
        XCTAssertTrue(diagnostics.grantOpen)
    }

    func testFirebaseSelectionIgnoresSupabaseTransientsButForwardsPresence() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        var iterator = fixture.transport.events.makeAsyncIterator()

        await fixture.supabase.emit(.typing(
            roomID: fixture.room.id,
            userID: fixture.friendID,
            active: true
        ))
        await fixture.supabase.emit(.characterPulse(CharacterPulseEvent(
            id: UUID(),
            roomID: fixture.room.id,
            userID: fixture.friendID
        )))
        await fixture.supabase.emit(.characterThrow(CharacterThrowEvent(
            id: UUID(),
            roomID: fixture.room.id,
            actorUserID: fixture.friendID,
            targetUserID: fixture.ownID,
            sourceCharacterID: "pixel_cat",
            throwableID: "throwable_leaf"
        )))
        await fixture.supabase.emit(.presence(
            roomID: fixture.room.id,
            userID: fixture.friendID,
            state: .online
        ))

        guard case .presence(let roomID, let userID, let state) = await iterator.next()
        else { return XCTFail("Presence event must be forwarded") }
        XCTAssertEqual(roomID, fixture.room.id)
        XCTAssertEqual(userID, fixture.friendID)
        XCTAssertEqual(state, .online)
    }

    func testRoomSwitchReusesWarmLiveListenersAndRejectsUnauthorizedRoom() async throws {
        let secondRoomID = UUID()
        let fixture = try Fixture(additionalAuthorizedRoomID: secondRoomID)
        let secondRoom = fixture.makeRoom(id: secondRoomID)
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])

        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom],
            activeRoomID: fixture.room.id
        )
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id,
            eventID: UUID()
        )
        try await fixture.transport.setActiveRoom(secondRoomID)
        try await fixture.transport.publishCharacterPulse(
            roomID: secondRoomID,
            eventID: UUID()
        )

        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.activeRoomID, secondRoomID)
        XCTAssertEqual(diagnostics.liveListenerCount, 2)
        XCTAssertEqual(
            fixture.values.paths,
            [
                FirebaseV2Path.inbox(userID: fixture.ownID),
                FirebaseV2Path.liveRoom(fixture.room.id),
                FirebaseV2Path.liveRoom(secondRoomID),
            ]
        )
        XCTAssertEqual(fixture.values.writeOperations, [
            .setServerTimestamp(path: FirebaseV2Path.pulse(
                roomID: fixture.room.id,
                userID: fixture.ownID
            )),
            .setServerTimestamp(path: FirebaseV2Path.pulse(
                roomID: secondRoomID,
                userID: fixture.ownID
            )),
        ])
        await assertThrowsAsync(
            FirebaseV2CompositeTransportError.roomNotAuthorized
        ) {
            try await fixture.transport.setActiveRoom(UUID())
        }
        try await fixture.transport.setActiveRoom(fixture.room.id)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
    }

    func testLatestRoomChoiceWinsWhenEarlierPresencePreparationFinishesLate() async throws {
        let secondRoomID = UUID()
        let fixture = try Fixture(additionalAuthorizedRoomID: secondRoomID)
        let secondRoom = fixture.makeRoom(id: secondRoomID)
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom],
            activeRoomID: fixture.room.id
        )
        await fixture.supabase.blockSetActiveRoom(secondRoomID)

        let staleChoice = Task {
            try await fixture.transport.setActiveRoom(secondRoomID)
        }
        try await eventually {
            await fixture.supabase.isSetActiveRoomBlocked(secondRoomID)
        }
        try await fixture.transport.setActiveRoom(fixture.room.id)
        await fixture.supabase.resumeSetActiveRoom(secondRoomID)

        do {
            try await staleChoice.value
            XCTFail("Earlier room choice must not commit after a newer choice")
        } catch is CancellationError {
            // Expected latest-wins invalidation.
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.activeRoomID, fixture.room.id)
        XCTAssertEqual(diagnostics.liveListenerCount, 2)
    }

    func testRepeatedSynchronizationKeepsLiveListenerAndDeliversLaterThrow() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )

        var iterator = fixture.transport.events.makeAsyncIterator()
        fixture.values.yield(Data("""
        {
          "x": {
            "\(fixture.friendID.uuidString.lowercased())": {
              "k": "18",
              "t": 1800000000000,
              "u": "\(fixture.ownID.uuidString.lowercased())"
            }
          }
        }
        """.utf8), at: FirebaseV2Path.liveRoom(fixture.room.id))

        guard case .characterThrow(let characterThrow) = await iterator.next()
        else { return XCTFail("The retained listener must deliver a later throw") }
        XCTAssertEqual(characterThrow.actorUserID, fixture.friendID)
        XCTAssertEqual(characterThrow.targetUserID, fixture.ownID)

        let establishCount = await fixture.credentials.establishCount
        XCTAssertEqual(establishCount, 1)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.inbox(userID: fixture.ownID)
        }.count, 1)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
    }

    func testMemberJoiningAnotherRoomKeepsBothLiveListenersAndActiveDelivery() async throws {
        let secondRoomID = UUID()
        let fixture = try Fixture(additionalAuthorizedRoomID: secondRoomID)
        var secondRoom = fixture.makeRoom(id: secondRoomID)
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom], activeRoomID: fixture.room.id
        )

        secondRoom.members.append(RoomMember(
            userID: UUID(), nickname: "새 친구", characterID: "pixel_penguin",
            presence: .offline
        ))
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom], activeRoomID: fixture.room.id
        )

        let activePath = FirebaseV2Path.liveRoom(fixture.room.id)
        let changedPath = FirebaseV2Path.liveRoom(secondRoomID)
        XCTAssertEqual(fixture.values.paths.filter { $0 == activePath }.count, 1)
        XCTAssertEqual(fixture.values.paths.filter { $0 == changedPath }.count, 1)
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.liveListenerCount, 2)

        var events = fixture.transport.events.makeAsyncIterator()
        fixture.values.yield(Data("""
        {"x":{"\(fixture.friendID.uuidString.lowercased())":{
          "k":"18","t":1800000000000,"u":"\(fixture.ownID.uuidString.lowercased())"
        }}}
        """.utf8), at: activePath)
        guard case .characterThrow(let value) = await events.next() else {
            return XCTFail("An unchanged active room must keep delivering events")
        }
        XCTAssertEqual(value.actorUserID, fixture.friendID)
    }

    func testLiveListenerMustDeliverInitialSnapshotBeforeRoomCommit() async throws {
        let fixture = try Fixture(
            automaticallyYieldLiveBaseline: false,
            liveReadinessTimeout: .milliseconds(20)
        )

        await assertThrowsAsync(FirebaseV2CompositeTransportError.liveListenerUnavailable) {
            _ = try await fixture.transport.synchronize(
                rooms: [fixture.room],
                activeRoomID: fixture.room.id
            )
        }
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertNil(diagnostics.activeRoomID)
        XCTAssertEqual(diagnostics.liveListenerCount, 0)
    }

    func testInboxAdvanceTriggersAuthoritativeReconciliation() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        let inboxPath = FirebaseV2Path.inbox(userID: fixture.ownID)
        fixture.values.yield(
            Data(#"{"a":"00000000000000000001","r":{}}"#.utf8),
            at: inboxPath
        )
        fixture.values.yield(
            Data(#"{"a":"00000000000000000002","r":{}}"#.utf8),
            at: inboxPath
        )

        try await eventually {
            let establishCount = await fixture.credentials.establishCount
            let synchronizeCount = await fixture.supabase.synchronizeCount
            return establishCount == 2
                && synchronizeCount >= 2
                && fixture.values.paths.filter {
                    $0 == FirebaseV2Path.liveRoom(fixture.room.id)
                }.count == 1
        }
        let requests = await fixture.credentials.establishRequests
        XCTAssertEqual(requests, [
            CompositeCredentialRequest(trigger: .sessionRestore, minimumAccessRevision: nil),
            CompositeCredentialRequest(
                trigger: .explicitRecovery,
                minimumAccessRevision: try XCTUnwrap(
                    RealtimeRevision(rawValue: "00000000000000000002")
                )
            ),
        ])
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.inbox(userID: fixture.ownID)
        }.count, 2)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
    }

    func testMembershipRevisionKeepsAuthorizedListenersAndPrunesRevokedRoom() async throws {
        let secondRoomID = UUID()
        let fixture = try Fixture(
            additionalAuthorizedRoomID: secondRoomID,
            recoveryRevokesAdditionalRoom: true
        )
        let secondRoom = fixture.makeRoom(id: secondRoomID)
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom], activeRoomID: fixture.room.id
        )
        fixture.values.yield(
            Data(#"{"a":"00000000000000000002","r":{}}"#.utf8),
            at: FirebaseV2Path.inbox(userID: fixture.ownID)
        )

        try await eventually {
            let diagnostics = await fixture.transport.diagnostics()
            let synchronizeCount = await fixture.supabase.synchronizeCount
            return synchronizeCount >= 2 && diagnostics.liveListenerCount == 1
        }
        let activePath = FirebaseV2Path.liveRoom(fixture.room.id)
        let revokedPath = FirebaseV2Path.liveRoom(secondRoomID)
        XCTAssertEqual(fixture.values.paths.filter { $0 == activePath }.count, 1)
        XCTAssertEqual(fixture.values.paths.filter { $0 == revokedPath }.count, 1)
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id, eventID: UUID()
        )
        await assertThrowsAsync(FirebaseV2CompositeTransportError.roomNotAuthorized) {
            try await fixture.transport.setActiveRoom(secondRoomID)
        }
    }

    func testPeerMembershipRevisionKeepsBothAuthorizedRoomListeners() async throws {
        let secondRoomID = UUID()
        let fixture = try Fixture(additionalAuthorizedRoomID: secondRoomID)
        let secondRoom = fixture.makeRoom(id: secondRoomID)
        await fixture.supabase.replaceRooms([fixture.room, secondRoom])
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room, secondRoom], activeRoomID: fixture.room.id
        )
        fixture.values.yield(
            Data(#"{"a":"00000000000000000002","r":{}}"#.utf8),
            at: FirebaseV2Path.inbox(userID: fixture.ownID)
        )

        try await eventually {
            let establishCount = await fixture.credentials.establishCount
            let synchronizeCount = await fixture.supabase.synchronizeCount
            return establishCount == 2 && synchronizeCount >= 2
        }
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(secondRoomID)
        }.count, 1)
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.liveListenerCount, 2)
        XCTAssertTrue(diagnostics.grantOpen)
    }

    func testAccessRevisionRecoveryFailurePreservesSharedBackendForRetry() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room], activeRoomID: fixture.room.id
        )
        await fixture.credentials.failNextRecovery()
        let inboxPath = FirebaseV2Path.inbox(userID: fixture.ownID)
        fixture.values.yield(
            Data(#"{"a":"00000000000000000001","r":{}}"#.utf8),
            at: inboxPath
        )
        fixture.values.yield(
            Data(#"{"a":"00000000000000000002","r":{}}"#.utf8),
            at: inboxPath
        )

        try await eventually {
            await fixture.transport.diagnostics().shutDown
        }
        let shutdownCount = await fixture.supabase.shutdownCount
        XCTAssertEqual(shutdownCount, 0)
        let requests = await fixture.credentials.establishRequests
        XCTAssertEqual(requests.last?.minimumAccessRevision?.rawValue,
                       "00000000000000000002")
        await assertThrowsAsync(FirebaseV2CompositeTransportError.shutDown) {
            try await fixture.transport.publishCharacterPulse(
                roomID: fixture.room.id, eventID: UUID()
            )
        }
    }

    func testFirebaseSelectionConsumesTransientsAndReadsChatByIDWithoutFullSnapshot() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        var iterator = fixture.transport.events.makeAsyncIterator()
        let livePath = FirebaseV2Path.liveRoom(fixture.room.id)
        let durableMessage = ChatMessage(id: UUID(uuidString: "22222222-2222-4222-8222-222222222222")!,
            roomID: fixture.room.id, senderID: fixture.friendID, body: "Postgres body",
            createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(durableMessage)
        fixture.values.yield(Data("{}".utf8), at: livePath)
        fixture.values.yield(Data("""
        {
          "t": {
            "\(fixture.friendID.uuidString.lowercased())": {
              "11111111-1111-4111-8111-111111111111": 1800000000000
            }
          },
          "c": {"\(fixture.friendID.uuidString.lowercased())": 1800000000000},
          "x": {
            "\(fixture.friendID.uuidString.lowercased())": {
              "k": "18",
              "t": 1800000000000,
              "u": "\(fixture.ownID.uuidString.lowercased())"
            }
          },
          "e": {
            "b": "hint-only",
            "i": "22222222-2222-4222-8222-222222222222",
            "n": 1,
            "s": "\(fixture.friendID.uuidString.lowercased())",
            "t": 1800000000000
          }
        }
        """.utf8), at: livePath)

        guard case .typing(let typingRoomID, let typingUserID, let active) = await iterator.next()
        else { return XCTFail("Expected Firebase typing event") }
        XCTAssertEqual(typingRoomID, fixture.room.id)
        XCTAssertEqual(typingUserID, fixture.friendID)
        XCTAssertTrue(active)

        guard case .characterPulse(let pulse) = await iterator.next()
        else { return XCTFail("Expected Firebase pulse event") }
        XCTAssertEqual(pulse.roomID, fixture.room.id)
        XCTAssertEqual(pulse.userID, fixture.friendID)

        guard case .characterThrow(let characterThrow) = await iterator.next()
        else { return XCTFail("Expected Firebase throw event") }
        XCTAssertEqual(characterThrow.roomID, fixture.room.id)
        XCTAssertEqual(characterThrow.actorUserID, fixture.friendID)
        XCTAssertEqual(characterThrow.targetUserID, fixture.ownID)
        XCTAssertEqual(characterThrow.throwableID, "throwable_leaf")

        guard case .message(let verified) = await iterator.next()
        else { return XCTFail("Expected a Postgres-verified message") }
        XCTAssertEqual(verified, durableMessage)
        let synchronizeCount = await fixture.supabase.synchronizeCount
        let requests = await fixture.supabase.messageRequests
        XCTAssertEqual(requests, [durableMessage.id])
        XCTAssertEqual(synchronizeCount, 1)
    }

    func testInboxAndLiveForOneSequenceShareOneVerifiedIDQuery() async throws {
        let fixture = try Fixture()
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "verified", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        let inbox = FirebaseV2Path.inbox(userID: fixture.ownID)
        fixture.values.yield(Data("""
        {"a":"00000000000000000001","r":{"\(fixture.room.id.uuidString)":{}}}
        """.utf8), at: inbox)
        fixture.values.yield(Data("""
        {"a":"00000000000000000001","r":{"\(fixture.room.id.uuidString)":{"n":1}}}
        """.utf8), at: inbox)
        fixture.values.yield(chatSnapshot(message, sequence: 1), at: FirebaseV2Path.liveRoom(fixture.room.id))
        var iterator = fixture.transport.events.makeAsyncIterator()
        let event = await iterator.next()
        guard case .message(let received) = event else { return XCTFail("Expected one verified row; got \(String(describing: event))") }
        XCTAssertEqual(received, message)
        let requests = await fixture.supabase.messageRequests
        let recent = await fixture.supabase.recentRequests
        let full = await fixture.supabase.synchronizeCount
        XCTAssertEqual(requests, [message.id])
        XCTAssertTrue(recent.isEmpty)
        XCTAssertEqual(full, 1)
    }

    func testSequenceGapRepairsOnlyRecentMessagesWithoutFullSnapshot() async throws {
        let fixture = try Fixture()
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "last", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        fixture.values.yield(chatSnapshot(message, sequence: 7), at: FirebaseV2Path.liveRoom(fixture.room.id))
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .messagesReplaced(let roomID, let messages) = await iterator.next()
        else { return XCTFail("A compact-slot gap must repair the recent window") }
        XCTAssertEqual(roomID, fixture.room.id)
        XCTAssertEqual(messages, [message])
        let recent = await fixture.supabase.recentRequests
        let full = await fixture.supabase.synchronizeCount
        XCTAssertEqual(recent, [fixture.room.id])
        XCTAssertEqual(full, 1)
    }

    func testFailedIDReadRetainsDirtySequenceUntilRetrySucceeds() async throws {
        let fixture = try Fixture(deliverySleep: { _ in try await Task.sleep(for: .milliseconds(1)) })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "retry", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        await fixture.supabase.failNextMessage()
        fixture.values.yield(chatSnapshot(message, sequence: 1), at: FirebaseV2Path.liveRoom(fixture.room.id))
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .technicalError = await iterator.next() else { return XCTFail("Expected read failure") }
        guard case .message(let received) = await iterator.next() else { return XCTFail("Dirty ID must retry") }
        XCTAssertEqual(received, message)
        let requests = await fixture.supabase.messageRequests
        XCTAssertEqual(requests, [message.id, message.id])
    }

    func testOverflowPublishesOneAtomicMessagePresenceAndVisibilityBaselineWithoutRepairLoop() async throws {
        let fixture = try Fixture(eventBufferLimit: 1)
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        await fixture.supabase.storeOverlaySuppression(roomID: fixture.room.id,
            userID: fixture.friendID, suppressed: true)
        await fixture.supabase.emit(.presence(roomID: fixture.room.id, userID: fixture.friendID, state: .away))
        await fixture.supabase.emit(.presence(roomID: fixture.room.id, userID: fixture.friendID, state: .online))
        try await eventually {
            let count = await fixture.supabase.repairCount
            let pending = await fixture.transport.diagnostics().eventRepairPending
            return count == 1 && !pending
        }
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .eventGapRepaired(let repair) = await iterator.next() else { return XCTFail("Expected atomic repair") }
        XCTAssertEqual(repair.reconciliation.snapshot.rooms.map(\.id), [fixture.room.id])
        XCTAssertEqual(repair.presence.count, fixture.room.members.count + 1)
        XCTAssertTrue(repair.presence.contains { event in
            guard case .overlaySuppression(let roomID, let userID, let suppressed) = event else { return false }
            return roomID == fixture.room.id && userID == fixture.friendID && suppressed
        })
        let count = await fixture.supabase.repairCount
        XCTAssertEqual(count, 1)
    }

    func testFirstInboxSequenceDoesNotSkipRowsCommittedAfterStartupRead() async throws {
        let fixture = try Fixture()
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "startup race", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        fixture.values.yield(Data("""
        {"a":"00000000000000000001","r":{"\(fixture.room.id.uuidString)":{"n":7}}}
        """.utf8), at: FirebaseV2Path.inbox(userID: fixture.ownID))
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .messagesReplaced(let roomID, let rows) = await iterator.next()
        else { return XCTFail("First compact sequence must query its recent window") }
        XCTAssertEqual(roomID, fixture.room.id)
        XCTAssertEqual(rows, [message])
        let full = await fixture.supabase.synchronizeCount
        XCTAssertEqual(full, 1)
    }

    func testDurableEventDuringSlowOverflowRepairRetainsDirtyStateForAnotherPass() async throws {
        let barrier = CompositeRepairBarrier()
        let fixture = try Fixture(eventBufferLimit: 1,
            deliverySleep: { _ in try await Task.sleep(for: .milliseconds(1)) })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        await fixture.supabase.beforeNextRepair { await barrier.suspend() }
        await fixture.supabase.emit(.messagesInvalidated(roomID: fixture.room.id))
        await fixture.supabase.emit(.messagesInvalidated(roomID: fixture.room.id))
        try await eventually { await barrier.isSuspended }
        await fixture.supabase.emit(.presence(roomID: fixture.room.id, userID: fixture.friendID, state: .away))
        // Observe the forwarded event before releasing the HTTP stand-in.
        var iterator = fixture.transport.events.makeAsyncIterator()
        var receivedPresence = false
        while !receivedPresence, let event = await iterator.next() {
            if case .presence = event { receivedPresence = true }
        }
        XCTAssertTrue(receivedPresence)
        await barrier.release()
        try await eventually {
            let count = await fixture.supabase.repairCount
            let pending = await fixture.transport.diagnostics().eventRepairPending
            return count == 2 && !pending
        }
        guard case .eventGapRepaired = await iterator.next() else { return XCTFail("Expected repaired baseline") }
    }

    func testLiveOverlaySuppressionDuringSlowOverflowRepairRequiresExactlyOneFollowup() async throws {
        let barrier = CompositeRepairBarrier()
        let fixture = try Fixture(eventBufferLimit: 1,
            deliverySleep: { _ in try await Task.sleep(for: .milliseconds(1)) })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        await fixture.supabase.storeOverlaySuppression(roomID: fixture.room.id,
            userID: fixture.friendID, suppressed: false)
        await fixture.supabase.beforeNextRepair { await barrier.suspend() }
        await fixture.supabase.emit(.messagesInvalidated(roomID: fixture.room.id))
        await fixture.supabase.emit(.messagesInvalidated(roomID: fixture.room.id))
        try await eventually { await barrier.isSuspended }

        let forwarded = expectation(description: "Live suppression is forwarded while repair is suspended")
        let repaired = expectation(description: "Repair retains the latest suppression baseline")
        let consumer = Task {
            var observedRepair = false
            for await event in fixture.transport.events {
                if case .overlaySuppression(let roomID, let userID, let suppressed) = event {
                    XCTAssertEqual(roomID, fixture.room.id)
                    XCTAssertEqual(userID, fixture.friendID)
                    XCTAssertTrue(suppressed)
                    forwarded.fulfill()
                } else if case .eventGapRepaired(let baseline) = event {
                    XCTAssertTrue(baseline.presence.contains { event in
                        guard case .overlaySuppression(let roomID, let userID, let suppressed) = event else { return false }
                        return roomID == fixture.room.id && userID == fixture.friendID && suppressed
                    })
                    if !observedRepair { observedRepair = true; repaired.fulfill() }
                }
            }
        }
        defer { consumer.cancel() }
        await fixture.supabase.emit(.overlaySuppression(roomID: fixture.room.id,
            userID: fixture.friendID, suppressed: true))
        await fulfillment(of: [forwarded], timeout: 2)
        await barrier.release()
        await fulfillment(of: [repaired], timeout: 2)
        try await eventually {
            let count = await fixture.supabase.repairCount
            let pending = await fixture.transport.diagnostics().eventRepairPending
            return count == 2 && !pending
        }
    }

    func testInternalIDReadCancellationRetriesWithoutAnotherFirebaseNotification() async throws {
        let fixture = try Fixture(deliverySleep: { _ in try await Task.sleep(for: .milliseconds(1)) })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "retry stale query", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        await fixture.supabase.cancelNextMessage()
        fixture.values.yield(chatSnapshot(message, sequence: 1), at: FirebaseV2Path.liveRoom(fixture.room.id))
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .message(let received) = await iterator.next() else { return XCTFail("Dirty sequence must retry") }
        XCTAssertEqual(received, message)
        let requests = await fixture.supabase.messageRequests
        XCTAssertEqual(requests, [message.id, message.id])
    }

    func testRoomEpochChangeDuringIDReadDiscardsItAndRepairsRecentWindow() async throws {
        let barrier = CompositeRepairBarrier()
        let fixture = try Fixture(deliverySleep: { _ in try await Task.sleep(for: .milliseconds(1)) })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        let message = ChatMessage(id: UUID(), roomID: fixture.room.id, senderID: fixture.friendID,
            body: "epoch race", createdAt: Date(timeIntervalSince1970: 1_800_000_000))
        await fixture.supabase.store(message)
        await fixture.supabase.beforeNextMessage { await barrier.suspend() }
        fixture.values.yield(chatSnapshot(message, sequence: 1), at: FirebaseV2Path.liveRoom(fixture.room.id))
        try await eventually { await barrier.isSuspended }
        var nextRoom = fixture.room
        nextRoom.realtimeEpoch += 1
        await fixture.supabase.replaceRooms([nextRoom])
        await fixture.supabase.emitSnapshot()
        var iterator = fixture.transport.events.makeAsyncIterator()
        guard case .snapshot(let state) = await iterator.next() else { return XCTFail("Expected new membership epoch") }
        XCTAssertEqual(state.rooms.first?.realtimeEpoch, nextRoom.realtimeEpoch)
        await barrier.release()
        guard case .messagesReplaced(let roomID, let rows) = await iterator.next()
        else { return XCTFail("Old-epoch ID result must not publish as a live message") }
        XCTAssertEqual(roomID, nextRoom.id)
        XCTAssertEqual(rows, [message])
        let recent = await fixture.supabase.recentRequests
        XCTAssertEqual(recent, [nextRoom.id])
    }

    private func chatSnapshot(_ message: ChatMessage, sequence: Int64) -> Data {
        Data("""
        {"e":{"b":"untrusted hint","i":"\(message.id.uuidString)","n":\(sequence),
        "s":"\(message.senderID.uuidString)","t":1800000000000}}
        """.utf8)
    }

    func testServerOffsetCorrectsBothClockDirectionsAndStopsOnShutdown() async throws {
        // Ten minutes of wall-clock skew is outside both freshness windows and
        // longer than the rollout lease. Only transient time must be corrected.
        for offset: Int64 in [-600_000, 600_000] {
            let offsets = CompositeServerOffsetStub()
            let fixture = try Fixture(serverTimeOffsets: offsets)
            _ = try await fixture.transport.synchronize(
                rooms: [fixture.room], activeRoomID: fixture.room.id
            )
            let received = expectation(description: "fresh remote pulse and throw with offset \(offset)")
            received.expectedFulfillmentCount = 2
            let events = fixture.transport.events
            let receiver = Task {
                for await event in events {
                    switch event {
                    case .characterPulse(let pulse):
                        XCTAssertEqual(pulse.userID, fixture.friendID)
                        received.fulfill()
                    case .characterThrow(let value):
                        XCTAssertEqual(value.actorUserID, fixture.friendID)
                        XCTAssertEqual(value.targetUserID, fixture.ownID)
                        received.fulfill()
                    default: break
                    }
                }
            }
            offsets.yield(offset)
            try await eventually {
                await fixture.transport.diagnostics().serverTimeOffsetMilliseconds == offset
            }
            let timestamp = 1_800_000_000_000 + offset
            fixture.values.yield(Data("""
            {"c":{"\(fixture.friendID.uuidString.lowercased())":\(timestamp)},
             "x":{"\(fixture.friendID.uuidString.lowercased())":{
               "k":"18","t":\(timestamp),"u":"\(fixture.ownID.uuidString.lowercased())"}}}
            """.utf8), at: FirebaseV2Path.liveRoom(fixture.room.id))
            await fulfillment(of: [received], timeout: 2)
            let lease = await fixture.transport.rolloutLeaseStatus()
            XCTAssertGreaterThan(try XCTUnwrap(lease).expiresIn, .seconds(250))
            await fixture.transport.shutdown()
            await receiver.value
            try await eventually { offsets.isCancelled }
            XCTAssertEqual(offsets.subscriptionCount, 1)
        }
    }

    func testOffsetRefreshReschedulesTypingExpiry() async throws {
        let offsets = CompositeServerOffsetStub()
        let fixture = try Fixture(serverTimeOffsets: offsets)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room], activeRoomID: fixture.room.id
        )
        let typingStarted = expectation(description: "typing began")
        let typingStopped = expectation(description: "updated server time expired typing")
        let pulseReceived = expectation(description: "pulse plays once")
        let events = fixture.transport.events
        let receiver = Task {
            for await event in events {
                switch event {
                case .typing(_, _, let active):
                    (active ? typingStarted : typingStopped).fulfill()
                case .characterPulse:
                    pulseReceived.fulfill()
                default: break
                }
            }
        }
        let livePath = FirebaseV2Path.liveRoom(fixture.room.id)
        let data = Data("""
        {"c":{"\(fixture.friendID.uuidString.lowercased())":1800000000000},
         "t":{"\(fixture.friendID.uuidString.lowercased())":{
           "11111111-1111-4111-8111-111111111111":1800000000000}}}
        """.utf8)
        fixture.values.yield(data, at: livePath)
        await fulfillment(of: [typingStarted, pulseReceived], timeout: 2)
        offsets.yield(6_001)
        await fulfillment(of: [typingStopped], timeout: 2)
        await fixture.transport.shutdown()
        await receiver.value
        try await eventually { offsets.isCancelled }
    }

    func testUnknownFirebaseThrowableWireCodeIsDroppedWithoutBlockingTyping() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        var iterator = fixture.transport.events.makeAsyncIterator()
        let livePath = FirebaseV2Path.liveRoom(fixture.room.id)
        fixture.values.yield(Data("{}".utf8), at: livePath)
        fixture.values.yield(Data("""
        {
          "t": {
            "\(fixture.friendID.uuidString.lowercased())": {
              "11111111-1111-4111-8111-111111111111": 1800000000000
            }
          },
          "x": {
            "\(fixture.friendID.uuidString.lowercased())": {
              "k": "999999",
              "t": 1800000000000,
              "u": "\(fixture.ownID.uuidString.lowercased())"
            }
          }
        }
        """.utf8), at: livePath)

        guard case .typing(_, let userID, let active) = await iterator.next()
        else { return XCTFail("Valid Firebase typing must survive an unknown throw code") }
        XCTAssertEqual(userID, fixture.friendID)
        XCTAssertTrue(active)

        await fixture.supabase.emit(.presence(
            roomID: fixture.room.id,
            userID: fixture.friendID,
            state: .online
        ))
        guard case .presence = await iterator.next()
        else { return XCTFail("Unknown wire code must not emit a throw event") }
    }

    func testGrantBarrierBlocksWritesUntilRequestedRevisionIsAcknowledged() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        let nextRevision = try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000002"))
        await fixture.transport.beginGrant(
            revision: nextRevision,
            requiresEntitlement: true
        )

        await assertThrowsAsync(FirebaseV2CompositeTransportError.accessGrantClosed) {
            try await fixture.transport.publishCharacterPulse(
                roomID: fixture.room.id,
                eventID: UUID()
            )
        }
        try await fixture.transport.convergeGrant(
            revision: nextRevision,
            membershipValid: true,
            entitlementValid: true
        )
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id,
            eventID: UUID()
        )
        let publicationCount = await fixture.supabase.transientPublications.count
        XCTAssertEqual(publicationCount, 0)
        XCTAssertEqual(fixture.values.writeOperations.count, 1)
    }

    func testSynchronizationRetriesOnlyPendingGrantAfterCommittedMutation() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        let nextRevision = try XCTUnwrap(
            RealtimeRevision(rawValue: "00000000000000000002")
        )
        await fixture.credentials.failNextRecovery()

        do {
            try await fixture.transport.convergeAccessGrant(
                revision: nextRevision,
                requiresEntitlement: false
            )
            XCTFail("The first grant convergence must fail")
        } catch CompositeCredentialError.recoveryFailed {
            // The server mutation is already committed; only convergence may retry.
        }
        var diagnostics = await fixture.transport.diagnostics()
        XCTAssertFalse(diagnostics.grantOpen)

        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id,
            eventID: UUID()
        )

        diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.grantOpen)
        let requests = await fixture.credentials.establishRequests
        XCTAssertEqual(requests, [
            CompositeCredentialRequest(trigger: .sessionRestore, minimumAccessRevision: nil),
            CompositeCredentialRequest(
                trigger: .explicitRecovery,
                minimumAccessRevision: nextRevision
            ),
            CompositeCredentialRequest(
                trigger: .explicitRecovery,
                minimumAccessRevision: nextRevision
            ),
        ])
        let publicationCount = await fixture.supabase.transientPublications.count
        XCTAssertEqual(publicationCount, 0)
        XCTAssertEqual(fixture.values.writeOperations.count, 1)
    }

    func testCredentialGrantRecoveryKeepsAuthorizedRoomWhenSupabasePresenceIsUnavailable() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room], activeRoomID: fixture.room.id
        )
        let previousPresenceUpdates = await fixture.supabase.activeRoomRequests.count
        await fixture.supabase.rejectActiveRoomUpdates()
        let nextRevision = try XCTUnwrap(
            RealtimeRevision(rawValue: "00000000000000000002")
        )

        try await fixture.transport.convergeAccessGrant(
            revision: nextRevision,
            requiresEntitlement: false
        )
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.grantOpen)
        XCTAssertFalse(diagnostics.shutDown)
        XCTAssertEqual(diagnostics.activeRoomID, fixture.room.id)
        let presenceUpdates = await fixture.supabase.activeRoomRequests.count
        let invalidations = await fixture.credentials.invalidateCount
        XCTAssertEqual(presenceUpdates, previousPresenceUpdates)
        XCTAssertEqual(invalidations, 0)
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id, eventID: UUID()
        )
        XCTAssertEqual(fixture.values.writeOperations.count, 1)
    }

    func testShutdownCancelsBothPlanesAndPreventsFurtherOperations() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )

        await fixture.transport.shutdown()
        await fixture.transport.shutdown()

        let invalidateCount = await fixture.credentials.invalidateCount
        let shutdownCount = await fixture.supabase.shutdownCount
        XCTAssertEqual(invalidateCount, 1)
        XCTAssertEqual(shutdownCount, 1)
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.shutDown)
        XCTAssertEqual(diagnostics.inboxListenerCount, 0)
        XCTAssertEqual(diagnostics.liveListenerCount, 0)
        await assertThrowsAsync(FirebaseV2CompositeTransportError.shutDown) {
            try await fixture.transport.setLocalPresence(.online)
        }
    }

    func testFirebaseIdentityInvalidationTearsDownListenersButPreservesSupabaseForRecovery() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        var events = fixture.transport.events.makeAsyncIterator()

        await fixture.credentials.emitInvalidation()

        guard case .connection(let status) = await events.next() else {
            return XCTFail("Identity invalidation must publish disconnected state")
        }
        XCTAssertFalse(status.isReady)
        XCTAssertFalse(status.activeRoomTransportConnected)
        guard case .technicalError = await events.next() else {
            return XCTFail("Disconnected state must precede the user-facing error")
        }
        try await eventually {
            let diagnostics = await fixture.transport.diagnostics()
            let invalidateCount = await fixture.credentials.invalidateCount
            let activeRoomRequests = await fixture.supabase.activeRoomRequests
            return diagnostics.shutDown
                && invalidateCount == 1
                && activeRoomRequests.last.map { $0 == nil } == true
        }
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertEqual(diagnostics.inboxListenerCount, 0)
        XCTAssertEqual(diagnostics.liveListenerCount, 0)
        let shutdownCount = await fixture.supabase.shutdownCount
        let activeRoomRequests = await fixture.supabase.activeRoomRequests
        XCTAssertEqual(shutdownCount, 0)
        XCTAssertEqual(activeRoomRequests.last!, nil)
    }

    func testTerminalFirebaseListenerFailureFailsClosedButPreservesSharedSupabasePlane() async throws {
        let fixture = try Fixture()
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        var events = fixture.transport.events.makeAsyncIterator()

        fixture.values.finish(at: FirebaseV2Path.liveRoom(fixture.room.id))

        guard case .connection(let status) = await events.next() else {
            return XCTFail("Terminal Firebase listener failure must publish disconnected state")
        }
        XCTAssertFalse(status.isReady)
        XCTAssertFalse(status.activeRoomTransportConnected)

        try await eventually {
            let diagnostics = await fixture.transport.diagnostics()
            let invalidateCount = await fixture.credentials.invalidateCount
            let activeRoomRequests = await fixture.supabase.activeRoomRequests
            return diagnostics.shutDown
                && invalidateCount == 1
                && activeRoomRequests.last.map { $0 == nil } == true
        }
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertFalse(diagnostics.grantOpen)
        XCTAssertEqual(diagnostics.inboxListenerCount, 0)
        XCTAssertEqual(diagnostics.liveListenerCount, 0)
        let invalidateCount = await fixture.credentials.invalidateCount
        let shutdownCount = await fixture.supabase.shutdownCount
        let activeRoomRequests = await fixture.supabase.activeRoomRequests
        XCTAssertEqual(invalidateCount, 1)
        XCTAssertEqual(
            shutdownCount,
            0,
            "The router must retain the shared Supabase backend for a remote legacy switch"
        )
        XCTAssertEqual(activeRoomRequests.last!, nil)
        await assertThrowsAsync(FirebaseV2CompositeTransportError.shutDown) {
            try await fixture.transport.publishCharacterPulse(
                roomID: fixture.room.id,
                eventID: UUID()
            )
        }
    }

    func testRolloutLeaseRefreshReauthorizesWithoutReplacingLiveListener() async throws {
        let fixture = try Fixture(leaseRefreshExpiresAt: 1_800_000_600_000)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        let previousPresenceUpdates = await fixture.supabase.activeRoomRequests.count

        try await fixture.transport.refreshRolloutLease()

        let requests = await fixture.credentials.establishRequests
        XCTAssertEqual(requests.last, CompositeCredentialRequest(
            trigger: .rolloutLeaseRefresh,
            minimumAccessRevision: try XCTUnwrap(
                RealtimeRevision(rawValue: "00000000000000000001")
            )
        ))
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.inbox(userID: fixture.ownID)
        }.count, 2)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
        let presenceUpdates = await fixture.supabase.activeRoomRequests.count
        XCTAssertEqual(presenceUpdates, previousPresenceUpdates)
        let leaseStatus = await fixture.transport.rolloutLeaseStatus()
        let lease = try XCTUnwrap(leaseStatus)
        XCTAssertGreaterThan(lease.expiresIn, .seconds(599))
        XCTAssertLessThanOrEqual(lease.expiresIn, .seconds(600))
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.grantOpen)
    }

    func testThirtyMinuteLeaseRefreshReauthorizesWithoutReplacingLiveListener() async throws {
        let fixture = try Fixture(leaseRefreshExpiresAt: 1_800_001_800_000)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        let previousPresenceUpdates = await fixture.supabase.activeRoomRequests.count

        try await fixture.transport.refreshRolloutLease()

        let requests = await fixture.credentials.establishRequests
        XCTAssertEqual(requests.last, CompositeCredentialRequest(
            trigger: .rolloutLeaseRefresh,
            minimumAccessRevision: try XCTUnwrap(
                RealtimeRevision(rawValue: "00000000000000000001")
            )
        ))
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.inbox(userID: fixture.ownID)
        }.count, 2)
        XCTAssertEqual(fixture.values.paths.filter {
            $0 == FirebaseV2Path.liveRoom(fixture.room.id)
        }.count, 1)
        let presenceUpdates = await fixture.supabase.activeRoomRequests.count
        XCTAssertEqual(presenceUpdates, previousPresenceUpdates)
        let leaseStatus = await fixture.transport.rolloutLeaseStatus()
        let lease = try XCTUnwrap(leaseStatus)
        XCTAssertGreaterThan(lease.expiresIn, .seconds(1_799))
        XCTAssertLessThanOrEqual(lease.expiresIn, .seconds(1_800))
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.grantOpen)
    }

    func testTransientRolloutLeaseRefreshFailureKeepsCurrentGrantUntilHardDeadline() async throws {
        let fixture = try Fixture(leaseRefreshExpiresAt: 1_800_000_600_000)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )
        await fixture.credentials.failNextLeaseRefresh()

        do {
            try await fixture.transport.refreshRolloutLease()
            XCTFail("Injected renewal failure must escape for a bounded retry")
        } catch CompositeCredentialError.recoveryFailed {
            // Existing Firebase credential/listeners remain authoritative.
        }
        try await fixture.transport.publishCharacterPulse(
            roomID: fixture.room.id,
            eventID: UUID()
        )
        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertTrue(diagnostics.grantOpen)
    }

    func testChatAuthenticationRejectRefreshesSelectorAndCredentialWithoutResending() async throws {
        let selector = CompositeRolloutRefreshRecorder()
        let fixture = try Fixture(refreshPolicy: {
            await selector.refresh()
        })
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room], activeRoomID: fixture.room.id
        )
        await fixture.chatCaller.failNextUnauthenticated()

        let outcome = await fixture.transport.sendChat(
            roomID: fixture.room.id, body: "hello", id: UUID()
        )
        guard case .definitelyRejected = outcome else {
            return XCTFail("Authentication reject must not be retried as a message")
        }
        try await eventually {
            await fixture.credentials.establishCount == 2
                && fixture.values.paths.filter {
                    $0 == FirebaseV2Path.inbox(userID: fixture.ownID)
                }.count == 2
        }
        let selectorCalls = await selector.refreshCount
        let chatCalls = await fixture.chatCaller.callCount
        XCTAssertEqual(selectorCalls, 1)
        XCTAssertEqual(chatCalls, 1)

        let retry = await fixture.transport.sendChat(
            roomID: fixture.room.id, body: "next", id: UUID()
        )
        guard case .confirmed = retry else {
            return XCTFail("Fresh credential must restore chat")
        }
    }

    func testAtomicRepairDoesNotReopenFirebaseWhileAuthenticationRecoveryIsPending() async throws {
        let selector = CompositeRolloutRefreshRecorder()
        await selector.blockNextRefresh()
        let fixture = try Fixture(refreshPolicy: { await selector.refresh() })
        defer { Task { await fixture.transport.shutdown() } }
        _ = try await fixture.transport.synchronize(rooms: [fixture.room], activeRoomID: fixture.room.id)
        await fixture.chatCaller.failNextUnauthenticated()
        _ = await fixture.transport.sendChat(roomID: fixture.room.id, body: "reject", id: UUID())
        try await eventually { await selector.refreshCount == 1 }
        let repair = try await fixture.transport.repairDroppedEvents(
            rooms: [fixture.room], activeRoomID: fixture.room.id)
        XCTAssertEqual(repair.connection?.isReady, false)
        XCTAssertEqual(repair.connection?.activeRoomTransportConnected, false)
        await selector.resumeRefresh()
    }

    func testSupabaseReconnectDoesNotReopenChatDuringAuthenticationRecovery() async throws {
        let selector = CompositeRolloutRefreshRecorder()
        await selector.blockNextRefresh()
        let fixture = try Fixture(refreshPolicy: {
            await selector.refresh()
        })
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room], activeRoomID: fixture.room.id
        )
        var events = fixture.transport.events.makeAsyncIterator()
        await fixture.chatCaller.failNextUnauthenticated()

        _ = await fixture.transport.sendChat(
            roomID: fixture.room.id, body: "hello", id: UUID()
        )
        guard case .connection(let disconnected) = await events.next() else {
            return XCTFail("Authentication failure must close chat connection")
        }
        XCTAssertFalse(disconnected.transportConnected)
        try await eventually { await selector.refreshCount == 1 }

        await fixture.supabase.emit(.connection(BackendConnectionStatus(
            transportConnected: true,
            recoveryReconciled: true,
            activeRoomTransportConnected: true
        )))
        await fixture.supabase.emit(.presence(
            roomID: fixture.room.id,
            userID: fixture.friendID,
            state: .online
        ))
        guard case .presence = await events.next() else {
            return XCTFail("Supabase must not reopen chat while auth recovery is pending")
        }

        let blockedSend = await fixture.transport.sendChat(
            roomID: fixture.room.id, body: "blocked", id: UUID()
        )
        guard case .definitelyRejected = blockedSend else {
            return XCTFail("Chat sends must remain closed during auth recovery")
        }
        await selector.resumeRefresh()
        try await eventually { await fixture.credentials.establishCount == 2 }
    }

    func testNonAdvancingRolloutLeaseFailsClosed() async throws {
        let fixture = try Fixture(leaseRefreshExpiresAt: 1_800_000_300_000)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )

        await assertThrowsAsync(
            FirebaseV2CompositeTransportError.rolloutLeaseDidNotAdvance
        ) {
            try await fixture.transport.refreshRolloutLease()
        }

        let diagnostics = await fixture.transport.diagnostics()
        let shutdownCount = await fixture.supabase.shutdownCount
        let activeRoomRequests = await fixture.supabase.activeRoomRequests
        XCTAssertTrue(diagnostics.shutDown)
        XCTAssertEqual(shutdownCount, 0)
        XCTAssertEqual(activeRoomRequests.last!, nil)
    }

    func testRolloutLeaseHardDeadlineFailsClosedWithoutShuttingSharedSupabase() async throws {
        let fixture = try Fixture(initialLeaseExpiresAt: 1_800_000_000_030)
        _ = try await fixture.transport.synchronize(
            rooms: [fixture.room],
            activeRoomID: fixture.room.id
        )

        try await eventually {
            await fixture.transport.diagnostics().shutDown
        }

        let diagnostics = await fixture.transport.diagnostics()
        XCTAssertFalse(diagnostics.grantOpen)
        let shutdownCount = await fixture.supabase.shutdownCount
        let activeRoomRequests = await fixture.supabase.activeRoomRequests
        XCTAssertEqual(shutdownCount, 0)
        XCTAssertEqual(activeRoomRequests.last!, nil)
        await assertThrowsAsync(FirebaseV2CompositeTransportError.shutDown) {
            try await fixture.transport.publishTyping(
                roomID: fixture.room.id,
                event: "typing_start"
            )
        }
    }

    private func assertThrowsAsync<E: Error & Equatable>(
        _ expected: E,
        operation: () async throws -> Void
    ) async {
        do {
            try await operation()
            XCTFail("Expected operation to throw")
        } catch let error as E {
            XCTAssertEqual(error, expected)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    private func eventually(
        timeout: Duration = .seconds(2),
        condition: @escaping () async -> Bool
    ) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while clock.now < deadline {
            if await condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("Condition did not become true before timeout")
    }
}

private struct Fixture {
    let ownID = UUID()
    let sessionID = UUID()
    let friendID = UUID()
    let room: Room
    let throwWireCode = FirebaseV2WireCode(rawValue: "18")!
    let credentials: CompositeCredentialStub
    let supabase: CompositeSupabasePlaneStub
    let values: CompositeValueStreamStub
    let chatCaller: CompositeChatCallerStub
    let transport: FirebaseV2CompositeTransport

    init(
        additionalAuthorizedRoomID: UUID? = nil,
        recoveryRevokesAdditionalRoom: Bool = false,
        automaticallyYieldLiveBaseline: Bool = true,
        liveReadinessTimeout: Duration = .seconds(1),
        initialLeaseExpiresAt: Int64 = 1_800_000_300_000,
        leaseRefreshExpiresAt: Int64? = nil,
        serverTimeOffsets: (any FirebaseV2ServerTimeOffsetStreaming)? = nil,
        refreshPolicy: (@Sendable () async throws -> RealtimeRolloutPolicyDecision)? = nil,
        eventBufferLimit: Int = 256,
        deliverySleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) throws {
        values = CompositeValueStreamStub(
            automaticallyYieldBaseline: automaticallyYieldLiveBaseline
        )
        room = Room(
            id: UUID(),
            name: "친구방",
            ownerID: ownID,
            members: [
                RoomMember(
                    userID: ownID,
                    nickname: "나",
                    characterID: "pixel_hamster",
                    presence: .online
                ),
                RoomMember(
                    userID: friendID,
                    nickname: "친구",
                    characterID: "pixel_cat",
                    presence: .online
                ),
            ],
            inviteCodeHint: "••••"
        )
        let authorizedRooms = [room.id] + [additionalAuthorizedRoomID].compactMap { $0 }
        let initialSession = FirebaseV2CompositeSession(
            identity: RealtimeCredentialIdentity(accountID: ownID, sessionID: sessionID),
            bootstrap: try Self.makeBootstrap(
                rooms: authorizedRooms,
                wireCode: throwWireCode,
                rolloutLeaseExpiresAt: initialLeaseExpiresAt
            )
        )
        let recoveredRevision = try XCTUnwrap(
            RealtimeRevision(rawValue: "00000000000000000002")
        )
        let recoveredSession = FirebaseV2CompositeSession(
            identity: initialSession.identity,
            bootstrap: try Self.makeBootstrap(
                rooms: recoveryRevokesAdditionalRoom ? [room.id] : authorizedRooms,
                wireCode: throwWireCode,
                accessRevision: recoveredRevision.rawValue,
                rolloutLeaseExpiresAt: initialLeaseExpiresAt
            )
        )
        let leaseRefreshSession: FirebaseV2CompositeSession?
        if let leaseRefreshExpiresAt {
            leaseRefreshSession = FirebaseV2CompositeSession(
                identity: initialSession.identity,
                bootstrap: try Self.makeBootstrap(
                    rooms: authorizedRooms,
                    wireCode: throwWireCode,
                    rolloutLeaseExpiresAt: leaseRefreshExpiresAt
                )
            )
        } else {
            leaseRefreshSession = nil
        }
        credentials = CompositeCredentialStub(
            session: initialSession,
            recoverySessions: [recoveredRevision: recoveredSession],
            leaseRefreshSession: leaseRefreshSession
        )
        let reconciliation = BackendReconciliation(
            snapshot: BackendSnapshot(
                profile: Profile(
                    id: ownID,
                    nickname: "나",
                    characterID: "pixel_hamster",
                    equippedThrowableID: "throwable_leaf"
                ),
                rooms: [room]
            ),
            activeRoomID: room.id,
            activeMessages: []
        )
        supabase = CompositeSupabasePlaneStub(reconciliation: reconciliation)
        chatCaller = CompositeChatCallerStub(senderID: ownID)
        let chatClient = FirebaseV2ChatClient(
            caller: chatCaller,
            senderUserID: ownID,
            bubbleCatalogIDByWireCode: [:]
        )
        transport = FirebaseV2CompositeTransport(
            credentials: credentials,
            supabasePlane: supabase,
            databaseValues: values,
            databaseWrites: values,
            chatClient: chatClient,
            refreshPolicy: refreshPolicy,
            throwableCatalogIDByWireCode: [
                FirebaseV2WireCode(rawValue: "0")!: "patch_soft_ball",
                throwWireCode: "throwable_leaf",
            ],
            serverTimeOffsets: serverTimeOffsets,
            liveReadinessTimeout: liveReadinessTimeout,
            eventBufferLimit: eventBufferLimit,
            deliverySleep: deliverySleep,
            nowMilliseconds: { 1_800_000_000_000 }
        )
    }

    func makeRoom(id: UUID) -> Room {
        Room(
            id: id,
            name: "두 번째 방",
            ownerID: ownID,
            members: room.members,
            inviteCodeHint: "••••"
        )
    }

    private static func makeBootstrap(
        rooms: [UUID],
        wireCode: FirebaseV2WireCode,
        accessRevision: String = "00000000000000000001",
        rolloutLeaseExpiresAt: Int64
    ) throws -> FirebaseV2BootstrapSuccess {
        let roomJSON = rooms
            .map { "\"\($0.uuidString.lowercased())\"" }
            .joined(separator: ",")
        let json = """
        {
          "protocolVersion": 2,
          "databaseURL": "https://sidey.asia-southeast1.firebasedatabase.app",
          "firebaseApiKey": "test-api-key",
          "customToken": "test-custom-token",
          "permissionSync": "event-driven",
          "authTokenLifetimeSeconds": 3600,
          "refreshAfter": \(rolloutLeaseExpiresAt - 30_000),
          "rolloutLeaseExpiresAt": \(rolloutLeaseExpiresAt),
          "accessRevision": "\(accessRevision)",
          "rooms": [\(roomJSON)],
          "wireItems": ["\(wireCode.rawValue)"]
        }
        """
        return try JSONDecoder().decode(
            FirebaseV2BootstrapSuccess.self,
            from: Data(json.utf8)
        )
    }
}

private struct CompositeCredentialRequest: Equatable, Sendable {
    let trigger: RealtimeBootstrapTrigger
    let minimumAccessRevision: RealtimeRevision?
}

private enum CompositeCredentialError: Error {
    case recoveryFailed
}

private actor CompositeCredentialStub: FirebaseV2CompositeCredentialEstablishing {
    nonisolated let invalidations: AsyncStream<Void>
    private let invalidationContinuation: AsyncStream<Void>.Continuation
    let session: FirebaseV2CompositeSession
    let recoverySessions: [RealtimeRevision: FirebaseV2CompositeSession]
    let leaseRefreshSession: FirebaseV2CompositeSession?
    private(set) var establishCount = 0
    private(set) var establishRequests: [CompositeCredentialRequest] = []
    private(set) var invalidateCount = 0
    private var remainingRecoveryFailures = 0
    private var remainingLeaseRefreshFailures = 0

    init(
        session: FirebaseV2CompositeSession,
        recoverySessions: [RealtimeRevision: FirebaseV2CompositeSession],
        leaseRefreshSession: FirebaseV2CompositeSession?
    ) {
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        invalidations = pair.stream
        invalidationContinuation = pair.continuation
        self.session = session
        self.recoverySessions = recoverySessions
        self.leaseRefreshSession = leaseRefreshSession
    }

    func establish(
        trigger: RealtimeBootstrapTrigger,
        minimumAccessRevision: RealtimeRevision?
    ) async throws -> FirebaseV2CompositeSession {
        establishCount += 1
        establishRequests.append(CompositeCredentialRequest(
            trigger: trigger,
            minimumAccessRevision: minimumAccessRevision
        ))
        if trigger == .rolloutLeaseRefresh {
            if remainingLeaseRefreshFailures > 0 {
                remainingLeaseRefreshFailures -= 1
                throw CompositeCredentialError.recoveryFailed
            }
            return leaseRefreshSession ?? session
        }
        if minimumAccessRevision != nil, remainingRecoveryFailures > 0 {
            remainingRecoveryFailures -= 1
            throw CompositeCredentialError.recoveryFailed
        }
        if let minimumAccessRevision,
           let recovered = recoverySessions[minimumAccessRevision] {
            return recovered
        }
        return session
    }

    func invalidate() async {
        invalidateCount += 1
        invalidationContinuation.finish()
    }

    func emitInvalidation() {
        invalidationContinuation.yield(())
    }

    func failNextRecovery() {
        remainingRecoveryFailures += 1
    }

    func failNextLeaseRefresh() {
        remainingLeaseRefreshFailures += 1
    }
}

private actor CompositeSupabasePlaneStub: FirebaseV2SupabasePlane {
    nonisolated let events: AsyncStream<BackendEvent>
    private let continuation: AsyncStream<BackendEvent>.Continuation
    private var reconciliation: BackendReconciliation
    private var selectedRoomID: UUID?
    private var blockedActiveRooms: Set<UUID> = []
    private var activeRoomContinuations: [UUID: CheckedContinuation<Void, Never>] = [:]
    private(set) var synchronizeCount = 0
    private(set) var messageRequests: [UUID] = []
    private(set) var recentRequests: [UUID] = []
    private(set) var repairCount = 0
    private var messages: [UUID: ChatMessage] = [:]
    private var overlaySuppression: [UUID: [UUID: Bool]] = [:]
    private var messageFailures = 0
    private var messageCancellations = 0
    private var beforeMessage: (@Sendable () async -> Void)?
    private var beforeRepair: (@Sendable () async -> Void)?
    private(set) var shutdownCount = 0
    private(set) var activeRoomRequests: [UUID?] = []
    private var activeRoomUpdatesRejected = false
    private(set) var transientPublications: [CompositeTransientPublication] = []

    init(reconciliation: BackendReconciliation) {
        self.reconciliation = reconciliation
        self.selectedRoomID = reconciliation.activeRoomID
        let pair = AsyncStream<BackendEvent>.makeStream(bufferingPolicy: .bufferingNewest(32))
        events = pair.stream
        continuation = pair.continuation
    }

    func synchronize(
        rooms: [Room],
        activeRoomID: UUID?
    ) async throws -> BackendReconciliation {
        selectedRoomID = activeRoomID
        return try await reconcilePreservingActiveRoom()
    }

    func reconcilePreservingActiveRoom() async throws -> BackendReconciliation {
        synchronizeCount += 1
        return BackendReconciliation(
            snapshot: reconciliation.snapshot,
            activeRoomID: selectedRoomID,
            activeMessages: reconciliation.activeMessages
        )
    }

    func message(id: UUID, roomID: UUID) async throws -> ChatMessage? {
        messageRequests.append(id)
        if let beforeMessage { self.beforeMessage = nil; await beforeMessage() }
        if messageCancellations > 0 { messageCancellations -= 1; throw CancellationError() }
        if messageFailures > 0 { messageFailures -= 1; throw URLError(.timedOut) }
        return messages[id]
    }

    func recentMessages(roomID: UUID) async throws -> [ChatMessage] {
        recentRequests.append(roomID)
        return messages.values.filter { $0.roomID == roomID }.sorted { $0.createdAt < $1.createdAt }
    }

    func repairDroppedEventsPreservingActiveRoom() async throws -> BackendEventGapRepair {
        repairCount += 1
        if let beforeRepair { self.beforeRepair = nil; await beforeRepair() }
        let result = try await reconcilePreservingActiveRoom()
        let visibility: [BackendEvent] = result.snapshot.rooms.flatMap { room in
            room.members.compactMap { member -> BackendEvent? in
                guard let suppressed = overlaySuppression[room.id]?[member.userID] else { return nil }
                return .overlaySuppression(roomID: room.id, userID: member.userID, suppressed: suppressed)
            }
        }
        return BackendEventGapRepair(reconciliation: result,
            messagesByRoom: Dictionary(grouping: messages.values, by: \.roomID),
            presence: result.snapshot.rooms.flatMap { room in room.members.map {
                .presence(roomID: room.id, userID: $0.userID, state: $0.presence)
            } } + visibility, connection: BackendConnectionStatus(transportConnected: true, recoveryReconciled: true))
    }

    func store(_ message: ChatMessage) { messages[message.id] = message }
    func storeOverlaySuppression(roomID: UUID, userID: UUID, suppressed: Bool) {
        overlaySuppression[roomID, default: [:]][userID] = suppressed
    }
    func failNextMessage() { messageFailures += 1 }
    func cancelNextMessage() { messageCancellations += 1 }
    func beforeNextMessage(_ operation: @escaping @Sendable () async -> Void) { beforeMessage = operation }
    func emitSnapshot() { continuation.yield(.snapshot(reconciliation.snapshot)) }
    func beforeNextRepair(_ operation: @escaping @Sendable () async -> Void) { beforeRepair = operation }

    func setActiveRoom(_ roomID: UUID?) async throws {
        activeRoomRequests.append(roomID)
        if activeRoomUpdatesRejected { throw URLError(.timedOut) }
        selectedRoomID = roomID
        guard let roomID, blockedActiveRooms.contains(roomID) else { return }
        await withCheckedContinuation { continuation in
            activeRoomContinuations[roomID] = continuation
        }
    }
    func rejectActiveRoomUpdates() { activeRoomUpdatesRejected = true }
    func setLocalPresence(_ state: PresenceState) async throws {}

    func publishTyping(roomID: UUID, event: String) async throws {
        transientPublications.append(.typing(roomID: roomID, event: event))
    }

    func publishCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        transientPublications.append(.pulse(roomID: roomID, eventID: eventID))
    }

    func publishCharacterThrow(
        roomID: UUID,
        eventID: UUID,
        targetUserID: UUID
    ) async throws {
        transientPublications.append(.characterThrow(
            roomID: roomID,
            eventID: eventID,
            targetUserID: targetUserID
        ))
    }

    func shutdown() async {
        shutdownCount += 1
        continuation.finish()
    }

    func emit(_ event: BackendEvent) {
        if case .overlaySuppression(let roomID, let userID, let suppressed) = event {
            storeOverlaySuppression(roomID: roomID, userID: userID, suppressed: suppressed)
        }
        continuation.yield(event)
    }

    func replaceRooms(_ rooms: [Room]) {
        reconciliation = BackendReconciliation(
            snapshot: BackendSnapshot(
                profile: reconciliation.snapshot.profile,
                rooms: rooms,
                activeEntitlementKeys: reconciliation.snapshot.activeEntitlementKeys
            ),
            activeRoomID: reconciliation.activeRoomID,
            activeMessages: reconciliation.activeMessages
        )
    }

    func blockSetActiveRoom(_ roomID: UUID) {
        blockedActiveRooms.insert(roomID)
    }

    func isSetActiveRoomBlocked(_ roomID: UUID) -> Bool {
        activeRoomContinuations[roomID] != nil
    }

    func resumeSetActiveRoom(_ roomID: UUID) {
        blockedActiveRooms.remove(roomID)
        activeRoomContinuations.removeValue(forKey: roomID)?.resume()
    }
}

private enum CompositeTransientPublication: Equatable, Sendable {
    case typing(roomID: UUID, event: String)
    case pulse(roomID: UUID, eventID: UUID)
    case characterThrow(roomID: UUID, eventID: UUID, targetUserID: UUID)
}

private final class CompositeValueStreamStub: @unchecked Sendable,
    FirebaseV2DatabaseValueStreaming, FirebaseV2DatabaseWriteTransport {
    private struct Entry {
        let id: UUID
        let continuation: AsyncThrowingStream<Data, Error>.Continuation
    }

    private let lock = NSLock()
    private let automaticallyYieldBaseline: Bool
    private var storedPaths: [String] = []
    private var storedWriteOperations: [FirebaseV2DatabaseWriteOperation] = []
    private var continuations: [String: Entry] = [:]

    init(automaticallyYieldBaseline: Bool) {
        self.automaticallyYieldBaseline = automaticallyYieldBaseline
    }

    var paths: [String] {
        lock.withLock { storedPaths }
    }

    var writeOperations: [FirebaseV2DatabaseWriteOperation] {
        lock.withLock { storedWriteOperations }
    }

    func values(at path: String) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { continuation in
            let id = UUID()
            lock.withLock {
                storedPaths.append(path)
                continuations[path] = Entry(id: id, continuation: continuation)
            }
            if automaticallyYieldBaseline {
                continuation.yield(Data("{}".utf8))
            }
            continuation.onTermination = { [weak self] _ in
                _ = self?.lock.withLock {
                    guard self?.continuations[path]?.id == id else { return }
                    self?.continuations.removeValue(forKey: path)
                }
            }
        }
    }

    func yield(_ data: Data, at path: String) {
        let continuation = lock.withLock { continuations[path]?.continuation }
        continuation?.yield(data)
    }

    func finish(at path: String) {
        let continuation = lock.withLock { continuations[path]?.continuation }
        continuation?.finish()
    }

    func perform(_ operation: FirebaseV2DatabaseWriteOperation) async throws {
        lock.withLock {
            storedWriteOperations.append(operation)
        }
    }
}

private actor CompositeChatCallerStub: FirebaseV2ChatCalling {
    private let senderID: UUID
    private(set) var callCount = 0
    private var remainingUnauthenticatedFailures = 0

    init(senderID: UUID) {
        self.senderID = senderID
    }

    func failNextUnauthenticated() {
        remainingUnauthenticatedFailures += 1
    }

    func call(_ request: FirebaseV2ChatRequest) async throws -> FirebaseV2ChatResponse {
        callCount += 1
        if remainingUnauthenticatedFailures > 0 {
            remainingUnauthenticatedFailures -= 1
            throw NSError(
                domain: FunctionsErrorDomain,
                code: FunctionsErrorCode.unauthenticated.rawValue
            )
        }
        return FirebaseV2ChatResponse(
            body: request.body,
            messageID: request.messageID,
            bubbleWireCode: nil,
            sequence: Int64(callCount),
            timestamp: FirebaseV2Timestamp(rawValue: 1_800_000_000_000 + Int64(callCount))!
        )
    }
}

private actor CompositeRolloutRefreshRecorder {
    private(set) var refreshCount = 0
    private var shouldBlockNextRefresh = false
    private var blockedRefresh: CheckedContinuation<Void, Never>?

    func blockNextRefresh() {
        shouldBlockNextRefresh = true
    }

    func refresh() async -> RealtimeRolloutPolicyDecision {
        refreshCount += 1
        if shouldBlockNextRefresh {
            shouldBlockNextRefresh = false
            await withCheckedContinuation { continuation in
                blockedRefresh = continuation
            }
        }
        return RealtimeRolloutPolicyDecision(state: .firebaseV2, cacheTTLSeconds: 300)
    }

    func resumeRefresh() {
        blockedRefresh?.resume()
        blockedRefresh = nil
    }
}

private final class CompositeServerOffsetStub: @unchecked Sendable,
    FirebaseV2ServerTimeOffsetStreaming {
    private let lock = NSLock()
    private var continuation: AsyncStream<Int64>.Continuation?
    private var cancelled = false
    private var subscriptions = 0

    var isCancelled: Bool { lock.withLock { cancelled } }
    var subscriptionCount: Int { lock.withLock { subscriptions } }

    func offsets() -> AsyncStream<Int64> {
        AsyncStream { continuation in
            lock.withLock {
                self.continuation = continuation
                subscriptions += 1
            }
            continuation.onTermination = { [weak self] _ in
                self?.lock.withLock { self?.cancelled = true }
            }
        }
    }

    func yield(_ offset: Int64) {
        let continuation = lock.withLock { self.continuation }
        continuation?.yield(offset)
    }
}

private actor CompositeRepairBarrier {
    private(set) var isSuspended = false
    private var continuation: CheckedContinuation<Void, Never>?
    func suspend() async {
        isSuspended = true
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { continuation?.resume(); continuation = nil }
}
