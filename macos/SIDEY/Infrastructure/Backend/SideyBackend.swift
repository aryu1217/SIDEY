import Foundation
import OSLog
import Supabase

struct AuthSessionRestorer<Value: Sendable>: Sendable {
    let loadCurrentSession: @Sendable () async throws -> Value
    let isSessionMissing: @Sendable (Error) -> Bool
    let loadFreshRefreshToken: @Sendable () throws -> String?
    let refreshSession: @Sendable (String) async throws -> Value

    func session() async throws -> Value {
        do {
            return try await loadCurrentSession()
        } catch {
            guard isSessionMissing(error),
                  let refreshToken = try loadFreshRefreshToken(),
                  !refreshToken.isEmpty else {
                throw error
            }
            return try await refreshSession(refreshToken)
        }
    }
}

actor SideyBackend {
    nonisolated let events: AsyncStream<BackendEvent>

    private let client: SupabaseClient
    private let keychain: KeychainStore
    private let legacyRefreshAccount: String
    private let inviteAccountPrefix: String
    private let eventContinuation: AsyncStream<BackendEvent>.Continuation
    private var eventSubscribers: [UUID: AsyncStream<BackendEvent>.Continuation] = [:]
    private var presenceReplay = BackendPresenceReplay()
    private let networkPathMonitor: any NetworkPathMonitoring
    private let recoveryLogger = Logger(
        subsystem: "app.sidey.desktop",
        category: "RealtimeRecovery"
    )
    private var channels: [UUID: RoomRealtimeChannels] = [:]
    private var desiredTopology = RealtimeDesiredTopology()
    private var realtimeGeneration = 0
    private var realtimeRecoveryTask: Task<Void, Never>?
    private var realtimeRecoveryAttempt = 0
    private var rebuildingGeneration: Int?
    private var realtimeWatchdogTask: Task<Void, Never>?
    private var networkPathTask: Task<Void, Never>?
    private var networkAvailability = NetworkAvailabilityState()
    private var structuralSnapshotTask: Task<Void, Never>?
    private var structuralSnapshotAttempt = 0
    private var typingExpiryTasks: [String: Task<Void, Never>] = [:]
    private var activeRoomID: UUID?
    private var localPresence: PresenceState = .online
    private lazy var presencePublicationQueue = PresencePublicationQueue<PresencePublicationIntent> {
        [weak self] intent in
        guard let self else { return }
        try await self.performPresencePublication(intent)
    }
    private var connectionTracker = RealtimeConnectionTracker()
    private var lastEmittedConnectionStatus: BackendConnectionStatus?
    private var recoveryReconciled = false
    private var isShuttingDown = false

    init(
        configuration: RuntimeConfiguration,
        keychain: KeychainStore = KeychainStore(),
        networkPathMonitor: any NetworkPathMonitoring = SystemNetworkPathMonitor()
    ) {
        let eventPair = AsyncStream<BackendEvent>.makeStream(
            bufferingPolicy: .bufferingNewest(256)
        )
        self.events = eventPair.stream
        self.eventContinuation = eventPair.continuation
        let fingerprint = configuration.backendFingerprint
        let legacyRefreshAccount = "supabase-refresh:\(fingerprint):default"
        self.keychain = keychain
        self.networkPathMonitor = networkPathMonitor
        self.legacyRefreshAccount = legacyRefreshAccount
        self.inviteAccountPrefix = "room-invite:\(fingerprint):default:"

        let storage = SideyAuthStorage(
            keychain: keychain,
            legacyRefreshAccount: legacyRefreshAccount
        )
        let authOptions = SupabaseClientOptions.AuthOptions(
            storage: storage,
            storageKey: "supabase-session:\(fingerprint):default",
            autoRefreshToken: true,
            emitLocalSessionAsInitialSession: true
        )
        let options = SupabaseClientOptions(auth: authOptions)
        self.client = SupabaseClient(
            supabaseURL: configuration.supabaseURL,
            supabaseKey: configuration.supabasePublishableKey,
            options: options
        )
    }

    func boot(requireExistingSession: Bool = false) async throws -> BackendSnapshot {
        _ = try await restoreOrCreateSession(requireExistingSession: requireExistingSession)
        return try await loadSnapshot()
    }

    func subscribeEvents() -> AsyncStream<BackendEvent> {
        let subscriberID = UUID()
        let pair = AsyncStream<BackendEvent>.makeStream(
            bufferingPolicy: .bufferingNewest(256)
        )
        guard !isShuttingDown else {
            pair.continuation.finish()
            return pair.stream
        }
        eventSubscribers[subscriberID] = pair.continuation
        // A Firebase-only recovery leaves these Supabase channels alive. A
        // new adapter will not receive another presence_state from the server.
        pair.continuation.yield(.connection(currentConnectionStatus()))
        for event in presenceReplay.events(subscribedRoomIDs: livePresenceRoomIDs()) {
            pair.continuation.yield(event)
        }
        pair.continuation.onTermination = { [weak self] _ in
            Task { await self?.removeEventSubscriber(subscriberID) }
        }
        return pair.stream
    }

    func signInWithApple(identityToken: String, nonce: String) async throws {
        _ = try await client.auth.signInWithIdToken(
            credentials: OpenIDConnectCredentials(
                provider: .apple,
                idToken: identityToken,
                nonce: nonce
            )
        )
    }

    func currentAccessToken() async throws -> String {
        try await authenticatedSession().accessToken
    }

    func createDrawEvent(
        roomID: UUID,
        kind: DrawKind,
        participantIDs: [UUID],
        entryLabels: [String]?,
        ladderLabels: [String]?,
        title: String? = nil
    ) async throws -> DrawEvent {
        _ = try await authenticatedSession()
        do {
            return try await client.rpc(
                "create_draw_event_v3",
                params: CreateDrawEventV3Parameters(
                    roomID: roomID, kind: kind.rawValue,
                    participantIDs: participantIDs, entryLabels: entryLabels,
                    ladderLabels: ladderLabels, title: title
                )
            ).execute().value
        } catch {
            throw SideyBackendError.normalized(error)
        }
    }

    func listDrawEvents(roomID: UUID) async throws -> [DrawEvent] {
        _ = try await authenticatedSession()
        return try await client.rpc(
            "list_draw_events",
            params: DrawRoomParameters(roomID: roomID)
        ).execute().value
    }

    func openDrawEvent(id: UUID) async throws -> DrawEvent {
        _ = try await authenticatedSession()
        return try await client.rpc(
            "open_draw_event",
            params: DrawEventParameters(eventID: id)
        ).execute().value
    }

    func closeDrawEvent(id: UUID) async throws -> DrawEvent {
        _ = try await authenticatedSession()
        return try await client.rpc(
            "close_draw_event",
            params: DrawEventParameters(eventID: id)
        ).execute().value
    }

    func deleteDrawEvent(id: UUID) async throws {
        _ = try await authenticatedSession()
        do {
            _ = try await client.rpc(
                "delete_draw_event",
                params: DrawEventParameters(eventID: id)
            ).execute()
        } catch {
            throw SideyBackendError.normalized(error)
        }
    }

    func currentFirebaseV2BootstrapSession() async throws -> FirebaseV2SupabaseSession {
        let session = try await authenticatedSession()
        return try FirebaseV2SupabaseSessionDecoder.decode(
            accessToken: session.accessToken,
            expectedAccountID: session.user.id
        )
    }

    func registerRealtimeCapabilityV2(
        platform: String,
        appVersion: String,
        protocolVersion: Int,
        contractHash: String
    ) async throws -> RealtimeRolloutPolicyResponse {
        _ = try await authenticatedSession()
        return try await client.rpc(
            "register_realtime_capability_v2",
            params: RealtimeCapabilityRegistrationParameters(
                platform: platform,
                appVersion: appVersion,
                protocolVersion: protocolVersion,
                contractHash: contractHash
            )
        ).execute().value
    }

    func signOut() async throws {
        try await client.auth.signOut(scope: .local)
    }

    func syncRealtime(rooms: [Room], activeRoomID: UUID?) async throws -> BackendReconciliation {
        guard !isShuttingDown else { throw CancellationError() }
        startNetworkPathMonitoringIfNeeded()
        do {
            try await configureChannels(rooms: rooms, activeRoomID: activeRoomID)
            return try await reconcileCurrentState(emitEvents: false)
        } catch {
            // Channel setup can succeed while the follow-up snapshot or message
            // reconciliation fails. In that state the transport watchdog sees
            // healthy subscriptions and will not start recovery on its own, so
            // recoveryReconciled would otherwise remain false indefinitely.
            if !Task.isCancelled {
                scheduleRealtimeRecovery(trigger: .reconciliation, immediate: false)
            }
            throw error
        }
    }

    func setActiveRoom(_ roomID: UUID?) async throws {
        activeRoomID = roomID
        try await publishPresence()
    }

    func setLocalPresence(_ state: PresenceState) async throws {
        localPresence = state == .away ? .away : .online
        try await publishPresence()
    }

    func broadcastTyping(roomID: UUID, event: String) async throws {
        guard ["typing_start", "typing_keepalive", "typing_stop"].contains(event),
              let roomChannels = subscribedChannels(roomID: roomID)
        else { return }
        _ = try await client.rpc(
            "broadcast_room_event",
            params: BroadcastRoomEventParameters(
                roomID: roomID,
                realtimeEpoch: roomChannels.epoch,
                event: event == "typing_keepalive" ? "typing_start" : event,
                eventID: nil
            )
        ).retry(enabled: false).execute()
    }

    func broadcastCharacterPulse(roomID: UUID, eventID: UUID) async throws {
        guard roomID == activeRoomID,
              let roomChannels = subscribedChannels(roomID: roomID)
        else { return }
        _ = try await client.rpc(
            "broadcast_room_event",
            params: BroadcastRoomEventParameters(
                roomID: roomID,
                realtimeEpoch: roomChannels.epoch,
                event: "character_pulse",
                eventID: eventID
            )
        ).execute()
    }

    func broadcastCharacterThrow(roomID: UUID, eventID: UUID, targetUserID: UUID) async throws {
        guard roomID == activeRoomID,
              let roomChannels = subscribedChannels(roomID: roomID)
        else { return }
        _ = try await client.rpc(
            "broadcast_character_throw",
            params: BroadcastCharacterThrowParameters(
                roomID: roomID,
                realtimeEpoch: roomChannels.epoch,
                eventID: eventID,
                targetUserID: targetUserID
            )
        ).execute()
    }

    func shutdown() async {
        isShuttingDown = true
        connectionTracker.replaceDesiredRoomIDs([])
        desiredTopology = RealtimeDesiredTopology()
        realtimeGeneration += 1
        realtimeRecoveryTask?.cancel()
        realtimeRecoveryTask = nil
        realtimeWatchdogTask?.cancel()
        realtimeWatchdogTask = nil
        networkPathTask?.cancel()
        networkPathTask = nil
        networkPathMonitor.cancel()
        structuralSnapshotTask?.cancel()
        structuralSnapshotTask = nil
        await removeAllChannels()
        client.realtimeV2.disconnect(reason: "SIDEY shutdown")
        for task in typingExpiryTasks.values { task.cancel() }
        typingExpiryTasks.removeAll()
        await presencePublicationQueue.cancel()
        for continuation in eventSubscribers.values {
            continuation.finish()
        }
        eventSubscribers.removeAll()
        eventContinuation.finish()
    }

    func setTreeMovementPaused(_ paused: Bool, expectedRevision: Int64, expectedUserID: UUID) async throws -> Profile {
        try Task.checkCancellation()
        guard client.auth.currentUser?.id == expectedUserID else { throw SideyBackendError.sessionRecoveryFailed }
        struct Parameters: Encodable {
            let p_paused: Bool
            let p_expected_revision: Int64
        }
        let rows: [DatabaseProfile] = try await client.rpc(
            "set_tree_movement_paused",
            params: Parameters(p_paused: paused, p_expected_revision: expectedRevision)
        ).execute().value
        guard let profile = rows.first else { throw SideyBackendError.malformedResponse }
        return profile.domain
    }

    func loadSnapshot() async throws -> BackendSnapshot {
        let session = try await client.auth.session
        async let profileRows: [DatabaseProfile] = client.from("profiles")
            .select()
            .eq("id", value: session.user.id.uuidString)
            .execute().value
        async let roomRows: [DatabaseRoom] = client.from("rooms")
            .select()
            .order("created_at", ascending: true)
            .execute().value
        async let membershipRows: [DatabaseMembership] = client.from("room_members")
            .select()
            .order("joined_at", ascending: true)
            .execute().value
        async let visibleProfiles: [DatabaseProfile] = client.from("profiles")
            .select()
            .execute().value
        async let entitlementKeys = loadActiveEntitlementKeysIfAvailable()

        let (profiles, rooms, memberships, peers, remoteEntitlementKeys) = try await (
            profileRows, roomRows, membershipRows, visibleProfiles, entitlementKeys
        )
        let profile = profiles.first
        let profileByID = Dictionary(uniqueKeysWithValues: peers.map { ($0.id, $0) })
        let membershipsByRoom = Dictionary(grouping: memberships, by: \.roomID)
        let mappedRooms = rooms.map { room in
            let members = (membershipsByRoom[room.id] ?? []).map { membership in
                let peer = profileByID[membership.userID]
                return RoomMember(
                    userID: membership.userID,
                    nickname: peer?.nickname ?? L10n.text("profile.nickname.unknown"),
                    characterID: PixelCharacterCatalog.canonicalID(for: peer?.characterID ?? "pixel_hamster"),
                    presence: .offline,
                    equippedBubbleStyleID: peer?.equippedBubbleStyleID,
                    treeMovementPaused: peer?.treeMovementPaused ?? false,
                    treeMovementRevision: peer?.treeMovementRevision
                )
            }
            return Room(
                id: room.id,
                name: room.name,
                ownerID: room.ownerID,
                members: members,
                inviteCodeHint: room.inviteCodeHint,
                inviteVersion: 0,
                inviteCodeReady: room.inviteCodeReady,
                realtimeEpoch: room.realtimeEpoch
            )
        }
        return BackendSnapshot(
            profile: profile?.domain,
            rooms: mappedRooms,
            activeEntitlementKeys: CommerceEntitlementSnapshotPolicy.resolvedKeys(
                remoteKeys: remoteEntitlementKeys,
                profileCharacterID: profile?.characterID
            )
        )
    }

    /// Commerce is an optional feature boundary. A missing or temporarily
    /// unavailable commerce schema must not turn the core messenger snapshot
    /// into a connection failure.
    private func loadActiveEntitlementKeysIfAvailable() async -> Set<String>? {
        do {
            let rows: [DatabaseCommerceEntitlement] = try await client
                .from("commerce_entitlements")
                .select("entitlement_key,status")
                .eq("status", value: "active")
                .execute().value
            return Set(rows.map(\.entitlementKey))
        } catch {
            return nil
        }
    }

    func storeState() async throws -> [CommerceState] {
        let rows: [DatabaseCommerceState] = try await client.rpc(
            "get_store_state"
        ).execute().value
        do {
            return try StoreCatalogResponse.validatedStates(rows)
        } catch let error as StoreCatalogResponse.ValidationError {
            switch error {
            case .duplicateProduct(let id):
                recoveryLogger.error("Store catalog duplicate product: \(id, privacy: .public)")
            case .mismatchedProduct(let id):
                recoveryLogger.error("Store catalog metadata mismatch: \(id, privacy: .public)")
            }
            throw SideyBackendError.malformedResponse
        }
    }

    func commerceState(
        productID: String = CommerceCatalog.starlightUpalupaProductID
    ) async throws -> CommerceState {
        guard let state = try await storeState().first(where: { $0.product.id == productID }) else {
            throw SideyBackendError.malformedResponse
        }
        return state
    }

    func setEquippedCosmetic(
        kind: CommerceProductKind,
        catalogItemID: String?
    ) async throws -> Profile {
        guard kind != .character else { throw SideyBackendError.malformedResponse }
        let value: DatabaseProfile = try await client.rpc(
            "set_equipped_cosmetic",
            params: SetEquippedCosmeticParameters(
                productKind: kind,
                catalogItemID: catalogItemID
            )
        ).execute().value
        return value.domain
    }

    /// Candidate Firebase v2 grant RPC. This is deliberately separate from the
    /// released legacy RPC so the rollout gate can select exactly one mutation
    /// shape without changing old-client behavior.
    func setEquippedCosmeticV2(
        kind: CommerceProductKind,
        catalogItemID: String?
    ) async throws -> FirebaseV2EquippedCosmeticGrant {
        guard kind != .character else { throw SideyBackendError.malformedResponse }
        let rows: [FirebaseV2EquipCosmeticGrantRow] = try await client.rpc(
            "set_equipped_cosmetic_v2",
            params: SetEquippedCosmeticParameters(
                productKind: kind,
                catalogItemID: catalogItemID
            )
        ).execute().value
        guard let row = rows.first else { throw SideyBackendError.malformedResponse }
        return FirebaseV2EquippedCosmeticGrant(
            profile: row.profile.domain,
            accessRevision: row.accessRevision
        )
    }

    func firebaseV2StoreWireItems() async throws -> [FirebaseV2StoreWireItem] {
        try await client.rpc("get_store_state_v2").execute().value
    }

    func currentFirebaseAccessRevision() async throws -> RealtimeRevision {
        let value: String = try await client.rpc(
            "current_firebase_access_revision"
        ).execute().value
        guard let revision = RealtimeRevision(rawValue: value) else {
            throw SideyBackendError.malformedResponse
        }
        return revision
    }

    @discardableResult
    func upsertProfile(nickname: String, characterID: String = "pixel_hamster") async throws -> Profile {
        guard ProfileValidator.isValidNickname(nickname) else { throw SideyBackendError.invalidProfile }
        let normalized = ProfileValidator.normalizedNickname(nickname)
        let value: DatabaseProfile = try await client.rpc(
            "upsert_profile",
            params: UpsertProfileParameters(nickname: normalized, characterID: characterID)
        ).execute().value
        return value.domain
    }

    func createRoom(name: String) async throws -> CreatedRoom {
        guard RoomNameValidator.isValid(name) else { throw SideyBackendError.invalidRoomName }
        let normalized = RoomNameValidator.normalized(name)
        let rows: [CreateRoomRow] = try await client.rpc(
            "create_room",
            params: CreateRoomParameters(name: normalized)
        ).execute().value
        guard let row = rows.first else { throw SideyBackendError.malformedResponse }
        let storedInKeychain: Bool
        do {
            try keychain.writeString(row.inviteCode, account: inviteAccount(roomID: row.roomID))
            storedInKeychain = true
        } catch {
            storedInKeychain = false
        }
        return CreatedRoom(
            roomID: row.roomID,
            inviteCode: row.inviteCode,
            storedInKeychain: storedInKeychain
        )
    }

    func createRoomV2(name: String) async throws -> FirebaseV2CreatedRoomGrant {
        guard RoomNameValidator.isValid(name) else { throw SideyBackendError.invalidRoomName }
        let normalized = RoomNameValidator.normalized(name)
        let rows: [FirebaseV2CreateRoomGrantRow] = try await client.rpc(
            "create_room_v2",
            params: CreateRoomParameters(name: normalized)
        ).execute().value
        guard let row = rows.first else { throw SideyBackendError.malformedResponse }
        let storedInKeychain: Bool
        do {
            try keychain.writeString(row.inviteCode, account: inviteAccount(roomID: row.roomID))
            storedInKeychain = true
        } catch {
            storedInKeychain = false
        }
        return FirebaseV2CreatedRoomGrant(
            room: CreatedRoom(
                roomID: row.roomID,
                inviteCode: row.inviteCode,
                storedInKeychain: storedInKeychain
            ),
            accessRevision: row.accessRevision
        )
    }

    func joinRoom(inviteCode: String) async throws -> JoinedRoom {
        let normalized = inviteCode.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard !normalized.isEmpty else { throw SideyBackendError.invalidInviteCode }
        let rows: [JoinRoomRow] = try await client.rpc(
            "join_room",
            params: JoinRoomParameters(inviteCode: normalized)
        ).execute().value
        guard let row = rows.first else { throw SideyBackendError.malformedResponse }
        if let code = row.errorCode, !code.isEmpty { throw SideyBackendError.business(code: code) }
        guard let roomID = row.roomID else { throw SideyBackendError.malformedResponse }
        // The database intentionally stores only a hash. Preserve the plaintext
        // code the user already supplied so this device can offer a copy action.
        let storedInKeychain: Bool
        do {
            try keychain.writeString(normalized, account: inviteAccount(roomID: roomID))
            storedInKeychain = true
        } catch {
            storedInKeychain = false
        }
        return JoinedRoom(roomID: roomID, storedInKeychain: storedInKeychain)
    }

    func joinRoomV2(inviteCode: String) async throws -> FirebaseV2JoinedRoomGrant {
        let normalized = inviteCode.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard !normalized.isEmpty else { throw SideyBackendError.invalidInviteCode }
        let rows: [FirebaseV2JoinRoomGrantRow] = try await client.rpc(
            "join_room_v2",
            params: JoinRoomParameters(inviteCode: normalized)
        ).execute().value
        guard let row = rows.first else { throw SideyBackendError.malformedResponse }
        if let code = row.errorCode, !code.isEmpty { throw SideyBackendError.business(code: code) }
        let grant: FirebaseV2JoinRoomGrant
        do {
            grant = try row.validatedGrant()
        } catch {
            throw SideyBackendError.malformedResponse
        }
        let storedInKeychain: Bool
        do {
            try keychain.writeString(normalized, account: inviteAccount(roomID: grant.roomID))
            storedInKeychain = true
        } catch {
            storedInKeychain = false
        }
        return FirebaseV2JoinedRoomGrant(
            room: JoinedRoom(roomID: grant.roomID, storedInKeychain: storedInKeychain),
            accessRevision: grant.accessRevision
        )
    }

    func rotateInviteCode(roomID: UUID) async throws -> CreatedRoom {
        let code: String = try await client.rpc(
            "rotate_invite_code",
            params: RotateInviteCodeParameters(roomID: roomID)
        ).execute().value
        let storedInKeychain: Bool
        do {
            try keychain.writeString(code, account: inviteAccount(roomID: roomID))
            storedInKeychain = true
        } catch {
            storedInKeychain = false
        }
        return CreatedRoom(roomID: roomID, inviteCode: code, storedInKeychain: storedInKeychain)
    }

    func leaveRoom(_ roomID: UUID) async throws {
        let _: UUID? = try await client.rpc(
            "leave_room",
            params: LeaveRoomParameters(roomID: roomID)
        ).execute().value
        try? keychain.delete(account: inviteAccount(roomID: roomID))
        if activeRoomID == roomID { activeRoomID = nil }
        await removeChannel(roomID)
    }

    func renameRoom(_ roomID: UUID, name: String) async throws {
        guard RoomNameValidator.isValid(name) else { throw SideyBackendError.invalidRoomName }
        do {
            _ = try await client.rpc(
                "rename_room",
                params: RenameRoomParameters(
                    roomID: roomID,
                    name: RoomNameValidator.normalized(name)
                )
            ).execute()
        } catch {
            throw SideyBackendError.normalized(error)
        }
    }

    func removeRoomMember(_ roomID: UUID, userID: UUID) async throws {
        do {
            _ = try await client.rpc(
                "remove_room_member",
                params: RemoveRoomMemberParameters(roomID: roomID, userID: userID)
            ).execute()
        } catch {
            throw SideyBackendError.normalized(error)
        }
    }

    func deleteRoom(_ roomID: UUID) async throws {
        do {
            _ = try await client.rpc(
                "delete_room",
                params: DeleteRoomParameters(roomID: roomID)
            ).execute()
        } catch {
            throw SideyBackendError.normalized(error)
        }
        try? keychain.delete(account: inviteAccount(roomID: roomID))
        if activeRoomID == roomID { activeRoomID = nil }
        await removeChannel(roomID)
    }

    func sendMessage(roomID: UUID, body: String, id: UUID = UUID()) async throws -> ChatMessage {
        let normalized = MessageValidator.normalized(body)
        guard MessageValidator.isValid(normalized) else { throw SideyBackendError.invalidMessage }
        let parameters = SendMessageParameters(id: id, roomID: roomID, body: normalized)
        do {
            let value: DatabaseMessage = try await client.rpc(
                "send_message",
                params: parameters
            ).execute().value
            return try value.domain
        } catch {
            // An HTTP failure can happen after Postgres committed. Resolve the
            // client UUID through RLS first, then retry exactly once with the
            // same UUID. Never mint a replacement ID for an ambiguous result.
            if let committed = try? await message(id: id, roomID: roomID),
               committed.body == normalized,
               committed.senderID == client.auth.currentUser?.id {
                return committed
            }
            do {
                let value: DatabaseMessage = try await client.rpc(
                    "send_message",
                    params: parameters
                ).execute().value
                return try value.domain
            } catch {
                if let committed = try? await message(id: id, roomID: roomID),
                   committed.body == normalized,
                   committed.senderID == client.auth.currentUser?.id {
                    return committed
                }
                throw SideyBackendError.normalized(error)
            }
        }
    }

    func message(id: UUID, roomID: UUID) async throws -> ChatMessage? {
        let rows: [DatabaseMessage] = try await client.from("messages")
            .select()
            .eq("id", value: id.uuidString)
            .eq("room_id", value: roomID.uuidString)
            .limit(1)
            .execute().value
        return try rows.first.map { try $0.domain }
    }

    func recentMessages(roomID: UUID, limit: Int = 50) async throws -> [ChatMessage] {
        let rows: [DatabaseMessage] = try await client.from("messages")
            .select()
            .eq("room_id", value: roomID.uuidString)
            .order("created_at", ascending: false)
            .limit(min(max(limit, 1), 50))
            .execute().value
        return try rows.reversed().map { try $0.domain }
    }

    func historyPage(
        roomID: UUID,
        before cursor: MessageHistoryCursor?,
        pageSize: Int
    ) async throws -> MessageHistoryPage {
        let boundedPageSize = min(max(pageSize, 1), 50)
        let cutoff = PostgresTimestampEncoder.encode(
            Date().addingTimeInterval(-MessageLedger.retentionInterval)
        )
        let query = client.from("messages")
            .select()
            .eq("room_id", value: roomID.uuidString)
            .gte("created_at", value: cutoff)

        if let cursor {
            _ = try PostgresTimestampDecoder.decode(cursor.rawCreatedAt)
            _ = query.or(
                "created_at.lt.\(cursor.rawCreatedAt),and(created_at.eq.\(cursor.rawCreatedAt),id.lt.\(cursor.id.uuidString))"
            )
        }

        let rows: [DatabaseMessage] = try await query
            .order("created_at", ascending: false)
            .order("id", ascending: false)
            .limit(boundedPageSize + 1)
            .execute().value
        return try MessageHistoryPageMapper.page(from: rows, pageSize: boundedPageSize)
    }

    func currentUserID() -> UUID? {
        client.auth.currentUser?.id
    }

    func storedInviteCode(roomID: UUID) throws -> String? {
        try keychain.readString(account: inviteAccount(roomID: roomID))
    }

#if DEBUG
    func interruptRealtimeConnectionForTesting() {
        client.realtimeV2.disconnect(reason: "SIDEY integration recovery test")
    }

    func simulateNetworkAvailabilityForTesting(_ availability: NetworkAvailability) async {
        await handleNetworkAvailability(availability)
    }

    func deleteOwnAccountForTesting() async throws {
        _ = try await client.rpc("delete_own_account").execute()
    }
#endif

    private func restoreOrCreateSession(requireExistingSession: Bool) async throws -> Session {
        do {
            return try await authenticatedSession()
        } catch let error as SideyBackendError where error == .sessionRecoveryFailed {
            if requireExistingSession {
                throw error
            }
            return try await client.auth.signInAnonymously()
        }
    }

    private func authenticatedSession() async throws -> Session {
        let client = self.client
        let keychain = self.keychain
        let legacyRefreshAccount = self.legacyRefreshAccount
        let restorer = AuthSessionRestorer(
            loadCurrentSession: { try await client.auth.session },
            isSessionMissing: { error in
                guard let authError = error as? AuthError else { return false }
                if case .sessionMissing = authError { return true }
                return false
            },
            loadFreshRefreshToken: {
                try keychain.readFreshString(account: legacyRefreshAccount)
            },
            refreshSession: { token in
                try await client.auth.refreshSession(refreshToken: token)
            }
        )
        do {
            return try await restorer.session()
        } catch let authError as AuthError {
            if case .sessionMissing = authError {
                throw SideyBackendError.sessionRecoveryFailed
            }
            throw authError
        }
    }

    private func configureChannels(rooms: [Room], activeRoomID: UUID?) async throws {
        let requestedRooms = Array(rooms.prefix(5))
        let requestedTopology = RealtimeTopology(rooms: requestedRooms)
        let previousDesiredTopology = RealtimeTopology(channelEpochs: desiredTopology.roomEpochs)
        let liveTopology = RealtimeTopology(channelEpochs: Dictionary(
            uniqueKeysWithValues: channels.map { ($0.key, $0.value.epoch) }
        ))
        let updatePlan = RealtimeTopologyUpdatePlan.make(
            live: liveTopology,
            requestedRooms: requestedRooms
        )
        desiredTopology.replace(rooms: requestedRooms)
        let desiredRoomIDs = desiredTopology.roomIDs
        let departedRoomIDs = Set(previousDesiredTopology.roomEpochs.keys).subtracting(desiredRoomIDs)
        for roomID in departedRoomIDs {
            try? keychain.delete(account: inviteAccount(roomID: roomID))
        }
        let resolvedActiveRoomID = activeRoomID.flatMap {
            desiredRoomIDs.contains($0) ? $0 : nil
        }
        let topologyChanged = requestedTopology != previousDesiredTopology
            || requestedTopology != liveTopology
        let activeRoomChanged = self.activeRoomID != resolvedActiveRoomID

        self.activeRoomID = resolvedActiveRoomID
        connectionTracker.replaceDesiredRoomIDs(desiredRoomIDs)
        if topologyChanged {
            recoveryReconciled = false
        }
        emitConnectionState()

        guard networkAvailability.current != .unavailable else {
            markAllRoomsUnsubscribed()
            emitConnectionState()
            throw SideyBackendError.realtimeUnavailable
        }

        if topologyChanged {
            realtimeRecoveryTask?.cancel()
            realtimeRecoveryTask = nil
            realtimeRecoveryAttempt = 0
            let generation = realtimeGeneration
            emitConnectionState()
            do {
                try await applyChannelUpdate(
                    updatePlan,
                    rooms: requestedRooms,
                    generation: generation
                )
            } catch {
                if !Task.isCancelled {
                    scheduleRealtimeRecovery(trigger: .channel, immediate: false)
                }
                throw error
            }
        }
        if topologyChanged || activeRoomChanged {
            try await publishPresence()
        }
        startRealtimeWatchdogIfNeeded()
    }

    private func applyChannelUpdate(
        _ plan: RealtimeTopologyUpdatePlan,
        rooms: [Room],
        generation: Int
    ) async throws {
        rebuildingGeneration = generation
        defer {
            if rebuildingGeneration == generation {
                rebuildingGeneration = nil
            }
        }
        for roomID in plan.removals {
            await removeChannel(roomID)
        }
        try ensureCurrentRealtimeGeneration(generation)
        for room in rooms where plan.additions.contains(room.id) {
            try await addChannel(
                roomID: room.id,
                epoch: room.realtimeEpoch,
                generation: generation
            )
            try ensureCurrentRealtimeGeneration(generation)
        }
        guard connectionTracker.isConnected else {
            throw SideyBackendError.realtimeUnavailable
        }
    }

    private func addChannel(roomID: UUID, epoch: Int, generation: Int) async throws {
        guard let userID = client.auth.currentUser?.id else {
            throw SideyBackendError.authenticationRequired
        }
        try ensureCurrentRealtimeGeneration(generation)

        let topicPrefix = "room:\(roomID.uuidString.lowercased()):\(epoch)"
        let databaseChannel = client.channel("\(topicPrefix):db") { config in
            config.isPrivate = true
            config.broadcast.receiveOwnBroadcasts = false
        }
        let ephemeralChannel = client.channel("\(topicPrefix):ephemeral") { config in
            config.isPrivate = true
            config.broadcast.receiveOwnBroadcasts = false
            config.presence = PresenceJoinConfig(key: userID.uuidString.lowercased())
        }

        let messageChanges = databaseChannel.broadcastStream(event: "message_changed")
        let structureChanges = databaseChannel.broadcastStream(event: "structure_changed")
        let messagesPruned = databaseChannel.broadcastStream(event: "messages_pruned")
        let drawChanges = databaseChannel.broadcastStream(event: "draw_events_changed")
        let databaseStatuses = databaseChannel.statusChange
        let typingStart = ephemeralChannel.broadcastStream(event: "typing_start")
        let typingStop = ephemeralChannel.broadcastStream(event: "typing_stop")
        let characterPulse = ephemeralChannel.broadcastStream(event: "character_pulse")
        let characterThrow = ephemeralChannel.broadcastStream(event: "character_throw")
        let presenceChanges = ephemeralChannel.presenceChange()
        let ephemeralStatuses = ephemeralChannel.statusChange

        let tasks = [
            Task { [weak self] in
                for await payload in drawChanges {
                    await self?.handleDrawBroadcast(
                        roomID: roomID,
                        generation: generation,
                        payload: payload
                    )
                }
            },
            Task { [weak self] in
                for await payload in messageChanges {
                    await self?.handleDatabaseBroadcast(
                        roomID: roomID,
                        generation: generation,
                        payload: payload,
                        event: "message_changed"
                    )
                }
            },
            Task { [weak self] in
                for await payload in structureChanges {
                    await self?.handleDatabaseBroadcast(
                        roomID: roomID,
                        generation: generation,
                        payload: payload,
                        event: "structure_changed"
                    )
                }
            },
            Task { [weak self] in
                for await payload in messagesPruned {
                    await self?.handleDatabaseBroadcast(
                        roomID: roomID,
                        generation: generation,
                        payload: payload,
                        event: "messages_pruned"
                    )
                }
            },
            Task { [weak self] in
                for await payload in typingStart {
                    await self?.handleTyping(
                        roomID: roomID,
                        generation: generation,
                        payload: payload,
                        active: true
                    )
                }
            },
            Task { [weak self] in
                for await payload in typingStop {
                    await self?.handleTyping(
                        roomID: roomID,
                        generation: generation,
                        payload: payload,
                        active: false
                    )
                }
            },
            Task { [weak self] in
                for await payload in characterPulse {
                    await self?.handleCharacterPulse(
                        roomID: roomID,
                        generation: generation,
                        payload: payload
                    )
                }
            },
            Task { [weak self] in
                for await payload in characterThrow {
                    await self?.handleCharacterThrow(
                        roomID: roomID,
                        generation: generation,
                        payload: payload
                    )
                }
            },
            Task { [weak self] in
                for await action in presenceChanges {
                    await self?.handlePresence(
                        roomID: roomID,
                        generation: generation,
                        action: action
                    )
                }
            },
            Task { [weak self] in
                for await status in databaseStatuses {
                    await self?.handleChannelStatus(
                        roomID: roomID,
                        generation: generation,
                        status: status,
                        isPresenceChannel: false
                    )
                }
            },
            Task { [weak self] in
                for await status in ephemeralStatuses {
                    await self?.handleChannelStatus(
                        roomID: roomID,
                        generation: generation,
                        status: status,
                        isPresenceChannel: true
                    )
                }
            }
        ]
        channels[roomID] = RoomRealtimeChannels(
            epoch: epoch,
            generation: generation,
            database: databaseChannel,
            ephemeral: ephemeralChannel,
            tasks: tasks
        )

        do {
            try await databaseChannel.subscribeWithError()
            try Task.checkCancellation()
            try ensureCurrentRealtimeGeneration(generation)
            try await ephemeralChannel.subscribeWithError()
            try Task.checkCancellation()
            try ensureCurrentRealtimeGeneration(generation)
            updateRoomSubscription(roomID: roomID)
        } catch {
            await removeChannel(roomID, expectedGeneration: generation)
            throw error
        }
    }

    private func removeChannel(_ roomID: UUID, expectedGeneration: Int? = nil) async {
        if let expectedGeneration,
           channels[roomID]?.generation != expectedGeneration {
            return
        }
        connectionTracker.setSubscribed(false, roomID: roomID)
        presenceReplay.invalidate(roomID: roomID)
        guard let roomChannels = channels.removeValue(forKey: roomID) else { return }
        roomChannels.tasks.forEach { $0.cancel() }
        await client.removeChannel(roomChannels.database)
        await client.removeChannel(roomChannels.ephemeral)
    }

    private func removeAllChannels() async {
        let channelGenerations = channels.map { ($0.key, $0.value.generation) }
        for (roomID, generation) in channelGenerations {
            await removeChannel(roomID, expectedGeneration: generation)
        }
    }

    private func handleChannelStatus(
        roomID: UUID,
        generation: Int,
        status: RealtimeChannelStatus,
        isPresenceChannel: Bool
    ) async {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        // The database notification channel can resubscribe independently of
        // the live Presence channel. Only the latter owns this snapshot.
        if isPresenceChannel, status != .subscribed {
            presenceReplay.invalidate(roomID: roomID)
        }
        switch status {
        case .subscribed:
            updateRoomSubscription(roomID: roomID)
            emitConnectionState()
        case .unsubscribed:
            connectionTracker.setSubscribed(false, roomID: roomID)
            recoveryReconciled = false
            emitConnectionState()
            if rebuildingGeneration == nil {
                scheduleRealtimeRecovery(trigger: .channel, immediate: false)
            }
        case .subscribing, .unsubscribing:
            connectionTracker.setSubscribed(false, roomID: roomID)
            recoveryReconciled = false
            emitConnectionState()
        }
    }

    private func updateRoomSubscription(roomID: UUID) {
        guard let roomChannels = channels[roomID],
              roomChannels.generation == realtimeGeneration
        else {
            connectionTracker.setSubscribed(false, roomID: roomID)
            return
        }
        connectionTracker.setSubscribed(
            RealtimeChannelPairPolicy.isSubscribed(
                database: roomChannels.database.status == .subscribed,
                ephemeral: roomChannels.ephemeral.status == .subscribed
            ),
            roomID: roomID
        )
    }

    private func publishPresence() async throws {
        try await presencePublicationQueue.submit(PresencePublicationIntent(
            activeRoomID: activeRoomID,
            localPresence: localPresence
        ))
    }

    private func performPresencePublication(_ intent: PresencePublicationIntent) async throws {
        let desiredRoomIDs = connectionTracker.desiredRoomIDs
        guard !desiredRoomIDs.isEmpty else { return }
        guard let userID = client.auth.currentUser?.id else {
            throw SideyBackendError.realtimeUnavailable
        }
        guard client.realtimeV2.status == .connected else {
            throw SideyBackendError.realtimeUnavailable
        }
        let generation = realtimeGeneration
        let publicationChannels = channels

        // A publication is a complete desired-room batch. Missing or
        // unsubscribed channels are errors instead of silently producing a
        // partial Presence state that can make two rooms look active.
        for roomID in desiredRoomIDs.sorted(by: {
            $0.uuidString < $1.uuidString
        }) {
            guard let roomChannels = channels[roomID],
                  roomChannels.generation == realtimeGeneration,
                  roomChannels.database.status == .subscribed,
                  roomChannels.ephemeral.status == .subscribed
            else {
                throw SideyBackendError.realtimeUnavailable
            }
            let state = PresencePublicationPlan.state(
                for: roomID,
                activeRoomID: intent.activeRoomID,
                localPresence: intent.localPresence
            )
            try await roomChannels.ephemeral.track(PresencePayload(
                userID: userID,
                state: state,
                onlineAt: ISO8601DateFormatter().string(from: .now)
            ))
            // track does not expose the server acknowledgement. At least reject
            // a batch whose connection or channels changed while it suspended,
            // including replacements that reuse the same realtime generation.
            guard !isShuttingDown,
                  !Task.isCancelled,
                  generation == realtimeGeneration,
                  client.auth.currentUser?.id == userID,
                  client.realtimeV2.status == .connected,
                  connectionTracker.desiredRoomIDs == desiredRoomIDs,
                  desiredRoomIDs.allSatisfy({ roomID in
                      guard let original = publicationChannels[roomID],
                            let current = channels[roomID]
                      else { return false }
                      return current.generation == generation
                          && current.database === original.database
                          && current.ephemeral === original.ephemeral
                          && current.database.status == .subscribed
                          && current.ephemeral.status == .subscribed
                          && desiredTopology.epoch(for: roomID) == current.epoch
                  })
            else {
                throw SideyBackendError.realtimeUnavailable
            }
        }
    }

    private func subscribedChannels(roomID: UUID) -> RoomRealtimeChannels? {
        guard client.realtimeV2.status == .connected,
              let roomChannels = channels[roomID],
              roomChannels.generation == realtimeGeneration,
              roomChannels.database.status == .subscribed,
              roomChannels.ephemeral.status == .subscribed
        else {
            scheduleRealtimeRecovery(trigger: .channel, immediate: false)
            return nil
        }
        return roomChannels
    }

    private func startRealtimeWatchdogIfNeeded() {
        guard realtimeWatchdogTask == nil else { return }
        realtimeWatchdogTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .seconds(RealtimeRecoveryPolicy.watchdogInterval))
                } catch {
                    return
                }
                guard !Task.isCancelled else { return }
                await self?.inspectRealtimeHealth()
            }
        }
    }

    private func inspectRealtimeHealth() {
        guard !isShuttingDown,
              networkAvailability.current != .unavailable,
              rebuildingGeneration == nil
        else { return }
        let socketConnected = client.realtimeV2.status == .connected
        for roomID in connectionTracker.desiredRoomIDs {
            let subscribed = socketConnected
                && channels[roomID]?.generation == realtimeGeneration
                && channels[roomID]?.database.status == .subscribed
                && channels[roomID]?.ephemeral.status == .subscribed
            connectionTracker.setSubscribed(subscribed, roomID: roomID)
            if !subscribed {
                recoveryReconciled = false
                scheduleRealtimeRecovery(trigger: .watchdog, immediate: false)
            }
        }
        emitConnectionState()
    }

    private func scheduleRealtimeRecovery(
        trigger: RealtimeRecoveryTrigger,
        immediate: Bool
    ) {
        guard !isShuttingDown,
              networkAvailability.current != .unavailable,
              realtimeRecoveryTask == nil
        else { return }

        recoveryReconciled = false
        markAllRoomsUnsubscribed()
        cancelTypingExpiryTasks()
        realtimeRecoveryAttempt += 1
        realtimeGeneration += 1
        let attempt = realtimeRecoveryAttempt
        let generation = realtimeGeneration
        let delay = immediate
            ? RealtimeRecoveryPolicy.pathRecoveryDebounce
            : RealtimeRecoveryPolicy.delay(forAttempt: attempt)
        recoveryLogger.notice(
            "scheduled generation=\(generation, privacy: .public) attempt=\(attempt, privacy: .public) trigger=\(trigger.rawValue, privacy: .public)"
        )
        emitConnectionState()
        realtimeRecoveryTask = Task { [weak self] in
            do {
                try await Task.sleep(for: .seconds(delay))
            } catch {
                return
            }
            guard !Task.isCancelled else { return }
            await self?.recoverRealtimeTopology(generation: generation, attempt: attempt)
        }
    }

    private func recoverRealtimeTopology(generation: Int, attempt: Int) async {
        guard !isShuttingDown,
              generation == realtimeGeneration,
              networkAvailability.current != .unavailable
        else { return }

        rebuildingGeneration = generation
        recoveryLogger.notice(
            "started generation=\(generation, privacy: .public) attempt=\(attempt, privacy: .public)"
        )
        do {
            await presencePublicationQueue.cancel()
            await removeAllChannels()
            client.realtimeV2.disconnect(reason: "SIDEY topology recovery")
            try await Task.sleep(for: .milliseconds(150))
            try ensureCurrentRealtimeGeneration(generation)

            let snapshot = try await loadSnapshot()
            try ensureCurrentRealtimeGeneration(generation)
            let rooms = Array(snapshot.rooms.prefix(5))
            desiredTopology.replace(rooms: rooms)
            connectionTracker.replaceDesiredRoomIDs(desiredTopology.roomIDs)
            activeRoomID = activeRoomID.flatMap { requestedID in
                desiredTopology.roomIDs.contains(requestedID) ? requestedID : nil
            } ?? rooms.first?.id

            await client.realtimeV2.connect()
            try ensureCurrentRealtimeGeneration(generation)
            guard client.realtimeV2.status == .connected else {
                throw SideyBackendError.realtimeUnavailable
            }

            for room in rooms {
                try await addChannel(
                    roomID: room.id,
                    epoch: room.realtimeEpoch,
                    generation: generation
                )
                try ensureCurrentRealtimeGeneration(generation)
            }
            guard connectionTracker.isConnected else {
                throw SideyBackendError.realtimeUnavailable
            }
            try await publishPresence()
            try ensureCurrentRealtimeGeneration(generation)
            let reconciliation = try await makeReconciliation(snapshot: snapshot)
            try ensureCurrentRealtimeGeneration(generation)

            rebuildingGeneration = nil
            realtimeRecoveryTask = nil
            realtimeRecoveryAttempt = 0
            recoveryReconciled = true
            replayPresence(rooms: snapshot.rooms)
            emit(.reconciliation(reconciliation))
            emitConnectionState()
            recoveryLogger.notice(
                "completed generation=\(generation, privacy: .public) rooms=\(rooms.count, privacy: .public)"
            )
        } catch {
            guard generation == realtimeGeneration else { return }
            rebuildingGeneration = nil
            realtimeRecoveryTask = nil
            recoveryReconciled = false
            markAllRoomsUnsubscribed()
            await removeAllChannels()
            emitConnectionState()
            recoveryLogger.error(
                "failed generation=\(generation, privacy: .public) attempt=\(attempt, privacy: .public)"
            )
            scheduleRealtimeRecovery(trigger: .retry, immediate: false)
        }
    }

    private func startNetworkPathMonitoringIfNeeded() {
        guard networkPathTask == nil else { return }
        let updates = networkPathMonitor.updates
        networkPathTask = Task { [weak self] in
            for await availability in updates {
                guard !Task.isCancelled else { return }
                await self?.handleNetworkAvailability(availability)
            }
        }
        networkPathMonitor.start()
    }

    private func handleNetworkAvailability(_ availability: NetworkAvailability) async {
        guard !isShuttingDown else { return }
        switch networkAvailability.update(availability) {
        case .unchanged, .initialAvailable:
            return
        case .becameUnavailable:
            realtimeGeneration += 1
            realtimeRecoveryTask?.cancel()
            realtimeRecoveryTask = nil
            realtimeRecoveryAttempt = 0
            rebuildingGeneration = nil
            recoveryReconciled = false
            structuralSnapshotTask?.cancel()
            structuralSnapshotTask = nil
            markAllRoomsUnsubscribed()
            cancelTypingExpiryTasks()
            emitConnectionState()
            await presencePublicationQueue.cancel()
            await removeAllChannels()
            client.realtimeV2.disconnect(reason: "SIDEY network unavailable")
            recoveryLogger.notice("network unavailable")
        case .becameAvailable:
            recoveryLogger.notice("network available")
            scheduleRealtimeRecovery(trigger: .networkPath, immediate: true)
        }
    }

    private func ensureCurrentRealtimeGeneration(_ generation: Int) throws {
        guard !isShuttingDown,
              generation == realtimeGeneration,
              networkAvailability.current != .unavailable
        else { throw CancellationError() }
    }

    private func isCurrentChannel(roomID: UUID, generation: Int) -> Bool {
        guard channels[roomID]?.generation == generation else { return false }
        return RealtimeChannelGenerationPolicy.accepts(
            candidateGeneration: generation,
            currentGeneration: realtimeGeneration,
            desiredEpoch: desiredTopology.epoch(for: roomID),
            channelEpoch: channels[roomID]?.epoch
        )
    }

    private func markAllRoomsUnsubscribed() {
        presenceReplay.invalidateAll()
        for roomID in connectionTracker.desiredRoomIDs {
            connectionTracker.setSubscribed(false, roomID: roomID)
        }
    }

    private func cancelTypingExpiryTasks() {
        for task in typingExpiryTasks.values { task.cancel() }
        typingExpiryTasks.removeAll()
    }

    private func reconcileCurrentState(emitEvents: Bool) async throws -> BackendReconciliation {
        var snapshot = try await loadSnapshot()
        let snapshotEpochs = Dictionary(uniqueKeysWithValues: snapshot.rooms.prefix(5).map {
            ($0.id, $0.realtimeEpoch)
        })
        let channelEpochs = Dictionary(uniqueKeysWithValues: channels.map { ($0.key, $0.value.epoch) })
        if snapshotEpochs != channelEpochs {
            try await configureChannels(rooms: snapshot.rooms, activeRoomID: activeRoomID)
            snapshot = try await loadSnapshot()
        }
        guard connectionTracker.isConnected else {
            throw SideyBackendError.realtimeUnavailable
        }
        let reconciliation = try await makeReconciliation(snapshot: snapshot)
        // Replay a complete membership baseline, including offline members,
        // after a receiver was replaced or a dropped event forced reconciliation.
        replayPresence(rooms: snapshot.rooms)
        if emitEvents {
            emit(.reconciliation(reconciliation))
        }
        recoveryReconciled = true
        emitConnectionState()
        return reconciliation
    }

    private func makeReconciliation(snapshot: BackendSnapshot) async throws -> BackendReconciliation {
        let reconciledActiveRoomID = activeRoomID.flatMap { requestedID in
            snapshot.rooms.contains(where: { $0.id == requestedID }) ? requestedID : snapshot.rooms.first?.id
        } ?? snapshot.rooms.first?.id
        let messages: [ChatMessage]
        if let reconciledActiveRoomID {
            messages = try await recentMessages(roomID: reconciledActiveRoomID)
        } else {
            messages = []
        }
        return BackendReconciliation(
            snapshot: snapshot,
            activeRoomID: reconciledActiveRoomID,
            activeMessages: messages
        )
    }

    private func currentConnectionStatus() -> BackendConnectionStatus {
        let pathAvailable = networkAvailability.current != .unavailable
        let socketAvailable = connectionTracker.desiredRoomIDs.isEmpty
            || client.realtimeV2.status == .connected
        return RealtimeConnectionStatusPolicy.resolve(
            pathAvailable: pathAvailable,
            socketAvailable: socketAvailable,
            recoveryTaskRunning: realtimeRecoveryTask != nil,
            rebuildingChannels: rebuildingGeneration != nil,
            allRoomsSubscribed: connectionTracker.isConnected,
            recoveryReconciled: recoveryReconciled,
            hasActiveRoom: activeRoomID != nil,
            activeRoomSubscribed: activeRoomID.map {
                connectionTracker.isSubscribed(roomID: $0)
            } ?? false
        )
    }

    private func emitConnectionState() {
        let status = currentConnectionStatus()
        guard lastEmittedConnectionStatus != status else { return }
        lastEmittedConnectionStatus = status
        emit(.connection(status))
    }

    private func livePresenceRoomIDs() -> Set<UUID> {
        guard networkAvailability.current != .unavailable,
              client.realtimeV2.status == .connected else { return [] }
        return Set(channels.compactMap { roomID, roomChannels in
            guard isCurrentChannel(roomID: roomID, generation: roomChannels.generation),
                  roomChannels.ephemeral.status == .subscribed else { return nil }
            return roomID
        })
    }

    private func replayPresence(rooms: [Room]) {
        for event in presenceReplay.snapshot(
            rooms: rooms,
            subscribedRoomIDs: livePresenceRoomIDs()
        ) {
            emit(event)
        }
    }

    private func handleDatabaseBroadcast(
        roomID: UUID,
        generation: Int,
        payload: JSONObject,
        event: String
    ) async {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        let inner = payload["payload"]?.objectValue ?? payload
        guard let change = try? inner.decode(as: DatabaseChangePayload.self),
              change.roomID == roomID
        else { return }

        switch event {
        case "message_changed":
            guard let operation = change.operation,
                  ["INSERT", "UPDATE", "DELETE"].contains(operation),
                  let messageID = change.messageID
            else { return }
            if operation == "DELETE" {
                emit(.messageDeleted(roomID: roomID, messageID: messageID))
                return
            }
            do {
                guard let verified = try await message(id: messageID, roomID: roomID) else {
                    throw SideyBackendError.malformedResponse
                }
                guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
                emit(.message(verified))
            } catch {
                guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
                recoveryLogger.error(
                    "Message receive failed: \(String(reflecting: error), privacy: .private)"
                )
                emit(.technicalError(L10n.text("backend.error.message_receive_failed")))
            }
        case "structure_changed":
            guard let entity = change.entity,
                  ["profiles", "rooms", "room_members"].contains(entity),
                  let operation = change.operation,
                  ["INSERT", "UPDATE", "DELETE"].contains(operation)
            else { return }
            scheduleStructuralSnapshot()
        case "messages_pruned":
            emit(.messagesInvalidated(roomID: roomID))
        default:
            return
        }
    }

    private func handleDrawBroadcast(roomID: UUID, generation: Int, payload: JSONObject) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        let inner = payload["payload"]?.objectValue ?? payload
        guard let change = try? inner.decode(as: DrawChangePayload.self),
              change.roomID == roomID,
              ["created", "closed"].contains(change.operation)
        else { return }
        emit(.drawEventsChanged(roomID: roomID))
    }

    private func scheduleStructuralSnapshot() {
        guard structuralSnapshotTask == nil,
              !isShuttingDown,
              networkAvailability.current != .unavailable
        else { return }
        let attempt = structuralSnapshotAttempt
        let delay: Duration = attempt == 0
            ? .milliseconds(150)
            : .seconds(RealtimeRecoveryPolicy.delay(forAttempt: attempt))
        structuralSnapshotTask = Task { [weak self] in
            do {
                try await Task.sleep(for: delay)
            } catch {
                return
            }
            guard !Task.isCancelled else { return }
            await self?.emitStructuralSnapshot()
        }
    }

    private func emitStructuralSnapshot() async {
        structuralSnapshotTask = nil
        guard !isShuttingDown, networkAvailability.current != .unavailable else { return }
        do {
            let snapshot = try await loadSnapshot()
            let snapshotTopology = RealtimeTopology(rooms: snapshot.rooms)
            let channelTopology = RealtimeTopology(channelEpochs: Dictionary(
                uniqueKeysWithValues: channels.map { ($0.key, $0.value.epoch) }
            ))
            structuralSnapshotAttempt = 0
            if snapshotTopology == channelTopology {
                emit(.snapshot(snapshot))
            } else {
                try await configureChannels(
                    rooms: snapshot.rooms,
                    activeRoomID: activeRoomID
                )
                _ = try await reconcileCurrentState(emitEvents: true)
            }
        } catch {
            recoveryLogger.error(
                "Room state resynchronization failed: \(String(reflecting: error), privacy: .private)"
            )
            emit(.technicalError(L10n.text("backend.error.room_resync_failed")))
            structuralSnapshotAttempt += 1
            scheduleStructuralSnapshot()
            if !recoveryReconciled {
                scheduleRealtimeRecovery(trigger: .reconciliation, immediate: false)
            }
        }
    }

    private func handlePresence(
        roomID: UUID,
        generation: Int,
        action: any PresenceAction
    ) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        var joined: [UUID: PresenceState] = [:]
        for (userIDString, presence) in action.joins {
            guard let userID = UUID(uuidString: userIDString),
                  let payload = try? presence.decodeState(as: PresencePayload.self)
            else { continue }
            joined[userID] = payload.state
        }
        let left = Set(action.leaves.keys.compactMap(UUID.init(uuidString:)))
        for update in PresenceChangePlan.updates(joined: joined, left: left) {
            presenceReplay.record(roomID: roomID, userID: update.userID, state: update.state)
            emit(.presence(
                roomID: roomID,
                userID: update.userID,
                state: update.state
            ))
        }
    }

    private func handleTyping(
        roomID: UUID,
        generation: Int,
        payload: JSONObject,
        active: Bool
    ) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        let inner = payload["payload"]?.objectValue ?? payload
        guard let typing = try? inner.decode(as: TypingPayload.self),
              typing.roomID == roomID,
              typing.userID != client.auth.currentUser?.id
        else { return }
        let key = "\(roomID.uuidString)|\(typing.userID.uuidString)"
        typingExpiryTasks.removeValue(forKey: key)?.cancel()
        guard !active || typingExpiryTasks.count < 60 else {
            scheduleStructuralSnapshot()
            return
        }
        emit(.typing(roomID: roomID, userID: typing.userID, active: active))
        guard active else { return }
        typingExpiryTasks[key] = Task { [weak self] in
            try? await Task.sleep(for: .seconds(6))
            guard !Task.isCancelled else { return }
            await self?.expireTyping(
                roomID: roomID,
                userID: typing.userID,
                key: key,
                generation: generation
            )
        }
    }

    private func handleCharacterPulse(
        roomID: UUID,
        generation: Int,
        payload: JSONObject
    ) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        let inner = payload["payload"]?.objectValue ?? payload
        guard let pulse = try? inner.decode(as: CharacterPulsePayload.self),
              pulse.roomID == roomID,
              pulse.userID != client.auth.currentUser?.id
        else { return }
        emit(.characterPulse(CharacterPulseEvent(
            id: pulse.eventID,
            roomID: roomID,
            userID: pulse.userID
        )))
    }

    private func handleCharacterThrow(
        roomID: UUID,
        generation: Int,
        payload: JSONObject
    ) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        let inner = payload["payload"]?.objectValue ?? payload
        guard let value = try? inner.decode(as: CharacterThrowPayload.self),
              value.schemaVersion == 1,
              value.roomID == roomID,
              value.actorUserID != value.targetUserID,
              value.actorUserID != client.auth.currentUser?.id
        else { return }
        emit(.characterThrow(CharacterThrowEvent(
            id: value.eventID,
            roomID: roomID,
            actorUserID: value.actorUserID,
            targetUserID: value.targetUserID,
            sourceCharacterID: value.sourceCharacterID,
            throwableID: PixelCharacterThrowCatalog.supports(objectID: value.throwableID)
                ? value.throwableID
                : nil
        )))
    }

    private func expireTyping(roomID: UUID, userID: UUID, key: String, generation: Int) {
        guard isCurrentChannel(roomID: roomID, generation: generation) else { return }
        typingExpiryTasks.removeValue(forKey: key)
        emit(.typing(roomID: roomID, userID: userID, active: false))
    }

    private func emit(_ event: BackendEvent) {
        var terminatedSubscribers: [UUID] = []
        var subscriberDroppedEvent = false
        for (id, continuation) in eventSubscribers {
            switch continuation.yield(event) {
            case .enqueued:
                break
            case .dropped:
                subscriberDroppedEvent = true
            case .terminated:
                terminatedSubscribers.append(id)
            @unknown default:
                subscriberDroppedEvent = true
            }
        }
        for id in terminatedSubscribers {
            eventSubscribers.removeValue(forKey: id)
        }
        if subscriberDroppedEvent {
            scheduleStructuralSnapshot()
        }
        switch eventContinuation.yield(event) {
        case .enqueued:
            break
        case .dropped:
            scheduleStructuralSnapshot()
        case .terminated:
            // A transport switch intentionally retires the original consumer
            // before attaching a fresh subscriber. Backend lifetime is owned
            // by `shutdown()`; terminating this compatibility stream must not
            // stop RPC, Presence, or subscriber fanout.
            break
        @unknown default:
            scheduleStructuralSnapshot()
        }
    }

    private func removeEventSubscriber(_ id: UUID) {
        eventSubscribers.removeValue(forKey: id)
    }

    private func inviteAccount(roomID: UUID) -> String {
        inviteAccountPrefix + roomID.uuidString.lowercased()
    }
}

private struct RealtimeCapabilityRegistrationParameters: Encodable, Sendable {
    let platform: String
    let appVersion: String
    let protocolVersion: Int
    let contractHash: String

    private enum CodingKeys: String, CodingKey {
        case platform = "p_platform"
        case appVersion = "p_app_version"
        case protocolVersion = "p_protocol_version"
        case contractHash = "p_contract_hash"
    }
}

private enum RealtimeRecoveryTrigger: String, Sendable {
    case channel
    case networkPath = "network-path"
    case reconciliation
    case retry
    case watchdog
}

private struct RoomRealtimeChannels: Sendable {
    let epoch: Int
    let generation: Int
    let database: RealtimeChannelV2
    let ephemeral: RealtimeChannelV2
    let tasks: [Task<Void, Never>]
}
