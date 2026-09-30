import CoreGraphics
import Foundation

enum LaunchReason: String, Codable, Sendable {
    case firstRun
    case manual
    case loginItem
}

enum LaunchRouter {
    static let loginItemArgument = "--sidey-login-item"

    static func reason(hasShownNativeLanding: Bool, arguments: [String]) -> LaunchReason {
        guard hasShownNativeLanding else { return .firstRun }
        return arguments.contains(loginItemArgument) ? .loginItem : .manual
    }
}

enum DockVisibilityPolicy {
    static func shouldShowDockIcon(
        firstRunPresentationActive: Bool = false,
        settingsWindowPresented: Bool = false
    ) -> Bool {
        firstRunPresentationActive || settingsWindowPresented
    }
}

enum ManualReopenPolicy {
    static func shouldOpenSettings(
        hasShownNativeLanding: Bool,
        composerVisible: Bool,
        originatesFromOverlayInteraction: Bool = false
    ) -> Bool {
        hasShownNativeLanding && !composerVisible && !originatesFromOverlayInteraction
    }
}

enum BackendBootstrapState: Equatable, Sendable {
    case pending
    case ready
    case failed
}

enum GroupOperation: Equatable, Sendable {
    case idle
    case creating
    case joining
    case switching(UUID)

    var blocksMutations: Bool { self != .idle }

    var allowsRoomSelection: Bool {
        switch self {
        case .idle, .switching: true
        case .creating, .joining: false
        }
    }

    func isSwitching(to roomID: UUID) -> Bool {
        self == .switching(roomID)
    }

    var createButtonTitle: String {
        self == .creating
            ? L10n.text("group.create.in_progress")
            : L10n.text("groups.create.action")
    }

    var joinButtonTitle: String {
        self == .joining
            ? L10n.text("group.join.in_progress")
            : L10n.text("groups.action.join_by_code")
    }
}

enum FirstRunDestination: Equatable, Sendable {
    case waiting
    case onboarding
    case overlay
    case recovery
}

enum FirstRunTransition {
    static func destination(
        landingCompleted: Bool,
        onboardingComplete: Bool,
        backendState: BackendBootstrapState
    ) -> FirstRunDestination {
        guard landingCompleted else { return .waiting }
        guard onboardingComplete else { return .onboarding }
        return switch backendState {
        case .pending: .waiting
        case .ready: .overlay
        case .failed: .recovery
        }
    }
}

enum OverlayRevealPolicy {
    static func isVisible(
        requested: Bool,
        onboardingComplete: Bool,
        backendState: BackendBootstrapState
    ) -> Bool {
        requested && onboardingComplete && backendState == .ready
    }
}

enum SettingsPage: String, CaseIterable, Identifiable, Sendable {
    case profile
    case groups
    case draws
    case store
    case app

    var id: String { rawValue }

    var systemImage: String {
        switch self {
        case .profile: "person.crop.circle"
        case .groups: "person.2"
        case .draws: "dice"
        case .store: "sparkles"
        case .app: "gearshape"
        }
    }
}

struct Profile: Codable, Equatable, Sendable {
    let id: UUID
    var nickname: String
    var characterID: String
    var equippedBubbleStyleID: String?
    var equippedThrowableID: String?
    var treeMovementPaused: Bool
    var treeMovementRevision: Int64?

    init(
        id: UUID,
        nickname: String,
        characterID: String,
        equippedBubbleStyleID: String? = nil,
        equippedThrowableID: String? = nil,
        treeMovementPaused: Bool = false,
        treeMovementRevision: Int64? = nil
    ) {
        self.id = id
        self.nickname = nickname
        self.characterID = characterID
        self.equippedBubbleStyleID = equippedBubbleStyleID
        self.equippedThrowableID = equippedThrowableID
        self.treeMovementPaused = treeMovementPaused
        self.treeMovementRevision = treeMovementRevision
    }
}

struct Room: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    var name: String
    var ownerID: UUID
    var members: [RoomMember]
    var inviteCodeHint: String
    var inviteVersion: Int = 0
    var inviteCodeReady: Bool = true
    var realtimeEpoch: Int = 1
}

struct RoomMember: Codable, Equatable, Identifiable, Sendable {
    var id: UUID { userID }
    let userID: UUID
    var nickname: String
    var characterID: String
    var presence: PresenceState
    var equippedBubbleStyleID: String? = nil
    var treeMovementPaused: Bool = false
    var treeMovementRevision: Int64? = nil
}

struct ChatMessage: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let senderID: UUID
    let body: String
    let createdAt: Date
    let bubbleStyleID: String?

    init(
        id: UUID,
        roomID: UUID,
        senderID: UUID,
        body: String,
        createdAt: Date,
        bubbleStyleID: String? = nil
    ) {
        self.id = id
        self.roomID = roomID
        self.senderID = senderID
        self.body = body
        self.createdAt = createdAt
        self.bubbleStyleID = bubbleStyleID
    }
}

enum MessageDeliveryState: Equatable, Sendable {
    case pending
    case confirmed
    case failed
}

struct MessageLedgerEntry: Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let senderID: UUID
    var body: String
    var createdAt: Date
    var state: MessageDeliveryState
    var bubbleStyleID: String? = nil
}

struct MessageLedger: Equatable, Sendable {
    static let maximumConfirmedPerRoom = 50
    static let retentionInterval = TimeInterval(ProductLimits.messageRetentionDays * 24 * 60 * 60)

    private(set) var entries: [MessageLedgerEntry] = []

    @discardableResult
    mutating func confirm(_ message: ChatMessage, now: Date = .now) -> Bool {
        let wasKnown = entries.contains(where: { $0.id == message.id })
        if let index = entries.firstIndex(where: { $0.id == message.id }) {
            entries[index].body = message.body
            entries[index].createdAt = message.createdAt
            entries[index].bubbleStyleID = message.bubbleStyleID
        } else {
            entries.append(MessageLedgerEntry(
                id: message.id,
                roomID: message.roomID,
                senderID: message.senderID,
                body: message.body,
                createdAt: message.createdAt,
                state: .confirmed,
                bubbleStyleID: message.bubbleStyleID
            ))
        }
        sortAndPrune(now: now)
        return !wasKnown
    }

    mutating func replaceConfirmed(roomID: UUID, with messages: [ChatMessage], now: Date = .now) {
        entries.removeAll { $0.roomID == roomID }
        for message in messages where message.roomID == roomID {
            confirm(message, now: now)
        }
        sortAndPrune(now: now)
    }

    mutating func remove(id: UUID, roomID: UUID) {
        entries.removeAll { $0.id == id && $0.roomID == roomID }
    }

    mutating func retain(roomIDs: Set<UUID>) {
        entries.removeAll { !roomIDs.contains($0.roomID) }
    }

    mutating func prune(now: Date = .now) {
        sortAndPrune(now: now)
    }

    var latest: MessageLedgerEntry? { entries.last }

    func latest(in roomID: UUID) -> MessageLedgerEntry? {
        entries.last(where: { $0.roomID == roomID })
    }

    private mutating func sortAndPrune(now: Date) {
        let cutoff = now.addingTimeInterval(-Self.retentionInterval)
        entries.removeAll { $0.createdAt < cutoff }
        entries.sort { lhs, rhs in
            lhs.createdAt == rhs.createdAt ? lhs.id.uuidString < rhs.id.uuidString : lhs.createdAt < rhs.createdAt
        }

        let roomIDs = Set(entries.map(\.roomID))
        for roomID in roomIDs {
            let roomIndices = entries.indices.filter { entries[$0].roomID == roomID }
            let overflow = roomIndices.count - Self.maximumConfirmedPerRoom
            guard overflow > 0 else { continue }
            let removedIDs = Set(roomIndices.prefix(overflow).map { entries[$0].id })
            entries.removeAll { removedIDs.contains($0.id) }
        }
    }
}

enum OutgoingMessageState: Equatable, Sendable {
    case pending
    case failed
}

struct OutgoingMessage: Equatable, Identifiable, Sendable {
    let id: UUID
    let roomID: UUID
    let senderID: UUID
    let body: String
    let createdAt: Date
    var state: OutgoingMessageState
}

struct MessageOutbox: Equatable, Sendable {
    static let maximumFailedPerRoom = 50

    private(set) var entries: [OutgoingMessage] = []

    mutating func stage(
        id: UUID,
        roomID: UUID,
        senderID: UUID,
        body: String,
        createdAt: Date = .now
    ) {
        guard !entries.contains(where: { $0.id == id }) else { return }
        entries.append(OutgoingMessage(
            id: id,
            roomID: roomID,
            senderID: senderID,
            body: body,
            createdAt: createdAt,
            state: .pending
        ))
    }

    @discardableResult
    mutating func confirm(id: UUID, roomID: UUID) -> Bool {
        let previousCount = entries.count
        entries.removeAll { $0.id == id && $0.roomID == roomID }
        return entries.count != previousCount
    }

    mutating func retain(roomIDs: Set<UUID>) {
        entries.removeAll { !roomIDs.contains($0.roomID) }
    }

    @discardableResult
    mutating func fail(id: UUID, roomID: UUID) -> OutgoingMessage? {
        guard let index = entries.firstIndex(where: {
            $0.id == id && $0.roomID == roomID && $0.state == .pending
        }) else { return nil }
        entries[index].state = .failed
        pruneFailed(roomID: roomID)
        return entries.first(where: { $0.id == id && $0.roomID == roomID })
    }

    private mutating func pruneFailed(roomID: UUID) {
        let failed = entries
            .filter { $0.roomID == roomID && $0.state == .failed }
            .sorted { $0.createdAt < $1.createdAt }
        let overflow = failed.count - Self.maximumFailedPerRoom
        guard overflow > 0 else { return }
        let removedIDs = Set(failed.prefix(overflow).map(\.id))
        entries.removeAll { removedIDs.contains($0.id) }
    }
}

struct RealtimeRoomPlan: Equatable, Sendable {
    let desired: Set<UUID>
    let additions: Set<UUID>
    let removals: Set<UUID>
    let activeRoomID: UUID?

    static func make(existing: Set<UUID>, requested: [UUID], activeRoomID: UUID?) -> Self {
        let desired = Set(requested.prefix(5))
        return Self(
            desired: desired,
            additions: desired.subtracting(existing),
            removals: existing.subtracting(desired),
            activeRoomID: activeRoomID.flatMap { desired.contains($0) ? $0 : nil }
        )
    }
}

struct RealtimeConnectionTracker: Equatable, Sendable {
    private(set) var desiredRoomIDs: Set<UUID> = []
    private(set) var subscribedRoomIDs: Set<UUID> = []

    mutating func replaceDesiredRoomIDs(_ roomIDs: Set<UUID>) {
        desiredRoomIDs = roomIDs
        subscribedRoomIDs.formIntersection(roomIDs)
    }

    mutating func setSubscribed(_ subscribed: Bool, roomID: UUID) {
        guard desiredRoomIDs.contains(roomID) else {
            subscribedRoomIDs.remove(roomID)
            return
        }
        if subscribed {
            subscribedRoomIDs.insert(roomID)
        } else {
            subscribedRoomIDs.remove(roomID)
        }
    }

    func isSubscribed(roomID: UUID) -> Bool {
        subscribedRoomIDs.contains(roomID)
    }

    var isConnected: Bool {
        subscribedRoomIDs == desiredRoomIDs
    }
}

struct RealtimeTopology: Equatable, Sendable {
    let roomEpochs: [UUID: Int]

    init(rooms: some Sequence<Room>) {
        roomEpochs = Dictionary(uniqueKeysWithValues: rooms.prefix(5).map {
            ($0.id, $0.realtimeEpoch)
        })
    }

    init(channelEpochs: [UUID: Int]) {
        roomEpochs = channelEpochs
    }
}

struct RealtimeTopologyUpdatePlan: Equatable, Sendable {
    let additions: Set<UUID>
    let removals: Set<UUID>

    static func make(live: RealtimeTopology, requestedRooms: [Room]) -> Self {
        let desired = RealtimeTopology(rooms: requestedRooms)
        let additions = Set(desired.roomEpochs.compactMap { roomID, desiredEpoch in
            live.roomEpochs[roomID] == desiredEpoch ? nil : roomID
        })
        let removals = Set(live.roomEpochs.compactMap { roomID, liveEpoch in
            desired.roomEpochs[roomID] == liveEpoch ? nil : roomID
        })
        return Self(additions: additions, removals: removals)
    }
}

struct RealtimeDesiredTopology: Equatable, Sendable {
    private(set) var roomEpochs: [UUID: Int] = [:]

    mutating func replace(rooms: some Sequence<Room>) {
        roomEpochs = Dictionary(uniqueKeysWithValues: rooms.prefix(5).map {
            ($0.id, $0.realtimeEpoch)
        })
    }

    var roomIDs: Set<UUID> {
        Set(roomEpochs.keys)
    }

    func epoch(for roomID: UUID) -> Int? {
        roomEpochs[roomID]
    }
}

enum RealtimeChannelPairPolicy {
    static func isSubscribed(database: Bool, ephemeral: Bool) -> Bool {
        database && ephemeral
    }
}

enum RealtimeChannelGenerationPolicy {
    static func accepts(
        candidateGeneration: Int,
        currentGeneration: Int,
        desiredEpoch: Int?,
        channelEpoch: Int?
    ) -> Bool {
        candidateGeneration == currentGeneration
            && desiredEpoch != nil
            && desiredEpoch == channelEpoch
    }
}

enum RealtimeRecoveryPolicy {
    static let watchdogInterval: TimeInterval = 5
    static let pathRecoveryDebounce: TimeInterval = 0.35
    static let maximumDelay: TimeInterval = 30

    static func delay(forAttempt attempt: Int) -> TimeInterval {
        let exponent = Double(max(0, min(attempt - 1, 5)))
        return min(8 * pow(2, exponent), maximumDelay)
    }
}

enum PresencePublicationPlan {
    static func state(
        for roomID: UUID,
        activeRoomID: UUID?,
        localPresence: PresenceState
    ) -> PresenceState {
        guard roomID == activeRoomID else { return .offline }
        return localPresence == .away ? .away : .online
    }
}

struct PresenceUpdate: Equatable, Sendable {
    let userID: UUID
    let state: PresenceState
}

enum PresenceChangePlan {
    /// Supabase Presence can report a state replacement as leave(old) and
    /// join(new) for the same key in one delta. The join must win without an
    /// intermediate/final offline overwrite.
    static func updates(
        joined: [UUID: PresenceState],
        left: Set<UUID>
    ) -> [PresenceUpdate] {
        let offline = left.subtracting(joined.keys).map {
            PresenceUpdate(userID: $0, state: .offline)
        }
        let current = joined.map {
            PresenceUpdate(userID: $0.key, state: $0.value)
        }
        return (offline + current).sorted { lhs, rhs in
            lhs.userID.uuidString < rhs.userID.uuidString
        }
    }
}

enum ProfileValidator {
    static let minimumNicknameCharacters = 2
    static let maximumNicknameCharacters = 8

    static func normalizedNickname(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isValidNickname(_ value: String) -> Bool {
        let normalized = normalizedNickname(value)
        return normalized.count >= minimumNicknameCharacters
            && normalized.count <= maximumNicknameCharacters
            && value.rangeOfCharacter(from: .newlines) == nil
            && !value.contains("\t")
    }

    static func limitedNicknameDraft(_ value: String) -> String {
        let singleLine = value.filter { !$0.isNewline && $0 != "\t" }
        return String(singleLine.prefix(maximumNicknameCharacters))
    }

    static func displayNickname(_ value: String) -> String {
        String(normalizedNickname(value).prefix(maximumNicknameCharacters))
    }
}

enum RoomNameValidator {
    static let minimumCharacters = 1
    static let maximumCharacters = 20

    static func normalized(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isValid(_ value: String) -> Bool {
        let normalized = normalized(value)
        return normalized.count >= minimumCharacters
            && normalized.count <= maximumCharacters
            && value.rangeOfCharacter(from: .newlines) == nil
            && !value.contains("\t")
    }

    static func limitedDraft(_ value: String) -> String {
        let singleLine = value.filter { !$0.isNewline && $0 != "\t" }
        let normalized = normalized(singleLine)
        guard normalized.count > maximumCharacters else { return singleLine }
        return String(normalized.prefix(maximumCharacters))
    }
}

enum RoomManagementPolicy {
    static func isOwner(_ member: RoomMember, in room: Room) -> Bool {
        room.ownerID == member.userID
    }

    static func canManage(_ room: Room, currentUserID: UUID?) -> Bool {
        room.ownerID == currentUserID
    }

    static func canRemove(
        _ member: RoomMember,
        from room: Room,
        currentUserID: UUID?
    ) -> Bool {
        canManage(room, currentUserID: currentUserID)
            && member.userID != currentUserID
    }
}

enum RoomLeaveConfirmation: Equatable, Sendable {
    case member
    case ownerWithRemainingMembers
    case lastOwner

    static func resolve(room: Room, currentUserID: UUID?) -> Self {
        guard room.ownerID == currentUserID else { return .member }
        return room.members.contains(where: { $0.userID != currentUserID })
            ? .ownerWithRemainingMembers
            : .lastOwner
    }

    var message: String {
        switch self {
        case .member:
            L10n.text("group.leave.confirmation.member")
        case .ownerWithRemainingMembers:
            L10n.text("group.leave.confirmation.owner_transfer")
        case .lastOwner:
            L10n.text("group.leave.confirmation.last_member")
        }
    }
}

struct SuccessFeedbackState: Equatable, Sendable {
    static let displayDuration: Duration = .seconds(3)

    private(set) var message: String?
    private(set) var generation = 0

    @discardableResult
    mutating func present(_ message: String) -> Int {
        generation += 1
        self.message = message
        return generation
    }

    mutating func dismiss(generation expectedGeneration: Int? = nil) {
        guard expectedGeneration == nil || expectedGeneration == generation else { return }
        generation += 1
        message = nil
    }
}

enum StoreSortOrder: String, CaseIterable, Identifiable, Sendable {
    case catalog
    case priceAscending
    case priceDescending

    var id: String { rawValue }

    var title: String {
        switch self {
        case .catalog: L10n.text("store.sort.catalog")
        case .priceAscending: L10n.text("store.sort.price_ascending")
        case .priceDescending: L10n.text("store.sort.price_descending")
        }
    }
}

enum StoreProductFilter {
    static func apply(
        _ states: [CommerceProductState],
        kind: CommerceProductKind,
        sortOrder: StoreSortOrder,
        hidesOwned: Bool,
        activeEntitlementKeys: Set<String>
    ) -> [CommerceProductState] {
        states
            .filter { state in
                guard state.product.kind == kind else { return false }
                let isOwned = activeEntitlementKeys.contains(state.product.entitlementKey)
                    || state.isEquipped
                return !hidesOwned || !isOwned
            }
            .sorted { lhs, rhs in
                let result: ComparisonResult
                switch sortOrder {
                case .catalog:
                    result = compare(lhs.product.sortOrder, rhs.product.sortOrder)
                case .priceAscending:
                    result = compare(lhs.product.amountKRW, rhs.product.amountKRW)
                case .priceDescending:
                    result = compare(rhs.product.amountKRW, lhs.product.amountKRW)
                }
                if result != .orderedSame { return result == .orderedAscending }
                if lhs.product.sortOrder != rhs.product.sortOrder {
                    return lhs.product.sortOrder < rhs.product.sortOrder
                }
                return lhs.product.id < rhs.product.id
            }
    }

    private static func compare(_ lhs: Int, _ rhs: Int) -> ComparisonResult {
        if lhs < rhs { return .orderedAscending }
        if lhs > rhs { return .orderedDescending }
        return .orderedSame
    }
}

enum CosmeticEquipmentFeedback {
    static func successMessage(
        kind: CommerceProductKind,
        product: CommerceProduct?
    ) -> String {
        if let product {
            return L10n.format("commerce.equipment.success.named", product.displayName)
        }
        switch kind {
        case .bubble:
            return L10n.text("commerce.equipment.success.default_bubble")
        case .throwable:
            return L10n.text("commerce.equipment.success.default_throwable")
        case .character:
            return L10n.text("commerce.equipment.success.default_character")
        }
    }
}

enum MessageValidator {
    static let maximumCharacters = 200
    static let maximumLines = 3

    static func normalized(_ value: String) -> String {
        value.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isValid(_ value: String) -> Bool {
        !value.isEmpty
            && value.count <= maximumCharacters
            && value.split(separator: "\n", omittingEmptySubsequences: false).count <= maximumLines
    }

    static func isValidDraft(_ value: String) -> Bool {
        value.count <= maximumCharacters
            && value.replacingOccurrences(of: "\r\n", with: "\n")
                .replacingOccurrences(of: "\r", with: "\n")
                .split(separator: "\n", omittingEmptySubsequences: false).count <= maximumLines
    }
}
