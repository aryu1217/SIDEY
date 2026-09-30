import Foundation
import XCTest
@testable import SIDEY

final class BackendIntegrationTests: XCTestCase {
    func testTwoNativeClientsMessageTypingPresenceAndCleanup() async throws {
        // This scenario intentionally includes 10 s and 30 s profile-change gaps,
        // then forces a disconnect/recovery cycle. Keep XCTest's watchdog from
        // cancelling the test before the network assertions can finish.
        executionTimeAllowance = 180
        var environment = ProcessInfo.processInfo.environment
        let testBundle = Bundle(for: Self.self)
        let integrationFlag = configuredValue(
            environment: environment,
            environmentKey: "SIDEY_RUN_BACKEND_INTEGRATION",
            bundle: testBundle,
            bundleKey: "SIDEYRunBackendIntegration"
        )
        guard integrationFlag == "1" else {
            throw XCTSkip("명시적인 로컬·staging 환경에서만 통합 테스트 실행")
        }
        environment["SIDEY_SUPABASE_URL"] = configuredValue(
            environment: environment,
            environmentKey: "SIDEY_SUPABASE_URL",
            bundle: testBundle,
            bundleKey: "SIDEYSupabaseURL"
        )
        environment["SIDEY_SUPABASE_PUBLISHABLE_KEY"] = configuredValue(
            environment: environment,
            environmentKey: "SIDEY_SUPABASE_PUBLISHABLE_KEY",
            bundle: testBundle,
            bundleKey: "SIDEYSupabasePublishableKey"
        )
        guard environment["SIDEY_SUPABASE_URL"]?.isEmpty == false,
              environment["SIDEY_SUPABASE_PUBLISHABLE_KEY"]?.isEmpty == false
        else {
            XCTFail("통합 테스트에는 SIDEY_SUPABASE_URL과 SIDEY_SUPABASE_PUBLISHABLE_KEY가 필요합니다.")
            return
        }
        let configuration = try RuntimeConfiguration.resolve(
            releaseChannel: .staging,
            environment: environment
        )
        guard !configuration.isProductionBackend else {
            XCTFail("통합 테스트는 SIDEY production backend에서 실행할 수 없습니다.")
            return
        }
        let firstStore = KeychainStore(service: "app.sidey.desktop.integration.\(UUID().uuidString)")
        let secondStore = KeychainStore(service: "app.sidey.desktop.integration.\(UUID().uuidString)")
        let first = SideyBackend(configuration: configuration, keychain: firstStore)
        let second = SideyBackend(configuration: configuration, keychain: secondStore)
        let firstProbe = BackendEventProbe()
        let secondProbe = BackendEventProbe()
        let firstEvents = Task { await firstProbe.consume(first.events) }
        let secondEvents = Task { await secondProbe.consume(second.events) }
        var createdRoomIDs: Set<UUID> = []
        var phase = "boot"

        do {
            _ = try await first.boot()
            _ = try await second.boot()
            _ = try await first.upsertProfile(nickname: "통합첫째")
            _ = try await second.upsertProfile(nickname: "통합둘째")
            let created = try await first.createRoom(name: "네이티브 통합 \(Int.random(in: 1000...9999))")
            createdRoomIDs.insert(created.roomID)
            let joinedRoom = try await second.joinRoom(inviteCode: created.inviteCode)
            XCTAssertEqual(joinedRoom.roomID, created.roomID)
            XCTAssertTrue(joinedRoom.storedInKeychain)
            let creatorStoredCode = try await first.storedInviteCode(roomID: created.roomID)
            let joinerStoredCode = try await second.storedInviteCode(roomID: created.roomID)
            XCTAssertEqual(creatorStoredCode, created.inviteCode)
            XCTAssertEqual(joinerStoredCode, created.inviteCode)

            let firstSnapshot = try await first.loadSnapshot()
            let secondSnapshot = try await second.loadSnapshot()
            _ = try await first.syncRealtime(rooms: firstSnapshot.rooms, activeRoomID: created.roomID)
            _ = try await second.syncRealtime(rooms: secondSnapshot.rooms, activeRoomID: created.roomID)
            try await waitUntil("두 클라이언트 Realtime 구독") {
                let firstConnected = await firstProbe.isConnected
                let secondConnected = await secondProbe.isConnected
                return firstConnected && secondConnected
            }

            let optionalFirstUserID = await first.currentUserID()
            let firstUserID = try XCTUnwrap(optionalFirstUserID)
            let optionalSecondUserID = await second.currentUserID()
            let secondUserID = try XCTUnwrap(optionalSecondUserID)
            let firstDisconnectsBeforeProfiles = await firstProbe.disconnectedAfterConnectCount
            let secondDisconnectsBeforeProfiles = await secondProbe.disconnectedAfterConnectCount
            let profileChanges: [(SideyBackend, BackendEventProbe, UUID, String, String, Duration)] = [
                (first, secondProbe, firstUserID, "첫째즉시", "pixel_cat", .zero),
                (second, firstProbe, secondUserID, "둘째십초", "pixel_penguin", .seconds(10)),
                (first, secondProbe, firstUserID, "첫째삼십초", "pixel_puppy", .seconds(30))
            ]
            for (backend, peerProbe, userID, nickname, characterID, delay) in profileChanges {
                phase = "profile delay: \(nickname)"
                if delay > .zero { try await Task.sleep(for: delay) }
                phase = "profile mutation: \(nickname)"
                let snapshotsBeforeProfile = await peerProbe.snapshotCount
                _ = try await backend.upsertProfile(
                    nickname: nickname,
                    characterID: characterID
                )
                phase = "profile broadcast: \(nickname)"
                try await waitUntil("상대 클라이언트 프로필 변경 수신") {
                    await peerProbe.hasMember(
                        after: snapshotsBeforeProfile,
                        userID: userID,
                        nickname: nickname,
                        characterID: characterID
                    )
                }
            }
            let firstDisconnectsAfterProfiles = await firstProbe.disconnectedAfterConnectCount
            let secondDisconnectsAfterProfiles = await secondProbe.disconnectedAfterConnectCount
            XCTAssertEqual(firstDisconnectsAfterProfiles, firstDisconnectsBeforeProfiles)
            XCTAssertEqual(secondDisconnectsAfterProfiles, secondDisconnectsBeforeProfiles)

            phase = "room rename"
            let snapshotsBeforeRename = await secondProbe.snapshotCount
            let renamedRoom = "이름 변경 \(Int.random(in: 1000...9999))"
            try await first.renameRoom(created.roomID, name: renamedRoom)
            try await waitUntil("두 번째 클라이언트 그룹 이름 변경 수신") {
                await secondProbe.hasSnapshot(
                    after: snapshotsBeforeRename,
                    roomID: created.roomID,
                    expectedName: renamedRoom
                )
            }

            let body = "Swift 네이티브 통합 \(UUID().uuidString.prefix(8))"
            let sent = try await first.sendMessage(roomID: created.roomID, body: body)
            try await waitUntil("두 번째 클라이언트 메시지 수신") {
                await secondProbe.messageIDs.contains(sent.id)
            }
            let receivedMessages = try await second.recentMessages(roomID: created.roomID)
            XCTAssertEqual(receivedMessages.last?.body, body)

            try await first.broadcastTyping(roomID: created.roomID, event: "typing_start")
            try await waitUntil("typing_start Broadcast 수신") { await secondProbe.typingStarted }
            try await first.broadcastTyping(roomID: created.roomID, event: "typing_stop")
            try await waitUntil("typing_stop Broadcast 수신") { await secondProbe.typingStopped }

            let pulseEventID = UUID()
            try await first.broadcastCharacterPulse(roomID: created.roomID, eventID: pulseEventID)
            try await waitUntil("character_pulse Broadcast 수신") {
                await secondProbe.characterPulseEventIDs.contains(pulseEventID)
            }

            try await first.setLocalPresence(.away)
            try await waitUntil("away Presence 수신") {
                await secondProbe.presence[firstUserID] == .away
            }

            let presenceUpdatesBeforeInterruption = await firstProbe.presenceUpdateCounts[secondUserID, default: 0]
            let disconnectionsBeforeInterruption = await secondProbe.disconnectedAfterConnectCount
            await second.simulateNetworkAvailabilityForTesting(.unavailable)
            try await waitUntil("강제 단절 감지", timeout: .seconds(15)) {
                await secondProbe.disconnectedAfterConnectCount > disconnectionsBeforeInterruption
            }
            try await Task.sleep(for: .milliseconds(500))
            await second.simulateNetworkAvailabilityForTesting(.available)
            try await waitUntil("Realtime 자동 재구독", timeout: .seconds(30)) {
                await secondProbe.isConnected
            }
            try await waitUntil("재구독 뒤 Presence 재발행") {
                await firstProbe.hasPresenceUpdate(
                    userID: secondUserID,
                    state: .online,
                    after: presenceUpdatesBeforeInterruption
                )
            }

            let recoveredBody = "재구독 메시지 \(UUID().uuidString.prefix(8))"
            let recoveredMessage = try await first.sendMessage(roomID: created.roomID, body: recoveredBody)
            try await waitUntil("재구독 뒤 메시지 수신") {
                await secondProbe.messageIDs.contains(recoveredMessage.id)
            }

            let recoveredPulseEventID = UUID()
            try await first.broadcastCharacterPulse(roomID: created.roomID, eventID: recoveredPulseEventID)
            try await waitUntil("재구독 뒤 character_pulse 수신") {
                await secondProbe.characterPulseEventIDs.contains(recoveredPulseEventID)
            }

            let deletionRoom = try await first.createRoom(name: "삭제 통합 \(Int.random(in: 1000...9999))")
            createdRoomIDs.insert(deletionRoom.roomID)
            _ = try await second.joinRoom(inviteCode: deletionRoom.inviteCode)
            let firstDeletionSnapshot = try await first.loadSnapshot()
            let secondDeletionSnapshot = try await second.loadSnapshot()
            _ = try await first.syncRealtime(
                rooms: firstDeletionSnapshot.rooms,
                activeRoomID: created.roomID
            )
            _ = try await second.syncRealtime(
                rooms: secondDeletionSnapshot.rooms,
                activeRoomID: created.roomID
            )

            let snapshotsBeforeDeletion = await secondProbe.snapshotCount
            try await first.deleteRoom(deletionRoom.roomID)
            createdRoomIDs.remove(deletionRoom.roomID)
            try await waitUntil("참가 클라이언트 그룹 삭제 수신") {
                await secondProbe.hasSnapshot(
                    after: snapshotsBeforeDeletion,
                    roomID: deletionRoom.roomID,
                    expectedName: nil
                )
            }
            let afterDeletionSnapshot = try await second.loadSnapshot()
            _ = try await second.syncRealtime(rooms: afterDeletionSnapshot.rooms, activeRoomID: created.roomID)
            let deletedRoomStoredCode = try await second.storedInviteCode(roomID: deletionRoom.roomID)
            XCTAssertNil(deletedRoomStoredCode)

            let leaveRoom = try await first.createRoom(name: "나가기 통합 \(Int.random(in: 1000...9999))")
            createdRoomIDs.insert(leaveRoom.roomID)
            _ = try await second.joinRoom(inviteCode: leaveRoom.inviteCode)
            let firstLeaveSnapshot = try await first.loadSnapshot()
            let secondLeaveSnapshot = try await second.loadSnapshot()
            _ = try await first.syncRealtime(
                rooms: firstLeaveSnapshot.rooms,
                activeRoomID: created.roomID
            )
            _ = try await second.syncRealtime(
                rooms: secondLeaveSnapshot.rooms,
                activeRoomID: created.roomID
            )

            let snapshotsBeforeLeave = await firstProbe.snapshotCount
            try await second.leaveRoom(leaveRoom.roomID)
            try await waitUntil("일반 멤버 그룹 나가기 수신") {
                await firstProbe.hasRoomMembers(
                    after: snapshotsBeforeLeave,
                    roomID: leaveRoom.roomID,
                    expected: [firstUserID]
                )
            }
            let afterLeaveSnapshot = try await second.loadSnapshot()
            XCTAssertFalse(afterLeaveSnapshot.rooms.contains(where: { $0.id == leaveRoom.roomID }))
            let leftRoomStoredCode = try await second.storedInviteCode(roomID: leaveRoom.roomID)
            XCTAssertNil(leftRoomStoredCode)
            try await first.deleteRoom(leaveRoom.roomID)
            createdRoomIDs.remove(leaveRoom.roomID)

            let snapshotsBeforeRemoval = await secondProbe.snapshotCount
            try await first.removeRoomMember(created.roomID, userID: secondUserID)
            try await waitUntil("추방된 클라이언트 그룹 제거 수신") {
                await secondProbe.hasSnapshot(
                    after: snapshotsBeforeRemoval,
                    roomID: created.roomID,
                    expectedName: nil
                )
            }
            _ = try await second.syncRealtime(rooms: [], activeRoomID: nil)
            let removedRoomStoredCode = try await second.storedInviteCode(roomID: created.roomID)
            XCTAssertNil(removedRoomStoredCode)

            try await first.deleteRoom(created.roomID)
            createdRoomIDs.remove(created.roomID)
        } catch {
            XCTFail("통합 테스트 실패 단계: \(phase) · \(error)")
            for roomID in createdRoomIDs {
                try? await second.leaveRoom(roomID)
                try? await first.deleteRoom(roomID)
            }
            try? await first.deleteOwnAccountForTesting()
            try? await second.deleteOwnAccountForTesting()
            firstEvents.cancel()
            secondEvents.cancel()
            await first.shutdown()
            await second.shutdown()
            try? firstStore.deleteAll()
            try? secondStore.deleteAll()
            throw error
        }

        try await first.deleteOwnAccountForTesting()
        try await second.deleteOwnAccountForTesting()
        firstEvents.cancel()
        secondEvents.cancel()
        await first.shutdown()
        await second.shutdown()
        try firstStore.deleteAll()
        try secondStore.deleteAll()
    }

    private func waitUntil(
        _ label: String,
        timeout: Duration = .seconds(15),
        condition: @escaping @Sendable () async -> Bool
    ) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while clock.now < deadline {
            if await condition() { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        XCTFail("\(label): 통합 이벤트가 \(timeout) 안에 도착하지 않음")
        throw IntegrationTimeout()
    }

    private func configuredValue(
        environment: [String: String],
        environmentKey: String,
        bundle: Bundle,
        bundleKey: String
    ) -> String? {
        if let value = environment[environmentKey], !value.isEmpty {
            return value
        }
        if let value = bundle.object(forInfoDictionaryKey: bundleKey) as? String,
           !value.isEmpty
        {
            return value
        }
        if let value = bundle.object(forInfoDictionaryKey: bundleKey) as? NSNumber {
            return value.stringValue
        }
        return nil
    }
}

private actor BackendEventProbe {
    private(set) var isConnected = false
    private(set) var messageIDs: Set<UUID> = []
    private(set) var typingStarted = false
    private(set) var typingStopped = false
    private(set) var characterPulseEventIDs: Set<UUID> = []
    private(set) var presence: [UUID: PresenceState] = [:]
    private(set) var presenceUpdateCounts: [UUID: Int] = [:]
    private(set) var disconnectedAfterConnectCount = 0
    private(set) var snapshotCount = 0
    private(set) var roomNames: [UUID: String] = [:]
    private(set) var roomMemberIDs: [UUID: Set<UUID>] = [:]
    private(set) var members: [UUID: Profile] = [:]
    private var hasConnected = false

    func hasPresenceUpdate(userID: UUID, state: PresenceState, after count: Int) -> Bool {
        presence[userID] == state && presenceUpdateCounts[userID, default: 0] > count
    }

    func hasSnapshot(after count: Int, roomID: UUID, expectedName: String?) -> Bool {
        snapshotCount > count && roomNames[roomID] == expectedName
    }

    func hasRoomMembers(after count: Int, roomID: UUID, expected: Set<UUID>) -> Bool {
        snapshotCount > count && roomMemberIDs[roomID] == expected
    }

    func hasMember(
        after count: Int,
        userID: UUID,
        nickname: String,
        characterID: String
    ) -> Bool {
        guard snapshotCount > count, let member = members[userID] else { return false }
        return member.nickname == nickname && member.characterID == characterID
    }

    private func apply(_ snapshot: BackendSnapshot) {
        snapshotCount += 1
        roomNames = Dictionary(uniqueKeysWithValues: snapshot.rooms.map { ($0.id, $0.name) })
        roomMemberIDs = Dictionary(uniqueKeysWithValues: snapshot.rooms.map { room in
            (room.id, Set(room.members.map(\.userID)))
        })
        for room in snapshot.rooms {
            for member in room.members {
                members[member.userID] = Profile(
                    id: member.userID,
                    nickname: member.nickname,
                    characterID: member.characterID
                )
            }
        }
    }

    func consume(_ events: AsyncStream<BackendEvent>) async {
        for await event in events {
            switch event {
            case .connection(let status):
                if status.transportConnected {
                    hasConnected = true
                } else if hasConnected {
                    disconnectedAfterConnectCount += 1
                }
                isConnected = status.isReady
            case .message(let message):
                messageIDs.insert(message.id)
            case .messageDeleted:
                break
            case .messagesInvalidated:
                break
            case .messagesReplaced(_, let messages):
                messageIDs.formUnion(messages.map(\.id))
            case .drawEventsChanged:
                break
            case .typing(_, _, let active):
                if active { typingStarted = true } else { typingStopped = true }
            case .characterPulse(let event):
                characterPulseEventIDs.insert(event.id)
            case .characterThrow:
                break
            case .presence(_, let userID, let state):
                presence[userID] = state
                presenceUpdateCounts[userID, default: 0] += 1
            case .snapshot(let snapshot):
                apply(snapshot)
            case .reconciliation(let reconciliation):
                apply(reconciliation.snapshot)
            case .technicalError:
                break
            }
        }
    }
}

private struct IntegrationTimeout: Error {}
