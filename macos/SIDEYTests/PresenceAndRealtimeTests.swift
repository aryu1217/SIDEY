import XCTest
@testable import SIDEY

@MainActor
final class PresenceAndRealtimeTests: XCTestCase {
    func testSystemActivityStateUsesAnyInputAndLiveScreenLockState() {
        XCTAssertEqual(SystemActivityMonitor.anyInputEventType.rawValue, UInt32.max)
        XCTAssertEqual(
            SystemActivityMonitor.state(screenLocked: false, idleSeconds: 299.9, awayThreshold: 300),
            .online
        )
        XCTAssertEqual(
            SystemActivityMonitor.state(screenLocked: false, idleSeconds: 300, awayThreshold: 300),
            .away
        )
        XCTAssertEqual(
            SystemActivityMonitor.state(screenLocked: true, idleSeconds: 0, awayThreshold: 300),
            .away
        )
    }

    func testSystemActivityPollingRecoversWhenUnlockNotificationWasMissed() {
        var screenLocked = true
        var states: [PresenceState] = []
        let monitor = SystemActivityMonitor(
            idleSecondsProvider: { 0 },
            screenLockedProvider: { screenLocked },
            onChange: { states.append($0) }
        )

        monitor.start()
        screenLocked = false
        monitor.refresh()
        monitor.stop()

        XCTAssertEqual(states, [.away, .online])
    }

    func testSoloRoomUsesEffectiveLocalPresenceInsteadOfOfflineSnapshot() {
        let roomID = UUID()
        let userID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "나", characterID: "minty_pup"),
                rooms: [Room(
                    id: roomID,
                    name: "혼자 테스트",
                    ownerID: userID,
                    members: [RoomMember(
                        userID: userID,
                        nickname: "나",
                        characterID: "minty_pup",
                        presence: .offline
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: userID
        )

        model.connectionState = .online
        model.presence = .online
        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .online)

        model.presence = .away
        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .away)

        model.connectionState = .connecting
        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .reconnecting)
    }

    func testLocalPresenceUsesRecoveredTransportWhileReconciliationIsPending() {
        let roomID = UUID()
        let userID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "나", characterID: "pixel_hamster"),
                rooms: [Room(
                    id: roomID,
                    name: "복구 테스트",
                    ownerID: userID,
                    members: [RoomMember(
                        userID: userID,
                        nickname: "나",
                        characterID: "pixel_hamster",
                        presence: .offline
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: userID
        )
        model.connectionState = .connecting
        model.presence = .online

        model.setActiveRoomRealtimeConnected(true)

        XCTAssertTrue(model.activeRoomTransportConnected)
        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .online)
    }

    func testOfflinePresenceUsesRedIndicatorWhileReconnectRemainsGray() {
        XCTAssertEqual(PresenceIndicatorTone.tone(for: .offline), .red)
        XCTAssertEqual(PresenceIndicatorTone.tone(for: .reconnecting), .gray)
    }

    func testTypingStopRestoresAwayPresence() {
        let roomID = UUID()
        let friendID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: friendID,
                    members: [RoomMember(
                        userID: friendID,
                        nickname: "친구",
                        characterID: "minty_pup",
                        presence: .away
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        model.updateTyping(roomID: roomID, userID: friendID, active: true)
        XCTAssertEqual(model.rooms[0].members[0].presence, .typing)
        model.updateTyping(roomID: roomID, userID: friendID, active: false)
        XCTAssertEqual(model.rooms[0].members[0].presence, .away)
    }

    func testTypingKeepsBaseMotionAndUsesSeparateFlag() {
        let roomID = UUID()
        let friendID = UUID()
        var preferences = AppPreferences.defaults
        preferences.activeRoomID = roomID
        let model = AppModel(preferences: preferences)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: friendID,
                    members: [RoomMember(
                        userID: friendID,
                        nickname: "친구",
                        characterID: "minty_pup",
                        presence: .online
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        model.updateTyping(roomID: roomID, userID: friendID, active: true)

        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .online)
        XCTAssertEqual(model.pixelWorldMembers.first?.isTyping, true)
        XCTAssertEqual(model.pixelWorldMembers.first?.characterID, "pixel_hamster")
    }

    func testPresenceAndTypingArrivingBeforeNewRoomSnapshotAreRetained() {
        let roomID = UUID()
        let friendID = UUID()
        let model = AppModel(preferences: .defaults)

        model.updatePresence(roomID: roomID, userID: friendID, state: .online)
        model.updateTyping(roomID: roomID, userID: friendID, active: true)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "새 그룹",
                    ownerID: friendID,
                    members: [RoomMember(
                        userID: friendID,
                        nickname: "친구",
                        characterID: "pixel_cat",
                        presence: .offline
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        XCTAssertEqual(model.rooms[0].members[0].presence, .typing)
        XCTAssertEqual(model.pixelWorldMembers.first?.presence, .online)
        XCTAssertEqual(model.pixelWorldMembers.first?.isTyping, true)
    }

    func testJoinedRoomIsResolvedAgainstFreshSnapshotForRealtimePublication() {
        let previousRoomID = UUID()
        let joinedRoomID = UUID()
        var preferences = AppPreferences.defaults
        preferences.activeRoomID = previousRoomID
        let model = AppModel(preferences: preferences)
        let previousRoom = Room(
            id: previousRoomID,
            name: "기존 그룹",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            inviteVersion: 1
        )
        let joinedRoom = Room(
            id: joinedRoomID,
            name: "참여한 그룹",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "CD••••",
            inviteVersion: 1
        )
        model.apply(
            snapshot: BackendSnapshot(profile: nil, rooms: [previousRoom]),
            currentUserID: UUID()
        )

        model.preferences.activeRoomID = joinedRoomID

        XCTAssertEqual(model.activeRoom?.id, previousRoomID)
        XCTAssertEqual(
            model.resolvedActiveRoomID(in: [previousRoom, joinedRoom]),
            joinedRoomID
        )
        XCTAssertEqual(model.resolvedActiveRoomID(in: [joinedRoom]), joinedRoomID)
    }

    func testOfflineMembersCanBeFilteredWithoutRemovingOnlineMembers() {
        let roomID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: UUID(),
                    members: [
                        RoomMember(userID: UUID(), nickname: "온라인", characterID: "pixel_hamster", presence: .online),
                        RoomMember(userID: UUID(), nickname: "오프라인", characterID: "pixel_hamster", presence: .offline)
                    ],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        XCTAssertEqual(model.pixelWorldMembers.count, 2)
        model.preferences.showOfflineMembers = false
        XCTAssertEqual(model.pixelWorldMembers.map(\.nickname), ["온라인"])
    }

    func testCurrentUserRemainsVisibleWhenOfflineMembersAreHidden() {
        let userID = UUID()
        let roomID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "나", characterID: "pixel_cat"),
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: userID,
                    members: [RoomMember(
                        userID: userID,
                        nickname: "나",
                        characterID: "pixel_cat",
                        presence: .offline
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: userID
        )
        model.preferences.showOfflineMembers = false
        model.connectionState = .idle

        XCTAssertEqual(model.pixelWorldMembers.count, 1)
        XCTAssertTrue(model.pixelWorldMembers[0].isCurrentUser)
        XCTAssertEqual(model.pixelWorldMembers[0].characterID, "pixel_cat")
        XCTAssertEqual(model.selectedCharacterID, "pixel_cat")
        XCTAssertEqual(model.preferences.selectedCharacterID, "pixel_cat")
    }

    func testRealtimePlanCapsRoomsAndSwitchesActiveRoom() {
        let rooms = (0..<6).map { _ in UUID() }
        let existing = Set([rooms[0], UUID()])
        let plan = RealtimeRoomPlan.make(existing: existing, requested: rooms, activeRoomID: rooms[4])

        XCTAssertEqual(plan.desired.count, 5)
        XCTAssertEqual(plan.activeRoomID, rooms[4])
        XCTAssertTrue(plan.removals.isSubset(of: existing))
        XCTAssertFalse(plan.desired.contains(rooms[5]))
    }

    func testRealtimeConnectionRequiresEveryDesiredRoomAndRecoversAfterResubscribe() {
        let firstRoomID = UUID()
        let secondRoomID = UUID()
        var tracker = RealtimeConnectionTracker()

        tracker.replaceDesiredRoomIDs([firstRoomID, secondRoomID])
        XCTAssertFalse(tracker.isConnected)

        tracker.setSubscribed(true, roomID: firstRoomID)
        XCTAssertFalse(tracker.isConnected)
        tracker.setSubscribed(true, roomID: secondRoomID)
        XCTAssertTrue(tracker.isConnected)

        tracker.setSubscribed(false, roomID: firstRoomID)
        XCTAssertFalse(tracker.isConnected)
        tracker.setSubscribed(true, roomID: firstRoomID)
        XCTAssertTrue(tracker.isConnected)

        tracker.replaceDesiredRoomIDs([secondRoomID])
        XCTAssertTrue(tracker.isConnected)
    }

    func testConnectionStatusSeparatesTransportFromRecoveryReadiness() {
        XCTAssertFalse(BackendConnectionStatus(
            transportConnected: false,
            recoveryReconciled: true
        ).isReady)
        XCTAssertFalse(BackendConnectionStatus(
            transportConnected: true,
            recoveryReconciled: false
        ).isReady)
        XCTAssertTrue(BackendConnectionStatus(
            transportConnected: true,
            recoveryReconciled: true
        ).isReady)
    }

    func testActiveRoomTransportCanRecoverBeforeEveryDesiredRoom() {
        let activeRoomID = UUID()
        let delayedRoomID = UUID()
        var tracker = RealtimeConnectionTracker()
        tracker.replaceDesiredRoomIDs([activeRoomID, delayedRoomID])
        tracker.setSubscribed(true, roomID: activeRoomID)

        XCTAssertTrue(tracker.isSubscribed(roomID: activeRoomID))
        XCTAssertFalse(tracker.isSubscribed(roomID: delayedRoomID))
        XCTAssertFalse(tracker.isConnected)

        let status = BackendConnectionStatus(
            transportConnected: tracker.isConnected,
            recoveryReconciled: false,
            activeRoomTransportConnected: tracker.isSubscribed(roomID: activeRoomID)
        )
        XCTAssertFalse(status.isReady)
        XCTAssertTrue(status.activeRoomTransportConnected)
    }

    func testInactiveRoomChannelSwapKeepsActiveRoomTransportConnected() {
        let status = RealtimeConnectionStatusPolicy.resolve(
            pathAvailable: true,
            socketAvailable: true,
            recoveryTaskRunning: false,
            rebuildingChannels: true,
            allRoomsSubscribed: false,
            recoveryReconciled: false,
            hasActiveRoom: true,
            activeRoomSubscribed: true
        )

        XCTAssertFalse(status.transportConnected)
        XCTAssertFalse(status.isReady)
        XCTAssertTrue(status.activeRoomTransportConnected)
    }

    func testActiveRoomChannelSwapTemporarilyDisconnectsOnlyActiveRoom() {
        let status = RealtimeConnectionStatusPolicy.resolve(
            pathAvailable: true,
            socketAvailable: true,
            recoveryTaskRunning: false,
            rebuildingChannels: true,
            allRoomsSubscribed: false,
            recoveryReconciled: false,
            hasActiveRoom: true,
            activeRoomSubscribed: false
        )

        XCTAssertFalse(status.activeRoomTransportConnected)
    }

    func testActiveRoomReconnectDoesNotResetInactiveRoomPresence() {
        let userID = UUID()
        let activeRoomID = UUID()
        let inactiveRoomID = UUID()
        let inactiveFriendID = UUID()
        var preferences = AppPreferences.defaults
        preferences.activeRoomID = activeRoomID
        let model = AppModel(preferences: preferences)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "나", characterID: "pixel_hamster"),
                rooms: [
                    Room(
                        id: activeRoomID,
                        name: "활성",
                        ownerID: userID,
                        members: [RoomMember(
                            userID: userID,
                            nickname: "나",
                            characterID: "pixel_hamster",
                            presence: .online
                        )],
                        inviteCodeHint: "AB••••"
                    ),
                    Room(
                        id: inactiveRoomID,
                        name: "비활성",
                        ownerID: inactiveFriendID,
                        members: [RoomMember(
                            userID: inactiveFriendID,
                            nickname: "친구",
                            characterID: "pixel_cat",
                            presence: .online
                        )],
                        inviteCodeHint: "CD••••"
                    ),
                ]
            ),
            currentUserID: userID
        )

        model.setActiveRoomRealtimeConnected(false)

        XCTAssertEqual(model.rooms[0].members[0].presence, .reconnecting)
        XCTAssertEqual(model.rooms[1].members[0].presence, .online)
    }

    func testProfileDraftSurvivesCharacterResponseAndUnrelatedSnapshot() {
        let userID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "확정닉", characterID: "pixel_hamster"),
                rooms: []
            ),
            currentUserID: userID
        )
        model.nickname = "편집중"

        model.apply(profile: Profile(
            id: userID,
            nickname: "확정닉",
            characterID: "pixel_cat"
        ))
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "확정닉", characterID: "pixel_cat"),
                rooms: []
            ),
            currentUserID: userID
        )

        XCTAssertEqual(model.nickname, "편집중")
        XCTAssertEqual(model.confirmedNickname, "확정닉")
        XCTAssertEqual(model.selectedCharacterID, "pixel_cat")
        XCTAssertTrue(model.hasNicknameChanges)
    }

    func testNicknameDirtyStateUsesNormalizedDraftAndValidation() {
        let userID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "사이디", characterID: "pixel_hamster"),
                rooms: []
            ),
            currentUserID: userID
        )

        model.nickname = "  사이디  "
        XCTAssertFalse(model.hasNicknameChanges)
        XCTAssertTrue(model.nicknameDraftIsValid)
        model.nickname = "한"
        XCTAssertTrue(model.hasNicknameChanges)
        XCTAssertFalse(model.nicknameDraftIsValid)
    }

    func testCharacterRequestKeepsConfirmedSelectionUntilSuccessAndBlocksDuplicates() {
        let userID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "사이디", characterID: "pixel_hamster"),
                rooms: []
            ),
            currentUserID: userID
        )

        XCTAssertTrue(model.beginCharacterEquipmentRequest(characterID: "pixel_cat"))
        XCTAssertEqual(model.selectedCharacterID, "pixel_hamster")
        XCTAssertEqual(model.pendingCharacterID, "pixel_cat")
        XCTAssertFalse(model.beginCharacterEquipmentRequest(characterID: "pixel_penguin"))

        model.endCharacterEquipmentRequest()
        XCTAssertEqual(model.selectedCharacterID, "pixel_hamster")
        model.apply(profile: Profile(
            id: userID,
            nickname: "사이디",
            characterID: "pixel_cat"
        ))
        XCTAssertEqual(model.selectedCharacterID, "pixel_cat")
    }

    func testProfileApplyChangesIdentityWithoutDiscardingFriendPresence() {
        let roomID = UUID()
        let userID = UUID()
        let friendID = UUID()
        var preferences = AppPreferences.defaults
        preferences.activeRoomID = roomID
        let model = AppModel(preferences: preferences)
        model.apply(
            snapshot: BackendSnapshot(
                profile: Profile(id: userID, nickname: "이전닉", characterID: "pixel_hamster"),
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: userID,
                    members: [
                        RoomMember(
                            userID: userID,
                            nickname: "이전닉",
                            characterID: "pixel_hamster",
                            presence: .offline
                        ),
                        RoomMember(
                            userID: friendID,
                            nickname: "친구",
                            characterID: "pixel_cat",
                            presence: .offline
                        )
                    ],
                    inviteCodeHint: "AB••••"
                )]
            ),
            currentUserID: userID
        )
        model.connectionState = .online
        model.updatePresence(roomID: roomID, userID: friendID, state: .online)

        model.apply(profile: Profile(
            id: userID,
            nickname: "새닉네임",
            characterID: "pixel_penguin"
        ))

        XCTAssertEqual(model.connectionState, .online)
        XCTAssertEqual(model.nickname, "새닉네임")
        XCTAssertEqual(model.selectedCharacterID, "pixel_penguin")
        XCTAssertEqual(
            model.rooms[0].members.first(where: { $0.userID == userID })?.nickname,
            "새닉네임"
        )
        XCTAssertEqual(
            model.rooms[0].members.first(where: { $0.userID == friendID })?.presence,
            .online
        )
    }

    func testProfileAndRoomNameChangesDoNotChangeRealtimeTopology() {
        let roomID = UUID()
        let original = Room(
            id: roomID,
            name: "이전 이름",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 7
        )
        var metadataOnly = original
        metadataOnly.name = "새 이름"
        metadataOnly.members = [RoomMember(
            userID: UUID(),
            nickname: "새 프로필",
            characterID: "pixel_penguin",
            presence: .offline
        )]
        var newEpoch = metadataOnly
        newEpoch.realtimeEpoch = 8

        XCTAssertEqual(
            RealtimeTopology(rooms: [original]),
            RealtimeTopology(rooms: [metadataOnly])
        )
        XCTAssertNotEqual(
            RealtimeTopology(rooms: [original]),
            RealtimeTopology(rooms: [newEpoch])
        )
    }

    func testDesiredRealtimeTopologyKeepsEpochWithoutLiveChannels() {
        let roomID = UUID()
        let room = Room(
            id: roomID,
            name: "복구 대상",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 7
        )
        var desired = RealtimeDesiredTopology()

        desired.replace(rooms: [room])

        XCTAssertEqual(desired.roomIDs, [roomID])
        XCTAssertEqual(desired.epoch(for: roomID), 7)
        XCTAssertEqual(RealtimeTopology(channelEpochs: [:]).roomEpochs, [:])
        XCTAssertEqual(desired.epoch(for: roomID), 7)
    }

    func testRealtimeTopologyUpdateAddsRoomWithoutReplacingStableChannels() {
        let stableRoom = Room(
            id: UUID(),
            name: "기존 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 3
        )
        let addedRoom = Room(
            id: UUID(),
            name: "추가 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "CD••••",
            realtimeEpoch: 1
        )
        let plan = RealtimeTopologyUpdatePlan.make(
            live: RealtimeTopology(rooms: [stableRoom]),
            requestedRooms: [stableRoom, addedRoom]
        )

        XCTAssertEqual(plan.additions, [addedRoom.id])
        XCTAssertTrue(plan.removals.isEmpty)
    }

    func testRealtimeTopologyUpdateRemovesOnlyDepartedRoom() {
        let stableRoom = Room(
            id: UUID(),
            name: "유지 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 3
        )
        let departedRoom = Room(
            id: UUID(),
            name: "나간 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "CD••••",
            realtimeEpoch: 1
        )
        let plan = RealtimeTopologyUpdatePlan.make(
            live: RealtimeTopology(rooms: [stableRoom, departedRoom]),
            requestedRooms: [stableRoom]
        )

        XCTAssertTrue(plan.additions.isEmpty)
        XCTAssertEqual(plan.removals, [departedRoom.id])
    }

    func testRealtimeTopologyUpdateReplacesOnlyChangedEpoch() {
        let stableRoom = Room(
            id: UUID(),
            name: "유지 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 3
        )
        let changedRoomID = UUID()
        let liveChangedRoom = Room(
            id: changedRoomID,
            name: "변경 방",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "CD••••",
            realtimeEpoch: 1
        )
        var requestedChangedRoom = liveChangedRoom
        requestedChangedRoom.realtimeEpoch = 2
        let plan = RealtimeTopologyUpdatePlan.make(
            live: RealtimeTopology(rooms: [stableRoom, liveChangedRoom]),
            requestedRooms: [stableRoom, requestedChangedRoom]
        )

        XCTAssertEqual(plan.additions, [changedRoomID])
        XCTAssertEqual(plan.removals, [changedRoomID])
    }

    func testRealtimeTopologyUpdateIgnoresMetadataOnlyChanges() {
        let liveRoom = Room(
            id: UUID(),
            name: "이전 이름",
            ownerID: UUID(),
            members: [],
            inviteCodeHint: "AB••••",
            realtimeEpoch: 3
        )
        var requestedRoom = liveRoom
        requestedRoom.name = "새 이름"
        requestedRoom.members = [RoomMember(
            userID: UUID(),
            nickname: "친구",
            characterID: "pixel_penguin",
            presence: .offline
        )]
        let plan = RealtimeTopologyUpdatePlan.make(
            live: RealtimeTopology(rooms: [liveRoom]),
            requestedRooms: [requestedRoom]
        )

        XCTAssertTrue(plan.additions.isEmpty)
        XCTAssertTrue(plan.removals.isEmpty)
    }

    func testDeniedRoomRepairRetriesLatestEpochWithoutRemovingHealthyRoom() async throws {
        let ownerID = UUID()
        let stableRoom = Room(
            id: UUID(), name: "유지 방", ownerID: ownerID, members: [],
            inviteCodeHint: "AB••••", realtimeEpoch: 3
        )
        var changedRoom = Room(
            id: UUID(), name: "변경 방", ownerID: ownerID, members: [],
            inviteCodeHint: "CD••••", realtimeEpoch: 2
        )
        let probe = RoomRepairProbe()

        do {
            _ = try await RealtimeRoomChannelRepair.run(
                rooms: [stableRoom, changedRoom],
                liveEpochs: [stableRoom.id: 3, changedRoom.id: 1],
                healthyRoomIDs: [stableRoom.id],
                removeChannel: { await probe.remove($0) },
                configureChannels: { try await probe.configure($0) }
            )
            XCTFail("A stale epoch subscription must fail before retry")
        } catch RoomRepairProbeError.denied {
            // The next attempt must use a new authorized snapshot.
        }

        changedRoom.realtimeEpoch = 3
        let repaired = try await RealtimeRoomChannelRepair.run(
            rooms: [stableRoom, changedRoom],
            liveEpochs: [stableRoom.id: 3],
            healthyRoomIDs: [stableRoom.id],
            removeChannel: { await probe.remove($0) },
            configureChannels: { try await probe.configure($0) }
        )

        let result = await probe.snapshot()
        XCTAssertTrue(repaired)
        XCTAssertEqual(result.removed, [changedRoom.id, changedRoom.id])
        XCTAssertEqual(result.configuredEpochs, [2, 3])
        XCTAssertEqual(result.stableRoomIDs, [stableRoom.id, stableRoom.id])
    }

    func testPersistentRoomDenialBacksOffWithoutRemovingOtherRoom() async throws {
        let ownerID = UUID()
        let stableRoom = Room(
            id: UUID(), name: "유지 방", ownerID: ownerID, members: [],
            inviteCodeHint: "AB••••", realtimeEpoch: 3
        )
        let deniedRoom = Room(
            id: UUID(), name: "거부 방", ownerID: ownerID, members: [],
            inviteCodeHint: "CD••••", realtimeEpoch: 2
        )
        let probe = RoomRepairProbe(failuresToInject: 4)
        var retry = RealtimeStructuralSnapshotRetry()

        for expectedDelay in [Duration.seconds(8), .seconds(16), .seconds(30), .seconds(30)] {
            do {
                _ = try await RealtimeRoomChannelRepair.run(
                    rooms: [stableRoom, deniedRoom],
                    liveEpochs: [stableRoom.id: 3],
                    healthyRoomIDs: [stableRoom.id],
                    removeChannel: { await probe.remove($0) },
                    configureChannels: { try await probe.configure($0) }
                )
                XCTFail("Injected permission denial must fail this room repair")
            } catch RoomRepairProbeError.denied {
                retry.failed()
            }
            XCTAssertEqual(retry.delay, expectedDelay)
        }

        let result = await probe.snapshot()
        XCTAssertEqual(result.removed, Array(repeating: deniedRoom.id, count: 4))
        XCTAssertEqual(result.stableRoomIDs, Array(repeating: stableRoom.id, count: 4))
    }

    func testRealtimeGenerationRejectsStaleCallbacksAndEpochs() {
        XCTAssertTrue(RealtimeChannelGenerationPolicy.accepts(
            candidateGeneration: 4,
            currentGeneration: 4,
            candidateCallbackGeneration: 2,
            currentCallbackGeneration: 2,
            desiredEpoch: 9,
            channelEpoch: 9
        ))
        XCTAssertFalse(RealtimeChannelGenerationPolicy.accepts(
            candidateGeneration: 3,
            currentGeneration: 4,
            candidateCallbackGeneration: 2,
            currentCallbackGeneration: 2,
            desiredEpoch: 9,
            channelEpoch: 9
        ))
        XCTAssertFalse(RealtimeChannelGenerationPolicy.accepts(
            candidateGeneration: 4,
            currentGeneration: 4,
            candidateCallbackGeneration: 2,
            currentCallbackGeneration: 2,
            desiredEpoch: 10,
            channelEpoch: 9
        ))
        XCTAssertFalse(RealtimeChannelGenerationPolicy.accepts(
            candidateGeneration: 4,
            currentGeneration: 4,
            candidateCallbackGeneration: 1,
            currentCallbackGeneration: 2,
            desiredEpoch: 9,
            channelEpoch: 9
        ))
    }

    func testRealtimeChannelPairRequiresBothSubscriptions() {
        XCTAssertFalse(RealtimeChannelPairPolicy.isSubscribed(database: true, ephemeral: false))
        XCTAssertFalse(RealtimeChannelPairPolicy.isSubscribed(database: false, ephemeral: true))
        XCTAssertTrue(RealtimeChannelPairPolicy.isSubscribed(database: true, ephemeral: true))
    }

    func testNetworkAvailabilityTransitionsCoalesceDuplicateUpdates() {
        var state = NetworkAvailabilityState()

        XCTAssertEqual(state.update(.available), .initialAvailable)
        XCTAssertEqual(state.update(.available), .unchanged)
        XCTAssertEqual(state.update(.unavailable), .becameUnavailable)
        XCTAssertEqual(state.update(.unavailable), .unchanged)
        XCTAssertEqual(state.update(.available), .becameAvailable)

        var initiallyOfflineState = NetworkAvailabilityState()
        XCTAssertEqual(initiallyOfflineState.update(.unavailable), .becameUnavailable)
        XCTAssertEqual(initiallyOfflineState.update(.available), .becameAvailable)
    }

    func testRealtimeRecoveryBackoffStartsAtEightSecondsAndCapsAtThirty() {
        XCTAssertEqual(RealtimeRecoveryPolicy.watchdogInterval, 5)
        XCTAssertEqual(RealtimeRecoveryPolicy.pathRecoveryDebounce, 0.35)
        XCTAssertEqual(RealtimeRecoveryPolicy.delay(forAttempt: 1), 8)
        XCTAssertEqual(RealtimeRecoveryPolicy.delay(forAttempt: 2), 16)
        XCTAssertEqual(RealtimeRecoveryPolicy.delay(forAttempt: 3), 30)
        XCTAssertEqual(RealtimeRecoveryPolicy.delay(forAttempt: 20), 30)
        XCTAssertEqual(RealtimeRecoveryPolicy.delay(forAttempt: 0), 8)
    }

    func testActiveRoomSwitchPublishesOfflineToPreviousRoomAndLocalStateToNewRoom() {
        let previousRoomID = UUID()
        let nextRoomID = UUID()

        XCTAssertEqual(
            PresencePublicationPlan.state(
                for: previousRoomID,
                activeRoomID: nextRoomID,
                localPresence: .away
            ),
            .offline
        )
        XCTAssertEqual(
            PresencePublicationPlan.state(
                for: nextRoomID,
                activeRoomID: nextRoomID,
                localPresence: .away
            ),
            .away
        )
    }

    func testPresenceStateReplacementDoesNotEndOfflineWhenJoinAndLeaveShareAUser() {
        let updatedUserID = UUID()
        let departedUserID = UUID()

        let updates = PresenceChangePlan.updates(
            joined: [updatedUserID: .away],
            left: [updatedUserID, departedUserID]
        )

        XCTAssertEqual(
            updates.first(where: { $0.userID == updatedUserID })?.state,
            .away
        )
        XCTAssertEqual(
            updates.first(where: { $0.userID == departedUserID })?.state,
            .offline
        )
        XCTAssertEqual(updates.filter { $0.userID == updatedUserID }.count, 1)
    }

    func testReconnectRequiresFreshRemotePresenceInsteadOfRestoringStaleOnline() {
        let roomID = UUID()
        let friendID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: friendID,
                    members: [RoomMember(
                        userID: friendID,
                        nickname: "친구",
                        characterID: "minty_pup",
                        presence: .online
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        model.setActiveRoomRealtimeConnected(false)
        XCTAssertEqual(model.rooms[0].members[0].presence, .reconnecting)
        model.setActiveRoomRealtimeConnected(true)
        XCTAssertEqual(model.rooms[0].members[0].presence, .offline)
    }

    func testFirebaseRecoveryRestoresLiveSupabasePresenceWithoutAnotherPeerJoin() {
        let roomID = UUID()
        let onlineID = UUID()
        let awayID = UUID()
        let room = presenceReplayRoom(id: roomID, userIDs: [onlineID, awayID])
        let model = AppModel(preferences: .defaults)
        model.apply(snapshot: BackendSnapshot(profile: nil, rooms: [room]), currentUserID: UUID())
        var replay = BackendPresenceReplay()
        for (userID, state) in [(onlineID, PresenceState.online), (awayID, .away)] {
            replay.record(roomID: roomID, userID: userID, state: state)
            model.updatePresence(roomID: roomID, userID: userID, state: state)
        }
        model.setActiveRoomRealtimeConnected(true)

        // Firebase credentials expire, but the Supabase Presence channel stays live.
        model.setActiveRoomRealtimeConnected(false)
        XCTAssertTrue(model.rooms[0].members.allSatisfy { $0.presence == .reconnecting })
        for event in replay.snapshot(rooms: [room], subscribedRoomIDs: [roomID]) {
            guard case .presence(let roomID, let userID, let state) = event else {
                return XCTFail("Presence replay must contain only Presence events")
            }
            model.updatePresence(roomID: roomID, userID: userID, state: state)
        }
        model.setActiveRoomRealtimeConnected(true)

        XCTAssertEqual(model.rooms[0].members.first { $0.userID == onlineID }?.presence, .online)
        XCTAssertEqual(model.rooms[0].members.first { $0.userID == awayID }?.presence, .away)
    }

    func testPresenceReplayInvalidationDropsDisconnectedRoomWithoutDroppingOtherRooms() {
        let disconnectedRoomID = UUID()
        let liveRoomID = UUID()
        let userID = UUID()
        var replay = BackendPresenceReplay()
        replay.record(roomID: disconnectedRoomID, userID: userID, state: .online)
        replay.record(roomID: liveRoomID, userID: userID, state: .away)

        replay.invalidate(roomID: disconnectedRoomID)
        let events = replay.events(subscribedRoomIDs: [disconnectedRoomID, liveRoomID])
        XCTAssertEqual(events.count, 1)
        guard let event = events.first,
              case .presence(let roomID, let replayedUserID, let state) = event else {
            return XCTFail("The unaffected room must retain its live Presence")
        }
        XCTAssertEqual(roomID, liveRoomID)
        XCTAssertEqual(replayedUserID, userID)
        XCTAssertEqual(state, .away)

        let baseline = replay.snapshot(
            rooms: [presenceReplayRoom(id: disconnectedRoomID, userIDs: [userID])],
            subscribedRoomIDs: [disconnectedRoomID]
        )
        guard let baselineEvent = baseline.first,
              case .presence(_, _, let disconnectedState) = baselineEvent else {
            return XCTFail("The rebuilt room must start from an offline baseline")
        }
        XCTAssertEqual(disconnectedState, .offline)

        replay.invalidateAll()
        XCTAssertTrue(replay.events(subscribedRoomIDs: [disconnectedRoomID, liveRoomID]).isEmpty)
    }

    func testPresenceReplaySnapshotClearsDepartedAndUnobservedPeersAndExcludesRemovedMembers() {
        let roomID = UUID()
        let departedID = UUID()
        let unobservedID = UUID()
        let removedMemberID = UUID()
        let room = presenceReplayRoom(id: roomID, userIDs: [departedID, unobservedID])
        let model = AppModel(preferences: .defaults)
        model.apply(snapshot: BackendSnapshot(profile: nil, rooms: [room]), currentUserID: UUID())
        model.updatePresence(roomID: roomID, userID: departedID, state: .online)
        model.updatePresence(roomID: roomID, userID: unobservedID, state: .away)
        var replay = BackendPresenceReplay()
        replay.record(roomID: roomID, userID: departedID, state: .online)
        replay.record(roomID: roomID, userID: removedMemberID, state: .online)
        replay.record(roomID: roomID, userID: departedID, state: .offline)

        let events = replay.snapshot(rooms: [room], subscribedRoomIDs: [roomID])
        XCTAssertEqual(events.count, 2)
        var replayedUserIDs: Set<UUID> = []
        for event in events {
            guard case .presence(let roomID, let userID, let state) = event else {
                return XCTFail("Presence snapshot must contain only Presence events")
            }
            replayedUserIDs.insert(userID)
            model.updatePresence(roomID: roomID, userID: userID, state: state)
        }
        XCTAssertEqual(replayedUserIDs, [departedID, unobservedID])
        XCTAssertTrue(model.rooms[0].members.allSatisfy { $0.presence == .offline })
        XCTAssertEqual(replay.events(subscribedRoomIDs: [roomID]).count, 1)
    }

    func testPresenceReplayNeverReplaysAnUnsubscribedRoom() {
        let roomID = UUID()
        let userID = UUID()
        var replay = BackendPresenceReplay()
        replay.record(roomID: roomID, userID: userID, state: .online)

        XCTAssertTrue(replay.events(subscribedRoomIDs: []).isEmpty)
        XCTAssertTrue(replay.snapshot(
            rooms: [presenceReplayRoom(id: roomID, userIDs: [userID])],
            subscribedRoomIDs: []
        ).isEmpty)
    }

    private func presenceReplayRoom(id: UUID, userIDs: [UUID]) -> Room {
        Room(
            id: id,
            name: "친구들",
            ownerID: userIDs[0],
            members: userIDs.map {
                RoomMember(userID: $0, nickname: "친구", characterID: "minty_pup", presence: .offline)
            },
            inviteCodeHint: "AB••••",
            inviteVersion: 1
        )
    }

    func testBroadcastTypingLeaseDoesNotRemainStuckAcrossReconnect() {
        let roomID = UUID()
        let friendID = UUID()
        let model = AppModel(preferences: .defaults)
        model.apply(
            snapshot: BackendSnapshot(
                profile: nil,
                rooms: [Room(
                    id: roomID,
                    name: "친구들",
                    ownerID: friendID,
                    members: [RoomMember(
                        userID: friendID,
                        nickname: "친구",
                        characterID: "minty_pup",
                        presence: .away
                    )],
                    inviteCodeHint: "AB••••",
                    inviteVersion: 1
                )]
            ),
            currentUserID: UUID()
        )

        model.updateTyping(roomID: roomID, userID: friendID, active: true)
        XCTAssertEqual(model.rooms[0].members[0].presence, .typing)

        model.setActiveRoomRealtimeConnected(false)
        XCTAssertEqual(model.rooms[0].members[0].presence, .reconnecting)
        model.setActiveRoomRealtimeConnected(true)
        XCTAssertEqual(model.rooms[0].members[0].presence, .offline)
    }

    func testCharacterPulseCooldownAllowsOneEventPerMemberEverySecond() {
        let roomID = UUID()
        let userID = UUID()
        var cooldown = CharacterPulseCooldown()

        XCTAssertTrue(cooldown.accept(roomID: roomID, userID: userID, uptime: 100))
        XCTAssertFalse(cooldown.accept(roomID: roomID, userID: userID, uptime: 100.999))
        XCTAssertTrue(cooldown.accept(roomID: roomID, userID: userID, uptime: 101))
        XCTAssertTrue(cooldown.accept(roomID: roomID, userID: UUID(), uptime: 101))
        XCTAssertFalse(cooldown.accept(roomID: roomID, userID: userID, uptime: .infinity))
        XCTAssertEqual(CharacterPulseCooldown.duration, 1)
    }

    func testCharacterThrowCooldownIsGlobalPerActorAtHalfASecond() {
        let actor = UUID()
        var cooldown = CharacterThrowCooldown()

        XCTAssertTrue(cooldown.accept(actorUserID: actor, uptime: 10))
        XCTAssertFalse(cooldown.accept(actorUserID: actor, uptime: 10.499))
        XCTAssertTrue(cooldown.accept(actorUserID: actor, uptime: 10.5))
        XCTAssertTrue(cooldown.accept(actorUserID: UUID(), uptime: 10.5))
        XCTAssertFalse(cooldown.accept(actorUserID: actor, uptime: .infinity))
        XCTAssertEqual(CharacterThrowCooldown.duration, 0.5)
    }

    func testCharacterThrowTargetIgnoresPresenceButRejectsCurrentUser() {
        for presence in PresenceState.allCases {
            XCTAssertTrue(CharacterThrowTargetPolicy.canTarget(PixelWorldMember(
                id: UUID(), nickname: "친구", characterID: "pixel_hamster",
                presence: presence, isTyping: presence == .typing, isCurrentUser: false
            )), "\(presence)")
        }
        XCTAssertFalse(CharacterThrowTargetPolicy.canTarget(PixelWorldMember(
            id: UUID(), nickname: "나", characterID: "pixel_hamster",
            presence: .online, isTyping: false, isCurrentUser: true
        )))
    }


    func testLatestRoomSelectionWinsWithoutApplyingCompletedStaleSwitch() async {
        let roomB = UUID()
        let roomC = UUID()
        let bStarted = expectation(description: "B 전환 시작")
        let cCommitted = expectation(description: "C만 적용")
        let gate = AsyncTestGate()
        let probe = SerialExecutionProbe<UUID>()
        var committedRoomIDs: [UUID] = []

        let pipeline = RoomSwitchPipeline(
            debounce: .milliseconds(5),
            performSwitch: { roomID in
                await probe.begin(roomID)
                if roomID == roomB {
                    bStarted.fulfill()
                    await gate.wait()
                }
                await probe.end()
                return []
            },
            restoreCommittedRoom: {},
            operationChanged: { _ in },
            committed: { roomID, _ in
                committedRoomIDs.append(roomID)
                if roomID == roomC { cCommitted.fulfill() }
            },
            failed: { _, error, _ in
                XCTFail("예상하지 못한 전환 실패: \(error)")
            }
        )

        pipeline.request(roomB)
        await fulfillment(of: [bStarted], timeout: 1)
        pipeline.request(roomC)
        await gate.open()
        await fulfillment(of: [cCommitted], timeout: 1)

        let switchProbe = await probe.snapshot()
        XCTAssertEqual(committedRoomIDs, [roomC])
        XCTAssertEqual(switchProbe.maximumConcurrent, 1)
        XCTAssertEqual(switchProbe.started, [roomB, roomC])
        pipeline.cancel()
    }

    func testRepeatedRoomReconciliationPublishesPresenceOnlyOncePerUnchangedRoom() {
        var ledger = PresencePublicationLedger()
        let activeRoom = UUID()
        let otherRoom = UUID()
        var publications: [UUID] = []
        for _ in 0..<10 {
            for roomID in [activeRoom, otherRoom] {
                let state = PresencePublicationPlan.state(
                    for: roomID, activeRoomID: activeRoom, localPresence: .online
                )
                if ledger.needsPublication(roomID: roomID, channelGeneration: 1, state: state) {
                    publications.append(roomID)
                    ledger.didPublish(roomID: roomID, channelGeneration: 1, state: state,
                                      observedRevision: ledger.invalidationRevision)
                }
            }
        }
        XCTAssertEqual(publications, [activeRoom, otherRoom])
        XCTAssertTrue(ledger.needsPublication(
            roomID: activeRoom, channelGeneration: 1, state: .away
        ))
        XCTAssertFalse(ledger.needsPublication(
            roomID: otherRoom, channelGeneration: 1, state: .offline
        ))
    }

    func testRejoiningRoomRepublishesPresenceWithoutRepublishingHealthyRoom() {
        var ledger = PresencePublicationLedger()
        let repairedRoom = UUID()
        let healthyRoom = UUID()
        for roomID in [repairedRoom, healthyRoom] {
            ledger.didPublish(roomID: roomID, channelGeneration: 1, state: .online,
                              observedRevision: ledger.invalidationRevision)
        }
        // A new channel object needs an initial track even with identical state.
        XCTAssertTrue(ledger.needsPublication(
            roomID: repairedRoom, channelGeneration: 2, state: .online
        ))
        // The SDK can also rejoin the same channel object after an unsubscribe.
        let revisionBeforeUnsubscribe = ledger.invalidationRevision
        ledger.invalidate(roomID: repairedRoom)
        XCTAssertNotEqual(ledger.invalidationRevision, revisionBeforeUnsubscribe)
        XCTAssertTrue(ledger.needsPublication(
            roomID: repairedRoom, channelGeneration: 1, state: .online
        ))
        XCTAssertFalse(ledger.needsPublication(
            roomID: healthyRoom, channelGeneration: 1, state: .online
        ))
        ledger.invalidateAll()
        XCTAssertTrue(ledger.needsPublication(
            roomID: healthyRoom, channelGeneration: 1, state: .online
        ))
    }

    func testSubscriptionResetDuringTrackCannotSuppressRetryAfterRejoin() {
        var ledger = PresencePublicationLedger()
        let roomID = UUID()
        let revisionBeforeTrack = ledger.invalidationRevision
        XCTAssertTrue(ledger.needsPublication(roomID: roomID, channelGeneration: 1, state: .online))
        // Same SDK object has already rejoined when its delayed reset arrives.
        ledger.invalidate(roomID: roomID)
        XCTAssertFalse(ledger.didPublish(
            roomID: roomID, channelGeneration: 1, state: .online,
            observedRevision: revisionBeforeTrack
        ))
        XCTAssertTrue(ledger.needsPublication(roomID: roomID, channelGeneration: 1, state: .online))
        XCTAssertTrue(ledger.didPublish(
            roomID: roomID, channelGeneration: 1, state: .online,
            observedRevision: ledger.invalidationRevision
        ))
        XCTAssertFalse(ledger.needsPublication(roomID: roomID, channelGeneration: 1, state: .online))
    }

    func testPresencePublicationCoalescesToLatestFullStateAndRunsSerially() async throws {
        let firstStarted = expectation(description: "첫 Presence 게시 시작")
        let gate = AsyncTestGate()
        let probe = SerialExecutionProbe<Int>()
        let queue = PresencePublicationQueue<Int> { value in
            await probe.begin(value)
            if value == 1 {
                firstStarted.fulfill()
                await gate.wait()
            }
            await probe.end()
        }

        let first = Task { try await queue.submit(1) }
        await fulfillment(of: [firstStarted], timeout: 1)
        let second = Task { try await queue.submit(2) }
        try await Task.sleep(for: .milliseconds(10))
        let third = Task { try await queue.submit(3) }
        try await Task.sleep(for: .milliseconds(10))
        await gate.open()

        try await first.value
        try await second.value
        try await third.value
        let publicationProbe = await probe.snapshot()
        XCTAssertEqual(publicationProbe.maximumConcurrent, 1)
        XCTAssertEqual(publicationProbe.started, [1, 3])
        await queue.cancel()
    }
}

private actor AsyncTestGate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        guard !isOpen else { return }
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func open() {
        isOpen = true
        let pending = waiters
        waiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
}

private actor SerialExecutionProbe<Value: Sendable & Equatable> {
    private(set) var started: [Value] = []
    private(set) var maximumConcurrent = 0
    private var concurrent = 0

    func begin(_ value: Value) {
        concurrent += 1
        maximumConcurrent = max(maximumConcurrent, concurrent)
        started.append(value)
    }

    func end() {
        concurrent -= 1
    }

    func snapshot() -> (started: [Value], maximumConcurrent: Int) {
        (started, maximumConcurrent)
    }
}

private enum RoomRepairProbeError: Error {
    case denied
}

private actor RoomRepairProbe {
    private let failuresToInject: Int
    private var removed: [UUID] = []
    private var configuredEpochs: [Int] = []
    private var stableRoomIDs: [UUID] = []

    init(failuresToInject: Int = 1) {
        self.failuresToInject = failuresToInject
    }

    func remove(_ roomID: UUID) {
        removed.append(roomID)
    }

    func configure(_ rooms: [Room]) throws {
        stableRoomIDs.append(rooms[0].id)
        configuredEpochs.append(rooms[1].realtimeEpoch)
        if configuredEpochs.count <= failuresToInject { throw RoomRepairProbeError.denied }
    }

    func snapshot() -> (removed: [UUID], configuredEpochs: [Int], stableRoomIDs: [UUID]) {
        (removed, configuredEpochs, stableRoomIDs)
    }
}
